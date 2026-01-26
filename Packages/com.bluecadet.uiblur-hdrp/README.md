# Bluecadet UI Blur (HDRP)

A performant real-time blur effect for Unity UI using the Kawase dual-filter algorithm. This is the HDRP version of the UI Blur package.

## Requirements

- Unity 6000.3+
- High Definition Render Pipeline (HDRP)

## Features

- **Kawase Blur**: Fast multi-pass blur algorithm optimized for real-time rendering
- **Dual Filter**: Efficient downsampling/upsampling for smooth blur at lower cost
- **Global Texture**: Renders blur to a global texture accessible by any UI shader
- **Configurable Quality**: Adjustable blur strength, passes, and resolution

## Setup

### 1. Add Custom Pass Volume

Add a Custom Pass Volume to your scene with the `UIBlurCustomPass`:

1. Create an empty GameObject in your scene
2. Add Component > Rendering > Custom Pass Volume
3. Set "Injection Point" to your preferred timing (e.g., "After Post Process")
4. Click "+" to add a custom pass
5. Select "UIBlurCustomPass"
6. Configure settings as needed

### 2. Configure Settings

| Setting | Range | Description |
|---------|-------|-------------|
| **Blur Scale** | 0-64 | Blur intensity. Higher = stronger blur |
| **Blur Passes** | 2-6 | Number of blur iterations. More = smoother |
| **Render Texture Name** | string | Global shader property name (default: `_UIBlurTexture`) |
| **Resolution Scale** | 0.25-1.0 | Render resolution multiplier. Lower = better performance |

### 3. Sample in UI Shader

Access the blurred texture in any UI shader:

```hlsl
TEXTURE2D(_UIBlurTexture);
SAMPLER(sampler_UIBlurTexture);

half4 frag(Varyings i) : SV_Target {
    // Sample the blur texture using screen coordinates
    float2 screenUV = i.screenPos.xy / i.screenPos.w;
    half4 blurColor = SAMPLE_TEXTURE2D(_UIBlurTexture, sampler_UIBlurTexture, screenUV);

    // Combine with UI element color, tint, etc.
    return blurColor * _Color;
}
```

An example shader is included: `Bluecadet/UIBlur/UIBlurHDRP`

## Algorithm

The package uses the Kawase dual-filter blur algorithm:

1. **Downsample Pass**: Progressively reduce texture resolution while applying blur kernel
2. **Upsample Pass**: Progressively increase resolution while blending samples

This approach provides high-quality blur at a fraction of the cost of traditional Gaussian blur.

## Example Use Cases

- Frosted glass UI panels
- Background blur behind modals/popups
- Depth-of-field style UI effects
- Glassmorphism design patterns

## Namespace

```csharp
using Bluecadet.UIBlur.HDRP;
```

## Differences from URP Version

The HDRP version uses HDRP's Custom Pass system instead of URP's Renderer Features. The core blur algorithm and shader logic are identical - only the pipeline integration differs.

| URP | HDRP |
|-----|------|
| `UIBlurRenderFeature` on Renderer | `UIBlurCustomPass` in Custom Pass Volume |
| Renderer Feature settings | Custom Pass settings |
| `RenderPassEvent` | Custom Pass Injection Point |
