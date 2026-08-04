using System;
using UnityEngine;

namespace Bluecadet.Hap
{
    /// <summary>
    /// Block-compressed layout of one decoded texture, as reported by the native plugin.
    /// A frame carries one texture for Hap / Hap Alpha / Hap Q / Hap R, and two for
    /// Hap Q Alpha (<see cref="YCoCgDXT5"/> colour plus <see cref="RGTC1"/> alpha).
    /// </summary>
    internal enum HapFormat
    {
        /// <summary>BC1 — Hap.</summary>
        DXT1 = 1,
        /// <summary>BC3 — Hap Alpha.</summary>
        DXT5 = 2,
        /// <summary>BC7 — Hap R.</summary>
        BC7 = 3,
        /// <summary>BC3 carrying scaled YCoCg — Hap Q, and Hap Q Alpha's colour texture.</summary>
        YCoCgDXT5 = 4,
        /// <summary>BC4 — Hap Q Alpha's alpha texture.</summary>
        RGTC1 = 5,
    }

    /// <summary>Format and decoded size of one of a frame's textures.</summary>
    internal readonly struct HapTextureInfo
    {
        public readonly HapFormat Format;

        /// <summary>Decoded byte size of this texture, as reported by the native plugin.</summary>
        public readonly int BufferSize;

        public HapTextureInfo(HapFormat format, int bufferSize)
        {
            Format = format;
            BufferSize = bufferSize;
        }
    }

    /// <summary>
    /// What the Hap variant of an open file means for playback, derived once from the texture
    /// layout the native plugin reports: which texture holds the colour, whether a second one
    /// holds the alpha, which of them must be sampled linearly, and which output shader turns
    /// them back into RGBA. Everything downstream asks this rather than re-deriving it from
    /// formats and texture counts.
    /// </summary>
    internal readonly struct HapVariant
    {
        /// <summary>Format of the frame's colour texture.</summary>
        public readonly HapFormat ColorFormat;

        /// <summary>How many textures a frame carries: 1, or 2 for Hap Q Alpha.</summary>
        public readonly int TextureCount;

        HapVariant(HapFormat colorFormat, int textureCount)
        {
            ColorFormat = colorFormat;
            TextureCount = textureCount;
        }

        /// <summary>Read the variant off the per-texture layout reported for an open file.</summary>
        public static HapVariant From(HapTextureInfo[] textures) =>
            new(textures[0].Format, textures.Length);

        /// <summary>Index of the frame's colour texture.</summary>
        public int ColorIndex => 0;

        /// <summary>
        /// Index of the frame's alpha texture. Only meaningful when
        /// <see cref="HasAlphaTexture"/>.
        /// </summary>
        public int AlphaIndex => 1;

        /// <summary>True for Hap Q Alpha, whose alpha arrives as a second texture.</summary>
        public bool HasAlphaTexture => TextureCount > AlphaIndex;

        /// <summary>
        /// Whether a texture must be created linear. The alpha texture holds coverage, not
        /// colour: sampled through an sRGB format in a linear project its ramp comes out
        /// gamma-warped. The colour textures keep sRGB sampling.
        /// </summary>
        public bool IsLinear(int textureIndex) => HasAlphaTexture && textureIndex == AlphaIndex;

        /// <summary>
        /// Output shader for this variant. All of them V-flip; the YCoCg variants additionally
        /// decode the colour space, and Hap Q Alpha takes its alpha from the second texture.
        /// </summary>
        public string ShaderName
        {
            get
            {
                if (ColorFormat != HapFormat.YCoCgDXT5) return "HapFlip";
                return HasAlphaTexture ? "HapYCoCgAlphaDecode" : "HapYCoCgDecode";
            }
        }
    }

    internal static class HapFormatExtensions
    {
        /// <summary>
        /// Translate a native texture format code. Returns false for a code this package has no
        /// <see cref="HapFormat"/> for, which the caller must treat as an unsupported file
        /// rather than decode as something else.
        /// </summary>
        public static bool TryToHapFormat(int nativeCode, out HapFormat format)
        {
            format = (HapFormat)nativeCode;
            return Enum.IsDefined(typeof(HapFormat), format);
        }

        public static TextureFormat ToUnityFormat(this HapFormat fmt) => fmt switch
        {
            HapFormat.DXT1      => TextureFormat.DXT1,
            HapFormat.DXT5      => TextureFormat.DXT5,
            HapFormat.BC7       => TextureFormat.BC7,
            HapFormat.YCoCgDXT5 => TextureFormat.DXT5, // same GPU format; YCoCg decoded by the shader
            HapFormat.RGTC1     => TextureFormat.BC4,  // single-channel alpha, decoded by the shader
            _                   => TextureFormat.DXT1,
        };
    }
}
