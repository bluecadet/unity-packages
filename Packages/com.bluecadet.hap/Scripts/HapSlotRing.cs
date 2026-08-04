using System;
using System.Threading;

namespace Bluecadet.Hap
{
    /// <summary>
    /// Slot arbitration between the decode thread (writer) and the main thread (reader) for
    /// a ring of frame buffers. Pure bookkeeping — it owns no memory, so the same protocol
    /// covers the GPU texture ring and can be exercised in isolation by tests.
    ///
    /// Protocol:
    /// <list type="bullet">
    /// <item>Decode thread: <see cref="BeginWrite"/> → fill the slot → <see cref="CommitWrite"/>.</item>
    /// <item>Main thread: <see cref="TryPin"/> → use the slot → <see cref="MarkUploaded"/> →
    ///       <see cref="ClearPin"/>.</item>
    /// </list>
    ///
    /// A slot is off limits to the writer while it is
    /// <list type="bullet">
    /// <item>the most recently committed slot (the reader is about to pin it),</item>
    /// <item>pinned by the reader, or</item>
    /// <item>within the retire window: the GPU may still be reading a slot the main thread
    ///       uploaded up to <c>retireDepth</c> uploads ago, so it stays reserved that long.</item>
    /// </list>
    ///
    /// Handing off between the two threads without a lock takes some care:
    /// <list type="bullet">
    /// <item>Each thread publishes to one field and then reads the other's, so every one of
    ///       those accesses is a full fence. With plain acquire/release, both threads can
    ///       miss each other's store and claim the same slot.</item>
    /// <item>Publications are numbered, not just identified by slot. The writer cycles back
    ///       around to a slot the reader is looking at, and a bare slot index cannot tell
    ///       "still the frame I pinned" from "same slot, newer frame" — so the reader would
    ///       keep reading a slot that is being overwritten. Pinning the numbered publication
    ///       makes that case visible, and the reader simply re-pins the newer frame.</item>
    /// </list>
    /// </summary>
    internal sealed class HapSlotRing
    {
        /// <summary>Fewest slots that can satisfy the published/pinned exclusions.</summary>
        public const int MinSlotCount = 3;

        /// <summary>
        /// How many times <see cref="TryPin"/> re-tries when the writer publishes a new frame
        /// mid-pin. Each retry means the writer committed another frame in the meantime, which
        /// takes it a whole decode; a handful of attempts is far more than playback ever needs.
        /// </summary>
        const int MaxPinAttempts = 8;

        /// <summary>No frame has been published yet.</summary>
        const long NoPublication = -1;

        readonly int _slotCount;
        readonly int _retireDepth;
        readonly int[] _frameIndices;
        readonly long[] _uploadStamps;

        /// <summary>
        /// The newest publication: a monotonic sequence number in the high 32 bits and the
        /// slot it landed in in the low 32. Always read and written with Interlocked.
        /// </summary>
        long _published = NoPublication;

        /// <summary>The publication the main thread is currently using, or <see cref="NoPublication"/>.</summary>
        long _pinned = NoPublication;

        /// <summary>Publication counter. Writer thread only.</summary>
        long _sequence;

        /// <summary>Monotonic count of main-thread uploads, the clock the retire window runs on.</summary>
        long _uploadCounter;

        /// <summary>Where the round-robin search resumes. Writer thread only.</summary>
        int _writeIndex;

        /// <summary>
        /// How many times the writer had to fall back to a slot inside the retire window,
        /// which a correctly sized ring never does. Watched by the tests that pin the sizing.
        /// </summary>
        long _fallbackWrites;

        public int SlotCount => _slotCount;

        /// <summary>
        /// Slot count that guarantees the writer always finds a slot outside every exclusion.
        /// The retire window covers the slot uploaded this frame plus the <c>retireDepth</c>
        /// before it — <c>retireDepth + 1</c> slots — and the published and pinned slots can
        /// each be a different one again, so a ring has to be one bigger than that to always
        /// have somewhere to write.
        /// </summary>
        public static int SlotCountFor(int retireDepth) => Math.Max(4, retireDepth + 4);

