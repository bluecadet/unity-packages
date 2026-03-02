namespace Bluecadet.Spring
{
    /// <summary>
    /// Primary entry point for creating and managing spring and decay animations.
    ///
    /// <para><b>Basic usage:</b></para>
    /// <code>
/// // Create a spring toward a target
/// // Long-lived handle — configure once, re-target as often as needed.
/// // Velocity is always preserved across To() calls.
/// _spring = Spring.Create(0f)
///     .WithDamping(26f)
///     .WithStiffness(170f)
///     .Bind(x => transform.localPosition = new Vector3(x, 0, 0));
///
/// _spring.To(1f);  // animate to 1
/// _spring.To(2f);  // re-target mid-flight, velocity preserved
///
/// // Allocation-free bind (no closure)
/// Spring.Create(Vector3.zero)
///     .Bind(transform, (v, t) => t.localPosition = v)
///     .To(targetPos);
    ///
    /// // Decay / inertia
    /// _decay = Spring.CreateDecay(Vector2.zero)
    ///     .WithFriction(8f)
    ///     .Bind(v => rb.velocity = v);
    ///
    /// _decay.Play(swipeVelocity);
    /// </code>
    ///
    /// <para><b>Lifetime:</b> instances are safe to hold indefinitely and re-use after completion.
    /// Call <see cref="Release{T}(SpringValue{T})"/> to explicitly return an instance to the pool
    /// when it is no longer needed (e.g. in <c>OnDestroy</c>).</para>
    /// </summary>
    public static class Spring
    {
        // -------------------------------------------------------------------------
        // Factory — springs
        // -------------------------------------------------------------------------

        /// <summary>
        /// Create a spring starting at <paramref name="initial"/>.
        /// If <paramref name="target"/> is provided the spring begins animating immediately;
        /// otherwise it remains idle until <see cref="SpringValue{T}.To(T)"/> is called.
        /// </summary>
        /// <param name="initial">Starting value.</param>
        /// <param name="target">Optional immediate target. Pass null to configure before starting.</param>
        public static SpringValue<T> Create<T>(T initial, T? target = null) where T : struct
        {
            var spring = SpringPool<T>.GetSpring();
            spring.Init(initial, target);
            return spring;
        }

        // -------------------------------------------------------------------------
        // Factory — decays
        // -------------------------------------------------------------------------

        /// <summary>
        /// Create a decay starting at <paramref name="initial"/>.
        /// If <paramref name="velocity"/> is provided the decay begins immediately;
        /// otherwise it remains idle until <see cref="DecayValue{T}.Play"/> is called.
        /// </summary>
        /// <param name="initial">Starting position.</param>
        /// <param name="velocity">Optional initial velocity. Pass null to configure before starting.</param>
        public static DecayValue<T> CreateDecay<T>(T initial, T? velocity = null) where T : struct
        {
            var decay = SpringPool<T>.GetDecay();
            decay.Init(initial, velocity);
            return decay;
        }

        // -------------------------------------------------------------------------
        // Manual release
        // -------------------------------------------------------------------------

        /// <summary>
        /// Return <paramref name="spring"/> to the pool. Call this when the spring is
        /// no longer needed (e.g. in <c>OnDestroy</c>). After this call the reference
        /// should be discarded.
        /// </summary>
        public static void Release<T>(SpringValue<T> spring) where T : struct
            => SpringPool<T>.ReturnSpring(spring);

        /// <summary>
        /// Stop <paramref name="decay"/> and return it to the pool.
        /// </summary>
        public static void Release<T>(DecayValue<T> decay) where T : struct
            => SpringPool<T>.ReturnDecay(decay);

        // -------------------------------------------------------------------------
        // Global control
        // -------------------------------------------------------------------------

        /// <summary>
        /// Stop all active springs and decays immediately and return them to their pools.
        /// Does not fire <c>OnComplete</c> or <c>OnRest</c> callbacks.
        /// All held references become invalid after this call.
        /// </summary>
        public static void KillAll() => SpringManager.Shutdown();
    }
}
