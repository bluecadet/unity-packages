using UnityEngine;

namespace Bluecadet.Hap
{
    internal enum HapFormat
    {
        DXT1      = 1,
        DXT5      = 2,
        BC7       = 3,
        YCoCgDXT5 = 4,
    }

    internal static class HapFormatExtensions
    {
        public static HapFormat ToHapFormat(int nativeCode) => (HapFormat)nativeCode;

        public static TextureFormat ToUnityFormat(this HapFormat fmt) => fmt switch
        {
            HapFormat.DXT1      => TextureFormat.DXT1,
            HapFormat.DXT5      => TextureFormat.DXT5,
            HapFormat.BC7       => TextureFormat.BC7,
            HapFormat.YCoCgDXT5 => TextureFormat.DXT5,  // HAP Q — same GPU format, YCoCg decoded by shader
            _                   => TextureFormat.DXT1,
        };

        public static string ShaderName(this HapFormat fmt) =>
            fmt == HapFormat.YCoCgDXT5 ? "HapYCoCgDecode" : "HapFlip";
    }
}
