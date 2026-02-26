using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Bluecadet.Hap
{
    /// <summary>
    /// A lock-free ring buffer for passing decoded video frames from a background
    /// decode thread to the main thread.
    ///
    /// Design:
    /// - 3 slots of native memory, each large enough to hold one decoded frame
    /// - Writer (decode thread) writes to WritePtr, then calls CommitWrite
    /// - Reader (main thread) reads from TryRead (atomic snapshot of both frame index and pointer)
    /// - No locks during steady-state operation — uses volatile + memory barriers
    ///
    /// The 3-slot design ensures the writer always has a slot to write to that isn't
    /// the one the reader is currently using. When the writer commits, it publishes
    /// a new read slot and advances the write head, skipping over the old read slot
    /// if necessary to avoid overwriting data the reader may still be using.
    /// </summary>
    internal sealed class HapFrameRingBuffer : IDisposable
    {
        const int SlotCount = 3;

        /// <summary>Native memory buffers for decoded frame data.</summary>
        readonly IntPtr[] _slots;

        /// <summary>Frame index stored in each slot (-1 if empty).</summary>
        readonly int[] _frameIndices;

        readonly int _slotSize;

        /// <summary>Next slot the writer will write into.</summary>
        int _writeIndex;

        /// <summary>
        /// Slot containing the most recently committed frame, available for reading.
        /// Volatile because it's written by the decode thread and read by the main thread.
        /// </summary>
        volatile int _readIndex = -1;

        /// <summary>
        /// 0 = not disposed, 1 = disposed.
        /// Int rather than bool so Interlocked.CompareExchange can guard the free loop
        /// against concurrent calls from Dispose() and the GC finalizer.
        /// </summary>
        int _disposed;

        public int SlotSize => _slotSize;

        public HapFrameRingBuffer(int slotSize)
        {
            _slotSize = slotSize;
            _slots = new IntPtr[SlotCount];
            _frameIndices = new int[SlotCount];

            for (int i = 0; i < SlotCount; i++)
            {
                _slots[i] = Marshal.AllocHGlobal(slotSize);
                _frameIndices[i] = -1;
            }
        }

        /// <summary>
        /// Ensure native memory is freed even if Dispose() isn't called.
        /// </summary>
        ~HapFrameRingBuffer()
        {
            Dispose();
        }

        /// <summary>
        /// Pointer to the current write slot. The decode thread writes decoded frame data here.
        /// </summary>
        public IntPtr WritePtr => _slots[_writeIndex];

        /// <summary>
        /// Mark the current write slot as containing a decoded frame and make it available
        /// for reading. Advances the write head to the next slot.
        ///
        /// Thread safety: Called only from the decode thread.
        /// </summary>
        public void CommitWrite(int frameIndex)
        {
            // Store the frame index in the current write slot
            _frameIndices[_writeIndex] = frameIndex;

            // Memory barrier ensures the frame data and index are visible before we publish
            Thread.MemoryBarrier();

            // Atomically publish this slot as the new read slot, and get the old read slot
            int prev = Interlocked.Exchange(ref _readIndex, _writeIndex);

            // Advance write head to next slot
            int next = (_writeIndex + 1) % SlotCount;

            // Skip over the slot the reader may still be using (the previous read slot).
            // This prevents the writer from overwriting data during an in-progress upload.
            if (next == prev)
                next = (next + 1) % SlotCount;

            _writeIndex = next;
        }

        /// <summary>
        /// Snapshot the current read slot atomically, returning both the frame index and data
        /// pointer from the same _readIndex capture. Prefer this over separate ReadFrame /
        /// ReadPtr calls to avoid a TOCTOU race where _readIndex could change between the two.
        ///
        /// Returns false if no frame has been committed yet.
        /// Thread safety: Called only from the main thread.
        /// </summary>
        public bool TryRead(out int frameIndex, out IntPtr ptr)
        {
            int idx = _readIndex;
            if (idx < 0)
            {
                frameIndex = -1;
                ptr = IntPtr.Zero;
                return false;
            }

            // Memory barrier ensures we read frameIndex and ptr after the slot index
            Thread.MemoryBarrier();
            frameIndex = _frameIndices[idx];
            ptr = _slots[idx];
            return true;
        }

        /// <summary>
        /// Free all native memory. Safe to call multiple times and from any thread —
        /// Interlocked.CompareExchange ensures only the first caller runs the free loop.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            for (int i = 0; i < SlotCount; i++)
            {
                if (_slots[i] != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_slots[i]);
                    _slots[i] = IntPtr.Zero;
                }
            }

            GC.SuppressFinalize(this);
        }
    }
}
