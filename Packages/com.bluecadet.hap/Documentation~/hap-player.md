---
title: HapPlayer
description: The component and scripting reference — inspector fields, render modes, open and close semantics, seeking, and lifecycle.
---

`HapPlayer` is the only component in the package. It owns a playback clock, the
render output path, and the serialized settings; the file itself, the decode
thread, and the GPU resources are managed behind it.

Namespace: `Bluecadet.Hap`.

## Inspector fields

There are no tooltips or range attributes on these fields, so the inspector shows
Unity's own humanised labels.

| Label | Field | Default | Notes |
| --- | --- | --- | --- |
| File Path | `filePath` | — | Absolute path to a Hap `.mov` |
| Play On Enable | `playOnEnable` | `true` | Begin playback in `OnEnable` |
| Loop | `loop` | `true` | Restart at the end instead of stopping |
| Render Mode | `renderMode` | `MaterialOverride` | See [render modes](#render-modes) |
| Target Renderer | `targetRenderer` | — | Used by `MaterialOverride` |
| Target Render Texture | `targetRenderTexture` | — | Used by `RenderTexture` |
| Time Source | `timeSource` | `GameTime` | See [time source](#time-source) |
| Playback Speed | `playbackSpeed` | `1` | See [playback speed](#playback-speed) |

## Render modes

| `HapRenderMode` | Target field | What happens each frame |
| --- | --- | --- |
| `APIOnly` | — | Nothing is rendered; read `Texture` yourself |
| `MaterialOverride` | Target Renderer | The texture is set via a `MaterialPropertyBlock` |
| `RenderTexture` | Target Render Texture | The frame is blitted to the assigned RT |

`RenderTexture` mode is the one to use for a UI `RawImage`, or when several
materials need the same frame.

## Time source

| `HapTimeSource` | Clock |
| --- | --- |
| `GameTime` | `Time.deltaTime` — affected by `Time.timeScale` |
| `UnscaledGameTime` | `Time.unscaledDeltaTime` — keeps playing at `timeScale = 0` |

## Playback speed

`PlaybackSpeed` is a plain unclamped multiplier. `1` is normal, `2` is double
speed, and `0` is treated as paused (`IsPlaying` reports `false`). **Negative
values play in reverse** — set the speed before calling `Play()`.

## Opening and closing

Opening and closing are asynchronous. `OpenAsync`/`CloseAsync` return
[`Awaitable`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Awaitable.html)s
that complete on the main thread. `Open`/`Close` are fire-and-forget equivalents
for inspector-driven use, paired with the `Opened` event.

Both are main-thread-only calls, and both always complete on the main thread.

Opening validates the container and decodes the first frame up front, so a bad or
unsupported file fails at the call rather than partway through playback.

```csharp
async void PlayAsync(HapPlayer player, string path)
{
    OpenResult result = await player.OpenAsync(path);
    if (result.Success)
    {
        player.Play();
        return;
    }

    switch (result.Status)
    {
        case HapOpenStatus.FileNotFound:
            Debug.LogError($"No file at {path}");
            break;
        case HapOpenStatus.UnsupportedFormat:
            Debug.LogError($"{path} is not a supported Hap variant");
            break;
        default:
            Debug.LogError($"Could not open {path}: {result.Status}");
            break;
    }
}
```

### Overlapping calls

- Calling `OpenAsync` while a file is opening or already open **supersedes** the
  previous call. The earlier await completes with `HapOpenStatus.Superseded`; it
  does not throw.
- An `OpenAsync` issued while a close is still tearing down queues behind it and
  starts once the teardown lands.
- Disabling or destroying the component completes any pending awaits with
  `HapOpenStatus.Cancelled`.

### Open results

`OpenResult` is a readonly struct:

```csharp
public HapOpenStatus Status { get; }
public string FilePath { get; }
public bool Success { get; }   // Status == HapOpenStatus.Success
```

`HapOpenStatus`, in declaration order: `Success`, `Superseded`, `Cancelled`,
`InvalidPath`, `FileNotFound`, `FileUnreadable`, `NotAVideoFile`, `NoHapTrack`,
`UnsupportedFormat`, `CorruptVideo`, `OutOfMemory`, `GpuSetupFailed`.

An open failure never throws. It reports a status through `OpenResult` for
awaited callers, and logs for `Open`/inspector-driven callers. A decode failure
during playback is logged with the reason the native plugin returned.

## Seeking, and opening at a timecode

Set `Time` right after asking for the file. You do not have to wait for the open,
and there is no need to seek from an `Opened` handler:

```csharp
player.Open(path);
player.Time = 12.5f;   // where the video starts, not a seek once it is running
player.Play();
```

The seek is held until the file arrives, then clamped to its duration and used as
the first frame decoded, so the video appears at that timecode rather than showing
frame 0 first. Reading `Time` back before the file is open returns what was asked
for.

`Open`/`OpenAsync` clear a seek made before the call, so a timecode meant for one
video is never inherited by the next. `Stop()` clears one too.

## Public API

```csharp
// Frame and file state
public Texture Texture { get; }      // current decoded frame
public bool IsPlaying { get; }
public bool IsOpen { get; }
public bool IsOpening { get; }
public bool IsClosing { get; }
public int FrameCount { get; }
public float Duration { get; }
public float Time { get; set; }      // seek by setting, before or after the open lands
public float FrameRate { get; }
public int Width { get; }
public int Height { get; }
public string FilePath { get; }

// Settings
public bool Loop { get; set; }
public Renderer TargetRenderer { get; set; }
public HapRenderMode RenderMode { get; set; }
public HapTimeSource TimeSource { get; set; }
public float PlaybackSpeed { get; set; }
public RenderTexture TargetRenderTexture { get; set; }

// Process-wide, see performance
public static int DecodeThreadCount { get; set; }
public static long UploadBudgetBytesPerFrame { get; set; }

public event Action Opened;
public event Action PlaybackCompleted;
public event Action PlaybackLooped;

public void Play();
public void Pause();
public void Stop();                            // resets to frame 0
public void Open(string path);                 // fire-and-forget open
public void Close();                           // fire-and-forget close
public Awaitable<OpenResult> OpenAsync(string path);
public Awaitable CloseAsync();
```

## Lifecycle

| Callback | Behaviour |
| --- | --- |
| `OnEnable` | Opens File Path if set, and starts playback if Play On Enable is set |
| `OnDisable` | Stops playback and starts releasing the file; teardown keeps advancing while disabled, so textures are released promptly |
| `OnDestroy` | The same teardown, with a short bounded wait for the decode thread to park before Unity reclaims the textures |

Players do not tick themselves. While a player has a file open — or an open or
close in flight — it is driven by one shared main-thread loop that advances every
player's clock, decode request, texture upload, and render. That is what lets
uploads be spread across players rather than all landing in the same slice of a
frame; see [playing many videos at once](performance.md).

A disabled component or an inactive GameObject does not play: its clock does not
advance and nothing of it is decoded, uploaded, or rendered. Its open or close
still runs to completion regardless, which is what lets `OnDisable`'s teardown
finish while disabled.

## Known limitations

- **Non-ASCII file paths on Windows are unsupported.** The native plugin opens
  files through the ANSI Win32 file API on Windows, so paths outside the current
  ANSI code page fail to open.
- **A disabled player that is never re-enabled may defer its final teardown.**
  Outside play mode, `HapPlayer` relies on the editor's update loop to finish
  releasing a disabled player's native resources. If that loop does not run again
  — an idle editor — the release waits until it does.
