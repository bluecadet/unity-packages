---
title: Spring animation
description: Physics-based spring animations, decay/inertia, and rubberband boundary constraints for Unity.
---

`com.bluecadet.spring` drives values toward targets with a damped harmonic oscillator (`SpringValue<T>`), decays velocity to rest for momentum-based motion (`DecayValue<T>`), and applies iOS-style elastic resistance at boundaries (`Rubberband`). Per-type math is Burst-compiled for `float`, `Vector2`, and `Vector3`.

## Requirements

- Unity 6000.3+
- `com.unity.burst` 1.8.27
- `com.unity.mathematics` 1.3.3

## Install

OpenUPM is the recommended path:

```sh
openupm add com.bluecadet.spring
```

The scoped registry (`https://package.openupm.com`, scope `com.bluecadet`) only needs adding once per project.

To install from a Git URL instead, point at the release tag, which follows the `com.bluecadet.spring@<version>` format:

```json
{
  "dependencies": {
    "com.bluecadet.spring": "https://github.com/bluecadet/unity-packages.git?path=Packages/com.bluecadet.spring#com.bluecadet.spring@<version>"
  }
}
```

Pick a version from [Releases](https://github.com/bluecadet/unity-packages/releases) — every released tag is listed there. The `openupm add` form above always takes the latest.

## Quick start

### A basic spring

```csharp
using Bluecadet.Spring;

// Create once (e.g. in Awake), hold as a field
_spring = Spring.Create(0f)
    .WithDamping(26f)
    .WithStiffness(170f)
    .Bind(x => transform.localPosition = new Vector3(x, 0, 0));

// Animate to a target (velocity is preserved mid-flight)
_spring.To(1f);

// Release when done (e.g. OnDestroy)
Spring.Release(_spring);
```

### A decay

```csharp
_decay = Spring.CreateDecay(Vector2.zero)
    .WithFriction(8f)
    .Bind(v => scrollView.velocity = v);

// Trigger on swipe
_decay.Play(swipeVelocity);

Spring.Release(_decay);
```

### A rubberband clamp

```csharp
using Bluecadet.Spring;

// Resist a value past its bounds (e.g. during drag)
float constrained = Rubberband.Apply(value, min: 0f, max: 100f, resistance: 0.55f);

// Snap back to bounds on release
if (Rubberband.IsOutOfBounds(position, 0f, 100f))
    _spring.To(Rubberband.Clamp(position, 0f, 100f));
```

## Next

- [Springs and decay](springs-and-decay.md) — builder options, binding, events, and lifetime for `SpringValue<T>` and `DecayValue<T>`.
- [Rubberband](rubberband.md) — the static `Rubberband` API and a worked drag/release example.
