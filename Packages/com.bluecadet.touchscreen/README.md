# Bluecadet Touchscreen Input Module

A custom Unity input module for handling multi-touch input on touchscreen installations, with gesture detection for pan and pinch interactions.

## Requirements

- Unity 6000.3+
- Unity Input System package

## Features

- **Multi-touch Support**: Handle multiple simultaneous touch points
- **Gesture Detection**: Pan (single-finger drag) and pinch (two-finger zoom/rotate) gestures
- **Velocity Tracking**: Rolling velocity calculation for smooth inertia animations
- **Alt+Click Simulation**: Simulate pinch gestures with mouse for development
- **Debug Visualization**: Gizmo rendering for active touch points

## Setup

### TouchscreenInputModule

Replace Unity's default Standalone Input Module with `TouchscreenInputModule`:

1. Add `TouchscreenInputModule` component to your EventSystem GameObject
2. Disable or remove the default `StandaloneInputModule`
3. Assign Input Action references for point and click actions

```
EventSystem
├── TouchscreenInputModule
│   ├── Point Action: <Your Input Action>
│   └── Click Action: <Your Input Action>
```

### TouchGestureListener

Add gesture detection to any UI element:

1. Add `TouchGestureListener` component to a GameObject with a `RaycastTarget`
2. Subscribe to gesture events in the Inspector or via code

## Usage

### Handling Pan Gestures

```csharp
using Bluecadet.Touchscreen;

public class PanHandler : MonoBehaviour {
    public TouchGestureListener gestureListener;

    void Start() {
        gestureListener.OnPanStart.AddListener(OnPanStart);
        gestureListener.OnPan.AddListener(OnPan);
        gestureListener.OnPanEnd.AddListener(OnPanEnd);
    }

    void OnPanStart(GesturePanStartData data) {
        // data.startPosition - initial touch position
    }

    void OnPan(GesturePanData data) {
        // data.position - current position
        // data.delta - movement since last frame
        // data.velocity - current velocity (delta / deltaTime)
    }

    void OnPanEnd(GesturePanEndData data) {
        // data.totalDelta - total movement
        // data.rollingVelocity - averaged velocity for smooth inertia
        // data.finalVelocity - instantaneous velocity at release
    }
}
```

### Handling Pinch Gestures

```csharp
void OnPinchStart(GesturePinchStartData data) {
    // data.initial.distance - starting distance between fingers
    // data.initial.origin - midpoint between fingers
    // data.initial.angle - angle between fingers (radians)
}

void OnPinch(GesturePinchData data) {
    // data.scaleFactor - current scale (current distance / initial distance)
    // data.current.origin - current midpoint
    // data.delta.distance - distance change since last frame
    // data.delta.angle - rotation change since last frame
}

void OnPinchEnd(GesturePinchEndData data) {
    // data.totalScaleFactor - final scale factor
    // data.totalRotation - total rotation in radians
    // data.rollingOriginVelocity - averaged velocity of pinch center
}
```

## Gesture Events

### Pan Events

| Event | Data Type | Description |
|-------|-----------|-------------|
| `OnPanStart` | `GesturePanStartData` | Single finger touch began |
| `OnPan` | `GesturePanData` | Finger is moving |
| `OnPanEnd` | `GesturePanEndData` | Finger released |

### Pinch Events

| Event | Data Type | Description |
|-------|-----------|-------------|
| `OnPinchStart` | `GesturePinchStartData` | Second finger touched down |
| `OnPinch` | `GesturePinchData` | Two fingers moving |
| `OnPinchEnd` | `GesturePinchEndData` | One finger released |

## Development Features

### Multi-touch Simulation

Hold **Alt** while clicking and dragging to simulate a two-finger pinch gesture. The mouse position controls one touch point, and a mirrored point is created on the opposite side of the initial click position.

### Debug Gizmos

Touch points are visualized in the Scene view when the TouchscreenInputModule is selected. Different colors indicate:
- Active touch points
- Simulated touch points (Alt+click)

## Namespace

```csharp
using Bluecadet.Touchscreen;
```
