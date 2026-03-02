# Bluecadet Spring

Physics-based spring animations and decay/inertia systems for Unity.

## Requirements

- Unity 6000.3+
- com.unity.burst
- com.unity.mathematics

## Features

- **Spring Animations**: Damped harmonic oscillator physics (underdamped, critically damped, overdamped)
- **Decay/Inertia**: Exponential velocity decay for momentum-based animations
- **Rubberband Effects**: iOS-style elastic boundary constraints
- **Burst-compiled physics**: Per-component math compiled via Unity Burst for built-in types
- **Zero-allocation bindings**: Closure-free bind overloads for performance-sensitive code
- **Smart pooling**: Weak-reference pool — fire-and-forget or long-lived, no friction either way

## Quick Start

### Basic Spring

```csharp
using Bluecadet.Spring;

// Create once (e.g. in Awake), hold as a field
_spring = Spring.Create(0f)
    .WithDamping(26f)
    .WithStiffness(170f)
    .Bind(x => transform.localPosition = new Vector3(x, 0, 0));

// Animate to a target (call anytime — velocity is preserved mid-flight)
_spring.To(1f);

// Re-target smoothly
_spring.To(2f);

// Release when done (e.g. OnDestroy)
Spring.Release(_spring);
```

### Allocation-Free Bind

Avoids closure allocation by passing the target object explicitly:

```csharp
_spring = Spring.Create(Vector3.zero)
    .Bind(transform, (v, t) => t.localPosition = v);

_spring.To(targetPos);
```

### Decay / Inertia

Start an animation with an initial velocity that exponentially decays to rest:

```csharp
_decay = Spring.CreateDecay(Vector2.zero)
    .WithFriction(8f)
    .Bind(v => scrollView.velocity = v);

// Trigger on swipe
_decay.Play(swipeVelocity);

// Add to existing velocity (e.g. continuous input)
_decay.AddVelocity(delta);

Spring.Release(_decay);
```

### Rubberband Boundaries

Apply elastic constraints when values exceed bounds:

```csharp
using Bluecadet.Spring;

// Constrain a value with rubberband effect (e.g. during drag)
float constrained = Rubberband.Apply(value, min: 0f, max: 100f, resistance: 0.55f);

// Snap back to bounds on release
if (Rubberband.IsOutOfBounds(position, 0f, 100f))
    _spring.To(Rubberband.Clamp(position, 0f, 100f));

// Dampen velocity when scrolling past edges
velocity = Rubberband.DampenVelocity(velocity, position, min: 0f, max: 100f);
```

## Builder API

All `With*` methods return `self` and can be chained before or after `Bind`.

### SpringValue\<T\>

| Method | Description | Default |
|--------|-------------|---------|
| `WithDamping(float)` | Resistance to motion | `26` |
| `WithStiffness(float)` | Spring force toward target | `170` |
| `WithMass(float)` | Simulated mass | `1` |
| `WithPrecision(float)` | Rest detection threshold | `0.01` |
| `WithOnComplete(Action)` | Called on natural rest or `Stop()` | — |
| `WithOnStart(Action)` | Called when animation begins | — |
| `WithOnRest(Action)` | Called when velocity reaches zero | — |

### DecayValue\<T\>

| Method | Description | Default |
|--------|-------------|---------|
| `WithFriction(float)` | Decay rate | `5` |
| `WithVelocityThreshold(float)` | Rest detection threshold | `0.001` |
| `WithOnComplete(Action)` | Called on natural rest or `Stop()` | — |
| `WithOnStart(Action)` | Called when animation begins | — |
| `WithOnRest(Action)` | Called when velocity reaches zero | — |

## Bind Overloads

Four overloads cover simple and allocation-free patterns:

```csharp
// Simple — one closure allocation on bind
.Bind(Action<T> setter)

// With spring access (velocity, etc.) — one closure allocation
.Bind(Action<T, SpringValue<T>> setter)

// Allocation-free — no closure
.Bind<TTarget>(TTarget target, Action<T, TTarget> setter)

// Allocation-free with spring access
.Bind<TTarget>(TTarget target, Action<T, TTarget, SpringValue<T>> setter)
```

## Events

Subscribe directly for dynamic add/remove (not available as builder methods):

```csharp
_spring.OnChange += s => Debug.Log(s.Value);
_spring.OnChange -= handler; // remove when needed
```

`OnRest`, `OnStart`, and `OnComplete` also work this way.

## Animation Control

### Spring

```csharp
spring.To(target);                          // animate toward target, preserve velocity
spring.Set(value);                          // jump immediately, fires OnChange
spring.Stop();                              // halt, fires OnRest + OnComplete
spring.Start(target, velocity, initial);    // low-level: set all at once
```

### Decay

```csharp
decay.Play(velocity);       // start or replace velocity
decay.AddVelocity(delta);   // accumulate velocity
decay.Set(value);           // jump position, clear velocity
decay.Stop();               // halt
```

## Lifetime and Pooling

Instances are obtained from a pool and **not** auto-returned on completion, so you can call `To()` / `Play()` repeatedly on the same instance without re-allocating.

```csharp
// Explicit release (call in OnDestroy)
Spring.Release(_spring);
Spring.Release(_decay);

// Release all active instances globally (no callbacks fired)
Spring.KillAll();
```

Fire-and-forget instances (no held reference) are GC'd naturally — no explicit release needed.

## Supported Types

Built-in support (Burst-compiled):
- `float`
- `Vector2`
- `Vector3`

Custom types can be registered:

```csharp
SpringMathRegistry.Register<MyType>(new MyTypeMath());
```

## Namespace

```csharp
using Bluecadet.Spring;
```
