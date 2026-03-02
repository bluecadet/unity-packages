using System;
using Unity.Mathematics;

namespace Bluecadet.Spring
{
    /// <summary>
    /// Spring animation using damped harmonic oscillator physics.
    /// Obtain instances via <see cref="Spring.Create{T}"/>. Instances are safe to hold
    /// long-term and re-target repeatedly — velocity and acceleration are always preserved.
    ///
    /// Supports float, Vector2, Vector3, or custom types via SpringMathRegistry.
    /// Physics math is delegated to <see cref="SpringPhysics"/> (Burst-compiled for float components).
    /// </summary>
    public class SpringValue<T> : ITargetedMotion<T> where T : struct
    {
        // --- Binding interface (internal) ---

        private interface IBinding
        {
            void Invoke(T value, SpringValue<T> spring);
        }

        private struct SimpleBinding : IBinding
        {
            public Action<T> Setter;
            public void Invoke(T value, SpringValue<T> spring) => Setter?.Invoke(value);
        }

        private struct SimpleBindingWithSpring : IBinding
        {
            public Action<T, SpringValue<T>> Setter;
            public void Invoke(T value, SpringValue<T> spring) => Setter?.Invoke(value, spring);
        }

        private struct TypedBinding<TTarget> : IBinding
        {
            public TTarget Target;
            public Action<T, TTarget> Setter;
            public void Invoke(T value, SpringValue<T> spring) => Setter?.Invoke(value, Target);
        }

        private struct TypedBindingWithSpring<TTarget> : IBinding
        {
            public TTarget Target;
            public Action<T, TTarget, SpringValue<T>> Setter;
            public void Invoke(T value, SpringValue<T> spring) => Setter?.Invoke(value, Target, spring);
        }

        // --- Static math instance (cached per T) ---

        private static readonly ISpringMath<T> Math = SpringMath<T>.Instance;

        // --- Public state ---

        public T    Value       { get; private set; }
        public T    Velocity    { get; private set; }
        public T    TargetValue { get; private set; }
        public bool IsFinished  { get; private set; } = true;

        // --- Events ---

        public event Action<SpringValue<T>> OnChange;
        public event Action<SpringValue<T>> OnRest;
        public event Action<SpringValue<T>> OnStart;
        public event Action                 OnComplete;

        // --- Binding ---

        private IBinding _binding;
        private bool     _bindingSubscribed;

        // --- Per-frame component advance (resolved once at construction, avoids per-frame type checks) ---

        private readonly Action<float> _advanceComponents;

        // --- Physics parameters ---

        private float _damping   = 26f;
        private float _mass      = 1f;
        private float _stiffness = 170f;
        private float _precision = 0.01f;

        // Precomputed coefficients (stored as SpringAxisState for Burst hand-off)
        private SpringAxisState _axisTemplate;

        // -------------------------------------------------------------------------
        // Construction / pool lifecycle
        // -------------------------------------------------------------------------

        internal SpringValue()
        {
            Value       = Math.Zero;
            Velocity    = Math.Zero;
            TargetValue = Math.Zero;
            RecomputeCoefficients();

            // Resolve the correct component-advance path once, avoiding per-frame type checks.
            if (this is SpringValue<float> sf)
                _advanceComponents = dt => AdvanceFloat(sf, dt);
            else if (this is SpringValue<UnityEngine.Vector2> sv2)
                _advanceComponents = dt => AdvanceVector2(sv2, dt);
            else if (this is SpringValue<UnityEngine.Vector3> sv3)
                _advanceComponents = dt => AdvanceVector3(sv3, dt);
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
                SpringManager.RemoveActiveSpring(this);
            }

            Value       = Math.Zero;
            Velocity    = Math.Zero;
            TargetValue = Math.Zero;

            _damping   = 26f;
            _mass      = 1f;
            _stiffness = 170f;
            _precision = 0.01f;
            RecomputeCoefficients();

            OnChange   = null;
            OnRest     = null;
            OnStart    = null;
            OnComplete = null;

            _binding           = null;
            _bindingSubscribed = false;
        }

        /// <summary>
        /// Apply initial state after acquiring from the pool. Called by <see cref="Spring.Create{T}"/>.
        /// </summary>
        internal void Init(T initial, T? target)
        {
            Value       = initial;
            TargetValue = initial;
            Velocity    = Math.Zero;
            IsFinished  = true;

            if (target.HasValue)
                To(target.Value);
        }

