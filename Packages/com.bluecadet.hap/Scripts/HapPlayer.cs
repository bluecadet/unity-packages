using System;
using System.IO;
using System.Threading;
using UnityEngine;
using Unity.Profiling;

namespace Bluecadet.Hap
{
    /// <summary>
    /// How HapPlayer outputs the decoded video frame.
    /// </summary>
    public enum HapRenderMode
    {
        /// <summary>No automatic rendering; read <see cref="HapPlayer.Texture"/> from script.</summary>
        APIOnly,
        /// <summary>Automatically apply the texture to a Renderer via MaterialPropertyBlock.</summary>
        MaterialOverride,
        /// <summary>Blit each frame to a RenderTexture (useful for UI or multi-material setups).</summary>
        RenderTexture
    }

    /// <summary>
    /// Which Unity time source drives playback.
    /// </summary>
    public enum HapTimeSource
    {
        /// <summary>Uses Time.deltaTime — affected by Time.timeScale.</summary>
        GameTime,
        /// <summary>Uses Time.unscaledDeltaTime — plays even when Time.timeScale is 0.</summary>
        UnscaledGameTime
    }

    /// <summary>
    /// MonoBehaviour that plays HAP-encoded video files.
    ///
    /// Architecture overview:
    /// - A background thread (DecodeLoop) reads compressed frames from disk and decompresses them
    ///   into GPU-ready DXT/BC7 texture data using a native C plugin.
    /// - The main thread uploads the decompressed data to a Texture2D each frame.
    /// - A ring buffer passes decoded frames from the background thread to the main thread
    ///   without allocations or locks during steady-state playback.
    ///
    /// This design keeps expensive I/O and decompression off the main thread while still
    /// allowing the GPU texture upload (which must happen on the main thread) to occur
    /// without stalling on disk reads.
    /// </summary>
    public class HapPlayer : MonoBehaviour
    {
        // ─────────────────────────────────────────────────────────────────────
        // Serialized fields (exposed in Inspector)
        // ─────────────────────────────────────────────────────────────────────

        [SerializeField] string filePath;
        [SerializeField] bool playOnEnable = true;
        [SerializeField] bool loop = true;
        [SerializeField] Renderer targetRenderer;
        [SerializeField] HapRenderMode renderMode = HapRenderMode.MaterialOverride;
        [SerializeField] HapTimeSource timeSource = HapTimeSource.GameTime;
        [SerializeField] float playbackSpeed = 1f;
        [SerializeField] RenderTexture targetRenderTexture;

        // ─────────────────────────────────────────────────────────────────────
        // Native handle and managed helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Opaque pointer to the native HapHandle (demuxer + decoder state).</summary>
        IntPtr _handle;

        /// <summary>Ring buffer holding decoded frame data, shared between decode thread and main thread.</summary>
        HapFrameRingBuffer _ringBuffer;

        /// <summary>Manages the Texture2D and uploads raw DXT/BC7 data to it.</summary>
        HapTextureUploader _uploader;

        // ─────────────────────────────────────────────────────────────────────
        // Video metadata (populated on Open)
        // ─────────────────────────────────────────────────────────────────────

        int _frameCount;
        float _frameRate;
        float _duration;
        int _frameBufferSize;  // Size in bytes of one decoded frame
        int _width;
        int _height;

        // ─────────────────────────────────────────────────────────────────────
        // Playback state (main thread only)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Current playback position in seconds.</summary>
        float _clock;

        /// <summary>True if playback is advancing (not paused/stopped).</summary>
        bool _playing;

        /// <summary>Frame index of the last frame uploaded to the texture (to avoid redundant uploads).</summary>
        int _lastUploadedFrame = -1;

        /// <summary>
        /// Unity frame count when the video was last opened. We skip GPU uploads in the same frame
        /// as Open() because D3D12 requires at least one command-list flush between RenderTexture.Create()
        /// and the first Graphics.Blit that targets it — otherwise the GPU encounters an uninitialized
        /// render target in the same command buffer and removes the device.
        /// </summary>
        int _openedFrame = -1;

        // ─────────────────────────────────────────────────────────────────────
        // Background decode thread coordination
        // ─────────────────────────────────────────────────────────────────────

        Thread _decodeThread;

        /// <summary>Set to false to signal the decode thread to exit.</summary>
        volatile bool _decodeRunning;

