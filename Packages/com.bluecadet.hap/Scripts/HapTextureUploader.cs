using System;
using UnityEngine;
using Unity.Profiling;

namespace Bluecadet.Hap
{
    /// <summary>
    /// Manages a Texture2D for HAP video playback and handles uploading
    /// raw DXT/BC7 compressed data to it.
    ///
    /// HAP videos store frames pre-compressed in GPU texture formats (DXT1, DXT5, or BC7).
    /// This means the "decoded" frame data can be uploaded directly to the GPU without
    /// CPU-side decompression — the GPU handles decompression during sampling.
    /// This is what makes HAP playback so efficient.
    /// </summary>
    internal sealed class HapTextureUploader : IDisposable
    {
        static readonly ProfilerMarker s_LoadDataMarker = new ProfilerMarker("HapPlayer.LoadRawTextureData");
        static readonly ProfilerMarker s_ApplyMarker = new ProfilerMarker("HapPlayer.TextureApply");

        Texture2D _texture;
        bool _disposed;

        /// <summary>The texture containing the current video frame.</summary>
        public Texture2D Texture => _texture;

        /// <summary>
        /// Create a texture uploader for the given video dimensions and format.
        /// </summary>
        /// <param name="width">Video width in pixels</param>
        /// <param name="height">Video height in pixels</param>
        /// <param name="hapTextureFormat">HAP texture format code from native plugin</param>
        public HapTextureUploader(int width, int height, int hapTextureFormat)
        {
            // Map HAP format codes to Unity TextureFormat
            bool unknownFormat = false;
            var format = hapTextureFormat switch
            {
                HapNative.TexFormatDXT1      => TextureFormat.DXT1,  // HAP
                HapNative.TexFormatDXT5      => TextureFormat.DXT5,  // HAP Alpha
                HapNative.TexFormatBC7       => TextureFormat.BC7,
                HapNative.TexFormatYCoCgDXT5 => TextureFormat.DXT5,  // HAP Q — same GPU format as DXT5, YCoCg decoded by shader
                _ => Fallback(out unknownFormat)
            };

            if (unknownFormat)
                Debug.LogWarning($"[HapTextureUploader] Unknown texture format {hapTextureFormat}, falling back to DXT1");

            // Create texture with no mipmaps (video frames don't need them)
            _texture = new Texture2D(width, height, format, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
        }

        static TextureFormat Fallback(out bool flagged) { flagged = true; return TextureFormat.DXT1; }

        /// <summary>
        /// Upload raw compressed texture data to the GPU.
        ///
        /// This is called each frame from the main thread. The data pointer comes from
        /// the ring buffer and contains DXT/BC7 compressed pixel data that the GPU
        /// can use directly.
        /// </summary>
        /// <param name="data">Pointer to raw compressed texture data</param>
        /// <param name="size">Size of the data in bytes</param>
        public void Upload(IntPtr data, int size)
        {
            if (_texture == null || data == IntPtr.Zero) return;

            // Load the raw compressed data into the texture's CPU buffer
            using (s_LoadDataMarker.Auto())
            {
                _texture.LoadRawTextureData(data, size);
            }

            // Upload to GPU. Parameters: updateMipmaps=false, makeNoLongerReadable=false
            // We keep it readable so we can update it again next frame
            using (s_ApplyMarker.Auto())
            {
                _texture.Apply(false, false);
            }
        }

        /// <summary>
        /// Destroy the texture and free GPU resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_texture != null)
            {
                UnityEngine.Object.Destroy(_texture);
                _texture = null;
            }
        }
    }
}
