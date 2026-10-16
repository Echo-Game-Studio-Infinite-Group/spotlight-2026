using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class SSRRenderFeature : ScriptableRendererFeature
{
    public enum DitherMode
    {
        Dither8x8,
        InterleavedGradient
    }

    public enum DebugView
    {
        Off,
        HitMask,
        ReflectedColor
    }

    [System.Serializable]
    public class SSRSettings
    {
        public bool isEnabled = true;
        public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;

        [Header("Ray Marching")]
        [Min(0.001f)] public float stepStrideLength = 0.03f;
        [Range(8f, 256f)] public float maxSteps = 96f;
        [Range(0f, 1f)] public float minSmoothness = 0.55f;
        [Range(0f, 2f)] public float intensity = 0.75f;
        [Range(0f, 4f), Tooltip("Reflection color gain only. Does not change ray hits, blend weight, or AO; 1 preserves the original result.")]
        public float reflectionBrightness = 1f;
        public bool reflectSky = false;
        [Header("Forward renderer compatibility")]
        [Tooltip("Legacy fallback for non-Universal renderers. URP selects camera normals or GBuffer from its actual rendering mode automatically.")]
        public bool useForwardNormals = false;
        [Range(0f, 1f)] public float forwardSmoothness = 0.8f;

        [Header("Quality")]
        [Range(0, 1)] public int downsample = 0;
        public DitherMode ditherMode = DitherMode.InterleavedGradient;
        [Tooltip("32位命中UV避免高分辨率半浮点量化；只增加命中缓冲，不改变源颜色格式。")]
        public bool highPrecisionTrace = true;
        [Range(0f, 4f), Tooltip("反射采样抖动半径，单位为当前反射缓冲像素。")]
        public float jitterRadiusPixels = 0.75f;
        [Range(0f, 7f)] public float roughnessMipMax = 5f;

        [Header("Debug")]
        public DebugView debugView = DebugView.Off;
    }

    public SSRSettings settings = new SSRSettings();

    [SerializeField, HideInInspector] private Shader shader;

    private Material material;
    private SSRPass pass;

    private const string ShaderName = "Hidden/OurFunction/SSR";

    private static readonly System.Reflection.PropertyInfo ActualRenderingModeProperty =
        typeof(UniversalRenderer).GetProperty("renderingModeActual",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

    private static bool UsesForwardPath(ScriptableRenderer renderer, bool legacyFallback)
    {
        if (renderer is UniversalRenderer && ActualRenderingModeProperty != null)
            return (RenderingMode)ActualRenderingModeProperty.GetValue(renderer) != RenderingMode.Deferred;
        return legacyFallback;
    }

    public override void Create()
    {
        pass = new SSRPass(settings);
        EnsureMaterial();
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(material);
        material = null;
        base.Dispose(disposing);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!settings.isEnabled)
            return;
        if (renderingData.cameraData.renderType != CameraRenderType.Base)
            return;
        if (renderingData.cameraData.cameraType != CameraType.Game &&
            renderingData.cameraData.cameraType != CameraType.SceneView)
            return;
        if (!EnsureMaterial())
            return;

        bool forwardPath = UsesForwardPath(renderer, settings.useForwardNormals);
        pass.renderPassEvent = settings.renderPassEvent;
        pass.Setup(renderer, material, forwardPath);
        pass.ConfigureInput(forwardPath
            ? ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal
            : ScriptableRenderPassInput.Depth);
        renderer.EnqueuePass(pass);
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

    private sealed class SSRPass : ScriptableRenderPass
    {
        private static readonly int SourceTexId = Shader.PropertyToID("_SSR_SourceTex");
        private static readonly int ReflectionMapId = Shader.PropertyToID("_SSR_ReflectionMap");
        private static readonly int TempSourceId = Shader.PropertyToID("_SSR_TempSource");
        private static readonly int InverseProjectionMatrixId = Shader.PropertyToID("_SSR_InverseProjectionMatrix");
        private static readonly int InverseViewMatrixId = Shader.PropertyToID("_SSR_InverseViewMatrix");
        private static readonly int ProjectionMatrixId = Shader.PropertyToID("_SSR_ProjectionMatrix");
        private static readonly int ViewMatrixId = Shader.PropertyToID("_SSR_ViewMatrix");
        private static readonly int WorldSpaceViewDirId = Shader.PropertyToID("_SSR_WorldSpaceViewDir");
        private static readonly int RenderScaleId = Shader.PropertyToID("_SSR_RenderScale");
        private static readonly int FrameId = Shader.PropertyToID("_SSR_Frame");
        private static readonly int DitherModeId = Shader.PropertyToID("_SSR_DitherMode");
        private static readonly int DebugViewId = Shader.PropertyToID("_SSR_DebugView");
        private static readonly int IntensityId = Shader.PropertyToID("_SSR_Intensity");
        private static readonly int ReflectionBrightnessId = Shader.PropertyToID("_SSR_ReflectionBrightness");
        private static readonly int StrideId = Shader.PropertyToID("_SSR_Stride");
        private static readonly int NumStepsId = Shader.PropertyToID("_SSR_NumSteps");
        private static readonly int MinSmoothnessId = Shader.PropertyToID("_SSR_MinSmoothness");
        private static readonly int ReflectSkyId = Shader.PropertyToID("_SSR_ReflectSky");
        private static readonly int ForwardModeId = Shader.PropertyToID("_SSR_UseForwardNormals");
        private static readonly int ForwardSmoothnessId = Shader.PropertyToID("_SSR_ForwardSmoothness");

        private const int PassTrace = 0;
        private const int PassComposite = 1;

        private readonly SSRSettings settings;
        private readonly ProfilingSampler sampler = new ProfilingSampler("SSR");

        private ScriptableRenderer renderer;
        private Material material;
        private int frame;

        public SSRPass(SSRSettings settings)
        {
            this.settings = settings;
        }

        private bool forwardPath;

        public void Setup(ScriptableRenderer renderer, Material material, bool forwardPath)
        {
            this.forwardPath = forwardPath;
            this.renderer = renderer;
            this.material = material;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (material == null || renderer == null)
                return;

            CameraData cameraData = renderingData.cameraData;
            RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.useMipMap = true;
            desc.autoGenerateMips = true;
            desc.mipCount = 8;

            int downsample = Mathf.Clamp(settings.downsample, 0, 1);
            int scale = downsample + 1;
            int reflectionWidth = Mathf.Max(1, desc.width / scale);
            int reflectionHeight = Mathf.Max(1, desc.height / scale);

            SetMaterialProperties(cameraData, reflectionWidth, reflectionHeight);

            RenderTextureDescriptor reflectionDesc = desc;
            reflectionDesc.width = reflectionWidth;
            reflectionDesc.height = reflectionHeight;
            reflectionDesc.useMipMap = false;
            reflectionDesc.autoGenerateMips = false;
            reflectionDesc.mipCount = 1;
            reflectionDesc.colorFormat = settings.highPrecisionTrace ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGBHalf;

            CommandBuffer cmd = CommandBufferPool.Get("SSR");
            using (new ProfilingScope(cmd, sampler))
            {
                RenderTargetIdentifier source = renderer.cameraColorTargetHandle.nameID;

                cmd.GetTemporaryRT(TempSourceId, desc, FilterMode.Trilinear);
                cmd.GetTemporaryRT(ReflectionMapId, reflectionDesc, FilterMode.Point);

                cmd.Blit(source, TempSourceId);

                cmd.SetGlobalTexture(SourceTexId, TempSourceId);
                cmd.SetRenderTarget(ReflectionMapId);
                cmd.DrawProcedural(Matrix4x4.identity, material, PassTrace, MeshTopology.Triangles, 3, 1);

                cmd.SetGlobalTexture(SourceTexId, TempSourceId);
                cmd.SetGlobalTexture(ReflectionMapId, new RenderTargetIdentifier(ReflectionMapId));
                // 保留 URP 相机深度附件，避免后续透明 Pass 继承仅颜色绑定。
            cmd.SetRenderTarget(source, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
                renderer.cameraDepthTargetHandle.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                cmd.DrawProcedural(Matrix4x4.identity, material, PassComposite, MeshTopology.Triangles, 3, 1);

                cmd.ReleaseTemporaryRT(ReflectionMapId);
                cmd.ReleaseTemporaryRT(TempSourceId);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            frame = (frame + 1) & 1023;
        }

        private void SetMaterialProperties(CameraData cameraData, int width, int height)
        {
            Camera camera = cameraData.camera;
            Matrix4x4 projectionMatrix = cameraData.GetGPUProjectionMatrix();
            Matrix4x4 viewMatrix = cameraData.GetViewMatrix();
            float renderScale = cameraData.isSceneViewCamera ? 1f : cameraData.renderScale;

            material.SetMatrix(InverseProjectionMatrixId, projectionMatrix.inverse);
            material.SetMatrix(ProjectionMatrixId, projectionMatrix);
            material.SetMatrix(InverseViewMatrixId, viewMatrix.inverse);
            material.SetMatrix(ViewMatrixId, viewMatrix);
            material.SetVector(WorldSpaceViewDirId, camera.transform.forward);
            material.SetFloat(RenderScaleId, renderScale);
            material.SetInt(FrameId, frame);
            material.SetVector("_SSR_TraceTexelSize", new Vector4(1f / width, 1f / height, width, height));
            material.SetFloat("_SSR_JitterRadiusPixels", settings.jitterRadiusPixels);
            material.SetFloat("_SSR_RoughnessMipMax", settings.roughnessMipMax);
            material.SetInt(DitherModeId, settings.ditherMode == DitherMode.InterleavedGradient ? 1 : 0);
            material.SetInt(DebugViewId, (int)settings.debugView);
            material.SetFloat(IntensityId, settings.intensity);
            material.SetFloat(ReflectionBrightnessId, Mathf.Clamp(settings.reflectionBrightness, 0f, 4f));
            material.SetFloat(StrideId, Mathf.Max(0.001f, settings.stepStrideLength));
            material.SetFloat(NumStepsId, Mathf.Max(8f, settings.maxSteps));
            material.SetFloat(MinSmoothnessId, Mathf.Clamp01(settings.minSmoothness));
            material.SetInt(ReflectSkyId, settings.reflectSky ? 1 : 0);
            material.SetFloat(ForwardModeId, forwardPath ? 1f : 0f);
            material.SetFloat(ForwardSmoothnessId, Mathf.Clamp01(settings.forwardSmoothness));
        }
    }
}
