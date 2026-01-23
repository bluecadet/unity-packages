using UnityEngine;

namespace Bluecadet.Spring
{
    /// <summary>
    /// Static utilities for rubberband/elastic boundary effects.
    /// Useful for scroll views, drag constraints, and similar UI patterns.
    /// </summary>
    public static class Rubberband
    {
        /// <summary>
        /// Apply rubberband resistance when value exceeds bounds.
        /// Returns the constrained value with elastic falloff.
        /// </summary>
        /// <param name="value">Current value</param>
        /// <param name="min">Minimum bound</param>
        /// <param name="max">Maximum bound</param>
        /// <param name="resistance">Resistance factor (0-1). Higher = more resistance. Default 0.55</param>
        /// <param name="maxOvershoot">Maximum distance past bounds. Default: infinite</param>
        public static float Apply(float value, float min, float max, float resistance = 0.55f, float maxOvershoot = float.MaxValue)
        {
            if (value < min)
            {
                float overshoot = min - value;
                float dampedOvershoot = DampenOvershoot(overshoot, resistance, maxOvershoot);
                return min - dampedOvershoot;
            }
            if (value > max)
            {
                float overshoot = value - max;
                float dampedOvershoot = DampenOvershoot(overshoot, resistance, maxOvershoot);
                return max + dampedOvershoot;
            }
            return value;
        }

        /// <summary>
        /// Apply rubberband to Vector2 within rectangular bounds.
        /// </summary>
        public static Vector2 Apply(Vector2 value, Vector2 min, Vector2 max, float resistance = 0.55f, float maxOvershoot = float.MaxValue)
        {
            return new Vector2(
                Apply(value.x, min.x, max.x, resistance, maxOvershoot),
                Apply(value.y, min.y, max.y, resistance, maxOvershoot)
            );
        }

        /// <summary>
        /// Apply rubberband to Vector3 within box bounds.
        /// </summary>
        public static Vector3 Apply(Vector3 value, Vector3 min, Vector3 max, float resistance = 0.55f, float maxOvershoot = float.MaxValue)
        {
            return new Vector3(
                Apply(value.x, min.x, max.x, resistance, maxOvershoot),
                Apply(value.y, min.y, max.y, resistance, maxOvershoot),
                Apply(value.z, min.z, max.z, resistance, maxOvershoot)
            );
        }

        /// <summary>
        /// Check if value is outside bounds.
        /// </summary>
        public static bool IsOutOfBounds(float value, float min, float max)
        {
            return value < min || value > max;
        }

        /// <summary>
        /// Check if any component is outside bounds.
        /// </summary>
        public static bool IsOutOfBounds(Vector2 value, Vector2 min, Vector2 max)
        {
            return IsOutOfBounds(value.x, min.x, max.x) || IsOutOfBounds(value.y, min.y, max.y);
        }

        /// <summary>
        /// Check if any component is outside bounds.
        /// </summary>
        public static bool IsOutOfBounds(Vector3 value, Vector3 min, Vector3 max)
        {
            return IsOutOfBounds(value.x, min.x, max.x) ||
                   IsOutOfBounds(value.y, min.y, max.y) ||
                   IsOutOfBounds(value.z, min.z, max.z);
        }

        /// <summary>
        /// Get the nearest point within bounds.
        /// </summary>
        public static float Clamp(float value, float min, float max)
        {
            return Mathf.Clamp(value, min, max);
        }

        /// <summary>
        /// Get the nearest point within bounds.
        /// </summary>
        public static Vector2 Clamp(Vector2 value, Vector2 min, Vector2 max)
        {
            return new Vector2(
                Mathf.Clamp(value.x, min.x, max.x),
                Mathf.Clamp(value.y, min.y, max.y)
            );
        }

        /// <summary>
        /// Get the nearest point within bounds.
        /// </summary>
        public static Vector3 Clamp(Vector3 value, Vector3 min, Vector3 max)
        {
            return new Vector3(
                Mathf.Clamp(value.x, min.x, max.x),
                Mathf.Clamp(value.y, min.y, max.y),
                Mathf.Clamp(value.z, min.z, max.z)
            );
        }

        /// <summary>
        /// Calculate the overshoot amount past bounds. Returns 0 if within bounds.
        /// </summary>
        public static float GetOvershoot(float value, float min, float max)
        {
            if (value < min) return min - value;
            if (value > max) return value - max;
            return 0f;
        }

        /// <summary>
        /// Calculate the overshoot vector past bounds.
        /// </summary>
        public static Vector2 GetOvershoot(Vector2 value, Vector2 min, Vector2 max)
        {
            return new Vector2(
                GetOvershoot(value.x, min.x, max.x),
                GetOvershoot(value.y, min.y, max.y)
            );
        }

        /// <summary>
        /// Dampen velocity when outside bounds. Useful for slowing down scrolling past edges.
        /// </summary>
        /// <param name="velocity">Current velocity</param>
        /// <param name="value">Current position</param>
        /// <param name="min">Minimum bound</param>
        /// <param name="max">Maximum bound</param>
        /// <param name="dampFactor">Velocity multiplier when outside bounds (0-1)</param>
        public static float DampenVelocity(float velocity, float value, float min, float max, float dampFactor = 0.5f)
        {
            if ((value < min && velocity < 0) || (value > max && velocity > 0))
            {
                return velocity * dampFactor;
            }
            return velocity;
        }

        /// <summary>
        /// Dampen velocity vector when outside bounds.
        /// </summary>
        public static Vector2 DampenVelocity(Vector2 velocity, Vector2 value, Vector2 min, Vector2 max, float dampFactor = 0.5f)
        {
            return new Vector2(
                DampenVelocity(velocity.x, value.x, min.x, max.x, dampFactor),
                DampenVelocity(velocity.y, value.y, min.y, max.y, dampFactor)
            );
        }

        // Logarithmic dampening for natural rubber-band feel
        private static float DampenOvershoot(float overshoot, float resistance, float maxOvershoot)
        {
            // Using logarithmic formula similar to iOS rubber-banding:
            // damped = (1 - (1 / ((x * c / d) + 1))) * d
            // where c = resistance constant, d = dimension/max distance
            float dimension = maxOvershoot < float.MaxValue ? maxOvershoot : 1000f;
            float c = (1f - resistance) * 0.5f; // Invert resistance so higher values = more resistance
            float damped = (1f - (1f / ((overshoot * c / dimension) + 1f))) * dimension;
            return Mathf.Min(damped, maxOvershoot);
        }
    }
}
