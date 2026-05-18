using System;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Bluecadet.Hap
{
    /// <summary>
    /// A FIFO queue for passing decoded video frames from the decode thread to the main thread.
    ///
    /// Frames are delivered in commit order. The main thread's TryPeek applies a clock gate:
    /// a pre-fetched frame whose index is ahead of the playback clock is held in the queue
    /// rather than returned early, eliminating the ±1-display-frame timing jitter that a
    /// "latest wins" ring buffer produces.
    ///
    /// Thread model: single writer (decode thread), single reader (main thread).
    ///
    /// Flush/seek: the main thread calls Flush() when a seek is detected, incrementing a
    /// generation counter. Stale slots (from before the flush) are silently drained by
    /// TryPeek. The decode thread polls FlushVersion to detect flushes and reset its state.
    ///
    /// Separate TryPeek/Consume: TryPeek does NOT advance the read head. Consume() does.
    /// This separation ensures the decode thread cannot reuse a slot while the main thread
    /// is still memcpy-ing from it inside Upload(). Call Consume() after Upload() returns.
    ///
    /// Capacity = 4: supports 1 explicit decode + 1 prefetch + 2 safety slots. The full
    /// guard in GetWritePtr prevents the decode thread from lapping the read head.
    /// </summary>
    internal sealed class HapFrameQueue : IDisposable
    {
        const int Capacity = 4;

        readonly NativeArray<byte>[] _slots;
        readonly int[] _frameIndices;
        readonly int[] _slotVersions;
        readonly int _slotSize;

        // Monotonically increasing. Decode thread is sole writer; main thread reads for empty check.
        volatile int _tail;

        // Monotonically increasing. Main thread is sole writer; decode thread reads for full check.
        volatile int _head;

        // Incremented by main thread on flush/seek. Both threads read; only main thread writes.
        volatile int _flushVersion;

        int _disposed;

        /// <summary>Current flush generation. Decode thread polls this to detect seeks.</summary>
        public int FlushVersion => _flushVersion;

        public HapFrameQueue(int slotSize)
        {
            _slotSize = slotSize;
            _slots = new NativeArray<byte>[Capacity];
            _frameIndices = new int[Capacity];
            _slotVersions = new int[Capacity];

            for (int i = 0; i < Capacity; i++)
            {
                _slots[i] = new NativeArray<byte>(slotSize, Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                _frameIndices[i] = -1;
                _slotVersions[i] = -1; // sentinel: no valid version is -1
            }
        }

        // ── Decode-thread write path ──────────────────────────────────────────

        /// <summary>
        /// Get a raw pointer to the next write slot.
        /// Returns IntPtr.Zero if the queue is full (decode thread should back off).
        /// Also outputs the current flush version so CommitWrite can detect a concurrent flush.
        /// </summary>
        public unsafe IntPtr GetWritePtr(out int version)
        {
            version = _flushVersion;
            if (_tail - _head >= Capacity) return IntPtr.Zero; // full
            return (IntPtr)_slots[_tail % Capacity].GetUnsafePtr();
        }

        /// <summary>
        /// Publish the data written to the current write slot.
        /// Returns false if a Flush happened between GetWritePtr and CommitWrite — in that
        /// case the caller should discard the write and not update its lastDecoded state.
        /// </summary>
        public bool CommitWrite(int frameIndex, int version)
        {
            if (version != _flushVersion) return false; // flush during write: discard
            if (_tail - _head >= Capacity) return false; // full (safety guard)

            int slot = _tail % Capacity;
            _frameIndices[slot] = frameIndex;
            _slotVersions[slot] = version;
            Thread.MemoryBarrier(); // ensure data/metadata visible before tail advance
            _tail++;
            return true;
        }

        // ── Main-thread read path ─────────────────────────────────────────────

        /// <summary>
        /// Peek at the oldest queued frame without consuming it.
        ///
        /// Stale slots (version mismatch after a Flush) are silently drained first.
        ///
        /// Clock gate: if the oldest frame's index is ahead of the playback clock
        /// (pre-fetched but not yet needed), returns false WITHOUT consuming the slot.
        /// The frame stays in the queue and will be returned on a future call when
        /// clockFrame catches up.
        ///
        /// Parameters:
        ///   clockFrame  — ClockToFrame(_clock): the video frame the clock is currently at
        ///   direction   — +1 for forward, -1 for reverse
        ///
        /// After a successful peek, call Consume() once Upload() has finished copying
        /// from the returned data buffer.
        /// </summary>
        public bool TryPeek(int clockFrame, int direction,
                            out int frameIndex, out NativeArray<byte> data)
        {
            while (_tail != _head)
            {
                Thread.MemoryBarrier();
                int slot = _head % Capacity;

                // Drain version-stale slots (written before the last Flush).
                if (_slotVersions[slot] != _flushVersion) { _head++; continue; }

                int fi = _frameIndices[slot];

                // Clock gate: hold pre-fetched frames that are ahead of the playback clock.
                // Forward: fi > clockFrame means the frame is in the future — hold.
                // Reverse: fi < clockFrame means the frame is in the future (going back) — hold.
                bool tooEarly = direction >= 0 ? fi > clockFrame : fi < clockFrame;
                if (tooEarly) { frameIndex = -1; data = default; return false; }

                // Drain frames that are behind the clock when a newer frame is already
                // in the queue. This prevents stale pre-fetches from blocking the desired
                // frame after a scrub. If this is the only frame available, return it
                // anyway — it's the best we have (e.g. decode thread is 1 frame slow).
                bool isPast = direction >= 0 ? fi < clockFrame : fi > clockFrame;
                if (isPast && _tail - _head > 1) { _head++; continue; }

                frameIndex = fi;
                data = _slots[slot];
                return true;
            }

            frameIndex = -1;
            data = default;
            return false;
        }

        /// <summary>
        /// Advance the read head, releasing the peeked slot so the decode thread
        /// may reuse its memory. Must be called after Upload() has finished copying
        /// from the slot's NativeArray.
        /// </summary>
        public void Consume()
        {
            _head++;
        }

        // ── Flush (main thread, on seek) ──────────────────────────────────────

        /// <summary>
        /// Invalidate all currently queued frames. Called by the main thread when a seek
        /// is detected. Stale frames are drained lazily by TryPeek on subsequent calls.
        /// The decode thread detects the generation change via FlushVersion.
        /// </summary>
        public void Flush()
        {
            Interlocked.Increment(ref _flushVersion);
        }

        // ── Dispose ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
            for (int i = 0; i < Capacity; i++)
                if (_slots[i].IsCreated) _slots[i].Dispose();
        }
    }
}
