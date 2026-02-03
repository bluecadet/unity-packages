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
    /// - Reader (main thread) reads from ReadPtr/ReadFrame
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

        bool _disposed;

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
        /// Pointer to the most recently committed frame data.
        /// Returns IntPtr.Zero if no frame has been committed yet.
        ///
        /// Thread safety: Called only from the main thread.
        /// </summary>
        public IntPtr ReadPtr
        {
            get
            {
                int idx = _readIndex;
                return idx >= 0 ? _slots[idx] : IntPtr.Zero;
            }
        }

        /// <summary>
        /// Frame index of the most recently committed frame.
        /// Returns -1 if no frame has been committed yet.
        ///
        /// Thread safety: Called only from the main thread.
        /// </summary>
        public int ReadFrame
        {
            get
            {
                int idx = _readIndex;
                if (idx < 0) return -1;

                // Memory barrier ensures we read the frame index after the slot index
                Thread.MemoryBarrier();
                return _frameIndices[idx];
            }
        }

        /// <summary>
        /// Free all native memory. Safe to call multiple times.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

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
