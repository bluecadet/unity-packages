using System;
using System.Collections.Generic;
using UnityEngine;

namespace Bluecadet.Spring
{
    /// <summary>
    /// Singleton MonoBehaviour that updates all active springs and decays.
    /// Also provides target-based motion tracking for DOTween-style queries.
    /// </summary>
    public class SpringManager : MonoBehaviour
    {
        private static SpringManager _instance;

        // Using List instead of HashSet to avoid enumeration allocations in Update loop
        private List<IMotion> _activeSprings = new();
        private List<IMotion> _activeDecays = new();
        private HashSet<IMotion> _activeSpringSet = new(); // For O(1) contains check
        private HashSet<IMotion> _activeDecaySet = new();
        private List<IMotion> _toRemove = new();

        // Target tracking: target -> (id -> motion)
        private Dictionary<object, Dictionary<string, IMotion>> _motionsByTarget = new();
        private Stack<Dictionary<string, IMotion>> _dictPool = new();

        private const string DefaultId = "__default__";

        private static void EnsureInstance()
        {
            if (_instance == null)
            {
                var go = new GameObject("[Spring Manager]");
                _instance = go.AddComponent<SpringManager>();
                DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideInHierarchy;
            }
        }

        #region Active Motion Management

        public static void AddActiveSpring(IMotion motion)
        {
            EnsureInstance();
            if (_instance._activeSpringSet.Add(motion))
            {
                _instance._activeSprings.Add(motion);
            }
        }

        public static void RemoveActiveSpring(IMotion motion)
        {
            if (_instance == null) return;
            _instance._toRemove.Add(motion);
        }

        public static void AddActiveDecay(IMotion motion)
        {
            EnsureInstance();
            if (_instance._activeDecaySet.Add(motion))
            {
                _instance._activeDecays.Add(motion);
            }
        }

        public static void RemoveActiveDecay(IMotion motion)
        {
            if (_instance == null) return;
            _instance._toRemove.Add(motion);
        }

        #endregion

        #region Target-Based Registration

        public static void RegisterMotion(IMotion motion)
        {
            if (motion.Target == null) return;

            EnsureInstance();
            var target = motion.Target;
            var id = motion.Id ?? DefaultId;

            if (!_instance._motionsByTarget.TryGetValue(target, out var idMap))
            {
                idMap = _instance._dictPool.Count > 0
                    ? _instance._dictPool.Pop()
                    : new Dictionary<string, IMotion>();
                _instance._motionsByTarget[target] = idMap;
            }

            if (idMap.TryGetValue(id, out var existing) && existing != motion)
            {
                existing.Stop();
            }

            idMap[id] = motion;
        }

        public static void UnregisterMotion(IMotion motion)
        {
            if (_instance == null || motion.Target == null) return;

            var target = motion.Target;
            var id = motion.Id ?? DefaultId;

            if (_instance._motionsByTarget.TryGetValue(target, out var idMap))
            {
                if (idMap.TryGetValue(id, out var existing) && existing == motion)
                {
                    idMap.Remove(id);

                    if (idMap.Count == 0)
                    {
                        _instance._motionsByTarget.Remove(target);
                        idMap.Clear();
                        _instance._dictPool.Push(idMap);
                    }
                }
            }
        }

        #endregion

        #region Query Methods

        public static IMotion GetMotion(object target, string id = null)
        {
            if (_instance == null || target == null) return null;

            if (_instance._motionsByTarget.TryGetValue(target, out var idMap))
            {
                var key = id ?? DefaultId;
                if (idMap.TryGetValue(key, out var motion))
                {
                    return motion;
                }

                if (id == null)
                {
                    foreach (var m in idMap.Values)
                        return m;
                }
            }

            return null;
        }

        public static T GetMotion<T>(object target, string id = null) where T : class, IMotion
        {
            return GetMotion(target, id) as T;
        }

        public static IEnumerable<IMotion> GetAllMotions(object target)
        {
            if (_instance == null || target == null) yield break;

            if (_instance._motionsByTarget.TryGetValue(target, out var idMap))
            {
                foreach (var motion in idMap.Values)
                    yield return motion;
            }
        }

        public static bool HasActiveMotions(object target)
        {
            if (_instance == null || target == null) return false;
            return _instance._motionsByTarget.ContainsKey(target);
        }

        #endregion

        #region Control Methods

        public static void KillMotions(object target)
        {
            if (_instance == null || target == null) return;

            if (_instance._motionsByTarget.TryGetValue(target, out var idMap))
            {
                var motions = new List<IMotion>(idMap.Values);
                foreach (var motion in motions)
                {
                    motion.Stop();
                    UnregisterMotion(motion);
                }
            }
        }

        public static void KillMotion(object target, string id)
        {
            var motion = GetMotion(target, id);
            if (motion != null)
            {
                motion.Stop();
                UnregisterMotion(motion);
            }
        }

        public static void CompleteMotions(object target)
        {
            if (_instance == null || target == null) return;

            if (_instance._motionsByTarget.TryGetValue(target, out var idMap))
            {
                var motions = new List<IMotion>(idMap.Values);
                foreach (var motion in motions)
                    CompleteMotionInternal(motion);
            }
        }

        public static void CompleteMotion(object target, string id)
        {
            var motion = GetMotion(target, id);
            if (motion != null)
                CompleteMotionInternal(motion);
        }

        private static void CompleteMotionInternal(IMotion motion)
        {
            // Use pattern matching to handle all ITargetedMotion<T> types
            switch (motion)
            {
                case ITargetedMotion<float> sf:
                    sf.Set(sf.TargetValue);
                    break;
                case ITargetedMotion<Vector2> sv2:
                    sv2.Set(sv2.TargetValue);
                    break;
                case ITargetedMotion<Vector3> sv3:
                    sv3.Set(sv3.TargetValue);
                    break;
                default:
                    motion.Stop();
                    break;
            }

            UnregisterMotion(motion);
        }

        public static void StopAll()
        {
            if (_instance == null) return;

            // Use for loop to avoid allocation from foreach on List
            for (int i = 0; i < _instance._activeSprings.Count; i++)
                _instance._activeSprings[i].Stop();
            for (int i = 0; i < _instance._activeDecays.Count; i++)
                _instance._activeDecays[i].Stop();

            _instance._activeSprings.Clear();
            _instance._activeDecays.Clear();
            _instance._activeSpringSet.Clear();
            _instance._activeDecaySet.Clear();
            _instance._motionsByTarget.Clear();
        }

        #endregion

        #region Stats

        public static int ActiveSpringCount => _instance?._activeSprings.Count ?? 0;
        public static int ActiveDecayCount => _instance?._activeDecays.Count ?? 0;
        public static int TrackedTargetCount => _instance?._motionsByTarget.Count ?? 0;

        #endregion

        #region MonoBehaviour

        void Update()
        {
            float dt = Time.deltaTime;

            // Use for loop to avoid enumeration allocation
            for (int i = 0; i < _activeSprings.Count; i++)
                _activeSprings[i].Advance(dt);

            for (int i = 0; i < _activeDecays.Count; i++)
                _activeDecays[i].Advance(dt);
        }

        void LateUpdate()
        {
            if (_toRemove.Count == 0) return;

            foreach (var motion in _toRemove)
            {
                if (_activeSpringSet.Remove(motion))
                {
                    _activeSprings.Remove(motion);
                }
                if (_activeDecaySet.Remove(motion))
                {
                    _activeDecays.Remove(motion);
                }
            }
            _toRemove.Clear();
        }

        void OnDestroy()
        {
            _instance = null;
        }

        #endregion
    }
}