        /// <summary>
        /// Called by <see cref="Spring.KillAll"/> via <see cref="IMotion.Release"/>.
        /// </summary>
        void IMotion.Release() => SpringPool<T>.ReturnSpring(this);

        // -------------------------------------------------------------------------
        // Builder API — all methods return `this` for chaining
        // -------------------------------------------------------------------------

        /// <summary>Configure damping (resistance). Default: 26.</summary>
        public SpringValue<T> WithDamping(float damping)
        {
            _damping = damping;
            RecomputeCoefficients();
            return this;
        }

        /// <summary>Configure stiffness (spring force). Default: 170.</summary>
        public SpringValue<T> WithStiffness(float stiffness)
        {
            _stiffness = math.max(0.001f, stiffness);
            RecomputeCoefficients();
            return this;
        }

        /// <summary>Configure mass. Default: 1.</summary>
        public SpringValue<T> WithMass(float mass)
        {
            _mass = math.max(0.001f, mass);
            RecomputeCoefficients();
            return this;
        }

        /// <summary>Configure rest threshold. Default: 0.01.</summary>
        public SpringValue<T> WithPrecision(float precision)
        {
            _precision = math.max(0.0001f, precision);
            return this;
        }

        /// <summary>Subscribe to completion (natural rest or Stop()).</summary>
        public SpringValue<T> WithOnComplete(Action handler)
        {
            OnComplete += handler;
            return this;
        }

        /// <summary>Subscribe to animation start.</summary>
        public SpringValue<T> WithOnStart(Action handler)
        {
            OnStart += _ => handler();
            return this;
        }

        /// <summary>Subscribe to reaching rest (natural completion or Stop()).</summary>
        public SpringValue<T> WithOnRest(Action handler)
        {
            OnRest += _ => handler();
            return this;
        }

        /// <summary>
        /// Bind a setter that receives the current value each frame.
        /// Allocates one delegate (the closure) on first call.
        /// </summary>
        public SpringValue<T> Bind(Action<T> setter)
        {
            _binding = new SimpleBinding { Setter = setter };
            EnsureBindingSubscribed();
            return this;
        }

        /// <summary>
        /// Bind a setter that receives the current value and the full spring each frame.
        /// Use this when you need access to <see cref="Velocity"/> or other spring state.
        /// Allocates one delegate (the closure) on first call.
        /// </summary>
        public SpringValue<T> Bind(Action<T, SpringValue<T>> setter)
        {
            _binding = new SimpleBindingWithSpring { Setter = setter };
            EnsureBindingSubscribed();
            return this;
        }

        /// <summary>
        /// Bind a setter with an explicit target object to avoid closure allocations.
        /// Boxes <see cref="TypedBinding{TTarget}"/> once on call; zero per-frame allocation.
        /// </summary>
        public SpringValue<T> Bind<TTarget>(TTarget target, Action<T, TTarget> setter)
        {
            _binding = new TypedBinding<TTarget> { Target = target, Setter = setter };
            EnsureBindingSubscribed();
            return this;
        }

        /// <summary>
        /// Bind a setter with an explicit target object and full spring context to avoid closure allocations.
        /// Use this when you need access to <see cref="Velocity"/> or other spring state without a closure.
        /// Boxes <see cref="TypedBindingWithSpring{TTarget}"/> once on call; zero per-frame allocation.
        /// </summary>
        public SpringValue<T> Bind<TTarget>(TTarget target, Action<T, TTarget, SpringValue<T>> setter)
        {
            _binding = new TypedBindingWithSpring<TTarget> { Target = target, Setter = setter };
            EnsureBindingSubscribed();
            return this;
        }

        private void EnsureBindingSubscribed()
        {
            if (!_bindingSubscribed)
            {
                _bindingSubscribed = true;
                OnChange += s => _binding?.Invoke(s.Value, s);
            }
        }

        // -------------------------------------------------------------------------
        // Animation control
        // -------------------------------------------------------------------------

        /// <summary>
        /// Animate toward <paramref name="target"/>, preserving current velocity.
        /// Safe to call repeatedly mid-flight — acceleration is always continuous.
        /// Returns self for chaining.
        /// </summary>
        public SpringValue<T> To(T target)
        {
            TargetValue = target;
            if (IsFinished)
            {
                IsFinished = false;
                OnStart?.Invoke(this);
                SpringManager.AddActiveSpring(this);
            }
            return this;
        }

