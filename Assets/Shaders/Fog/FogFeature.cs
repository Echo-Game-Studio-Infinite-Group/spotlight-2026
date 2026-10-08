using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Post-process height fog / haze for the showcase scene.
/// Parameters live on the renderer feature itself, so no scene material is required.
/// </summary>
public class FogFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class FogSettings
    {
        public bool isEnabled = true;
        public RenderPassEvent renderPassEvent = (RenderPassEvent)546;

        [Header("Height Fog")]
        [ColorUsage(false, true)]
        public Color fogColor = new Color(0.55f, 0.68f, 0.85f, 1f);
        [Range(0f, 0.2f)]
        public float fogDensity = 0.008f;
        [Range(0f, 1f)]
        public float maxFogOpacity = 0.35f;
        [Tooltip("Base fog height in world-space Y.")]
        public float fogHeightStart = 0f;
        [Tooltip("How quickly fog fades with height. Higher values make upper areas clearer.")]
        public float heightFalloff = 0.05f;

        [Header("Proximity Fade")]
        public float fogStartDistance = 15f;
        public float fogStartFadeRange = 25f;

        [Header("Skybox Horizon")]
        [Range(1f, 50f)]
        public float skyboxZenithFade = 15f;

        [Header("Directional Light Scattering")]
        [ColorUsage(false, true)]
        public Color inscatterColor = new Color(1.0f, 0.95f, 0.8f, 1f);
        [Range(-0.99f, 0.99f)]
        public float scatteringG = 0.55f;
        [Range(0f, 4f)]
        public float sunScatterIntensity = 0.45f;
        public float inscatterStartDistance = 50f;
    }

    public FogSettings settings = new FogSettings();

    [SerializeField, HideInInspector] private Shader m_Shader;
    private Material m_Material;
    private FogRenderPass m_Pass;

    private const string k_ShaderName = "Hidden/OurFunction/HeightFog";

    public override void Create()
    {
        m_Pass = new FogRenderPass();
        GetMaterial();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!settings.isEnabled) return;
        // Keep this to Game cameras. Scene View depth attachments can be unstable while
        // the editor repaints, which makes fog debugging noisier than useful.
        if (renderingData.cameraData.cameraType != CameraType.Game)
            return;
        if (renderingData.cameraData.renderType != CameraRenderType.Base)
            return;

        if (!GetMaterial())
        {
            Debug.LogWarning("[Fog] Shader not found: " + k_ShaderName);
            return;
        }

        m_Pass.renderPassEvent = settings.renderPassEvent;
        m_Pass.Setup(m_Material, settings);
        m_Pass.ConfigureInput(ScriptableRenderPassInput.Depth);
        renderer.EnqueuePass(m_Pass);
    }

    public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
    {
        if (m_Pass != null)
            m_Pass.SetCameraTargets(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(m_Material);
    }

    private bool GetMaterial()
    {
        if (m_Material != null)
            return true;

        if (m_Shader == null)
            m_Shader = Shader.Find(k_ShaderName);

        if (m_Shader == null)
            return false;

        m_Material = CoreUtils.CreateEngineMaterial(m_Shader);
        return m_Material != null;
    }

    class FogRenderPass : ScriptableRenderPass
    {
        private Material material;
        private FogSettings settings;
        private RTHandle source;
        private RTHandle depth;

        public void SetCameraTargets(RTHandle color, RTHandle cameraDepth)
        { source = color; depth = cameraDepth; }

        private static readonly int s_TempID = Shader.PropertyToID("_TempHeightFog");
        private static readonly int s_SourceTex = Shader.PropertyToID("_HeightFog_SourceTex");
        private static readonly int s_FogColor = Shader.PropertyToID("_FogColor");
        private static readonly int s_FogDensity = Shader.PropertyToID("_FogDensity");
        private static readonly int s_MaxFogOpacity = Shader.PropertyToID("_MaxFogOpacity");
        private static readonly int s_FogHeightStart = Shader.PropertyToID("_FogHeightStart");
        private static readonly int s_HeightFalloff = Shader.PropertyToID("_HeightFalloff");
        private static readonly int s_FogStartDistance = Shader.PropertyToID("_FogStartDistance");
        private static readonly int s_FogStartFadeRange = Shader.PropertyToID("_FogStartFadeRange");
        private static readonly int s_SkyboxZenithFade = Shader.PropertyToID("_SkyboxZenithFade");
        private static readonly int s_InscatterColor = Shader.PropertyToID("_InscatterColor");
        private static readonly int s_ScatteringG = Shader.PropertyToID("_ScatteringG");
        private static readonly int s_SunScatterIntensity = Shader.PropertyToID("_SunScatterIntensity");
        private static readonly int s_InscatterStartDistance = Shader.PropertyToID("_InscatterStartDistance");

        private readonly ProfilingSampler sampler = new ProfilingSampler("Post Height Fog");

        public void Setup(Material mat, FogSettings s)
        {
            material = mat;
            settings = s;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            ConfigureTarget(source, depth);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (material == null)
                return;

            material.SetColor(s_FogColor, settings.fogColor);
            var cameraOverride = renderingData.cameraData.camera.GetComponent<FogCameraOverride>();
            bool useOverride = cameraOverride != null && cameraOverride.isActiveAndEnabled;
            material.SetFloat(s_FogDensity, useOverride ? Mathf.Max(0, cameraOverride.density) : settings.fogDensity);
            material.SetFloat(s_MaxFogOpacity, useOverride ? Mathf.Clamp01(cameraOverride.maxOpacity) : settings.maxFogOpacity);
            material.SetFloat(s_FogHeightStart, settings.fogHeightStart);
            material.SetFloat(s_HeightFalloff, settings.heightFalloff);
            material.SetFloat(s_FogStartDistance, settings.fogStartDistance);
            material.SetFloat(s_FogStartFadeRange, settings.fogStartFadeRange);
            material.SetFloat(s_SkyboxZenithFade, settings.skyboxZenithFade);
            material.SetColor(s_InscatterColor, settings.inscatterColor);
            material.SetFloat(s_ScatteringG, settings.scatteringG);
            material.SetFloat(s_SunScatterIntensity, settings.sunScatterIntensity);
            material.SetFloat(s_InscatterStartDistance, settings.inscatterStartDistance);

            CommandBuffer cmd = CommandBufferPool.Get("Post Height Fog");
            using (new ProfilingScope(cmd, sampler))
            {
                RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                desc.bindMS = false;
                cmd.GetTemporaryRT(s_TempID, desc, FilterMode.Bilinear);

                cmd.Blit(source.nameID, s_TempID);
                cmd.SetGlobalTexture(s_SourceTex, s_TempID);
                cmd.SetRenderTarget(source.nameID, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
                    depth.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            cmd.ReleaseTemporaryRT(s_TempID);
        }
    }
}
