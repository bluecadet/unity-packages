---
title: Springs and decay
description: Reference for SpringValue<T> and DecayValue<T> — builders, binding, events, control, and lifetime.
---

See the [overview](index.md) for install and a quick start. This page covers `SpringValue<T>` and `DecayValue<T>` in full.

Both types are generic over `T : struct` and have no public constructor — create instances through `Spring.Create<T>` and `Spring.CreateDecay<T>`:

```csharp
static SpringValue<T> Create<T>(T initial, T? target = null) where T : struct
static DecayValue<T> CreateDecay<T>(T initial, T? velocity = null) where T : struct
```

## SpringValue\<T>

A damped harmonic oscillator: velocity and position move toward `TargetValue` under configurable stiffness, damping, and mass.

### Properties

| Property | Type | Access | Description |
|---|---|---|---|
| `Value` | `T` | get | Current position |
| `Velocity` | `T` | get | Current velocity |
| `TargetValue` | `T` | get | Value the spring is animating toward |
| `IsFinished` | `bool` | get | Whether the spring has come to rest |
| `Damping` | `float` | get/set | Resistance to motion |
| `Stiffness` | `float` | get/set | Spring force toward target |
| `Mass` | `float` | get/set | Simulated mass |
| `Precision` | `float` | get/set | Rest detection threshold |

### Builders

All `With*` methods return the instance and can be chained before or after `Bind`.

| Method | Description | Default |
|---|---|---|
| `WithDamping(float)` | Resistance to motion | `26` |
| `WithStiffness(float)` | Spring force toward target | `170` |
| `WithMass(float)` | Simulated mass | `1` |
| `WithPrecision(float)` | Rest detection threshold | `0.01` |
| `WithOnComplete(Action)` | Called on natural rest or `Stop()` | — |
| `WithOnStart(Action)` | Called when animation begins | — |
| `WithOnRest(Action)` | Called when velocity reaches zero | — |

### Animation control

```csharp
SpringValue<T> To(T target);                          // returns self; animate toward target, preserve velocity
void SetTarget(T target);                              // retarget without returning self
void Set(T value);                                      // jump immediately, fires OnChange
void Stop();                                             // halt, fires OnRest + OnComplete
void Start(T target, T? velocity = null, T? initial = null); // low-level: set all at once
void Advance(float deltaTime);                           // manually step the simulation
```

`To()` returns the `SpringValue<T>` instance, so it chains directly off `Bind` or off itself.

## DecayValue\<T>

Exponential velocity decay for momentum-based motion (no target — the value coasts to rest based on friction).

### Properties

| Property | Type | Access | Description |
|---|---|---|---|
| `Value` | `T` | get | Current position |
| `Velocity` | `T` | get | Current velocity |
| `IsFinished` | `bool` | get | Whether the decay has come to rest |
| `Friction` | `float` | get/set | Decay rate |
| `VelocityThreshold` | `float` | get/set | Rest detection threshold |

### Builders

| Method | Description | Default |
|---|---|---|
| `WithFriction(float)` | Decay rate | `5` |
| `WithVelocityThreshold(float)` | Rest detection threshold | `0.001` |
| `WithOnComplete(Action)` | Called on natural rest or `Stop()` | — |
| `WithOnStart(Action)` | Called when animation begins | — |
| `WithOnRest(Action)` | Called when velocity reaches zero | — |

### Animation control

```csharp
DecayValue<T> Play(T velocity);       // returns self; start or replace velocity
void Start(T velocity, T? initial = null); // low-level: set velocity and (optionally) position
void AddVelocity(T delta);            // accumulate velocity (e.g. continuous input)
void Set(T value);                    // jump position, clear velocity
void Stop();                          // halt
void Advance(float deltaTime);        // manually step the simulation
```

`Play()` returns the `DecayValue<T>` instance, chainable the same way as `To()`.

## Bind overloads

Four overloads on both types cover simple and allocation-free patterns:

```csharp
// Simple — one closure allocation on bind
Bind(Action<T> setter)

// With instance access (velocity, IsFinished, etc.) — one closure allocation
Bind(Action<T, SpringValue<T>> setter)   // or DecayValue<T>

// Allocation-free — no closure, target passed explicitly each call
Bind<TTarget>(TTarget target, Action<T, TTarget> setter)

// Allocation-free with instance access
Bind<TTarget>(TTarget target, Action<T, TTarget, SpringValue<T>> setter) // or DecayValue<T>
```

The two `Bind<TTarget>` overloads avoid a closure allocation: instead of capturing a variable (e.g. `transform`) in a lambda, they pass it as `target` on every invocation. Use these in performance-sensitive code (e.g. hot paths, many concurrent springs).

```csharp
// Allocates a closure over `transform`
_spring.Bind(x => transform.localPosition = new Vector3(x, 0, 0));

// No closure — transform is passed in, not captured
_spring.Bind(transform, (x, t) => t.localPosition = new Vector3(x, 0, 0));
```

## Events

`OnChange`, `OnStart`, and `OnRest` have no builder method — subscribe to them directly for dynamic add/remove:

```csharp
_spring.OnChange += s => Debug.Log(s.Value);
_spring.OnChange -= handler; // remove when needed
```

| Event | Signature | Fires |
|---|---|---|
| `OnChange` | `Action<SpringValue<T>>` (or `DecayValue<T>`) | Every step the value changes |
| `OnStart` | `Action<SpringValue<T>>` (or `DecayValue<T>`) | When the animation begins |
| `OnRest` | `Action<SpringValue<T>>` (or `DecayValue<T>`) | When velocity reaches zero |
| `OnComplete` | `Action` | On natural rest or `Stop()` |

`OnComplete`, `OnStart`, and `OnRest` are also available as `With*` builder methods; `OnChange` is not.

## Lifetime and pooling

Instances come from a pool and are not auto-returned on completion, so calling `To()` / `Play()` repeatedly on the same instance does not re-allocate.

```csharp
// Explicit release (e.g. in OnDestroy)
Spring.Release(_spring);
Spring.Release(_decay);

// Release every active spring and decay globally
Spring.KillAll();
```

> [!WARNING]
> `Spring.KillAll()` does not fire `OnComplete` or `OnRest` on the instances it releases.

Fire-and-forget instances (no held reference) are garbage-collected normally — no explicit release needed.

## Supported types and custom math

Built-in, Burst-compiled support: `float`, `Vector2`, `Vector3` (backed by the `FloatMath`, `Vector2Math`, and `Vector3Math` readonly structs).

Register a custom type by implementing `ISpringMath<T>` and registering it with `SpringMathRegistry`:

```csharp
public readonly struct MyTypeMath : ISpringMath<MyType>
{
    public MyType Add(MyType a, MyType b) => /* ... */;
    public MyType Subtract(MyType a, MyType b) => /* ... */;
    public MyType Scale(MyType value, float scalar) => /* ... */;
    public float Magnitude(MyType value) => /* ... */;
    public float Distance(MyType a, MyType b) => /* ... */;
    public MyType Zero => /* ... */;
}

SpringMathRegistry.Register<MyType>(new MyTypeMath());
```

## Monitoring active instances

`SpringManager` exposes the count of currently active instances:

```csharp
int active = SpringManager.ActiveSpringCount;
int decaying = SpringManager.ActiveDecayCount;
```
