# Bluecadet HAP

Unity package for GPU-compressed Hap video playback. Decodes Hap-encoded MOV
files directly into `Texture2D` memory with zero GC allocations during
steady-state playback.

## Installation

**Via openUPM (recommended)**

```sh
openupm add com.bluecadet.hap
```

Or add the scoped registry manually to `Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    {
      "name": "OpenUPM",
      "url": "https://package.openupm.com",
      "scopes": ["com.bluecadet"]
    }
  ],
  "dependencies": {
    "com.bluecadet.hap": "1.1.0"
  }
}
```

**Via Git URL**

```json
{
  "dependencies": {
    "com.bluecadet.hap": "https://github.com/bluecadet/unity-packages.git?path=Packages/com.bluecadet.hap#hap/v1.1.0"
  }
}
```

## Supported formats

| Format | Compression | Alpha |
|--------|-------------|-------|
| Hap | DXT1 | — |
| Hap Alpha | DXT5 | yes |
| Hap Q | YCoCg DXT5 | — |
| Hap Q Alpha | YCoCg DXT5 + RGTC1 | yes |
| Hap R | BC7 | — |

Hap Q Alpha decodes as real transparency: the player uploads two textures per
frame (YCoCg colour and an RGTC1 alpha plane) and combines them in a decode
shader. HapA (alpha-only) and Hap HDR (BC6H) are detected but not decoded —
opening one of those files fails with `HapOpenStatus.UnsupportedFormat`
rather than producing a broken frame.

## Usage

1. Add `HapPlayer` component to a GameObject
2. Set **File Path** to an absolute path to a Hap-encoded `.mov` file
3. Choose a **Render Mode**:
   - **MaterialOverride** — assigns the texture to a Renderer's material (default)
   - **RenderTexture** — blits each frame to an assigned RenderTexture (useful for UI `RawImage` or multi-material setups)
   - **APIOnly** — no automatic rendering; read `Texture` from script
4. Enter Play mode

### Render Modes

| Mode | Target field | Description |
|------|-------------|-------------|
| MaterialOverride | Target Renderer | Sets texture via `MaterialPropertyBlock` each frame |
| RenderTexture | Target Render Texture | `Graphics.Blit` each frame to the assigned RT |
| APIOnly | — | Access `Texture` from code; nothing is rendered automatically |

### Time Source

- **GameTime** (default) — uses `Time.deltaTime`, affected by `Time.timeScale`
- **UnscaledGameTime** — uses `Time.unscaledDeltaTime`, plays even when `Time.timeScale = 0`

### Opening and closing

Opening and closing a file are asynchronous. `OpenAsync`/`CloseAsync` return
[`Awaitable`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Awaitable.html)s
that complete on the main thread; `Open`/`Close` are fire-and-forget
equivalents for inspector-driven use, paired with the `Opened` event.

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

Calling `OpenAsync` again while a file is opening or already open supersedes
the previous call — its await completes with `HapOpenStatus.Superseded`
rather than throwing. An `OpenAsync` issued while a close is still tearing
down queues behind it and starts once the teardown lands. Disabling or
destroying the component completes any pending awaits with
`HapOpenStatus.Cancelled`. Open and close are main-thread-only calls and
always complete on the main thread.

`HapOpenStatus` values: `Success`, `Superseded`, `Cancelled`, `InvalidPath`,
`FileNotFound`, `FileUnreadable`, `NotAVideoFile`, `NoHapTrack`,
`UnsupportedFormat`, `CorruptVideo`, `OutOfMemory`, `GpuSetupFailed`.

### Public API

```csharp
public Texture Texture { get; }      // current decoded frame
public bool IsPlaying { get; }
public bool IsOpen { get; }
public bool IsOpening { get; }
public bool IsClosing { get; }
public int FrameCount { get; }
public float Duration { get; }
public float Time { get; set; }      // seek by setting
public float FrameRate { get; }
public int Width { get; }
public int Height { get; }

public string FilePath { get; }
public bool Loop { get; set; }
public Renderer TargetRenderer { get; set; }
public HapRenderMode RenderMode { get; set; }
public HapTimeSource TimeSource { get; set; }
public float PlaybackSpeed { get; set; }              // clamped >= 0
public RenderTexture TargetRenderTexture { get; set; }

public static int DecodeThreadCount { get; set; }    // process-wide, see below
public static long UploadBudgetBytesPerFrame { get; set; }    // process-wide, see below

public event Action Opened;
public event Action PlaybackCompleted;
public event Action PlaybackLooped;

public void Play();
public void Pause();
public void Stop();                          // resets to frame 0
public void Open(string path);                // fire-and-forget open
public void Close();                          // fire-and-forget close
public Awaitable<OpenResult> OpenAsync(string path);
public Awaitable CloseAsync();
```

`HapPlayer.DecodeThreadCount` controls how many threads decompress a chunked
frame's chunks in parallel. It is a process-wide static, not a per-player
setting: the last assignment wins and applies to every video currently
playing, from its next chunked frame onward. It reads back `0` until
assigned, meaning "use the plugin's default" (one thread per hardware thread
minus the ones the engine needs).