        /// <summary>The frame index the main thread wants decoded next. The decode thread watches this.</summary>
        volatile int _decodeTargetFrame = -1;

        /// <summary>
        /// Sign of the current playback direction: +1 for forward, -1 for reverse.
        /// Written by the main thread, read by the decode thread to orient pre-fetching.
        /// </summary>
        volatile int _decodeDirection = 1;

        /// <summary>Lock for coordinating between main thread and decode thread.</summary>
        readonly object _decodeLock = new object();

        /// <summary>Signaled when the decode thread exits, so Close() can wait for cleanup.</summary>
        readonly ManualResetEventSlim _decodeExited = new ManualResetEventSlim(true);

        // ─────────────────────────────────────────────────────────────────────
        // Rendering helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Reusable MaterialPropertyBlock to avoid material instancing.</summary>
        MaterialPropertyBlock _mpb;

        /// <summary>Cached shader property ID for _MainTex.</summary>
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        // ─────────────────────────────────────────────────────────────────────
        // Output RenderTexture (all formats)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Final decoded and orientation-corrected output texture.
        /// All formats blit through here to correct the 180° orientation flip and (for HAP Q)
        /// perform the YCoCg→RGB color space conversion.
        /// </summary>
        RenderTexture _outputRT;

        /// <summary>Material used for the output blit. Shader varies by format.</summary>
        Material _outputMat;

        // ─────────────────────────────────────────────────────────────────────
        // Profiler markers (visible in Unity Profiler)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Speed values whose absolute value is below this are treated as zero (paused).</summary>
        const float k_PlaybackSpeedEpsilon = 1e-5f;

        static readonly ProfilerMarker s_UpdateMarker     = new ProfilerMarker("HapPlayer.Update");
        static readonly ProfilerMarker s_UploadMarker     = new ProfilerMarker("HapPlayer.UploadFrame");
        static readonly ProfilerMarker s_RenderMarker     = new ProfilerMarker("HapPlayer.Render");
        static readonly ProfilerMarker s_ReadSampleMarker = new ProfilerMarker("HapPlayer.ReadSample"); // I/O / page-fault time
        static readonly ProfilerMarker s_DecompressMarker = new ProfilerMarker("HapPlayer.Decompress");  // Snappy CPU time

        // ─────────────────────────────────────────────────────────────────────
        // Events
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Raised when playback reaches the end and Loop is false.</summary>
        public event Action PlaybackCompleted;

        /// <summary>Raised each time playback loops back to the beginning.</summary>
        public event Action PlaybackLooped;

        // ─────────────────────────────────────────────────────────────────────
        // Public properties
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The current video frame as a correctly-oriented RGBA RenderTexture.
        /// Falls back to the raw DXT Texture2D if the output shader failed to load.
        /// </summary>
        public Texture Texture => _outputRT != null ? (Texture)_outputRT : (Texture)_uploader?.Texture;

        public bool IsPlaying => _playing && Mathf.Abs(playbackSpeed) > k_PlaybackSpeedEpsilon;
        public bool IsOpen => _handle != IntPtr.Zero;
        public int FrameCount => _frameCount;
        public float Duration => _duration;
        public float FrameRate => _frameRate;
        public int Width => _width;
        public int Height => _height;
        public string FilePath => filePath;

        public bool Loop
        {
            get => loop;
            set => loop = value;
        }

        public Renderer TargetRenderer
        {
            get => targetRenderer;
            set => targetRenderer = value;
        }

        public HapRenderMode RenderMode
        {
            get => renderMode;
            set => renderMode = value;
        }

        public HapTimeSource TimeSource
        {
            get => timeSource;
            set => timeSource = value;
        }

        /// <summary>
        /// Playback speed multiplier. Negative values play in reverse. 0 is treated as paused.
        /// To play in reverse, set a negative speed before calling <see cref="Play"/>.
        /// If the current position is at 0, <see cref="Play"/> automatically seeks to the end.
        /// </summary>
        public float PlaybackSpeed
        {
            get => playbackSpeed;
            set => playbackSpeed = value;
        }

        public RenderTexture TargetRenderTexture
        {
            get => targetRenderTexture;
            set => targetRenderTexture = value;
        }

