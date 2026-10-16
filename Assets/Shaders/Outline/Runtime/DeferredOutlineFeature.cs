using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;

namespace DeferredOutline
{
    public sealed class DeferredOutlineFeature : ScriptableRendererFeature
    {
        [Serializable]
        public sealed class FeatureSettings
        {
            public RenderPassEvent renderPassEvent = (RenderPassEvent)503;
            public bool sceneView = true;
            [Tooltip("默认每个 OutlineTarget 独立颜色、运动和显隐。关闭后使用旧版整层共享路径。无 Target 的对象仅旧路径处理。 ")]
            public bool individualTargets = true;
            [Tooltip("Renderer Rendering Layer 的位掩码，不是 GameObject Layer。默认第8位（128）。")]
            public uint renderingLayerMask = 128;
            [Tooltip("仅处理匹配 Group 的 Target；Group 只共享风格，不共享角色速度。")]
            public OutlineGroup group;
            public OutlinePreset presetOverride;
            [Range(16, 256)] public int maxDistancePixels = 128;
        }

        public FeatureSettings settings = new FeatureSettings();
        [SerializeField] Shader depthToMaskShader;
        [SerializeField] Shader compositeShader;
        [SerializeField, Tooltip("旧版距离场引用，仅保留资产兼容；当前Mask边缘路径不使用。 ")] Shader distanceFieldShader;
        DeferredOutlinePass pass;
        OutlineTarget explicitMotionSource;

        // 旧版整层路径的驱动对象。逐 Target 路径每个角色使用自己的运动输入，不抑制其他角色。
        public void SetMotionSource(OutlineTarget source) => explicitMotionSource = source;

        public override void Create()
        {
            pass?.Dispose();
            if (depthToMaskShader == null) depthToMaskShader = Shader.Find("Hidden/DeferredOutline/DepthToMask");
            if (compositeShader == null) compositeShader = Shader.Find("Hidden/DeferredOutline/Composite");
            if (distanceFieldShader == null) distanceFieldShader = Shader.Find("Hidden/DeferredOutline/DistanceField");
            pass = new DeferredOutlinePass(settings, depthToMaskShader, compositeShader, distanceFieldShader);
        }
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            CameraType type = renderingData.cameraData.cameraType;
            if (type == CameraType.Preview || (!settings.sceneView && type == CameraType.SceneView)) return;
            if (pass == null || !pass.IsReady || settings.renderingLayerMask == 0) return;
            pass.ExplicitMotionSource = explicitMotionSource;
            pass.renderPassEvent = settings.renderPassEvent;
            renderer.EnqueuePass(pass);
        }
        public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
        {
            if (pass == null || !pass.IsReady) return;
            CameraType type = renderingData.cameraData.cameraType;
            if (type == CameraType.Preview || (!settings.sceneView && type == CameraType.SceneView)) return;
            pass.SetCameraTarget(renderer.cameraColorTargetHandle, renderer.cameraDepthTargetHandle);
        }
        protected override void Dispose(bool disposing) => pass?.Dispose();

        sealed class DeferredOutlinePass : ScriptableRenderPass, IDisposable
        {
            readonly FeatureSettings settings;
            readonly List<OutlineTarget> activeTargets = new List<OutlineTarget>();
            readonly Dictionary<(Camera, OutlineTarget), Vector2> smoothedTails = new Dictionary<(Camera, OutlineTarget), Vector2>();
            readonly Dictionary<(Camera, OutlineTarget), double> cameraTimes = new Dictionary<(Camera, OutlineTarget), double>();
            readonly HashSet<int> checkedMaterials = new HashSet<int>();
            readonly ProfilingSampler sampler = new ProfilingSampler("Outline DepthOnly / Mask UV Edge");
            static readonly ShaderTagId DepthOnlyTag = new ShaderTagId("DepthOnly");
            static readonly ShaderTagId LightModeTag = new ShaderTagId("LightMode");
            readonly int selectedDepthId = Shader.PropertyToID("_DO_SelectedDepthRT");
            readonly int currentMaskId = Shader.PropertyToID("_DO_CurrentMaskRT");
            readonly Material maskMaterial, compositeMaterial;
            RTHandle cameraColor, cameraDepth;
            double fallbackCycles, previousEffectTime;
            Vector3 fallbackFlowOffset;
            public OutlineTarget ExplicitMotionSource { get; set; }
            public bool IsReady => maskMaterial != null && compositeMaterial != null;

