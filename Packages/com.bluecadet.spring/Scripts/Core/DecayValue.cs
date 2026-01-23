using System;
using UnityEngine;

namespace Bluecadet.Spring
{
    /// <summary>
    /// Exponential velocity decay for inertia/momentum effects.
    /// Unlike springs, decay has no target - it just slows down from initial velocity.
    /// Supports float, Vector2, Vector3, or custom types via SpringMathRegistry.
    /// </summary>
    public class DecayValue<T> : IVelocityMotion<T> where T : struct
    {
        private static readonly ISpringMath<T> Math = SpringMath<T>.Instance;

        public T Velocity { get; private set; }
        public T Value { get; private set; }
        public bool IsFinished { get; private set; } = true;

        public object Target { get; private set; }
        public string Id { get; private set; }

        public event Action<DecayValue<T>> OnChange;
        public event Action<DecayValue<T>> OnRest;
        public event Action<DecayValue<T>> OnStart;
        public event Action<IMotion> OnComplete;

        private Action<T> _setter;
        private bool _setterSubscribed;

        public Action<T> Setter
        {
            get => _setter;
            set
            {
                _setter = value;
                if (!_setterSubscribed && value != null)
                {
                    _setterSubscribed = true;
                    OnChange += decay => _setter?.Invoke(decay.Value);
                }
            }
        }

        private float _friction = 5f;
        private float _velocityThreshold = 0.001f;

        public float Friction
        {
            get => _friction;
            set => _friction = Mathf.Max(0.001f, value);
        }

        public float VelocityThreshold
        {
            get => _velocityThreshold;
            set => _velocityThreshold = Mathf.Max(0.0001f, value);
        }

        public DecayValue()
        {
            Velocity = Math.Zero;
            Value = Math.Zero;
        }

        #region Fluent Configuration

        /// <summary>
        /// Associate with a target object for DOTween-style queries.
        /// </summary>
        public DecayValue<T> SetTarget(object target, string id = null)
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
        /// Set motion identifier.
        /// Re-registers with SpringManager if already associated with a target.
        /// </summary>
        public DecayValue<T> SetId(string id)
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
        /// Configure decay parameters. Returns self for chaining.
        /// </summary>
        public DecayValue<T> SetConfig(float? friction = null, float? velocityThreshold = null)
        {
            if (friction.HasValue) Friction = friction.Value;
            if (velocityThreshold.HasValue) VelocityThreshold = velocityThreshold.Value;
            return this;
        }

        /// <summary>
        /// Set value setter callback. Returns self for chaining.
        /// </summary>
        public DecayValue<T> SetSetter(Action<T> setter)
        {
            Setter = setter;
            return this;
        }

        #endregion

        /// <summary>
        /// Start decay from current position with given velocity.
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
        /// Set position immediately with no velocity.
        /// </summary>
        public void Set(T value)
        {
            Value = value;
            Velocity = Math.Zero;

            bool wasRunning = !IsFinished;
            IsFinished = true;

            OnChange?.Invoke(this);

            if (wasRunning)
            {
                OnRest?.Invoke(this);
                OnComplete?.Invoke(this);
                SpringManager.RemoveActiveDecay(this);
            }
        }

        /// <summary>
        /// Advance simulation by deltaTime seconds.
        /// </summary>
        public void Advance(double deltaTime)
        {
            if (IsFinished) return;

            float t = (float)deltaTime;

            float decayFactor = Mathf.Exp(-_friction * t);
            T newVelocity = Math.Scale(Velocity, decayFactor);

            // Accurate position integration
            float integralFactor = (1f - decayFactor) / _friction;
            T displacement = Math.Scale(Velocity, integralFactor);

            Value = Math.Add(Value, displacement);
            Velocity = newVelocity;

            OnChange?.Invoke(this);

            if (Math.Magnitude(Velocity) < _velocityThreshold)
            {
                Velocity = Math.Zero;
                IsFinished = true;
                OnRest?.Invoke(this);
                OnComplete?.Invoke(this);
                SpringManager.RemoveActiveDecay(this);
            }
        }

        /// <summary>
        /// Stop immediately.
        /// </summary>
        public void Stop()
        {
            if (IsFinished) return;
            Velocity = Math.Zero;
            IsFinished = true;
            OnRest?.Invoke(this);
            OnComplete?.Invoke(this);
            SpringManager.RemoveActiveDecay(this);
        }
    }
}
