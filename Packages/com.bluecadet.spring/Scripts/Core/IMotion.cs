using System;

namespace Bluecadet.Spring
{
    /// <summary>
    /// Common interface for all animated values (springs, decays).
    /// Enables unified management and DOTween-style target association.
    /// </summary>
    public interface IMotion
    {
        bool IsFinished { get; }
        void Advance(double deltaTime);
        void Stop();

        /// <summary>
        /// Associated target object (for DOTween-style queries).
        /// </summary>
        object Target { get; }

        /// <summary>
        /// Optional identifier for multiple motions on same target.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Fired when motion completes naturally or is stopped.
        /// </summary>
        event Action<IMotion> OnComplete;
    }

    /// <summary>
    /// Generic motion interface with typed value access.
    /// </summary>
    public interface IMotion<T> : IMotion where T : struct
    {
        T Value { get; }
        T Velocity { get; }

        /// <summary>
        /// Set value immediately with no animation.
        /// </summary>
        void Set(T value);
    }

    /// <summary>
    /// Interface for motions that animate toward a target value (springs).
    /// </summary>
    public interface ITargetedMotion<T> : IMotion<T> where T : struct
    {
        T TargetValue { get; }

        /// <summary>
        /// Update the target and continue/restart animation.
        /// </summary>
        void SetTarget(T target);

        /// <summary>
        /// Start animation toward target with optional initial velocity/value.
        /// </summary>
        void Start(T target, T? velocity = null, T? initial = null);
    }

    /// <summary>
    /// Interface for motions driven by initial velocity (decays).
    /// </summary>
    public interface IVelocityMotion<T> : IMotion<T> where T : struct
    {
        /// <summary>
        /// Start with given velocity from current or specified position.
        /// </summary>
        void Start(T velocity, T? initial = null);

        /// <summary>
        /// Add velocity to current motion.
        /// </summary>
        void AddVelocity(T velocity);
    }
}
