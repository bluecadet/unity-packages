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

        // ─────────────────────────────────────────────────────────────────────
        // Background decode thread coordination
        // ─────────────────────────────────────────────────────────────────────

        Thread _decodeThread;

        /// <summary>Set to false to signal the decode thread to exit.</summary>
        volatile bool _decodeRunning;

        /// <summary>The frame index the main thread wants decoded next. The decode thread watches this.</summary>
        volatile int _decodeTargetFrame = -1;

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
        // Profiler markers (visible in Unity Profiler)
        // ─────────────────────────────────────────────────────────────────────

        static readonly ProfilerMarker s_UpdateMarker = new ProfilerMarker("HapPlayer.Update");
        static readonly ProfilerMarker s_UploadMarker = new ProfilerMarker("HapPlayer.UploadFrame");
        static readonly ProfilerMarker s_RenderMarker = new ProfilerMarker("HapPlayer.Render");
        static readonly ProfilerMarker s_DecodeMarker = new ProfilerMarker("HapPlayer.DecodeFrame");

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

        /// <summary>The current decoded frame as a Texture2D. Null if not open.</summary>
        public Texture2D Texture => _uploader?.Texture;

        public bool IsPlaying => _playing;
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

        /// <summary>Playback speed multiplier. Clamped to >= 0 (no reverse playback).</summary>
        public float PlaybackSpeed
        {
            get => playbackSpeed;
            set => playbackSpeed = Mathf.Max(0f, value);
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
            // Advance playback clock if playing
            if (_playing)
            {
                float dt = timeSource == HapTimeSource.UnscaledGameTime
                    ? UnityEngine.Time.unscaledDeltaTime
                    : UnityEngine.Time.deltaTime;
                _clock += dt * playbackSpeed;

                // Handle end of video
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

                // Tell the decode thread which frame we need
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
                    if (targetRenderer != null && _uploader?.Texture != null)
                    {
                        if (_mpb == null) _mpb = new MaterialPropertyBlock();
                        targetRenderer.GetPropertyBlock(_mpb);
                        _mpb.SetTexture(MainTexId, _uploader.Texture);
                        targetRenderer.SetPropertyBlock(_mpb);
                    }
                    break;
                case HapRenderMode.RenderTexture:
                    if (targetRenderTexture != null && _uploader?.Texture != null)
                        Graphics.Blit(_uploader.Texture, targetRenderTexture);
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
            _playing = true;
        }

        public void Pause()
        {
            _playing = false;
        }

        /// <summary>Stop playback and reset to the beginning.</summary>
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
            Close();
            filePath = path;
            Open();
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

            _clock = 0f;
            _lastUploadedFrame = -1;

            // Start background decode thread
            _decodeExited.Reset();
            _decodeRunning = true;
            _decodeThread = new Thread(DecodeLoop)
            {
                IsBackground = true,
                Name = "HapDecode"
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

            // Wait for decode thread to exit before disposing shared resources
            if (_decodeThread != null)
            {
                _decodeExited.Wait(2000);
                _decodeThread = null;
            }

            // Dispose managed resources
            _uploader?.Dispose();
            _uploader = null;

            _ringBuffer?.Dispose();
            _ringBuffer = null;

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
        /// </summary>
        void RequestDecode(int frame)
        {
            lock (_decodeLock)
            {
                _decodeTargetFrame = frame;
                Monitor.Pulse(_decodeLock);  // Wake up decode thread
            }
        }

        /// <summary>
        /// Background thread: waits for frame requests, decodes them, and commits to ring buffer.
        ///
        /// Flow:
        /// 1. Wait for _decodeTargetFrame to change (or exit signal)
        /// 2. Decode the requested frame into the ring buffer's write slot
        /// 3. If the target changed during decode (fast scrubbing), discard and re-decode latest
        /// 4. Commit the decoded frame to make it available to the main thread
        /// </summary>
        void DecodeLoop()
        {
            try
            {
                int lastDecoded = -1;

                while (_decodeRunning)
                {
                    int target;

                    // Wait for a new frame request
                    lock (_decodeLock)
                    {
                        while (_decodeRunning && _decodeTargetFrame == lastDecoded)
                            Monitor.Wait(_decodeLock, 100);

                        if (!_decodeRunning) break;
                        target = _decodeTargetFrame;
                    }

                    if (target == lastDecoded) continue;

                    // Get ring buffer (may be null if Close() was called)
                    var ringBuffer = _ringBuffer;
                    if (ringBuffer == null) break;

                    // Decode into the ring buffer's write slot
                    IntPtr buf = ringBuffer.WritePtr;
                    int result;
                    using (s_DecodeMarker.Auto())
                    {
                        result = HapNative.hap_decode_frame(_handle, target, buf, _frameBufferSize);
                    }

                    if (result == HapNative.ErrorNone)
                    {
                        // Fast scrub check: if target changed during decode, discard this frame
                        // and loop again to decode the latest requested frame
                        if (target != _decodeTargetFrame)
                            continue;

                        // Commit the frame to make it available for upload
                        ringBuffer.CommitWrite(target);
                        lastDecoded = target;
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

            // Check if there's a new frame to upload
            int readFrame = ringBuffer.ReadFrame;
            if (readFrame < 0 || readFrame == _lastUploadedFrame) return;

            IntPtr ptr = ringBuffer.ReadPtr;
            if (ptr == IntPtr.Zero) return;

            // Upload the raw DXT/BC7 data to the texture
            _uploader.Upload(ptr, _frameBufferSize);
            _lastUploadedFrame = readFrame;
        }
    }
}
