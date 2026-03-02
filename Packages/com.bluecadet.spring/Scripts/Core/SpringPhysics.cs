using Unity.Burst;
using Unity.Mathematics;

namespace Bluecadet.Spring
{
    /// <summary>
    /// Unmanaged state for a single spring axis (one float component).
    /// All fields are value types — safe to copy, Burst-compatible.
    /// </summary>
    internal struct SpringAxisState
    {
        public float Value;
        public float Velocity;
        public float TargetValue;

        // Precomputed physics coefficients (recomputed when params change)
        public float Zeta;    // damping ratio
        public float Omega0;  // natural frequency
        public float Omega1;  // underdamped: omega0 * sqrt(1 - zeta^2)
        public float Omega2;  // overdamped:  omega0 * sqrt(zeta^2 - 1)

        public float Precision;
        public byte IsAtRest;  // bool is not blittable in Burst calli context; use byte (0 = false, 1 = true)
    }

    /// <summary>
    /// Unmanaged state for a single decay axis (one float component).
    /// </summary>
    internal struct DecayAxisState
    {
        public float Value;
        public float Velocity;
        public float Friction;
        public float VelocityThreshold;
        public byte IsAtRest;  // bool is not blittable in Burst calli context; use byte (0 = false, 1 = true)
    }

    /// <summary>
    /// Burst-compiled static math for spring and decay simulation.
    /// All methods operate on a single float axis. Multi-dimensional types
    /// (Vector2, Vector3) call these per component via ISpringMath decomposition
    /// in the managed SpringValue/DecayValue wrappers.
    /// </summary>
    [BurstCompile]
    internal static class SpringPhysics
    {
        /// <summary>
        /// Recompute physics coefficients from spring parameters.
        /// Call whenever Damping, Mass, or Stiffness changes.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Fast)]
        internal static void ComputeCoefficients(
            ref SpringAxisState s,
            float damping,
            float mass,
            float stiffness)
        {
            s.Zeta   = damping / (2f * math.sqrt(stiffness * mass));
            s.Omega0 = math.sqrt(stiffness / mass);

            if (s.Zeta < 1f)
            {
                s.Omega1 = s.Omega0 * math.sqrt(1f - s.Zeta * s.Zeta);
                s.Omega2 = 0f;
            }
            else if (s.Zeta > 1f)
            {
                s.Omega1 = 0f;
                s.Omega2 = s.Omega0 * math.sqrt(s.Zeta * s.Zeta - 1f);
            }
            else
            {
                // Critically damped
                s.Omega1 = 0f;
                s.Omega2 = 0f;
            }
        }

        /// <summary>
        /// Advance a spring by deltaTime seconds using exact damped harmonic oscillator integration.
        /// Handles all three damping regimes: underdamped, critically damped, overdamped.
        /// Sets IsAtRest = true when both velocity and displacement are within Precision.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Fast)]
        internal static void AdvanceSpring(ref SpringAxisState s, float dt)
        {
            if (s.IsAtRest != 0) return;

            float v0 = -s.Velocity;
            float x0 = s.TargetValue - s.Value;

            float newValue;
            float newVelocity;

            if (s.Zeta < 1f)
            {
                // Underdamped — oscillates while decaying
                float sin1     = math.sin(s.Omega1 * dt);
                float cos1     = math.cos(s.Omega1 * dt);
                float envelope = math.exp(-s.Zeta * s.Omega0 * dt);

                float bCoeff      = v0 + x0 * (s.Zeta * s.Omega0);
                float displacement = envelope * (x0 * cos1 + bCoeff * (sin1 / s.Omega1));

                newValue = s.TargetValue - displacement;

                float velTerm1 = displacement * (s.Zeta * s.Omega0);
                float velTerm2 = envelope * (bCoeff * cos1 - x0 * (s.Omega1 * sin1));
                newVelocity = velTerm1 - velTerm2;
            }
            else if (s.Zeta > 1f)
            {
                // Overdamped — two exponential terms, no oscillation
                float expPlus  = math.exp((-s.Zeta * s.Omega0 + s.Omega2) * dt);
                float expMinus = math.exp((-s.Zeta * s.Omega0 - s.Omega2) * dt);

                float r1Coeff = s.Zeta * s.Omega0 + s.Omega2;
                float r2Coeff = s.Zeta * s.Omega0 - s.Omega2;
                float denom   = 2f * s.Omega2;

                float coeffA = (x0 * r1Coeff + v0) / denom;
                float coeffB = -(x0 * r2Coeff + v0) / denom;

                float displacement = coeffA * expPlus + coeffB * expMinus;
                newValue = s.TargetValue - displacement;

                float velA = coeffA * (-s.Zeta * s.Omega0 + s.Omega2) * expPlus;
                float velB = coeffB * (-s.Zeta * s.Omega0 - s.Omega2) * expMinus;
                newVelocity = -(velA + velB);
            }
            else
            {
                // Critically damped — fastest approach without overshoot
                float envelope = math.exp(-s.Omega0 * dt);
                float term2    = (v0 + x0 * s.Omega0) * dt;

                float displacement = (x0 + term2) * envelope;
                newValue = s.TargetValue - displacement;

                float velTerm1  = v0 * (dt * s.Omega0 - 1f);
                float velTerm2  = x0 * (dt * s.Omega0 * s.Omega0);
                newVelocity = (velTerm1 + velTerm2) * envelope;
            }

            s.Value    = newValue;
            s.Velocity = newVelocity;

            float speed = math.abs(s.Velocity);
            float dist  = math.abs(s.TargetValue - s.Value);

            if (speed < s.Precision && dist < s.Precision)
            {
                s.Value    = s.TargetValue;
                s.Velocity = 0f;
                s.IsAtRest = 1;
            }
        }

        /// <summary>
        /// Advance a decay by deltaTime seconds using exact exponential integration.
        /// Sets IsAtRest = true when velocity magnitude drops below VelocityThreshold.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Fast)]
        internal static void AdvanceDecay(ref DecayAxisState s, float dt)
        {
            if (s.IsAtRest != 0) return;

            float decayFactor    = math.exp(-s.Friction * dt);
            float integralFactor = (1f - decayFactor) / s.Friction;

            s.Value    += s.Velocity * integralFactor;
            s.Velocity *= decayFactor;

            if (math.abs(s.Velocity) < s.VelocityThreshold)
            {
                s.Velocity = 0f;
                s.IsAtRest = 1;
            }
        }
    }
}
