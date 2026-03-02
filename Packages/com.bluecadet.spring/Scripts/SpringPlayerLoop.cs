using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.Scripting;

// Ensures this assembly is preserved during managed code stripping in builds,
// even if no scene type directly references a type from this package.
[assembly: AlwaysLinkAssembly]

namespace Bluecadet.Spring
{
    /// <summary>
    /// Marker type used as the PlayerLoop identity key and Profiler label
    /// for the spring manager update system.
    /// </summary>
    internal struct SpringUpdate { }

    /// <summary>
    /// Installs <see cref="SpringManager.Update"/> into Unity's PlayerLoop at startup.
    /// Uses <see cref="RuntimeInitializeOnLoadMethod"/> so no MonoBehaviour or
    /// GameObject is needed.
    ///
    /// The system is appended to the end of the <c>Update</c> phase, equivalent
    /// to a late <c>MonoBehaviour.Update()</c>.
    ///
    /// Idempotent: safe to call when "Enter Play Mode > Disable Domain Reload" is enabled.
    /// </summary>
    internal static class SpringPlayerLoop
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();

            // Remove first — idempotent guard for domain-reload-disabled workflows.
            Uninstall(ref loop);
            Install(ref loop);

            PlayerLoop.SetPlayerLoop(loop);

            // Clean up on application quit (important for non-editor builds and
            // edit-mode tests that don't trigger a full domain reload).
            Application.quitting += OnQuit;
        }

        private static void OnQuit()
        {
            Application.quitting -= OnQuit;
            SpringManager.Shutdown();

            var loop = PlayerLoop.GetCurrentPlayerLoop();
            Uninstall(ref loop);
            PlayerLoop.SetPlayerLoop(loop);
        }

        private static void Install(ref PlayerLoopSystem root)
        {
            var system = new PlayerLoopSystem
            {
                type           = typeof(SpringUpdate),
                updateDelegate = SpringManager.Update,
            };

            if (!TryAppend<Update>(ref root, system))
                Debug.LogWarning("[Bluecadet.Spring] Could not inject into PlayerLoop Update phase.");
        }

        private static void Uninstall(ref PlayerLoopSystem root)
            => TryRemoveAll(ref root, typeof(SpringUpdate));

        // -------------------------------------------------------------------------
        // PlayerLoop tree helpers
        // -------------------------------------------------------------------------

        /// <summary>
        /// Recursively find <typeparamref name="TPhase"/> and append <paramref name="system"/>
        /// as its last child.
        /// </summary>
        private static bool TryAppend<TPhase>(ref PlayerLoopSystem root, PlayerLoopSystem system)
            where TPhase : struct
        {
            var subs = root.subSystemList;
            if (subs == null) return false;

            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type == typeof(TPhase))
                {
                    var children = new List<PlayerLoopSystem>(
                        subs[i].subSystemList ?? Array.Empty<PlayerLoopSystem>());
                    children.Add(system);
                    subs[i].subSystemList = children.ToArray();
                    root.subSystemList    = subs;
                    return true;
                }

                // Recurse into sub-phases
                if (TryAppend<TPhase>(ref subs[i], system))
                {
                    root.subSystemList = subs;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Recursively remove all systems whose <c>type</c> matches <paramref name="systemType"/>.
        /// </summary>
        private static void TryRemoveAll(ref PlayerLoopSystem root, Type systemType)
        {
            var subs = root.subSystemList;
            if (subs == null) return;

            var filtered = new List<PlayerLoopSystem>(subs.Length);
            bool changed = false;

            foreach (var s in subs)
            {
                if (s.type == systemType)
                {
                    changed = true;
                    continue;
                }

                var copy = s;
                TryRemoveAll(ref copy, systemType);
                filtered.Add(copy);
            }

            if (changed)
                root.subSystemList = filtered.ToArray();
        }
    }
}
