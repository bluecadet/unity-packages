Shader "Bluecadet/UIBlur/KawaseDualFilterHDRP"
{
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        float _Offset;

        half4 DownFrag(Varyings input) : SV_Target
        {
            float2 uv = input.texcoord;
            float2 texelSize = _BlitTexture_TexelSize.xy;
            float o = _Offset;

            half4 sum = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-o, -o) * texelSize);
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-o, o) * texelSize);
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(o, -o) * texelSize);
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(o, o) * texelSize);

            return sum * 0.25h;
        }

        half4 UpFrag(Varyings input) : SV_Target
        {
            float2 uv = input.texcoord;
            float2 texelSize = _BlitTexture_TexelSize.xy;
            float o = _Offset;

            half4 sum = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-o, -o) * texelSize) * 0.0625h;
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(0, -o) * texelSize) * 0.125h;
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(o, -o) * texelSize) * 0.0625h;

            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-o, 0) * texelSize) * 0.125h;
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv) * 0.25h;
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(o, 0) * texelSize) * 0.125h;

            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(-o, o) * texelSize) * 0.0625h;
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(0, o) * texelSize) * 0.125h;
            sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + float2(o, o) * texelSize) * 0.0625h;

            return sum;
        }
        ENDHLSL

        Pass
        {
            Name "Downsample"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DownFrag
            ENDHLSL
        }

        Pass
        {
            Name "Upsample"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment UpFrag
            ENDHLSL
        }
    }
}
