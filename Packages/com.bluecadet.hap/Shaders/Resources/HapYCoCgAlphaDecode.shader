Shader "Hidden/Bluecadet/HapYCoCgAlphaDecode"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _AlphaTex ("Alpha", 2D) = "white" {}
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
            #include "../HapDecode.cginc"

            sampler2D _MainTex;
            sampler2D _AlphaTex;

            // Hap Q Alpha carries two textures per frame:
            //   _MainTex  — YCoCg-Scaled colour in DXT5, same layout as Hap Q.
            //   _AlphaTex — the alpha channel in RGTC1/BC4, single channel in R.
            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 uv = HapFlipUV(i.uv);
                half4 s = tex2D(_MainTex, uv);
                float a = tex2D(_AlphaTex, uv).r;
                return half4(HapYCoCgToRGB(s), a);
            }
            ENDCG
        }
    }
}
