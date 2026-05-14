using System;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Bluecadet.Hap
{
    /// <summary>
    /// A lock-free ring buffer for passing decoded video frames from a background
    /// decode thread to the main thread.
    ///
    /// Design:
    /// - 4 slots backed by NativeArray&lt;byte&gt; (Allocator.Persistent), each large enough
    ///   to hold one decoded frame
    /// - Writer (decode thread) writes to WriteSlot, then calls CommitWrite
    /// - Reader (main thread) reads from TryRead (atomic snapshot of both frame index and data)
    /// - No locks during steady-state operation — uses volatile + memory barriers
    ///
    /// The 4-slot design ensures the writer always has a free slot even when two slots are
    /// protected simultaneously: the previous _readIndex (skipped since the last commit) and
    /// the _pinnedIndex (explicitly pinned by TryRead while the main thread uploads). When
    /// the writer commits, it skips both protected slots before advancing the write head.
    ///
    /// Uses NativeArray&lt;byte&gt; instead of Marshal.AllocHGlobal for:
    /// - Leak detection in the Unity editor (missing Dispose calls are reported)
    /// - Double-free and use-after-dispose safety checks in editor
    /// - Direct compatibility with Texture2D.LoadRawTextureData(NativeArray)
    /// </summary>
    internal sealed class HapFrameRingBuffer : IDisposable
    {
        const int SlotCount = 4;

        /// <summary>Native memory buffers for decoded frame data.</summary>
        readonly NativeArray<byte>[] _slots;

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
        /// Slot the main thread is currently uploading from (set by TryRead, cleared on next TryRead).
        /// Volatile because it's written by the main thread and read by the decode thread in CommitWrite.
        /// CommitWrite skips this slot when advancing the write head, preventing the decode thread from
        /// overwriting data the main thread is still reading.
        /// </summary>
        volatile int _pinnedIndex = -1;

        /// <summary>
        /// 0 = not disposed, 1 = disposed.
        /// Int rather than bool so Interlocked.CompareExchange can guard the dispose
        /// against concurrent calls.
        /// </summary>
        int _disposed;

        public int SlotSize => _slotSize;

        public HapFrameRingBuffer(int slotSize)
        {
            _slotSize = slotSize;
            _slots = new NativeArray<byte>[SlotCount];
            _frameIndices = new int[SlotCount];

            for (int i = 0; i < SlotCount; i++)
            {
                _slots[i] = new NativeArray<byte>(slotSize, Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                _frameIndices[i] = -1;
            }
        }

        /// <summary>
        /// The current write slot as a NativeArray. The decode thread writes decoded frame data here.
        /// </summary>
        public NativeArray<byte> WriteSlot => _slots[_writeIndex];

        /// <summary>
        /// Get the raw pointer to the current write slot for P/Invoke interop.
        /// Caller must be in an unsafe context.
        /// </summary>
        public unsafe IntPtr GetWritePtr()
        {
            return (IntPtr)_slots[_writeIndex].GetUnsafePtr();
        }

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

            // Advance write head to next slot, skipping any slot that is protected:
            //   prev        — the previous read slot, which the main thread may still be uploading from
            //   _pinnedIndex — the slot explicitly pinned by TryRead for the current active upload
            // With SlotCount=4 and at most 2 protected slots, there is always at least one free slot.
            int next = (_writeIndex + 1) % SlotCount;
            int pinned = _pinnedIndex;
            for (int guard = 0; guard < SlotCount - 1 && (next == prev || next == pinned); guard++)
                next = (next + 1) % SlotCount;

            _writeIndex = next;
        }

        /// <summary>
        /// Snapshot the current read slot atomically, returning both the frame index and data
        /// from the same _readIndex capture. Prefer this over separate property reads to avoid
        /// a TOCTOU race where _readIndex could change between them.
        ///
        /// Pins the slot before reading so CommitWrite won't reuse it while the caller is
        /// still uploading from it (see _pinnedIndex).
        ///
        /// Returns false if no frame has been committed yet.
        /// Thread safety: Called only from the main thread.
        /// </summary>
        public bool TryRead(out int frameIndex, out NativeArray<byte> data)
        {
            int idx = _readIndex;
            if (idx < 0)
            {
                frameIndex = -1;
                data = default;
                return false;
            }

            // Pin the slot before reading its contents. The decode thread reads _pinnedIndex
            // in CommitWrite and will skip this slot when choosing the next write target,
            // preventing it from overwriting data we're about to upload.
            _pinnedIndex = idx;

            // Memory barrier ensures the pin is visible to the decode thread and that we
            // read frameIndex and data after the slot index.
            Thread.MemoryBarrier();
            frameIndex = _frameIndices[idx];
            data = _slots[idx];
            return true;
        }

        /// <summary>
        /// Free all native memory. Safe to call multiple times and from any thread —
        /// Interlocked.CompareExchange ensures only the first caller runs the dispose loop.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            for (int i = 0; i < SlotCount; i++)
            {
                if (_slots[i].IsCreated)
                    _slots[i].Dispose();
            }
        }
    }
}
