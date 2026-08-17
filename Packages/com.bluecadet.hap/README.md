# Bluecadet Hap

Unity package for GPU-compressed Hap video playback. Decodes Hap-encoded MOV files
directly into `Texture2D` memory with zero GC allocations during steady-state
playback.

Supports Hap, Hap Alpha, Hap Q, Hap Q Alpha, and Hap R. Requires Unity 6000.3+.

## Installation

```sh
openupm add com.bluecadet.hap
```

The `com.bluecadet` scoped registry only needs adding once per project. See
[Installing a Package](../../README.md#installing-a-package) for the manual
`manifest.json` form and the Git URL alternative. Release tags are
`com.bluecadet.hap@<version>` — pick one from
[Releases](https://github.com/bluecadet/unity-packages/releases).

## Usage

Add a `HapPlayer` component to a GameObject, set **File Path** to an absolute path
to a Hap `.mov`, choose a **Render Mode**, and enter play mode.

From script, opening is asynchronous and reports a typed reason on failure rather
than throwing:

```csharp
OpenResult result = await player.OpenAsync(path);
if (result.Success)
    player.Play();
```

## Documentation

Full documentation is in [`Documentation~/`](Documentation~/index.md):

- [Supported formats](Documentation~/formats.md)
- [HapPlayer reference](Documentation~/hap-player.md)
- [Playing many videos at once](Documentation~/performance.md)
- [Architecture and the native plugin](Documentation~/native-plugin.md)

Release history is in [CHANGELOG.md](CHANGELOG.md).
