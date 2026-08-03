using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Bluecadet.Launchpad
{
	/// <summary>
	/// Owns a set of asset-cache references as one unit: acquiring the lease
	/// retains every path it can load, and disposing it releases exactly
	/// those paths and evicts whatever that made unreferenced. Refcount
	/// bookkeeping therefore exists in one place instead of being spread
	/// across every code path that might abandon a half-prepared version.
	///
	/// A lease is single-owner: hand the reference on (to a staged/current
	/// snapshot) or dispose it, never both. Dispose is idempotent.
	/// </summary>
	public sealed class AssetLease : IDisposable
	{
		/// <summary>
		/// How many loads may be in flight at once. Caches that pace
		/// themselves per frame (TextureCache decodes via UnityWebRequest and
		/// yields each frame) turn a sequential acquire into one frame per
		/// asset, so a version referencing 60 images would take a full second
		/// to prepare. Bounded rather than unbounded so a large version can't
		/// stall the main thread or exhaust file handles.
		/// </summary>
		private const int MaxConcurrentLoads = 8;

		private readonly IAssetCache _cache;
		private readonly HashSet<string> _retained = new HashSet<string>();
		private bool _disposed;

		private AssetLease(IAssetCache cache)
		{
			_cache = cache;
		}

		/// <summary>The paths this lease currently holds a reference to.</summary>
		public IReadOnlyCollection<string> RetainedPaths => _retained;

		/// <summary>
		/// Retains every distinct non-empty path, loading with bounded
		/// concurrency. Paths the cache reports as unloadable are skipped
		/// (a missing image must not sink a whole content version), but if
		/// the cache throws — or ct fires — the lease unwinds itself,
		/// releasing everything it had already retained, before the
		/// exception propagates. A failed acquire therefore never leaks a
		/// reference and never hands back a partial lease.
		/// </summary>
		public static async Task<AssetLease> AcquireAsync(IAssetCache cache, IEnumerable<string> paths, CancellationToken ct)
		{
			if (cache == null)
			{
				throw new ArgumentNullException(nameof(cache));
			}

			var lease = new AssetLease(cache);
			if (paths == null)
			{
				return lease;
			}

			var distinct = new List<string>();
			var seen = new HashSet<string>();
			foreach (var path in paths)
			{
				if (!string.IsNullOrEmpty(path) && seen.Add(path))
				{
					distinct.Add(path);
				}
			}

			if (distinct.Count == 0)
			{
				return lease;
			}

			try
			{
				using (var throttle = new SemaphoreSlim(MaxConcurrentLoads))
				{
					var loads = new List<Task>(distinct.Count);
					foreach (var path in distinct)
					{
						loads.Add(lease.RetainOneAsync(path, throttle, ct));
					}

					// WhenAll only completes once every load has finished, so
					// the semaphore is never disposed out from under a
					// still-running load, even when one of them faults.
					await Task.WhenAll(loads).ConfigureAwait(true);
				}
			}
			catch
			{
				lease.Dispose();
				throw;
			}

			return lease;
		}

		private async Task RetainOneAsync(string path, SemaphoreSlim throttle, CancellationToken ct)
		{
			await throttle.WaitAsync(ct).ConfigureAwait(true);
			try
			{
				ct.ThrowIfCancellationRequested();

				if (await _cache.RetainAsync(path, ct).ConfigureAwait(true))
				{
					// Loads may resume on any thread when there is no
					// synchronization context (edit-mode tests), so the
					// ledger of what we owe a Release for is guarded even
					// though the Unity runtime keeps this on the main thread.
					lock (_retained)
					{
						_retained.Add(path);
					}
				}
			}
			finally
			{
				throttle.Release();
			}
		}

		/// <summary>Releases every retained path and evicts what that freed. Idempotent.</summary>
		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;

			lock (_retained)
			{
				foreach (var path in _retained)
				{
					_cache.Release(path);
				}

				_retained.Clear();
			}

			_cache.EvictUnreferenced();
		}
	}
}
