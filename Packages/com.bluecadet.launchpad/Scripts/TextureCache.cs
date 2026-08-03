using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Bluecadet.Launchpad
{
	/// <summary>
	/// Caches decoded Texture2Ds by absolute file path with manual
	/// refcounting. Implements IAssetCache so ContentManager can drive it
	/// through the format-agnostic load seam. MAIN THREAD ONLY:
	/// UnityWebRequest and Texture2D/Object APIs require it — every member
	/// of this class must be called from the Unity main thread.
	///
	/// After Dispose every member is inert (null / false / no-op) rather
	/// than throwing: loads and view code routinely outlive the composition
	/// root being torn down, and a teardown-ordering mistake should degrade
	/// to a missing texture, not an exception storm.
	/// </summary>
	public sealed class TextureCache : IAssetCache, IDisposable
	{
		private sealed class Entry
		{
			public Texture2D Texture;
			public int RefCount;
		}

		private readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>();
		private readonly Dictionary<string, Task<Texture2D>> _inFlight = new Dictionary<string, Task<Texture2D>>();

		// Owns the lifetime of the shared load behind a coalesced GetAsync,
		// deliberately independent of any individual caller's token: if the
		// caller that happened to start the load cancels, the load must keep
		// running for every other caller still awaiting the same path.
		// Cancelled (and only then) on Dispose.
		private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
		private bool _disposed;

		public int Count => _cache.Count;

		/// <summary>
		/// IAssetCache seam: loads the texture if needed and claims one
		/// reference to it, returning false (having retained nothing) if it
		/// couldn't be loaded. The increment happens in the same
		/// main-thread turn the load completes in, with no await in
		/// between, so an EvictUnreferenced from another continuation can
		/// never free the entry between "loaded" and "claimed".
		/// </summary>
		public async Task<bool> RetainAsync(string absolutePath, CancellationToken ct)
		{
			if (_disposed || string.IsNullOrEmpty(absolutePath))
			{
				return false;
			}

			Texture2D texture = await GetAsync(absolutePath, ct).ConfigureAwait(true);
			if (texture == null || _disposed)
			{
				return false;
			}

			if (!_cache.TryGetValue(absolutePath, out var entry))
			{
				// Known limitation: an eviction that interleaved this load's
				// own completion already freed the entry. Reporting the
				// failure is honest — nothing was retained — and callers
				// treat it like any other unloadable asset.
				Debug.LogWarning($"[TextureCache] '{absolutePath}' was evicted while loading; not retained.");
				return false;
			}

			entry.RefCount++;
			return true;
		}

		/// <summary>
		/// Returns the cached texture for absolutePath, loading and decoding
		/// it if necessary. Coalesces concurrent requests for the same path
		/// onto one shared load, which runs under the cache's own lifetime
		/// token rather than any single caller's — so one caller cancelling
		/// (e.g. its view was destroyed) never aborts the load for the other
		/// callers coalesced onto it. Each caller still honors its own ct:
		/// if it fires first, this call returns null early while the shared
		/// load keeps running for whoever else is still waiting. Never
		/// throws — returns null (with a logged warning) if the file is
		/// missing or fails to decode. Public (beyond the IAssetCache seam)
		/// so views can fetch the actual Texture2D to display.
		/// </summary>
		public async Task<Texture2D> GetAsync(string absolutePath, CancellationToken ct)
		{
			if (_disposed || string.IsNullOrEmpty(absolutePath))
			{
				return null;
			}

			if (_cache.TryGetValue(absolutePath, out var entry))
			{
				return entry.Texture;
			}

			if (_inFlight.TryGetValue(absolutePath, out var inFlightTask))
			{
				return await WaitForCallerAsync(inFlightTask, ct).ConfigureAwait(true);
			}

			Task<Texture2D> task = LoadTextureAsync(absolutePath, _lifetimeCts.Token);
			_inFlight[absolutePath] = task;

			// Clearing the in-flight entry is tied to the load finishing,
			// not to this caller's lifetime — this caller may bail out of
			// WaitForCallerAsync below before `task` completes.
			_ = RemoveInFlightWhenDone(absolutePath, task);

			return await WaitForCallerAsync(task, ct).ConfigureAwait(true);
		}

		private async Task RemoveInFlightWhenDone(string absolutePath, Task<Texture2D> task)
		{
			try
			{
				await task.ConfigureAwait(true);
			}
			catch
			{
				// LoadTextureAsync never throws (it catches internally), but
				// guard anyway so this fire-and-forget cleanup can't surface
				// an unobserved-task exception.
			}
			finally
			{
				_inFlight.Remove(absolutePath);
			}
		}

		/// <summary>
		/// Awaits a shared load on behalf of one caller, honoring that
		/// caller's own ct independent of the load itself. netstandard2.1
		/// has no Task.WaitAsync, hence the manual WhenAny/TCS composition.
		/// Returns null if ct fires first, matching GetAsync's existing
		/// null-on-cancel contract (LoadTextureAsync itself never throws).
		/// </summary>
		private static async Task<Texture2D> WaitForCallerAsync(Task<Texture2D> sharedTask, CancellationToken ct)
		{
			if (!ct.CanBeCanceled || sharedTask.IsCompleted)
			{
				return await sharedTask.ConfigureAwait(true);
			}

			var cancelTcs = new TaskCompletionSource<bool>();
			using (ct.Register(() => cancelTcs.TrySetResult(true)))
			{
				Task completed = await Task.WhenAny(sharedTask, cancelTcs.Task).ConfigureAwait(true);
				if (completed == cancelTcs.Task)
				{
					// This caller's own token fired; the shared load is left
					// running for any other coalesced waiters.
					return null;
				}
			}

			return await sharedTask.ConfigureAwait(true);
		}

		private async Task<Texture2D> LoadTextureAsync(string absolutePath, CancellationToken ct)
		{
			try
			{
				if (!File.Exists(absolutePath))
				{
					Debug.LogWarning($"[TextureCache] File not found: '{absolutePath}'.");
					return null;
				}

				string uri = new Uri(absolutePath).AbsoluteUri;

				using (var request = UnityWebRequestTexture.GetTexture(uri, true))
				{
					var op = request.SendWebRequest();

					while (!op.isDone)
					{
						if (ct.IsCancellationRequested)
						{
							request.Abort();
							return null;
						}

						await Awaitable.NextFrameAsync(ct);
					}

					if (request.result != UnityWebRequest.Result.Success)
					{
						Debug.LogWarning($"[TextureCache] Failed to load '{absolutePath}': {request.error}");
						return null;
					}

					Texture2D texture = DownloadHandlerTexture.GetContent(request);
					if (texture == null)
					{
						Debug.LogWarning($"[TextureCache] Decoded texture is null for '{absolutePath}'.");
						return null;
					}

					if (_disposed)
					{
						// Dispose already destroyed and cleared the cache, so
						// putting this late arrival in would strand it for the
						// rest of the process.
						UnityEngine.Object.Destroy(texture);
						return null;
					}

					_cache[absolutePath] = new Entry { Texture = texture, RefCount = 0 };
					return texture;
				}
			}
			catch (OperationCanceledException)
			{
				return null;
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[TextureCache] Exception loading '{absolutePath}': {ex.Message}");
				return null;
			}
		}

		public void Release(string absolutePath)
		{
			if (_disposed || string.IsNullOrEmpty(absolutePath))
			{
				return;
			}

			if (_cache.TryGetValue(absolutePath, out var entry))
			{
				if (entry.RefCount <= 0)
				{
					// Surface a double-release instead of masking it as a
					// negative RefCount, which would require an extra
					// retain to ever reach zero again.
					Debug.LogError($"[TextureCache] Release called with RefCount already {entry.RefCount} for '{absolutePath}' (double-release?).");
					return;
				}

				entry.RefCount--;
			}
		}

		public void EvictUnreferenced()
		{
			if (_disposed)
			{
				return;
			}

			List<string> toRemove = null;
			foreach (var kvp in _cache)
			{
				if (kvp.Value.RefCount <= 0)
				{
					(toRemove ??= new List<string>()).Add(kvp.Key);
				}
			}

			if (toRemove == null)
			{
				return;
			}

			foreach (var key in toRemove)
			{
				Entry entry = _cache[key];
				if (entry.Texture != null)
				{
					UnityEngine.Object.Destroy(entry.Texture);
				}

				_cache.Remove(key);
			}
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			// Set first: every other member early-outs on it, and
			// LoadTextureAsync re-checks it after its await so a load that
			// finishes during teardown destroys its texture instead of
			// repopulating a cache nobody will ever clear again.
			_disposed = true;

			// Unblocks any still-running shared loads (they poll this token
			// each frame in LoadTextureAsync's send-request loop). Cancelled
			// but deliberately NOT disposed: those loads still hold
			// registrations against this token, and disposing underneath
			// them turns a clean teardown into ObjectDisposedException.
			try
			{
				_lifetimeCts.Cancel();
			}
			catch
			{
				// Best-effort teardown; texture destruction below must still run.
			}

			// Dropped rather than awaited: the loads are already cancelled,
			// and holding the tasks would keep their textures reachable long
			// after this cache stopped being able to own them.
			_inFlight.Clear();

			foreach (var entry in _cache.Values)
			{
				if (entry.Texture != null)
				{
					UnityEngine.Object.Destroy(entry.Texture);
				}
			}

			_cache.Clear();
		}
	}
}
