using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class VolumetricLightRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class VolumetricLightSettings
    {
        public enum DebugView
        {
            Off,
            SourceCapture,
            RawLight,
            BlurredLight
        }

        public bool isEnabled = true;
        // Resolve after transparents and before custom TAA (502), Nightsoul (503), fog and Bloom.
        public RenderPassEvent renderPassEvent = (RenderPassEvent)501;

        [Header("Scattering")]
        [ColorUsage(false, true)] public Color scatteringTint = new Color(1.0f, 0.95f, 0.85f, 1.0f);
        [Range(0f, 4f)] public float intensity = 4f;
        [Min(0f)] public float density = 0.035f;
        [Range(-0.95f, 0.95f)] public float anisotropy = -0.839f;
        [Min(1f)] public float maxDistance = 10f;

        [Header("Height Mask")]
        public float heightFogBase = 12f;
        [Min(0f)] public float heightFalloff = 0.69f;

        [Header("Shadowing")]
        [Range(0f, 1f)] public float shadowStrength = 0.887f;
        [Range(0f, 2f)] public float shadowBias = 0.732f;
        [Range(0f, 1f)] public float jitterStrength = 0.451f;

        [Header("Quality")]
        [Range(4, 128)] public int rayMarchSteps = 76;
        [Range(1, 4)] public int downsample = 2;
        [Range(0, 4)] public int blurIterations = 3;
        [Range(0.25f, 3f)] public float blurRadius = 1.901f;
        [Range(0.01f, 5f)] public float depthAwareBlurThreshold = 2.33f;

        [Header("Debug")]
        public DebugView debugView = DebugView.Off;
        public bool logDebugInfo;
        [Min(0.1f)] public float debugLogInterval = 1f;
        public string debugSourceTextureName = "_VolumetricLight_Debug_SourceCapture";
        public string debugRawTextureName = "_VolumetricLight_Debug_RawLight";
        public string debugBlurTextureName = "_VolumetricLight_Debug_BlurredLight";
    }

    public VolumetricLightSettings settings = new VolumetricLightSettings();

    [SerializeField] private Shader shader;

    private Material material;
    private VolumetricLightPass pass;

    private const string ShaderName = "Hidden/OurFunction/VolumetricLight";

    public override void Create()
    {
        pass = new VolumetricLightPass(settings);
        EnsureMaterial();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!settings.isEnabled)
            return;
        if (renderingData.cameraData.cameraType != CameraType.Game)
            return;
        if (renderingData.cameraData.renderType != CameraRenderType.Base)
            return;
        if (renderingData.lightData.mainLightIndex < 0)
            return;
        if (!EnsureMaterial())
            return;

        pass.renderPassEvent = settings.renderPassEvent;
        pass.Setup(renderer, material);
        pass.ConfigureInput(ScriptableRenderPassInput.Depth);
        renderer.EnqueuePass(pass);
    }

    public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
    {
        if (pass != null) pass.SetCameraTargets(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(material);
        material = null;
        base.Dispose(disposing);
    }

    private bool EnsureMaterial()
    {
        if (material != null)
            return true;

        if (shader == null)
            shader = Shader.Find(ShaderName);
        if (shader == null)
            return false;

        material = CoreUtils.CreateEngineMaterial(shader);
        return material != null;
    }

    private sealed class VolumetricLightPass : ScriptableRenderPass
    {
        private static readonly int SourceTexId = Shader.PropertyToID("_Volumetric_SourceTex");
        private static readonly int VolumetricTexId = Shader.PropertyToID("_VolumetricTex");
        private static readonly int RayMarchParamsId = Shader.PropertyToID("_RayMarchParams");
        private static readonly int PhaseParamsId = Shader.PropertyToID("_PhaseParams");
        private static readonly int HeightParamsId = Shader.PropertyToID("_HeightParams");
        private static readonly int ScatteringTintId = Shader.PropertyToID("_ScatteringTint");
        private static readonly int BlurDirectionId = Shader.PropertyToID("_BlurDirection");
        private static readonly int SourceTexelSizeId = Shader.PropertyToID("_SourceTexelSize");

        private static readonly int SourceCopyId = Shader.PropertyToID("_Volumetric_SourceCopy");
        private static readonly int LowResAId = Shader.PropertyToID("_Volumetric_LowResA");
        private static readonly int LowResBId = Shader.PropertyToID("_Volumetric_LowResB");
        private static readonly int DebugSourceCopyId = Shader.PropertyToID("_Volumetric_Debug_SourceCapture");
        private static readonly int DebugRawCopyId = Shader.PropertyToID("_Volumetric_Debug_RawLight");
        private static readonly int DebugBlurCopyId = Shader.PropertyToID("_Volumetric_Debug_BlurredLight");

        private const int PassRayMarch = 0;
        private const int PassBlur = 1;
        private const int PassComposite = 2;
        private const int PassDebugSource = 3;
        private const int PassDebugLight = 4;

        private readonly VolumetricLightSettings settings;
        private readonly ProfilingSampler sampler = new ProfilingSampler("Volumetric Light");

        private ScriptableRenderer renderer;
        private RTHandle cameraColor, cameraDepth;
        private Material material;
        private float nextDebugLogTime;
        private RenderTextureDescriptor lowResDescriptor;
        private bool debugResourcesAllocated;

        public VolumetricLightPass(VolumetricLightSettings settings)
        {
            this.settings = settings;
        }

        public void Setup(ScriptableRenderer renderer, Material material)
        {
            this.renderer = renderer;
            this.material = material;
        }

        public void SetCameraTargets(RTHandle color, RTHandle depth)
        {
            cameraColor = color;
            cameraDepth = depth;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            RenderTextureDescriptor fullDesc = renderingData.cameraData.cameraTargetDescriptor;
            fullDesc.depthBufferBits = 0;
            fullDesc.msaaSamples = 1;

            int downsample = Mathf.Max(1, settings.downsample);
            RenderTextureDescriptor lowDesc = fullDesc;
            lowDesc.width = Mathf.Max(1, fullDesc.width / downsample);
            lowDesc.height = Mathf.Max(1, fullDesc.height / downsample);
            // Light integration is HDR even when the camera color target is LDR.
            lowDesc.colorFormat = RenderTextureFormat.ARGBHalf;
            lowResDescriptor = lowDesc;

            cmd.GetTemporaryRT(SourceCopyId, fullDesc, FilterMode.Bilinear);
            cmd.GetTemporaryRT(LowResAId, lowDesc, FilterMode.Bilinear);
            cmd.GetTemporaryRT(LowResBId, lowDesc, FilterMode.Bilinear);
            debugResourcesAllocated = settings.debugView != VolumetricLightSettings.DebugView.Off
                || settings.logDebugInfo;
            if (debugResourcesAllocated)
            {
                cmd.GetTemporaryRT(DebugSourceCopyId, fullDesc, FilterMode.Bilinear);
                cmd.GetTemporaryRT(DebugRawCopyId, lowDesc, FilterMode.Bilinear);
                cmd.GetTemporaryRT(DebugBlurCopyId, lowDesc, FilterMode.Bilinear);
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (material == null || renderer == null || cameraColor == null || cameraDepth == null)
                return;

            var cameraOverride = renderingData.cameraData.camera.GetComponent<VolumetricLightCameraOverride>();
            bool useOverride = cameraOverride != null && cameraOverride.isActiveAndEnabled;
            bool useScatteringOverride = useOverride && cameraOverride.overrideScattering;
            Vector4 sourceTexelSize = new Vector4(1f / lowResDescriptor.width, 1f / lowResDescriptor.height, lowResDescriptor.width, lowResDescriptor.height);
            Vector4 rayMarchParams = new Vector4(
                Mathf.Clamp(settings.rayMarchSteps, 4, 128),
                Mathf.Max(1f, useOverride ? cameraOverride.maxDistance : settings.maxDistance),
                useScatteringOverride ? Mathf.Max(0f, cameraOverride.intensity) : settings.intensity,
                useScatteringOverride ? Mathf.Max(0f, cameraOverride.density) : settings.density);
            Vector4 phaseParams = new Vector4(
                useScatteringOverride ? Mathf.Clamp(cameraOverride.anisotropy, -0.95f, 0.95f) : settings.anisotropy,
                settings.jitterStrength,
                useOverride ? cameraOverride.shadowStrength : settings.shadowStrength,
                useOverride ? cameraOverride.shadowBias : settings.shadowBias);
            Vector4 heightParams = new Vector4(
                settings.heightFogBase,
                settings.heightFalloff,
                Mathf.Max(0.01f, settings.depthAwareBlurThreshold),
                0f);

            material.SetVector(RayMarchParamsId, rayMarchParams);
            material.SetVector(PhaseParamsId, phaseParams);
            material.SetVector(HeightParamsId, heightParams);
            material.SetColor(ScatteringTintId, settings.scatteringTint);
            material.SetFloat("_VolumetricCascadeCount", renderingData.shadowData.mainLightShadowCascadesCount);
            material.SetVector(SourceTexelSizeId, sourceTexelSize);

            CommandBuffer cmd = CommandBufferPool.Get("Volumetric Light");
            using (new ProfilingScope(cmd, sampler))
            {
                RenderTargetIdentifier source = cameraColor.nameID;

                cmd.Blit(source, SourceCopyId);
                if (debugResourcesAllocated) cmd.Blit(SourceCopyId, DebugSourceCopyId);

                DrawFullscreen(cmd, LowResAId, SourceCopyId, PassRayMarch);
                if (debugResourcesAllocated) cmd.Blit(LowResAId, DebugRawCopyId);

                int blurIterations = Mathf.Max(0, settings.blurIterations);
                if (blurIterations > 0)
                {
                    for (int i = 0; i < blurIterations; ++i)
                    {
                        cmd.SetGlobalVector(BlurDirectionId, new Vector4(settings.blurRadius, 0f, 0f, 0f));
                        DrawFullscreen(cmd, LowResBId, LowResAId, PassBlur);

                        cmd.SetGlobalVector(BlurDirectionId, new Vector4(0f, settings.blurRadius, 0f, 0f));
                        DrawFullscreen(cmd, LowResAId, LowResBId, PassBlur);
                    }
                }

                if (debugResourcesAllocated)
                {
                    cmd.Blit(LowResAId, DebugBlurCopyId);
                    SetDebugTextures(cmd);
                }

                int outputPass = GetOutputPass();
                SetOutputTexture(cmd);
                RenderTargetIdentifier outputSource = outputPass == PassDebugSource ? DebugSourceCopyId : SourceCopyId;
                DrawFullscreen(cmd, source, outputSource, outputPass);
                // Preserve the camera depth/stencil attachment for downstream features.
                cmd.SetRenderTarget(cameraColor.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                    cameraDepth.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            LogDebugInfo(renderingData, lowResDescriptor);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(SourceCopyId);
            cmd.ReleaseTemporaryRT(LowResAId);
            cmd.ReleaseTemporaryRT(LowResBId);
            if (debugResourcesAllocated)
            {
                cmd.ReleaseTemporaryRT(DebugSourceCopyId);
                cmd.ReleaseTemporaryRT(DebugRawCopyId);
                cmd.ReleaseTemporaryRT(DebugBlurCopyId);
            }
        }

        private void DrawFullscreen(CommandBuffer cmd, RenderTargetIdentifier destination, RenderTargetIdentifier source, int passIndex)
        {
            cmd.SetGlobalTexture(SourceTexId, source);
            cmd.SetRenderTarget(destination, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            cmd.DrawProcedural(Matrix4x4.identity, material, passIndex, MeshTopology.Triangles, 3, 1);
        }

        private int GetOutputPass()
        {
            switch (settings.debugView)
            {
                case VolumetricLightSettings.DebugView.SourceCapture:
                    return PassDebugSource;
                case VolumetricLightSettings.DebugView.RawLight:
                    return PassDebugLight;
                case VolumetricLightSettings.DebugView.BlurredLight:
                    return PassDebugLight;
                default:
                    return PassComposite;
            }
        }

        private void SetDebugTextures(CommandBuffer cmd)
        {
            cmd.SetGlobalTexture(settings.debugSourceTextureName, DebugSourceCopyId);
            cmd.SetGlobalTexture(settings.debugRawTextureName, DebugRawCopyId);
            cmd.SetGlobalTexture(settings.debugBlurTextureName, DebugBlurCopyId);
        }

        private void SetOutputTexture(CommandBuffer cmd)
        {
            switch (settings.debugView)
            {
                case VolumetricLightSettings.DebugView.RawLight:
                    cmd.SetGlobalTexture(VolumetricTexId, DebugRawCopyId);
                    break;
                case VolumetricLightSettings.DebugView.BlurredLight:
                    cmd.SetGlobalTexture(VolumetricTexId, DebugBlurCopyId);
                    break;
                default:
                    cmd.SetGlobalTexture(VolumetricTexId, LowResAId);
                    break;
            }
        }

        private void LogDebugInfo(RenderingData renderingData, RenderTextureDescriptor lowDesc)
        {
            if (!settings.logDebugInfo || Time.realtimeSinceStartup < nextDebugLogTime)
                return;

            nextDebugLogTime = Time.realtimeSinceStartup + Mathf.Max(0.1f, settings.debugLogInterval);
            VisibleLight mainLight = renderingData.lightData.visibleLights[renderingData.lightData.mainLightIndex];
            string lightName = mainLight.light != null ? mainLight.light.name : "<no Light component>";
            string shadowInfo = mainLight.light != null
                ? mainLight.light.shadows + ", renderMode=" + mainLight.light.renderMode + ", intensity=" + mainLight.light.intensity.ToString("0.###")
                : "<no Light component>";

            Debug.Log(
                "[VolumetricLight Debug] " +
                "source=cameraColorTarget, " +
                "rawTexture=" + settings.debugRawTextureName + ", " +
                "blurTexture=" + settings.debugBlurTextureName + ", " +
                "sourceTexture=" + settings.debugSourceTextureName + ", " +
                "mainLight=" + lightName + ", " +
                "lightType=" + mainLight.lightType + ", " +
                "lightShadows=" + shadowInfo + ", " +
                "debugView=" + settings.debugView + ", " +
                "lowRes=" + lowDesc.width + "x" + lowDesc.height + ", " +
                "blurIterations=" + settings.blurIterations + ", " +
                "depthAwareBlurThreshold=" + settings.depthAwareBlurThreshold);
        }
    }
}
