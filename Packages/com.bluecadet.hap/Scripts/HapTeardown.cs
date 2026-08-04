using System.Collections.Generic;
using UnityEngine;

namespace Bluecadet.Hap
{
    /// <summary>
    /// One file being closed: the session parking its threads and closing the native handle in
    /// the background, and the GPU resources that can only be destroyed on the main thread once
    /// it has. Callers waiting on <see cref="HapPlayer.CloseAsync"/> ride along here.
    ///
    /// Created on the main thread and finished there too, by whoever polls
    /// <see cref="TryFinish"/> each frame.
    /// </summary>
    internal sealed class HapTeardown
    {
        HapFileSession _session;
        HapOutputPipeline _pipeline;
        List<AwaitableCompletionSource> _waiters;

        /// <summary>Starts the session's background teardown immediately.</summary>
        public HapTeardown(HapFileSession session, HapOutputPipeline pipeline)
        {
            _session = session;
            _pipeline = pipeline;
            _session?.BeginTeardown();
        }

        /// <summary>Complete this source once the file is fully released.</summary>
        public void AddWaiter(AwaitableCompletionSource source)
        {
            if (source == null) return;
            _waiters ??= new List<AwaitableCompletionSource>();
            _waiters.Add(source);
        }

        /// <summary>True once the decode thread has parked and the native handle is closed.</summary>
        public bool IsReleased => _session == null || _session.IsTornDown;

        /// <summary>
        /// Block briefly for the background side to finish. Only the destroy path uses this;
        /// see <see cref="HapPlayer"/> for why it is allowed to there.
        /// </summary>
        public bool WaitForRelease(int timeoutMs) =>
            _session == null || _session.WaitForTeardown(timeoutMs);

        /// <summary>
        /// If the file is fully released, destroy its GPU resources. Returns true when the
        /// teardown is done with and can be dropped — at which point the caller should hand its
        /// waiters on with <see cref="DrainWaitersInto"/>. Main thread only.
        /// </summary>
        public bool TryFinish()
        {
            if (!IsReleased) return false;

            _pipeline?.Dispose();
            _pipeline = null;
            _session = null;

            return true;
        }

        /// <summary>
        /// Hand the callers waiting on this close to <paramref name="queue"/>, leaving none here.
        ///
        /// They are queued rather than completed here: their continuations resume inline and are
        /// free to reopen the player, so whoever owns the queue decides when it is safe to let
        /// them run.
        /// </summary>
        public void DrainWaitersInto(HapCompletionQueue queue)
        {
            if (_waiters == null) return;

            foreach (var waiter in _waiters)
                queue.Add(() => waiter.TrySetResult());

            _waiters = null;
        }
    }
}
