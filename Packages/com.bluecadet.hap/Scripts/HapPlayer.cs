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
    ///
    /// A player does not drive itself: while it has a file open or a request in flight it is
    /// registered with <see cref="HapMainLoop"/>, which advances every player's clock, decode
    /// request, upload and render from one place. See that class for why the uploads in
    /// particular are better off scheduled across all players than issued per component.
    /// </summary>
    public class HapPlayer : MonoBehaviour, IHapUploadTarget
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
                _lifecycle.Closing += HandleClosing;
                _lifecycle.Opened += HandleOpened;
                _lifecycle.StartFrame = TakeStartFrame;
                return _lifecycle;
            }
        }

        // ── Playback state ───────────────────────────────────────────────────

        PlaybackClock _playbackClock;
        bool _playing;
        bool _pendingPlay;
        int _openedFrame = -1;

        /// <summary>
        /// A seek asked for while no file was open yet, in seconds, or null for none. The clock
        /// cannot take it at the time: the duration to clamp it against and the frame rate to
        /// turn it into a frame both arrive with the file. So it waits here and is spent as that
        /// file starts decoding — see <see cref="TakeStartFrame"/>, which is also why opening no
        /// longer resets a caller's seek back to the top.
        /// </summary>
        float? _pendingSeekTime;

        /// <summary>
        /// Where this player sits in <see cref="HapMainLoop"/>'s list, or -1 when it is not
        /// registered. The loop owns the value; it lives on the player so leaving the loop costs
        /// a swap rather than a scan of every registered player.
        /// </summary>
        internal int MainLoopIndex = -1;

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

        static readonly ProfilerMarker s_ClockMarker = new("HapPlayer.AdvanceClock");
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

        /// <summary>
        /// How many bytes of decoded video the shared main loop is willing to hand to the GPU in
        /// one tick, across every open <see cref="HapPlayer"/>, or 0 (the default) for no cap.
        ///
        /// A tick that would go over the cap does not drop a player's frame outright: the players
        /// it defers keep showing what they already uploaded and their clocks keep running, and
        /// each gets another chance to upload on its next turn. Worth setting once enough players
        /// are open at once that a tick's uploads would overrun the frame budget — it trades some
        /// dropped frames for a flat per-frame upload cost across all of them.
        /// </summary>
        public static long UploadBudgetBytesPerFrame
        {
            get => HapMainLoop.UploadBudgetBytesPerFrame;
            set => HapMainLoop.UploadBudgetBytesPerFrame = value;
        }

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
        ///
        /// Seeking is allowed before the file is open — during an <see cref="Open(string)"/> in
        /// flight, or ahead of one — and the video then starts on that time rather than at the
        /// top. The seek is held as asked and clamped to the duration once the file supplies one,
        /// which is what this reads back in the meantime. Opening a file clears a seek made
        /// before the call, so a stale one cannot be inherited by the next video.
        /// </summary>
        public float Time
        {
            get => _pendingSeekTime ?? _playbackClock.Time;
            set
            {
                if (!IsOpen)
                {
                    _pendingSeekTime = value;
                    return;
                }

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
            // Closing is what takes a player out of the shared loop, and only once the file is
            // actually released — so a disabled player's teardown still finishes promptly instead
            // of waiting for something to enable it again.
            Lifecycle.Close(HapOpenStatus.Cancelled);
            SyncMainLoop();
        }

        void OnDestroy() => AbandonAfterDestroy(DestroyTeardownWaitMs);

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

            // Through the seek path, so this also discards a seek waiting on an open in flight —
            // that file is meant to start at the top now.
            Time = 0f;
        }

        // ── Lifecycle plumbing ───────────────────────────────────────────────

        /// <summary>
        /// Advance the open/close state machine. The first thing <see cref="HapMainLoop"/> does
        /// for a player each tick, and the only thing it does for one that is disabled or still
        /// releasing a file.
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
        /// Keep this player in the shared main loop exactly while the loop has something to do
        /// for it: play an open file, or carry an open or a close to its end — the latter
        /// including while the component is disabled, when nothing else would finish it.
        ///
        /// Both calls do nothing when the player is already in the state they ask for, so a video
        /// that just plays neither joins nor leaves the loop from one frame to the next.
        /// </summary>
        void SyncMainLoop()
        {
            // A call from the wrong thread is refused rather than acted on, and the loop's own
            // bookkeeping is no safer to touch from there than anything else.
            if (!HapThread.IsMain) return;

            if (Lifecycle.IsOpen || Lifecycle.HasPendingWork)
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

        /// <summary>
        /// The current file is going away, which takes the playback that was running on it — and
        /// any seek still waiting to be applied, since that was aimed at this file. A seek made
        /// after the Open call that raised this is what survives, and is the whole point: the
        /// caller asked for the video that is arriving to start there.
        /// </summary>
        void HandleClosing()
        {
            StopPlayback();
            _pendingSeekTime = null;
        }

        /// <summary>
        /// Where the file about to start decoding begins: a seek made while it was opening, or
        /// the top. Called once the metadata is known and before the first frame is queued, so
        /// the seek target is the first frame decoded and no frame 0 is ever shown on the way.
        /// </summary>
        int TakeStartFrame()
        {
            _playbackClock.Time = Mathf.Clamp(_pendingSeekTime ?? 0f, 0f, Duration);
            _pendingSeekTime = null;
            return _playbackClock.ToFrame(FrameCount, FrameRate);
        }

        /// <summary>
        /// The file is open. Its clock is already sitting on the frame decoding started from —
        /// <see cref="TakeStartFrame"/> settled that a moment ago — so this only marks the frame
        /// and tells everyone waiting on it.
        /// </summary>
        void HandleOpened()
        {
            _openedFrame = UnityEngine.Time.frameCount;

            Opened?.Invoke();

            // A handler is free to close this player again, which clears the pending play.
            if (_pendingPlay)
            {
                _pendingPlay = false;
                Play();
            }
        }

        // ── Playback tick, driven by HapMainLoop ─────────────────────────────

        /// <summary>The delta this player's clock runs on, per its <see cref="TimeSource"/>.</summary>
        internal float PlaybackDeltaTime => timeSource == HapTimeSource.UnscaledGameTime
            ? UnityEngine.Time.unscaledDeltaTime
            : UnityEngine.Time.deltaTime;

        /// <summary>
        /// The first half of a playback tick: advance the clock by <paramref name="deltaTime"/>
        /// seconds and ask the decoder for the frame it landed on.
        /// </summary>
        /// <returns>
        /// True when a decoded frame is waiting to go to the GPU, which is what puts this player
        /// in the tick's upload phase. The loop finishes every other player with
        /// <see cref="TickRender"/> instead.
        /// </returns>
        internal bool TickClock(float deltaTime)
        {
            if (!Lifecycle.IsOpen) return false;

            using (s_ClockMarker.Auto())
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

                    // Those handlers are free to close this player or open another file, which
                    // takes the session and the pipeline out from under the rest of this tick.
                    // Everything below does nothing without them.
                    SeekDecodeToClock();
                }
            }

            return HasFrameToUpload;
        }

        /// <summary>
        /// The second half of a tick for a player the upload phase picked: push the decoded frame
        /// to the GPU, show it, and swap the display buffer behind it.
        /// </summary>
        internal void TickUpload()
        {
            bool uploadedNewFrame;
            using (s_UploadMarker.Auto())
                uploadedNewFrame = Lifecycle.Pipeline?.Present() ?? false;

            TickRender();

            if (uploadedNewFrame)
                Lifecycle.Pipeline?.SwapBuffers();
        }

        /// <summary>
        /// The end of a tick that uploads nothing: no new frame was decoded, or the loop's upload
        /// budget deferred this player to a later tick. What is already on the GPU still has to
        /// reach its target — and <see cref="RenderFrame"/> skips even that when nothing about it
        /// changed.
        /// </summary>
        internal void TickRender()
        {
            using (s_RenderMarker.Auto())
                RenderFrame();
        }

        /// <summary>
        /// Whether the decode thread has a frame waiting that this player has not shown yet.
        /// Answered without pinning the frame, since it is asked of every player every tick and
        /// only decides who takes part in the upload phase.
        /// </summary>
        bool HasFrameToUpload
        {
            get
            {
                var pipeline = Lifecycle.Pipeline;
                if (pipeline == null) return false;

                // Nothing goes to the GPU in the frame the file opened in: D3D12 requires at
                // least one command-list flush between RenderTexture.Create() and the first blit
                // that targets it.
                //
                // Play mode only, because out of play mode Time.frameCount never advances — it
                // would hold every upload back forever, leaving preview stuck on an
                // uninitialized texture while the clock ran on. Preview therefore takes the
                // hazard rather than the freeze; it is a D3D12 one, and the editor submits its
                // own frames between the open and any tick that follows.
                if (Application.isPlaying && UnityEngine.Time.frameCount == _openedFrame)
                    return false;

                return pipeline.HasPendingFrame;
            }
        }

        long IHapUploadTarget.PendingUploadBytes => Lifecycle.Pipeline?.UploadBytes ?? 0;

        void IHapUploadTarget.TickUpload() => TickUpload();

        void IHapUploadTarget.TickRender() => TickRender();

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
