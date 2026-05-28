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

        /// <summary>Ring buffer passing decoded frames from the decode thread to the main thread.</summary>
        HapFrameRingBuffer _ringBuffer;

        /// <summary>One uploader per frame-in-flight slot; cycled by frame count to prevent CPU/GPU races.</summary>
        HapTextureUploader[] _uploaders;

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

        /// <summary>
        /// Set to false just before hap_close() is called so the decode thread
        /// can detect a closing handle and abort rather than crash into freed memory.
        /// Written by the main thread after _decodeExited signals; read by the
        /// decode thread as a belt-and-suspenders guard.
        /// </summary>
        volatile bool _handleValid;

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
        /// Double-buffered output RenderTextures. The blit writes to slot [writeIndex]
        /// while the scene reads slot [_displayIndex] (written last frame). writeIndex is
        /// always (_displayIndex+1)%2, guaranteeing the two slots are different resources
        /// every frame — even when video frames are skipped — preventing the D3D12
        /// within-frame write→read hazard on the same RT.
        /// </summary>
        RenderTexture[] _outputRTs;
        int _displayIndex;

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
        public Texture Texture => _outputRTs != null
            ? (Texture)_outputRTs[_displayIndex]
            : (Texture)_uploaders?[UnityEngine.Time.frameCount % _uploaders.Length]?.Texture;

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

            // Upload the latest decoded frame to the back buffer (does NOT swap yet).
            bool uploadedNewFrame;
            using (s_UploadMarker.Auto())
            {
                uploadedNewFrame = UploadFrame();
            }

            // Apply the texture to the output target based on render mode.
            // Reads from the FRONT buffer, which was written last frame — this
            // guarantees a full command-list submission separates the Blit write
            // from the scene's read, eliminating the D3D12 read/write hazard.
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

            // Advance the display index to the slot just written, AFTER the scene has
            // rendered from the previous slot. Using (_displayIndex+1)%N — not frameCount%N —
            // guarantees the write slot and display slot are always different resources,
            // even when video frames are skipped (e.g. 24fps video at 60fps Unity).
            if (uploadedNewFrame && _outputRTs != null)
                _displayIndex = (_displayIndex + 1) % _outputRTs.Length;
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
            // Guard against double-open (e.g. OnEnable called unexpectedly while already playing).
            // Without this, a second call would overwrite _handle and start a second decode thread
            // while the first one keeps running, leaking both the old handle and thread.
            if (IsOpen)
            {
                Debug.LogWarning($"[HapPlayer] Open() called while already open ('{filePath}'). Call Close() first.");
                return;
            }

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
            var format = HapFormatExtensions.ToHapFormat(HapNative.hap_get_texture_format(_handle));

            // Create ring buffer for passing decoded frames from decode thread to main thread
            _ringBuffer = new HapFrameRingBuffer(_frameBufferSize);

            // Set up the output blit pipeline.
            // All formats need a V-flip to correct Unity's raw DXT orientation
            // (HAP stores top-to-bottom; LoadRawTextureData treats it as bottom-to-top).
            // HAP Q additionally needs YCoCg→RGB color space conversion.
            string shaderName = format.ShaderName();
            var outputShader = Resources.Load<Shader>(shaderName);
            if (outputShader == null)
            {
                Debug.LogError($"[HapPlayer] Output shader '{shaderName}' not found — video will be unflipped");
            }
            else
            {
                _outputMat = new Material(outputShader) { hideFlags = HideFlags.HideAndDontSave };
                _outputRTs = new RenderTexture[2];
                _displayIndex = 0;
                for (int i = 0; i < 2; i++)
                {
                    _outputRTs[i] = new RenderTexture(_width, _height, 0, RenderTextureFormat.ARGB32)
                    {
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    _outputRTs[i].Create();
                }

                // Uploaders need one slot per frame-in-flight so a CPU Apply() and a GPU
                // blit never target the same Texture2D. Output RTs only need 2: the GPU
                // graphics queue is sequential, so the write slot and display slot are
                // always from different (already-completed) submissions.
                int uploaderCount = Mathf.Max(2, QualitySettings.maxQueuedFrames + 1);
                _uploaders = new HapTextureUploader[uploaderCount];
                for (int i = 0; i < uploaderCount; i++)
                    _uploaders[i] = new HapTextureUploader(_width, _height, format);

                // Initialize both RT slots to opaque black so D3D12 never reads
                // uninitialized memory before the first video frame arrives.
                for (int i = 0; i < 2; i++)
                    Graphics.Blit(Texture2D.blackTexture, _outputRTs[i]);
            }

            _clock = 0f;
            _lastUploadedFrame = -1;
            _openedFrame = UnityEngine.Time.frameCount;

            // Start background decode thread
            _handleValid = true;
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
            try
            {
                _decodeThread.Start();
            }
            catch (Exception ex)
            {
                // Thread creation failed (OS resource exhaustion or invalid state).
                // Reset all decode state so Close() doesn't hang indefinitely waiting
                // on _decodeExited, which the thread would never Set.
                Debug.LogError($"[HapPlayer] Failed to start decode thread: {ex.Message}");
                _decodeThread = null;
                _decodeRunning = false;
                _handleValid = false;
                _decodeExited.Set();
                HapNative.hap_close(_handle);
                _handle = IntPtr.Zero;
                return;
            }

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
            if (_uploaders != null)
            {
                foreach (var u in _uploaders) u?.Dispose();
                _uploaders = null;
            }

            _ringBuffer?.Dispose();
            _ringBuffer = null;

            if (_outputRTs != null)
            {
                foreach (var rt in _outputRTs)
                {
                    if (rt != null)
                    {
                        rt.Release();
                        UnityEngine.Object.Destroy(rt);
                    }
                }
                _outputRTs = null;
            }
            if (_outputMat != null)
            {
                UnityEngine.Object.Destroy(_outputMat);
                _outputMat = null;
            }

            // Close native handle.  Mark invalid before the call so the decode
            // thread (if it somehow missed _decodeRunning = false) can detect
            // the handle is gone and abort rather than crash into freed memory.
            _handleValid = false;
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
                var scheduler = new DecodeScheduler(frameCount);
                int consecutiveErrors = 0;

                while (_decodeRunning)
                {
                    int target;
                    bool isPrefetch;
                    int dir;

                    lock (_decodeLock)
                    {
                        // Read direction inside the lock so it is always consistent with _decodeTargetFrame.
                        dir = _decodeDirection;
                        var (t, pf, block) = scheduler.Next(_decodeTargetFrame, dir);

                        if (block)
                        {
                            // Block until the main thread requests a new frame or signals exit.
                            // 100ms safety timeout guards against Pulse being missed during a Close() race.
                            while (_decodeRunning && _decodeTargetFrame == scheduler.LastExplicit)
                                Monitor.Wait(_decodeLock, 100);

                            if (!_decodeRunning) break;
                            dir = _decodeDirection;
                            (t, pf, _) = scheduler.Next(_decodeTargetFrame, dir);
                        }

                        target = t;
                        isPrefetch = pf;
                    }

                    // Frame was already in the ring buffer — no decode work to do.
                    if (target == -1) continue;
                    if (ringBuffer == null) break;

                    // Belt-and-suspenders: if the handle was closed between our last
                    // _decodeRunning check and here, abort rather than crash into freed
                    // native memory.  The native null-checks in hap_read_sample provide
                    // a second layer of defence, but this avoids the P/Invoke entirely.
                    if (!_handleValid) break;

                    // Decode into the ring buffer's write slot.
                    // Split into two timed steps so the profiler shows I/O vs CPU separately:
                    //   ReadSample  — memcpy from memory-mapped file (page-fault / disk latency)
                    //   Decompress  — Snappy decompression (pure CPU)
                    IntPtr buf = ringBuffer.GetWritePtr();
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
                        consecutiveErrors++;
                        Debug.LogWarning($"[HapPlayer] Failed to decode frame {target}, error: {result} ({consecutiveErrors} consecutive)");
                        if (consecutiveErrors >= 10)
                        {
                            Debug.LogError($"[HapPlayer] Decode loop aborting after {consecutiveErrors} consecutive errors on '{filePath}'");
                            break;
                        }
                    }
                    else
                    {
                        consecutiveErrors = 0;
                        ringBuffer.CommitWrite(target);

                        // Asynchronously warm the OS page cache for the next-in-sequence frame.
                        // Warming after both explicit and pre-fetch decodes keeps the pipeline
                        // two frames ahead rather than one. No-op on non-Windows platforms.
                        if (frameCount > 1)
                        {
                            int nextFrame = (target + dir + frameCount) % frameCount;
                            HapNative.hap_prefetch_frame(handle, nextFrame);
                        }

                        scheduler.OnDecoded(target, isPrefetch, dir);
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
        /// Upload the latest decoded frame from the ring buffer to the GPU back buffer.
        /// Returns true if a new frame was blitted (caller must then swap front/back after rendering).
        /// Called from main thread in Update().
        /// </summary>
        bool UploadFrame()
        {
            var ringBuffer = _ringBuffer;
            if (ringBuffer == null) return false;

            // Skip GPU uploads in the same frame that Open() ran. D3D12 requires at least one
            // command-list flush between RenderTexture.Create() and the first blit that targets it.
            if (UnityEngine.Time.frameCount == _openedFrame) return false;

            if (!ringBuffer.TryAcquire(out var lease)) return false;
            using (lease)
            {
                if (lease.FrameIndex == _lastUploadedFrame) return false;

                var uploader = _uploaders[UnityEngine.Time.frameCount % _uploaders.Length];
                uploader.Upload(lease.Data);

                if (_outputRTs != null && _outputMat != null)
                {
                    int writeIndex = (_displayIndex + 1) % _outputRTs.Length;
                    Graphics.Blit(uploader.Texture, _outputRTs[writeIndex], _outputMat);
                }

                _lastUploadedFrame = lease.FrameIndex;
                return _outputRTs != null && _outputMat != null;
            }
        }
    }
}
