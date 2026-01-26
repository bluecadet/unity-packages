Shader "Bluecadet/UIBlur/UIBlurHDRP"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

            // Global texture set by UIBlurCustomPass
            TEXTURE2D_X(_UIBlurTexture);
            SAMPLER(sampler_UIBlurTexture);
            float4 _UIBlurTexture_TexelSize;

            CBUFFER_START(UnityPerMaterial)
            half4 _Tint;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Compute screen UV from clip space position
                float2 screenUV = IN.positionCS.xy * _ScreenSize.zw;

                half4 blurSample = SAMPLE_TEXTURE2D_X(_UIBlurTexture, sampler_UIBlurTexture, screenUV);

                return blurSample * _Tint;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