            public DeferredOutlinePass(FeatureSettings settings, Shader mask, Shader composite, Shader distance)
            {
                this.settings = settings;
                maskMaterial = mask != null ? CoreUtils.CreateEngineMaterial(mask) : null;
                compositeMaterial = composite != null ? CoreUtils.CreateEngineMaterial(composite) : null;
                // Legacy distance shader reference retained for asset compatibility; no distance field is rendered.
                ConfigureInput(ScriptableRenderPassInput.Depth);

            }
            public void SetCameraTarget(RTHandle color, RTHandle depth)
            { cameraColor = color; cameraDepth = depth; }
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            { ConfigureTarget(cameraColor, cameraDepth); ConfigureClear(ClearFlag.None, Color.clear); }

            OutlineTarget FindDriver()
            {
                if (Matches(ExplicitMotionSource)) return ExplicitMotionSource;
                OutlineTarget.GetActive(activeTargets);
                OutlineTarget result = null;
                foreach (OutlineTarget target in activeTargets)
                {
                    if (!Matches(target)) continue;
                    if (result == null || target.GetInstanceID() < result.GetInstanceID()) result = target;
                }
                return result;
            }
            bool Matches(OutlineTarget target)
            {
                if (target == null || !target.isActiveAndEnabled || target.ResolvedPreset == null || target.group != settings.group) return false;
                foreach (Renderer item in target.Renderers)
                    if (item != null && (item.renderingLayerMask & settings.renderingLayerMask) != 0) return true;
                return false;
            }
            bool VisibleTarget(OutlineTarget target, Camera camera)
            {
                #if UNITY_EDITOR
                if (camera.scene.IsValid() && UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(camera.scene)
                    && target.gameObject.scene != camera.scene) return false;
                #endif
                bool eligible = false;
                foreach (Renderer renderer in target.Renderers)
                    if (renderer != null && renderer.enabled && !renderer.forceRenderingOff && renderer.gameObject.activeInHierarchy
                        && (renderer.renderingLayerMask & settings.renderingLayerMask) != 0 && (camera.cullingMask & (1 << renderer.gameObject.layer)) != 0) { eligible = true; break; }
                if (!eligible) return false;
                Bounds bounds = target.GetWorldBounds();
                float depth = Mathf.Max(camera.nearClipPlane, camera.WorldToViewportPoint(bounds.center).z);
                float unitsPerPixel = camera.orthographic ? 2f * camera.orthographicSize / Mathf.Max(1,camera.pixelHeight)
                    : 2f * depth * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f) / Mathf.Max(1,camera.pixelHeight);
                bounds.Expand(2f * Mathf.Clamp(settings.maxDistancePixels,16,256) * unitsPerPixel);
                return GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(camera), bounds);
            }
            void PruneMotionState()
            {
                var stale = new List<(Camera, OutlineTarget)>();
                foreach (var key in cameraTimes.Keys)
                    if (key.Item1 == null || (!ReferenceEquals(key.Item2, null) && (key.Item2 == null || !key.Item2.isActiveAndEnabled || !activeTargets.Contains(key.Item2)))) stale.Add(key);
                foreach (var key in stale) { cameraTimes.Remove(key); smoothedTails.Remove(key); }
            }
            void DrawTarget(CommandBuffer cmd, OutlineTarget target, Camera camera)
            {
                var seen = new HashSet<Renderer>();
                Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
                foreach (Renderer item in target.Renderers)
                {
                    if (item == null || !seen.Add(item) || !item.enabled || item.forceRenderingOff || !item.gameObject.activeInHierarchy
                        || (item.renderingLayerMask & settings.renderingLayerMask) == 0 || (camera.cullingMask & (1 << item.gameObject.layer)) == 0
                        || !GeometryUtility.TestPlanesAABB(planes, item.bounds)) continue;
                    Material[] materials = item.sharedMaterials;
                    int count = item is SkinnedMeshRenderer skin && skin.sharedMesh != null ? skin.sharedMesh.subMeshCount
                        : item.TryGetComponent<MeshFilter>(out var filter) && filter.sharedMesh != null ? filter.sharedMesh.subMeshCount : materials.Length;
                    for (int sub = 0; sub < Mathf.Min(count, materials.Length); sub++)
                    {
                        Material material = materials[sub];
                        if (material == null || material.renderQueue > 2500) continue;
                        int depthPass = -1;
                        for (int index = 0; index < material.passCount; index++)
                            if (material.shader.FindPassTagValue(index, LightModeTag) == DepthOnlyTag) { depthPass = index; break; }
                        if (depthPass >= 0) cmd.DrawRenderer(item, material, sub, depthPass);
                        else if (checkedMaterials.Add(material.GetInstanceID())) Debug.LogWarning("[Outline] 跳过没有 LightMode=DepthOnly 的材质：" + material.name, item);
                    }
                }
            }
            void CheckDepthPasses(OutlineTarget driver)
            {
                if (driver == null) return;
                foreach (Renderer item in driver.Renderers)
                {
                    if (item == null || (item.renderingLayerMask & settings.renderingLayerMask) == 0) continue;
                    foreach (Material material in item.sharedMaterials)
                    {
                        if (material == null || !checkedMaterials.Add(material.GetInstanceID())) continue;
                        bool found = false;
                        for (int i = 0; i < material.passCount; i++)
                            if (material.shader.FindPassTagValue(i, LightModeTag) == DepthOnlyTag) { found = true; break; }
                        if (!found) Debug.LogWarning("[Outline] 材质缺少 LightMode=DepthOnly，将无法进入描边轮廓：" + material.name, item);
                    }
                }
            }
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (!IsReady) return;
                OutlineTarget.GetActive(activeTargets);
                PruneMotionState();
                var drivers = new List<OutlineTarget>();
                if (settings.individualTargets)
                {
                    foreach (OutlineTarget target in activeTargets)
                        if (Matches(target) && VisibleTarget(target, renderingData.cameraData.camera) && target.Visibility > 0.0001f) drivers.Add(target);
                    drivers.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
                }
                else drivers.Add(FindDriver());
                if (drivers.Count == 0) return;
                RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
                desc.msaaSamples = 1; desc.bindMS = false; desc.useMipMap = false; desc.autoGenerateMips = false;
                RenderTextureDescriptor depthDesc = desc;
                depthDesc.graphicsFormat = GraphicsFormat.None;
                depthDesc.depthStencilFormat = GraphicsFormat.D32_SFloat;
                depthDesc.depthBufferBits = 32;
                desc.depthBufferBits = 0;
                desc.colorFormat = RenderTextureFormat.ARGBFloat;
                CommandBuffer cmd = CommandBufferPool.Get("Outline DepthOnly / Mask UV Edge");
                using (new ProfilingScope(cmd, sampler))
                {
                    cmd.GetTemporaryRT(selectedDepthId, depthDesc, FilterMode.Point);
                    cmd.GetTemporaryRT(currentMaskId, desc, FilterMode.Point);
                    foreach (OutlineTarget driver in drivers)
                    {
                    OutlinePreset preset = driver != null ? driver.ResolvedPreset : settings.presetOverride != null ? settings.presetOverride : settings.group != null ? settings.group.preset : null;
                    if (preset == null || (driver != null && driver.Visibility <= 0.0001f)) continue;
                    cmd.SetRenderTarget(selectedDepthId);
                    CoreUtils.ClearRenderTarget(cmd, ClearFlag.Depth, Color.clear);
                    if (settings.individualTargets) DrawTarget(cmd, driver, renderingData.cameraData.camera);
                    else
                    {
                        context.ExecuteCommandBuffer(cmd); cmd.Clear();
                        DrawingSettings drawing = CreateDrawingSettings(DepthOnlyTag, ref renderingData, renderingData.cameraData.defaultOpaqueSortFlags);
                        drawing.perObjectData = PerObjectData.None;
                        FilteringSettings filtering = new FilteringSettings(RenderQueueRange.opaque, -1, settings.renderingLayerMask);
                        context.DrawRenderers(renderingData.cullResults, ref drawing, ref filtering);
                    }
                    ApplySettings(cmd, driver, preset, renderingData.cameraData.camera, desc);
                    cmd.SetGlobalTexture("_DO_SelectedDepth", selectedDepthId);
                    cmd.SetGlobalMatrix("_DO_InverseViewProjection", (renderingData.cameraData.GetGPUProjectionMatrix() * renderingData.cameraData.GetViewMatrix()).inverse);
                    cmd.SetGlobalVector("_DO_MaskTexelSize", new Vector4(1f / desc.width, 1f / desc.height, desc.width, desc.height));
                    cmd.SetRenderTarget(currentMaskId);
                    cmd.DrawProcedural(Matrix4x4.identity, maskMaterial, 0, MeshTopology.Triangles, 3, 1);
                    cmd.SetGlobalTexture("_DO_CurrentTex", currentMaskId);
                    cmd.SetRenderTarget(cameraColor.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                        cameraDepth.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                    cmd.DrawProcedural(Matrix4x4.identity, compositeMaterial, 0, MeshTopology.Triangles, 3, 1);
                    }
                    cmd.SetRenderTarget(cameraColor.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, cameraDepth.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                    cmd.ReleaseTemporaryRT(selectedDepthId);
                    cmd.ReleaseTemporaryRT(currentMaskId);
                }
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
            int ApplySettings(CommandBuffer cmd, OutlineTarget driver, OutlinePreset preset, Camera camera, RenderTextureDescriptor desc)
            {
                float visibility = driver != null ? driver.Visibility : 1f;
                Bounds bounds = driver != null ? driver.GetWorldBounds() : new Bounds(Vector3.zero, Vector3.one);
                cmd.SetGlobalFloat("_DO_Visibility", visibility);
                cmd.SetGlobalFloat("_DO_TransitionMode", (float)preset.transitionMode);
                cmd.SetGlobalVector("_DO_BoundsMinMax", new Vector4(bounds.min.y, bounds.max.y, 0f, 0f));
                cmd.SetGlobalFloat("_DO_NoiseScale", preset.transitionNoiseScale);
                cmd.SetGlobalFloat("_DO_TransitionDistortion", preset.transitionDistortion);
                cmd.SetGlobalFloat("_DO_TransitionSoftness", preset.transitionEdgeSoftness);
                cmd.SetGlobalFloat("_DO_UseSpecialMask", preset.enableSpecialMask && preset.specialMask != null ? 1f : 0f);
                cmd.SetGlobalTexture("_DO_SpecialMask", preset.specialMask != null ? preset.specialMask : Texture2D.whiteTexture);
                cmd.SetGlobalVector("_DO_MaskST", new Vector4(preset.maskTiling.x, preset.maskTiling.y, preset.maskOffset.x, preset.maskOffset.y));
                cmd.SetGlobalFloat("_DO_EqualSpacingPixels", preset.tbnEqualSpacingPixels);
                for (int i = 0; i < 4; i++) cmd.SetGlobalColor("_DO_Color" + i, driver != null ? driver.GetColor(i) : preset.GetColor(i));
                Vector4 widths = driver != null ? driver.GetWidths() : preset.Widths;
                float speedWeight = driver != null ? preset.EvaluateMotionFlame(driver.MotionSpeed) : 0f;
                float flameStrength = driver != null ? driver.GetFlameStrength() : preset.flameIntensity;
                cmd.SetGlobalVector("_DO_Widths", widths);
                cmd.SetGlobalFloat("_DO_LayerCount", preset.colorLayerCount);
                cmd.SetGlobalFloat("_DO_Distortion", driver != null ? driver.GetDistortion() : preset.distortion);
                cmd.SetGlobalFloat("_DO_FlowScale", preset.flowScale);
                cmd.SetGlobalColor("_DO_FlameColor", driver != null ? driver.GetFlameColor() : preset.flameColor);
                cmd.SetGlobalFloat("_DO_FlameIntensity", flameStrength);
                cmd.SetGlobalFloat("_DO_MotionWeight", speedWeight);
                cmd.SetGlobalFloat("_DO_GlowIntensity", preset.glowIntensity);
                cmd.SetGlobalFloat("_DO_GlowRadius", preset.glowRadius);
                double now = Application.isPlaying ? Time.timeAsDouble : Time.realtimeSinceStartupAsDouble;
                float effectDt = previousEffectTime > 0 ? Mathf.Max(0f, (float)(now - previousEffectTime)) : 0f;
                previousEffectTime = now;
                var motionKey = (camera, driver);
                float dt = cameraTimes.TryGetValue(motionKey, out double cameraTime) ? Mathf.Max(0f, (float)(now - cameraTime)) : 0f;
                cameraTimes[motionKey] = now;
                Vector3 flowCenter = driver != null ? driver.FlowReferenceCenter : camera.ViewportToWorldPoint(new Vector3(0.5f, 0.5f, 5f));
                float referenceHeight = driver != null ? driver.FlowReferenceHeight : 2f;
                Vector3 origin = driver != null ? driver.transform.position : flowCenter;
                Vector3 screenNow = camera.WorldToViewportPoint(origin);
                Vector3 screenBefore = camera.WorldToViewportPoint(origin - (driver != null ? driver.MotionVelocity : Vector3.zero) * preset.motionTailSeconds);
                Vector2 desired = screenNow.z > 0f && screenBefore.z > 0f
                    ? new Vector2((screenBefore.x - screenNow.x) * desc.width, (screenBefore.y - screenNow.y) * desc.height) : Vector2.zero;
                desired = Vector2.ClampMagnitude(desired, preset.motionTailMaxPixels);
                if (!smoothedTails.TryGetValue(motionKey, out Vector2 previous)) previous = desired;
                float response = desired.sqrMagnitude > previous.sqrMagnitude ? 0.065f : preset.motionTailReturnSeconds;
                Vector2 tail = Vector2.Lerp(previous, desired, 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, response)));
                smoothedTails[motionKey] = tail;
                cmd.SetGlobalVector("_DO_TailPixels", new Vector4(tail.x, tail.y, 0f, 0f));
                Vector3 upOrigin = camera.WorldToViewportPoint(flowCenter);
                Vector3 flowTop = camera.WorldToViewportPoint(flowCenter + camera.transform.up * referenceHeight);
                float projectedHeight = Mathf.Max(1f / desc.height, Mathf.Abs(flowTop.y - upOrigin.y));
                cmd.SetGlobalVector("_DO_FlowAnchor", new Vector4(upOrigin.x, upOrigin.y, projectedHeight, (float)desc.width / desc.height));
                float distortionMultiplier = Mathf.Abs(driver != null ? driver.GetDistortion() : preset.distortion) / 1.85f;
                float extensionPixels = projectedHeight * desc.height * Mathf.Max(0f, Mathf.Lerp(preset.idleExtensionHeight, preset.movingExtensionHeight, speedWeight))
                    * distortionMultiplier * Mathf.Max(0f, flameStrength);
                cmd.SetGlobalFloat("_DO_ExtensionPixels", extensionPixels);
                fallbackCycles = (fallbackCycles + preset.rhythmFrequencyHz * effectDt) % 25.0;
                fallbackFlowOffset += (Vector3.right * preset.flowSpeed.x + Vector3.up * preset.flowSpeed.y)
                    * (effectDt * 0.15f * referenceHeight / Mathf.Max(0.035f, preset.flowScale * 0.35f));
                Vector3 worldOffset = driver != null ? driver.FlowOffsetWorld : fallbackFlowOffset;
                Vector3 viewOffset = camera.worldToCameraMatrix.MultiplyVector(worldOffset);
                Vector4 clipOffset = camera.projectionMatrix * new Vector4(viewOffset.x, viewOffset.y, viewOffset.z, 0f);
                Vector4 clipCenter = camera.projectionMatrix * camera.worldToCameraMatrix * new Vector4(flowCenter.x, flowCenter.y, flowCenter.z, 1f);
                float projectionW = Mathf.Max(0.001f, clipCenter.w);
                cmd.SetGlobalVector("_DO_FlowOffset", new Vector4(clipOffset.x * projectionW - clipCenter.x * clipOffset.w,
                    clipOffset.y * projectionW - clipCenter.y * clipOffset.w, 0f, 0f) * (0.5f / (projectionW * projectionW)));
                cmd.SetGlobalFloat("_DO_RhythmPhase", driver != null ? driver.RhythmPhase : (float)(fallbackCycles * Math.PI * 2));
                Vector3 flowAxis = driver != null ? driver.FlowDirectionWorld : Vector3.up;
                Vector3 upPoint = camera.WorldToViewportPoint(flowCenter + flowAxis * referenceHeight);
                Vector2 worldUp = upOrigin.z > 0f && upPoint.z > 0f
                    ? new Vector2((upPoint.x - upOrigin.x) * desc.width, (upPoint.y - upOrigin.y) * desc.height) : Vector2.up;
                if (worldUp.sqrMagnitude < 0.001f) worldUp = Vector2.up;
                cmd.SetGlobalVector("_DO_WorldUpPixels", new Vector4(worldUp.x, worldUp.y, 0f, 0f));
                float width = widths.x;
                if (preset.colorLayerCount > 1) width += widths.y;
                if (preset.colorLayerCount > 2) width += widths.z;
                if (preset.colorLayerCount > 3) width += widths.w;
                float tongue = flameStrength > 0.001f ? 1.5f + 1.5f * flameStrength + preset.motionTailMaxPixels * 1.2f : 0f;
                float radius = Mathf.Clamp(width + preset.tbnEqualSpacingPixels + preset.glowRadius + tongue + 4f, 1f, Mathf.Clamp(settings.maxDistancePixels, 16, 256));
                cmd.SetGlobalFloat("_DO_MaxDistancePixels", radius);
                Vector4 screenBounds=new Vector4(0,0,1,1);
                if(settings.individualTargets && driver!=null)
                {
                    Vector3 minimum=bounds.min,maximum=bounds.max;
                    float minX=1,minY=1,maxX=0,maxY=0;bool safe=true;
                    for(int corner=0;corner<8;corner++)
                    {
                        Vector3 projected=camera.WorldToViewportPoint(new Vector3((corner&1)==0?minimum.x:maximum.x,(corner&2)==0?minimum.y:maximum.y,(corner&4)==0?minimum.z:maximum.z));
                        if(projected.z<=camera.nearClipPlane){safe=false;break;}
                        minX=Mathf.Min(minX,projected.x);minY=Mathf.Min(minY,projected.y);maxX=Mathf.Max(maxX,projected.x);maxY=Mathf.Max(maxY,projected.y);
                    }
                    // Conservative for any displacement direction and noise in [0,1], plus AA/jitter margin.
                    float marginX=(extensionPixels+widths.x+3f)/desc.width,marginY=(extensionPixels+widths.x+3f)/desc.height;
                    if(safe)screenBounds=new Vector4(Mathf.Clamp01(minX-marginX),Mathf.Clamp01(minY-marginY),Mathf.Clamp01(maxX+marginX),Mathf.Clamp01(maxY+marginY));
                }
                cmd.SetGlobalVector("_DO_ScreenBounds",screenBounds);
                return Mathf.CeilToInt(radius);
            }
            public void Dispose()
            {
                CoreUtils.Destroy(maskMaterial); CoreUtils.Destroy(compositeMaterial);
                smoothedTails.Clear(); cameraTimes.Clear(); checkedMaterials.Clear();
            }
        }
    }
}