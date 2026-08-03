using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Bluecadet.Launchpad.Tests
{
	/// <summary>
	/// An IAssetCache that keeps a full ledger instead of loading anything:
	/// how many times each path was retained, released, and how many
	/// references are outstanding right now. Tests assert on the ledger
	/// rather than on internal manager state, so a refcount leak shows up as
	/// a failing assertion no matter which code path leaked it.
	///
	/// Every operation is instantaneous by default, so a prepare finishes as
	/// soon as its off-thread diff hands back. Adding a path to PausedPaths
	/// makes its retain park until ResumePaused() is called, which is how a
	/// test interleaves a promotion with a prepare that is mid-acquire.
	/// </summary>
	internal sealed class FakeAssetCache : IAssetCache
	{
		private readonly object _ledgerLock = new object();
		private readonly Dictionary<string, int> _retains = new Dictionary<string, int>();
		private readonly Dictionary<string, int> _releases = new Dictionary<string, int>();
		private readonly Dictionary<string, int> _live = new Dictionary<string, int>();

		private TaskCompletionSource<bool> _pause;

		/// <summary>Retaining these reports failure (returns false) without retaining.</summary>
		public readonly HashSet<string> UnloadablePaths = new HashSet<string>();

		/// <summary>Retaining these throws, as a cache backed by real IO would.</summary>
		public readonly HashSet<string> ThrowingPaths = new HashSet<string>();

		/// <summary>Retaining these blocks until ResumePaused().</summary>
		public readonly HashSet<string> PausedPaths = new HashSet<string>();

		public int EvictCalls { get; private set; }

		/// <summary>Releases that arrived with no reference outstanding — always a bug.</summary>
		public int UnbalancedReleases { get; private set; }

		public int RetainCount(string path)
		{
			return Read(_retains, path);
		}

		public int ReleaseCount(string path)
		{
			return Read(_releases, path);
		}

		/// <summary>References currently outstanding for one path.</summary>
		public int LiveCount(string path)
		{
			return Read(_live, path);
		}

		/// <summary>References currently outstanding across every path; zero means nothing leaked.</summary>
		public int TotalLive
		{
			get
			{
				lock (_ledgerLock)
				{
					int total = 0;
					foreach (var count in _live.Values)
					{
						total += count;
					}

					return total;
				}
			}
		}

		public void ResumePaused()
		{
			TaskCompletionSource<bool> pause;
			lock (_ledgerLock)
			{
				pause = _pause;
				_pause = null;
				PausedPaths.Clear();
			}

			pause?.TrySetResult(true);
		}

		public async Task<bool> RetainAsync(string absolutePath, CancellationToken ct)
		{
			Task pause = null;
			lock (_ledgerLock)
			{
				if (PausedPaths.Contains(absolutePath))
				{
					pause = (_pause ??= new TaskCompletionSource<bool>()).Task;
				}
			}

			if (pause != null)
			{
				await pause.ConfigureAwait(false);
			}

			ct.ThrowIfCancellationRequested();

			if (ThrowingPaths.Contains(absolutePath))
			{
				throw new IOException($"FakeAssetCache: '{absolutePath}' is configured to fail.");
			}

			if (UnloadablePaths.Contains(absolutePath))
			{
				return false;
			}

			lock (_ledgerLock)
			{
				Bump(_retains, absolutePath);
				Bump(_live, absolutePath);
			}

			return true;
		}

		public void Release(string absolutePath)
		{
			lock (_ledgerLock)
			{
				if (Read(_live, absolutePath) <= 0)
				{
					UnbalancedReleases++;
					return;
				}

				Bump(_releases, absolutePath);
				_live[absolutePath] = _live[absolutePath] - 1;
			}
		}

		public void EvictUnreferenced()
		{
			lock (_ledgerLock)
			{
				EvictCalls++;
			}
		}

		private static void Bump(Dictionary<string, int> counts, string path)
		{
			counts.TryGetValue(path, out int count);
			counts[path] = count + 1;
		}

		private int Read(Dictionary<string, int> counts, string path)
		{
			lock (_ledgerLock)
			{
				counts.TryGetValue(path, out int count);
				return count;
			}
		}
	}
}