`HapPlayer.UploadBudgetBytesPerFrame` caps how many bytes of decoded video
the shared main loop (see Lifecycle, above) hands to the GPU in one tick,
across every open player. It is process-wide too, and defaults to `0`, which
means no cap. A tick that would go over the cap does not drop a frame
outright: the players it defers keep showing what they already uploaded and
their clocks keep running, and each gets another chance to upload on its
next turn. Worth setting once enough players are open at once that a tick's
uploads would overrun the frame budget — it trades some dropped frames for a
flat per-frame upload cost across all of them.

### Inspector Fields

| Field | Description |
|-------|-------------|
| File Path | Absolute path to a Hap `.mov` file |
| Play On Enable | Start playback automatically in OnEnable |
| Loop | Loop playback when reaching the end |
| Render Mode | APIOnly, MaterialOverride, or RenderTexture |
| Target Renderer | Renderer for MaterialOverride mode |
| Target Render Texture | RenderTexture for RenderTexture mode |
| Time Source | GameTime or UnscaledGameTime |
| Playback Speed | Speed multiplier (0 = paused, 1 = normal, 2 = double) |

### Lifecycle

- **OnEnable**: opens File Path, if set, and begins playback if Play On Enable is set
- **OnDisable**: stops playback and starts releasing the file; a disabled player's teardown keeps advancing, so textures are still released promptly
- **OnDestroy**: same teardown as OnDisable, with a short bounded wait for the decode thread to park before Unity reclaims the textures

Players do not tick themselves. While a player has a file open — or an open
or close in flight — it is driven by one shared main-thread loop that
advances every player's clock, decode request, texture upload and render.
That loop is what lets uploads, the expensive main-thread part of playback,
be spread across players rather than all landing in the same slice of a
frame: it issues them starting from a different player each frame, and can
be given a per-frame byte cap via `HapPlayer.UploadBudgetBytesPerFrame`,
after which the remaining players keep showing the frame they already have
and upload on a later frame instead.

A disabled component or an inactive GameObject does not play: its clock does
not advance and nothing of it is decoded, uploaded or rendered, matching a
plain `MonoBehaviour.Update` player's behavior before this shared loop
existed. Its open or close still runs to completion regardless, which is
what lets OnDisable's teardown keep advancing while disabled.

## Architecture

A native Zig plugin demuxes and decodes; C# handles playback timing, texture
upload, and Unity integration. Frames decompress directly into the texture
memory the GPU reads from — there is no intermediate CPU-side copy — and the
file is read through a memory-mapped view rather than buffered reads.
Opening a file validates the container and decodes its first frame up
front, so a bad or unsupported file fails at `OpenAsync`/`Open` with a typed
reason instead of failing partway through playback.

- **HapNative.cs** — P/Invoke bindings
- **HapFileSession.cs** — owns one open file: the native handle, the decode thread, and open/decode state
- **HapOutputPipeline.cs** — texture ring and the blit/property-block output path
- **HapPlayer.cs** — MonoBehaviour facade: playback clock, rendering, serialized settings
- **HapLifecycle.cs** — the awaitable open/close state machine behind HapPlayer
- **HapTeardown.cs** — releases one closed file's decode thread, native handle, and GPU resources
- **HapMainLoop.cs** — the one main-thread loop every player runs on: lifecycle, clocks, uploads and renders, in and out of play mode
- **HapUploadPhase.cs** — the upload half of a tick: rotation and the per-frame byte budget

## Performance with many concurrent players

The bottleneck for playing many videos at once is main-thread texture
upload, not disk I/O or decode. Each concurrent 4K Hap Q player costs
roughly **0.77 ms of main-thread time per frame** — almost entirely the
`Texture2D.Apply` memcpy of one decoded frame into GPU-visible memory. Cost
is dominated by frame byte size, so 1080p (a quarter of the pixels) costs
substantially less — extrapolating, a little over a quarter as much, since
a small fixed per-upload overhead doesn't shrink with resolution. Disk and
decode did not bind in testing; budget against upload.

**Average frame time understates the load.** If your content's frame rate
divides evenly into your display's refresh rate but doesn't match it (30 fps
content at 60 Hz is the common case), each player only uploads on half the
ticks — and by default every player picks the *same* half, because they all
start at time 0. That packs N uploads onto every other tick and none onto
the ticks in between, so the ticks that do work run at roughly double the
per-player cost your average would suggest. Measured on 4K Hap Q at N=16,
mean main-thread work reports ~49% of a 60 Hz frame budget while the busy
ticks already consume the entire budget. At N=24, half of all frames
overran the budget even though the reported average was ~69%. Size your
player count against worst-case (p99) frame time, not the mean.

### Staggering playback to spread uploads

Phase-offsetting each player's start time spreads uploads across the
content's frame period instead of bunching them on the same tick. Seek each
player before starting playback:

```csharp
for (int i = 0; i < players.Count; i++)
{
    HapPlayer player = players[i];
    await player.OpenAsync(path);
    player.Time = (i / (float)players.Count) * (1f / player.FrameRate);
    player.Play();
}
```

Measured on 4K Hap Q at N=24, staggered against in-phase: frames overrunning
the 60 Hz budget dropped from ~50% to under 1%, and p99 main-thread work
roughly halved. The trade is that total main-thread work across all players
rises 10-26% — staggering costs more in aggregate than it saves; it buys a
flatter, more predictable per-frame load rather than a lower one. Dropped
frames and delivered frame counts were unaffected in either arm.

**When not to use this.** Staggering introduces up to one content frame of
phase skew between players (33 ms at 30 fps). That's invisible when each
player drives an independent screen, but it's a visible seam if a single
image spans multiple displays, or if content is deliberately frame-locked
across players. Apply it only where players are allowed to drift out of
phase with each other.

**Limitations of these numbers.** Measured on macOS/Metal (Apple M4 Pro);
not verified on Windows/D3D12. Measured with a warm OS page cache. All
players in testing opened the same file, so file reads shared one
page-cache entry — deployments where each player opens a distinct file may
hit a disk bottleneck this testing did not exercise.

## Errors

An open failure never throws — it reports a typed `HapOpenStatus` through
`OpenResult` (for `OpenAsync`/awaited callers) and a log message (for
`Open`/inspector-driven callers). A decode failure during playback is logged
with the specific error the native plugin returned.

## Known limitations

- **Non-ASCII file paths on Windows are unsupported.** The native plugin opens files through the ANSI Win32 file API on Windows; paths outside the current ANSI code page will fail to open.
- **A disabled player that is never re-enabled may defer its final teardown.** Outside play mode, `HapPlayer` relies on the editor's update loop to finish releasing a disabled player's native resources; if the editor update loop doesn't run again (e.g. the editor is left idle), that release is deferred until it does.

## Building the Native Plugin

Requires Zig 0.16+.

Build the current host target:
```bash
cd Native~
zig build -Doptimize=ReleaseFast
```

Build a specific target:
```bash
cd Native~
zig build -Dtarget=aarch64-macos -Doptimize=ReleaseFast
zig build -Dtarget=x86_64-windows-gnu -Doptimize=ReleaseFast
zig build -Dtarget=aarch64-linux-gnu -Doptimize=ReleaseFast
```

Build every supported target:
```bash
cd Native~
zig build all -Doptimize=ReleaseFast
```

Artifacts are written under `Native~/zig-out/<target>/`. Supported targets are `macos-arm64`, `macos-x86_64`, `windows-arm64`, `windows-x86_64`, `linux-arm64`, and `linux-x86_64`.

To install build outputs directly into the package `Plugins/` directory, set Zig's install prefix:
```bash
cd Native~
zig build all -p ../Plugins -Doptimize=ReleaseFast
```

The installed full-matrix outputs are placed under `Plugins/<target>/` so architectures with the same library filename do not overwrite each other.

To install one selected target into `Plugins/`, run:
```bash
cd Native~
zig build -p ../Plugins -Dtarget=x86_64-windows-gnu -Doptimize=ReleaseFast
```

Run the native test suite with:
```bash
cd Native~
zig build test
```

Set `HAP_FUZZ_SECONDS` to also run the bounded, time-boxed fuzz loops against
the demuxer and decoder (skipped by default on a plain `zig build test`):
```bash
HAP_FUZZ_SECONDS=30 zig build test
```

## Vendor Libraries

All under `Native~/vendor/`. The native build compiles the vendored source directly with Zig. Frame decode itself is a first-party implementation (`Native~/src/core/hap_decode.zig`), not vendored code.

| Library | Source | License |
|---------|--------|---------|
| [minimp4](https://github.com/lieff/minimp4) | Header-only MOV/MP4 demuxer (hardened fork, see `Native~/vendor/README.md`) | CC0 |
| [snappy](https://github.com/google/snappy) | Google's official Snappy compression library, version 1.2.2 | BSD-3-Clause |

Full license texts are under `Native~/vendor/licenses/`.

### Known quirks

- **minimp4 + large files**: `MINIMP4_ALLOW_64BIT` must be defined before including `minimp4.h` or files over ~4GB will fail to parse. This is set in `vendor/minimp4/minimp4.c`.
- **minimp4 + HAP codec**: minimp4 doesn't recognize HAP sample entry FourCCs (`Hap1`, `HapY`, `HapM`, `HapA`) so it won't parse video dimensions from the stsd box. Additionally, QuickTime MOVs with two `hdlr` boxes per track (media handler + data handler) cause minimp4 to store the data handler type (`url `) instead of `vide`. The demuxer (`Native~/src/core/demuxer.zig`) works around both issues by parsing the stsd VisualSampleEntry directly from the moov atom.
