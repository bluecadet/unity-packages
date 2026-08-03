using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Bluecadet.Launchpad.Tests
{
	internal sealed class FakeVersionFeed : IVersionFeed
	{
		public event Action<string> OnVersionPromoted;

		public readonly List<string> Acks = new List<string>();
		public bool AckResult = true;

		public bool HasSubscribers => OnVersionPromoted != null;

		public void Promote(string versionId)
		{
			OnVersionPromoted?.Invoke(versionId);
		}

		public Task<bool> AckAsync(string versionId, CancellationToken ct)
		{
			lock (Acks)
			{
				Acks.Add(versionId);
			}

			return Task.FromResult(AckResult);
		}

		/// <summary>A snapshot, guarded defensively even though every access here is from the main thread.</summary>
		public string[] AckedVersions()
		{
			lock (Acks)
			{
				return Acks.ToArray();
			}
		}
	}

	internal sealed class FakeContentStore : IContentStore<string>
	{
		public readonly Dictionary<string, LoadedVersion<string>> Versions = new Dictionary<string, LoadedVersion<string>>();
		public LoadedVersion<string> ColdBoot;

		public Task<LoadedVersion<string>> LoadVersionAsync(string versionId, CancellationToken ct)
		{
			if (versionId == null)
			{
				return ColdBoot != null
					? Task.FromResult(ColdBoot)
					: Task.FromException<LoadedVersion<string>>(new DirectoryNotFoundException("FakeContentStore: nothing on disk."));
			}

			return Versions.TryGetValue(versionId, out var loaded)
				? Task.FromResult(loaded)
				: Task.FromException<LoadedVersion<string>>(new DirectoryNotFoundException($"FakeContentStore: unknown version '{versionId}'."));
		}
	}

	internal sealed class FakeGate : ISwapGate
	{
		public bool Allow;
		public int NotifyCount;
		public int ClearCount;

		public bool CanSwapNow => Allow;

		public void NotifyStagedPending()
		{
			NotifyCount++;
		}

		public void ClearPending()
		{
			ClearCount++;
		}
	}

	/// <summary>
	/// Drives ContentManager through fakes and asserts on FakeAssetCache's
	/// refcount ledger rather than on the manager's internals: every one of
	/// these scenarios used to be a way to strand asset references, so the
	/// interesting assertion is almost always "the ledger nets to zero for
	/// versions nobody is showing".
	///
	/// Preparing a version diffs off the main thread, so a prepare is always
	/// asynchronous even with instantaneous fakes: a test drives it by
	/// promoting and then awaiting the observable end of that prepare (its
	/// ack, or the commit/stage it produced) rather than by assuming
	/// Promote() returns with the work already done.
	/// </summary>
	[TestFixture]
	public class ContentManagerTests
	{
		private FakeVersionFeed _feed;
		private FakeContentStore _store;
		private FakeAssetCache _cache;
		private FakeGate _gate;
		private ContentManager<string> _manager;

		[SetUp]
		public void SetUp()
		{
			_feed = new FakeVersionFeed();
			_store = new FakeContentStore();
			_cache = new FakeAssetCache();
			_gate = new FakeGate { Allow = true };
		}

		[TearDown]
		public void TearDown()
		{
			_manager?.Dispose();
			_manager = null;

			Assert.That(_cache.UnbalancedReleases, Is.EqualTo(0), "A path was released more times than it was retained.");
		}

		private void StartManager()
		{
			_manager = new ContentManager<string>(
				_feed,
				_store,
				_cache,
				_gate,
				data => string.IsNullOrEmpty(data) ? Array.Empty<string>() : new[] { data });

			_manager.Start(CancellationToken.None);
		}

		private void DefineVersion(string versionId, params string[] paths)
		{
			var items = new List<ContentItem<string>>(paths.Length);
			for (int i = 0; i < paths.Length; i++)
			{
				items.Add(new ContentItem<string> { Id = paths[i], ContentHash = (ulong)(i + 1), Data = paths[i] });
			}

			_store.Versions[versionId] = new LoadedVersion<string>
			{
				VersionId = versionId,
				VersionFolder = "/fake/" + versionId,
				Items = items
			};
		}

		private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(2);

		private static async Task WaitUntil(Func<bool> condition, string description)
		{
			var stopwatch = Stopwatch.StartNew();

			while (stopwatch.Elapsed < _waitTimeout)
			{
				if (condition())
				{
					return;
				}

				await Task.Yield();
			}

			Assert.Fail($"Timed out waiting for {description}.");
		}

		/// <summary>
		/// The ack is the last step of a prepare, so it is the safest point to
		/// resume a test: whatever the version did — commit or stage — has
		/// already happened by the time it lands.
		/// </summary>
		private Task WaitForAck(string versionId)
		{
			return WaitUntil(() => Array.IndexOf(_feed.AckedVersions(), versionId) >= 0, $"the ack for version '{versionId}'");
		}

		private Task WaitForCommit(string versionId)
		{
			return WaitUntil(() => _manager.CurrentVersionId == versionId, $"version '{versionId}' to commit");
		}

		[Test]
		public void ColdBootWithNothingOnDisk_LeavesTheManagerIdle()
		{
			// The load fails before the prepare ever goes asynchronous, so
			// this one really is done by the time Start() returns.
			StartManager();

			Assert.That(_manager.State, Is.EqualTo(ContentManagerState.Idle));
			Assert.That(_manager.CurrentVersionId, Is.Null);
			Assert.That(_manager.Current, Is.Empty);
		}

		[Test]
		public async Task Promote_PreparesCommitsAndAcks()
		{
			DefineVersion("v1", "a", "b");
			StartManager();

			ContentVersion<string> applied = null;
			_manager.OnVersionApplied += v => applied = v;

			_feed.Promote("v1");
			await WaitForAck("v1");

			Assert.That(_manager.CurrentVersionId, Is.EqualTo("v1"));
			Assert.That(_manager.State, Is.EqualTo(ContentManagerState.Idle));
			Assert.That(_manager.Current.Count, Is.EqualTo(2));
			Assert.That(applied, Is.Not.Null);
			Assert.That(applied.VersionId, Is.EqualTo("v1"));
			Assert.That(applied.VersionFolder, Is.EqualTo("/fake/v1"));
			Assert.That(_feed.AckedVersions(), Is.EqualTo(new[] { "v1" }));

			Assert.That(_cache.LiveCount("a"), Is.EqualTo(1));
			Assert.That(_cache.LiveCount("b"), Is.EqualTo(1));
			Assert.That(_cache.TotalLive, Is.EqualTo(2));
		}

		[Test]
		public async Task SecondPromote_ReleasesTheOutgoingVersionAndKeepsSharedPaths()
		{
			DefineVersion("v1", "a", "b");
			DefineVersion("v2", "b", "c");
			StartManager();

			_feed.Promote("v1");
			await WaitForAck("v1");

			_feed.Promote("v2");
			await WaitForAck("v2");

			Assert.That(_manager.CurrentVersionId, Is.EqualTo("v2"));
			Assert.That(_feed.AckedVersions(), Is.EqualTo(new[] { "v1", "v2" }));

			Assert.That(_cache.LiveCount("a"), Is.EqualTo(0), "Only the outgoing version referenced 'a'.");
			Assert.That(_cache.LiveCount("b"), Is.EqualTo(1), "'b' is referenced by both versions and must survive the swap.");
			Assert.That(_cache.LiveCount("c"), Is.EqualTo(1));
			Assert.That(_cache.TotalLive, Is.EqualTo(2));
			Assert.That(_cache.RetainCount("b"), Is.EqualTo(2));
			Assert.That(_cache.ReleaseCount("b"), Is.EqualTo(1));
		}

		[Test]
		public async Task PromoteOfTheCurrentVersion_IsIgnored()
		{
			DefineVersion("v1", "a");
			StartManager();

			_feed.Promote("v1");
			await WaitForAck("v1");

			_feed.Promote("v1");

			Assert.That(_feed.AckedVersions(), Is.EqualTo(new[] { "v1" }));
			Assert.That(_cache.RetainCount("a"), Is.EqualTo(1));
		}

		[Test]
		public async Task ClosedGate_StagesUntilTickCommits()
		{
			DefineVersion("v1", "a");
			DefineVersion("v2", "b");
			StartManager();

			// The very first version always commits: there is nothing on
			// screen yet for a gate to protect.
			_feed.Promote("v1");
			await WaitForAck("v1");

			_gate.Allow = false;

			_feed.Promote("v2");
			await WaitForAck("v2");

			Assert.That(_manager.State, Is.EqualTo(ContentManagerState.Staged));
			Assert.That(_manager.StagedVersionId, Is.EqualTo("v2"));
			Assert.That(_manager.CurrentVersionId, Is.EqualTo("v1"));
			Assert.That(_feed.AckedVersions(), Is.EqualTo(new[] { "v1", "v2" }), "Acks happen at stage time, not commit time.");
			Assert.That(_gate.NotifyCount, Is.EqualTo(1));
			Assert.That(_cache.TotalLive, Is.EqualTo(2), "Both the current and the staged version hold their assets.");

			_manager.TickMainThread();
			Assert.That(_manager.State, Is.EqualTo(ContentManagerState.Staged), "A closed gate must not commit.");

			_gate.Allow = true;
			_manager.TickMainThread();

			Assert.That(_manager.CurrentVersionId, Is.EqualTo("v2"));
			Assert.That(_manager.State, Is.EqualTo(ContentManagerState.Idle));
			Assert.That(_cache.LiveCount("a"), Is.EqualTo(0));
			Assert.That(_cache.TotalLive, Is.EqualTo(1));
		}

		[Test]
		public async Task ApplyStagedNow_CommitsPastAClosedGate()
		{
			DefineVersion("v1", "a");
			DefineVersion("v2", "b");
			StartManager();

			_feed.Promote("v1");
			await WaitForAck("v1");

			_gate.Allow = false;
			_feed.Promote("v2");
			await WaitForAck("v2");

			_manager.ApplyStagedNow();

			Assert.That(_manager.CurrentVersionId, Is.EqualTo("v2"));
			Assert.That(_manager.StagedVersionId, Is.Null);
			Assert.That(_cache.TotalLive, Is.EqualTo(1));
		}

		[Test]
		public async Task PromoteDuringPrepare_CancelsTheFirstAndReleasesEverythingItRetained()
		{
			DefineVersion("v1", "a1", "a2");
			DefineVersion("v2", "b1");
			_cache.PausedPaths.Add("a2");
			StartManager();

			_feed.Promote("v1");
			await WaitUntil(() => _cache.LiveCount("a1") == 1, "the first prepare to get as far as retaining 'a1'");

			Assert.That(_manager.State, Is.EqualTo(ContentManagerState.Preparing), "'a2' is paused, so the first prepare cannot have finished.");

			_feed.Promote("v2");
			await WaitForAck("v2");

			Assert.That(_manager.CurrentVersionId, Is.EqualTo("v2"));

			_cache.ResumePaused();

			await WaitUntil(() => _cache.LiveCount("a1") == 0, "the superseded prepare to unwind");

			Assert.That(_cache.TotalLive, Is.EqualTo(1), "Only the winning version may still hold references.");
			Assert.That(_cache.LiveCount("b1"), Is.EqualTo(1));
			Assert.That(_cache.ReleaseCount("a1"), Is.EqualTo(1));
			Assert.That(_cache.LiveCount("a2"), Is.EqualTo(0));
			Assert.That(_feed.AckedVersions(), Is.EqualTo(new[] { "v2" }), "An abandoned version is never acked.");
			Assert.That(_manager.Current.Count, Is.EqualTo(1));
		}

		[Test]
		public async Task DisposeWhileStaged_ReleasesBothVersions()
		{
			DefineVersion("v1", "a");
			DefineVersion("v2", "b");
			StartManager();

			_feed.Promote("v1");
			await WaitForAck("v1");

			_gate.Allow = false;
			_feed.Promote("v2");
			await WaitForAck("v2");

			Assert.That(_manager.State, Is.EqualTo(ContentManagerState.Staged));
			Assert.That(_cache.TotalLive, Is.EqualTo(2));

			_manager.Dispose();

			Assert.That(_cache.TotalLive, Is.EqualTo(0));
			Assert.That(_feed.HasSubscribers, Is.False, "Dispose must unsubscribe from the feed.");
		}

		[Test]
		public async Task ThrowingAppliedSubscriber_DoesNotLeakOrWedgeTheManager()
		{
			LogAssert.Expect(LogType.Error, new Regex("OnVersionApplied subscriber threw"));

			DefineVersion("v1", "a");
			DefineVersion("v2", "b");
			StartManager();

			Action<ContentVersion<string>> thrower = _ => throw new InvalidOperationException("view blew up");
			_manager.OnVersionApplied += thrower;

			_feed.Promote("v1");
			await WaitForAck("v1");

			Assert.That(_manager.CurrentVersionId, Is.EqualTo("v1"), "The commit itself completed before the subscriber ran.");
			Assert.That(_manager.State, Is.EqualTo(ContentManagerState.Idle), "A throwing subscriber must not wedge the state machine.");
			Assert.That(_feed.AckedVersions(), Is.EqualTo(new[] { "v1" }), "The ack must still be sent.");

			_manager.OnVersionApplied -= thrower;
			_feed.Promote("v2");
			await WaitForAck("v2");

			Assert.That(_manager.CurrentVersionId, Is.EqualTo("v2"));
			Assert.That(_cache.LiveCount("a"), Is.EqualTo(0), "The outgoing version was still released.");
			Assert.That(_cache.TotalLive, Is.EqualTo(1));
		}

		[Test]
		public async Task FailedAssetLoadMidAcquire_UnwindsThePartialRetainsAndDropsTheVersion()
		{
			LogAssert.Expect(LogType.Error, new Regex("Prepare pipeline failed"));

			DefineVersion("v1", "a1", "a2");
			_cache.ThrowingPaths.Add("a2");
			StartManager();

			_feed.Promote("v1");

			// The error is logged inside the prepare's catch, which runs
			// before the handle that reports Preparing is retired.
			await WaitUntil(() => _manager.State == ContentManagerState.Idle, "the failed prepare to unwind");

			Assert.That(_cache.RetainCount("a1"), Is.EqualTo(1));
			Assert.That(_cache.ReleaseCount("a1"), Is.EqualTo(1), "The partial lease must unwind.");
			Assert.That(_cache.TotalLive, Is.EqualTo(0));
			Assert.That(_manager.CurrentVersionId, Is.Null, "A version that failed to prepare is never committed.");
			Assert.That(_feed.AckedVersions(), Is.Empty);
		}

		[Test]
		public async Task UnloadableAsset_IsSkippedWithoutSinkingTheVersion()
		{
			DefineVersion("v1", "a1", "missing");
			_cache.UnloadablePaths.Add("missing");
			StartManager();

			_feed.Promote("v1");
			await WaitForAck("v1");

			Assert.That(_manager.CurrentVersionId, Is.EqualTo("v1"));
			Assert.That(_cache.LiveCount("a1"), Is.EqualTo(1));
			Assert.That(_cache.LiveCount("missing"), Is.EqualTo(0));
			Assert.That(_cache.TotalLive, Is.EqualTo(1));
		}

		[Test]
		public async Task FailedAck_LogsOnceAndStillCommits()
		{
			LogAssert.Expect(LogType.Warning, new Regex("Ack failed for version 'v1'"));

			DefineVersion("v1", "a");
			_feed.AckResult = false;
			StartManager();

			_feed.Promote("v1");
			await WaitForAck("v1");

			Assert.That(_manager.CurrentVersionId, Is.EqualTo("v1"));
			Assert.That(_feed.AckedVersions(), Is.EqualTo(new[] { "v1" }), "Exactly one ack attempt per prepared version.");
		}

		[Test]
		public async Task UnchangedVersion_CommitsWithoutGating()
		{
			DefineVersion("v1", "a");
			_store.Versions["v2"] = new LoadedVersion<string>
			{
				VersionId = "v2",
				VersionFolder = "/fake/v2",
				Items = _store.Versions["v1"].Items
			};
			StartManager();

			_feed.Promote("v1");
			await WaitForAck("v1");

			_gate.Allow = false;
			_feed.Promote("v2");
			await WaitForAck("v2");

			Assert.That(_manager.CurrentVersionId, Is.EqualTo("v2"), "An empty diff commits immediately even behind a closed gate.");
			Assert.That(_manager.State, Is.EqualTo(ContentManagerState.Idle));
			Assert.That(_cache.LiveCount("a"), Is.EqualTo(1), "Retained once by each version, released once by the outgoing one.");
			Assert.That(_cache.TotalLive, Is.EqualTo(1));
		}

		[Test]
		public async Task ColdBootFromDisk_BecomesCurrentWithoutAPromotion()
		{
			DefineVersion("v1", "a");
			_store.ColdBoot = _store.Versions["v1"];

			StartManager();
			await WaitForCommit("v1");

			Assert.That(_manager.CurrentVersionId, Is.EqualTo("v1"));
			Assert.That(_cache.TotalLive, Is.EqualTo(1));
		}
	}
}
