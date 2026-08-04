using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Bluecadet.Hap
{
    /// <summary>
    /// Result code returned by every fallible native entry point.
    /// Mirrors <c>HapError</c> in the plugin's C header.
    /// </summary>
    internal enum HapError
    {
        Ok = 0,
        /// <summary>A null pointer, an out-of-range index, or a nonsensical count.</summary>
        InvalidArgument = 1,
        /// <summary>The path could not be opened (missing file, no permission).</summary>
        FileNotFound = 2,
        /// <summary>The file exists but could not be stat'd or memory-mapped.</summary>
        FileRead = 3,
        /// <summary>Not a parseable MP4/MOV container.</summary>
        NotAMov = 4,
        /// <summary>A container, but with no Hap video track in it.</summary>
        NoHapTrack = 5,
        /// <summary>A Hap track whose variant cannot be decoded.</summary>
        UnsupportedVariant = 6,
        /// <summary>The Hap track's sample table is empty or inconsistent with the file.</summary>
        CorruptTrack = 7,
        /// <summary>The frame index is outside [0, FrameCount).</summary>
        FrameOutOfRange = 8,
        /// <summary>The frame's bytes are not a valid/supported Hap frame.</summary>
        InvalidFrame = 9,
        /// <summary>The supplied buffer is smaller than the decoded texture.</summary>
        BufferTooSmall = 10,
        /// <summary>An allocation failed.</summary>
        OutOfMemory = 11,
    }

    /// <summary>
    /// P/Invoke bindings to the native bluecadet_hap plugin, which demuxes Hap MOV/MP4
    /// files and decompresses their frames into raw block-compressed texture data.
    ///
    /// Threading: calls on one handle must be serialized by the caller (this package uses a
    /// single decode thread per open file). Different handles are fully independent.
    /// <see cref="SetThreadCount"/> is process-global.
    /// </summary>
    internal static class HapNative
    {
        const string LibName = "bluecadet_hap";

        // ── Raw entry points ─────────────────────────────────────────────────

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        static extern int hap_open(byte[] utf8Path, out IntPtr outHandle);

        /// <summary>Release a handle and everything it owns. IntPtr.Zero is a no-op.</summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hap_close(IntPtr h);

        /// <summary>Video width in pixels, or 0 for a null handle.</summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_width(IntPtr h);

        /// <summary>Video height in pixels, or 0 for a null handle.</summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_height(IntPtr h);

        /// <summary>Number of frames in the video, or 0 for a null handle.</summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_frame_count(IntPtr h);

        /// <summary>Frame rate in frames per second, or 0 for a null handle.</summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern float hap_get_frame_rate(IntPtr h);

        /// <summary>Number of textures each frame carries: 1, or 2 for Hap Q Alpha.</summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_texture_count(IntPtr h);

        /// <summary>
        /// <see cref="HapFormat"/> of the given texture, or 0 if the handle is null or the
        /// index is out of range.
        /// </summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_texture_format(IntPtr h, int texIndex);

        /// <summary>
        /// Decoded byte size of the given texture — the buffer size
        /// <see cref="hap_decode_texture"/> needs — or 0 for a null handle / bad index.
        /// </summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_texture_buffer_size(IntPtr h, int texIndex);

        /// <summary>
        /// Decompress one texture of one frame straight into <paramref name="buf"/> — no
        /// intermediate copy, so the buffer may be a texture's raw data.
        ///
        /// For Hap Q Alpha, decoding texture 0 then texture 1 of the same frame reuses the
        /// sample read for the first call, so decode a frame's textures back to back.
        /// On a non-Ok result the buffer contents are unspecified (the frame may have been
        /// partially decoded), but nothing is written past <paramref name="bufSize"/>.
        /// </summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_decode_texture(IntPtr h, int frameIndex, int texIndex,
                                                    IntPtr buf, int bufSize);

        /// <summary>
        /// Ask the OS to fault in a frame's compressed bytes ahead of decoding it —
        /// <c>madvise(MADV_WILLNEED)</c> on macOS/Linux, <c>PrefetchVirtualMemory</c> on Windows.
        ///
        /// Purely advisory and cheap enough to call once per decoded frame: a null handle, an
        /// out-of-range or negative index, and a hint the kernel declines are all silent no-ops,
        /// and no decode behaves differently either way — only how long it waits on the disk.
        /// </summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hap_prefetch_frame(IntPtr h, int frameIndex);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        static extern int hap_set_thread_count(int threadCount);

        // ── Typed wrappers ───────────────────────────────────────────────────

        /// <summary>
        /// Open a Hap video file. On success <paramref name="handle"/> receives the new handle;
        /// on failure it is IntPtr.Zero and the returned error says why.
        ///
        /// The path is marshalled as UTF-8 explicitly rather than relying on the platform's
        /// default string marshalling, which the native side does not use.
        /// </summary>
        public static HapError Open(string path, out IntPtr handle)
        {
            if (string.IsNullOrEmpty(path))
            {
                handle = IntPtr.Zero;
                return HapError.InvalidArgument;
            }

            int byteCount = Encoding.UTF8.GetByteCount(path);
            var utf8 = new byte[byteCount + 1];
            Encoding.UTF8.GetBytes(path, 0, path.Length, utf8, 0);
            utf8[byteCount] = 0;

            return (HapError)hap_open(utf8, out handle);
        }

        /// <summary>
        /// Decode one texture of one frame into the given buffer.
        /// See <see cref="hap_decode_texture"/> for the contract.
        /// </summary>
        public static HapError DecodeTexture(IntPtr h, int frameIndex, int texIndex, IntPtr buf, int bufSize)
            => (HapError)hap_decode_texture(h, frameIndex, texIndex, buf, bufSize);

        /// <summary>
        /// Set how many threads decode a chunked frame's chunks in parallel. The count
        /// includes the decode thread itself, so 1 means "no helper threads".
        ///
        /// This setting is process-global, not per file: the last caller wins, and the change
        /// applies to every currently playing video from its next chunked frame onwards.
        /// </summary>
        public static HapError SetThreadCount(int threadCount) => (HapError)hap_set_thread_count(threadCount);
    }
}
