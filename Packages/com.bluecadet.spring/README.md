# Bluecadet Spring

Physics-based spring animations and decay/inertia systems for Unity.

## Requirements

- Unity 6000.3+

## Features

- **Spring Animations**: Damped harmonic oscillator physics (underdamped, critically damped, overdamped)
- **Decay/Inertia**: Exponential velocity decay for momentum-based animations
- **Rubberband Effects**: iOS-style elastic boundary constraints
- **Target API**: Target association for easy management of multiple animations

## Quick Start

### Basic Spring Animation

```csharp
using Bluecadet.Spring;

// Animate a value toward a target
Springs.To(targetPosition, v => transform.position = v);
```

### Target-Associated Springs

Associate animations with objects for easy management:

```csharp
// Create/reuse a spring for this object
Springs.To(gameObject, "position", targetPos, v => transform.position = v);

// Will stop any existing "position" spring on this object, then start a new one
Springs.To(gameObject, "position", newTargetPos, v => transform.position = v);

// Query and control
if (Springs.IsTweening(gameObject)) {
    Springs.Kill(gameObject, "position");
}
```

### Decay/Inertia

Start an animation with initial velocity that decays over time:

```csharp
// Fling with initial velocity
Springs.Decay(velocity, v => transform.position = v, startValue: transform.position);

// With target association
Springs.Decay(gameObject, "position", velocity, v => transform.position = v);
```

### Rubberband Boundaries

Apply elastic constraints when values exceed bounds:

```csharp
using Bluecadet.Spring;

// Constrain a value with rubberband effect
float constrained = Rubberband.Apply(value, min: 0f, max: 100f, resistance: 0.2f);

// Check if out of bounds
if (Rubberband.IsOutOfBounds(position, minBounds, maxBounds)) {
    // Snap back to bounds
    Springs.To(gameObject, "position", Rubberband.Clamp(position, minBounds, maxBounds),
               v => transform.position = v);
}

// Dampen velocity when outside bounds (for scroll views)
velocity = Rubberband.DampenVelocity(velocity, position, minBounds, maxBounds);
```

## Supported Types

Built-in support for:
- `float`
- `Vector2`
- `Vector3`

Custom types can be added via `SpringMathRegistry.Register<T>()`.

## Namespace

```csharp
using Bluecadet.Spring;
```
