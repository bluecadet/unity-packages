Shader "Hidden/Bluecadet/HapYCoCgDecode"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Pass
        {
            ZTest Always Cull Off ZWrite Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            // HAP Q stores YCoCg-Scaled color space in DXT5 channels:
            //   R = Co  (chroma orange)
            //   G = Cg  (chroma green)
            //   B = scale factor
            //   A = Y   (luma, stored in DXT5 alpha for higher precision)
            fixed4 frag(v2f_img i) : SV_Target
            {
                // Flip V to correct DXT orientation (same root cause as HapFlip.shader)
                half4 s = tex2D(_MainTex, float2(i.uv.x, 1.0 - i.uv.y));
                // Recover the scale factor: re-quantize B to the nearest stored multiple of 8/255
                float scale = 1.0 / (floor(s.b * 255.0 / 8.0 + 0.5) * (8.0 / 255.0) + 1.0);
                float Co = (s.r - 128.0 / 255.0) * scale;
                float Cg = (s.g - 128.0 / 255.0) * scale;
                float Y  = s.a;
                return half4(Y + Co - Cg, Y + Cg, Y - Co - Cg, 1.0);
            }
            ENDCG
        }
    }
}
