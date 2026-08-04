using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Bluecadet.Hap
{
    /// <summary>
    /// The open/close half of a <see cref="HapPlayer"/>: the file that is open or on its way in,
    /// the teardown of the one on its way out, and the callers awaiting either.
    ///
    /// The last call wins — an open supersedes whatever came before, and one made while a close
    /// is still finishing queues behind that teardown. Nothing here blocks: requests are started
    /// by <see cref="OpenAsync"/> / <see cref="CloseAsync"/> and carried to their result by
    /// <see cref="Tick"/>. Main thread only, which every entry point enforces.
    ///
    /// Holds no reference to the component that owns it, so it can still finish releasing a file
    /// for a player Unity has already destroyed.
    /// </summary>
    internal sealed class HapLifecycle
    {
        enum State { Idle, Opening, Ready }

        State _state = State.Idle;

        HapFileSession _session;
        HapOutputPipeline _pipeline;

        /// <summary>The file being closed, if any. At most one teardown is ever in flight.</summary>
        HapTeardown _teardown;

        /// <summary>
        /// Resolved path of an open waiting for <see cref="_teardown"/> to finish before it
        /// starts, or null when nothing is queued.
        /// </summary>
        string _queuedPath;

        /// <summary>The path of the request in flight, as the caller wrote it.</summary>
        string _requestPath;

        /// <summary>
        /// Callers awaiting the open in flight. Every request supersedes the ones before it and
        /// clears this, so it only ever holds waiters for a single request.
        /// </summary>
        readonly List<AwaitableCompletionSource<OpenResult>> _openWaiters = new();

        readonly HapCompletionQueue _completions = new();

        /// <summary>
        /// Set as soon as the owning component starts going away. Everything the API can still
        /// be asked to do after that is answered without starting anything.
        /// </summary>
        bool _destroyed;

        // ── Owner notifications ──────────────────────────────────────────────

        /// <summary>
        /// Raised when a request takes on a path, so the owner can record which file it is for.
        /// Not raised for a call that was refused.
        /// </summary>
        public event Action<string> PathAdopted;

        /// <summary>Raised as the current file starts being released, before it is torn down.</summary>
        public event Action Closing;

        /// <summary>
        /// Raised once a file is decoding, after its awaiting callers have been queued but
        /// before they resume, so a handler cannot cut them off.
        /// </summary>
        public event Action Opened;

        // ── State ────────────────────────────────────────────────────────────

        /// <summary>The open file, or null. Non-null exactly while <see cref="IsOpen"/>.</summary>
        public HapFileSession Session => _session;

        /// <summary>The open file's GPU pipeline, or null.</summary>
        public HapOutputPipeline Pipeline => _pipeline;

        /// <summary>True once a file is open and decoding.</summary>
        public bool IsOpen => _state == State.Ready;

        /// <summary>True while an open is in flight, including one queued behind a close.</summary>
        public bool IsOpening => _state == State.Opening || _queuedPath != null;

        /// <summary>True while a file is being released.</summary>
        public bool IsClosing => _teardown != null;

        /// <summary>True while something still needs <see cref="Tick"/> to carry it further.</summary>
        public bool HasPendingWork =>
            _teardown != null || _queuedPath != null ||
            _state == State.Opening || _openWaiters.Count > 0;

        // ── Open / close ─────────────────────────────────────────────────────

        /// <summary>
        /// Open a file, superseding whatever this was doing. The awaitable completes on the main
        /// thread once the file is decoding, or with the reason it did not open.
        /// </summary>
        public Awaitable<OpenResult> OpenAsync(string path)
        {
            var source = new AwaitableCompletionSource<OpenResult>();
            try
            {
                RequestOpen(path, source, nameof(OpenAsync));
            }
            finally
            {
                _completions.Flush();
            }
            return source.Awaitable;
        }

        /// <summary>Open a file without waiting for the result; failures are logged.</summary>
        public void Open(string path)
        {
            try
            {
                RequestOpen(path, null, nameof(Open));
            }
            finally
            {
                _completions.Flush();
            }
        }

        /// <summary>
        /// Close the current file. The awaitable completes once the decode thread has parked,
        /// the file is closed and its textures are released — or immediately if nothing is open.
        /// </summary>
        public Awaitable CloseAsync()
        {
            var source = new AwaitableCompletionSource();
            try
            {
                // Refused: there is nothing left to release and nothing left to wait for.
                if (!TryEnter(nameof(CloseAsync)))
                {
                    source.TrySetResult();
                    return source.Awaitable;
                }

                CloseCurrent(HapOpenStatus.Superseded);

                if (_teardown == null)
                    source.TrySetResult();
                else
                    _teardown.AddWaiter(source);

                return source.Awaitable;
            }
            finally
            {
                _completions.Flush();
            }
        }

        /// <summary>
        /// Close the current file without waiting for the teardown.
        /// </summary>
        /// <param name="supersedeStatus">
        /// What an open still in flight is told: <see cref="HapOpenStatus.Superseded"/> when the
        /// caller asked for this close, <see cref="HapOpenStatus.Cancelled"/> when the component
        /// went away underneath it.
        /// </param>
        public void Close(HapOpenStatus supersedeStatus)
        {
            if (!TryEnter(nameof(Close))) return;

            try
            {
                CloseCurrent(supersedeStatus);
            }
            finally
            {
                _completions.Flush();
            }
        }

        /// <summary>
        /// Advance the open/close state machine and hand out whatever that settled.
        /// </summary>
        public void Tick()
        {
            try
            {
                if (_teardown != null && _teardown.TryFinish())
                {
                    var finished = _teardown;
                    _teardown = null;

                    if (_queuedPath != null)
                    {
                        string queued = _queuedPath;
                        _queuedPath = null;
                        StartOpen(queued);
                    }

                    // Queued, not completed here: a close continuation may reopen the player,
                    // and it must see the state the teardown left behind rather than race it.
                    finished.DrainWaitersInto(_completions);
                }

                if (_state == State.Opening && _session != null)
                {
                    switch (_session.TryConsumeOpenResult())
                    {
                        case SessionOpenStatus.Opened:
                            CompleteOpen();
                            break;
                        case SessionOpenStatus.Failed:
                            FailOpen(_session.Error.ToOpenStatus());
                            break;
                    }
                }
            }
            finally
            {
                _completions.Flush();
            }
        }

        /// <summary>
        /// Release everything for a player that is going away. From here on this is inert: any
        /// open or close a continuation makes is answered immediately and starts nothing, so no
        /// session, thread or native handle can outlive the component that owned it.
        /// </summary>
        /// <param name="waitForReleaseMs">
        /// How long to give the background teardown before giving up on finishing it here. Only
        /// the destroy path passes anything but zero, and only because Unity is about to reclaim
        /// the textures the decode thread writes into and nothing there can await.
        /// </param>
        /// <returns>
        /// A teardown that did not finish in time and needs adopting elsewhere, or null.
        /// </returns>
        public HapTeardown Abandon(int waitForReleaseMs)
        {
            _destroyed = true;

            HapTeardown orphan = null;
            try
            {
                CloseCurrent(HapOpenStatus.Cancelled);

                if (_teardown != null)
                {
                    if (waitForReleaseMs > 0)
                        _teardown.WaitForRelease(waitForReleaseMs);

                    if (_teardown.TryFinish())
                        _teardown.DrainWaitersInto(_completions);
                    else
                        orphan = _teardown;

                    _teardown = null;
                }
            }
            finally
            {
                _completions.Flush();
            }

            return orphan;
        }

        // ── Lifecycle machinery ──────────────────────────────────────────────

        /// <summary>
        /// Guard for every entry point. These mutate state the decode thread reads and resume
        /// continuations, neither of which is safe off the main thread, and a player that is
        /// going away must start nothing at all. Both refusals leave the caller with
        /// <see cref="HapOpenStatus.Cancelled"/>.
        /// </summary>
        bool TryEnter(string apiName)
        {
            if (!HapThread.IsMain)
            {
                Debug.LogError($"[HapPlayer] {apiName} must be called from the main thread");
                return false;
            }

            return !_destroyed;
        }

        void RequestOpen(string path, AwaitableCompletionSource<OpenResult> source, string apiName)
        {
            if (!TryEnter(apiName))
            {
                Complete(source, new OpenResult(HapOpenStatus.Cancelled, path));
                return;
            }

            // Supersede and release whatever came before, while the request path still names it,
            // so the caller being cut off learns which file its request was for.
            CloseCurrent(HapOpenStatus.Superseded);

            _requestPath = path;
            PathAdopted?.Invoke(path);

            if (string.IsNullOrEmpty(path))
            {
                Complete(source, new OpenResult(HapOpenStatus.InvalidPath, path));
                return;
            }

            if (source != null)
                _openWaiters.Add(source);

            string resolved = ResolvePath(path);

            if (_teardown == null)
                StartOpen(resolved);
            else
                _queuedPath = resolved;   // the previous file is still being released
        }

        void StartOpen(string resolvedPath)
        {
            if (_destroyed) return;

            _session = new HapFileSession(resolvedPath);
            _session.Open();
            _state = State.Opening;
        }

        /// <summary>
        /// Stop playback and start releasing whatever is open or opening. Never blocks — the
        /// teardown finishes in <see cref="Tick"/>.
        /// </summary>
        void CloseCurrent(HapOpenStatus supersedeStatus)
        {
            _queuedPath = null;
            Closing?.Invoke();
            ResolveOpenWaiters(new OpenResult(supersedeStatus, _requestPath));

            if (_teardown != null) return;                       // already releasing
            if (_session == null && _pipeline == null)
            {
                _state = State.Idle;
                return;
            }

            _teardown = new HapTeardown(_session, _pipeline);
            _session = null;
            _pipeline = null;
            _state = State.Idle;
        }

        void CompleteOpen()
        {
            // The GPU can be up to maxQueuedFrames behind the main thread, so a texture must
            // not be decoded into again until that many later frames have been uploaded.
            int retireDepth = Mathf.Max(1, QualitySettings.maxQueuedFrames);
            _pipeline = new HapOutputPipeline(_session.Width, _session.Height, _session.Textures, retireDepth);

            // The pipeline could not set up its textures or its output shader, so no frame would
            // ever reach the screen. Stop here rather than spin on errors; it has logged why.
            if (!_pipeline.IsValid)
            {
                FailOpen(HapOpenStatus.GpuSetupFailed);
                return;
            }

            _session.StartDecoding(_pipeline.DecodeTarget, 0);
            _state = State.Ready;

            // Waiters are queued before the notification: a handler is free to close the player,
            // and that must not supersede the open it is being told succeeded.
            ResolveOpenWaiters(new OpenResult(HapOpenStatus.Success, _requestPath));
            Opened?.Invoke();
        }

        void FailOpen(HapOpenStatus status)
        {
            string path = _requestPath;
            bool observed = _openWaiters.Count > 0;

            ResolveOpenWaiters(new OpenResult(status, path));

            // A caller that awaited the result has already been told; only shout about failures
            // nobody is in a position to see.
            if (observed)
                Debug.LogWarning($"[HapPlayer] Could not open '{path}': {status}");
            else
                Debug.LogError($"[HapPlayer] Could not open '{path}': {status}");

            CloseCurrent(HapOpenStatus.Superseded);
        }

        /// <summary>Hand every caller awaiting the request in flight the same outcome.</summary>
        void ResolveOpenWaiters(OpenResult result)
        {
            for (int i = 0; i < _openWaiters.Count; i++)
                Complete(_openWaiters[i], result);

            _openWaiters.Clear();
        }

        void Complete(AwaitableCompletionSource<OpenResult> source, OpenResult result)
        {
            if (source == null) return;
            _completions.Add(() => source.TrySetResult(result));
        }

        static string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (Path.IsPathRooted(path)) return path;
            return Path.Combine(Application.streamingAssetsPath, path);
        }
    }
}
