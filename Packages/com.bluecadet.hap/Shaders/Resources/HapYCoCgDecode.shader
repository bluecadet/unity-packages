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
            #include "../HapDecode.cginc"

            sampler2D _MainTex;

            // Hap Q: one YCoCg-Scaled DXT5 texture, fully opaque.
            fixed4 frag(v2f_img i) : SV_Target
            {
                half4 s = tex2D(_MainTex, HapFlipUV(i.uv));
                return half4(HapYCoCgToRGB(s), 1.0);
            }
            ENDCG
        }
    }
}
