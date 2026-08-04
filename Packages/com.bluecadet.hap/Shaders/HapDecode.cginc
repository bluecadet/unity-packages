#ifndef HAP_DECODE_INCLUDED
#define HAP_DECODE_INCLUDED

// Shared by every Hap output shader: the V-flip each one needs, and the YCoCg-Scaled
// colour decode the Hap Q variants share.

// HAP DXT data is stored top-to-bottom, left-to-right (standard video convention).
// Unity's raw texture upload treats DXT as bottom-to-top (OpenGL convention), producing
// a 180° rotation. Sampling through this corrects it.
float2 HapFlipUV(float2 uv)
{
    return float2(uv.x, 1.0 - uv.y);
}

// Hap Q stores YCoCg-Scaled colour in the DXT5 channels:
//   R = Co (chroma orange)
//   G = Cg (chroma green)
//   B = scale factor
//   A = Y  (luma, in DXT5 alpha for higher precision)
half3 HapYCoCgToRGB(half4 s)
{
    // Recover the scale factor: re-quantize B to the nearest stored multiple of 8/255.
    float scale = 1.0 / (floor(s.b * 255.0 / 8.0 + 0.5) * (8.0 / 255.0) + 1.0);
    float Co = (s.r - 128.0 / 255.0) * scale;
    float Cg = (s.g - 128.0 / 255.0) * scale;
    float Y  = s.a;
    return half3(Y + Co - Cg, Y + Cg, Y - Co - Cg);
}

#endif // HAP_DECODE_INCLUDED
