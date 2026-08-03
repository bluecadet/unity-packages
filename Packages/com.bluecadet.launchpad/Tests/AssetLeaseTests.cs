using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Bluecadet.Launchpad.Tests
{
	[TestFixture]
	public class AssetLeaseTests
	{
		private static AssetLease Acquire(FakeAssetCache cache, CancellationToken ct, params string[] paths)
		{
			Task<AssetLease> task = AssetLease.AcquireAsync(cache, paths, ct);
			Assert.That(task.IsCompleted, Is.True, "FakeAssetCache retains synchronously, so acquire should not have suspended.");
			return task.GetAwaiter().GetResult();
		}

		[Test]
		public void Acquire_RetainsEveryDistinctPathOnce()
		{
			var cache = new FakeAssetCache();

			AssetLease lease = Acquire(cache, CancellationToken.None, "a", "b", "a", "", null);

			Assert.That(lease.RetainedPaths, Is.EquivalentTo(new[] { "a", "b" }));
			Assert.That(cache.RetainCount("a"), Is.EqualTo(1));
			Assert.That(cache.RetainCount("b"), Is.EqualTo(1));
			Assert.That(cache.TotalLive, Is.EqualTo(2));
		}

		[Test]
		public void Dispose_ReleasesEverythingAndEvicts()
		{
			var cache = new FakeAssetCache();
			AssetLease lease = Acquire(cache, CancellationToken.None, "a", "b");

			lease.Dispose();

			Assert.That(cache.TotalLive, Is.EqualTo(0));
			Assert.That(cache.EvictCalls, Is.EqualTo(1));
			Assert.That(lease.RetainedPaths, Is.Empty);
		}

		[Test]
		public void Dispose_IsIdempotent()
		{
			var cache = new FakeAssetCache();
			AssetLease lease = Acquire(cache, CancellationToken.None, "a");

			lease.Dispose();
			lease.Dispose();

			Assert.That(cache.ReleaseCount("a"), Is.EqualTo(1));
			Assert.That(cache.UnbalancedReleases, Is.EqualTo(0));
		}

		[Test]
		public void Acquire_UnloadablePathIsSkippedButTheRestSurvive()
		{
			var cache = new FakeAssetCache();
			cache.UnloadablePaths.Add("missing");

			AssetLease lease = Acquire(cache, CancellationToken.None, "a", "missing", "b");

			Assert.That(lease.RetainedPaths, Is.EquivalentTo(new[] { "a", "b" }));
			Assert.That(cache.LiveCount("missing"), Is.EqualTo(0));
		}

		[Test]
		public void Acquire_ThrowingPathUnwindsThePartialLease()
		{
			var cache = new FakeAssetCache();
			cache.ThrowingPaths.Add("boom");

			Assert.Catch<IOException>(() =>
			{
				Task<AssetLease> task = AssetLease.AcquireAsync(cache, new[] { "a", "boom", "b" }, CancellationToken.None);
				task.GetAwaiter().GetResult();
			});

			Assert.That(cache.TotalLive, Is.EqualTo(0), "Paths retained before the failure must be released.");
			Assert.That(cache.EvictCalls, Is.EqualTo(1));
			Assert.That(cache.UnbalancedReleases, Is.EqualTo(0));
		}

		[Test]
		public void Acquire_AlreadyCancelledTokenRetainsNothing()
		{
			var cache = new FakeAssetCache();
			using (var cts = new CancellationTokenSource())
			{
				cts.Cancel();

				Assert.Catch<OperationCanceledException>(() =>
				{
					Task<AssetLease> task = AssetLease.AcquireAsync(cache, new[] { "a", "b" }, cts.Token);
					task.GetAwaiter().GetResult();
				});
			}

			Assert.That(cache.TotalLive, Is.EqualTo(0));
		}

		[Test]
		public void Acquire_NullPathsYieldsAnEmptyLease()
		{
			var cache = new FakeAssetCache();

			AssetLease lease = Acquire(cache, CancellationToken.None, (string[])null);

			Assert.That(lease.RetainedPaths, Is.Empty);
			Assert.That(cache.TotalLive, Is.EqualTo(0));
		}
	}
}
