# Bluecadet HAP

Unity package for GPU-compressed HAP video playback. Decodes HAP-encoded MOV files to `Texture2D` with zero GC allocations during steady-state playback.

Supports HAP (DXT1), HAP Alpha (DXT5), and HAP Q (BC7).

## Usage

1. Add `HapPlayer` component to a GameObject
2. Set **File Path** to an absolute path to a HAP-encoded `.mov` file
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

### Public API

```csharp
public Texture2D Texture { get; }    // current decoded frame
public bool IsPlaying { get; }
public bool IsOpen { get; }
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

public event Action PlaybackCompleted;
public event Action PlaybackLooped;

public void Play();
public void Pause();
public void Stop();                  // resets to frame 0
public void Open(string path);      // close current, open new file
```

### Inspector Fields

| Field | Description |
|-------|-------------|
| File Path | Absolute path to a HAP `.mov` file |
| Play On Enable | Start playback automatically in OnEnable |
| Loop | Loop playback when reaching the end |
| Render Mode | APIOnly, MaterialOverride, or RenderTexture |
| Target Renderer | Renderer for MaterialOverride mode |
| Target Render Texture | RenderTexture for RenderTexture mode |
| Time Source | GameTime or UnscaledGameTime |
| Playback Speed | Speed multiplier (0 = paused, 1 = normal, 2 = double) |

### Lifecycle

- **OnEnable**: opens file, allocates buffers + texture, starts decode thread, begins playback if `playOnEnable` is true
- **OnDisable**: stops playback, joins decode thread, frees all native memory and texture
- **Update**: advances clock, requests background decode, uploads ready frame to texture on main thread

## Architecture

Native C plugin handles demux + decode. C# handles playback logic, texture upload, and Unity integration.

- **HapNative.cs** — P/Invoke bindings
- **HapFrameRingBuffer.cs** — triple-buffer using `Marshal.AllocHGlobal` (zero GC)
- **HapTextureUploader.cs** — `Texture2D.LoadRawTextureData(IntPtr)` + `Apply()`
- **HapPlayer.cs** — MonoBehaviour, playback clock, background decode thread

## Building the Native Plugin

Requires CMake 3.15+.

```bash
cd Native~
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build
```

To build a universal binary for both Apple Silicon and Intel on macOS:

```bash
cd Native~
cmake -B build -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES="arm64;x86_64"
cmake --build build
```

Verify with `lipo -info Plugins/bluecadet_hap.bundle`.

The post-build step copies the output to `Plugins/`:

| Platform | Output |
|----------|--------|
| macOS | `Plugins/bluecadet_hap.bundle` |
| Windows | `Plugins/bluecadet_hap.dll` |
| Linux | `Plugins/libbluecadet_hap.so` |

Only macOS (arm64) has been tested so far.

## Vendor Libraries

All under `Native~/vendor/`. None are modified except as noted below.

| Library | Source | License |
|---------|--------|---------|
| [minimp4](https://github.com/lieff/minimp4) | Header-only MOV/MP4 demuxer | CC0 |
| [hap](https://github.com/Vidvox/hap) | HAP reference codec | BSD-2-Clause |
| [snappy](https://github.com/google/snappy) | Google's official Snappy compression library | BSD-3-Clause |

### Known quirks

- **minimp4 + large files**: `MINIMP4_ALLOW_64BIT` must be defined before including `minimp4.h` or files over ~4GB will fail to parse. This is set in `hap_demux.c`.
- **minimp4 + HAP codec**: minimp4 doesn't recognize HAP sample entry FourCCs (`Hap1`, `HapY`, `HapM`, `HapA`) so it won't parse video dimensions from the stsd box. Additionally, QuickTime MOVs with two `hdlr` boxes per track (media handler + data handler) cause minimp4 to store the data handler type (`url `) instead of `vide`. The demuxer works around both issues by parsing the stsd VisualSampleEntry directly from the moov atom.
