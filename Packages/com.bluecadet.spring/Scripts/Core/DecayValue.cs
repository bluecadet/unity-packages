using System;
using Unity.Mathematics;

namespace Bluecadet.Spring
{
    /// <summary>
    /// Exponential velocity decay for inertia/momentum effects.
    /// Obtain instances via <see cref="Spring.CreateDecay{T}"/>. Instances are safe to hold
    /// long-term and re-triggered repeatedly via <see cref="AddVelocity"/> or <see cref="Play"/>.
    ///
    /// Unlike springs, decay has no target — it slows down from an initial velocity.
    /// Supports float, Vector2, Vector3, or custom types via SpringMathRegistry.
    /// Physics math is delegated to <see cref="SpringPhysics"/> (Burst-compiled for float components).
    /// </summary>
    public class DecayValue<T> : IVelocityMotion<T> where T : struct
    {
        // --- Binding interface (internal) ---

        private interface IBinding
        {
            void Invoke(T value, DecayValue<T> decay);
        }

        private struct SimpleBinding : IBinding
        {
            public Action<T> Setter;
            public void Invoke(T value, DecayValue<T> decay) => Setter?.Invoke(value);
        }

        private struct SimpleBindingWithDecay : IBinding
        {
            public Action<T, DecayValue<T>> Setter;
            public void Invoke(T value, DecayValue<T> decay) => Setter?.Invoke(value, decay);
        }

        private struct TypedBinding<TTarget> : IBinding
        {
            public TTarget Target;
            public Action<T, TTarget> Setter;
            public void Invoke(T value, DecayValue<T> decay) => Setter?.Invoke(value, Target);
        }

        private struct TypedBindingWithDecay<TTarget> : IBinding
        {
            public TTarget Target;
            public Action<T, TTarget, DecayValue<T>> Setter;
            public void Invoke(T value, DecayValue<T> decay) => Setter?.Invoke(value, Target, decay);
        }

        // --- Static math instance (cached per T) ---

        private static readonly ISpringMath<T> Math = SpringMath<T>.Instance;

        // --- Public state ---

        public T    Value      { get; private set; }
        public T    Velocity   { get; private set; }
        public bool IsFinished { get; private set; } = true;

        // --- Events ---

        public event Action<DecayValue<T>> OnChange;
        public event Action<DecayValue<T>> OnRest;
        public event Action<DecayValue<T>> OnStart;
        public event Action                OnComplete;

        // --- Binding ---

        private IBinding _binding;
        private bool     _bindingSubscribed;

        // --- Per-frame component advance (resolved once at construction, avoids per-frame type checks) ---

        private readonly Action<float> _advanceComponents;

        // --- Physics parameters ---

        private float _friction          = 5f;
        private float _velocityThreshold = 0.001f;

        // -------------------------------------------------------------------------
        // Construction / pool lifecycle
        // -------------------------------------------------------------------------

        internal DecayValue()
        {
            Value    = Math.Zero;
            Velocity = Math.Zero;

            // Resolve the correct component-advance path once, avoiding per-frame type checks.
            if (this is DecayValue<float> df)
                _advanceComponents = dt => AdvanceFloat(df, dt);
            else if (this is DecayValue<UnityEngine.Vector2> dv2)
                _advanceComponents = dt => AdvanceVector2(dv2, dt);
            else if (this is DecayValue<UnityEngine.Vector3> dv3)
                _advanceComponents = dt => AdvanceVector3(dv3, dt);
            else
                _advanceComponents = AdvanceFallback;
        }

        /// <summary>
        /// Reset all state and clear all subscribers. Called by <see cref="Spring.Release"/>.
        /// After this call the instance is returned to the pool — do not use the reference again.
        /// </summary>
        internal void ResetState()
        {
            if (!IsFinished)
            {
                IsFinished = true;
                SpringManager.RemoveActiveDecay(this);
            }

            Value    = Math.Zero;
            Velocity = Math.Zero;

            _friction          = 5f;
            _velocityThreshold = 0.001f;

            OnChange   = null;
            OnRest     = null;
            OnStart    = null;
            OnComplete = null;

            _binding           = null;
            _bindingSubscribed = false;
        }

        /// <summary>
        /// Apply initial state after acquiring from the pool. Called by <see cref="Spring.CreateDecay{T}"/>.
        /// </summary>
        internal void Init(T initial, T? velocity)
        {
            Value      = initial;
            Velocity   = Math.Zero;
            IsFinished = true;

            if (velocity.HasValue)
                Play(velocity.Value);
        }

