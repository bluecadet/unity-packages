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
    /// MonoBehaviour that plays Hap-encoded video files.
    ///
    /// Opening and closing are asynchronous and awaitable: <see cref="OpenAsync"/> completes on
    /// the main thread with a typed <see cref="OpenResult"/>, and <see cref="CloseAsync"/>
    /// completes once the file is fully released. The fire-and-forget <see cref="Open(string)"/>
    /// / <see cref="Close()"/> pair and the <see cref="Opened"/> event sit on top of the same
    /// path for inspector-driven use.
    ///
    /// The last call wins: opening while another open or a file is already live supersedes it,
    /// and the superseded caller's await completes with <see cref="HapOpenStatus.Superseded"/>.
    /// Opening while a close is still finishing queues behind that teardown.
    /// </summary>
    public class HapPlayer : MonoBehaviour
    {
        /// <summary>
        /// How long <see cref="OnDestroy"/> waits for the decode thread to park. Nothing can
        /// await there and Unity is about to reclaim the textures the decode thread writes
        /// into, so this is the one place a short block is worth it; the thread only has to
        /// finish the frame in flight. If it is slower, the teardown is handed to
        /// <see cref="HapMainLoop"/> and finishes there instead of blocking any longer.
        /// </summary>
        const int DestroyTeardownWaitMs = 250;

        // ── Serialized fields ────────────────────────────────────────────────

        [SerializeField] string filePath;
        [SerializeField] bool playOnEnable = true;
        [SerializeField] bool loop = true;
        [SerializeField] Renderer targetRenderer;
        [SerializeField] HapRenderMode renderMode = HapRenderMode.MaterialOverride;
        [SerializeField] HapTimeSource timeSource = HapTimeSource.GameTime;
        [SerializeField] float playbackSpeed = 1f;
        [SerializeField] RenderTexture targetRenderTexture;

        // ── Open/close state ─────────────────────────────────────────────────

        HapLifecycle _lifecycle;

        /// <summary>
        /// The open/close state machine, built on first touch: edit mode never runs Awake, and
        /// the async API has to work there too.
        /// </summary>
        HapLifecycle Lifecycle
        {
            get
            {
                if (_lifecycle != null) return _lifecycle;

                _lifecycle = new HapLifecycle();
                _lifecycle.PathAdopted += path => filePath = path;
                _lifecycle.Closing += StopPlayback;
                _lifecycle.Opened += HandleOpened;
                return _lifecycle;
            }
        }

        // ── Playback state ───────────────────────────────────────────────────

        PlaybackClock _playbackClock;
        bool _playing;
        bool _pendingPlay;
        int _openedFrame = -1;

        // ── Rendering helpers ────────────────────────────────────────────────

        MaterialPropertyBlock _mpb;
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        /// <summary>
        /// What <see cref="RenderFrame"/> last acted on, so a tick where none of it moved can
        /// skip the blit / property-block round trip entirely instead of repeating it for
        /// nothing every frame — the point of this whole cache.
        /// </summary>
        Texture _lastRenderedTexture;
        HapRenderMode _lastRenderMode;
        Renderer _lastTargetRenderer;
        RenderTexture _lastTargetRenderTexture;

        /// <summary>Test seam: counts calls to <see cref="RenderFrame"/> that did real GPU work.</summary>
        internal int RenderWorkCount { get; private set; }

        // ── Profiler markers ─────────────────────────────────────────────────

        const float k_PlaybackSpeedEpsilon = 1e-5f;

        static readonly ProfilerMarker s_UpdateMarker = new("HapPlayer.Update");
        static readonly ProfilerMarker s_UploadMarker = new("HapPlayer.UploadFrame");
        static readonly ProfilerMarker s_RenderMarker = new("HapPlayer.Render");

        // ── Events ───────────────────────────────────────────────────────────

        /// <summary>
        /// Raised once on the main thread when the video finishes opening and is ready to play.
        /// Not raised if opening fails, is superseded, or is cancelled.
        /// </summary>
        public event System.Action Opened;

        /// <summary>Raised when playback reaches the end and Loop is false.</summary>
        public event System.Action PlaybackCompleted;

        /// <summary>Raised each time playback loops back to the beginning.</summary>
        public event System.Action PlaybackLooped;

        // ── Public properties ────────────────────────────────────────────────

        /// <summary>
        /// The current video frame as a correctly-oriented RGBA RenderTexture, or null while
        /// no file is open.
        /// </summary>
        public Texture Texture => Lifecycle.Pipeline?.DisplayTexture;

        public bool IsPlaying  => _playing && Mathf.Abs(playbackSpeed) > k_PlaybackSpeedEpsilon;

        /// <summary>True once a file is open and decoding.</summary>
        public bool IsOpen     => Lifecycle.IsOpen;

        /// <summary>True while an open is in flight, including one queued behind a close.</summary>
        public bool IsOpening  => Lifecycle.IsOpening;

        /// <summary>True while a file is being released.</summary>
        public bool IsClosing  => Lifecycle.IsClosing;

        public int FrameCount  => Lifecycle.Session?.FrameCount ?? 0;
        public float Duration  => Lifecycle.Session?.Duration ?? 0f;
        public float FrameRate => Lifecycle.Session?.FrameRate ?? 0f;
        public int Width       => Lifecycle.Session?.Width ?? 0;
        public int Height      => Lifecycle.Session?.Height ?? 0;
        public string FilePath => filePath;

        /// <summary>
        /// How many threads decompress a chunked frame's chunks in parallel, including the
        /// decode thread itself (so 1 means "no helper threads"). Counts above the plugin's
        /// worker pool size are clamped to it.
        ///
        /// This is a process-wide setting shared by every <see cref="HapPlayer"/>, not a
        /// per-player one: the last assignment wins and applies to every video currently
        /// playing, from its next chunked frame onwards. Reads back 0 until it is assigned,
        /// meaning "the plugin's default", which is one thread per hardware thread minus the
        /// ones the engine needs.
        /// </summary>
        public static int DecodeThreadCount
        {
            get => s_decodeThreadCount;
            set
            {
                if (value < 1)
                {
                    Debug.LogError($"[HapPlayer] DecodeThreadCount must be at least 1, got {value}");
                    return;
                }
                var error = HapNative.SetThreadCount(value);
                if (error != HapError.Ok)
                {
                    Debug.LogError($"[HapPlayer] Failed to set decode thread count to {value}: {error}");
                    return;
                }
                s_decodeThreadCount = value;
            }
        }

        static int s_decodeThreadCount;

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
                SeekDecodeToClock();
            }
        }

        // ── Unity lifecycle ──────────────────────────────────────────────────

        void OnEnable()
        {
            // Nothing to open, so stay out of the shared main loop rather than have it spin up
            // for a player with no work in flight.
            if (string.IsNullOrEmpty(filePath)) return;

            // Re-enabling after a disable reopens the file. If that disable's teardown is still
            // finishing, the open simply queues behind it.
            Open(filePath);

            // After the open, not before: opening supersedes the previous file and stops its
            // playback, pending or otherwise. Play() queues itself behind an open in flight.
            if (playOnEnable) Play();
        }

        void OnDisable()
        {
            // Disabling stops ticking this component, but the shared main loop keeps the
            // teardown moving so the textures are still released promptly.
            Lifecycle.Close(HapOpenStatus.Cancelled);
            SyncMainLoop();
        }

        void OnDestroy() => AbandonAfterDestroy(DestroyTeardownWaitMs);

        void Update()
        {
            TickLifecycle();

            if (!Lifecycle.IsOpen) return;

            float deltaTime = timeSource == HapTimeSource.UnscaledGameTime
                ? UnityEngine.Time.unscaledDeltaTime
                : UnityEngine.Time.deltaTime;

            using (s_UpdateMarker.Auto())
                TickPlayback(deltaTime);
        }

        // ── Public open/close ────────────────────────────────────────────────

        /// <summary>
        /// Open a video file, superseding whatever this player was doing. The returned
        /// awaitable completes on the main thread with <see cref="HapOpenStatus.Success"/> once
        /// the file is decoding, or with the reason it did not open — including
        /// <see cref="HapOpenStatus.Superseded"/> if another Open/Close call replaced this one
        /// and <see cref="HapOpenStatus.Cancelled"/> if the component was torn down first.
        ///
        /// Relative paths resolve inside StreamingAssets. Main thread only.
        /// </summary>
        public Awaitable<OpenResult> OpenAsync(string path)
        {
            var awaitable = Lifecycle.OpenAsync(path);
            SyncMainLoop();
            return awaitable;
        }

        /// <summary>
        /// Open a video file without waiting for the result — the <see cref="Opened"/> event
        /// reports success, and failures are logged. Main thread only.
        /// </summary>
        public void Open(string path)
        {
            Lifecycle.Open(path);
            SyncMainLoop();
        }

        /// <summary>
        /// Close the current file. The returned awaitable completes on the main thread once the
        /// decode thread has parked, the file is closed and its textures are released — or
        /// immediately if nothing is open. Main thread only.
        /// </summary>
        public Awaitable CloseAsync()
        {
            var awaitable = Lifecycle.CloseAsync();
            SyncMainLoop();
            return awaitable;
        }

        /// <summary>Close the current file without waiting for the teardown. Main thread only.</summary>
        public void Close()
        {
            Lifecycle.Close(HapOpenStatus.Superseded);
            SyncMainLoop();
        }

        // ── Public playback control ──────────────────────────────────────────

        public void Play()
        {
            if (IsOpening)
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
                SeekDecodeToClock();
            }
            _playing = true;
        }

        public void Pause() => StopPlayback();

        /// <summary>Stop playback and reset to frame 0, regardless of playback direction.</summary>
        public void Stop()
        {
            StopPlayback();
            _playbackClock.Time = 0f;
            SeekDecodeToClock();
        }

        // ── Lifecycle plumbing ───────────────────────────────────────────────

        /// <summary>
        /// Advance the open/close state machine. Called from Update and from
        /// <see cref="HapMainLoop"/>, which keeps disabled players moving too.
        /// </summary>
        internal void TickLifecycle()
        {
            Lifecycle.Tick();
            SyncMainLoop();
        }

        /// <summary>
        /// Release everything for a player that is going away, whether or not Unity delivered
        /// <see cref="OnDestroy"/> (outside play mode it does not).
        /// </summary>
        /// <param name="waitForReleaseMs">
        /// How long to give the background teardown before handing it to <see cref="HapMainLoop"/>.
        /// Only OnDestroy passes anything but zero, and only because Unity is about to reclaim
        /// the textures the decode thread writes into and nothing there can await.
        /// </param>
        internal void AbandonAfterDestroy(int waitForReleaseMs = 0)
        {
            var orphan = Lifecycle.Abandon(waitForReleaseMs);
            if (orphan != null)
                HapMainLoop.Orphan(orphan);

            HapMainLoop.Unregister(this);
        }

        /// <summary>
        /// Keep the shared main loop ticking this player exactly while it has an open or close
        /// in flight — including while it is disabled, when its own Update is not running.
        /// </summary>
        void SyncMainLoop()
        {
            // A call from the wrong thread is refused rather than acted on, and the loop's own
            // bookkeeping is no safer to touch from there than anything else.
            if (!HapThread.IsMain) return;

            if (Lifecycle.HasPendingWork)
                HapMainLoop.Register(this);
            else
                HapMainLoop.Unregister(this);
        }

        /// <summary>Playback cannot outlive the file it was running on.</summary>
        void StopPlayback()
        {
            _pendingPlay = false;
            _playing = false;
        }

        /// <summary>The file is open: start it from the top and tell everyone waiting on it.</summary>
        void HandleOpened()
        {
            _playbackClock.Time = 0f;
            _openedFrame = UnityEngine.Time.frameCount;

            Opened?.Invoke();

            // A handler is free to close this player again, which clears the pending play.
            if (_pendingPlay)
            {
                _pendingPlay = false;
                Play();
            }
        }

        // ── Playback update ──────────────────────────────────────────────────

        /// <summary>
        /// Advance the clock by <paramref name="deltaTime"/> seconds, upload whatever has been
        /// decoded, and render it. Update supplies the frame's delta; tests supply their own.
        /// </summary>
        internal void TickPlayback(float deltaTime)
        {
            if (_playing && Mathf.Abs(playbackSpeed) > k_PlaybackSpeedEpsilon)
            {
                var evt = _playbackClock.Advance(deltaTime, playbackSpeed, Duration, loop);

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

                // Those handlers are free to close this player or open another file, which takes
                // the session and the pipeline out from under the rest of this tick. Everything
                // below does nothing without them.
                SeekDecodeToClock();
            }

            bool uploadedNewFrame;
            using (s_UploadMarker.Auto())
                uploadedNewFrame = UploadFrame();

            using (s_RenderMarker.Auto())
                RenderFrame();

            if (uploadedNewFrame)
                Lifecycle.Pipeline?.SwapBuffers();
        }

        /// <summary>
        /// Ask the decoder for the frame the clock is now on, reading ahead in the direction
        /// playback is moving.
        /// </summary>
        void SeekDecodeToClock()
        {
            if (!IsOpen || FrameCount <= 0) return;

            int direction = playbackSpeed > 0f ? 1 : -1;
            Lifecycle.Session.RequestDecode(_playbackClock.ToFrame(FrameCount, FrameRate), direction);
        }

        bool UploadFrame()
        {
            var pipeline = Lifecycle.Pipeline;
            if (pipeline == null) return false;
            // Skip GPU uploads in the same frame that the file opened in. D3D12 requires at least
            // one command-list flush between RenderTexture.Create() and the first blit that targets it.
            if (UnityEngine.Time.frameCount == _openedFrame) return false;
            return pipeline.Present();
        }

        /// <summary>
        /// Put the current frame wherever <see cref="RenderMode"/> says it goes — skipped when
        /// none of that has changed since the last call, so a paused video or one whose frame
        /// rate is below the display's does not repeat a full-resolution blit or a
        /// GetPropertyBlock/SetPropertyBlock round trip every single tick for nothing.
        ///
        /// Dirty is "the displayed texture, the render mode, or the target changed" — which also
        /// covers the first frame after opening (texture goes from null to something) and a
        /// reopen at a different size (a new pipeline means a new texture). It does not cover a
        /// third party clobbering the target behind this component's back — resetting the
        /// renderer's property block or drawing into the target RenderTexture itself — since
        /// nothing here changed to notice; that is an accepted limitation, not re-asserted every
        /// frame.
        /// </summary>
        void RenderFrame()
        {
            var tex = Texture;
            if (tex == null) return;

            bool unchanged = tex == _lastRenderedTexture
                              && renderMode == _lastRenderMode
                              && targetRenderer == _lastTargetRenderer
                              && targetRenderTexture == _lastTargetRenderTexture;
            if (unchanged) return;

            _lastRenderedTexture = tex;
            _lastRenderMode = renderMode;
            _lastTargetRenderer = targetRenderer;
            _lastTargetRenderTexture = targetRenderTexture;

            switch (renderMode)
            {
                case HapRenderMode.MaterialOverride:
                    if (targetRenderer == null) break;
                    _mpb ??= new MaterialPropertyBlock();
                    targetRenderer.GetPropertyBlock(_mpb);
                    _mpb.SetTexture(MainTexId, tex);
                    targetRenderer.SetPropertyBlock(_mpb);
                    RenderWorkCount++;
                    break;
                case HapRenderMode.RenderTexture:
                    if (targetRenderTexture != null)
                    {
                        Graphics.Blit(tex, targetRenderTexture);
                        RenderWorkCount++;
                    }
                    break;
                case HapRenderMode.APIOnly:
                    break;
            }
        }
    }
}
