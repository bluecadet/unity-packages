using System;
using System.Threading;
using UnityEngine;

namespace Bluecadet.Hap
{
    /// <summary>
    /// Owns the GPU output path for Hap playback: the ring of decode-target textures, a
    /// double-buffered RenderTexture pair, and the output blit material (V-flip, plus YCoCg
    /// decode and alpha compositing for the Hap Q variants).
    ///
    /// Double-buffered RTs prevent the D3D12 within-frame write→read hazard: the blit always
    /// targets the write slot while the scene reads the display slot (written last frame).
    /// Call <see cref="SwapBuffers"/> after the scene renders to advance the display slot.
    ///
    /// The ring's retire window plays the same role on the upload side: a slot's textures are
    /// not decoded into again until several later frames have been uploaded, so an Apply()
    /// never lands on a texture the GPU may still be reading.
    /// </summary>
    internal sealed class HapOutputPipeline : IDisposable
    {
        static readonly int AlphaTexId = Shader.PropertyToID("_AlphaTex");

        readonly HapTextureRing _ring;

        readonly HapVariant _variant;

        /// <summary>Double-buffered output RTs. Only built once the pipeline is known valid.</summary>
        readonly RenderTexture[] _rts;

        /// <summary>Blit material (flip + optional YCoCg/alpha decode).</summary>
        readonly Material _material;

        int _displayIndex;
        int _lastFrame = -1;

        int _disposed;

        /// <summary>
        /// False when the GPU side could not be set up — the textures are too small for the
        /// decoder's output, or the output shader is missing from the package. Either way no
        /// frame would ever reach the screen, so the caller must abandon the open. The reason
        /// has already been logged.
        /// </summary>
        public bool IsValid { get; }

        /// <summary>
        /// The textures to decode into, to hand to <see cref="HapFileSession.StartDecoding"/>.
        /// Only valid while <see cref="IsValid"/>.
        /// </summary>
        public HapTextureRing DecodeTarget => _ring;

        /// <summary>
        /// The RT currently safe to display (front buffer, written last frame).
        /// Only valid while <see cref="IsValid"/>.
        /// </summary>
        public RenderTexture DisplayTexture => _rts[_displayIndex];

        /// <summary>
        /// Whether the decode thread has published a frame <see cref="Present"/> has not uploaded
        /// yet. The main loop asks this to decide who takes part in a tick's upload phase, so it
        /// answers without pinning a slot; <see cref="Present"/> settles the question for real.
        /// </summary>
        public bool HasPendingFrame => _ring.TryPeekFrame(out int frameIndex) && frameIndex != _lastFrame;

        /// <summary>Bytes one <see cref="Present"/> hands to the GPU.</summary>
        public long UploadBytes => _ring.UploadBytes;

        /// <param name="textures">Per-texture format and size reported by the native plugin.</param>
        /// <param name="retireDepth">
        /// How many frames the GPU may lag behind the main thread — normally
        /// <c>QualitySettings.maxQueuedFrames</c>. Sizes the ring.
        /// </param>
        public HapOutputPipeline(int width, int height, HapTextureInfo[] textures, int retireDepth)
        {
            _variant = HapVariant.From(textures);
            _ring = new HapTextureRing(width, height, textures, retireDepth);
            if (!_ring.IsValid) return;

            // The shaders ship inside this package, so a missing one means a broken install.
            // Playing on without it would show upside-down, undecoded video, so the open fails.
            string shaderName = _variant.ShaderName;
            var shader = Resources.Load<Shader>(shaderName);
            if (shader == null)
            {
                Debug.LogError($"[HapPlayer] Output shader '{shaderName}' is missing from the package; " +
                               $"video will not play");
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

            IsValid = true;
        }

        /// <summary>
        /// Upload the newest decoded frame and blit it through the output shader. Returns true
        /// if a new RT frame was produced; false for duplicate frames and when nothing has been
        /// decoded yet.
        ///
        /// The caller must call <see cref="SwapBuffers"/> after the scene has rendered from
        /// <see cref="DisplayTexture"/> so next frame's write slot differs from the display slot.
        /// </summary>
        public bool Present()
        {
            if (!_ring.TryAcquire(out var lease)) return false;

            using (lease)
            {
                if (lease.FrameIndex == _lastFrame) return false;

                lease.Apply();

                if (_variant.HasAlphaTexture)
                    _material.SetTexture(AlphaTexId, lease.AlphaTexture);

                int writeIndex = (_displayIndex + 1) % _rts.Length;
                Graphics.Blit(lease.ColorTexture, _rts[writeIndex], _material);

                // Start the slot's retire window before the pin is released, so the decode
                // thread can never grab it in between.
                lease.MarkUploaded();
                _lastFrame = lease.FrameIndex;
                return true;
            }
        }

        /// <summary>
        /// Advance the display slot to the RT just written. Call AFTER the scene has rendered
        /// from <see cref="DisplayTexture"/> to guarantee the write and display slots are always
        /// different resources next frame (D3D12 within-frame hazard prevention).
        /// </summary>
        public void SwapBuffers() => _displayIndex = (_displayIndex + 1) % _rts.Length;

        /// <summary>
        /// Destroy all GPU resources. Safe to call multiple times. Main thread only, and only
        /// once the decode thread has stopped — it writes into the ring's textures.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _ring.Dispose();

            // A pipeline that failed to set up still gets disposed, and never built these.
            if (_rts != null)
            {
                foreach (var rt in _rts)
                {
                    if (rt != null)
                    {
                        rt.Release();
                        DestroyObject(rt);
                    }
                }
            }

            if (_material != null)
                DestroyObject(_material);
        }

        /// <summary>
        /// Destroy a resource with whichever call the current mode allows — an editor tool or
        /// an edit-mode preview tears the pipeline down outside play mode.
        /// </summary>
        static void DestroyObject(UnityEngine.Object obj)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEngine.Object.DestroyImmediate(obj);
                return;
            }
#endif
            UnityEngine.Object.Destroy(obj);
        }
    }
}
