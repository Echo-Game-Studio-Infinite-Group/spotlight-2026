using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// 速度感特效的动态强度参数（URP Volume 通道）：SpeedCameraFeedback 写入，渲染 Pass 每帧读取
// 走 Volume 而非静态单例：多相机各自走自己的 Volume 栈，域重载也不留悬挂引用
public sealed class SpeedFxVolume : UnityEngine.Rendering.VolumeComponent, UnityEngine.Rendering.IPostProcessComponent
{
    [Tooltip("运动模糊强度，1 = 物理正确长度，越大越夸张")]
    public ClampedFloatParameter motionBlur = new ClampedFloatParameter(0f, 0f, 4f);
    [Tooltip("径向拖影强度")]
    public ClampedFloatParameter radial = new ClampedFloatParameter(0f, 0f, 0.1f);
    [Tooltip("色差分离量（UV 空间）")]
    public ClampedFloatParameter chromatic = new ClampedFloatParameter(0f, 0f, 0.05f);
    public bool IsActive() => motionBlur.value > 0f || radial.value > 0f || chromatic.value > 0f;
    public bool IsTileCompatible() => false;
}

/// <summary>
/// 速度感后处理（在后处理之后注入一次全屏 Blit）：
///   • 运动模糊 —— 直接采样 URP 的运动向量贴图，强度不经过 URP 的 clamp(0.2) 限制
///   • 径向拖影 —— 可选的速度线
///   • 色差     —— 可选的 RGB 分离
///
/// 静态配置（材质、采样数、注入时机）在 Renderer Feature 资产上；
/// 动态强度走 SpeedFxVolume（Volume 通道）：SpeedCameraFeedback 每帧写入，渲染 Pass 每帧读取。
/// </summary>
public class RadialRedshiftFeature : ScriptableRendererFeature
{
    internal static readonly int MotionBlurStrengthId = Shader.PropertyToID("_MotionBlurStrength");
    internal static readonly int StrengthId = Shader.PropertyToID("_Strength");
    internal static readonly int ChromaticSpreadId = Shader.PropertyToID("_ChromaticSpread");
    internal static readonly int FalloffPowerId = Shader.PropertyToID("_FalloffPower");
    internal static readonly int SamplesId = Shader.PropertyToID("_Samples");
    internal static readonly int CenterId = Shader.PropertyToID("_Center");

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
        // URP 在渲染器数据重建 / Inspector 改动时会重复调 Create：
        // 必须先释放旧 Pass，否则其 runtimeMaterial（new 出来的 Native 对象）与 RTHandle 泄漏
        pass?.Dispose();
        pass = new RadialRedshiftPass(settings);
        pass.renderPassEvent = settings.renderPassEvent;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // 仅对 Game 相机生效：编辑器 SceneView 相机不做速度感预览（有意取舍），
        // 也不为它请求运动向量贴图，省掉编辑器相机渲染运动向量的开销
        if (renderingData.cameraData.cameraType != CameraType.Game)
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

        pass.renderPassEvent = settings.renderPassEvent;
        renderer.EnqueuePass(pass);
    }

    protected override void Dispose(bool disposing)
    {
        pass?.Dispose();
        pass = null;
    }

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

            // 动态强度从 Volume 栈读取：效果关闭时直接跳过整个 Pass，省掉两次全屏 Blit
            var speedFx = UnityEngine.Rendering.VolumeManager.instance.stack.GetComponent<SpeedFxVolume>();
            if (speedFx == null || !speedFx.IsActive())
            {
                return;
            }

            material.SetFloat(FalloffPowerId, settings.falloffPower);
            material.SetFloat(SamplesId, settings.samples);
            material.SetVector(CenterId, new Vector4(0.5f, 0.5f, 0f, 0f));
            material.SetFloat(MotionBlurStrengthId, speedFx.motionBlur.value);
            material.SetFloat(StrengthId, speedFx.radial.value);
            material.SetFloat(ChromaticSpreadId, speedFx.chromatic.value);

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
