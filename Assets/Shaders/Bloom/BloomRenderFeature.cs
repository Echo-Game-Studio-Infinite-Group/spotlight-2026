using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class BloomRenderFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class BloomSettings
    {
        public bool isEnabled = true;
        // 排在描边 548 之后、URP 内置后处理 550 之前，使描边高亮参与 HDR 泛光。
        public RenderPassEvent renderPassEvent = (RenderPassEvent)549;

        [Header("Bloom")]
        [Min(0f)] public float threshold = 1.25f;
        [Range(0f, 1f)] public float softKnee = 0.5f;
        [Range(0f, 3f)] public float intensity = 0.25f;
        [Range(0f, 1f)] public float scatter = 0.65f;
        [ColorUsage(false, true)] public Color tint = Color.white;

        [Header("Quality")]
        [Range(1, 6)] public int iterations = 4;
        [Range(1, 4)] public int downsample = 2;
        [Min(16)] public int minSize = 32;
    }

    public BloomSettings settings = new BloomSettings();

    [SerializeField, HideInInspector] private Shader shader;

    private Material material;
    private BloomPass pass;

    private const string ShaderName = "Hidden/OurFunction/Bloom";

    public override void Create()
    {
        pass = new BloomPass(settings);
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

        if (renderingData.cameraData.cameraType != CameraType.Game)
            return;
        if (renderingData.cameraData.renderType != CameraRenderType.Base)
            return;
        if (!EnsureMaterial())
            return;

        pass.renderPassEvent = settings.renderPassEvent;
        pass.Setup(renderer, material);
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

    private sealed class BloomPass : ScriptableRenderPass
    {
        private static readonly int SourceTexID = Shader.PropertyToID("_Bloom_SourceTex");
        private static readonly int SourceTexelSizeID = Shader.PropertyToID("_Bloom_SourceTex_TexelSize");
        private static readonly int BloomTexID = Shader.PropertyToID("_BloomTex");
        private static readonly int BloomParamsID = Shader.PropertyToID("_BloomParams");
        private static readonly int BloomTintID = Shader.PropertyToID("_BloomTint");
        private static readonly int TempSourceID = Shader.PropertyToID("_Bloom_Source");
        private static readonly int TempOutputID = Shader.PropertyToID("_Bloom_Output");

        private const int PassPrefilter = 0;
        private const int PassDownsample = 1;
        private const int PassUpsample = 2;
        private const int PassComposite = 3;
        private const int MaxPyramidSize = 6;

        private readonly BloomSettings settings;
        private readonly ProfilingSampler sampler = new ProfilingSampler("Bloom");
        private readonly int[] pyramidDown = new int[MaxPyramidSize];
        private readonly int[] pyramidUp = new int[MaxPyramidSize];

        private ScriptableRenderer renderer;
        private Material material;

        public BloomPass(BloomSettings settings)
        {
            this.settings = settings;
            ConfigureInput(ScriptableRenderPassInput.Color);
            for (int i = 0; i < MaxPyramidSize; ++i)
            {
                pyramidDown[i] = Shader.PropertyToID("_Bloom_Down" + i);
                pyramidUp[i] = Shader.PropertyToID("_Bloom_Up" + i);
            }
        }

        public void Setup(ScriptableRenderer renderer, Material material)
        {
            this.renderer = renderer;
            this.material = material;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (material == null)
                return;

            RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.useMipMap = false;
            desc.autoGenerateMips = false;

            int width = Mathf.Max(1, desc.width / Mathf.Max(1, settings.downsample));
            int height = Mathf.Max(1, desc.height / Mathf.Max(1, settings.downsample));
            int iterations = Mathf.Clamp(settings.iterations, 1, MaxPyramidSize);

            while (iterations > 1 && (width >> (iterations - 1)) < settings.minSize)
                iterations--;
            while (iterations > 1 && (height >> (iterations - 1)) < settings.minSize)
                iterations--;

            CommandBuffer cmd = CommandBufferPool.Get("Bloom");
            using (new ProfilingScope(cmd, sampler))
            {
                RenderTargetIdentifier source = renderer.cameraColorTargetHandle.nameID;
                cmd.GetTemporaryRT(TempSourceID, desc, FilterMode.Bilinear);
                cmd.GetTemporaryRT(TempOutputID, desc, FilterMode.Bilinear);
                cmd.Blit(source, TempSourceID);

                float knee = settings.threshold * settings.softKnee;
                material.SetVector(BloomParamsID, new Vector4(settings.threshold, knee, settings.intensity, settings.scatter));
                material.SetColor(BloomTintID, settings.tint);

                RenderTextureDescriptor pyramidDesc = desc;
                pyramidDesc.width = width;
                pyramidDesc.height = height;
                cmd.GetTemporaryRT(pyramidDown[0], pyramidDesc, FilterMode.Bilinear);

                DrawFullscreen(cmd, TempSourceID, pyramidDown[0], PassPrefilter, desc.width, desc.height);

                int lastDown = pyramidDown[0];
                for (int i = 1; i < iterations; ++i)
                {
                    pyramidDesc.width = Mathf.Max(1, pyramidDesc.width >> 1);
                    pyramidDesc.height = Mathf.Max(1, pyramidDesc.height >> 1);
                    cmd.GetTemporaryRT(pyramidDown[i], pyramidDesc, FilterMode.Bilinear);
                    DrawFullscreen(cmd, lastDown, pyramidDown[i], PassDownsample,
                        Mathf.Max(1, width >> (i - 1)), Mathf.Max(1, height >> (i - 1)));
                    lastDown = pyramidDown[i];
                }

                int lastUp = lastDown;
                for (int i = iterations - 2; i >= 0; --i)
                {
                    int target = pyramidUp[i];
                    RenderTextureDescriptor upDesc = desc;
                    upDesc.width = Mathf.Max(1, width >> i);
                    upDesc.height = Mathf.Max(1, height >> i);
                    cmd.GetTemporaryRT(target, upDesc, FilterMode.Bilinear);
                    cmd.SetGlobalTexture(BloomTexID, pyramidDown[i]);
                    DrawFullscreen(cmd, lastUp, target, PassUpsample,
                        Mathf.Max(1, width >> (i + 1)), Mathf.Max(1, height >> (i + 1)));
                    lastUp = target;
                }

                cmd.SetGlobalTexture(BloomTexID, lastUp);
                DrawFullscreen(cmd, TempSourceID, TempOutputID, PassComposite, desc.width, desc.height);
                cmd.Blit(TempOutputID, source);
                // 私有颜色目标结束后恢复两个附件，保持后续 Pass 与 URP 跟踪的目标一致。
                CoreUtils.SetRenderTarget(cmd, renderer.cameraColorTargetHandle,
                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                    renderer.cameraDepthTargetHandle, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                    ClearFlag.None, Color.clear);

                for (int i = 0; i < iterations; ++i)
                    cmd.ReleaseTemporaryRT(pyramidDown[i]);
                for (int i = 0; i < iterations - 1; ++i)
                    cmd.ReleaseTemporaryRT(pyramidUp[i]);

                cmd.ReleaseTemporaryRT(TempSourceID);
                cmd.ReleaseTemporaryRT(TempOutputID);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private void DrawFullscreen(CommandBuffer cmd, RenderTargetIdentifier source,
            RenderTargetIdentifier destination, int passIndex, int width, int height)
        {
            cmd.SetGlobalTexture(SourceTexID, source);
            cmd.SetGlobalVector(SourceTexelSizeID, new Vector4(1f / width, 1f / height, width, height));
            cmd.SetRenderTarget(destination, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            cmd.DrawProcedural(Matrix4x4.identity, material, passIndex, MeshTopology.Triangles, 3, 1);
        }
    }
}
