// AgX 后处理 RendererFeature —— 把 HST/AgX 插在 URP 后处理链之后、最终 blit 之前。
// 为什么需要它: URP 2022.3 只自带 Neutral / ACES, 而 Blender 侧参考图是 AgX + Medium High Contrast。
// 曲线形状差异会导致"暗处对得上、亮处一律偏高"(实测走廊 02 机位差 +0.25~0.32), 单一曝光值无法兼顾。
// 用法: 由 HST.SceneBuilder.EnableAgX() 程序化挂到 URP-Renderer 上; 同时把 Volume 的 Tonemapping 设为 None
//       (否则 AgX 会被再 tonemap 一次)。顺序: Bloom → ColorGrading(曝光/对比) → AgX → sRGB 编码。
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HST
{
    public class HstAgXFeature : ScriptableRendererFeature
    {
        [System.Serializable]
        public class Settings
        {
            public Shader shader;
            [Tooltip("1D LUT: x∈[0,1] ↔ log2(线性) ∈ [-14,+6], 像素=显示值。见 scripts/scene/make_agx_lut.py")]
            public Texture lut;
        }
        public Settings settings = new Settings();
        AgXPass m_Pass;

        public override void Create()
        {
            if (settings.shader == null) settings.shader = Shader.Find("HST/AgX");
            m_Pass = new AgXPass(settings.shader)
            { renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData data)
        {
            if (settings.shader == null || m_Pass == null) return;
            // 只给真正开后期的主相机加(反射探针/预览相机不加, 否则环境反射会被二次 tonemap)
            if (data.cameraData.cameraType != CameraType.Game && data.cameraData.cameraType != CameraType.SceneView) return;
            if (!data.cameraData.postProcessEnabled) return;
            m_Pass.SetLut(settings.lut);
            renderer.EnqueuePass(m_Pass);
        }

        class AgXPass : ScriptableRenderPass
        {
            Material m_Mat;
            RTHandle m_Tmp;
            Texture m_Lut;
            static readonly int s_BlitTex = Shader.PropertyToID("_BlitTexture");

            public void SetLut(Texture t) { m_Lut = t; }

            public AgXPass(Shader shader)
            {
                if (shader != null) m_Mat = CoreUtils.CreateEngineMaterial(shader);
                profilingSampler = new ProfilingSampler("HST AgX");
            }

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData data)
            {
                var desc = data.cameraData.cameraTargetDescriptor;
                desc.depthBufferBits = 0;
                desc.msaaSamples = 1;
                RenderingUtils.ReAllocateIfNeeded(ref m_Tmp, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_HST_AgXTmp");
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData data)
            {
                if (m_Mat == null) return;
                if (m_Lut != null && m_Mat.GetTexture("_AgXLut") != m_Lut) m_Mat.SetTexture("_AgXLut", m_Lut);
                var cmd = CommandBufferPool.Get("HST AgX");
                using (new ProfilingScope(cmd, profilingSampler))
                {
                    var src = data.cameraData.renderer.cameraColorTargetHandle;
                    if (src == null || m_Tmp == null) { context.ExecuteCommandBuffer(cmd); cmd.Clear(); CommandBufferPool.Release(cmd); return; }
                    Blitter.BlitCameraTexture(cmd, src, m_Tmp, m_Mat, 0);
                    Blitter.BlitCameraTexture(cmd, m_Tmp, src);
                }
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();
                CommandBufferPool.Release(cmd);
            }

            // URP 14 的 ScriptableRenderPass 没有可覆写的 Dispose(bool), 清理由 feature 的 Dispose 调
            public void Cleanup()
            {
                m_Tmp?.Release();
                m_Tmp = null;
                CoreUtils.Destroy(m_Mat);
                m_Mat = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && m_Pass != null) m_Pass.Cleanup();
            m_Pass = null;
            base.Dispose(disposing);
        }
    }
}
