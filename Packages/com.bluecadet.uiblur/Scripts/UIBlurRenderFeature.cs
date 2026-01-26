using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Bluecadet.UIBlur {

  [System.Serializable]
  public class BlurSettings {
    [Header("Blur Configuration")]
    [Range(0f, 64f)]
    [Tooltip("Blur scale - higher values produce stronger blur")]
    public float blurScale = 4f;

    [Header("Render Configuration")]
    [Tooltip("Name of the global RenderTexture that UI shaders can sample")]
    public string renderTextureName = "_UIBlurTexture";

    [Header("Performance")]
    [Range(0.25f, 1f)]
    [Tooltip("Resolution scale - lower values improve performance")]
    public float resolutionScale = 0.5f;
  }

  public class UIBlurRenderFeature : ScriptableRendererFeature {

    [SerializeField]
    private BlurSettings settings = new BlurSettings();

    public float BlurScale {
      get => settings.blurScale;
      set => settings.blurScale = value;
    }

    [SerializeField]
    public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingTransparents;

    private UIBlurRenderPass renderPass;
    private Material blurMaterial;
    

    public override void Create() {
      // Load the Kawase shader
      Shader blurShader = Shader.Find("Bluecadet/UIBlur/KawaseDualFilter");

      if (blurShader == null) {
        Debug.LogError("[UIBlurRenderFeature] Could not find shader 'Bluecadet/UIBlur/KawaseDualFilter'");
        return;
      }

      // Create material
      blurMaterial = CoreUtils.CreateEngineMaterial(blurShader);

      // Create the render pass
      renderPass = new UIBlurRenderPass(blurMaterial) {
        renderPassEvent = renderPassEvent
      };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
      // Validation checks
      if (renderPass == null || blurMaterial == null)
        return;

      // Don't run in scene view unless specifically enabled
      if (renderingData.cameraData.cameraType == CameraType.SceneView)
        return;

      // Don't run if camera is not rendering
      if (!renderingData.cameraData.camera.enabled)
        return;

      // Configure the render pass with current settings
      renderPass.Setup(
        settings.blurScale,
        settings.renderTextureName,
        settings.resolutionScale
      );

      // Enqueue the render pass
      renderer.EnqueuePass(renderPass);
    }

    protected override void Dispose(bool disposing) {
      if (disposing) {
        renderPass?.Dispose();
        CoreUtils.Destroy(blurMaterial);
      }
    }

  }

}