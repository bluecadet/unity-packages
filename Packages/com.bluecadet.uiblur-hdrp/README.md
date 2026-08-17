# Bluecadet UI Blur (HDRP)

Real-time blur for Unity UI using the Kawase dual-filter algorithm, rendered to a
global texture that any UI shader can sample. This is the HDRP build; the blur
itself is the same as the URP package's, and only the pipeline integration differs.

Requires Unity 6000.3+ and the High Definition Render Pipeline. For URP projects,
use [`com.bluecadet.uiblur`](../com.bluecadet.uiblur) instead.

## Installation

```sh
openupm add com.bluecadet.uiblur-hdrp
```

The `com.bluecadet` scoped registry only needs adding once per project. See
[Installing a Package](../../README.md#installing-a-package) for the manual
`manifest.json` form and the Git URL alternative. Release tags are
`com.bluecadet.uiblur-hdrp@<version>` — pick one from
[Releases](https://github.com/bluecadet/unity-packages/releases).

## Usage

Add a Custom Pass Volume to your scene, add `UIBlurCustomPass` to it, and set the
injection point. Then sample the global texture from a UI shader:

```hlsl
TEXTURE2D(_UIBlurTexture);
SAMPLER(sampler_UIBlurTexture);
half4 blurColor = SAMPLE_TEXTURE2D(_UIBlurTexture, sampler_UIBlurTexture, screenUV);
```

An example shader ships as `Bluecadet/UIBlur/UIBlurHDRP`.

## Documentation

Full documentation is in [`Documentation~/`](Documentation~/index.md) — setup, every
setting and its real range, the shader names shipped, and a full comparison against
the URP package.

Release history is in [CHANGELOG.md](CHANGELOG.md).
