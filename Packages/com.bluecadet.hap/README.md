# Bluecadet HAP

Unity package for GPU-compressed HAP video playback. Decodes HAP-encoded MOV files to `Texture2D` with zero GC allocations during steady-state playback.

Supports HAP (DXT1), HAP Alpha (DXT5), and HAP Q (BC7).

## Usage

1. Add `HapPlayer` component to a GameObject
2. Set **File Path** to an absolute path to a HAP-encoded `.mov` file
3. Optionally assign a **Target Renderer** to auto-apply the texture to its material
4. Enter Play mode

### Public API

```csharp
public Texture2D Texture { get; }    // current decoded frame
public bool IsPlaying { get; }
public bool IsOpen { get; }
public int FrameCount { get; }
public float Duration { get; }
public float Time { get; set; }      // seek by setting

public void Play();
public void Pause();
public void Stop();                  // resets to frame 0
```

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
| [snappy-c](https://github.com/andikleen/snappy-c) | Snappy decompression (C port) | BSD-3-Clause |

### Vendor modifications

- **snappy-c**: Added `snappy-c.h` and `snappy-c.c` — a compatibility shim that wraps andikleen's snappy API to match the Google `snappy-c.h` interface expected by the Vidvox HAP library. The original `snappy.h`/`snappy.c` are unmodified; `snappy-c.c` includes `snappy.c` with renamed symbols and provides wrapper functions with Google-compatible signatures (`snappy_status` return types, 4-argument `snappy_uncompress`, etc.).

### Known quirks

- **minimp4 + large files**: `MINIMP4_ALLOW_64BIT` must be defined before including `minimp4.h` or files over ~4GB will fail to parse. This is set in `hap_demux.c`.
- **minimp4 + HAP codec**: minimp4 doesn't recognize HAP sample entry FourCCs (`Hap1`, `HapY`, `HapM`, `HapA`) so it won't parse video dimensions from the stsd box. Additionally, QuickTime MOVs with two `hdlr` boxes per track (media handler + data handler) cause minimp4 to store the data handler type (`url `) instead of `vide`. The demuxer works around both issues by parsing the stsd VisualSampleEntry directly from the moov atom.