        /// <summary>
        /// Called by <see cref="Spring.KillAll"/> via <see cref="IMotion.Release"/>.
        /// </summary>
        void IMotion.Release() => SpringPool<T>.ReturnDecay(this);

        // -------------------------------------------------------------------------
        // Builder API
        // -------------------------------------------------------------------------

        /// <summary>Configure friction (decay rate). Default: 5.</summary>
        public DecayValue<T> WithFriction(float friction)
        {
            _friction = math.max(0.001f, friction);
            return this;
        }

        /// <summary>Configure the velocity threshold for rest detection. Default: 0.001.</summary>
        public DecayValue<T> WithVelocityThreshold(float threshold)
        {
            _velocityThreshold = math.max(0.0001f, threshold);
            return this;
        }

        /// <summary>Subscribe to completion (natural rest or Stop()).</summary>
        public DecayValue<T> WithOnComplete(Action handler)
        {
            OnComplete += handler;
            return this;
        }

        /// <summary>Subscribe to animation start.</summary>
        public DecayValue<T> WithOnStart(Action handler)
        {
            OnStart += _ => handler();
            return this;
        }

        /// <summary>Subscribe to reaching rest (natural completion or Stop()).</summary>
        public DecayValue<T> WithOnRest(Action handler)
        {
            OnRest += _ => handler();
            return this;
        }

        /// <summary>
        /// Bind a setter that receives the current value each frame.
        /// Allocates one delegate (the closure) on first call.
        /// </summary>
        public DecayValue<T> Bind(Action<T> setter)
        {
            _binding = new SimpleBinding { Setter = setter };
            EnsureBindingSubscribed();
            return this;
        }

        /// <summary>
        /// Bind a setter that receives the current value and the full decay each frame.
        /// Use this when you need access to <see cref="Velocity"/> or other decay state.
        /// Allocates one delegate (the closure) on first call.
        /// </summary>
        public DecayValue<T> Bind(Action<T, DecayValue<T>> setter)
        {
            _binding = new SimpleBindingWithDecay { Setter = setter };
            EnsureBindingSubscribed();
            return this;
        }

        /// <summary>
        /// Bind a setter with an explicit target object to avoid closure allocations.
        /// Boxes <see cref="TypedBinding{TTarget}"/> once on call; zero per-frame allocation.
        /// </summary>
        public DecayValue<T> Bind<TTarget>(TTarget target, Action<T, TTarget> setter)
        {
            _binding = new TypedBinding<TTarget> { Target = target, Setter = setter };
            EnsureBindingSubscribed();
            return this;
        }

        /// <summary>
        /// Bind a setter with an explicit target object and full decay context to avoid closure allocations.
        /// Use this when you need access to <see cref="Velocity"/> or other decay state without a closure.
        /// Boxes <see cref="TypedBindingWithDecay{TTarget}"/> once on call; zero per-frame allocation.
        /// </summary>
        public DecayValue<T> Bind<TTarget>(TTarget target, Action<T, TTarget, DecayValue<T>> setter)
        {
            _binding = new TypedBindingWithDecay<TTarget> { Target = target, Setter = setter };
            EnsureBindingSubscribed();
            return this;
        }

        private void EnsureBindingSubscribed()
        {
            if (!_bindingSubscribed)
            {
                _bindingSubscribed = true;
                OnChange += d => _binding?.Invoke(d.Value, d);
            }
        }

        // -------------------------------------------------------------------------
        // Animation control
        // -------------------------------------------------------------------------

        /// <summary>
        /// Start decay from current position with given velocity. Returns self for chaining.
        /// </summary>
        public DecayValue<T> Play(T velocity)
        {
            Velocity = velocity;

            if (IsFinished && Math.Magnitude(velocity) > _velocityThreshold)
            {
                IsFinished = false;
                OnStart?.Invoke(this);
                SpringManager.AddActiveDecay(this);
            }
            return this;
        }

        /// <summary>
        /// Start decay from given position with given velocity.
        /// Implements <see cref="IVelocityMotion{T}"/>.
        /// Unlike <see cref="Play"/>, this always fires <see cref="OnStart"/> and resets to the
        /// provided values, even if the decay is already running. Prefer <see cref="Play"/> for
        /// smooth mid-flight velocity replacement.
        /// </summary>
        public void Start(T velocity, T? initial = null)
        {
            Velocity = velocity;
            if (initial.HasValue) Value = initial.Value;

            IsFinished = false;
            OnStart?.Invoke(this);
            SpringManager.AddActiveDecay(this);
        }

