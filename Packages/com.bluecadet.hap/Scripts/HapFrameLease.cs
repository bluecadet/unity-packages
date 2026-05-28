using System;
using Unity.Collections;

namespace Bluecadet.Hap
{
    /// <summary>
    /// A scoped lease on a decoded frame in the ring buffer.
    /// Holds the frame data while the caller uses it and releases the ring-buffer
    /// pin automatically when disposed, preventing the decode thread from overwriting
    /// the slot while the main thread is reading it.
    ///
    /// Obtain via <see cref="HapFrameRingBuffer.TryAcquire"/>.
    /// Always use in a <c>using</c> block or call <see cref="Dispose"/> explicitly.
    /// </summary>
    internal struct HapFrameLease : IDisposable
    {
        /// <summary>Frame index of the decoded frame.</summary>
        public readonly int FrameIndex;

        /// <summary>Raw decoded texture data (DXT/BC7 bytes).</summary>
        public readonly NativeArray<byte> Data;

        readonly HapFrameRingBuffer _buffer;

        internal HapFrameLease(int frameIndex, NativeArray<byte> data, HapFrameRingBuffer buffer)
        {
            FrameIndex = frameIndex;
            Data = data;
            _buffer = buffer;
        }

        /// <summary>Release the ring-buffer pin. Safe to call on a default struct (no-op).</summary>
        public void Dispose() => _buffer?.ClearPin();
    }
}
