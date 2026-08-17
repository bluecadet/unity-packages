---
title: Touchscreen input
description: A touchscreen-focused input module for Unity's EventSystem, with pan/pinch gesture detection and multi-touch mouse simulation.
---

`com.bluecadet.touchscreen` replaces Unity's `StandaloneInputModule` with `TouchscreenInputModule`, an `EventSystem` input module built for multi-touch installations. `TouchGestureListener` layers pan and pinch gesture detection on top of any UI element that receives pointer events.

## Requirements

- Unity 6000.3+
- Unity Input System package, already installed in the consuming project

> [!WARNING]
> `package.json` declares no `dependencies`. The Input System requirement is expressed only as an `Unity.InputSystem` reference in the package's assembly definition, not as a package manifest dependency. If the Input System isn't already installed in your project, this package will not compile.

## Install

OpenUPM is the recommended path:

```sh
openupm add com.bluecadet.touchscreen
```

The scoped registry (`https://package.openupm.com`, scope `com.bluecadet`) only needs adding once per project.

To install from a Git URL instead, point at the release tag, which follows the `com.bluecadet.touchscreen@<version>` format:

```json
{
  "dependencies": {
    "com.bluecadet.touchscreen": "https://github.com/bluecadet/unity-packages.git?path=Packages/com.bluecadet.touchscreen#com.bluecadet.touchscreen@<version>"
  }
}
```

Pick a version from [Releases](https://github.com/bluecadet/unity-packages/releases) — every released tag is listed there. The `openupm add` form above always takes the latest.

## Setup

### TouchscreenInputModule

1. Add `TouchscreenInputModule` to the `EventSystem` GameObject.
2. Disable or remove the default `StandaloneInputModule` on the same GameObject — only one input module should be active.
3. Assign the three serialized fields in the inspector:

| Inspector field | Backing field | Type | Purpose |
|---|---|---|---|
| Actions Asset | `m_ActionsAsset` | `InputActionAsset` | The action asset containing the point/click actions below. |
| Point Action | `m_PointAction` | `InputActionReference` | Drives pointer position. |
| Click Action | `m_ClickAction` | `InputActionReference` | Drives pointer down/up. |

`TouchscreenInputModule` exposes a single public member beyond these fields: `public override void Process()`, called by the `EventSystem` each frame.

### TouchGestureListener

Add `TouchGestureListener` to a GameObject with a raycastable UI graphic. It implements `IPointerDownHandler`, `IPointerUpHandler`, and `IDragHandler`, and exposes six public `UnityEvent<T>` fields for pan and pinch gestures. Wire them in the inspector, or subscribe via `.AddListener(...)` in code.

See [gestures](gestures.md) for the full event and data-type reference.

## Development aids

### Multi-touch simulation

`MultiTouchSimulator` is a plain C# class, constructed internally by `TouchscreenInputModule` — there's nothing to add to a scene.

- Hold **Alt** to simulate a second touch point. The mouse controls one point; a mirrored point is generated for the other.
- While Alt is held, **Shift** switches the simulated pair from translate mode to rotate/scale mode.

### Scene-view gizmos

`PointerGizmoRenderer` is a static class with one member:

```csharp
public static void DrawPointerGizmos(Dictionary<int, PointerEventData> pointerData, bool isMultiTouchSimulationActive);
```

`TouchscreenInputModule` calls this from its `protected` `OnDrawGizmos` to visualize active and simulated touch points in the Scene view while the `EventSystem` GameObject is selected.

## Next

- [Gestures](gestures.md) — pan and pinch event tables, complete data-type reference, and `VelocityTracker`.
