using System;
using UnityEngine;

namespace Bluecadet.Spring
{
    /// <summary>
    /// Spring animation using damped harmonic oscillator physics.
    /// Supports float, Vector2, Vector3, or custom types via SpringMathRegistry.
    /// </summary>
    public class SpringValue<T> : ITargetedMotion<T> where T : struct
    {
        private static readonly ISpringMath<T> Math = SpringMath<T>.Instance;

        public T Velocity { get; private set; }
        public T Value { get; private set; }
        public T TargetValue { get; private set; }
        public bool IsFinished { get; private set; } = true;

        public object Target { get; private set; }
        public string Id { get; private set; }

        public event Action<SpringValue<T>> OnChange;
        public event Action<SpringValue<T>> OnRest;
        public event Action<SpringValue<T>> OnStart;
        public event Action<IMotion> OnComplete;

        private Action<T> _setter;
        private bool _setterSubscribed;

        /// <summary>
        /// Callback invoked with current value on each update.
        /// </summary>
        public Action<T> Setter
        {
            get => _setter;
            set
            {
                _setter = value;
                if (!_setterSubscribed && value != null)
                {
                    _setterSubscribed = true;
                    OnChange += spring => _setter?.Invoke(spring.Value);
                }
            }
        }

        private float _damping = 26f;
        private float _mass = 1f;
        private float _stiffness = 170f;
        private float _precision = 0.01f;

        private float _zeta;
        private float _omega0;
        private float _omega1; // For underdamped: omega0 * sqrt(1 - zeta^2)
        private float _omega2; // For overdamped: omega0 * sqrt(zeta^2 - 1)

        public float Damping
        {
            get => _damping;
            set => Config(damping: value);
        }

        public float Mass
        {
            get => _mass;
            set => Config(mass: value);
        }

        public float Stiffness
        {
            get => _stiffness;
            set => Config(stiffness: value);
        }

        public float Precision
        {
            get => _precision;
            set => _precision = Mathf.Max(0.0001f, value);
        }

        public SpringValue()
        {
            Velocity = Math.Zero;
            Value = Math.Zero;
            TargetValue = Math.Zero;
            Config();
        }

        #region Fluent Configuration

        /// <summary>
        /// Associate with a target object for DOTween-style queries.
        /// </summary>
        public SpringValue<T> SetTarget(object target, string id = null)
        {
            if (Target != null && Target != target)
            {
                SpringManager.UnregisterMotion(this);
            }

            Target = target;
            Id = id;

            if (target != null)
            {
                SpringManager.RegisterMotion(this);
            }

            return this;
        }

        /// <summary>
        /// Set motion identifier for multiple motions on same target.
        /// Re-registers with SpringManager if already associated with a target.
        /// </summary>
        public SpringValue<T> SetId(string id)
        {
            if (Id == id) return this;

            if (Target != null)
            {
                SpringManager.UnregisterMotion(this);
            }

            Id = id;

            if (Target != null)
            {
                SpringManager.RegisterMotion(this);
            }

            return this;
        }

        /// <summary>
        /// Configure spring parameters. Returns self for chaining.
        /// </summary>
        public SpringValue<T> SetConfig(float? damping = null, float? mass = null, float? stiffness = null, float? precision = null)
        {
            Config(damping, mass, stiffness);
            if (precision.HasValue) Precision = precision.Value;
            return this;
        }

        /// <summary>
        /// Set value setter callback. Returns self for chaining.
        /// </summary>
        public SpringValue<T> SetSetter(Action<T> setter)
        {
            Setter = setter;
            return this;
        }

        #endregion

        /// <summary>
        /// Configure spring parameters. Recomputes physics coefficients.
        /// </summary>
        public SpringValue<T> Config(float? damping = null, float? mass = null, float? stiffness = null)
        {
            if (damping.HasValue) _damping = damping.Value;
            if (mass.HasValue) _mass = Mathf.Max(0.001f, mass.Value);
            if (stiffness.HasValue) _stiffness = Mathf.Max(0.001f, stiffness.Value);

            _zeta = _damping / (2f * Mathf.Sqrt(_stiffness * _mass));
            _omega0 = Mathf.Sqrt(_stiffness / _mass);

            if (_zeta < 1f)
            {
                // Underdamped
                _omega1 = _omega0 * Mathf.Sqrt(1f - _zeta * _zeta);
                _omega2 = 0f;
            }
            else if (_zeta > 1f)
            {
                // Overdamped
                _omega1 = 0f;
                _omega2 = _omega0 * Mathf.Sqrt(_zeta * _zeta - 1f);
            }
            else
            {
                // Critically damped (zeta == 1)
                _omega1 = 0f;
                _omega2 = 0f;
            }

            return this;
        }

        /// <summary>
        /// Update target and continue/restart animation.
        /// </summary>
        public void SetTarget(T target)
        {
            TargetValue = target;
            if (IsFinished)
            {
                IsFinished = false;
                OnStart?.Invoke(this);
                SpringManager.AddActiveSpring(this);
            }
        }

