using System;
using System.Threading;
using Unity.Collections;
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

        /// <summary>
        /// 0 = not disposed, 1 = disposed. Int so Interlocked.CompareExchange can guard
        /// against double-Destroy if Dispose() is ever called concurrently.
        /// </summary>
        int _disposed;

        /// <summary>The texture containing the current video frame.</summary>
        public Texture2D Texture => _texture;

        /// <summary>
        /// Create a texture uploader for the given video dimensions and format.
        /// </summary>
        /// <param name="width">Video width in pixels</param>
        /// <param name="height">Video height in pixels</param>
        /// <param name="format">HAP texture format</param>
        public HapTextureUploader(int width, int height, HapFormat format)
        {
            bool unknownFormat = !System.Enum.IsDefined(typeof(HapFormat), format);
            if (unknownFormat)
                Debug.LogWarning($"[HapTextureUploader] Unknown texture format {(int)format}, falling back to DXT1");

            _texture = new Texture2D(width, height, format.ToUnityFormat(), false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
        }

        /// <summary>
        /// Upload raw compressed texture data to the GPU.
        ///
        /// This is called each frame from the main thread. The data comes from
        /// the ring buffer and contains DXT/BC7 compressed pixel data that the GPU
        /// can use directly.
        /// </summary>
        /// <param name="data">NativeArray containing raw compressed texture data</param>
        public void Upload(NativeArray<byte> data)
        {
            if (_texture == null || !data.IsCreated) return;

            // Load the raw compressed data into the texture's CPU buffer
            using (s_LoadDataMarker.Auto())
            {
                _texture.LoadRawTextureData(data);
            }

            // Upload to GPU. Parameters: updateMipmaps=false, makeNoLongerReadable=false
            // We keep it readable so we can update it again next frame
            using (s_ApplyMarker.Auto())
            {
                _texture.Apply(false, false);
            }
        }

        /// <summary>
        /// Destroy the texture and free GPU resources. Safe to call multiple times.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            if (_texture != null)
            {
#if UNITY_EDITOR
                if (!UnityEngine.Application.isPlaying)
                    UnityEngine.Object.DestroyImmediate(_texture);
                else
#endif
                    UnityEngine.Object.Destroy(_texture);
                _texture = null;
            }
        }
    }
}
