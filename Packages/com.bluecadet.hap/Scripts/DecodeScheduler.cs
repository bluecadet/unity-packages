namespace Bluecadet.Hap
{
    /// <summary>What the decode thread should do next.</summary>
    internal enum DecodeRequestKind
    {
        /// <summary>Decode the frame the main thread asked for.</summary>
        Decode,

        /// <summary>Speculatively decode a frame ahead of the main thread asking for it.</summary>
        Prefetch,

        /// <summary>The requested frame is already in the ring — nothing to decode this pass.</summary>
        Skip,

        /// <summary>Everything is decoded; park until the main thread asks for another frame.</summary>
        Wait,
    }

    /// <summary>One instruction from the <see cref="DecodeScheduler"/> to the decode thread.</summary>
    internal readonly struct DecodeRequest
    {
        public readonly DecodeRequestKind Kind;

        /// <summary>Frame to decode. Only meaningful for Decode and Prefetch.</summary>
        public readonly int Frame;

        DecodeRequest(DecodeRequestKind kind, int frame)
        {
            Kind = kind;
            Frame = frame;
        }

        /// <summary>Decode this frame in response to the main thread's request.</summary>
        public static DecodeRequest ToDecode(int frame) => new(DecodeRequestKind.Decode, frame);

        /// <summary>Decode this frame speculatively, before the main thread asks for it.</summary>
        public static DecodeRequest ToPrefetch(int frame) => new(DecodeRequestKind.Prefetch, frame);

        /// <summary>The requested frame is already in the ring.</summary>
        public static readonly DecodeRequest Skip = new(DecodeRequestKind.Skip, -1);

        /// <summary>Nothing to decode until the main thread asks for another frame.</summary>
        public static readonly DecodeRequest Wait = new(DecodeRequestKind.Wait, -1);

        /// <summary>True when the frame was chosen speculatively rather than requested.</summary>
        public bool IsPrefetch => Kind == DecodeRequestKind.Prefetch;
    }

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
        /// </summary>
        public DecodeRequest Next(int requested, int dir)
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
                    return DecodeRequest.Skip;
                }

                // Decode this frame in response to the main-thread request.
                return DecodeRequest.ToDecode(requested);
            }

            if (!_prefetchDone && _lastDecoded >= 0 && _frameCount > 1)
            {
                // Caught up with the main thread. Speculatively decode the next sequential
                // frame so it is ready before the main thread asks for it.
                return DecodeRequest.ToPrefetch((_lastDecoded + dir + _frameCount) % _frameCount);
            }

            // Prefetch done (or not applicable). Block until main thread requests a new frame.
            return DecodeRequest.Wait;
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
