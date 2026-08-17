---
title: UI blur for URP
description: A real-time Kawase dual-filter blur for Unity UI, rendered as a URP Renderer Feature.
---

`com.bluecadet.uiblur` renders a screen-space blur to a global texture (`_UIBlurTexture` by default) that any UI shader can sample. It integrates with the Universal Render Pipeline through `UIBlurRenderFeature`, added to a URP Renderer asset. The blur is a Kawase dual filter, implemented across `KawaseDualFilter`, `UIBlurRenderPass`, and `UIBlurRenderFeature` (namespace `Bluecadet.UIBlur`).

## Requirements

- Unity 6000.3+
- Universal Render Pipeline (URP)

> [!WARNING]
> `package.json` declares no dependency on URP. If URP isn't installed in the consuming project, this package won't compile.

## Install

OpenUPM is the recommended path:

```sh
openupm add com.bluecadet.uiblur
```

The scoped registry (`https://package.openupm.com`, scope `com.bluecadet`) only needs adding once per project.

To install from a Git URL instead, point at the release tag, which follows the `com.bluecadet.uiblur@<version>` format:

```json
{
  "dependencies": {
    "com.bluecadet.uiblur": "https://github.com/bluecadet/unity-packages.git?path=Packages/com.bluecadet.uiblur#com.bluecadet.uiblur@<version>"
  }
}
```

Pick a version from [Releases](https://github.com/bluecadet/unity-packages/releases) — every released tag is listed there. The `openupm add` form above always takes the latest.

## Setup

1. Select your URP Renderer asset (e.g. `UniversalRenderer`).
2. Click **Add Renderer Feature**.
3. Select **UI Blur Render Feature**.
4. Configure the settings below.

## Settings

| Field | Range | Default | Description |
|---|---|---|---|
| `blurScale` | 0–256 | `4` | Blur intensity. Higher values produce stronger blur. |
| `renderTextureName` | string | `_UIBlurTexture` | Name of the global render texture that UI shaders sample. |
| `resolutionScale` | 0.25–1.0 | `0.5` | Render resolution multiplier. Lower values improve performance. `UIBlurRenderPass.Setup` re-clamps this to `0.1–1` internally. |
| `renderPassEvent` | enum | `AfterRenderingTransparents` | When the render feature injects its pass. |

> [!NOTE]
> There's no "Blur Passes" inspector setting. Pass count is computed at render time from `blurScale` via `KawaseDualFilter.ComputeBlurParams`, capped internally at 6 passes — it isn't user-configurable.

`UIBlurRenderFeature` also exposes a public `BlurScale` float property, a passthrough to the underlying `blurScale` field, for adjusting blur strength from script. `renderTextureName` and `resolutionScale` have no equivalent public wrapper.

## Sample in a UI shader

```hlsl
sampler2D _UIBlurTexture;

half4 frag(v2f i) : SV_Target {
    float2 screenUV = i.screenPos.xy / i.screenPos.w;
    half4 blurColor = tex2D(_UIBlurTexture, screenUV);
    return blurColor * _Color;
}
```

## Algorithm

The blur runs a fixed number of Kawase dual-filter passes, computed from `blurScale`:

1. **Downsample**: progressively reduce resolution while applying the blur kernel.
2. **Upsample**: progressively increase resolution while blending samples.

This gets most of the visual quality of a Gaussian blur at a fraction of the sample cost. `KawaseDualFilter.ComputeBlurParams` maps `blurScale` to a pass count (capped at 6) and a per-pass sample offset; the nested `KawaseDualFilter.Resolution` struct tracks texel size at each step. The pass records into the Render Graph (`RecordRenderGraph`, `TextureHandle`, `Blitter.BlitTexture`) and is enqueued on the renderer via `EnqueuePass`.

In the Scene view (`CameraType.SceneView`), the feature sets the global texture to `Texture2D.blackTexture` and skips rendering the blur.

## Shaders and materials

| Asset | Shader name |
|---|---|
| `Shaders/KawaseDualFilter.shader` | `Bluecadet/UIBlur/KawaseDualFilter` |
| `Shaders/UIBlur.shader` | `Bluecadet/UIBlur/UIBlur` |
| `Materials/UIBlur.mat` | Uses `Bluecadet/UIBlur/UIBlur`. |

## Example use cases

- Frosted-glass UI panels
- Background blur behind modals and popups
- Depth-of-field-style UI effects
- Glassmorphism design patterns

## HDRP

Building on HDRP instead of URP? See [the HDRP package](/uiblur-hdrp/) — same blur, integrated as a Custom Pass.
