# Bluecadet UI Blur

A performant real-time blur effect for Unity UI using the Kawase dual-filter algorithm.

## Requirements

- Unity 6000.3+
- Universal Render Pipeline (URP)

## Features

- **Kawase Blur**: Fast multi-pass blur algorithm optimized for real-time rendering
- **Dual Filter**: Efficient downsampling/upsampling for smooth blur at lower cost
- **Global Texture**: Renders blur to a global texture accessible by any UI shader
- **Configurable Quality**: Adjustable blur strength, passes, and resolution

## Setup

### 1. Add Render Feature

Add the `UIBlurRenderFeature` to your URP Renderer:

1. Select your URP Renderer Asset (e.g., `UniversalRenderer`)
2. Click "Add Renderer Feature"
3. Select "UI Blur Render Feature"
4. Configure settings as needed

### 2. Configure Settings

| Setting | Range | Description |
|---------|-------|-------------|
| **Blur Scale** | 0-64 | Blur intensity. Higher = stronger blur |
| **Blur Passes** | 2-6 | Number of blur iterations. More = smoother |
| **Render Texture Name** | string | Global shader property name (default: `_UIBlurTexture`) |
| **Resolution Scale** | 0.25-1.0 | Render resolution multiplier. Lower = better performance |
| **Render Pass Event** | enum | When to capture the blur (default: After Rendering Transparents) |

### 3. Sample in UI Shader

Access the blurred texture in any UI shader:

```hlsl
sampler2D _UIBlurTexture;

half4 frag(v2f i) : SV_Target {
    // Sample the blur texture using screen coordinates
    float2 screenUV = i.screenPos.xy / i.screenPos.w;
    half4 blurColor = tex2D(_UIBlurTexture, screenUV);

    // Combine with UI element color, tint, etc.
    return blurColor * _Color;
}
```

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
using Bluecadet.UIBlur;
```
