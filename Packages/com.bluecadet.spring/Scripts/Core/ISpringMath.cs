using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Bluecadet.Spring
{
    /// <summary>
    /// Math operations for spring/decay physics.
    /// </summary>
    public interface ISpringMath<T> where T : struct
    {
        T Add(T a, T b);
        T Subtract(T a, T b);
        T Scale(T value, float scalar);
        float Magnitude(T value);
        float Distance(T a, T b);
        T Zero { get; }
    }

    /// <summary>
    /// Registry for looking up math operations by value type.
    /// Caches instances per-type for zero-allocation lookups after first access.
    /// </summary>
    public static class SpringMath<T> where T : struct
    {
        public static readonly ISpringMath<T> Instance;

        static SpringMath()
        {
            Instance = SpringMathRegistry.Get<T>();
            if (Instance == null)
            {
                throw new NotSupportedException(
                    $"SpringValue<{typeof(T).Name}> is not supported. " +
                    "Supported types: float, Vector2, Vector3. " +
                    "Use SpringMathRegistry.Register<T>() to add custom types.");
            }
        }
    }

    /// <summary>
    /// Central registry for spring math implementations. Extend by calling Register.
    /// Note: Register() is not thread-safe. Call it during application initialization
    /// (e.g., in [RuntimeInitializeOnLoadMethod]) before creating any springs.
    /// </summary>
    public static class SpringMathRegistry
    {
        private static readonly Dictionary<Type, object> _registry = new()
        {
            { typeof(float), new FloatMath() },
            { typeof(Vector2), new Vector2Math() },
            { typeof(Vector3), new Vector3Math() },
        };

        /// <summary>
        /// Register a custom math implementation for type T.
        /// </summary>
        public static void Register<T>(ISpringMath<T> math) where T : struct
        {
            _registry[typeof(T)] = math;
        }

        internal static ISpringMath<T> Get<T>() where T : struct
        {
            return _registry.TryGetValue(typeof(T), out var math) ? (ISpringMath<T>)math : null;
        }
    }

    #region Built-in Math Implementations

    public readonly struct FloatMath : ISpringMath<float>
    {
        public float Add(float a, float b) => a + b;
        public float Subtract(float a, float b) => a - b;
        public float Scale(float value, float scalar) => value * scalar;
        public float Magnitude(float value) => math.abs(value);
        public float Distance(float a, float b) => math.abs(a - b);
        public float Zero => 0f;
    }

    public readonly struct Vector2Math : ISpringMath<Vector2>
    {
        public Vector2 Add(Vector2 a, Vector2 b) => a + b;
        public Vector2 Subtract(Vector2 a, Vector2 b) => a - b;
        public Vector2 Scale(Vector2 value, float scalar) => value * scalar;
        public float Magnitude(Vector2 value) => math.length(new float2(value.x, value.y));
        public float Distance(Vector2 a, Vector2 b) => math.length(new float2(a.x - b.x, a.y - b.y));
        public Vector2 Zero => Vector2.zero;
    }

    public readonly struct Vector3Math : ISpringMath<Vector3>
    {
        public Vector3 Add(Vector3 a, Vector3 b) => a + b;
        public Vector3 Subtract(Vector3 a, Vector3 b) => a - b;
        public Vector3 Scale(Vector3 value, float scalar) => value * scalar;
        public float Magnitude(Vector3 value) => math.length(new float3(value.x, value.y, value.z));
        public float Distance(Vector3 a, Vector3 b) => math.length(new float3(a.x - b.x, a.y - b.y, a.z - b.z));
        public Vector3 Zero => Vector3.zero;
    }

    #endregion
}
