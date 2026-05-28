namespace Bluecadet.Hap
{
    /// <summary>
    /// Determines which frame the decode thread should decode next based on
    /// the main thread's most recent request and the thread's own decode history.
    ///
    /// Accessed only from the decode thread; not thread-safe.
    /// </summary>
    internal sealed class DecodeScheduler
    {
        readonly int _frameCount;
        int _lastExplicit = -1;
        int _lastDecoded  = -1;
        bool _prefetchDone;

        /// <summary>
        /// The most recent frame index the main thread requested that has been
        /// acknowledged (either decoded or found already in the ring buffer).
        /// Used by the decode loop's blocking condition.
        /// </summary>
        public int LastExplicit => _lastExplicit;

        public DecodeScheduler(int frameCount) => _frameCount = frameCount;

        /// <summary>
        /// Given the main thread's current request and playback direction, returns
        /// what the decode thread should do next.
        ///
        /// Returns (target, isPrefetch, shouldBlock):
        ///   target == -1, shouldBlock == false  → requested frame already in ring buffer; skip decode
        ///   target >= 0,  shouldBlock == false  → decode this frame (isPrefetch indicates speculative)
        ///   target == -1, shouldBlock == true   → nothing to do; caller should block on the decode lock
        /// </summary>
        public (int target, bool isPrefetch, bool shouldBlock) Next(int requested, int dir)
        {
            if (requested != _lastExplicit)
            {
                if (requested == _lastDecoded)
                {
                    // Already decoded by a prior prefetch — no decode needed.
                    // Only enable prefetch for the *next* frame if this request is sequential.
                    bool wasSeq = _lastExplicit < 0 ||
                                  requested == (_lastExplicit + dir + _frameCount) % _frameCount;
                    _lastExplicit = requested;
                    _prefetchDone = !wasSeq;
                    return (-1, false, false);
                }

                // Decode this frame in response to the main-thread request.
                return (requested, false, false);
            }

            if (!_prefetchDone && _lastDecoded >= 0 && _frameCount > 1)
            {
                // Caught up with the main thread. Speculatively decode the next sequential
                // frame so it is ready before the main thread asks for it.
                int prefetchTarget = (_lastDecoded + dir + _frameCount) % _frameCount;
                return (prefetchTarget, true, false);
            }

            // Prefetch done (or not applicable). Block until main thread requests a new frame.
            return (-1, false, true);
        }

        /// <summary>
        /// Update scheduler state after successfully decoding a frame.
        /// Must be called once per successful decode with the same (target, wasPrefetch, dir)
        /// values that were returned/used in the decode iteration.
        /// </summary>
        public void OnDecoded(int target, bool wasPrefetch, int dir)
        {
            _lastDecoded = target;
            if (wasPrefetch)
            {
                _prefetchDone = true;
            }
            else
            {
                bool wasSeq = _lastExplicit < 0 ||
                              target == (_lastExplicit + dir + _frameCount) % _frameCount;
                _lastExplicit = target;
                _prefetchDone = !wasSeq;
            }
        }
    }
}
