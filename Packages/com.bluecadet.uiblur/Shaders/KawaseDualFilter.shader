Shader "Bluecadet/UIBlur/KawaseDualFilter"
{
    Properties
    {
        [MainTexture] _MainTex ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        // No culling or depth
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        float _Offset;
        

        half4 downFrag(Varyings input) : SV_Target
        {
            // Kawase downsample - 4 bilinear samples in box pattern
            float2 uv = input.texcoord;
            float2 texelSize = _BlitTexture_TexelSize.xy;

            half4 sum = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(-1, -1) * texelSize * _Offset);
            sum += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(-1, 1) * texelSize * _Offset);
            sum += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(1, -1) * texelSize * _Offset);
            sum += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(1, 1) * texelSize * _Offset);

            return sum * 0.25h;
        }

        half4 upFrag(Varyings input) : SV_Target
        {
            // Kawase upsample - tent filter with 9 samples
            float2 uv = input.texcoord;
            float2 texelSize = _BlitTexture_TexelSize.xy;

            half4 sum = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(-1, -1) * texelSize * _Offset) * 0.0625h;
            sum += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(0, -1) * texelSize * _Offset) * 0.125h;
            sum += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(1, -1) * texelSize * _Offset) * 0.0625h;

            sum += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(-1, 0) * texelSize * _Offset) * 0.125h;
            sum += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv) * 0.25h;
            sum += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(1, 0) * texelSize * _Offset) * 0.125h;

            sum += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(-1, 1) * texelSize * _Offset) * 0.0625h;
            sum += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(0, 1) * texelSize * _Offset) * 0.125h;
            sum += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(1, 1) * texelSize * _Offset) * 0.0625h;

            return sum;
        }
        ENDHLSL

        Pass
        {
            Name "Downsample"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment downFrag
            ENDHLSL
        }

        Pass
        {
            Name "Upsample"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment upFrag
            ENDHLSL
        }
    }
}