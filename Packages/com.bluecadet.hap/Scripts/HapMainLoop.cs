using System.Collections.Generic;
using UnityEngine;

namespace Bluecadet.Hap
{
    /// <summary>
    /// Drives the parts of playback that have to happen on the main thread but cannot rely on
    /// a player's own Update: finishing an open or a close for a component that is disabled
    /// mid-flight, and finishing teardowns whose player has already been destroyed.
    ///
    /// Ticked by each player's Update, by the editor's update loop, and — in play mode — by a
    /// hidden object that outlives individual players.
    /// </summary>
    internal static class HapMainLoop
    {
        static readonly List<HapPlayer> s_players = new();
        static readonly List<HapPlayer> s_tickBuffer = new();
        static readonly List<HapTeardown> s_orphans = new();

        /// <summary>Close callers of orphaned teardowns, waiting to be resumed.</summary>
        static readonly HapCompletionQueue s_completions = new();

        static HapLifecycleDriver s_driver;

        /// <summary>Start ticking a player's lifecycle while it has work in flight.</summary>
        public static void Register(HapPlayer player)
        {
            if (player == null || s_players.Contains(player)) return;
            s_players.Add(player);
            EnsureDriver();
        }

        public static void Unregister(HapPlayer player) => s_players.Remove(player);

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

        /// <summary>Advance every registered player and orphaned teardown. Main thread only.</summary>
        public static void Tick()
        {
            for (int i = s_orphans.Count - 1; i >= 0; i--)
            {
                if (!s_orphans[i].TryFinish()) continue;

                var orphan = s_orphans[i];
                s_orphans.RemoveAt(i);
                CompleteWaiters(orphan);
            }

            if (s_players.Count == 0) return;

            // Players unregister themselves as they settle, so tick a copy.
            s_tickBuffer.Clear();
            s_tickBuffer.AddRange(s_players);
            foreach (var player in s_tickBuffer)
            {
                if (ReferenceEquals(player, null))
                {
                    s_players.Remove(player);
                    continue;
                }

                // Destroyed, but its managed state is still here: outside play mode Unity does
                // not deliver OnDestroy, so this is where such a player's callers are released.
                if (player == null)
                {
                    player.AbandonAfterDestroy();
                    s_players.Remove(player);
                    continue;
                }

                player.TickLifecycle();
            }
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
            s_players.Clear();
            s_tickBuffer.Clear();
            s_orphans.Clear();
            s_driver = null;
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void InitializeEditor()
        {
            UnityEditor.EditorApplication.update -= Tick;
            UnityEditor.EditorApplication.update += Tick;
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