        /// <param name="slotCount">Number of slots; clamped to at least <see cref="MinSlotCount"/>.</param>
        /// <param name="retireDepth">
        /// How many later uploads must happen before an uploaded slot may be written again —
        /// the number of frames the GPU is allowed to lag behind the main thread.
        /// </param>
        public HapSlotRing(int slotCount, int retireDepth)
        {
            _slotCount    = Math.Max(MinSlotCount, slotCount);
            _retireDepth  = Math.Max(0, retireDepth);
            _frameIndices = new int[_slotCount];
            _uploadStamps = new long[_slotCount];

            for (int i = 0; i < _slotCount; i++)
            {
                _frameIndices[i] = -1;
                // Far enough in the past that no slot starts inside the retire window.
                _uploadStamps[i] = long.MinValue / 2;
            }
        }

        // ── Writer (decode thread) ───────────────────────────────────────────

        /// <summary>Reserve a slot to decode into and return its index.</summary>
        public int BeginWrite()
        {
            long counter   = Interlocked.Read(ref _uploadCounter);
            long published = Interlocked.Read(ref _published);
            long pinned    = Interlocked.Read(ref _pinned);

            int read = SlotOf(published);
            int pin  = SlotOf(pinned);

            // Preferred: a slot that is neither published, nor pinned, nor still inside the
            // retire window of a previous upload.
            for (int i = 1; i <= _slotCount; i++)
            {
                int candidate = (_writeIndex + i) % _slotCount;
                if (candidate == read || candidate == pin) continue;
                if (counter - Interlocked.Read(ref _uploadStamps[candidate]) <= _retireDepth) continue;
                _writeIndex = candidate;
                return candidate;
            }

            // Fallback (only reachable when the ring is smaller than SlotCountFor(retireDepth)):
            // give up the GPU-lag margin, but never hand back a slot that is being read.
            Interlocked.Increment(ref _fallbackWrites);
            for (int i = 1; i <= _slotCount; i++)
            {
                int candidate = (_writeIndex + i) % _slotCount;
                if (candidate == read || candidate == pin) continue;
                _writeIndex = candidate;
                return candidate;
            }

            return _writeIndex;
        }

        /// <summary>Publish the slot returned by <see cref="BeginWrite"/> as the newest frame.</summary>
        public void CommitWrite(int slot, int frameIndex)
        {
            _frameIndices[slot] = frameIndex;

            // Full fence: publishes the frame data and its index, and orders this store ahead
            // of the pin the next BeginWrite reads.
            _sequence++;
            Interlocked.Exchange(ref _published, (_sequence << 32) | (uint)slot);

            _writeIndex = slot;
        }

        // ── Reader (main thread) ─────────────────────────────────────────────

        /// <summary>
        /// Pin the newest published frame so the writer skips its slot, and report which frame
        /// it holds. Returns false if nothing has been committed yet, or (vanishingly rarely)
        /// if the writer kept publishing faster than the pin could settle.
        /// </summary>
        public bool TryPin(out int slot, out int frameIndex)
        {
            for (int attempt = 0; attempt < MaxPinAttempts; attempt++)
            {
                long snapshot = Interlocked.Read(ref _published);
                if (snapshot == NoPublication) break;

                Interlocked.Exchange(ref _pinned, snapshot);

                // If the writer published again while we were pinning, that publication was
                // decided without seeing our pin — so drop it and pin the newer frame instead.
                if (Interlocked.Read(ref _published) != snapshot) continue;

                slot = SlotOf(snapshot);
                frameIndex = _frameIndices[slot];
                return true;
            }

            ClearPin();
            slot = -1;
            frameIndex = -1;
            return false;
        }

        /// <summary>
        /// Record that the slot's contents were handed to the GPU, starting its retire window.
        /// Call before <see cref="ClearPin"/>.
        /// </summary>
        public void MarkUploaded(int slot)
        {
            if (slot < 0 || slot >= _slotCount) return;
            long stamp = Interlocked.Increment(ref _uploadCounter);
            Interlocked.Exchange(ref _uploadStamps[slot], stamp);
        }

        /// <summary>Release the pin taken by <see cref="TryPin"/>.</summary>
        public void ClearPin() => Interlocked.Exchange(ref _pinned, NoPublication);

        // ── Diagnostics (tests) ──────────────────────────────────────────────

        /// <summary>
        /// How many writes have had to give up the retire margin. Anything but zero means the
        /// ring is too small for its retire depth and the writer is handing back slots the GPU
        /// may still be reading.
        /// </summary>
        public long FallbackWrites => Interlocked.Read(ref _fallbackWrites);

        static int SlotOf(long publication) => publication == NoPublication ? -1 : (int)(publication & 0xFFFFFFFFL);
    }
}
