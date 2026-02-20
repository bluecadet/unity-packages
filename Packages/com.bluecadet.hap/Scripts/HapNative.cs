using System;
using System.Runtime.InteropServices;

namespace Bluecadet.Hap
{
    /// <summary>
    /// P/Invoke bindings to the native bluecadet_hap plugin.
    ///
    /// The native plugin handles:
    /// - Demuxing: Reading compressed video frames from MOV/MP4 container files
    /// - Decoding: Decompressing HAP's Snappy-compressed data to raw DXT/BC7 texture data
    ///
    /// The plugin is written in C and lives in Native~/. It uses:
    /// - minimp4 for MOV demuxing
    /// - Vidvox HAP library for HAP frame decoding
    /// - snappy-c for Snappy decompression
    /// </summary>
    internal static class HapNative
    {
        const string LibName = "bluecadet_hap";

        /// <summary>
        /// Open a HAP video file. Returns an opaque handle for subsequent calls.
        /// </summary>
        /// <param name="path">Absolute path to the .mov file</param>
        /// <param name="err">Error code if open fails (see Error* constants)</param>
        /// <returns>Handle to the opened video, or IntPtr.Zero on failure</returns>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr hap_open(string path, out int err);

        /// <summary>Close a video handle and free all native resources.</summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hap_close(IntPtr h);

        /// <summary>Get video width in pixels.</summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_width(IntPtr h);

        /// <summary>Get video height in pixels.</summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_height(IntPtr h);

        /// <summary>Get total number of frames in the video.</summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_frame_count(IntPtr h);

        /// <summary>Get video frame rate in frames per second.</summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern float hap_get_frame_rate(IntPtr h);

        /// <summary>
        /// Get the texture format code (TexFormatDXT1, TexFormatDXT5, TexFormatBC7, or TexFormatYCoCgDXT5).
        /// </summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_texture_format(IntPtr h);

        /// <summary>
        /// Get the size in bytes of one decoded frame.
        /// Use this to allocate buffers for hap_decode_frame.
        /// </summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_frame_buffer_size(IntPtr h);

        /// <summary>
        /// Decode a video frame.
        ///
        /// This reads the compressed frame from disk, decompresses it, and writes
        /// raw DXT/BC7 texture data to the output buffer.
        ///
        /// Note: Not thread-safe per handle. Only call from one thread at a time.
        /// </summary>
        /// <param name="h">Video handle</param>
        /// <param name="frameIndex">Frame number to decode (0-based)</param>
        /// <param name="buf">Output buffer for decoded texture data</param>
        /// <param name="size">Size of output buffer (must be >= hap_get_frame_buffer_size)</param>
        /// <returns>ErrorNone on success, or an error code on failure</returns>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_decode_frame(IntPtr h, int frameIndex, IntPtr buf, int size);

        /// <summary>
        /// Set the number of threads for parallel decoding (if supported by the codec).
        /// </summary>
        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hap_set_thread_count(IntPtr h, int count);

        // ─────────────────────────────────────────────────────────────────────
        // Error codes
        // ─────────────────────────────────────────────────────────────────────

        public const int ErrorNone = 0;    // Success
        public const int ErrorFile = 1;    // File not found or I/O error
        public const int ErrorFormat = 2;  // Not a valid HAP video
        public const int ErrorDecode = 3;  // Decompression failed
        public const int ErrorArgs = 4;    // Invalid arguments

        // ─────────────────────────────────────────────────────────────────────
        // Texture format codes
        // ─────────────────────────────────────────────────────────────────────

        public const int TexFormatDXT1      = 1;  // HAP — DXT1/BC1, no alpha, 4:1 compression
        public const int TexFormatDXT5      = 2;  // HAP Alpha — DXT5/BC3, with alpha channel
        public const int TexFormatBC7       = 3;  // BC7 variant
        public const int TexFormatYCoCgDXT5 = 4;  // HAP Q — DXT5 with YCoCg color space, requires shader decode
    }
}
