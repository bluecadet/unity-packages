using System;
using System.Runtime.InteropServices;

namespace Bluecadet.Hap
{
    internal static class HapNative
    {
        const string LibName = "bluecadet_hap";

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr hap_open(string path, out int err);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hap_close(IntPtr h);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_width(IntPtr h);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_height(IntPtr h);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_frame_count(IntPtr h);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern float hap_get_frame_rate(IntPtr h);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_texture_format(IntPtr h);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_get_frame_buffer_size(IntPtr h);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int hap_decode_frame(IntPtr h, int frameIndex, IntPtr buf, int size);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void hap_set_thread_count(IntPtr h, int count);

        public const int ErrorNone = 0;
        public const int ErrorFile = 1;
        public const int ErrorFormat = 2;
        public const int ErrorDecode = 3;
        public const int ErrorArgs = 4;

        public const int TexFormatDXT1 = 1;
        public const int TexFormatDXT5 = 2;
        public const int TexFormatBC7 = 3;
    }
}
