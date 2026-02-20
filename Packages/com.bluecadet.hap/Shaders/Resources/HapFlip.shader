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

            sampler2D _MainTex;

            // HAP DXT data is stored top-to-bottom, left-to-right (standard video convention).
            // Unity's LoadRawTextureData treats raw DXT as bottom-to-top (OpenGL convention),
            // producing a 180° rotation. We correct that here.
            fixed4 frag(v2f_img i) : SV_Target
            {
                return tex2D(_MainTex, float2(i.uv.x, 1.0 - i.uv.y));
            }
            ENDCG
        }
    }
}
