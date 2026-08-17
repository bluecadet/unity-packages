---
title: Hap video playback
description: Decodes Hap-encoded MOV files straight into Texture2D memory, with no GC allocation during steady-state playback.
---

Hap is a GPU-friendly video codec: frames stay compressed in the format the GPU
samples natively (DXT/BC), so playback costs a memory copy rather than a CPU
colour-space conversion. This package wraps a native demuxer and decoder and
exposes one component, `HapPlayer`.

It exists because installation work routinely needs many simultaneous video
layers at high resolution, which Unity's built-in `VideoPlayer` does not hold up
under. The trade is that the source files must be encoded as Hap in advance.

## Installation

OpenUPM is the recommended route:

```sh
openupm add com.bluecadet.hap
```

Or add the scoped registry to `Packages/manifest.json` yourself. The registry
entry is only needed once per project, no matter how many `com.bluecadet`
packages you install:

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
    "com.bluecadet.hap": "<version>"
  }
}
```

To pin a git tag instead, release tags are `com.bluecadet.hap@<version>`:

```json
{
  "dependencies": {
    "com.bluecadet.hap": "https://github.com/bluecadet/unity-packages.git?path=Packages/com.bluecadet.hap#com.bluecadet.hap@<version>"
  }
}
```

Pick a version from [Releases](https://github.com/bluecadet/unity-packages/releases) — every released tag is listed there. The `openupm add` form above always takes the latest.

## Requirements

- Unity 6000.3+
- `com.unity.collections` (declared as a package dependency, so the package
  manager pulls it in)
- macOS, Windows, or Linux on arm64 or x86_64 — the native plugin ships
  prebuilt for those six targets

## Quick start

1. Add a `HapPlayer` component to a GameObject.
2. Set **File Path** to an absolute path to a Hap-encoded `.mov`.
3. Pick a **Render Mode** — **MaterialOverride** (the default) assigns the
   decoded texture to a `Renderer`'s material each frame.
4. Enter play mode. **Play On Enable** is on by default, so it starts on its own.

From script, opening is asynchronous and reports a typed reason on failure
instead of throwing:

```csharp
using Bluecadet.Hap;

async void Play(HapPlayer player, string path)
{
    OpenResult result = await player.OpenAsync(path);
    if (!result.Success)
    {
        Debug.LogError($"Could not open {path}: {result.Status}");
        return;
    }

    player.Play();
}
```

## Where to go next

- [Supported formats](formats.md) — which Hap variants decode, and which are
  detected but rejected.
- [HapPlayer](hap-player.md) — the full component and scripting reference:
  inspector fields, properties, open/close semantics, seeking, and lifecycle.
- [Playing many videos at once](performance.md) — measured per-player cost, why
  the average understates the load, and how to stagger playback.
- [Architecture and the native plugin](native-plugin.md) — how the C# and Zig
  halves divide up, and how to rebuild the plugin.
