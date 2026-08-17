---
title: Gestures
description: Event and data-type reference for TouchGestureListener's pan and pinch gestures, plus the VelocityTracker that backs their velocity fields.
---

`TouchGestureListener` (`MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler`) exposes six public `UnityEvent<T>` fields — not C# `event`s. Wire them in the inspector or call `.AddListener(...)` in code. See [overview](index.md) for setup.

## Pan events

Single-finger drag.

| Field | Event type | Data type |
|---|---|---|
| `OnPanStart` | `GesturePanStartEvent` | `GesturePanStartData` |
| `OnPan` | `GesturePanEvent` | `GesturePanData` |
| `OnPanEnd` | `GesturePanEndEvent` | `GesturePanEndData` |

### GesturePanStartData

| Member | Type | Description |
|---|---|---|
| `pointer` | `PointerEventData` | The pointer that started the pan. |
| `startPosition` | `Vector2` | Position at the start of the pan. |

### GesturePanData

| Member | Type | Description |
|---|---|---|
| `pointer` | `PointerEventData` | The pointer being dragged. |
| `position` | `Vector2` | Current position. |
| `delta` | `Vector2` | Movement since the last frame. |
| `velocity` | `Vector2` | Current velocity. |
| `initialPosition` | `Vector2` | Position at the start of the pan. |

### GesturePanEndData

| Member | Type | Description |
|---|---|---|
| `pointer` | `PointerEventData` | The pointer that was released. |
| `startPosition` | `Vector2` | Position at the start of the pan. |
| `endPosition` | `Vector2` | Position at release. |
| `totalDelta` | `Vector2` | Net movement from start to end. |
| `totalDistance` | `float` | Path length traveled. |
| `rollingVelocity` | `Vector2` | Averaged velocity, from `VelocityTracker`, for inertia. |
| `finalVelocity` | `Vector2` | Instantaneous velocity at release. |

## Pinch events

Two-finger gesture.

| Field | Event type | Data type |
|---|---|---|
| `OnPinchStart` | `GesturePinchStartEvent` | `GesturePinchStartData` |
| `OnPinch` | `GesturePinchEvent` | `GesturePinchData` |
| `OnPinchEnd` | `GesturePinchEndEvent` | `GesturePinchEndData` |

### PinchValues

`struct`. Captures the two-finger geometry at a point in time.

| Member | Type | Description |
|---|---|---|
| `distance` | `float` | Distance between the two pointers. |
| `angle` | `float` | Angle between the two pointers, in radians. |
| `origin` | `Vector2` | Midpoint between the two pointers. |

### GesturePinchStartData

| Member | Type | Description |
|---|---|---|
| `pointer1` | `PointerEventData` | First pointer. |
| `pointer2` | `PointerEventData` | Second pointer. |
| `initial` | `PinchValues` | Geometry when the pinch started. |

### GesturePinchData

| Member | Type | Description |
|---|---|---|
| `pointer1` | `PointerEventData` | First pointer. |
| `pointer2` | `PointerEventData` | Second pointer. |
| `initial` | `PinchValues` | Geometry at pinch start. |
| `current` | `PinchValues` | Geometry this frame. |
| `delta` | `PinchValues` | Per-frame change in distance/angle/origin. |
| `scaleFactor` | `float` | `current.distance / initial.distance`. |

### GesturePinchEndData

| Member | Type | Description |
|---|---|---|
| `pointer1` | `PointerEventData` | First pointer. |
| `pointer2` | `PointerEventData` | Second pointer. |
| `initial` | `PinchValues` | Geometry at pinch start. |
| `final` | `PinchValues` | Geometry at release. |
| `totalScaleFactor` | `float` | `final.distance / initial.distance`. |
| `totalRotation` | `float` | Net rotation, in radians. |
| `rollingOriginVelocity` | `Vector2` | Averaged velocity of the pinch origin, from `VelocityTracker`, for inertia. |
| `finalOriginVelocity` | `Vector2` | Instantaneous velocity of the pinch origin at release. |

> [!WARNING]
> `delta` means different things on the two gestures. `GesturePanData.delta` is a `Vector2` — a movement vector. `GesturePinchData.delta` is a `PinchValues` — the per-frame change in distance, angle, and origin. Reading one as if it were the other is a type error the compiler catches, but the naming collision is easy to trip over when skimming code.

## Example

```csharp
using UnityEngine;
using Bluecadet.Touchscreen;

public class GestureHandler : MonoBehaviour {
    public TouchGestureListener gestureListener;

    void Start() {
        gestureListener.OnPanStart.AddListener(OnPanStart);
        gestureListener.OnPan.AddListener(OnPan);
        gestureListener.OnPanEnd.AddListener(OnPanEnd);

        gestureListener.OnPinchStart.AddListener(OnPinchStart);
        gestureListener.OnPinch.AddListener(OnPinch);
        gestureListener.OnPinchEnd.AddListener(OnPinchEnd);
    }

    void OnPanStart(GesturePanStartData data) {
        Debug.Log($"Pan started at {data.startPosition}");
    }

    void OnPan(GesturePanData data) {
        transform.position += (Vector3)data.delta;
    }

    void OnPanEnd(GesturePanEndData data) {
        // data.rollingVelocity is a smoothed velocity, suited to feeding a
        // decay/inertia animation on release.
        Debug.Log($"Pan ended, rolling velocity {data.rollingVelocity}");
    }

    void OnPinchStart(GesturePinchStartData data) {
        Debug.Log($"Pinch started, distance {data.initial.distance}");
    }

    void OnPinch(GesturePinchData data) {
        transform.localScale = Vector3.one * data.scaleFactor;
        // data.delta is PinchValues here, not a Vector2.
        float rotationDeltaDegrees = data.delta.angle * Mathf.Rad2Deg;
        transform.Rotate(Vector3.forward, rotationDeltaDegrees);
    }

    void OnPinchEnd(GesturePinchEndData data) {
        Debug.Log($"Pinch ended, total scale {data.totalScaleFactor}");
    }
}
```

## VelocityTracker

`VelocityTracker<T>` backs the rolling-velocity fields above (`GesturePanEndData.rollingVelocity`, `GesturePinchEndData.rollingOriginVelocity`) and is usable directly for other inertia effects.

```csharp
public VelocityTracker(int sampleCount, Func<T,T,T> add, Func<T,T,T> subtract, Func<T,float,T> scale, T zero);
```

| Member | Description |
|---|---|
| `void Track(T position, float time)` | Record a position sample. |
| `void TrackVelocity(T velocity, float time)` | Record a velocity sample directly. |
| `T GetLastVelocity(float currentTime = -1f, float maxAge = 0.1f)` | Most recent velocity, ignoring samples older than `maxAge`. |
| `T GetAveragedVelocity(float currentTime = -1f, float maxAge = 0.1f)` | Velocity averaged over samples younger than `maxAge`. |
| `void Clear()` | Discard all samples. |

Convenience subclasses supply the `add`/`subtract`/`scale`/`zero` arguments for common types:

| Type | Constructor |
|---|---|
| `VelocityTracker2D` | `VelocityTracker2D(int sampleCount = 5)` |
| `VelocityTracker3D` | `VelocityTracker3D(int sampleCount = 5)` |
| `VelocityTracker1D` | `VelocityTracker1D(int sampleCount = 5)` |

```csharp
var tracker = new VelocityTracker2D();

void Update() {
    tracker.Track(transform.position, Time.time);
}

void OnRelease() {
    Vector2 velocity = tracker.GetAveragedVelocity(Time.time);
    // Feed velocity into a decay/spring for inertia.
}
```
