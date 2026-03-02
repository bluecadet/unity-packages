using System;
using System.Collections.Generic;

namespace Bluecadet.Spring
{
    /// <summary>
    /// Weak-reference pool for SpringValue and DecayValue instances.
    ///
    /// Fire-and-forget springs (no strong reference held by the caller) are collected
    /// by the GC naturally; their pool slots are reclaimed on the next acquire.
    /// Long-lived springs held by the caller are never pooled and never go stale —
    /// both use cases require no special handling.
    ///
    /// <see cref="ReturnSpring"/> / <see cref="ReturnDecay"/> can be called explicitly
    /// (via <see cref="Spring.Release"/>) to reset and reclaim a slot immediately
    /// rather than waiting for GC.
    ///
    /// Slot lookup is O(1): a free-slot stack avoids scanning the weak-ref list.
    /// </summary>
    internal static class SpringPool<T> where T : struct
    {
        // Parallel structure for springs and decays.
        // _slots holds the weak refs; _freeSlots holds indices of slots known to be empty.
        // A slot becomes free either when the GC collects the target (lazy discovery on Get)
        // or immediately when Release is called.

        private static readonly List<WeakReference<SpringValue<T>>> _springSlots = new();
        private static readonly Stack<int>                           _springFree  = new();

        private static readonly List<WeakReference<DecayValue<T>>>  _decaySlots  = new();
        private static readonly Stack<int>                           _decayFree   = new();

        // ---- Springs ----

        internal static SpringValue<T> GetSpring()
        {
            while (_springFree.Count > 0)
            {
                int i = _springFree.Pop();

                // The slot may have been reused since it was pushed — verify it's still dead.
                if (i < _springSlots.Count && !_springSlots[i].TryGetTarget(out _))
                {
                    var s = new SpringValue<T>();
                    _springSlots[i].SetTarget(s);
                    return s;
                }
                // Slot was already reclaimed by a previous Get — discard and keep popping.
            }

            // No free slots — grow the list.
            var fresh = new SpringValue<T>();
            _springSlots.Add(new WeakReference<SpringValue<T>>(fresh));
            return fresh;
        }

        internal static void ReturnSpring(SpringValue<T> spring)
        {
            spring.ResetState();

            // Find the slot and mark it free immediately.
            for (int i = 0; i < _springSlots.Count; i++)
            {
                if (_springSlots[i].TryGetTarget(out var target) && ReferenceEquals(target, spring))
                {
                    _springSlots[i].SetTarget(null!);
                    _springFree.Push(i);
                    return;
                }
            }
        }

        // ---- Decays ----

        internal static DecayValue<T> GetDecay()
        {
            while (_decayFree.Count > 0)
            {
                int i = _decayFree.Pop();

                if (i < _decaySlots.Count && !_decaySlots[i].TryGetTarget(out _))
                {
                    var d = new DecayValue<T>();
                    _decaySlots[i].SetTarget(d);
                    return d;
                }
            }

            var fresh = new DecayValue<T>();
            _decaySlots.Add(new WeakReference<DecayValue<T>>(fresh));
            return fresh;
        }

        internal static void ReturnDecay(DecayValue<T> decay)
        {
            decay.ResetState();

            for (int i = 0; i < _decaySlots.Count; i++)
            {
                if (_decaySlots[i].TryGetTarget(out var target) && ReferenceEquals(target, decay))
                {
                    _decaySlots[i].SetTarget(null!);
                    _decayFree.Push(i);
                    return;
                }
            }
        }
    }
}
