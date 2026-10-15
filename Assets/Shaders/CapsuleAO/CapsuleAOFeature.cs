using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Spotlight.NPR;

namespace Spotlight.Rendering
{
    public sealed class CapsuleAOFeature : ScriptableRendererFeature
    {
        [System.Serializable]
        public sealed class Settings
        {
            public bool IsEnabled = true;
            [Range(0, .5f)] public float Strength = .25f;
            [Range(.05f, 1)] public float Distance = .6f;
        }
        public Settings settings = new Settings();
        // 依赖当帧 NPR 对象标识，不能在关闭 NPR 后误读上一个相机的全局纹理。
        public NPRLightingFeature nprFeature;
        [SerializeField, HideInInspector] Shader shader;
        Material material;
        CapsulePass pass;
        static readonly PropertyInfo ActualMode = typeof(UniversalRenderer).GetProperty("renderingModeActual", BindingFlags.Instance | BindingFlags.NonPublic);
        public override void Create() { pass?.Dispose(); pass = new CapsulePass(settings); EnsureMaterial(); }
        bool EnsureMaterial()
        {
            if (material) return true;
            if (!shader) shader = Shader.Find("Hidden/OurFunction/CapsuleAO");
            if (!shader) return false;
            material = CoreUtils.CreateEngineMaterial(shader);
            return material;
        }
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData data)
        {
            if (!settings.IsEnabled || settings.Strength <= 0 || RuntimeCapsuleOccluder.Active.Count == 0) return;
            if (!nprFeature || !nprFeature.isActive || !nprFeature.settings.IsEnabled) return;
            if (data.cameraData.renderType != CameraRenderType.Base || data.cameraData.cameraType != CameraType.Game) return;
            if (!(renderer is UniversalRenderer) || ActualMode == null || (RenderingMode)ActualMode.GetValue(renderer) != RenderingMode.Deferred) return;
            if (!EnsureMaterial()) return;
            pass.Setup(renderer, material);
            renderer.EnqueuePass(pass);
        }
        protected override void Dispose(bool disposing) { pass?.Dispose(); CoreUtils.Destroy(material); }
        sealed class CapsulePass : ScriptableRenderPass
        {
            readonly Settings settings;
            readonly Vector4[] starts = new Vector4[32], ends = new Vector4[32];
            readonly ProfilingSampler sampler = new ProfilingSampler("Character Capsule AO");
            RTHandle source;
            ScriptableRenderer renderer;
            Material material;
            public CapsulePass(Settings settings)
            {
                this.settings = settings;
                renderPassEvent = (RenderPassEvent)402;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }
            public void Setup(ScriptableRenderer renderer, Material material) { this.renderer = renderer; this.material = material; }
            public override void Execute(ScriptableRenderContext context, ref RenderingData data)
            {
                int count = 0;
                foreach (RuntimeCapsuleOccluder actor in RuntimeCapsuleOccluder.Active)
                    if (actor && actor.isActiveAndEnabled) count = actor.Append(starts, ends, count);
                if (count == 0) return;
                var descriptor = data.cameraData.cameraTargetDescriptor;
                descriptor.depthBufferBits = 0; descriptor.msaaSamples = 1;
                RenderingUtils.ReAllocateIfNeeded(ref source, descriptor, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_CapsuleAOSource");
                var cmd = CommandBufferPool.Get("Character Capsule AO");
                using (new ProfilingScope(cmd, sampler))
                {
                    material.SetVectorArray("_CapsuleAOStart", starts);
                    material.SetVectorArray("_CapsuleAOEnd", ends);
                    material.SetInt("_CapsuleAOCount", count);
                    material.SetVector("_CapsuleAOSettings", new Vector4(settings.Strength, settings.Distance, 0, 0));
                    material.SetMatrix("_CapsuleAOInverseVP", (data.cameraData.GetGPUProjectionMatrix() * data.cameraData.GetViewMatrix()).inverse);
                    Blitter.BlitCameraTexture(cmd, renderer.cameraColorTargetHandle, source);
                    Blitter.BlitCameraTexture(cmd, source, renderer.cameraColorTargetHandle, material, 0);
                    // 全屏 Blit 不绑定深度，后续透明物体仍需要原相机深度进行正确遮挡。
                    cmd.SetRenderTarget(renderer.cameraColorTargetHandle.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                        renderer.cameraDepthTargetHandle.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                }
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
            public void Dispose() { source?.Release(); }
        }
    }
}
