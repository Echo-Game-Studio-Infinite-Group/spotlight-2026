using System.Reflection;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Spotlight.NPR
{
    public sealed class NPRLightingFeature : ScriptableRendererFeature
    {
        [System.Serializable]
        public sealed class Settings
        {
            public bool IsEnabled = true;
            public bool SceneView = true;
            [Range(0, 6)] public float OutlineWidth = 1.5f;
            [Range(1, 6)] public int MaxOutlineWidth = 3;
            public Color OutlineColor = new Color(0.035f, 0.045f, 0.07f, 1f);
        }
        public Settings settings = new Settings();
        [SerializeField] Shader lightingShader;
        Material lightingMaterial;
        ToonPass pass;
        static readonly PropertyInfo ActualMode = typeof(UniversalRenderer).GetProperty("renderingModeActual", BindingFlags.Instance | BindingFlags.NonPublic);
        public override void Create()
        {
            pass?.Dispose();
            CoreUtils.Destroy(lightingMaterial);
            if (lightingShader == null) lightingShader = Shader.Find("Hidden/OurFunction/NPRLighting");
            lightingMaterial = lightingShader != null ? CoreUtils.CreateEngineMaterial(lightingShader) : null;
            pass = new ToonPass(settings, lightingMaterial);
        }
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData data)
        {
            if (!settings.IsEnabled || lightingMaterial == null || data.cameraData.renderType != CameraRenderType.Base) return;
            if (data.cameraData.cameraType != CameraType.Game && data.cameraData.cameraType != CameraType.Preview && data.cameraData.cameraType != CameraType.Reflection && !(settings.SceneView && data.cameraData.cameraType == CameraType.SceneView)) return;
            if (!(renderer is UniversalRenderer) || ActualMode == null || (RenderingMode)ActualMode.GetValue(renderer) != RenderingMode.Deferred) return;
            renderer.EnqueuePass(pass);
        }
        protected override void Dispose(bool disposing) { pass?.Dispose(); CoreUtils.Destroy(lightingMaterial); }
        sealed class ToonPass : ScriptableRenderPass
        {
            readonly Settings settings;
            readonly Material material;
            static readonly ShaderTagId StyleTag = new ShaderTagId("NPRStyleData");
            static readonly int Shadow = Shader.PropertyToID("_NPRShadow");
            static readonly int Lighting = Shader.PropertyToID("_NPRLighting");
            static readonly int Detail = Shader.PropertyToID("_NPRDetail");
            static readonly int OutlineColor = Shader.PropertyToID("_NPRMaterialOutlineColor");
            static readonly int Source = Shader.PropertyToID("_NPRSource");
            readonly RTHandle[] styles = { RTHandles.Alloc(new RenderTargetIdentifier(Shadow)), RTHandles.Alloc(new RenderTargetIdentifier(Lighting)), RTHandles.Alloc(new RenderTargetIdentifier(Detail)), RTHandles.Alloc(new RenderTargetIdentifier(OutlineColor)) };
            RenderTextureDescriptor descriptor;
            public ToonPass(Settings settings, Material material)
            {
                this.settings = settings; this.material = material;
                renderPassEvent = (RenderPassEvent)401;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData data)
            {
                descriptor = data.cameraData.cameraTargetDescriptor;
                descriptor.depthBufferBits = 0; descriptor.depthStencilFormat = GraphicsFormat.None;
                descriptor.msaaSamples = 1; descriptor.bindMS = false;
                descriptor.useMipMap = false; descriptor.autoGenerateMips = false;
                cmd.GetTemporaryRT(Source, descriptor, FilterMode.Point);
                descriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                cmd.GetTemporaryRT(Shadow, descriptor, FilterMode.Point);
                cmd.GetTemporaryRT(Lighting, descriptor, FilterMode.Point);
                cmd.GetTemporaryRT(Detail, descriptor, FilterMode.Point);
                var colorDescriptor = descriptor;
                colorDescriptor.graphicsFormat = SystemInfo.IsFormatSupported(GraphicsFormat.B10G11R11_UFloatPack32, FormatUsage.Render) ? GraphicsFormat.B10G11R11_UFloatPack32 : GraphicsFormat.R16G16B16A16_SFloat;
                cmd.GetTemporaryRT(OutlineColor, colorDescriptor, FilterMode.Point);
                // 只清新辅助颜色附件，保留URP真实深度；Equal限制为当前可见表面。
                ConfigureTarget(styles, data.cameraData.renderer.cameraDepthTargetHandle);
                ConfigureClear(ClearFlag.Color, Color.clear);
            }
            public override void Execute(ScriptableRenderContext context, ref RenderingData data)
            {
                var drawing = CreateDrawingSettings(StyleTag, ref data, data.cameraData.defaultOpaqueSortFlags);
                drawing.perObjectData = PerObjectData.None;
                var filtering = new FilteringSettings(RenderQueueRange.opaque);
                context.DrawRenderers(data.cullResults, ref drawing, ref filtering);
                var renderer = data.cameraData.renderer;
                var cmd = CommandBufferPool.Get("NPR Deferred Toon Lighting");
                try
                {
                    cmd.Blit(renderer.cameraColorTargetHandle.nameID, Source);
                    cmd.SetGlobalTexture(OutlineColor, OutlineColor); cmd.SetGlobalTexture(Shadow, Shadow); cmd.SetGlobalTexture(Lighting, Lighting); cmd.SetGlobalTexture(Detail, Detail); cmd.SetGlobalTexture(Source, Source);
                    cmd.SetGlobalMatrix("_NPRInverseViewProjection", (data.cameraData.GetGPUProjectionMatrix() * data.cameraData.GetViewMatrix()).inverse);
                    cmd.SetGlobalVector("_NPRTexelSize", new Vector4(1f / descriptor.width, 1f / descriptor.height, descriptor.width, descriptor.height));
                    cmd.SetGlobalColor("_NPROutlineColor", settings.OutlineColor);
                    cmd.SetGlobalFloat("_NPROutlineWidth", data.cameraData.cameraType == CameraType.Reflection ? 0 : settings.OutlineWidth);
                    cmd.SetGlobalFloat("_NPRMaxOutlineWidth", data.cameraData.cameraType == CameraType.Reflection ? 0 : settings.MaxOutlineWidth);
                    cmd.SetGlobalFloat("_NPRHasMainLight", data.lightData.mainLightIndex >= 0 ? 1f : 0f);
                    // URP14 ForwardLights packs visible lights in order, excluding mainLightIndex.
                    int visibleAdditional = Mathf.Max(0, data.lightData.visibleLights.Length - (data.lightData.mainLightIndex >= 0 ? 1 : 0));
                    cmd.SetGlobalFloat("_NPRAdditionalLightCount", data.lightData.supportsAdditionalLights ? Mathf.Min(data.lightData.additionalLightsCount, visibleAdditional) : 0);
                    // 镜像 URP14 DeferredLights 的 SSAOOnly 条件，避免无方向光时重复遮蔽。
                    bool hasDirectionalStencilLight = false;
                    if (data.lightData.additionalLightsCount != 0 || data.lightData.mainLightIndex >= 0)
                        foreach (var visibleLight in data.lightData.visibleLights)
                            if (visibleLight.lightType == LightType.Directional) { hasDirectionalStencilLight = true; break; }
                    cmd.SetGlobalFloat("_NPRIndirectAOApplied", hasDirectionalStencilLight ? 0f : 1f);
                    cmd.SetRenderTarget(renderer.cameraColorTargetHandle.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                        renderer.cameraDepthTargetHandle.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                    cmd.SetViewport(new Rect(0, 0, descriptor.width, descriptor.height));
                    cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3);
                    context.ExecuteCommandBuffer(cmd);
                }
                finally { CommandBufferPool.Release(cmd); }
            }
            public void Dispose() { foreach (var handle in styles) handle.Release(); }
            public override void OnCameraCleanup(CommandBuffer cmd)
            {
                cmd.ReleaseTemporaryRT(OutlineColor); cmd.ReleaseTemporaryRT(Shadow); cmd.ReleaseTemporaryRT(Lighting); cmd.ReleaseTemporaryRT(Detail); cmd.ReleaseTemporaryRT(Source);
            }
        }
    }
}