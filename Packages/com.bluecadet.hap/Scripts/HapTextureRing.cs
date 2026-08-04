using System;
using System.Threading;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;

namespace Bluecadet.Hap
{
    /// <summary>
    /// The ring of GPU textures video frames are decoded into. Each slot holds one readable
    /// <see cref="Texture2D"/> per texture the format carries (one, or two for Hap Q Alpha).
    ///
    /// This is the zero-copy upload path: every texture's raw data pointer is cached on the
    /// main thread at construction, the decode thread decompresses straight into it, and the
    /// main thread only has to call <see cref="Texture2D.Apply(bool, bool)"/> on the committed
    /// slot. There is no staging buffer and no memcpy between decode and upload.
    ///
    /// Pointer stability across Apply — the assumption this rests on — is verified empirically
    /// by the TextureZeroCopyTests suite. If it ever stops holding, this class is the single
    /// place to fall back to per-slot NativeArrays plus LoadRawTextureData.
    ///
    /// Threading: construction, <see cref="TryAcquire"/>, <see cref="MarkUploaded"/> and
    /// <see cref="Dispose"/> are main-thread only; <see cref="BeginWrite"/>,
    /// <see cref="GetWritePtr"/> and <see cref="CommitWrite"/> are decode-thread only.
    /// The decode thread must have exited before <see cref="Dispose"/> runs — it writes
    /// through raw pointers into memory this destroys.
    /// </summary>
    internal sealed class HapTextureRing : IDisposable
    {
        static readonly ProfilerMarker s_ApplyMarker = new("HapPlayer.TextureApply");

        readonly HapSlotRing _slots;

        /// <summary>[slot][textureIndex]</summary>
        readonly Texture2D[][] _textures;

        /// <summary>[slot][textureIndex] — raw data pointers cached on the main thread.</summary>
        readonly IntPtr[][] _pointers;

        /// <summary>Raw data length of each texture index, the buffer size passed to the decoder.</summary>
        readonly int[] _bufferSizes;

        readonly int _textureCount;

        readonly HapVariant _variant;

        int _disposed;

        /// <summary>Number of textures per frame: 1, or 2 for Hap Q Alpha.</summary>
        public int TextureCount => _textureCount;

        public int SlotCount => _slots.SlotCount;

        /// <summary>What the file's Hap variant means for the textures in this ring.</summary>
        public HapVariant Variant => _variant;

        /// <summary>
        /// False when a slot texture's raw data is smaller than the decoder needs, which would
        /// make every decode fail. Callers should treat the ring as unusable.
        /// </summary>
        public bool IsValid { get; }

        /// <param name="textures">Per-texture format and decoded size reported by the native plugin.</param>
        /// <param name="retireDepth">Frames the GPU may lag behind before a slot is reused.</param>
        public HapTextureRing(int width, int height, HapTextureInfo[] textures, int retireDepth)
        {
            _textureCount = textures.Length;
            _variant      = HapVariant.From(textures);
            _slots        = new HapSlotRing(HapSlotRing.SlotCountFor(retireDepth), retireDepth);
            _textures     = new Texture2D[_slots.SlotCount][];
            _pointers     = new IntPtr[_slots.SlotCount][];
            _bufferSizes  = new int[_textureCount];

            bool valid = true;

            for (int s = 0; s < _slots.SlotCount; s++)
            {
                _textures[s] = new Texture2D[_textureCount];
                _pointers[s] = new IntPtr[_textureCount];

                for (int t = 0; t < _textureCount; t++)
                {
                    HapFormat format = textures[t].Format;
                    TextureFormat unityFormat = format.ToUnityFormat();

                    if (s == 0 && !Enum.IsDefined(typeof(HapFormat), format))
                        Debug.LogWarning($"[HapPlayer] Unknown texture format {(int)format}, " +
                                         $"decoding texture {t} as {unityFormat}");

                    // The decoder overwrites every texel of every slot before any read ever
                    // happens, so the zero-fill (and its accompanying GPU upload of that zeroed
                    // data) a default Texture2D performs is wasted work on the open path.
                    var tex = new Texture2D(width, height, unityFormat, mipChain: false,
                        linear: _variant.IsLinear(t), createUninitialized: true);
                    tex.filterMode = FilterMode.Bilinear;
                    tex.wrapMode = TextureWrapMode.Clamp;

                    _textures[s][t] = tex;

                    var raw = tex.GetRawTextureData<byte>();
                    unsafe { _pointers[s][t] = (IntPtr)raw.GetUnsafePtr(); }

                    if (s != 0) continue;

                    // The decoder is handed the texture's real capacity, so a mismatch here is
                    // reported once rather than turning into a decode error every frame.
                    _bufferSizes[t] = raw.Length;
                    if (raw.Length < textures[t].BufferSize)
                    {
                        valid = false;
                        Debug.LogError(
                            $"[HapPlayer] Texture {t} holds {raw.Length} bytes but the decoder needs " +
                            $"{textures[t].BufferSize} for {width}x{height} {format}; video will not play");
                    }
                }
            }

            IsValid = valid;
        }

