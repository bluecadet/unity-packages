using System;

namespace Bluecadet.Hap
{
    /// <summary>
    /// Outcome of a background open: either a live native handle plus the track's metadata,
    /// or the typed reason the file could not be played.
    /// </summary>
    internal sealed class OpenResult
    {
        static readonly HapTextureInfo[] s_NoTextures = Array.Empty<HapTextureInfo>();

        public readonly IntPtr Handle;

        /// <summary>Why the open failed, or <see cref="HapError.Ok"/> on success.</summary>
        public readonly HapError Error;

        public readonly int FrameCount;
        public readonly float FrameRate;
        public readonly int Width;
        public readonly int Height;

        /// <summary>One entry per texture the frames carry (1, or 2 for Hap Q Alpha).</summary>
        public readonly HapTextureInfo[] Textures;

        public bool Success => Error == HapError.Ok && Handle != IntPtr.Zero;

        public OpenResult(IntPtr handle, int frameCount, float frameRate,
                          int width, int height, HapTextureInfo[] textures)
        {
            Handle     = handle;
            Error      = HapError.Ok;
            FrameCount = frameCount;
            FrameRate  = frameRate;
            Width      = width;
            Height     = height;
            Textures   = textures;
        }

        OpenResult(HapError error)
        {
            Handle   = IntPtr.Zero;
            Error    = error;
            Textures = s_NoTextures;
        }

        public static OpenResult Failed(HapError error) => new(error);
    }
}
