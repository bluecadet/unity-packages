using System.Collections.Generic;
using UnityEngine;

namespace Bluecadet.Spring
{
    /// <summary>
    /// Static manager that tracks and advances all active springs and decays.
    /// Updated each frame via the PlayerLoop (see <see cref="SpringPlayerLoop"/>).
    /// No MonoBehaviour or GameObject required.
    /// </summary>
    public static class SpringManager
    {
        // List for allocation-free for-loop iteration; Dictionary for O(1) add-guard and O(1) swap-remove index lookup.
        private static readonly List<IMotion>             _activeSprings   = new();
        private static readonly Dictionary<IMotion, int>  _activeSpringIdx = new(); // motion -> index in list
        private static readonly List<IMotion>             _activeDecays    = new();
        private static readonly Dictionary<IMotion, int>  _activeDecayIdx  = new();

        // Deferred removal — mutating the list during Advance() would corrupt iteration.
        private static readonly List<IMotion> _toRemove = new();

        // -------------------------------------------------------------------------
        // PlayerLoop entry point
        // -------------------------------------------------------------------------

        /// <summary>
        /// Called once per frame by the PlayerLoop system.
        /// Advances all active springs and decays, then flushes the removal queue.
        /// </summary>
        internal static void Update()
        {
            float dt = Time.deltaTime;

            for (int i = 0; i < _activeSprings.Count; i++)
                _activeSprings[i].Advance(dt);

            for (int i = 0; i < _activeDecays.Count; i++)
                _activeDecays[i].Advance(dt);

            FlushRemovals();
        }

        // -------------------------------------------------------------------------
        // Active motion management (called by SpringValue / DecayValue)
        // -------------------------------------------------------------------------

        internal static void AddActiveSpring(IMotion motion)
        {
            if (!_activeSpringIdx.ContainsKey(motion))
            {
                _activeSpringIdx[motion] = _activeSprings.Count;
                _activeSprings.Add(motion);
            }
        }

        internal static void RemoveActiveSpring(IMotion motion)
            => _toRemove.Add(motion);

        internal static void AddActiveDecay(IMotion motion)
        {
            if (!_activeDecayIdx.ContainsKey(motion))
            {
                _activeDecayIdx[motion] = _activeDecays.Count;
                _activeDecays.Add(motion);
            }
        }

        internal static void RemoveActiveDecay(IMotion motion)
            => _toRemove.Add(motion);

        // -------------------------------------------------------------------------
        // Global control
        // -------------------------------------------------------------------------

        /// <summary>
        /// Stop all active motions, return them to their pools, and clear all lists.
        /// Called by <see cref="Spring.KillAll"/>.
        /// </summary>
        internal static void Shutdown()
        {
            for (int i = 0; i < _activeSprings.Count; i++)
                _activeSprings[i].Release();

            for (int i = 0; i < _activeDecays.Count; i++)
                _activeDecays[i].Release();

            _activeSprings.Clear();
            _activeSpringIdx.Clear();
            _activeDecays.Clear();
            _activeDecayIdx.Clear();
            _toRemove.Clear();
        }

        // -------------------------------------------------------------------------
        // Stats
        // -------------------------------------------------------------------------

        /// <summary>Number of currently animating springs.</summary>
        public static int ActiveSpringCount => _activeSprings.Count;

        /// <summary>Number of currently animating decays.</summary>
        public static int ActiveDecayCount => _activeDecays.Count;

        // -------------------------------------------------------------------------
        // Private helpers
        // -------------------------------------------------------------------------

        private static void FlushRemovals()
        {
            if (_toRemove.Count == 0) return;

            for (int i = 0; i < _toRemove.Count; i++)
            {
                var m = _toRemove[i];
                if (!SwapRemove(_activeSprings, _activeSpringIdx, m))
                    SwapRemove(_activeDecays, _activeDecayIdx, m);
            }
            _toRemove.Clear();
        }

        /// <summary>
        /// O(1) removal: swap the target element with the last, pop the last, update the index map.
        /// </summary>
        private static bool SwapRemove(List<IMotion> list, Dictionary<IMotion, int> idx, IMotion motion)
        {
            if (!idx.TryGetValue(motion, out int i)) return false;

            int last = list.Count - 1;
            if (i < last)
            {
                // Move last element into the vacated slot and update its index.
                var tail = list[last];
                list[i]  = tail;
                idx[tail] = i;
            }

            list.RemoveAt(last);
            idx.Remove(motion);
            return true;
        }
    }
}
