---
title: Architecture and the native plugin
description: How the C# and Zig halves divide the work, and how to rebuild the plugin from source.
---

This page is for people working on the package, not for using it. Consumers get
prebuilt binaries for all six supported targets and never need Zig installed.

## How the work divides

A native Zig plugin demuxes the container and decodes frames. C# handles playback
timing, texture upload, and Unity integration.

Two properties fall out of that split:

- Frames decompress **directly into the texture memory the GPU reads from** —
  there is no intermediate CPU-side copy.
- The file is read through a **memory-mapped view** rather than buffered reads.

Opening a file validates the container and decodes its first frame up front, so a
bad or unsupported file fails at `OpenAsync`/`Open` with a typed reason rather than
failing partway through playback.

## C# layout

Only five types in `Scripts/` are public — `HapPlayer`, `OpenResult`,
`HapOpenStatus`, `HapRenderMode`, and `HapTimeSource`. Everything below is
internal, and is listed because the file names are how the codebase is navigated:

| File | Responsibility |
| --- | --- |
| `HapNative.cs` | P/Invoke bindings to the C ABI |
| `HapFileSession.cs` | One open file: native handle, decode thread, open/decode state |
| `HapOutputPipeline.cs` | Texture ring, and the blit / property-block output path |
| `HapPlayer.cs` | The MonoBehaviour facade: playback clock, rendering, serialized settings |
| `HapLifecycle.cs` | The awaitable open/close state machine behind `HapPlayer` |
| `HapTeardown.cs` | Releases one closed file's decode thread, native handle, and GPU resources |
| `HapMainLoop.cs` | The single main-thread loop every player runs on — lifecycle, clocks, uploads, renders, in and out of play mode |
| `HapUploadPhase.cs` | The upload half of a tick: rotation and the per-frame byte budget |

## Native layout

Under `Native~/`, which Unity ignores because of the trailing `~`:

| Path | What |
| --- | --- |
| `src/bluecadet_hap.zig` | The exported C ABI (`hap_open`, `hap_decode_texture`, …) |
| `src/bluecadet_hap.h` | Hand-maintained header for that ABI, mirrored by the C# bindings |
| `src/core/` | Engine-agnostic demux/decode core — pure Zig plus the vendored C libraries |
| `vendor/` | minimp4 and snappy, plus their licenses |
| `tests/fixtures/` | `.mov` fixtures, golden decoded textures, and the fuzz crash corpus |

Frame decode is a first-party implementation (`src/core/hap_decode.zig`), not
vendored code.

## Building

Requires Zig 0.16+. Run from inside `Native~/` — the test fixtures are looked up
relative to it.

```bash
cd Native~
zig build -Doptimize=ReleaseFast              # current host target
zig build all -Doptimize=ReleaseFast          # every supported target
```

Named targets:

```bash
zig build -Dtarget=aarch64-macos -Doptimize=ReleaseFast
zig build -Dtarget=x86_64-windows-gnu -Doptimize=ReleaseFast
zig build -Dtarget=aarch64-linux-gnu -Doptimize=ReleaseFast
```

Artifacts land under `Native~/zig-out/<target>/`. Supported targets are
`macos-arm64`, `macos-x86_64`, `windows-arm64`, `windows-x86_64`, `linux-arm64`,
and `linux-x86_64`.

To install build outputs straight into the package's `Plugins/` directory, set
Zig's install prefix:

```bash
zig build all -p ../Plugins -Doptimize=ReleaseFast   # all targets
zig build -p ../Plugins -Dtarget=x86_64-windows-gnu -Doptimize=ReleaseFast
```

Full-matrix outputs are placed under `Plugins/<target>/` so architectures sharing
a library filename do not overwrite each other.

## Tests

```bash
cd Native~
zig build test
```

The bounded fuzz loops against the demuxer and decoder are skipped by default. Set
`HAP_FUZZ_SECONDS` to time-box and run them:

```bash
HAP_FUZZ_SECONDS=30 zig build test
```

## Vendored libraries

All under `Native~/vendor/`. The native build compiles the vendored sources
directly with Zig; full license texts are under `Native~/vendor/licenses/`.

| Library | What | License |
| --- | --- | --- |
| [minimp4](https://github.com/lieff/minimp4) | Header-only MOV/MP4 demuxer, hardened fork | CC0 |
| [snappy](https://github.com/google/snappy) | Google's Snappy compression library, 1.2.2 | BSD-3-Clause |

### Known quirks

- **minimp4 and large files.** `MINIMP4_ALLOW_64BIT` must be defined before
  including `minimp4.h`, or files over ~4 GB fail to parse. It is set in
  `vendor/minimp4/minimp4.c`.
- **minimp4 and the Hap codec.** minimp4 does not recognise Hap sample-entry
  FourCCs (`Hap1`, `HapY`, `HapM`, `HapA`), so it will not parse video dimensions
  from the `stsd` box. Separately, QuickTime MOVs with two `hdlr` boxes per track
  (a media handler and a data handler) cause minimp4 to store the data handler
  type (`url `) instead of `vide`. `src/core/demuxer.zig` works around both by
  parsing the `stsd` VisualSampleEntry directly out of the `moov` atom.
