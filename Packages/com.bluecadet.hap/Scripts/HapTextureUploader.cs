using System;
using UnityEngine;

namespace Bluecadet.Hap
{
    internal sealed class HapTextureUploader : IDisposable
    {
        Texture2D _texture;

        public Texture2D Texture => _texture;

        public HapTextureUploader(int width, int height, int hapTextureFormat)
        {
            var format = hapTextureFormat switch
            {
                HapNative.TexFormatDXT1 => TextureFormat.DXT1,
                HapNative.TexFormatDXT5 => TextureFormat.DXT5,
                HapNative.TexFormatBC7 => TextureFormat.BC7,
                _ => TextureFormat.DXT1
            };

            _texture = new Texture2D(width, height, format, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
        }

        public void Upload(IntPtr data, int size)
        {
            if (_texture == null || data == IntPtr.Zero) return;
            _texture.LoadRawTextureData(data, size);
            _texture.Apply(false, false);
        }

        public void Dispose()
        {
            if (_texture != null)
            {
                UnityEngine.Object.Destroy(_texture);
                _texture = null;
            }
        }
    }
}
