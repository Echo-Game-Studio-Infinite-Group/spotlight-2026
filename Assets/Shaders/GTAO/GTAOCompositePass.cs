using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Optional GTAO enhancement layer.
/// It composites color multi-bounce and a lightweight SSDO bounce after lighting,
/// while leaving the core grayscale GTAO path untouched.
/// </summary>
public class GTAOCompositePass : ScriptableRenderPass
{
    private static readonly int SourceTexID = Shader.PropertyToID("_GTAOComposite_SourceTex");
    private static readonly int SourceTexelSizeID = Shader.PropertyToID("_GTAOComposite_SourceTex_TexelSize");
    private static readonly int ForwardModeID = Shader.PropertyToID("_GTAOComposite_ForwardMode");
    private static readonly int TempSourceID = Shader.PropertyToID("_GTAOComposite_Source");
    private static readonly int CompositeParamsID = Shader.PropertyToID("_GTAOCompositeParams");
    private static readonly int SSDOParamsID = Shader.PropertyToID("_GTAOSSDOParams");

    private readonly ProfilingSampler compositeSampler = new ProfilingSampler("GTAO Multi-Bounce / SSDO");

    private GroundTruthAmbientOcclusion settings;
    private ScriptableRenderer renderer;
    private Material material;
    private bool forwardMode;

    public void Setup(GroundTruthAmbientOcclusion settings, ScriptableRenderer renderer, Material material, bool forwardMode)
    {
        this.settings = settings;
        this.renderer = renderer;
        this.material = material;
        this.forwardMode = forwardMode;
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (settings == null || renderer == null || material == null)
            return;

        bool multiBounceEnabled = settings.enableAO.value && settings.enableMultiBounce.value && settings.multiBounceStrength.value > 0.0f;
        bool debugSSDO = settings.debugSSDOToScreen.value;
        bool ssdoEnabled = (settings.enableSSDO.value || debugSSDO) && settings.ssdoIntensity.value > 0.0f;
        if (!multiBounceEnabled && !ssdoEnabled && !debugSSDO)
            return;

        RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
        desc.depthBufferBits = 0;
        desc.msaaSamples = 1;

        CommandBuffer cmd = CommandBufferPool.Get("GTAO Multi-Bounce / SSDO");
        using (new ProfilingScope(cmd, compositeSampler))
        {
            RenderTargetIdentifier source = renderer.cameraColorTargetHandle.nameID;
            cmd.GetTemporaryRT(TempSourceID, desc, FilterMode.Bilinear);
            cmd.Blit(source, TempSourceID);

            material.SetVector(CompositeParamsID, new Vector4(
                multiBounceEnabled ? 1.0f : 0.0f,
                settings.multiBounceStrength.value,
                ssdoEnabled ? 1.0f : 0.0f,
                debugSSDO ? 1.0f : 0.0f));

            material.SetVector(SSDOParamsID, new Vector4(
                settings.ssdoIntensity.value,
                settings.ssdoRadius.value,
                settings.ssdoSampleCount.value,
                settings.ssdoMaxContribution.value));

            cmd.SetGlobalTexture(SourceTexID, TempSourceID);
            material.SetFloat(ForwardModeID, forwardMode ? 1f : 0f);
            material.SetVector(SourceTexelSizeID, new Vector4(
                1.0f / desc.width,
                1.0f / desc.height,
                desc.width,
                desc.height));
            // 保留 URP 相机深度附件，避免后续透明 Pass 继承仅颜色绑定。
            cmd.SetRenderTarget(source, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
                renderer.cameraDepthTargetHandle.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1);
            cmd.ReleaseTemporaryRT(TempSourceID);
        }

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }
}
