using System.Reflection;
using System.Collections.Generic;
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
            [Tooltip("平滑法线几何描边专用 Rendering Layer；需要描边的 Renderer 在原掩码基础上添加此位。")]
            public uint CharacterOutlineRenderingLayerMask = 1u << 8;
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
            readonly int browStencilClearPass;
            readonly List<Material> browFaceMaterials = new List<Material>();
            readonly List<Material> browHairMaterials = new List<Material>();
            readonly Plane[] browFrustumPlanes = new Plane[6];
            static readonly int FaceSH = Shader.PropertyToID("_NPRFaceSH");
            static readonly string[] FaceSHNames = { "unity_SHAr", "unity_SHAg", "unity_SHAb", "unity_SHBr", "unity_SHBg", "unity_SHBb", "unity_SHC" };
            readonly SphericalHarmonicsL2[] faceProbe = new SphericalHarmonicsL2[1];
            readonly MaterialPropertyBlock faceProbePacking = new MaterialPropertyBlock();
            readonly List<Vector4> faceProbeVector = new List<Vector4>(1);
            readonly Vector4[] faceSHVectors = new Vector4[7];
            static readonly ShaderTagId StyleTag = new ShaderTagId("NPRStyleData");
            static readonly ShaderTagId CharacterOutlineTag = new ShaderTagId("NPRCharacterOutline");
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
                browStencilClearPass = material != null ? material.FindPass("NPRBrowStencilClear") : -1;
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
                // 角色与独立金属材质在模型空间数据仍可用时计算自定义直接光，沿用现有四个 MRT。
                var lightGlobals = CommandBufferPool.Get("NPR Custom Direct Light Inputs");
                int visibleAdditional = Mathf.Max(0, data.lightData.visibleLights.Length - (data.lightData.mainLightIndex >= 0 ? 1 : 0));
                lightGlobals.SetGlobalFloat("_NPRCustomMainLightPresent", data.lightData.mainLightIndex >= 0 ? 1f : 0f);
                lightGlobals.SetGlobalFloat("_NPRCustomAdditionalLightCount", data.lightData.supportsAdditionalLights ? Mathf.Min(data.lightData.additionalLightsCount, visibleAdditional) : 0);
                context.ExecuteCommandBuffer(lightGlobals);
                CommandBufferPool.Release(lightGlobals);
                var drawing = CreateDrawingSettings(StyleTag, ref data, data.cameraData.defaultOpaqueSortFlags);
                drawing.perObjectData = PerObjectData.LightData | PerObjectData.LightIndices;
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
                    var outlineDrawing = CreateDrawingSettings(CharacterOutlineTag, ref data, data.cameraData.defaultOpaqueSortFlags);
                    outlineDrawing.perObjectData = PerObjectData.None;
                    // 基础几何描边只绘制可见对象背面；ZTest 保持墙体遮挡，后续 Bloom/TAA 可正常处理。
                    var characterFiltering = new FilteringSettings(RenderQueueRange.opaque, -1, settings.CharacterOutlineRenderingLayerMask);
                    if (settings.CharacterOutlineRenderingLayerMask != 0)
                        context.DrawRenderers(data.cullResults, ref outlineDrawing, ref characterFiltering);
                    DrawBrowOverlays(context, cmd, ref data);
                }
                finally { CommandBufferPool.Release(cmd); }
            }
            static int MeshSubmeshCount(Renderer renderer)
            {
                if (renderer is SkinnedMeshRenderer skinned)
                    return skinned.sharedMesh != null ? skinned.sharedMesh.subMeshCount : 0;
                if (renderer.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null)
                    return filter.sharedMesh.subMeshCount;
                return 0;
            }
            static bool VisibleTargetRenderer(Renderer renderer, Camera camera)
                => renderer != null && renderer.enabled && !renderer.forceRenderingOff
                    && renderer.gameObject.activeInHierarchy && (camera.cullingMask & (1 << renderer.gameObject.layer)) != 0;

            static bool FaceSurfaceMaterial(Material face)
                => face != null && face.HasProperty("_CharacterModel")
                    && Mathf.Abs(face.GetFloat("_CharacterModel") - 2) < 0.5f
                    && face.FindPass("NPRFaceSurface") >= 0;

            static bool ActiveFaceOverlayMaterial(Material face)
                => FaceSurfaceMaterial(face) && face.HasProperty("_BangsOpacity")
                    && face.GetFloat("_BangsOpacity") < 1 && face.FindPass("NPRBrowThroughHair") >= 0;

            void SetFaceProbe(CommandBuffer cmd, Renderer face)
            {
                faceProbe[0] = RenderSettings.ambientProbe;
                if (face.lightProbeUsage != LightProbeUsage.Off && LightmapSettings.lightProbes != null
                    && LightmapSettings.lightProbes.count > 0)
                {
                    Vector3 position = face.probeAnchor != null ? face.probeAnchor.position : face.bounds.center;
                    LightProbes.GetInterpolatedProbe(position, face, out faceProbe[0]);
                }
                // 原生 API 按 URP SampleSH9 所用的七向量布局打包，不手写归一化/亮度修正。
                faceProbePacking.CopySHCoefficientArraysFrom(faceProbe);
                for (int i = 0; i < faceSHVectors.Length; i++)
                {
                    faceProbePacking.GetVectorArray(FaceSHNames[i], faceProbeVector);
                    faceSHVectors[i] = faceProbeVector[0];
                }
                cmd.SetGlobalVectorArray(FaceSH, faceSHVectors);
            }

            void DrawBrowOverlays(ScriptableRenderContext context, CommandBuffer cmd, ref RenderingData data)
            {
                if (NPRBrowOverlayTarget.ActiveTargets.Count == 0) return;
                Camera camera = data.cameraData.camera;
                GeometryUtility.CalculateFrustumPlanes(camera, browFrustumPlanes);
                bool drewTarget = false, markedHair = false;
                cmd.Clear();
                for (int targetIndex = 0; targetIndex < NPRBrowOverlayTarget.ActiveTargets.Count; targetIndex++)
                {
                    NPRBrowOverlayTarget target = NPRBrowOverlayTarget.ActiveTargets[targetIndex];
                    if (target == null || !target.isActiveAndEnabled || !VisibleTargetRenderer(target.FaceRenderer, camera)) continue;
                    Bounds bounds = target.FaceRenderer.bounds;
                    if (target.HairRenderer != null) bounds.Encapsulate(target.HairRenderer.bounds);
                    if (!GeometryUtility.TestPlanesAABB(browFrustumPlanes, bounds)) continue;
                    target.FaceRenderer.GetSharedMaterials(browFaceMaterials);
                    browHairMaterials.Clear();
                    if (target.HairRenderer != null) target.HairRenderer.GetSharedMaterials(browHairMaterials);
                    int faceCount = Mathf.Min(browFaceMaterials.Count, MeshSubmeshCount(target.FaceRenderer));
                    int hairCount = target.HairRenderer != null ? Mathf.Min(browHairMaterials.Count, MeshSubmeshCount(target.HairRenderer)) : 0;
                    bool hasFacePass = false, hasOverlayPass = false, hasHairPass = false;
                    for (int i = 0; i < faceCount; i++)
                    {
                        hasFacePass |= FaceSurfaceMaterial(browFaceMaterials[i]);
                        hasOverlayPass |= ActiveFaceOverlayMaterial(browFaceMaterials[i]);
                    }
                    if (!hasFacePass) continue;
                    for (int i = 0; i < hairCount; i++)
                        if (browHairMaterials[i] != null && browHairMaterials[i].FindPass("NPRHairBrowStencil") >= 0) { hasHairPass = true; break; }
                    // 同一角色可见脸与透发脸共用探针/辐射函数；不让不透明度端点切换另一套光照。
                    cmd.SetRenderTarget(data.cameraData.renderer.cameraColorTargetHandle.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                        data.cameraData.renderer.cameraDepthTargetHandle.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                    cmd.SetViewport(new Rect(0, 0, descriptor.width, descriptor.height));
                    SetFaceProbe(cmd, target.FaceRenderer);
                    for (int i = 0; i < faceCount; i++)
                    {
                        Material face = browFaceMaterials[i];
                        if (FaceSurfaceMaterial(face)) cmd.DrawRenderer(target.FaceRenderer, face, i, face.FindPass("NPRFaceSurface"));
                    }
                    drewTarget = true;
                    // 刘海完全不透明时仅跳过头发模板/隐藏面绘制，可见脸仍保持同样的光照。
                    if (!hasOverlayPass || !hasHairPass || browStencilClearPass < 0 || !VisibleTargetRenderer(target.HairRenderer, camera)) continue;
                    cmd.DrawProcedural(Matrix4x4.identity, material, browStencilClearPass, MeshTopology.Triangles, 3);
                    for (int i = 0; i < hairCount; i++)
                    {
                        Material hair = browHairMaterials[i];
                        int index = hair != null ? hair.FindPass("NPRHairBrowStencil") : -1;
                        if (index >= 0) cmd.DrawRenderer(target.HairRenderer, hair, i, index);
                    }
                    for (int i = 0; i < faceCount; i++)
                    {
                        Material face = browFaceMaterials[i];
                        if (ActiveFaceOverlayMaterial(face)) cmd.DrawRenderer(target.FaceRenderer, face, i, face.FindPass("NPRBrowThroughHair"));
                    }
                    markedHair = true;
                }
                if (drewTarget)
                {
                    // 目标之间及末尾只清自己的 0x02 位，多角色不能借用彼此的头发模板。
                    if (markedHair) cmd.DrawProcedural(Matrix4x4.identity, material, browStencilClearPass, MeshTopology.Triangles, 3);
                    context.ExecuteCommandBuffer(cmd);
                }
            }
            public void Dispose() { foreach (var handle in styles) handle.Release(); }
            public override void OnCameraCleanup(CommandBuffer cmd)
            {
                cmd.ReleaseTemporaryRT(OutlineColor); cmd.ReleaseTemporaryRT(Shadow); cmd.ReleaseTemporaryRT(Lighting); cmd.ReleaseTemporaryRT(Detail); cmd.ReleaseTemporaryRT(Source);
            }
        }
    }
}