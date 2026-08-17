# Bluecadet Spring

Physics-based spring animations, decay/inertia, and rubberband boundary constraints
for Unity. Per-component math is Burst-compiled for `float`, `Vector2`, and
`Vector3`, and closure-free bind overloads keep hot paths allocation-free.

Requires Unity 6000.3+, `com.unity.burst`, and `com.unity.mathematics`.

## Installation

```sh
openupm add com.bluecadet.spring
```

The `com.bluecadet` scoped registry only needs adding once per project. See
[Installing a Package](../../README.md#installing-a-package) for the manual
`manifest.json` form and the Git URL alternative. Release tags are
`com.bluecadet.spring@<version>` — pick one from
[Releases](https://github.com/bluecadet/unity-packages/releases).

## Usage

```csharp
using Bluecadet.Spring;

// Create once, hold as a field
_spring = Spring.Create(0f)
    .WithDamping(26f)
    .WithStiffness(170f)
    .Bind(x => transform.localPosition = new Vector3(x, 0, 0));

_spring.To(1f);          // animate; velocity is preserved mid-flight
Spring.Release(_spring); // in OnDestroy
```

`Spring.CreateDecay` gives velocity-based inertia instead, and `Rubberband` applies
elastic resistance past a boundary.

## Documentation

Full documentation is in [`Documentation~/`](Documentation~/index.md):

- [Springs and decay](Documentation~/springs-and-decay.md)
- [Rubberband](Documentation~/rubberband.md)

Release history is in [CHANGELOG.md](CHANGELOG.md).