        /// <summary>Implements <see cref="ITargetedMotion{T}"/>. Equivalent to <see cref="To"/>.</summary>
        public void SetTarget(T target) => To(target);

        /// <summary>
        /// Start animating toward target with optional explicit initial velocity and position.
        /// Implements <see cref="ITargetedMotion{T}"/>.
        /// Unlike <see cref="To"/>, this always fires <see cref="OnStart"/> and resets to the
        /// provided values, even if the spring is already running. Prefer <see cref="To"/> for
        /// smooth mid-flight re-targeting.
        /// </summary>
        public void Start(T target, T? velocity = null, T? initial = null)
        {
            TargetValue = target;
            if (velocity.HasValue) Velocity = velocity.Value;
            if (initial.HasValue)  Value    = initial.Value;

            IsFinished = false;
            OnStart?.Invoke(this);
            SpringManager.AddActiveSpring(this);
        }

        /// <summary>
        /// Set value immediately with no animation. Fires OnChange.
        /// </summary>
        public void Set(T value)
        {
            TargetValue = value;
            Value       = value;
            Velocity    = Math.Zero;

            bool wasRunning = !IsFinished;
            IsFinished = true;

            OnChange?.Invoke(this);

            if (wasRunning)
            {
                OnRest?.Invoke(this);
                OnComplete?.Invoke();
                SpringManager.RemoveActiveSpring(this);
            }
        }

        /// <summary>
        /// Stop at current position.
        /// </summary>
        public void Stop()
        {
            if (IsFinished) return;
            IsFinished = true;
            OnRest?.Invoke(this);
            OnComplete?.Invoke();
            SpringManager.RemoveActiveSpring(this);
        }

        // -------------------------------------------------------------------------
        // Direct property accessors (for runtime mutation after setup)
        // -------------------------------------------------------------------------

        public float Damping
        {
            get => _damping;
            set { _damping = value; RecomputeCoefficients(); }
        }

        public float Mass
        {
            get => _mass;
            set { _mass = math.max(0.001f, value); RecomputeCoefficients(); }
        }

        public float Stiffness
        {
            get => _stiffness;
            set { _stiffness = math.max(0.001f, value); RecomputeCoefficients(); }
        }

        public float Precision
        {
            get => _precision;
            set => _precision = math.max(0.0001f, value);
        }

        // -------------------------------------------------------------------------
        // IMotion.Advance — called by SpringManager each frame
        // -------------------------------------------------------------------------

        public void Advance(float deltaTime)
        {
            if (IsFinished) return;

            _advanceComponents(deltaTime);

            OnChange?.Invoke(this);

            if (Math.Magnitude(Velocity) < _precision && Math.Distance(TargetValue, Value) < _precision)
            {
                Value      = TargetValue;
                Velocity   = Math.Zero;
                IsFinished = true;
                OnRest?.Invoke(this);
                OnComplete?.Invoke();
                SpringManager.RemoveActiveSpring(this);
            }
        }

        // -------------------------------------------------------------------------
        // Component-wise physics dispatch
        // -------------------------------------------------------------------------

        private static void AdvanceFloat(SpringValue<float> s, float dt)
        {
            var axis = s.MakeAxisState(s.Value, s.Velocity, s.TargetValue);
            SpringPhysics.AdvanceSpring(ref axis, dt);
            s.Value    = axis.Value;
            s.Velocity = axis.Velocity;
        }

        private static void AdvanceVector2(SpringValue<UnityEngine.Vector2> s, float dt)
        {
            var v   = s.Value;
            var vel = s.Velocity;
            var tgt = s.TargetValue;

            var ax = s.MakeAxisState(v.x, vel.x, tgt.x);
            var ay = s.MakeAxisState(v.y, vel.y, tgt.y);

            SpringPhysics.AdvanceSpring(ref ax, dt);
            SpringPhysics.AdvanceSpring(ref ay, dt);

            s.Value    = new UnityEngine.Vector2(ax.Value,    ay.Value);
            s.Velocity = new UnityEngine.Vector2(ax.Velocity, ay.Velocity);
        }

