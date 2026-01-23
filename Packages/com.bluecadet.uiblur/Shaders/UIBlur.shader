Shader "Bluecadet/UIBlur/UIBlur"
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
            "Queue" = "Overlay"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag_horizontal
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Global texture set by CaptureTextureFeature
            TEXTURE2D(_UIBlurTexture);
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
                float4 screenPos : TEXCOORD1;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                OUT.screenPos = ComputeScreenPos(OUT.positionCS);
                return OUT;
            }

            half4 frag_horizontal(Varyings IN) : SV_Target
            {
                float2 screenUV = IN.screenPos.xy / IN.screenPos.w;

                float4 sample = SAMPLE_TEXTURE2D(_UIBlurTexture, sampler_UIBlurTexture, screenUV);

                return sample * _Tint;
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
