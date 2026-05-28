using System;
using System.Threading;
using Unity.Collections;
using UnityEngine;

namespace Bluecadet.Hap
{
    /// <summary>
    /// Owns the GPU output pipeline for HAP video playback: a ring of texture uploaders,
    /// a double-buffered RenderTexture pair, and the output blit material.
    ///
    /// Double-buffered RTs prevent the D3D12 within-frame write→read hazard: the blit always
    /// targets the write slot while the scene reads the display slot (written last frame).
    /// Call <see cref="SwapBuffers"/> after the scene renders to advance the display slot.
    ///
    /// The uploader ring prevents a CPU Apply() and a GPU blit from targeting the same
    /// Texture2D simultaneously: consecutive uploads use different slots.
    /// </summary>
    internal sealed class HapOutputPipeline : IDisposable
    {
        readonly HapTextureUploader[] _uploaders;

        /// <summary>Double-buffered output RTs. Null if the output shader failed to load.</summary>
        readonly RenderTexture[] _rts;

        /// <summary>Blit material (flip + optional YCoCg decode). Null if shader load failed.</summary>
        readonly Material _material;

        int _displayIndex;
        int _uploadCount;
        int _lastFrame = -1;

        /// <summary>The most recently used uploader (for RawTexture fallback).</summary>
        HapTextureUploader _lastUploader;

        int _disposed;

        /// <summary>
        /// The RT currently safe to display (front buffer, written last frame).
        /// Null if the output shader failed to load.
        /// </summary>
        public RenderTexture DisplayTexture => _rts?[_displayIndex];

        /// <summary>
        /// The most recently uploaded raw Texture2D.
        /// Useful as a fallback when <see cref="DisplayTexture"/> is null (shader not loaded).
        /// </summary>
        public Texture2D RawTexture => _lastUploader?.Texture ?? _uploaders[0]?.Texture;

        /// <param name="uploaderCount">
        /// Number of uploader slots. Must be at least 2 and typically
        /// <c>Mathf.Max(2, QualitySettings.maxQueuedFrames + 1)</c>.
        /// </param>
        public HapOutputPipeline(int width, int height, HapFormat format, int uploaderCount)
        {
            _uploaders = new HapTextureUploader[uploaderCount];
            for (int i = 0; i < uploaderCount; i++)
                _uploaders[i] = new HapTextureUploader(width, height, format);

            string shaderName = format.ShaderName();
            var shader = Resources.Load<Shader>(shaderName);
            if (shader == null)
            {
                Debug.LogError($"[HapOutputPipeline] Output shader '{shaderName}' not found — video will not be flipped");
                return;
            }

            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _rts = new RenderTexture[2];
            for (int i = 0; i < 2; i++)
            {
                _rts[i] = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                _rts[i].Create();
            }

            // Initialize both RT slots to opaque black so D3D12 never reads
            // uninitialized memory before the first video frame arrives.
            for (int i = 0; i < 2; i++)
                Graphics.Blit(Texture2D.blackTexture, _rts[i]);
        }

        /// <summary>
        /// Upload <paramref name="frameData"/> to the GPU write slot and blit through the output
        /// shader (V-flip + optional YCoCg decode). Returns true if a new RT frame was produced;
        /// returns false for duplicate frames or when the output shader is unavailable.
        ///
        /// The caller must call <see cref="SwapBuffers"/> after the scene has rendered from
        /// <see cref="DisplayTexture"/> so next frame's write slot differs from the display slot.
        /// </summary>
        public bool Upload(NativeArray<byte> frameData, int frameIndex)
        {
            if (frameIndex == _lastFrame) return false;

            var uploader = _uploaders[_uploadCount % _uploaders.Length];
            _uploadCount++;
            uploader.Upload(frameData);
            _lastUploader = uploader;

            if (_rts != null && _material != null)
            {
                int writeIndex = (_displayIndex + 1) % _rts.Length;
                Graphics.Blit(uploader.Texture, _rts[writeIndex], _material);
            }

            _lastFrame = frameIndex;
            return _rts != null && _material != null;
        }

        /// <summary>
        /// Advance the display slot to the RT just written. Call AFTER the scene has rendered
        /// from <see cref="DisplayTexture"/> to guarantee the write and display slots are always
        /// different resources next frame (D3D12 within-frame hazard prevention).
        /// </summary>
        public void SwapBuffers()
        {
            if (_rts != null)
                _displayIndex = (_displayIndex + 1) % _rts.Length;
        }

        /// <summary>Destroy all GPU resources. Safe to call multiple times.</summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            foreach (var u in _uploaders)
                u?.Dispose();

            if (_rts != null)
            {
                foreach (var rt in _rts)
                {
                    if (rt != null)
                    {
                        rt.Release();
                        UnityEngine.Object.Destroy(rt);
                    }
                }
            }

            if (_material != null)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    UnityEngine.Object.DestroyImmediate(_material);
                else
#endif
                    UnityEngine.Object.Destroy(_material);
            }
        }
    }
}
