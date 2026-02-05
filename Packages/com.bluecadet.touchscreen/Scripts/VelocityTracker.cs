using UnityEngine;

namespace Bluecadet.Touchscreen {

    /// <summary>
    /// Tracks velocity using a rolling average of recent samples.
    /// Useful for smooth inertia calculations on gesture release.
    /// </summary>
    public class VelocityTracker<T> where T : struct {
        private readonly int sampleCount;
        private readonly T[] samples;
        private readonly float[] timestamps;
        private int currentIndex;
        private int filledCount;

        private readonly System.Func<T, T, T> add;
        private readonly System.Func<T, T, T> subtract;
        private readonly System.Func<T, float, T> scale;
        private readonly T zero;

        /// <summary>
        /// Create a velocity tracker with the specified sample count.
        /// </summary>
        /// <param name="sampleCount">Number of samples to average (default 5)</param>
        /// <param name="add">Function to add two values</param>
        /// <param name="subtract">Function to subtract two values</param>
        /// <param name="scale">Function to scale a value by a float</param>
        /// <param name="zero">Zero value for the type</param>
        public VelocityTracker(
            int sampleCount,
            System.Func<T, T, T> add,
            System.Func<T, T, T> subtract,
            System.Func<T, float, T> scale,
            T zero
        ) {
            this.sampleCount = sampleCount;
            this.samples = new T[sampleCount];
            this.timestamps = new float[sampleCount];
            this.currentIndex = 0;
            this.filledCount = 0;
            this.add = add;
            this.subtract = subtract;
            this.scale = scale;
            this.zero = zero;
        }

        /// <summary>
        /// Track a new position sample. Velocity is computed from position delta over time.
        /// </summary>
        /// <param name="position">Current position</param>
        /// <param name="time">Current time (typically Time.time)</param>
        public void Track(T position, float time) {
            // Get previous sample for velocity calculation
            int prevIndex = (currentIndex - 1 + sampleCount) % sampleCount;

            if (filledCount > 0) {
                float deltaTime = time - timestamps[prevIndex];
                if (deltaTime > 0) {
                    T prevPosition = samples[prevIndex];
                    T delta = subtract(position, prevPosition);
                    T velocity = scale(delta, 1f / deltaTime);

                    // Store velocity in current slot
                    samples[currentIndex] = velocity;
                    timestamps[currentIndex] = time;
                    currentIndex = (currentIndex + 1) % sampleCount;
                    filledCount = Mathf.Min(filledCount + 1, sampleCount);
                }
            } else {
                // First sample - just store position for next delta calculation
                samples[currentIndex] = zero;
                timestamps[currentIndex] = time;
                currentIndex = (currentIndex + 1) % sampleCount;
                filledCount = 1;
            }
        }

        /// <summary>
        /// Track a velocity sample directly (when velocity is already computed).
        /// </summary>
        /// <param name="velocity">Velocity value</param>
        /// <param name="time">Current time</param>
        public void TrackVelocity(T velocity, float time) {
            samples[currentIndex] = velocity;
            timestamps[currentIndex] = time;
            currentIndex = (currentIndex + 1) % sampleCount;
            filledCount = Mathf.Min(filledCount + 1, sampleCount);
        }

        /// <summary>
        /// Get the most recent velocity sample (instantaneous/final velocity).
        /// </summary>
        /// <param name="currentTime">Reference time to calculate sample age from. If negative, returns last sample regardless of age.</param>
        /// <param name="maxAge">Maximum age of sample to consider valid (default 0.1 seconds)</param>
        /// <returns>Most recent velocity, or zero if no samples or sample is too old</returns>
        public T GetLastVelocity(float currentTime = -1f, float maxAge = 0.1f) {
            if (filledCount == 0) return zero;
            int lastIndex = (currentIndex - 1 + sampleCount) % sampleCount;

            if (currentTime >= 0f) {
                float age = currentTime - timestamps[lastIndex];
                if (age > maxAge) return zero;
            }

            return samples[lastIndex];
        }

        /// <summary>
        /// Get the averaged velocity from recent samples.
        /// </summary>
        /// <param name="currentTime">Reference time to calculate sample age from. If negative, uses most recent sample's timestamp.</param>
        /// <param name="maxAge">Maximum age of samples to include (default 0.1 seconds)</param>
        /// <returns>Averaged velocity, or zero if no valid samples</returns>
        public T GetAveragedVelocity(float currentTime = -1f, float maxAge = 0.1f) {
            if (filledCount == 0) return zero;

            if (currentTime < 0f) {
                currentTime = timestamps[(currentIndex - 1 + sampleCount) % sampleCount];
            }
            T sum = zero;
            int count = 0;

            for (int i = 0; i < filledCount; i++) {
                int index = (currentIndex - 1 - i + sampleCount) % sampleCount;
                float age = currentTime - timestamps[index];

                if (age <= maxAge) {
                    sum = add(sum, samples[index]);
                    count++;
                }
            }

            return count > 0 ? scale(sum, 1f / count) : zero;
        }

        /// <summary>
        /// Clear all tracked samples.
        /// </summary>
        public void Clear() {
            currentIndex = 0;
            filledCount = 0;
            for (int i = 0; i < sampleCount; i++) {
                samples[i] = zero;
                timestamps[i] = 0;
            }
        }
    }

    /// <summary>
    /// Pre-configured velocity tracker for Vector2.
    /// </summary>
    public class VelocityTracker2D : VelocityTracker<Vector2> {
        public VelocityTracker2D(int sampleCount = 5) : base(
            sampleCount,
            (a, b) => a + b,
            (a, b) => a - b,
            (v, s) => v * s,
            Vector2.zero
        ) { }
    }

    /// <summary>
    /// Pre-configured velocity tracker for Vector3.
    /// </summary>
    public class VelocityTracker3D : VelocityTracker<Vector3> {
        public VelocityTracker3D(int sampleCount = 5) : base(
            sampleCount,
            (a, b) => a + b,
            (a, b) => a - b,
            (v, s) => v * s,
            Vector3.zero
        ) { }
    }

    /// <summary>
    /// Pre-configured velocity tracker for float.
    /// </summary>
    public class VelocityTracker1D : VelocityTracker<float> {
        public VelocityTracker1D(int sampleCount = 5) : base(
            sampleCount,
            (a, b) => a + b,
            (a, b) => a - b,
            (v, s) => v * s,
            0f
        ) { }
    }
}