        // ── Decode thread ────────────────────────────────────────────────────

        /// <summary>Reserve a slot to decode into.</summary>
        public int BeginWrite() => _slots.BeginWrite();

        /// <summary>Raw data pointer of one texture of a reserved slot.</summary>
        public IntPtr GetWritePtr(int slot, int textureIndex) => _pointers[slot][textureIndex];

        /// <summary>Byte capacity of one texture, to pass to the decoder as the buffer size.</summary>
        public int GetBufferSize(int textureIndex) => _bufferSizes[textureIndex];

        /// <summary>Publish a fully decoded slot to the main thread.</summary>
        public void CommitWrite(int slot, int frameIndex) => _slots.CommitWrite(slot, frameIndex);

        // ── Main thread ──────────────────────────────────────────────────────

        /// <summary>
        /// Take a lease on the newest decoded frame. The lease pins the slot against the
        /// decode thread until it is disposed, so use it in a <c>using</c> block.
        /// Returns false if nothing has been decoded yet.
        /// </summary>
        public bool TryAcquire(out HapFrameLease lease)
        {
            if (!_slots.TryPin(out int slot, out int frameIndex))
            {
                lease = default;
                return false;
            }
            lease = new HapFrameLease(frameIndex, slot, this);
            return true;
        }

        /// <summary>Texture <paramref name="textureIndex"/> of a slot.</summary>
        public Texture2D GetTexture(int slot, int textureIndex) => _textures[slot][textureIndex];

        /// <summary>Push a slot's decoded bytes to the GPU, keeping the textures readable.</summary>
        public void Apply(int slot)
        {
            using (s_ApplyMarker.Auto())
            {
                var slotTextures = _textures[slot];
                for (int t = 0; t < slotTextures.Length; t++)
                {
                    if (slotTextures[t] != null)
                        slotTextures[t].Apply(false, false);
                }
            }
        }

        /// <summary>Start a slot's retire window after its contents were handed to the GPU.</summary>
        public void MarkUploaded(int slot) => _slots.MarkUploaded(slot);

        /// <summary>Release the lease's pin.</summary>
        public void ClearPin() => _slots.ClearPin();

        /// <summary>
        /// Destroy every texture. Main thread only, and only once the decode thread has
        /// exited — it writes into this memory through cached raw pointers.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            for (int s = 0; s < _textures.Length; s++)
            {
                for (int t = 0; t < _textures[s].Length; t++)
                {
                    var tex = _textures[s][t];
                    if (tex == null) continue;
#if UNITY_EDITOR
                    if (!Application.isPlaying)
                        UnityEngine.Object.DestroyImmediate(tex);
                    else
#endif
                        UnityEngine.Object.Destroy(tex);
                    _textures[s][t] = null;
                    _pointers[s][t] = IntPtr.Zero;
                }
            }
        }
    }
}
