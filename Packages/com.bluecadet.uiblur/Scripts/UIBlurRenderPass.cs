using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Experimental.Rendering;

namespace Bluecadet.UIBlur
{

  public class UIBlurRenderPass : ScriptableRenderPass
  {
    // Material and shader properties
    private Material blurMaterial;
    private static readonly int OffsetId = Shader.PropertyToID("_Offset");

    // Shader pass indices
    private const int PASS_DOWNSAMPLE = 0;
    private const int PASS_UPSAMPLE = 1;
    private const int MAX_PASSES = 6;

    // Configuration
    private float blurScale;
    private string renderTextureName;
    private int renderTextureId;
    private float resolutionScale;

    public UIBlurRenderPass(Material material)
    {
      blurMaterial = material;
    }

    public void Setup(float scale, string textureName, float resScale)
    {
      blurScale = scale;
      renderTextureName = textureName;
      renderTextureId = Shader.PropertyToID(textureName);
      resolutionScale = Mathf.Clamp(resScale, 0.1f, 1f);
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
      if (blurMaterial == null)
        return;

      // Get camera data from frame data
      UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
      UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

      // Get camera descriptor and calculate scaled resolution
      RenderTextureDescriptor descriptor = cameraData.cameraTargetDescriptor;
      int width = Mathf.CeilToInt(descriptor.width * resolutionScale);
      int height = Mathf.CeilToInt(descriptor.height * resolutionScale);

      // Create texture descriptor for blur textures
      TextureDesc textureDesc = new TextureDesc(width, height);
      textureDesc.colorFormat = GraphicsFormat.R16G16B16A16_SFloat;
      textureDesc.depthBufferBits = DepthBits.None;
      textureDesc.msaaSamples = MSAASamples.None;
      textureDesc.filterMode = FilterMode.Bilinear;
      textureDesc.wrapMode = TextureWrapMode.Clamp;

      // If no blur, skip everything and just passthrough
      if (blurScale <= 0.01f)
      {
        textureDesc.width = width;
        textureDesc.height = height;
        textureDesc.name = "UIBlur_Passthrough";
        TextureHandle passthrough = renderGraph.CreateTexture(textureDesc);

        AddBlitPass(renderGraph, "UIBlur_Passthrough", resourceData.activeColorTexture, passthrough,
                    blurMaterial, PASS_DOWNSAMPLE, 0f, true, renderTextureId);
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

      // Build standard 0.5x resolution chain
      List<KawaseDualFilter.Resolution> resolutions = new List<KawaseDualFilter.Resolution>();
      float currentWidth = width;
      float currentHeight = height;
      for (int i = 0; i < actualPasses; i++)
      {
        currentWidth *= 0.5f;
        currentHeight *= 0.5f;
        resolutions.Add(new KawaseDualFilter.Resolution(currentWidth, currentHeight));
      }

      TextureHandle source = resourceData.activeColorTexture;
      TextureHandle target;

      // --- DOWNSAMPLE PHASE ---
      for (int i = 0; i < resolutions.Count; i++)
      {
        var res = resolutions[i];
        textureDesc.width = res.Width;
        textureDesc.height = res.Height;
        textureDesc.name = $"UIBlur_Down_{i}";
        target = renderGraph.CreateTexture(textureDesc);

        AddBlitPass(renderGraph, $"UIBlur_Downsample_{i}", source, target,
                    blurMaterial, PASS_DOWNSAMPLE, sampleOffset, false, 0);

        source = target;
      }

      // --- UPSAMPLE PHASE ---
      for (int i = resolutions.Count - 2; i >= 0; i--)
      {
        var res = resolutions[i];
        textureDesc.width = res.Width;
        textureDesc.height = res.Height;
        textureDesc.name = $"UIBlur_Up_{i}";
        target = renderGraph.CreateTexture(textureDesc);

        AddBlitPass(renderGraph, $"UIBlur_Upsample_{i}", source, target,
                    blurMaterial, PASS_UPSAMPLE, sampleOffset, false, 0);

        source = target;
      }

      // Final upsample to original resolution
      textureDesc.width = width;
      textureDesc.height = height;
      textureDesc.name = "UIBlur_Final";
      target = renderGraph.CreateTexture(textureDesc);

      AddBlitPass(renderGraph, "UIBlur_Final", source, target,
                  blurMaterial, PASS_UPSAMPLE, sampleOffset, true, renderTextureId);
    }

    private void AddBlitPass(RenderGraph renderGraph, string passName, TextureHandle source,
                             TextureHandle destination, Material material, int pass, float offset,
                             bool setGlobalTexture, int globalTextureId)
    {
      using (var builder = renderGraph.AddRasterRenderPass<BlitPassData>(passName, out var passData))
      {
        passData.material = material;
        passData.passIndex = pass;
        passData.offset = offset;
        passData.source = source;

        builder.UseTexture(source, AccessFlags.Read);
        builder.SetRenderAttachment(destination, 0, AccessFlags.Write);

        // Set global texture if this is the final pass
        if (setGlobalTexture)
        {
          builder.SetGlobalTextureAfterPass(destination, globalTextureId);
        }

        builder.SetRenderFunc((BlitPassData data, RasterGraphContext context) =>
        {
          // Set shader properties before blit
          data.material.SetFloat(OffsetId, data.offset);

          // Blit using Blitter API which handles texture binding in render graph
          // Blitter automatically binds data.source to _BlitTexture
          Blitter.BlitTexture(context.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, data.passIndex);
        });
      }
    }

    private class BlitPassData
    {
      internal Material material;
      internal int passIndex;
      internal float offset;
      internal TextureHandle source;
    }

    public void Dispose()
    {
      // No manual cleanup needed with render graph
    }
  }

}