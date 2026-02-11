using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Experimental.Rendering;

namespace Bluecadet.UIBlur.HDRP
{
    [System.Serializable]
    public class UIBlurCustomPass : CustomPass
    {
        // [Header("Blur Configuration")]
        [SerializeField, Range(0f, 64f)]
        public float blurScale = 4f;

        // [Header("Render Configuration")]
        [SerializeField]
        public string renderTextureName = "_UIBlurTexture";

        // [Header("Performance")]
        [SerializeField, Range(0.25f, 1f)]
        public float resolutionScale = 0.5f;

        private Material blurMaterial;
        private static readonly int OffsetId = Shader.PropertyToID("_Offset");
        private static readonly int BlitTextureId = Shader.PropertyToID("_BlitTexture");
        private static readonly int BlitTextureSizeId = Shader.PropertyToID("_BlitTexture_TexelSize");
        private static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");

        private const int PASS_DOWNSAMPLE = 0;
        private const int PASS_UPSAMPLE = 1;
        private const int MAX_PASSES = 6;

        // Pre-allocated RTHandles for all possible passes
        private RTHandle[] blurRTs;
        private RTHandle finalRT;
        private int renderTextureId;

        protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
        {
            Shader blurShader = Shader.Find("Bluecadet/UIBlur/KawaseDualFilterHDRP");
            if (blurShader == null)
            {
                Debug.LogError("[UIBlurCustomPass] Could not find shader");
                return;
            }
            blurMaterial = CoreUtils.CreateEngineMaterial(blurShader);
            renderTextureId = Shader.PropertyToID(renderTextureName);

            AllocateRTs();
        }

        private void AllocateRTs()
        {
            blurRTs = new RTHandle[MAX_PASSES];
            float scale = resolutionScale;

            for (int i = 0; i < MAX_PASSES; i++)
            {
                scale *= 0.5f;
                blurRTs[i] = RTHandles.Alloc(
                    Vector2.one * scale, TextureXR.slices,
                    dimension: TextureXR.dimension,
                    colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                    useDynamicScale: true,
                    name: $"UIBlur_{i}"
                );
            }

            finalRT = RTHandles.Alloc(
                Vector2.one, TextureXR.slices,
                dimension: TextureXR.dimension,
                colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                useDynamicScale: true,
                name: "UIBlur_Final"
            );
        }

        protected override void Cleanup()
        {
            CoreUtils.Destroy(blurMaterial);
            if (blurRTs != null)
            {
                foreach (var rt in blurRTs)
                    rt?.Release();
                blurRTs = null;
            }
            finalRT?.Release();
            finalRT = null;
        }

        protected override void Execute(CustomPassContext ctx)
        {
            if (blurMaterial == null || blurRTs == null) return;

            // Skip blur in scene view to avoid flashing artifacts
            if (ctx.hdCamera.camera.cameraType == CameraType.SceneView)
            {
                CoreUtils.SetRenderTarget(ctx.cmd, finalRT, ClearFlag.Color, Color.clear);
                ctx.cmd.SetGlobalTexture(renderTextureId, finalRT);
                return;
            }

            // Early exit for near-zero blur - just copy camera buffer
            if (blurScale <= 0.01f)
            {
                ctx.cmd.SetGlobalFloat(OffsetId, 0f);
                SetSourceTexture(ctx, ctx.cameraColorBuffer);
                CoreUtils.SetRenderTarget(ctx.cmd, finalRT);
                CoreUtils.DrawFullScreen(ctx.cmd, blurMaterial, PASS_DOWNSAMPLE);
                ctx.cmd.SetGlobalTexture(renderTextureId, finalRT);
                return;
            }

            // Dynamic pass count with offset-based smooth transitions
            //
            // Model: effective_blur ≈ offset × 2^N
            // For continuous blur: offset = blurScale / 2^N
            //
            // When N increases by 1, offset halves but 2^N doubles → blur stays constant.
            // We choose N so that offset stays in a quality range (not too high, not too low).
            //
            // Transition points (where offset hits max threshold of 2.0):
            //   N=1 handles blurScale 0-4, N=2 handles 4-8, N=3 handles 8-16, etc.

            const float maxOffset = 2.0f;

            // Find minimum pass count where offset stays within quality range
            int actualPasses = MAX_PASSES;
            for (int n = 1; n <= MAX_PASSES; n++)
            {
                float neededOffset = blurScale / Mathf.Pow(2f, n);
                if (neededOffset <= maxOffset)
                {
                    actualPasses = n;
                    break;
                }
            }

            // Calculate offset to achieve exact target blur
            // blur = offset × 2^N  →  offset = blurScale / 2^N
            float sampleOffset = blurScale / Mathf.Pow(2f, actualPasses);
            ctx.cmd.SetGlobalFloat(OffsetId, sampleOffset);

            // DOWNSAMPLE: camera -> RT0 -> RT1 -> RT2 ...
            // First pass from camera
            SetSourceTexture(ctx, ctx.cameraColorBuffer);
            CoreUtils.SetRenderTarget(ctx.cmd, blurRTs[0]);
            CoreUtils.DrawFullScreen(ctx.cmd, blurMaterial, PASS_DOWNSAMPLE);

            // Subsequent downsamples
            for (int i = 1; i < actualPasses; i++)
            {
                SetSourceTexture(ctx, blurRTs[i - 1]);
                CoreUtils.SetRenderTarget(ctx.cmd, blurRTs[i]);
                CoreUtils.DrawFullScreen(ctx.cmd, blurMaterial, PASS_DOWNSAMPLE);
            }

            // UPSAMPLE: RTn -> RTn-1 -> ... -> RT0 -> final
            for (int i = actualPasses - 2; i >= 0; i--)
            {
                SetSourceTexture(ctx, blurRTs[i + 1]);
                CoreUtils.SetRenderTarget(ctx.cmd, blurRTs[i]);
                CoreUtils.DrawFullScreen(ctx.cmd, blurMaterial, PASS_UPSAMPLE);
            }

            // Final upsample
            SetSourceTexture(ctx, blurRTs[0]);
            CoreUtils.SetRenderTarget(ctx.cmd, finalRT);
            CoreUtils.DrawFullScreen(ctx.cmd, blurMaterial, PASS_UPSAMPLE);

            ctx.cmd.SetGlobalTexture(renderTextureId, finalRT);
        }

        private void SetSourceTexture(CustomPassContext ctx, RTHandle source)
        {
            ctx.cmd.SetGlobalTexture(BlitTextureId, source);
            Vector2Int size = source.GetScaledSize(source.rtHandleProperties.currentViewportSize);
            ctx.cmd.SetGlobalVector(BlitTextureSizeId, new Vector4(
                1f / size.x,
                1f / size.y,
                size.x,
                size.y
            ));
            ctx.cmd.SetGlobalVector(BlitScaleBiasId, new Vector4(1f, 1f, 0f, 0f));
        }
    }
}
