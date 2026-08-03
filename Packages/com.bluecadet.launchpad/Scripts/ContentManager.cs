using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Bluecadet.Launchpad
{
	public enum ContentManagerState
	{
		Idle,
		Preparing,
		Staged
	}

	/// <summary>
	/// One fully prepared content version: its items, its diff against the
	/// version it replaces, and the asset references it holds. Immutable and
	/// self-contained, so publishing a version is a single reference
	/// assignment and there is no window where the manager's view of "which
	/// version" and "which assets" disagree.
	/// </summary>
	public sealed class ContentVersion<T>
	{
		internal ContentVersion(LoadedVersion<T> loaded, ContentDiff<T> diff, AssetLease lease)
		{
			VersionId = loaded.VersionId;
			VersionFolder = loaded.VersionFolder;
			Items = loaded.Items ?? Array.Empty<ContentItem<T>>();
			Diff = diff;
			Lease = lease;
		}

		public string VersionId { get; }

		public string VersionFolder { get; }

		public IReadOnlyList<ContentItem<T>> Items { get; }

		/// <summary>How this version differs from the one it replaces.</summary>
		public ContentDiff<T> Diff { get; }

		/// <summary>The asset references pinned for as long as this version is staged or current.</summary>
		internal AssetLease Lease { get; }
	}

	/// <summary>
	/// Conducts the content lifecycle. On Start, it both subscribes for
	/// promoted versions AND immediately kicks off a cold-boot prepare
	/// (versionId: null) that loads whatever version is already on disk, so
	/// the app is usable even if the controller is unreachable at launch; if
	/// nothing is on disk yet this is a logged no-op and the app waits for
	/// the controller. Thereafter it loads and diffs promoted versions off
	/// the main thread — the store loads on its own worker and ContentDiffer
	/// runs on the pool — then resumes on the main thread to preload assets
	/// for the version, stage the result, and commit it to Current when the
	/// supplied ISwapGate allows. Everything from the asset preload onward
	/// is main-thread work; nothing off-thread touches a Unity object or any
	/// of this manager's state.
	/// Acks the controller as soon as a version is prepared (downloaded +
	/// validated + assets preloaded) — not when it's finally shown — so a
	/// busy/deferred UI never blocks Launchpad's ack tracking.
	///
	/// Concurrency model: exactly one prepare may be in flight. A newly
	/// promoted version cancels it, discards anything staged, and starts its
	/// own — latest always wins, and the cancelled prepare unwinds its own
	/// asset references on the way out.
	///
	/// Failure mode: if the store/mapper throws while loading a version
	/// (malformed or partial content), that version is dropped — logged,
	/// State goes back to Idle, no stage, no ack. CurrentVersionId is left
	/// unchanged so the 10s fallback poll retries later; there is no
	/// partial/corrupt commit.
	///
	/// Known limitation: streamed media (e.g. video played directly off
	/// disk rather than fully preloaded) is not protected by the
	/// ack-at-prepare timing — the version folder a stream is still reading
	/// from is not pinned by anything in this class, so an old version's
	/// files can be removed/replaced by a later swap while a stream is
	/// still open against them.
	/// </summary>
	public sealed class ContentManager<T> : IDisposable
	{
		private readonly IVersionFeed _feed;
		private readonly IContentStore<T> _store;
		private readonly IAssetCache _assets;
		private readonly ISwapGate _gate;
		private readonly Func<T, IEnumerable<string>> _preloadPathsSelector;

		// Cancelled but never disposed: per-prepare sources link to this
		// token and may outlive Dispose by a continuation or two, and
		// disposing a parent out from under a live linked child is exactly
		// the ObjectDisposedException this class must not raise during
		// teardown. One undisposed source per manager, once, is the cheaper
		// side of that trade.
		private CancellationTokenSource _cts;

		// Non-null exactly while a prepare is in flight; also the handle a
		// superseding promotion uses to cancel it. Per-prepare sources ARE
		// disposed (by the prepare that owns them), or every promotion would
		// leak a registration on _cts for the life of the app.
		private CancellationTokenSource _prepareCts;

		private ContentVersion<T> _current;
		private ContentVersion<T> _staged;
		private bool _started;
		private bool _disposed;

		public IReadOnlyList<ContentItem<T>> Current => _current?.Items ?? Array.Empty<ContentItem<T>>();

		public string CurrentVersionId => _current?.VersionId;

		public string StagedVersionId => _staged?.VersionId;

		/// <summary>
		/// Derived from what actually exists rather than tracked separately,
		/// so it cannot drift out of step with the snapshots it describes.
		/// </summary>
		public ContentManagerState State
		{
			get
			{
				if (_staged != null)
				{
					return ContentManagerState.Staged;
				}

				return _prepareCts != null ? ContentManagerState.Preparing : ContentManagerState.Idle;
			}
		}

		/// <summary>Fired on the main thread when a version is staged (before commit).</summary>
		public event Action<ContentVersion<T>> OnVersionStaged;

		/// <summary>Fired on the main thread once Current has been updated.</summary>
		public event Action<ContentVersion<T>> OnVersionApplied;

		public ContentManager(IVersionFeed feed, IContentStore<T> store, IAssetCache assets, ISwapGate gate, Func<T, IEnumerable<string>> preloadPathsSelector)
		{
			_feed = feed ?? throw new ArgumentNullException(nameof(feed));
			_store = store ?? throw new ArgumentNullException(nameof(store));
			_assets = assets ?? throw new ArgumentNullException(nameof(assets));
			_gate = gate ?? throw new ArgumentNullException(nameof(gate));
			_preloadPathsSelector = preloadPathsSelector;
		}

		/// <summary>Subscribes to the feed's promotions and kicks off a cold-boot load. Call once.</summary>
		public void Start(CancellationToken ct)
		{
			if (_started)
			{
				// The new token is silently dropped; Start() is call-once by contract.
				Debug.LogWarning("[ContentManager] Start() called again after already started; ignoring the new CancellationToken.");
				return;
			}

			_started = true;
			_cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			_feed.OnVersionPromoted += OnVersionPromoted;

			// Cold boot: whatever is already on disk becomes Current
			// immediately, without waiting on the controller. If nothing is
			// on disk yet, PrepareAsync logs and leaves State at Idle.
			StartPrepare(null);
		}

		private void OnVersionPromoted(string versionId)
		{
			if (string.IsNullOrEmpty(versionId) || _disposed)
			{
				return;
			}

			if (versionId == CurrentVersionId || versionId == StagedVersionId)
			{
				return;
			}

			StartPrepare(versionId);
		}

		/// <summary>
		/// Latest-wins: cancel whatever is in flight, drop whatever is
		/// staged, and prepare this version instead. The cancelled prepare
		/// releases its own asset references as it unwinds, so nothing here
		/// has to reason about how far it got.
		/// </summary>
		private void StartPrepare(string versionId)
		{
			CancelPrepare();
			DiscardStaged();

			var prepareCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
			_prepareCts = prepareCts;
			_ = PrepareAsync(versionId, prepareCts);
		}

		private void CancelPrepare()
		{
			CancellationTokenSource prepareCts = _prepareCts;
			if (prepareCts == null)
			{
				return;
			}

			// Cleared before cancelling so the prepare's own teardown can tell
			// it is no longer the current one; it still owns the Dispose.
			_prepareCts = null;
			prepareCts.Cancel();
		}

		private async Task PrepareAsync(string versionId, CancellationTokenSource prepareCts)
		{
			CancellationToken ct = prepareCts.Token;

			try
			{
				LoadedVersion<T> loaded;
				try
				{
					loaded = await _store.LoadVersionAsync(versionId, ct).ConfigureAwait(true);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (DirectoryNotFoundException ex) when (versionId == null)
				{
					// Cold boot and nothing is on disk yet is expected and
					// not an error, but the same exception also fires for
					// a misconfigured contentRoot, so include ex.Message
					// (it names the missing path) rather than a fixed
					// string, or a config bug is silently indistinguishable
					// from an empty-disk first run.
					Debug.LogWarning($"[ContentManager] Cold boot: no content resolved yet; waiting for controller. ({ex.Message})");
					return;
				}
				catch (Exception ex)
				{
					// Content keeps working with the old version; CurrentVersionId
					// never advanced, so the fallback poll will retry this later.
					Debug.LogError($"[ContentManager] Failed to load version '{versionId ?? "(cold boot)"}': {ex.Message}");
					return;
				}

				// Snapshotted here, still on the main thread, so the pool
				// thread below never reads manager state: it only sees two
				// item lists that are already finished being built. Diffing a
				// large version is pure CPU work over those lists —
				// ContentDiffer touches no Unity API and no shared mutable
				// state — so it has no business holding up a frame.
				// ConfigureAwait(true) puts us back on the main thread before
				// anything below touches leases, events, or Unity objects.
				IReadOnlyList<ContentItem<T>> previousItems = Current;
				ContentDiff<T> diff = await Task
					.Run(() => ContentDiffer.Diff(previousItems, loaded.Items), ct)
					.ConfigureAwait(true);

				// Refcount invariant: retain exactly one reference per
				// distinct non-empty path referenced by ALL of this version's
				// items (not just added/changed), so committing can release
				// the outgoing version wholesale and shared paths survive on
				// the incoming version's own reference. Assets are loaded
				// before staging so the eventual commit never blocks on IO.
				AssetLease lease = await AssetLease.AcquireAsync(_assets, CollectDistinctPaths(loaded.Items), ct).ConfigureAwait(true);

				try
				{
					// Everything from here to the hand-off is synchronous on
					// purpose: this is the last chance to notice that a newer
					// version superseded us, and an await in between would
					// let a cancellation land after the check but before the
					// publish, stranding this version's references.
					ct.ThrowIfCancellationRequested();

					var version = new ContentVersion<T>(loaded, diff, lease);

					// The prepare's work is done; releasing the handle now
					// means State stops reporting Preparing and the ack below
					// can no longer be cancelled by the next promotion.
					FinishPrepare(prepareCts);

					if (_current == null || diff.IsEmpty || _gate.CanSwapNow)
					{
						Commit(version);
					}
					else
					{
						Stage(version);
					}

					lease = null;
				}
				finally
				{
					// Only still owned here if the hand-off above didn't happen.
					lease?.Dispose();
				}

				bool acked = await _feed.AckAsync(loaded.VersionId, ct).ConfigureAwait(true);
				if (!acked)
				{
					Debug.LogWarning($"[ContentManager] Ack failed for version '{loaded.VersionId}'.");
				}
			}
			catch (OperationCanceledException)
			{
				// Superseded or torn down; the lease unwound itself on the way out.
			}
			catch (Exception ex)
			{
				Debug.LogError($"[ContentManager] Prepare pipeline failed for '{versionId ?? "(cold boot)"}': {ex.Message}");
			}
			finally
			{
				FinishPrepare(prepareCts);
				prepareCts.Dispose();
			}
		}

		/// <summary>Retires a prepare handle, but only if it is still the current one. Idempotent.</summary>
		private void FinishPrepare(CancellationTokenSource prepareCts)
		{
			if (ReferenceEquals(_prepareCts, prepareCts))
			{
				_prepareCts = null;
			}
		}

		private void Stage(ContentVersion<T> version)
		{
			_staged = version;
			_gate.NotifyStagedPending();
			Raise(OnVersionStaged, version, nameof(OnVersionStaged));
		}

		/// <summary>
		/// Publishes a prepared version as Current. The swap itself is a
		/// single reference assignment, so every derived property flips at
		/// once; only then is the outgoing version's asset lease released,
		/// and only after that does anything app-visible run.
		/// </summary>
		private void Commit(ContentVersion<T> version)
		{
			ContentVersion<T> outgoing = _current;

			_current = version;
			_staged = null;

			try
			{
				// Safe to run before subscribers have swapped their views:
				// paths the new version also references survive on its own
				// reference, and nothing renders between here and the event
				// below — it is all one synchronous main-thread turn.
				outgoing?.Lease.Dispose();
			}
			finally
			{
				_gate.ClearPending();
			}

			Raise(OnVersionApplied, version, nameof(OnVersionApplied));
		}

		/// <summary>
		/// Fires an app-facing event with state already fully consistent, and
		/// swallows subscriber exceptions. A view that throws while
		/// refreshing must not abort the rest of the lifecycle — doing so
		/// would skip the controller ack and leave the manager looking busy
		/// forever.
		/// </summary>
		private static void Raise(Action<ContentVersion<T>> handler, ContentVersion<T> version, string eventName)
		{
			if (handler == null)
			{
				return;
			}

			try
			{
				handler.Invoke(version);
			}
			catch (Exception ex)
			{
				Debug.LogError($"[ContentManager] A {eventName} subscriber threw for version '{version.VersionId}': {ex}");
			}
		}

		private void DiscardStaged()
		{
			ContentVersion<T> staged = _staged;
			if (staged == null)
			{
				return;
			}

			_staged = null;
			staged.Lease.Dispose();

			// The discarded version's pending state must not leak into the
			// next one, or it inherits a stale max-defer countdown.
			_gate.ClearPending();
		}

		private HashSet<string> CollectDistinctPaths(IReadOnlyList<ContentItem<T>> items)
		{
			var set = new HashSet<string>();
			if (_preloadPathsSelector == null || items == null)
			{
				return set;
			}

			foreach (var item in items)
			{
				IEnumerable<string> paths = _preloadPathsSelector(item.Data);
				if (paths == null)
				{
					continue;
				}

				foreach (var path in paths)
				{
					if (!string.IsNullOrEmpty(path))
					{
						set.Add(path);
					}
				}
			}

			return set;
		}

		/// <summary>Call every frame from a MonoBehaviour Update to drive gated commits.</summary>
		public void TickMainThread()
		{
			if (!RequireActive(nameof(TickMainThread)))
			{
				return;
			}

			if (_staged != null && _gate.CanSwapNow)
			{
				Commit(_staged);
			}
		}

		/// <summary>Manual commit override (no-op if nothing staged).</summary>
		public void ApplyStagedNow()
		{
			if (!RequireActive(nameof(ApplyStagedNow)))
			{
				return;
			}

			if (_staged != null)
			{
				Commit(_staged);
			}
		}

		private bool RequireActive(string methodName)
		{
			if (_disposed)
			{
				return false;
			}

			if (_started)
			{
				return true;
			}

			Debug.LogWarning($"[ContentManager] {methodName} called before Start(); ignoring.");
			return false;
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;

			_feed.OnVersionPromoted -= OnVersionPromoted;

			// Cancelling the in-flight prepare first means it unwinds its own
			// lease; cancelling _cts then unblocks anything linked to it that
			// started before this manager owned a prepare handle.
			CancelPrepare();

			try
			{
				_cts?.Cancel();
			}
			catch
			{
				// Best-effort teardown; the releases below must still run.
			}

			// Release whatever this instance was holding retained, or the
			// cache entries outlive it.
			DiscardStaged();
			_current?.Lease.Dispose();
			_current = null;
		}
	}
}
