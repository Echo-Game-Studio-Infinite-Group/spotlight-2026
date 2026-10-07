using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Inserts GTAO into URP's built-in screen-space occlusion path.
/// In Deferred this runs after GBuffer and before deferred lighting, so lighting can sample
/// _ScreenSpaceOcclusionTexture. Debug mode is a simple full-screen AO blit.
/// </summary>
public class GTAORendererFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class GTAOSettings
    {
        public bool isEnabled = true;
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingGbuffer;
        [Tooltip("Legacy fallback for non-Universal renderers. URP selects camera normals or GBuffer from its actual rendering mode automatically.")]
        public bool useForwardNormals = false;
        public ComputeShader gtaoComputeShader;
    }

    public GTAOSettings settings = new GTAOSettings();

    [SerializeField, HideInInspector] private Shader compositeShader;

    private GTAORenderPass gtaoRenderPass;
    private GTAODebugBlitPass debugBlitPass;
    private GTAOCompositePass compositePass;
    private Material compositeMaterial;

    private const string CompositeShaderName = "Hidden/OurFunction/GTAOComposite";

    // URP 14 exposes the actual (including per-camera Forward fallback) mode only
    // internally. Read the existing property without changing renderer/asset settings.
    private static readonly System.Reflection.PropertyInfo ActualRenderingModeProperty =
        typeof(UniversalRenderer).GetProperty("renderingModeActual",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

    private static bool UsesForwardPath(ScriptableRenderer renderer, bool legacyFallback)
    {
        if (renderer is UniversalRenderer && ActualRenderingModeProperty != null)
            return (RenderingMode)ActualRenderingModeProperty.GetValue(renderer) != RenderingMode.Deferred;
        return legacyFallback;
    }

    static bool ShouldRenderCamera(ref RenderingData renderingData)
    {
        CameraData cameraData = renderingData.cameraData;
        if (cameraData.renderType != CameraRenderType.Base)
            return false;

        return cameraData.cameraType == CameraType.Game || cameraData.cameraType == CameraType.SceneView;
    }

    public override void Create()
    {
        gtaoRenderPass?.Dispose();
        CoreUtils.Destroy(compositeMaterial);
        compositeMaterial = null;
        gtaoRenderPass = new GTAORenderPass(settings);
        debugBlitPass = new GTAODebugBlitPass();
        compositePass = new GTAOCompositePass();
        EnsureCompositeMaterial();
    }

    protected override void Dispose(bool disposing)
    {
        gtaoRenderPass?.Dispose();
        CoreUtils.Destroy(compositeMaterial);
        compositeMaterial = null;
        base.Dispose(disposing);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!settings.isEnabled || !ShouldRenderCamera(ref renderingData))
            return;

        GroundTruthAmbientOcclusion gtao = VolumeManager.instance.stack.GetComponent<GroundTruthAmbientOcclusion>();
        if (gtao == null || !gtao.IsActive())
            return;

        if (settings.gtaoComputeShader == null)
        {
            if (Time.frameCount % 120 == 0)
                Debug.LogWarning("[GTAO] Compute shader is missing. Assign GTAOComputeShader.compute on the GTAO renderer feature.");
            return;
        }

        bool forwardPath = UsesForwardPath(renderer, settings.useForwardNormals);
        // Deferred's depth-normal prepass primes the camera attachment, not
        // _CameraDepthTexture. The GBuffer depth copy (211) must finish first.
        // Publish AO before lighting consumes it, even if a legacy asset requests
        // an event intended for a Forward renderer. Do not overwrite serialized values.
        gtaoRenderPass.renderPassEvent = forwardPath
            ? RenderPassEvent.AfterRenderingPrePasses
            : (RenderPassEvent)Mathf.Clamp((int)settings.renderPassEvent,
                (int)RenderPassEvent.AfterRenderingGbuffer, (int)RenderPassEvent.BeforeRenderingDeferredLights - 1);
        gtaoRenderPass.Setup(gtao, renderer);
        gtaoRenderPass.ConfigureInput(forwardPath
            ? ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal
            : ScriptableRenderPassInput.Depth);
        renderer.EnqueuePass(gtaoRenderPass);

        if (gtao.debugAOToScreen.value)
        {
            debugBlitPass.renderPassEvent = (RenderPassEvent)547;
            debugBlitPass.Setup(renderer);
            renderer.EnqueuePass(debugBlitPass);
            return;
        }

        if (gtao.RequiresComposite())
        {
            if (!EnsureCompositeMaterial())
            {
                if (Time.frameCount % 120 == 0)
                    Debug.LogWarning("[GTAO] Composite shader is missing. Multi-Bounce/SSDO composite is skipped.");
                return;
            }

            compositePass.renderPassEvent = (RenderPassEvent)547;
            compositePass.Setup(gtao, renderer, compositeMaterial, forwardPath);
            compositePass.ConfigureInput(ScriptableRenderPassInput.Depth);
            renderer.EnqueuePass(compositePass);
        }
    }

    private bool EnsureCompositeMaterial()
    {
        if (compositeMaterial != null)
            return true;

        if (compositeShader == null)
            compositeShader = Shader.Find(CompositeShaderName);
        if (compositeShader == null)
            return false;

        compositeMaterial = CoreUtils.CreateEngineMaterial(compositeShader);
        return compositeMaterial != null;
    }
}
