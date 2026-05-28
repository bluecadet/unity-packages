using System.IO;
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
    /// Delegates file I/O, native handle lifetime, and background thread coordination to
    /// <see cref="HapFileSession"/>. Owns the output GPU pipeline and playback clock.
    /// </summary>
    public class HapPlayer : MonoBehaviour
    {
        // ── Serialized fields ────────────────────────────────────────────────

        [SerializeField] string filePath;
        [SerializeField] bool playOnEnable = true;
        [SerializeField] bool loop = true;
        [SerializeField] Renderer targetRenderer;
        [SerializeField] HapRenderMode renderMode = HapRenderMode.MaterialOverride;
        [SerializeField] HapTimeSource timeSource = HapTimeSource.GameTime;
        [SerializeField] float playbackSpeed = 1f;
        [SerializeField] RenderTexture targetRenderTexture;

        // ── Session + pipeline ───────────────────────────────────────────────

        HapFileSession _session;
        HapOutputPipeline _outputPipeline;

        // ── Playback state ───────────────────────────────────────────────────

        PlaybackClock _playbackClock;
        bool _playing;
        bool _pendingPlay;
        int _openedFrame = -1;

        // ── Rendering helpers ────────────────────────────────────────────────

        MaterialPropertyBlock _mpb;
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        // ── Profiler markers ─────────────────────────────────────────────────

        const float k_PlaybackSpeedEpsilon = 1e-5f;

        static readonly ProfilerMarker s_UpdateMarker = new("HapPlayer.Update");
        static readonly ProfilerMarker s_UploadMarker = new("HapPlayer.UploadFrame");
        static readonly ProfilerMarker s_RenderMarker = new("HapPlayer.Render");

        // ── Events ───────────────────────────────────────────────────────────

        /// <summary>
        /// Raised once on the main thread when the video finishes opening and is ready to play.
        /// Not raised if opening fails or is cancelled.
        /// </summary>
        public event System.Action Opened;

        /// <summary>Raised when playback reaches the end and Loop is false.</summary>
        public event System.Action PlaybackCompleted;

        /// <summary>Raised each time playback loops back to the beginning.</summary>
        public event System.Action PlaybackLooped;

        // ── Public properties ────────────────────────────────────────────────

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

        public bool IsPlaying  => _playing && Mathf.Abs(playbackSpeed) > k_PlaybackSpeedEpsilon;
        public bool IsOpen     => _session?.IsOpen ?? false;
        public bool IsOpening  => _session?.IsOpening ?? false;

        public int FrameCount  => _session?.FrameCount ?? 0;
        public float Duration  => _session?.Duration ?? 0f;
        public float FrameRate => _session?.FrameRate ?? 0f;
        public int Width       => _session?.Width ?? 0;
        public int Height      => _session?.Height ?? 0;
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
            get => _playbackClock.Time;
            set
            {
                _playbackClock.Time = Mathf.Clamp(value, 0f, Duration);
                if (FrameCount > 0)
                {
                    int dir = playbackSpeed > 0f ? 1 : -1;
                    _session.RequestDecode(_playbackClock.ToFrame(FrameCount, FrameRate), dir);
                }
            }
        }

        // ── Unity lifecycle ──────────────────────────────────────────────────

        void OnEnable()
        {
            _session ??= new HapFileSession();
            _pendingPlay = playOnEnable;
            if (!string.IsNullOrEmpty(filePath))
                _session.Open(ResolvePath(filePath));
        }

        void OnDisable()
        {
            _session?.CancelOpen();
            CloseInternal();
        }

        void OnDestroy()
        {
            _session?.CancelOpen();
            CloseInternal();
            _session?.Join();
            _session?.Dispose();
            _session = null;
        }

        void Update()
        {
            if (_session == null) return;

            switch (_session.TryConsumeOpenResult())
            {
                case SessionOpenStatus.Opened:
                    CompleteOpen();
                    break;
                case SessionOpenStatus.Failed:
                    _pendingPlay = false;
                    break;
            }

            if (!IsOpen) return;
            using (s_UpdateMarker.Auto())
                UpdatePlayback();
        }

        // ── File open/close ──────────────────────────────────────────────────

        static string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (Path.IsPathRooted(path)) return path;
            return Path.Combine(Application.streamingAssetsPath, path);
        }

        void CompleteOpen()
        {
            int uploaderCount = Mathf.Max(2, QualitySettings.maxQueuedFrames + 1);
            _outputPipeline = new HapOutputPipeline(_session.Width, _session.Height, _session.Format, uploaderCount);
            _playbackClock.Time = 0f;
            _openedFrame = UnityEngine.Time.frameCount;
            _session.StartDecoding(0);

            Opened?.Invoke();
            if (_pendingPlay)
            {
                _pendingPlay = false;
                Play();
            }
        }

        void CloseInternal()
        {
            _playing = false;
            _outputPipeline?.Dispose();
            _outputPipeline = null;
            _session?.Close();
        }

        // ── Public playback control ──────────────────────────────────────────

        public void Play()
        {
            if (_session?.IsOpening == true)
            {
                _pendingPlay = true;
                return;
            }
            if (!IsOpen) return;
            // When starting reverse playback from position 0, jump to the end so the first
            // Update doesn't immediately hit the start-of-video boundary.
            if (playbackSpeed < -k_PlaybackSpeedEpsilon && _playbackClock.Time <= 0f)
            {
                _playbackClock.Time = Duration;
                if (FrameCount > 0)
                    _session.RequestDecode(_playbackClock.ToFrame(FrameCount, FrameRate), -1);
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
            _playbackClock.Time = 0f;
            if (IsOpen && FrameCount > 0)
                _session.RequestDecode(0, 1);
        }

        /// <summary>Close current file (if any) and open a new one.</summary>
        public void Open(string path)
        {
            _session ??= new HapFileSession();
            _session.CancelOpen();
            CloseInternal();
            filePath = path;
            _session.Open(ResolvePath(filePath));
        }

        // ── Playback update ──────────────────────────────────────────────────

        void UpdatePlayback()
        {
            if (_playing && Mathf.Abs(playbackSpeed) > k_PlaybackSpeedEpsilon)
            {
                float dt = timeSource == HapTimeSource.UnscaledGameTime
                    ? UnityEngine.Time.unscaledDeltaTime
                    : UnityEngine.Time.deltaTime;

                int dir = playbackSpeed > 0f ? 1 : -1;
                var evt = _playbackClock.Advance(dt, playbackSpeed, Duration, loop);

                switch (evt)
                {
                    case ClockAdvanceEvent.Looped:
                        PlaybackLooped?.Invoke();
                        break;
                    case ClockAdvanceEvent.Completed:
                        _playing = false;
                        PlaybackCompleted?.Invoke();
                        break;
                }

                _session.RequestDecode(_playbackClock.ToFrame(FrameCount, FrameRate), dir);
            }

            bool uploadedNewFrame;
            using (s_UploadMarker.Auto())
                uploadedNewFrame = UploadFrame();

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

        bool UploadFrame()
        {
            if (_outputPipeline == null) return false;
            // Skip GPU uploads in the same frame that CompleteOpen() ran. D3D12 requires at least
            // one command-list flush between RenderTexture.Create() and the first blit that targets it.
            if (UnityEngine.Time.frameCount == _openedFrame) return false;
            if (!_session.TryAcquireFrame(out var lease)) return false;
            using (lease)
                return _outputPipeline.Upload(lease.Data, lease.FrameIndex);
        }
    }
}
