Shader "Hidden/Bluecadet/HapFlip"
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

            // Hap / Hap Alpha / Hap R need no colour decode: the block-compressed texture is
            // already RGBA, so this only corrects the DXT orientation.
            fixed4 frag(v2f_img i) : SV_Target
            {
                return tex2D(_MainTex, HapFlipUV(i.uv));
            }
            ENDCG
        }
    }
}
