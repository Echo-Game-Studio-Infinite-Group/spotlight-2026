using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 速度感后处理（在后处理之后注入一次全屏 Blit）：
///   • 运动模糊 —— 直接采样 URP 的运动向量贴图，强度不经过 URP 的 clamp(0.2) 限制
///   • 径向拖影 —— 可选的速度线
///   • 色差     —— 可选的 RGB 分离
///
/// 静态配置（材质、采样数、注入时机）在 Renderer Feature 资产上；
/// 动态强度由 SpeedCameraFeedback 每帧通过静态方法写入。
/// </summary>
public class RadialRedshiftFeature : ScriptableRendererFeature
{
    internal static readonly int MotionBlurStrengthId = Shader.PropertyToID("_MotionBlurStrength");
    internal static readonly int StrengthId = Shader.PropertyToID("_Strength");
    internal static readonly int ChromaticSpreadId = Shader.PropertyToID("_ChromaticSpread");
    internal static readonly int FalloffPowerId = Shader.PropertyToID("_FalloffPower");
    internal static readonly int SamplesId = Shader.PropertyToID("_Samples");
    internal static readonly int CenterId = Shader.PropertyToID("_Center");

    private static RadialRedshiftFeature activeInstance;

    [Serializable]
    public class Settings
    {
        [Tooltip("承载 RadialRedshift.shader 的材质")]
        public Material material;

        [Tooltip("径向衰减指数：越小中心越清晰、边缘拉伸越强")]
        [Range(0.5f, 4f)]
        public float falloffPower = 1.5f;

        [Tooltip("采样步数：越大越平滑，开销越高")]
        [Range(2, 24)]
        public int samples = 10;

        [Tooltip("注入时机，默认在后处理之后")]
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    public Settings settings = new Settings();

    private RadialRedshiftPass pass;

    public override void Create()
    {
        pass = new RadialRedshiftPass(settings);
        pass.renderPassEvent = settings.renderPassEvent;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // 仅对 Game / Scene 相机生效，避免 UI 相机等重复叠加
        var cameraType = renderingData.cameraData.cameraType;
        if (cameraType != CameraType.Game && cameraType != CameraType.SceneView)
        {
            return;
        }

        if (settings.material == null || pass == null)
        {
            return;
        }

        // 请求运动向量贴图。必须在 renderer.Setup 之前设置，
        // 而 AddRenderPasses 恰好先于 Setup 执行（见 UniversalRenderPipeline.RenderSingleCamera），
        // 所以在这里调用是有效的 —— URP 会因此渲染出 _MotionVectorTexture。
        pass.ConfigureInput(ScriptableRenderPassInput.Motion);

        activeInstance = this;
        pass.renderPassEvent = settings.renderPassEvent;
        renderer.EnqueuePass(pass);
    }

    protected override void Dispose(bool disposing)
    {
        if (activeInstance == this)
        {
            activeInstance = null;
        }

        pass?.Dispose();
        pass = null;
    }

    internal Material RuntimeMaterial => pass != null ? pass.RuntimeMaterial : null;

    /// <summary>
    /// 由 SpeedCameraFeedback 每帧调用，写入当前速度对应的各项强度。
    /// </summary>
    /// <param name="motionBlur">运动模糊强度，1 = 物理正确长度，越大越夸张（无上限）</param>
    /// <param name="radial">径向拖影强度，对应 _Strength</param>
    /// <param name="chromatic">色差分离量（UV 空间），0.005 左右已很明显</param>
    public static void SetSpeed(float motionBlur, float radial, float chromatic)
    {
        var material = activeInstance?.RuntimeMaterial;
        if (material == null)
        {
            return;
        }

        material.SetFloat(MotionBlurStrengthId, Mathf.Max(0f, motionBlur));
        material.SetFloat(StrengthId, Mathf.Clamp(radial, 0f, 0.1f));
        material.SetFloat(ChromaticSpreadId, Mathf.Clamp(chromatic, 0f, 0.05f));
    }

    /// <summary>诊断用：暴露运行时材质，便于检查参数是否真的写入。</summary>
    public static Material DebugMaterial => activeInstance?.RuntimeMaterial;

    private class RadialRedshiftPass : ScriptableRenderPass
    {
        private readonly Settings settings;

        private RTHandle source;
        private RTHandle temp;

        // 运行时材质副本：避免把参数写进共享的 .mat 资产
        private Material runtimeMaterial;

        internal Material RuntimeMaterial
        {
            get
            {
                if (runtimeMaterial == null && settings.material != null)
                {
                    runtimeMaterial = new Material(settings.material)
                    {
                        name = settings.material.name + " (Runtime)"
                    };
                }

                return runtimeMaterial;
            }
        }

        public RadialRedshiftPass(Settings settings)
        {
            this.settings = settings;
            profilingSampler = new ProfilingSampler("Speed FX");
        }

        public void Dispose()
        {
            if (temp != null)
            {
                temp.Release();
                temp = null;
            }

            CoreUtils.Destroy(runtimeMaterial);
            runtimeMaterial = null;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            source = renderingData.cameraData.renderer.cameraColorTargetHandle;

            // 用一张独立的临时 RT 承接效果结果，彻底避免原地 Blit 的读写冲突
            var descriptor = renderingData.cameraData.cameraTargetDescriptor;
            descriptor.depthBufferBits = 0;

            RenderingUtils.ReAllocateIfNeeded(
                ref temp,
                descriptor,
                FilterMode.Bilinear,
                TextureWrapMode.Clamp,
                name: "_SpeedFxTemp");

            ConfigureTarget(source);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var material = RuntimeMaterial;
            if (material == null || source == null || temp == null)
            {
                return;
            }

            material.SetFloat(FalloffPowerId, settings.falloffPower);
            material.SetFloat(SamplesId, settings.samples);
            material.SetVector(CenterId, new Vector4(0.5f, 0.5f, 0f, 0f));

            CommandBuffer cmd = CommandBufferPool.Get();

            using (new ProfilingScope(cmd, profilingSampler))
            {
                // 两步 Blit：
                //   1) source -> temp  应用速度效果
                //   2) temp -> source  写回相机颜色目标
                Blitter.BlitCameraTexture(cmd, source, temp, material, 0);
                Blitter.BlitCameraTexture(cmd, temp, source);
            }

            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            source = null;
        }
    }
}
