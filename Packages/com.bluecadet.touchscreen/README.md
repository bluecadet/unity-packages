# Bluecadet Touchscreen

A touchscreen-focused input module for Unity's `EventSystem`, with pan and pinch
gesture detection, rolling velocity tracking for inertia, and Alt+click multi-touch
simulation for development.

Requires Unity 6000.3+ and Unity's Input System. Note that the Input System is
referenced by assembly definition rather than declared as a package dependency, so
it must already be installed in the project.

## Installation

```sh
openupm add com.bluecadet.touchscreen
```

The `com.bluecadet` scoped registry only needs adding once per project. See
[Installing a Package](../../README.md#installing-a-package) for the manual
`manifest.json` form and the Git URL alternative. Release tags are
`com.bluecadet.touchscreen@<version>` — pick one from
[Releases](https://github.com/bluecadet/unity-packages/releases).

## Usage

Add `TouchscreenInputModule` to your `EventSystem` GameObject, disable or remove
the default `StandaloneInputModule`, and assign the **Point Action** and **Click
Action** references.

Then add a `TouchGestureListener` to any raycast target and subscribe to its
gesture events:

```csharp
gestureListener.OnPan.AddListener(data => Pan(data.delta));
gestureListener.OnPinch.AddListener(data => Zoom(data.scaleFactor));
```

## Documentation

Full documentation is in [`Documentation~/`](Documentation~/index.md):

- [Gestures](Documentation~/gestures.md) — every pan and pinch event and its data

Release history is in [CHANGELOG.md](CHANGELOG.md).
