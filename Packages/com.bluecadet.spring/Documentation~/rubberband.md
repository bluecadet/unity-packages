---
title: Rubberband
description: Reference for the static Rubberband API — elastic boundary resistance, clamping, and overshoot.
---

See the [overview](index.md) for install, and [springs and decay](springs-and-decay.md) for `SpringValue<T>`/`DecayValue<T>`. `Rubberband` is a static class with `float`, `Vector2`, and `Vector3` overloads (except where noted) for elastic boundary constraints — the iOS-style resistance that increases as a value moves past its bounds.

## Apply

Resists a value past `min`/`max`, easing toward `maxOvershoot` as the value moves further out of bounds.

```csharp
static float Apply(float value, float min, float max, float resistance = 0.55f, float maxOvershoot = float.MaxValue)
static Vector2 Apply(Vector2 value, Vector2 min, Vector2 max, float resistance = 0.55f, float maxOvershoot = float.MaxValue)
static Vector3 Apply(Vector3 value, Vector3 min, Vector3 max, float resistance = 0.55f, float maxOvershoot = float.MaxValue)
```

| Parameter | Default | Description |
|---|---|---|
| `resistance` | `0.55` | Higher values resist overshoot more; lower values allow more travel past bounds |
| `maxOvershoot` | `float.MaxValue` | Caps how far the result can move past `min`/`max` |

```csharp
float constrained = Rubberband.Apply(value, min: 0f, max: 100f, resistance: 0.55f);
```

## IsOutOfBounds

```csharp
static bool IsOutOfBounds(float value, float min, float max)
static bool IsOutOfBounds(Vector2 value, Vector2 min, Vector2 max)
static bool IsOutOfBounds(Vector3 value, Vector3 min, Vector3 max)
```

## Clamp

```csharp
static float Clamp(float value, float min, float max)
static Vector2 Clamp(Vector2 value, Vector2 min, Vector2 max)
static Vector3 Clamp(Vector3 value, Vector3 min, Vector3 max)
```

## GetOvershoot

```csharp
static float GetOvershoot(float value, float min, float max)
static Vector2 GetOvershoot(Vector2 value, Vector2 min, Vector2 max)
```

> [!NOTE]
> `GetOvershoot` has no `Vector3` overload.

## DampenVelocity

Reduces velocity as a value moves past its bounds, for slowing scroll/drag momentum near edges.

```csharp
static float DampenVelocity(float velocity, float value, float min, float max, float dampFactor = 0.5f)
static Vector2 DampenVelocity(Vector2 velocity, Vector2 value, Vector2 min, Vector2 max, float dampFactor = 0.5f)
```

| Parameter | Default | Description |
|---|---|---|
| `dampFactor` | `0.5` | Fraction of velocity retained per step while out of bounds |

> [!NOTE]
> `DampenVelocity` has no `Vector3` overload.

## Worked example: drag and release

```csharp
using Bluecadet.Spring;

const float min = 0f;
const float max = 100f;

// While dragging, apply elastic resistance past the bounds
void OnDrag(float rawPosition)
{
    float displayPosition = Rubberband.Apply(rawPosition, min, max, resistance: 0.55f);
    transform.localPosition = new Vector3(displayPosition, 0, 0);
}

// On release, dampen velocity if out of bounds, then spring back into range
void OnRelease(float rawPosition, float velocity)
{
    if (Rubberband.IsOutOfBounds(rawPosition, min, max))
    {
        velocity = Rubberband.DampenVelocity(velocity, rawPosition, min, max);
        float target = Rubberband.Clamp(rawPosition, min, max);
        _spring.Start(target, velocity);
    }
    else
    {
        _decay.Play(velocity);
    }
}
```