        /// <summary>
        /// Add velocity to current motion, restarting if finished.
        /// </summary>
        public void AddVelocity(T velocity)
        {
            Velocity = Math.Add(Velocity, velocity);

            if (IsFinished && Math.Magnitude(Velocity) > _velocityThreshold)
            {
                IsFinished = false;
                OnStart?.Invoke(this);
                SpringManager.AddActiveDecay(this);
            }
        }

        /// <summary>
        /// Set position immediately with no velocity. Fires OnChange.
        /// </summary>
        public void Set(T value)
        {
            Value    = value;
            Velocity = Math.Zero;

            bool wasRunning = !IsFinished;
            IsFinished = true;

            OnChange?.Invoke(this);

            if (wasRunning)
            {
                OnRest?.Invoke(this);
                OnComplete?.Invoke();
                SpringManager.RemoveActiveDecay(this);
            }
        }

        /// <summary>
        /// Stop immediately.
        /// </summary>
        public void Stop()
        {
            if (IsFinished) return;
            Velocity   = Math.Zero;
            IsFinished = true;
            OnRest?.Invoke(this);
            OnComplete?.Invoke();
            SpringManager.RemoveActiveDecay(this);
        }

        // -------------------------------------------------------------------------
        // Direct property accessors
        // -------------------------------------------------------------------------

        public float Friction
        {
            get => _friction;
            set => _friction = math.max(0.001f, value);
        }

        public float VelocityThreshold
        {
            get => _velocityThreshold;
            set => _velocityThreshold = math.max(0.0001f, value);
        }

        // -------------------------------------------------------------------------
        // IMotion.Advance — called by SpringManager each frame
        // -------------------------------------------------------------------------

        public void Advance(float deltaTime)
        {
            if (IsFinished) return;

            _advanceComponents(deltaTime);

            OnChange?.Invoke(this);

            if (Math.Magnitude(Velocity) < _velocityThreshold)
            {
                Velocity   = Math.Zero;
                IsFinished = true;
                OnRest?.Invoke(this);
                OnComplete?.Invoke();
                SpringManager.RemoveActiveDecay(this);
            }
        }

        // -------------------------------------------------------------------------
        // Component-wise physics dispatch
        // -------------------------------------------------------------------------

        private static void AdvanceFloat(DecayValue<float> d, float dt)
        {
            var axis = MakeAxisState(d.Value, d.Velocity, d._friction, d._velocityThreshold);
            SpringPhysics.AdvanceDecay(ref axis, dt);
            d.Value    = axis.Value;
            d.Velocity = axis.Velocity;
        }

        private static void AdvanceVector2(DecayValue<UnityEngine.Vector2> d, float dt)
        {
            var v   = d.Value;
            var vel = d.Velocity;

            var ax = MakeAxisState(v.x, vel.x, d._friction, d._velocityThreshold);
            var ay = MakeAxisState(v.y, vel.y, d._friction, d._velocityThreshold);

            SpringPhysics.AdvanceDecay(ref ax, dt);
            SpringPhysics.AdvanceDecay(ref ay, dt);

            d.Value    = new UnityEngine.Vector2(ax.Value,    ay.Value);
            d.Velocity = new UnityEngine.Vector2(ax.Velocity, ay.Velocity);
        }

        private static void AdvanceVector3(DecayValue<UnityEngine.Vector3> d, float dt)
        {
            var v   = d.Value;
            var vel = d.Velocity;

            var ax = MakeAxisState(v.x, vel.x, d._friction, d._velocityThreshold);
            var ay = MakeAxisState(v.y, vel.y, d._friction, d._velocityThreshold);
            var az = MakeAxisState(v.z, vel.z, d._friction, d._velocityThreshold);

            SpringPhysics.AdvanceDecay(ref ax, dt);
            SpringPhysics.AdvanceDecay(ref ay, dt);
            SpringPhysics.AdvanceDecay(ref az, dt);

            d.Value    = new UnityEngine.Vector3(ax.Value,    ay.Value,    az.Value);
            d.Velocity = new UnityEngine.Vector3(ax.Velocity, ay.Velocity, az.Velocity);
        }

        private void AdvanceFallback(float dt)
        {
            float decayFactor    = (float)System.Math.Exp(-_friction * dt);
            float integralFactor = (1f - decayFactor) / _friction;

            Value    = Math.Add(Value, Math.Scale(Velocity, integralFactor));
            Velocity = Math.Scale(Velocity, decayFactor);
        }

        private static DecayAxisState MakeAxisState(
            float value, float velocity, float friction, float threshold)
        {
            return new DecayAxisState
            {
                Value             = value,
                Velocity          = velocity,
                Friction          = friction,
                VelocityThreshold = threshold,
                IsAtRest          = 0,
            };
        }
    }
}
