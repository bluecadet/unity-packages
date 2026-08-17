# Bluecadet UI Blur

Real-time blur for Unity UI using the Kawase dual-filter algorithm, rendered to a
global texture that any UI shader can sample.

Requires Unity 6000.3+ and the Universal Render Pipeline. For HDRP projects, use
[`com.bluecadet.uiblur-hdrp`](../com.bluecadet.uiblur-hdrp) instead.

## Installation

```sh
openupm add com.bluecadet.uiblur
```

The `com.bluecadet` scoped registry only needs adding once per project. See
[Installing a Package](../../README.md#installing-a-package) for the manual
`manifest.json` form and the Git URL alternative. Release tags are
`com.bluecadet.uiblur@<version>` — pick one from
[Releases](https://github.com/bluecadet/unity-packages/releases).

## Usage

Add **UI Blur Render Feature** to your URP Renderer asset, then sample the global
texture from a UI shader:

```hlsl
sampler2D _UIBlurTexture;
half4 blurColor = tex2D(_UIBlurTexture, i.screenPos.xy / i.screenPos.w);
```

The render texture name, blur scale, and resolution scale are all configurable on
the render feature.

## Documentation

Full documentation is in [`Documentation~/`](Documentation~/index.md) — setup,
every setting and its real range, the shader names shipped, and how the dual-filter
passes work.

Release history is in [CHANGELOG.md](CHANGELOG.md).