        /// <summary>
        /// Start animating toward target. Optionally set initial velocity and value.
        /// </summary>
        public void Start(T target, T? velocity = null, T? initial = null)
        {
            TargetValue = target;
            if (velocity.HasValue) Velocity = velocity.Value;
            if (initial.HasValue) Value = initial.Value;

            IsFinished = false;
            OnStart?.Invoke(this);
            SpringManager.AddActiveSpring(this);
        }

        /// <summary>
        /// Set value immediately with no animation.
        /// </summary>
        public void Set(T value)
        {
            TargetValue = value;
            Value = value;
            Velocity = Math.Zero;

            bool wasRunning = !IsFinished;
            IsFinished = true;

            OnChange?.Invoke(this);

            if (wasRunning)
            {
                OnRest?.Invoke(this);
                OnComplete?.Invoke(this);
                SpringManager.RemoveActiveSpring(this);
            }
        }

        /// <summary>
        /// Advance simulation by deltaTime seconds.
        /// </summary>
        public void Advance(double deltaTime)
        {
            if (IsFinished) return;

            float t = (float)deltaTime;

            T v0 = Math.Scale(Velocity, -1f);
            T x0 = Math.Subtract(TargetValue, Value);

            T newValue;
            T newVelocity;

            if (_zeta < 1f)
            {
                // Underdamped - oscillates while decaying
                float sin1 = Mathf.Sin(_omega1 * t);
                float cos1 = Mathf.Cos(_omega1 * t);
                float envelope = Mathf.Exp(-_zeta * _omega0 * t);

                T term1 = Math.Scale(x0, cos1);
                T bCoeff = Math.Add(v0, Math.Scale(x0, _zeta * _omega0));
                T term2 = Math.Scale(bCoeff, sin1 / _omega1);
                T displacement = Math.Scale(Math.Add(term1, term2), envelope);

                newValue = Math.Subtract(TargetValue, displacement);

                T velTerm1 = Math.Scale(displacement, _zeta * _omega0);
                T velTerm2Inner = Math.Subtract(Math.Scale(bCoeff, cos1), Math.Scale(x0, _omega1 * sin1));
                T velTerm2 = Math.Scale(velTerm2Inner, envelope);
                newVelocity = Math.Subtract(velTerm1, velTerm2);
            }
            else if (_zeta > 1f)
            {
                // Overdamped - exponential decay without oscillation
                float expPlus = Mathf.Exp((-_zeta * _omega0 + _omega2) * t);
                float expMinus = Mathf.Exp((-_zeta * _omega0 - _omega2) * t);

                // Coefficients from initial conditions
                float aCoeff = (_zeta * _omega0 + _omega2);
                float bCoeff = (_zeta * _omega0 - _omega2);

                // For position: x(t) = (A * e^(r1*t) + B * e^(r2*t)) where r1,r2 are roots
                // Solving initial conditions: A + B = x0, A*r1 + B*r2 = -v0
                float denom = 2f * _omega2;
                T coeffA = Math.Scale(Math.Add(Math.Scale(x0, aCoeff), v0), 1f / denom);
                T coeffB = Math.Scale(Math.Add(Math.Scale(x0, bCoeff), v0), -1f / denom);

                T displacement = Math.Add(Math.Scale(coeffA, expPlus), Math.Scale(coeffB, expMinus));
                newValue = Math.Subtract(TargetValue, displacement);

                // Velocity is derivative
                T velA = Math.Scale(coeffA, (-_zeta * _omega0 + _omega2) * expPlus);
                T velB = Math.Scale(coeffB, (-_zeta * _omega0 - _omega2) * expMinus);
                newVelocity = Math.Scale(Math.Add(velA, velB), -1f);
            }
            else
            {
                // Critically damped (zeta == 1) - fastest decay without oscillation
                float envelope = Mathf.Exp(-_omega0 * t);
                T term1 = x0;
                T term2 = Math.Scale(Math.Add(v0, Math.Scale(x0, _omega0)), t);
                T displacement = Math.Scale(Math.Add(term1, term2), envelope);

                newValue = Math.Subtract(TargetValue, displacement);

                T velTerm1 = Math.Scale(v0, t * _omega0 - 1f);
                T velTerm2 = Math.Scale(x0, t * _omega0 * _omega0);
                newVelocity = Math.Scale(Math.Add(velTerm1, velTerm2), envelope);
            }

            Value = newValue;
            Velocity = newVelocity;

            OnChange?.Invoke(this);

            if (Math.Magnitude(Velocity) < _precision && Math.Distance(TargetValue, Value) < _precision)
            {
                Value = TargetValue;
                Velocity = Math.Zero;
                IsFinished = true;
                OnRest?.Invoke(this);
                OnComplete?.Invoke(this);
                SpringManager.RemoveActiveSpring(this);
            }
        }

        /// <summary>
        /// Stop immediately at current position.
        /// </summary>
        public void Stop()
        {
            if (IsFinished) return;
            IsFinished = true;
            OnRest?.Invoke(this);
            OnComplete?.Invoke(this);
            SpringManager.RemoveActiveSpring(this);
        }
    }
}
