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
    /// - A background thread (HapOpen) calls hap_open() and reads file metadata, then signals
    ///   the main thread to create GPU resources and start the decode thread. This keeps the
    ///   expensive file-map + frame-offset pre-cache + first-frame probe off the main thread.
    /// - A background thread (HapDecode) reads compressed frames from disk and decompresses
    ///   them into GPU-ready DXT/BC7 texture data using a native C plugin.
    /// - The main thread uploads the decompressed data to a Texture2D each frame.
    /// - A ring buffer passes decoded frames from the background thread to the main thread
    ///   without allocations or locks during steady-state playback.
    /// - On disable, GPU resources are disposed immediately (they don't depend on the native
    ///   handle), and a short-lived background thread (HapClose) waits for the decode thread
    ///   to exit before freeing the ring buffer and calling hap_close(). This prevents the
    ///   decode-thread wait from blocking the main thread.
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

        /// <summary>Owns the output RT pair, uploader ring, and blit material.</summary>
        HapOutputPipeline _outputPipeline;

        // ─────────────────────────────────────────────────────────────────────
        // Video metadata (populated on CompleteOpen)
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

        /// <summary>
        /// Unity frame count when the video was last opened. We skip GPU uploads in the same frame
        /// as CompleteOpen() because D3D12 requires at least one command-list flush between
        /// RenderTexture.Create() and the first Graphics.Blit that targets it.
        /// </summary>
        int _openedFrame = -1;

        // ─────────────────────────────────────────────────────────────────────
        // Async open state
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Background thread that calls hap_open() and reads file metadata.
        /// Set in BeginOpen(), cleared on the main thread in Update() after the result is consumed.
        /// Only touched on the main thread except during its own execution.
        /// </summary>
        Thread _openThread;

        /// <summary>
        /// Set to true by the main thread to tell the open background thread to discard its result.
        /// The thread checks this after hap_open() returns; if set, it closes the handle and exits.
        /// </summary>
        volatile bool _openCancelled;

        /// <summary>
        /// Written by the open background thread when hap_open() and metadata reads are complete.
        /// The main thread polls this in Update() and calls CompleteOpen() when non-null.
        /// Null handle signals failure; non-null signals success.
        /// </summary>
        volatile OpenResult _openResult;

        /// <summary>
        /// True if Play() was called (or playOnEnable was set) while an open was in progress.
        /// Consumed by CompleteOpen() on the main thread.
        /// </summary>
        bool _pendingPlay;

        // ─────────────────────────────────────────────────────────────────────
        // Deferred close state
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Background thread that waits for the decode thread to exit, then frees the ring buffer
        /// and calls hap_close(). Avoids blocking the main thread on decode-thread teardown.
        /// Joined in BeginOpen() (before starting a new open) and in OnDestroy().
        /// </summary>
        Thread _closeThread;

        // ─────────────────────────────────────────────────────────────────────
        // Background decode thread coordination
        // ─────────────────────────────────────────────────────────────────────

        Thread _decodeThread;

        /// <summary>Set to false to signal the decode thread to exit.</summary>
        volatile bool _decodeRunning;

        /// <summary>
        /// Set to false just before hap_close() is called so the decode thread
        /// can detect a closing handle and abort rather than crash into freed memory.
        /// Written by the close background thread after _decodeExited signals; read by the
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
        readonly object _decodeLock = new();

        /// <summary>Signaled when the decode thread exits, so the close background thread can proceed.</summary>
        readonly ManualResetEventSlim _decodeExited = new(true);

        // ─────────────────────────────────────────────────────────────────────
        // Rendering helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Reusable MaterialPropertyBlock to avoid material instancing.</summary>
        MaterialPropertyBlock _mpb;

        /// <summary>Cached shader property ID for _MainTex.</summary>
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        // ─────────────────────────────────────────────────────────────────────
        // Profiler markers (visible in Unity Profiler)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Speed values whose absolute value is below this are treated as zero (paused).</summary>
        const float k_PlaybackSpeedEpsilon = 1e-5f;

        static readonly ProfilerMarker s_UpdateMarker     = new("HapPlayer.Update");
        static readonly ProfilerMarker s_UploadMarker     = new("HapPlayer.UploadFrame");
        static readonly ProfilerMarker s_RenderMarker     = new("HapPlayer.Render");
        static readonly ProfilerMarker s_ReadSampleMarker = new("HapPlayer.ReadSample"); // I/O / page-fault time
        static readonly ProfilerMarker s_DecompressMarker = new("HapPlayer.Decompress");  // Snappy CPU time

        // ─────────────────────────────────────────────────────────────────────
        // Events
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Raised once on the main thread when the video finishes opening and is ready to play.
        /// Not raised if opening fails or is cancelled.
        /// </summary>
        public event Action Opened;

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
        public Texture Texture
        {
            get
            {
                if (_outputPipeline == null) return null;
                Texture t = _outputPipeline.DisplayTexture;
                return t != null ? t : _outputPipeline.RawTexture;
            }
        }

        public bool IsPlaying => _playing && Mathf.Abs(playbackSpeed) > k_PlaybackSpeedEpsilon;
        public bool IsOpen    => _handle != IntPtr.Zero;

        /// <summary>True while the background open thread is running (hap_open not yet complete).</summary>
        public bool IsOpening => _openThread != null;

        public int FrameCount  => _frameCount;
        public float Duration  => _duration;
        public float FrameRate => _frameRate;
        public int Width       => _width;
        public int Height      => _height;
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
            _pendingPlay = playOnEnable;
            BeginOpen();
        }

        void OnDisable()
        {
            _openCancelled = true;
            Close();
        }

        void OnDestroy()
        {
            _openCancelled = true;
            Close();
            // Block here — OnDestroy can afford to wait, and we must not dispose
            // _decodeExited while the close thread is still waiting on it.
            _openThread?.Join();
            _openThread = null;
            // Drain any result the open thread left behind so the handle isn't leaked.
            var leftover = _openResult;
            _openResult = null;
            if (leftover != null && leftover.Handle != IntPtr.Zero)
                HapNative.hap_close(leftover.Handle);
            _closeThread?.Join();
            _decodeExited.Dispose();
        }

        /// <summary>
        /// Main update loop: consume pending open result, advance clock, request decode,
        /// upload frame, render.
        /// </summary>
        void Update()
        {
            // Consume async open result produced by the background open thread.
            if (_openResult is { } result)
            {
                _openResult = null;
                _openThread = null;
                if (!_openCancelled && result.Handle != IntPtr.Zero)
                    CompleteOpen(result);
                else
                {
                    _pendingPlay = false;
                    if (result.Handle != IntPtr.Zero)
                        HapNative.hap_close(result.Handle);
                }
            }

            if (!IsOpen) return;

            using (s_UpdateMarker.Auto())
                UpdatePlayback();
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
                uploadedNewFrame = UploadFrame();

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
                        _mpb ??= new MaterialPropertyBlock();
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
                    break;
            }

            if (uploadedNewFrame)
                _outputPipeline?.SwapBuffers();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public playback control
        // ─────────────────────────────────────────────────────────────────────

        public void Play()
        {
            if (_openThread != null)
            {
                // Open in progress — defer play until CompleteOpen() runs.
                _pendingPlay = true;
                return;
            }
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
            _pendingPlay = false;
            _playing = false;
        }

        /// <summary>Stop playback and reset to frame 0, regardless of playback direction.</summary>
        public void Stop()
        {
            _pendingPlay = false;
            _playing = false;
            _clock = 0f;
            if (IsOpen && _frameCount > 0)
                RequestDecode(0);
        }

        /// <summary>Close current file (if any) and open a new one.</summary>
        public void Open(string path)
        {
            _openCancelled = true;
            Close();
            filePath = path;
            BeginOpen();
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
        /// Start the background open thread. The thread calls hap_open(), reads file metadata,
        /// and stores an OpenResult. Update() picks it up on the main thread and calls CompleteOpen().
        /// </summary>
        void BeginOpen()
        {
            if (string.IsNullOrEmpty(filePath)) return;

            // If an open is already in progress (rapid enable/disable/enable, or Open(path) called
            // mid-open), wait for it to finish and discard any result before starting a fresh open.
            if (_openThread != null)
            {
                _openThread.Join();
                _openThread = null;
                var stale = _openResult;
                _openResult = null;
                if (stale != null && stale.Handle != IntPtr.Zero)
                    HapNative.hap_close(stale.Handle);
            }

            if (IsOpen) return;

            // Ensure the previous deferred close has fully finished before reusing _decodeExited
            // and starting a new decode thread. In practice the close thread exits within a frame
            // since the decode thread responds immediately to _decodeRunning = false.
            _closeThread?.Join();
            _closeThread = null;

            string resolved = ResolvePath(filePath);
            _openCancelled = false;
            _openResult = null;

            _openThread = new Thread(() => OpenBackground(resolved))
            {
                IsBackground = true,
                Name = "HapOpen"
            };
            _openThread.Start();
        }

        /// <summary>
        /// Background thread body: calls hap_open() (expensive: file mapping, frame-offset
        /// pre-cache, first-frame decode probe) then reads cheap metadata. Stores the result
        /// for the main thread to consume in Update().
        /// </summary>
        void OpenBackground(string resolved)
        {
            int err;
            IntPtr handle = HapNative.hap_open(resolved, out err);

            if (handle == IntPtr.Zero)
            {
                if (!_openCancelled)
                    Debug.LogError($"[HapPlayer] Failed to open '{resolved}', error: {err}");
                _openResult = OpenResult.Failed;
                return;
            }

            int frameCount = HapNative.hap_get_frame_count(handle);
            float frameRate = HapNative.hap_get_frame_rate(handle);

            if (frameRate <= 0f || frameCount <= 0)
            {
                if (!_openCancelled)
                    Debug.LogError($"[HapPlayer] Invalid video ({frameCount} frames, {frameRate} fps) in '{resolved}'");
                HapNative.hap_close(handle);
                _openResult = OpenResult.Failed;
                return;
            }

            _openResult = new OpenResult(
                handle,
                frameCount,
                frameRate,
                HapNative.hap_get_frame_buffer_size(handle),
                HapNative.hap_get_width(handle),
                HapNative.hap_get_height(handle),
                HapFormatExtensions.ToHapFormat(HapNative.hap_get_texture_format(handle))
            );
        }

        /// <summary>
        /// Called on the main thread from Update() once the background open thread completes.
        /// Creates GPU resources, starts the decode thread, and triggers playback if requested.
        /// </summary>
        void CompleteOpen(OpenResult result)
        {
            _handle          = result.Handle;
            _frameCount      = result.FrameCount;
            _frameRate       = result.FrameRate;
            _frameBufferSize = result.FrameBufferSize;
            _width           = result.Width;
            _height          = result.Height;
            _duration        = _frameCount / _frameRate;

            _ringBuffer = new HapFrameRingBuffer(_frameBufferSize);

            int uploaderCount = Mathf.Max(2, QualitySettings.maxQueuedFrames + 1);
            _outputPipeline = new HapOutputPipeline(_width, _height, result.Format, uploaderCount);

            _clock = 0f;
            _openedFrame = UnityEngine.Time.frameCount;

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
                Debug.LogError($"[HapPlayer] Failed to start decode thread: {ex.Message}");
                _decodeThread = null;
                _decodeRunning = false;
                _handleValid = false;
                _decodeExited.Set();
                HapNative.hap_close(_handle);
                _handle = IntPtr.Zero;
                return;
            }

            if (_frameCount > 0)
                RequestDecode(0);

            Opened?.Invoke();

            if (_pendingPlay)
            {
                _pendingPlay = false;
                Play();
            }
        }

        /// <summary>
        /// Immediately clears all instance state and disposes GPU resources (which are safe to
        /// release without waiting for the decode thread). Starts a background thread (HapClose)
        /// that waits for the decode thread to exit, then frees the ring buffer and calls
        /// hap_close() — avoiding any main-thread stall on decode-thread teardown.
        /// </summary>
        void Close()
        {
            _playing = false;

            if (!IsOpen && _decodeThread == null)
            {
                _frameCount = 0;
                _frameRate  = 0;
                _duration   = 0;
                return;
            }

            // Signal decode thread to exit and wake it up.
            _decodeRunning = false;
            lock (_decodeLock)
                Monitor.Pulse(_decodeLock);

            // Capture resources before clearing instance state, so the close background
            // thread holds valid references while the main thread sees a clean slate.
            var pipeline    = _outputPipeline;
            var ringBuffer  = _ringBuffer;
            var handle      = _handle;
            var decodeThread = _decodeThread;

            _outputPipeline = null;
            _ringBuffer     = null;
            _handle         = IntPtr.Zero;
            _handleValid    = false;
            _decodeThread   = null;
            _frameCount     = 0;
            _frameRate      = 0;
            _duration       = 0;

            // GPU resources are written exclusively by the main thread and are independent
            // of the native handle — dispose them now while still on the main thread.
            pipeline?.Dispose();

            // Ring buffer and native handle must outlive the decode thread. Wait for it
            // off the main thread to avoid blocking the frame.
            _closeThread = new Thread(() =>
            {
                if (decodeThread != null)
                    _decodeExited.Wait();

                ringBuffer?.Dispose();

                // Mark invalid before hap_close so a lingering decode thread (extremely
                // unlikely given the Wait above) can detect the handle is gone.
                _handleValid = false;
                if (handle != IntPtr.Zero)
                    HapNative.hap_close(handle);
            })
            {
                IsBackground = true,
                Name = "HapClose"
            };
            _closeThread.Start();
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
        /// Scheduling logic (explicit vs. prefetch, when to block) is delegated to
        /// <see cref="DecodeScheduler"/>.
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
                // Signal that we've exited so the close background thread can proceed safely.
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
            if (ringBuffer == null || _outputPipeline == null) return false;

            // Skip GPU uploads in the same frame that CompleteOpen() ran. D3D12 requires at least
            // one command-list flush between RenderTexture.Create() and the first blit that targets it.
            if (UnityEngine.Time.frameCount == _openedFrame) return false;

            if (!ringBuffer.TryAcquire(out var lease)) return false;
            using (lease)
                return _outputPipeline.Upload(lease.Data, lease.FrameIndex);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Open result (transferred from open background thread to main thread)
        // ─────────────────────────────────────────────────────────────────────

        sealed class OpenResult
        {
            public static readonly OpenResult Failed = new(IntPtr.Zero, 0, 0f, 0, 0, 0, default);

            public readonly IntPtr Handle;
            public readonly int FrameCount;
            public readonly float FrameRate;
            public readonly int FrameBufferSize;
            public readonly int Width;
            public readonly int Height;
            public readonly HapFormat Format;

            public OpenResult(IntPtr handle, int frameCount, float frameRate,
                              int frameBufferSize, int width, int height, HapFormat format)
            {
                Handle          = handle;
                FrameCount      = frameCount;
                FrameRate       = frameRate;
                FrameBufferSize = frameBufferSize;
                Width           = width;
                Height          = height;
                Format          = format;
            }
        }
    }
}
