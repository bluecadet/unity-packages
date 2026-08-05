using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace Bluecadet.Hap
{
    /// <summary>
    /// The one main-thread loop every <see cref="HapPlayer"/> runs on: it advances their
    /// open/close state, their playback clocks and their GPU uploads, and finishes teardowns
    /// whose player has already been destroyed.
    ///
    /// Driving every player from here rather than from each one's own Update is what makes the
    /// upload phase possible. Uploads are the expensive main-thread part of playback, and a
    /// player that runs its own Update uploads in whatever slice of the frame Unity calls it in —
    /// which, for players started together, is the same slice for all of them. A central tick
    /// sees every player that has a frame waiting at once, so it can start the uploads from a
    /// rotating player and stop them at <see cref="UploadBudgetBytesPerFrame"/>.
    ///
    /// A tick runs in two phases, keeping each player's own order — lifecycle, clock, decode
    /// request, upload, render, buffer swap — intact:
    /// <list type="number">
    /// <item>every player's open/close state machine advances, whether or not it is currently
    ///       allowed to play,</item>
    /// <item>every player that is <c>isActiveAndEnabled</c> advances its clock and asks for the
    ///       frame it landed on; the ones with a decoded frame waiting are collected and upload,
    ///       in an order that rotates each tick. A disabled or inactive player sits this phase
    ///       out entirely, the same as it would have sat out its own Update.</item>
    /// </list>
    ///
    /// Ticked in play mode by a hidden object that outlives individual players, and outside play
    /// mode by the editor's update loop — which, while play mode is paused, still runs the first
    /// phase on its own so an in-flight open or close is not stuck waiting for an Update that
    /// will not come.
    /// </summary>
    internal static class HapMainLoop
    {
        static readonly ProfilerMarker s_TickMarker = new("HapMainLoop.Tick");
        static readonly ProfilerMarker s_UploadPhaseMarker = new("HapMainLoop.UploadPhase");

        static readonly List<HapPlayer> s_players = new();
        static readonly List<HapPlayer> s_tickBuffer = new();
        static readonly List<HapTeardown> s_orphans = new();

        /// <summary>Players with a decoded frame waiting, rebuilt by each tick's playback phase.</summary>
        static readonly List<IHapUploadTarget> s_due = new();

        static readonly HapUploadPhase s_uploads = new();

        /// <summary>Close callers of orphaned teardowns, waiting to be resumed.</summary>
        static readonly HapCompletionQueue s_completions = new();

        static HapLifecycleDriver s_driver;

        /// <summary>
        /// How many bytes of decoded video the loop is willing to hand to the GPU in one tick, or
        /// 0 (the default) for no cap. Uploads past the cap are deferred to a later tick rather
        /// than dropped mid-frame: the player keeps rendering what it already has, its clock
        /// keeps running, and it uploads whatever is newest when its turn comes round again.
        ///
        /// Worth setting when the number of players makes a tick's uploads overrun the frame
        /// budget — it trades dropped frames on some players for a flat per-frame cost across all
        /// of them.
        ///
        /// Internal: <see cref="HapPlayer.UploadBudgetBytesPerFrame"/> is the public knob a
        /// consumer actually reaches, forwarded straight through to this field.
        /// </summary>
        public static long UploadBudgetBytesPerFrame;

        /// <summary>How many players the loop is currently ticking. Test seam.</summary>
        internal static int RegisteredCount => s_players.Count;

        /// <summary>
        /// How many times a player has joined or left the loop. Test seam: steady-state playback
        /// must not move this, since registration is a state transition, not per-frame work.
        /// </summary>
        internal static int RegistrationChanges { get; private set; }

        // ── Registration ─────────────────────────────────────────────────────

        /// <summary>
        /// Start ticking a player. Doing nothing when it is already registered is what keeps
        /// playback from touching this list at all.
        /// </summary>
        public static void Register(HapPlayer player)
        {
            if (ReferenceEquals(player, null) || player.MainLoopIndex >= 0) return;

            player.MainLoopIndex = s_players.Count;
            s_players.Add(player);
            RegistrationChanges++;
            EnsureDriver();
        }

        /// <summary>
        /// Stop ticking a player. O(1): the player carries where it sits in the list, so its slot
        /// is filled by the last entry instead of the list being scanned for it.
        /// </summary>
        public static void Unregister(HapPlayer player)
        {
            if (ReferenceEquals(player, null)) return;

            int index = player.MainLoopIndex;
            if (index < 0) return;

            int last = s_players.Count - 1;
            s_players[index] = s_players[last];
            s_players[index].MainLoopIndex = index;
            s_players.RemoveAt(last);

            player.MainLoopIndex = -1;
            RegistrationChanges++;
        }

        /// <summary>
        /// Adopt a teardown whose player is going away, so its textures are still destroyed and
        /// its close waiters still complete.
        /// </summary>
        public static void Orphan(HapTeardown teardown)
        {
            if (teardown == null) return;

            if (teardown.TryFinish())
            {
                CompleteWaiters(teardown);
                return;
            }

            s_orphans.Add(teardown);
            EnsureDriver();
        }

        /// <summary>Complete the close callers of a teardown whose player is gone.</summary>
        static void CompleteWaiters(HapTeardown teardown)
        {
            teardown.DrainWaitersInto(s_completions);
            s_completions.Flush();
        }

        // ── Tick ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Advance every registered player and orphaned teardown, each player's clock running on
        /// its own time source. Main thread only.
        /// </summary>
        public static void Tick() => Tick(null);

        /// <summary>
        /// Tick with one delta for every player instead of each player's own time source — what
        /// the editor loop uses, having no engine frame to take a delta from, and what tests use
        /// to drive playback by a clock they control.
        /// </summary>
        internal static void Tick(float? deltaOverride)
        {
            using (s_TickMarker.Auto())
            {
                TickLifecyclePhase();
                TickPlaybackPhase(deltaOverride);
            }
        }

        static void FinishOrphans()
        {
            for (int i = s_orphans.Count - 1; i >= 0; i--)
            {
                if (!s_orphans[i].TryFinish()) continue;

                var orphan = s_orphans[i];
                s_orphans.RemoveAt(i);
                CompleteWaiters(orphan);
            }
        }

        /// <summary>
        /// First phase: finish orphaned teardowns and carry every player's open/close state
        /// machine forward, including a player Unity destroyed without delivering
        /// <c>OnDestroy</c>. Runs regardless of a player's enabled state — an open or a close has
        /// to finish whether or not that player is currently allowed to play.
        ///
        /// Safe to run more than once in the same frame: <see cref="HapPlayer.TickLifecycle"/>
        /// only ever carries a state machine one step closer to settling. That is what lets the
        /// editor's paused-play-mode tick call this on its own, without also running
        /// <see cref="TickPlaybackPhase"/>.
        /// </summary>
        static void TickLifecyclePhase()
        {
            FinishOrphans();

            if (s_players.Count == 0) return;

            // A player leaves the loop as it settles, and a callback is free to open or close any
            // player from inside this phase, so tick a copy.
            s_tickBuffer.Clear();
            s_tickBuffer.AddRange(s_players);

            foreach (var player in s_tickBuffer)
            {
                // Destroyed, but its managed state is still here: outside play mode Unity does
                // not deliver OnDestroy, so this is where such a player's callers are released.
                if (player == null)
                {
                    player.AbandonAfterDestroy();
                    continue;
                }

                player.TickLifecycle();
            }

            s_tickBuffer.Clear();
        }

        /// <summary>
        /// Second phase: carry the clock of every player that is <c>isActiveAndEnabled</c>
        /// forward and collect the ones with a frame to upload, then run the upload phase. A
        /// disabled component or an inactive GameObject is skipped here entirely — its clock does
        /// not move and nothing of it is decoded, uploaded or rendered — matching the contract a
        /// self-driving <c>MonoBehaviour.Update</c> gave every player before this loop replaced
        /// it. Its lifecycle still advances in <see cref="TickLifecyclePhase"/> regardless, so an
        /// open or close already in flight is not held up by being disabled.
        /// </summary>
        static void TickPlaybackPhase(float? deltaOverride)
        {
            if (s_players.Count > 0)
            {
                // A player leaves the loop as it settles, and a playback event handler is free to
                // open or close any player from inside this phase, so tick a copy.
                s_tickBuffer.Clear();
                s_tickBuffer.AddRange(s_players);

                foreach (var player in s_tickBuffer)
                {
                    if (player == null || !player.isActiveAndEnabled) continue;

                    float deltaTime = deltaOverride ?? player.PlaybackDeltaTime;

                    if (player.TickClock(deltaTime))
                        s_due.Add(player);
                    else
                        player.TickRender();
                }

                s_tickBuffer.Clear();
            }

            using (s_UploadPhaseMarker.Auto())
                s_uploads.Run(s_due, UploadBudgetBytesPerFrame);

            s_due.Clear();
        }

        static void EnsureDriver()
        {
            if (s_driver != null || !Application.isPlaying) return;

            var host = new GameObject("Hap Lifecycle") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(host);
            s_driver = host.AddComponent<HapLifecycleDriver>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void InitializeRuntime()
        {
            // Entering play mode with domain reload turned off keeps these statics from the
            // last session, where every player and every hidden driver object was destroyed.
            foreach (var player in s_players)
            {
                if (!ReferenceEquals(player, null))
                    player.MainLoopIndex = -1;
            }

            s_players.Clear();
            s_tickBuffer.Clear();
            s_due.Clear();
            s_orphans.Clear();
            s_driver = null;
        }

#if UNITY_EDITOR
        static double s_lastEditorTime;

        [UnityEditor.InitializeOnLoadMethod]
        static void InitializeEditor()
        {
            UnityEditor.EditorApplication.update -= TickEditor;
            UnityEditor.EditorApplication.update += TickEditor;
        }

        /// <summary>
        /// The out-of-play-mode tick, plus the one thing play mode cannot rely on the hidden
        /// driver for: <see cref="MonoBehaviour.Update"/> does not fire while play mode is
        /// paused, but <see cref="UnityEditor.EditorApplication.update"/> keeps firing regardless
        /// — so this is the only loop left to finish an in-flight open or close, or an orphaned
        /// teardown, while paused.
        ///
        /// Playback never advances from here in play mode: unpaused, the hidden driver already
        /// ticks it once a frame, and paused is not when a clock should move at all either way.
        /// Only the lifecycle phase runs there, which is harmless to run twice in the same frame
        /// — the old code wired this loop straight to the full tick, and lifecycle work was
        /// always safe under that.
        ///
        /// Out of play mode the editor loop has no engine frame delta to hand players, so it
        /// measures its own.
        /// </summary>
        static void TickEditor()
        {
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            double elapsed = now - s_lastEditorTime;
            s_lastEditorTime = now;

            if (Application.isPlaying)
            {
                TickLifecyclePhase();
                return;
            }

            // First tick of a session, or one the editor took long enough over that advancing a
            // clock by it would jump the video: charge nothing for it.
            float deltaTime = elapsed > 0d && elapsed < 1d ? (float)elapsed : 0f;
            Tick(deltaTime);
        }
#endif
    }

    /// <summary>
    /// Ticks <see cref="HapMainLoop"/> in play mode, on a hidden object that is not tied to any
    /// one player's enabled state.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class HapLifecycleDriver : MonoBehaviour
    {
        void Update() => HapMainLoop.Tick();
    }
}
