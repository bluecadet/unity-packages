using System;
using UnityEngine;

namespace Bluecadet.Hap
{
    /// <summary>
    /// A scoped lease on the newest decoded frame. Holds the ring slot pinned so the decode
    /// thread cannot decode into its textures while the main thread is uploading and drawing
    /// from them, and releases the pin when disposed.
    ///
    /// Obtain via <see cref="HapTextureRing.TryAcquire"/> — the only thing that hands one out,
    /// and only along with a ring — and always use it in a <c>using</c> block.
    /// </summary>
    internal readonly struct HapFrameLease : IDisposable
    {
        /// <summary>Frame index of the decoded frame.</summary>
        public readonly int FrameIndex;

        /// <summary>Ring slot the frame lives in.</summary>
        public readonly int Slot;

        readonly HapTextureRing _ring;

        internal HapFrameLease(int frameIndex, int slot, HapTextureRing ring)
        {
            FrameIndex = frameIndex;
            Slot = slot;
            _ring = ring;
        }

        /// <summary>The frame's colour texture (block-compressed, still V-flipped).</summary>
        public Texture2D ColorTexture => _ring.GetTexture(Slot, _ring.Variant.ColorIndex);

        /// <summary>The frame's alpha texture for Hap Q Alpha, or null for single-texture formats.</summary>
        public Texture2D AlphaTexture
        {
            get
            {
                var variant = _ring.Variant;
                return variant.HasAlphaTexture ? _ring.GetTexture(Slot, variant.AlphaIndex) : null;
            }
        }

        /// <summary>Upload the frame's decoded bytes to the GPU.</summary>
        public void Apply() => _ring.Apply(Slot);

        /// <summary>Record that the frame's textures were handed to the GPU this frame.</summary>
        public void MarkUploaded() => _ring.MarkUploaded(Slot);

        /// <summary>Release the slot pin.</summary>
        public void Dispose() => _ring.ClearPin();
    }
}