        /// <summary>
        /// Current playback time in seconds. Setting this seeks to that time.
        /// </summary>
        public float Time
        {
            get => _clock;
            set
            {
                _clock = Mathf.Clamp(value, 0f, _duration);
                if (_frameCount > 0)
                    RequestDecode(ClockToFrame(_clock));
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Unity lifecycle
        // ─────────────────────────────────────────────────────────────────────

        void OnEnable()
        {
            Open();
            if (playOnEnable && IsOpen)
                Play();
        }

        void OnDisable()
        {
            Close();
        }

        void OnDestroy()
        {
            Close();
            _decodeExited.Dispose();
        }

        /// <summary>
        /// Main update loop: advance clock, request decode, upload frame, render.
        /// </summary>
        void Update()
        {
            if (!IsOpen) return;

            using (s_UpdateMarker.Auto())
            {
                UpdatePlayback();
            }
        }

        void UpdatePlayback()
        {
            // Advance playback clock if playing (speed == 0 is treated as paused)
            if (_playing && Mathf.Abs(playbackSpeed) > k_PlaybackSpeedEpsilon)
            {
                float dt = timeSource == HapTimeSource.UnscaledGameTime
                    ? UnityEngine.Time.unscaledDeltaTime
                    : UnityEngine.Time.deltaTime;
                _clock += dt * playbackSpeed;

                // Handle boundary (forward: past end, reverse: before start)
                if (_clock >= _duration)
                {
                    if (loop)
                    {
                        _clock %= _duration;
                        PlaybackLooped?.Invoke();
                    }
                    else
                    {
                        _clock = _duration;
                        _playing = false;
                        PlaybackCompleted?.Invoke();
                    }
                }
                else if (_clock < 0f)
                {
                    if (loop)
                    {
                        // ((_clock % _duration) + _duration) % _duration maps any negative clock
                        // into [0, _duration), including exact multiples of _duration.
                        _clock = ((_clock % _duration) + _duration) % _duration;
                        PlaybackLooped?.Invoke();
                    }
                    else
                    {
                        _clock = 0f;
                        _playing = false;
                        PlaybackCompleted?.Invoke();
                    }
                }

                // Tell the decode thread the current direction and which frame we need.
                _decodeDirection = playbackSpeed > 0f ? 1 : -1;
                int frame = ClockToFrame(_clock);
                RequestDecode(frame);
            }

            // Upload the latest decoded frame to the GPU texture
            using (s_UploadMarker.Auto())
            {
                UploadFrame();
            }

            // Apply the texture to the output target based on render mode
            using (s_RenderMarker.Auto())
            switch (renderMode)
            {
                case HapRenderMode.MaterialOverride:
                    var tex = Texture;
                    if (targetRenderer != null && tex != null)
                    {
                        if (_mpb == null) _mpb = new MaterialPropertyBlock();
                        targetRenderer.GetPropertyBlock(_mpb);
                        _mpb.SetTexture(MainTexId, tex);
                        targetRenderer.SetPropertyBlock(_mpb);
                    }
                    break;
                case HapRenderMode.RenderTexture:
                    var srcTex = Texture;
                    if (targetRenderTexture != null && srcTex != null)
                        Graphics.Blit(srcTex, targetRenderTexture);
                    break;
                case HapRenderMode.APIOnly:
                    // User reads Texture property directly
                    break;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public playback control
        // ─────────────────────────────────────────────────────────────────────

        public void Play()
        {
            if (!IsOpen) return;
            // When starting reverse playback from position 0, jump to the end so the first
            // Update doesn't immediately hit the start-of-video boundary.
            if (playbackSpeed < -k_PlaybackSpeedEpsilon && _clock <= 0f)
            {
                _clock = _duration;
                if (_frameCount > 0)
                    RequestDecode(ClockToFrame(_clock));
            }
            _playing = true;
        }

        public void Pause()
        {
            _playing = false;
        }

        /// <summary>Stop playback and reset to frame 0, regardless of playback direction.</summary>
        public void Stop()
        {
            _playing = false;
            _clock = 0f;
            if (IsOpen && _frameCount > 0)
                RequestDecode(0);
        }

        /// <summary>Close current file (if any) and open a new one.</summary>
        public void Open(string path)
        {
            string prevPath = filePath;
            Close();
            filePath = path;
            Open();
            // If open failed, restore the previous path so a future OnEnable retries the
            // last known-good file rather than permanently recording an invalid one.
            if (!IsOpen)
                filePath = prevPath;
        }

        // ─────────────────────────────────────────────────────────────────────
        // File open/close
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolve relative paths against StreamingAssets, leave absolute paths unchanged.
        /// </summary>
        static string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (Path.IsPathRooted(path)) return path;
            return Path.Combine(Application.streamingAssetsPath, path);
        }

        /// <summary>
        /// Open the video file, initialize native handle, ring buffer, texture, and start decode thread.
        /// </summary>
        void Open()
        {
            if (string.IsNullOrEmpty(filePath)) return;

            string resolved = ResolvePath(filePath);

            // Open the native demuxer/decoder
            int err;
            _handle = HapNative.hap_open(resolved, out err);
            if (_handle == IntPtr.Zero)
            {
                Debug.LogError($"[HapPlayer] Failed to open '{resolved}', error: {err}");
                return;
            }

            // Read video metadata
            _frameCount = HapNative.hap_get_frame_count(_handle);
            _frameRate = HapNative.hap_get_frame_rate(_handle);

            if (_frameRate <= 0f || _frameCount <= 0)
            {
                Debug.LogError($"[HapPlayer] Invalid video ({_frameCount} frames, {_frameRate} fps) in '{resolved}'");
                HapNative.hap_close(_handle);
                _handle = IntPtr.Zero;
                return;
            }

            _duration = _frameCount / _frameRate;
            _frameBufferSize = HapNative.hap_get_frame_buffer_size(_handle);

            _width = HapNative.hap_get_width(_handle);
            _height = HapNative.hap_get_height(_handle);
            int texFormat = HapNative.hap_get_texture_format(_handle);

            // Create ring buffer for passing decoded frames from decode thread to main thread
            _ringBuffer = new HapFrameRingBuffer(_frameBufferSize);

            // Create texture uploader (manages the Texture2D)
            _uploader = new HapTextureUploader(_width, _height, texFormat);

            // Set up the output blit pipeline.
            // All formats need a V-flip to correct Unity's raw DXT orientation
            // (HAP stores top-to-bottom; LoadRawTextureData treats it as bottom-to-top).
            // HAP Q additionally needs YCoCg→RGB color space conversion.
            string shaderName = texFormat == HapNative.TexFormatYCoCgDXT5
                ? "HapYCoCgDecode"
                : "HapFlip";
            var outputShader = Resources.Load<Shader>(shaderName);
            if (outputShader == null)
            {
                Debug.LogError($"[HapPlayer] Output shader '{shaderName}' not found — video will be unflipped");
            }
            else
            {
                _outputMat = new Material(outputShader) { hideFlags = HideFlags.HideAndDontSave };
                _outputRT = new RenderTexture(_width, _height, 0, RenderTextureFormat.ARGB32)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                _outputRT.Create();
            }

            _clock = 0f;
            _lastUploadedFrame = -1;
            _openedFrame = UnityEngine.Time.frameCount;

            // Start background decode thread
            _decodeExited.Reset();
            _decodeRunning = true;
            _decodeThread = new Thread(DecodeLoop)
            {
                IsBackground = true,
                Name = "HapDecode",
                // AboveNormal reduces wake-up scheduling latency, which is
                // measurably worse on Windows than macOS at default priority.
                Priority = System.Threading.ThreadPriority.AboveNormal,
            };
            _decodeThread.Start();

            // Request the first frame
            if (_frameCount > 0)
                RequestDecode(0);
        }

        /// <summary>
        /// Stop playback, shut down decode thread, and release all resources.
        /// </summary>
        void Close()
        {
            _playing = false;

            // Signal decode thread to exit and wake it up
            _decodeRunning = false;
            lock (_decodeLock)
                Monitor.Pulse(_decodeLock);

            // Wait for the decode thread to finish its current frame and exit cleanly before
            // freeing any resources it may be using. No timeout is used because proceeding
            // early risks a use-after-free inside hap_decode_frame on the native side.
            //
            // Trade-off: if the underlying file I/O stalls (e.g. a stuck network mount or
            // an OS-suspended disk during application quit), this call can hang indefinitely.
            // In practice hap_decode_frame is bounded by the OS's own I/O timeout, but be
            // aware of this when closing players that are reading from unreliable storage.
            if (_decodeThread != null)
            {
                _decodeExited.Wait();
                _decodeThread = null;
            }

            // Dispose managed resources
            _uploader?.Dispose();
            _uploader = null;

            _ringBuffer?.Dispose();
            _ringBuffer = null;

            if (_outputRT != null)
            {
                _outputRT.Release();
                UnityEngine.Object.Destroy(_outputRT);
                _outputRT = null;
            }
            if (_outputMat != null)
            {
                UnityEngine.Object.Destroy(_outputMat);
                _outputMat = null;
            }

            // Close native handle
            if (_handle != IntPtr.Zero)
            {
                HapNative.hap_close(_handle);
                _handle = IntPtr.Zero;
            }

            // Reset state
            _frameCount = 0;
            _frameRate = 0;
            _duration = 0;
            _lastUploadedFrame = -1;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Frame timing
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Convert a time in seconds to a frame index.</summary>
        int ClockToFrame(float clock)
        {
            if (_frameCount <= 0) return 0;
            int frame = Mathf.FloorToInt(clock * _frameRate);
            return Mathf.Clamp(frame, 0, _frameCount - 1);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Decode thread communication
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Tell the decode thread which frame we want. Called from main thread.
        /// Only pulses the decode thread if the target actually changed.
        /// </summary>
        void RequestDecode(int frame)
        {
            lock (_decodeLock)
            {
                if (_decodeTargetFrame == frame) return;
                _decodeTargetFrame = frame;
                Monitor.Pulse(_decodeLock);  // Wake up decode thread
            }
        }

        /// <summary>
        /// Background thread: waits for frame requests, decodes them, and commits to ring buffer.
        ///
        /// Two separate counters prevent a re-decode spin that would otherwise occur when
        /// look-ahead is added:
        ///   lastExplicit — the last frame index the main thread requested that we satisfied
        ///                  (either by decoding it, or by finding it already in the buffer).
        ///   lastDecoded  — the most recently written ring-buffer slot (may be a pre-fetch).
        ///
        /// Flow each iteration:
        ///   1. If _decodeTargetFrame != lastExplicit → main thread wants a new frame.
        ///        If it's already in the buffer (== lastDecoded from a prior pre-fetch),
        ///        just update lastExplicit and skip decoding; otherwise decode it.
        ///   2. Else if no pre-fetch done yet → pre-fetch the next sequential frame so it
        ///        is ready before the main thread asks for it (eliminates per-frame I/O stall).
        ///   3. Else → block until main thread requests a new frame.
        ///
        /// This keeps the pipeline full during linear playback and still responds immediately
        /// to seeks/scrubbing without re-decoding the same frame in a tight loop.
        /// </summary>
        void DecodeLoop()
        {
            // Capture fields that must remain valid for the lifetime of this thread.
            // Close() waits for us to exit before freeing these, but capturing locals makes
            // the lifetime dependency explicit and safe against future refactors.
            IntPtr handle = _handle;
            int frameBufferSize = _frameBufferSize;
            int frameCount = _frameCount;
            HapFrameRingBuffer ringBuffer = _ringBuffer;

            try
            {
                int lastExplicit = -1;   // last frame satisfied in response to a main-thread request
                int lastDecoded  = -1;   // most recent frame written to the ring buffer; -1 = nothing yet.
                                         //   The pre-fetch guard (lastDecoded >= 0) depends on this sentinel.
                bool prefetchDone = false;

                while (_decodeRunning)
                {
                    int target;
                    bool isPrefetch;

                    // Snapshot direction once per iteration (volatile, no lock needed).
                    // dir = +1 for forward playback, -1 for reverse.
                    int dir = _decodeDirection;

                    lock (_decodeLock)
                    {
                        int requested = _decodeTargetFrame;

                        if (requested != lastExplicit)
                        {
                            if (requested == lastDecoded)
                            {
                                // Already in the ring buffer from a pre-fetch — no decode needed.
                                // Apply the same sequential check as the explicit-decode path: only
                                // enable pre-fetch if this request is the natural next frame from the
                                // previous one. A seek that happens to land on the pre-fetched slot
                                // should not blindly pre-fetch the frame after it.
                                bool wasSeq = lastExplicit < 0 ||
                                              requested == (lastExplicit + dir + frameCount) % frameCount;
                                lastExplicit = requested;
                                prefetchDone = !wasSeq;
                                // target == -1 is the sentinel meaning "already buffered, skip decode"
                                target = -1;
                                isPrefetch = false;
                            }
                            else
                            {
                                // Decode this frame in response to a main-thread request.
                                target = requested;
                                isPrefetch = false;
                            }
                        }
                        else if (!prefetchDone && lastDecoded >= 0 && frameCount > 1)
                        {
                            // Caught up with the main thread. Pre-fetch the next sequential
                            // frame (in the current direction) so it's ready before it's requested.
                            target = (lastDecoded + dir + frameCount) % frameCount;
                            isPrefetch = true;
                        }
                        else
                        {
                            // Pre-fetch done (or not applicable). Block until the main thread
                            // requests a new frame or signals exit.
                            while (_decodeRunning && _decodeTargetFrame == lastExplicit)
                                Monitor.Wait(_decodeLock, 100);  // 100ms safety timeout in case Pulse is missed during a Close() race

                            if (!_decodeRunning) break;
                            target = _decodeTargetFrame;
                            isPrefetch = false;
                            prefetchDone = false;
                        }
                    }

                    // Frame was already in the ring buffer — no decode work to do.
                    if (target == -1) continue;
                    if (ringBuffer == null) break;

                    // Decode into the ring buffer's write slot.
                    // Split into two timed steps so the profiler shows I/O vs CPU separately:
                    //   ReadSample  — memcpy from memory-mapped file (page-fault / disk latency)
                    //   Decompress  — Snappy decompression via thread pool (pure CPU)
                    IntPtr buf = ringBuffer.WritePtr;
                    // readBytes > 0 means the native side stored the sample; native side
                    // tracks the byte count internally so we don't pass it to Decompress.
                    int readBytes;
                    using (s_ReadSampleMarker.Auto())
                        readBytes = HapNative.hap_read_sample(handle, target);

                    int result;
                    if (readBytes <= 0)
                    {
                        result = HapNative.ErrorFile;
                    }
                    else
                    {
                        using (s_DecompressMarker.Auto())
                            result = HapNative.hap_decompress_frame(handle, buf, frameBufferSize);
                    }

                    if (result != HapNative.ErrorNone)
                    {
                        Debug.LogWarning($"[HapPlayer] Failed to decode frame {target}, error: {result}");
                    }
                    else
                    {
                        ringBuffer.CommitWrite(target);
                        lastDecoded = target;

                        if (isPrefetch)
                        {
                            prefetchDone = true;
                        }
                        else
                        {
                            // Only pre-fetch if this was a sequential step in the current
                            // direction. After a seek/scrub the next request is unpredictable,
                            // so skip the pre-fetch — don't waste a decode slot on the wrong frame.
                            // dir handles both directions: +1 for forward, -1 for reverse.
                            bool wasSequential = lastExplicit < 0 ||
                                                 target == (lastExplicit + dir + frameCount) % frameCount;
                            lastExplicit = target;
                            prefetchDone = !wasSequential;
                        }
                    }
                }
            }
            finally
            {
                // Signal that we've exited so Close() can proceed safely
                _decodeExited.Set();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // GPU upload
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Upload the latest decoded frame from the ring buffer to the GPU texture.
        /// Called from main thread in Update().
        /// </summary>
        void UploadFrame()
        {
            var ringBuffer = _ringBuffer;
            if (ringBuffer == null) return;

            // Skip GPU uploads in the same frame that Open() ran. D3D12 requires at least one
            // command-list flush between RenderTexture.Create() and the first blit that targets it.
            if (UnityEngine.Time.frameCount == _openedFrame) return;

            // Snapshot both frame index and pointer from the same _readIndex capture.
            // Separate ReadFrame / ReadPtr calls would each re-read _readIndex, which the
            // decode thread can change between them, producing a mismatched frame/pointer pair.
            if (!ringBuffer.TryRead(out int readFrame, out IntPtr ptr)) return;
            if (readFrame == _lastUploadedFrame) return;

            // Upload the raw DXT/BC7 data to the texture
            _uploader.Upload(ptr, _frameBufferSize);

            // Blit through the output shader (flip orientation and/or YCoCg decode)
            if (_outputRT != null && _outputMat != null)
                Graphics.Blit(_uploader.Texture, _outputRT, _outputMat);

            _lastUploadedFrame = readFrame;
        }
    }
}
