---
title: UI blur for HDRP
description: A real-time Kawase dual-filter blur for Unity UI, rendered as an HDRP Custom Pass.
---

`com.bluecadet.uiblur-hdrp` renders the same Kawase dual-filter blur as [the URP package](/uiblur/), integrated into the High Definition Render Pipeline through `UIBlurCustomPass`, a `CustomPass` added to a Custom Pass Volume. The implementation lives in one file, `Scripts/UIBlurCustomPass.cs` (namespace `Bluecadet.UIBlur.HDRP`).

## Requirements

- Unity 6000.3+
- High Definition Render Pipeline (HDRP)

> [!WARNING]
> `package.json` declares no dependency on HDRP. If HDRP isn't installed in the consuming project, this package won't compile.

## Install

OpenUPM is the recommended path:

```sh
openupm add com.bluecadet.uiblur-hdrp
```

The scoped registry (`https://package.openupm.com`, scope `com.bluecadet`) only needs adding once per project.

To install from a Git URL instead, point at the release tag, which follows the `com.bluecadet.uiblur-hdrp@<version>` format:

```json
{
  "dependencies": {
    "com.bluecadet.uiblur-hdrp": "https://github.com/bluecadet/unity-packages.git?path=Packages/com.bluecadet.uiblur-hdrp#com.bluecadet.uiblur-hdrp@<version>"
  }
}
```

Pick a version from [Releases](https://github.com/bluecadet/unity-packages/releases) — every released tag is listed there. The `openupm add` form above always takes the latest.

## Setup

1. Create an empty GameObject in your scene.
2. Add Component > Rendering > **Custom Pass Volume**.
3. Set its injection point to your preferred timing.
4. Click **+** and select **UIBlurCustomPass**.
5. Configure the settings below.

`UIBlurCustomPass` is `[System.Serializable]`, but its `Setup`, `Cleanup`, and `Execute` overrides are all `protected` — there's no public API beyond the three serialized fields below, and nothing to call from script.

## Settings

| Field | Range | Default | Description |
|---|---|---|---|
| `blurScale` | 0–256 | `4` | Blur intensity. Higher values produce stronger blur. |
| `renderTextureName` | string | `_UIBlurTexture` | Name of the global render texture that UI shaders sample. |
| `resolutionScale` | 0.25–1.0 | `0.5` | Render resolution multiplier, used as-is with no further clamping. |

Unlike the URP inspector, these fields render without group headers — the `[Header(...)]` attributes in source are commented out.

> [!NOTE]
> There's no "Blur Passes" inspector setting. Pass count is computed at render time from `blurScale` via a private `ComputeBlurParams`, capped internally at 12 passes — it isn't user-configurable.

## Sample in a UI shader

```hlsl
TEXTURE2D_X(_UIBlurTexture);
SAMPLER(sampler_UIBlurTexture);

half4 frag(Varyings i) : SV_Target {
    float2 screenUV = i.screenPos.xy / i.screenPos.w;
    half4 blurColor = SAMPLE_TEXTURE2D_X(_UIBlurTexture, sampler_UIBlurTexture, screenUV);
    return blurColor * _Color;
}
```

For the Kawase dual-filter algorithm itself, see [the URP package](/uiblur/).

## Shaders

| Asset | Shader name |
|---|---|
| `Shaders/KawaseDualFilter.shader` | `Bluecadet/UIBlur/KawaseDualFilterHDRP` |
| `Shaders/UIBlur.shader` | `Bluecadet/UIBlur/UIBlurHDRP` |

## URP vs. HDRP

| | URP | HDRP |
|---|---|---|
| Integration | `UIBlurRenderFeature` on a URP Renderer asset | `UIBlurCustomPass` inside a Custom Pass Volume |
| Pass timing | Serialized `renderPassEvent`, default `AfterRenderingTransparents` | The Custom Pass Volume's injection point — no field on the pass itself |
| Settings shape | Separate `BlurSettings` class, plus a `BlurScale` passthrough property | Three fields directly on the pass, no property wrappers |
| Internal pass cap | `MAX_PASSES = 6` | `MAX_PASSES = 12` |
| Resolution clamp | `Setup` re-clamps `resolutionScale` to `0.1–1` | Uses `resolutionScale` as-is |
| Render path | Render Graph (`Blitter.BlitTexture`) | `RTHandle` arrays + `CoreUtils.DrawFullScreen` |
| Blur shader | `Bluecadet/UIBlur/KawaseDualFilter` | `Bluecadet/UIBlur/KawaseDualFilterHDRP` |
| Example UI shader | `Bluecadet/UIBlur/UIBlur` | `Bluecadet/UIBlur/UIBlurHDRP` |

The pipelines also differ internally: HDRP skips the render-graph abstraction in favor of manually pre-allocated `RTHandle` arrays (`RTHandles.Alloc`) drawn with `CoreUtils.DrawFullScreen` / `CoreUtils.SetRenderTarget`; its shaders use `SAMPLE_TEXTURE2D_X`/`TEXTURE2D_X` for XR single-pass and a manual UV Y-flip via `UNITY_UV_STARTS_AT_TOP`, and carry a `"RenderPipeline" = "HDRenderPipeline"` tag. In the Scene view (`CameraType.SceneView`), HDRP clears its final render target to `Color.clear` and sets that as the global texture, where URP swaps in a black texture instead.
