using System;
using System.Collections.Generic;
using UnityEngine;

namespace Bluecadet.Spring
{
    /// <summary>
    /// Static API for creating and managing springs/decays with DOTween-style target association.
    /// </summary>
    public static class Springs
    {
        #region Factory Methods - Springs

        /// <summary>
        /// Create a spring and start animating toward endValue.
        /// </summary>
        public static SpringValue<T> To<T>(T endValue, Action<T> setter, T? startValue = null) where T : struct
        {
            var spring = new SpringValue<T>();
            spring.Setter = setter;
            spring.Start(endValue, initial: startValue);
            return spring;
        }

        /// <summary>
        /// Create a spring associated with a target. Reuses existing spring if one exists for this target/id.
        /// </summary>
        public static SpringValue<T> To<T>(object target, string id, T endValue, Action<T> setter, T? startValue = null) where T : struct
        {
            var existing = SpringManager.GetMotion<SpringValue<T>>(target, id);
            if (existing != null)
            {
                existing.SetTarget(endValue);
                return existing;
            }

            var spring = new SpringValue<T>();
            spring.SetTarget(target, id);
            spring.Setter = setter;
            spring.Start(endValue, initial: startValue);
            return spring;
        }

        #endregion

        #region Factory Methods - Decay

        /// <summary>
        /// Create a decay with initial velocity.
        /// </summary>
        public static DecayValue<T> Decay<T>(T velocity, Action<T> setter, T? startValue = null) where T : struct
        {
            var decay = new DecayValue<T>();
            decay.Setter = setter;
            decay.Start(velocity, initial: startValue);
            return decay;
        }

        /// <summary>
        /// Create a decay associated with a target. Adds velocity to existing decay if one exists.
        /// </summary>
        public static DecayValue<T> Decay<T>(object target, string id, T velocity, Action<T> setter, T? startValue = null) where T : struct
        {
            var existing = SpringManager.GetMotion<DecayValue<T>>(target, id);
            if (existing != null)
            {
                existing.AddVelocity(velocity);
                return existing;
            }

            var decay = new DecayValue<T>();
            decay.SetTarget(target, id);
            decay.Setter = setter;
            decay.Start(velocity, initial: startValue);
            return decay;
        }

        #endregion

        #region Query Methods

        /// <summary>
        /// Get a motion by target and optional id.
        /// </summary>
        public static IMotion Get(object target, string id = null) => SpringManager.GetMotion(target, id);

        /// <summary>
        /// Get a typed motion by target and optional id.
        /// </summary>
        public static T Get<T>(object target, string id = null) where T : class, IMotion => SpringManager.GetMotion<T>(target, id);

        /// <summary>
        /// Get all motions associated with a target.
        /// </summary>
        public static IEnumerable<IMotion> GetAll(object target) => SpringManager.GetAllMotions(target);

        /// <summary>
        /// Check if target has any active motions.
        /// </summary>
        public static bool IsTweening(object target) => SpringManager.HasActiveMotions(target);

        /// <summary>
        /// Check if target has a specific motion.
        /// </summary>
        public static bool IsTweening(object target, string id) => SpringManager.GetMotion(target, id) != null;

        #endregion

        #region Control Methods

        /// <summary>
        /// Kill all motions on a target.
        /// </summary>
        public static void Kill(object target) => SpringManager.KillMotions(target);

        /// <summary>
        /// Kill a specific motion on a target.
        /// </summary>
        public static void Kill(object target, string id) => SpringManager.KillMotion(target, id);

        /// <summary>
        /// Complete all motions on a target (jump to end value).
        /// </summary>
        public static void Complete(object target) => SpringManager.CompleteMotions(target);

        /// <summary>
        /// Complete a specific motion on a target.
        /// </summary>
        public static void Complete(object target, string id) => SpringManager.CompleteMotion(target, id);

        /// <summary>
        /// Kill all active motions.
        /// </summary>
        public static void KillAll() => SpringManager.StopAll();

        #endregion
    }
}
