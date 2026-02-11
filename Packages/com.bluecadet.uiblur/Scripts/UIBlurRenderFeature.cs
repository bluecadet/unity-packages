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
    private Texture2D clearTexture;

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

      // In scene view, set a clear texture so shaders sampling the blur texture
      // get transparent instead of black, then skip the actual blur pass.
      if (renderingData.cameraData.cameraType == CameraType.SceneView) {
        if (clearTexture == null) {
          clearTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
          clearTexture.SetPixel(0, 0, Color.clear);
          clearTexture.Apply();
        }
        Shader.SetGlobalTexture(settings.renderTextureName, clearTexture);
        return;
      }

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
        if (clearTexture != null) {
          CoreUtils.Destroy(clearTexture);
          clearTexture = null;
        }
      }
    }

  }

}