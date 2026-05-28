using System;

namespace Bluecadet.Hap
{
    internal sealed class OpenResult
    {
        public static readonly OpenResult Failed = new(IntPtr.Zero, 0, 0f, 0, 0, 0, default);

        public readonly IntPtr Handle;
        public readonly int FrameCount;
        public readonly float FrameRate;
        public readonly int FrameBufferSize;
        public readonly int Width;
        public readonly int Height;
        public readonly HapFormat Format;

        public OpenResult(IntPtr handle, int frameCount, float frameRate,
                          int frameBufferSize, int width, int height, HapFormat format)
        {
            Handle          = handle;
            FrameCount      = frameCount;
            FrameRate       = frameRate;
            FrameBufferSize = frameBufferSize;
            Width           = width;
            Height          = height;
            Format          = format;
        }
    }
}