        private static void AdvanceVector3(SpringValue<UnityEngine.Vector3> s, float dt)
        {
            var v   = s.Value;
            var vel = s.Velocity;
            var tgt = s.TargetValue;

            var ax = s.MakeAxisState(v.x, vel.x, tgt.x);
            var ay = s.MakeAxisState(v.y, vel.y, tgt.y);
            var az = s.MakeAxisState(v.z, vel.z, tgt.z);

            SpringPhysics.AdvanceSpring(ref ax, dt);
            SpringPhysics.AdvanceSpring(ref ay, dt);
            SpringPhysics.AdvanceSpring(ref az, dt);

            s.Value    = new UnityEngine.Vector3(ax.Value,    ay.Value,    az.Value);
            s.Velocity = new UnityEngine.Vector3(ax.Velocity, ay.Velocity, az.Velocity);
        }

        private void AdvanceFallback(float dt)
        {
            T v0 = Math.Scale(Velocity, -1f);
            T x0 = Math.Subtract(TargetValue, Value);

            T newValue;
            T newVelocity;

            float zeta   = _axisTemplate.Zeta;
            float omega0 = _axisTemplate.Omega0;
            float omega1 = _axisTemplate.Omega1;
            float omega2 = _axisTemplate.Omega2;

            if (zeta < 1f)
            {
                float sin1     = (float)System.Math.Sin(omega1 * dt);
                float cos1     = (float)System.Math.Cos(omega1 * dt);
                float envelope = (float)System.Math.Exp(-zeta * omega0 * dt);

                T bCoeff       = Math.Add(v0, Math.Scale(x0, zeta * omega0));
                T displacement = Math.Scale(Math.Add(Math.Scale(x0, cos1), Math.Scale(bCoeff, sin1 / omega1)), envelope);

                newValue = Math.Subtract(TargetValue, displacement);

                T velTerm1  = Math.Scale(displacement, zeta * omega0);
                T velTerm2  = Math.Scale(Math.Subtract(Math.Scale(bCoeff, cos1), Math.Scale(x0, omega1 * sin1)), envelope);
                newVelocity = Math.Subtract(velTerm1, velTerm2);
            }
            else if (zeta > 1f)
            {
                float expPlus  = (float)System.Math.Exp((-zeta * omega0 + omega2) * dt);
                float expMinus = (float)System.Math.Exp((-zeta * omega0 - omega2) * dt);

                float r1Coeff = zeta * omega0 + omega2;
                float r2Coeff = zeta * omega0 - omega2;
                float denom   = 2f * omega2;

                T coeffA = Math.Scale(Math.Add(Math.Scale(x0, r1Coeff), v0), 1f / denom);
                T coeffB = Math.Scale(Math.Add(Math.Scale(x0, r2Coeff), v0), -1f / denom);

                T displacement = Math.Add(Math.Scale(coeffA, expPlus), Math.Scale(coeffB, expMinus));
                newValue = Math.Subtract(TargetValue, displacement);

                T velA = Math.Scale(coeffA, (-zeta * omega0 + omega2) * expPlus);
                T velB = Math.Scale(coeffB, (-zeta * omega0 - omega2) * expMinus);
                newVelocity = Math.Scale(Math.Add(velA, velB), -1f);
            }
            else
            {
                float envelope = (float)System.Math.Exp(-omega0 * dt);
                T term2        = Math.Scale(Math.Add(v0, Math.Scale(x0, omega0)), dt);
                T displacement = Math.Scale(Math.Add(x0, term2), envelope);

                newValue = Math.Subtract(TargetValue, displacement);

                T velTerm1  = Math.Scale(v0, dt * omega0 - 1f);
                T velTerm2  = Math.Scale(x0, dt * omega0 * omega0);
                newVelocity = Math.Scale(Math.Add(velTerm1, velTerm2), envelope);
            }

            Value    = newValue;
            Velocity = newVelocity;
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        private SpringAxisState MakeAxisState(float value, float velocity, float target)
        {
            return new SpringAxisState
            {
                Value       = value,
                Velocity    = velocity,
                TargetValue = target,
                Zeta        = _axisTemplate.Zeta,
                Omega0      = _axisTemplate.Omega0,
                Omega1      = _axisTemplate.Omega1,
                Omega2      = _axisTemplate.Omega2,
                Precision   = _precision,
                IsAtRest    = 0,
            };
        }

        private void RecomputeCoefficients()
        {
            _axisTemplate = new SpringAxisState { Precision = _precision };
            SpringPhysics.ComputeCoefficients(ref _axisTemplate, _damping, _mass, _stiffness);
        }
    }
}
