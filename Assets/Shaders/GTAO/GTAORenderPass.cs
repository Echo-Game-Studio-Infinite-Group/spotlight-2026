using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;

/// <summary>
/// GTAO Render Pass - 执行完整的GTAO计算流程, 并接入URP全局AO链路
///
/// 渲染流程 (3阶段, 4次Compute Dispatch):
///   阶段1 GTAOMain        : _CameraDepthTexture → _GTAOBuffer(半分辨率, R=AO/GB=法线/A=深度)
///   阶段2 Blur(H/V)       : 29tap双边模糊 + 双线性上采样 → _VerticalBlurBuffer(全分辨率)
///   阶段3 VisualizeMain   : 输出可见度AO → _VisualizeBuffer
///
/// 集成: 把 _VisualizeBuffer 设为全局 _ScreenSpaceOcclusionTexture, 开启
///       _SCREEN_SPACE_OCCLUSION 关键字, URP前向Lit在不透明Pass自动消费压暗环境光。
///       因此本Pass必须在不透明Pass之前执行。
/// </summary>
public class GTAORenderPass : ScriptableRenderPass
{
    private const string profilerTag = "Ground Truth Ambient Occlusion";
    private const string gtaoKernelName = "GTAOMain";
    private const string blurHorizontalKernelName = "BlurHorizontalMain";
    private const string blurVerticalKernelName = "BlurVerticalMain";
    private const string temporalKernelName = "TemporalFilterMain";
    private const string visualizeKernelName = "VisualizeMain";

    private new ProfilingSampler profilingSampler;
    private ProfilingSampler gtaoSampler = new ProfilingSampler("GTAO Pass");
    private ProfilingSampler blurSampler = new ProfilingSampler("Blur Pass");
    private ProfilingSampler temporalSampler = new ProfilingSampler("Temporal Pass");
    private ProfilingSampler visualizeSampler = new ProfilingSampler("Visualize Pass");

    private static readonly string gtaoTextureName = "_GTAOBuffer";
    private static readonly int gtaoTextureID = Shader.PropertyToID(gtaoTextureName);

    private static readonly string horizontalBlurTextureName = "_HorizontalBlurBuffer";
    private static readonly int horizontalBlurTextureID = Shader.PropertyToID(horizontalBlurTextureName);

    private static readonly string verticalBlurTextureName = "_VerticalBlurBuffer";
    private static readonly int verticalBlurTextureID = Shader.PropertyToID(verticalBlurTextureName);

    private static readonly string visualizeTextureName = "_VisualizeBuffer";
    private static readonly int visualizeTextureID = Shader.PropertyToID(visualizeTextureName);

    private GroundTruthAmbientOcclusion groundTruthAmbientOcclusion;
    private ComputeShader gtaoComputeShader;
    private GTAORendererFeature.GTAOSettings settings;

    private static readonly int cameraDepthTextureID = Shader.PropertyToID("_CameraDepthTexture");

    private int downsamplingFactor;
    private Vector2Int fullRes;
    private Vector2Int downsampleRes;
    private int frameIndex;
    private const int debugLogFrameInterval = 120;
    private bool warnedMissingDepth;
    // 项目锁定 URP14：该 RTHandle 是 GBufferCopyDepthPass 的当前相机目标。
    private static readonly System.Reflection.FieldInfo CameraDepthCopyField =
        typeof(UniversalRenderer).GetField("m_DepthTexture",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    // Temporal 历史 RT (ping-pong, 持久跨帧)
    private RenderTexture[] historyRT = new RenderTexture[2];
    private bool historyValid;
    private int historyCameraId = -1;

    static readonly int _GTAOFrameIndexID = Shader.PropertyToID("_FrameIndex");
    static readonly int _GTAODownsamplingFactorID = Shader.PropertyToID("_DownsamplingFactor");
    static readonly int _GTAOIntensityID = Shader.PropertyToID("_Intensity");
    static readonly int _GTAOSampleRadiusID = Shader.PropertyToID("_SampleRadius");
    static readonly int _GTAODistributionPowerID = Shader.PropertyToID("_DistributionPower");
    static readonly int _GTAOFalloffRangeID = Shader.PropertyToID("_FalloffRange");
    static readonly int _GTAOHorizonBiasID = Shader.PropertyToID("_HorizonBias");
    static readonly int _GTAOTemporalBlendID = Shader.PropertyToID("_TemporalBlend");
    static readonly int _GTAOVarianceClampScaleID = Shader.PropertyToID("_VarianceClampScale");
    static readonly int _GTAOTemporalEnabledID = Shader.PropertyToID("_TemporalEnabled");
    static readonly int _GTAOHistoryTextureID = Shader.PropertyToID("_HistoryTexture");

    static readonly int _GTAOTextureSizeID = Shader.PropertyToID("_TextureSize");
    static readonly int _GTAODepthTextureID = Shader.PropertyToID("_DepthTexture");
    static readonly int _GTAOTextureID = Shader.PropertyToID("_GTAOTexture");
    static readonly int _GTAORWTextureID = Shader.PropertyToID("_RW_GTAOTexture");
    static readonly int _GTAORWBlurTextureID = Shader.PropertyToID("_RW_BlurTexture");
    static readonly int _GTAORWVisualizeTextureID = Shader.PropertyToID("_RW_VisualizeTexture");
    static readonly int _GTAODebugRawAOTextureID = Shader.PropertyToID("_GTAO_DebugRawAOTexture");
    static readonly int _GTAODebugBlurAOTextureID = Shader.PropertyToID("_GTAO_DebugBlurAOTexture");
    static readonly int _GTAOCompositeAOTextureID = Shader.PropertyToID("_GTAO_AOTexture");

    // URP 全局 AO 链路
    private const string k_ScreenSpaceOcclusionKeyword = "_SCREEN_SPACE_OCCLUSION";
    static readonly int _SSAOTextureID = Shader.PropertyToID("_ScreenSpaceOcclusionTexture");
    static readonly int _AmbientOcclusionParamID = Shader.PropertyToID("_AmbientOcclusionParam");

    public GTAORenderPass(GTAORendererFeature.GTAOSettings settings)
    {
        this.settings = settings;
        profilingSampler = new ProfilingSampler(profilerTag);
        renderPassEvent = settings.renderPassEvent;
        gtaoComputeShader = settings.gtaoComputeShader;
        frameIndex = 0;
    }

    public void Setup(GroundTruthAmbientOcclusion groundTruthAmbientOcclusion, ScriptableRenderer renderer)
    {
        this.groundTruthAmbientOcclusion = groundTruthAmbientOcclusion;
        // 每帧同步最新的ComputeShader引用, 避免Create时为null导致无效果
        this.gtaoComputeShader = settings.gtaoComputeShader;
    }

    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
    {
        RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
        desc.enableRandomWrite = true;
        desc.depthBufferBits = 0;
        desc.msaaSamples = 1;
        desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;

        downsamplingFactor = groundTruthAmbientOcclusion.downsamplingFactor.value;
        fullRes = new Vector2Int(desc.width, desc.height);
        downsampleRes = new Vector2Int(Mathf.CeilToInt((float)desc.width / downsamplingFactor), Mathf.CeilToInt((float)desc.height / downsamplingFactor));

        cmd.GetTemporaryRT(visualizeTextureID, desc);
        cmd.GetTemporaryRT(verticalBlurTextureID, desc);
        desc.height = downsampleRes.y;
        cmd.GetTemporaryRT(horizontalBlurTextureID, desc);
        desc.width = downsampleRes.x;
        cmd.GetTemporaryRT(gtaoTextureID, desc);
    }

    private void DoGTAOCalculation(CommandBuffer cmd, RenderTargetIdentifier depthid, RenderTargetIdentifier gtaoid, ComputeShader computeShader, bool allowTemporal)
    {
        if (!computeShader.HasKernel(gtaoKernelName)) return;
        int gtaoKernel = computeShader.FindKernel(gtaoKernelName);

        computeShader.GetKernelThreadGroupSizes(gtaoKernel, out uint x, out uint y, out uint z);
        cmd.SetComputeIntParam(computeShader, _GTAOFrameIndexID, frameIndex);
        cmd.SetComputeIntParam(computeShader, _GTAODownsamplingFactorID, downsamplingFactor);
        cmd.SetComputeIntParam(computeShader, _GTAOTemporalEnabledID, allowTemporal && groundTruthAmbientOcclusion.temporalEnabled.value ? 1 : 0);
        cmd.SetComputeVectorParam(computeShader, _GTAOTextureSizeID, new Vector4(fullRes.x, fullRes.y, 1.0f / fullRes.x, 1.0f / fullRes.y));

        cmd.SetComputeFloatParam(computeShader, _GTAOSampleRadiusID, groundTruthAmbientOcclusion.radius.value);
        cmd.SetComputeFloatParam(computeShader, _GTAODistributionPowerID, groundTruthAmbientOcclusion.distributionPower.value);
        cmd.SetComputeFloatParam(computeShader, _GTAOFalloffRangeID, groundTruthAmbientOcclusion.falloffRange.value);
        cmd.SetComputeFloatParam(computeShader, _GTAOHorizonBiasID, groundTruthAmbientOcclusion.horizonBias.value);

        cmd.SetComputeTextureParam(computeShader, gtaoKernel, _GTAODepthTextureID, depthid);
        cmd.SetComputeTextureParam(computeShader, gtaoKernel, _GTAORWTextureID, gtaoid);

        cmd.DispatchCompute(computeShader, gtaoKernel,
                Mathf.CeilToInt((float)downsampleRes.x / x),
                Mathf.CeilToInt((float)downsampleRes.y / y),
                1);
    }

    private void DoBlur(CommandBuffer cmd, RenderTargetIdentifier gtaoid, RenderTargetIdentifier horizontalid, RenderTargetIdentifier verticalid, ComputeShader computeShader)
    {
        if (!computeShader.HasKernel(blurHorizontalKernelName) || !computeShader.HasKernel(blurVerticalKernelName)) return;
        int horizontalKernel = computeShader.FindKernel(blurHorizontalKernelName);
        int verticalKernel = computeShader.FindKernel(blurVerticalKernelName);

        uint x, y, z;
        computeShader.GetKernelThreadGroupSizes(horizontalKernel, out x, out y, out z);
        cmd.SetComputeTextureParam(computeShader, horizontalKernel, _GTAOTextureID, gtaoid);
        cmd.SetComputeTextureParam(computeShader, horizontalKernel, _GTAORWBlurTextureID, horizontalid);
        cmd.DispatchCompute(computeShader, horizontalKernel,
                            Mathf.CeilToInt((float)fullRes.x / x),
                            Mathf.CeilToInt((float)downsampleRes.y / y),
                            1);

        computeShader.GetKernelThreadGroupSizes(verticalKernel, out x, out y, out z);
        cmd.SetComputeTextureParam(computeShader, verticalKernel, _GTAOTextureID, horizontalid);
        cmd.SetComputeTextureParam(computeShader, verticalKernel, _GTAORWBlurTextureID, verticalid);
        cmd.DispatchCompute(computeShader, verticalKernel,
                            Mathf.CeilToInt((float)fullRes.x / x),
                            Mathf.CeilToInt((float)fullRes.y / y),
                            1);
    }

    // 输出最终AO(可见度)到输出纹理, 供 URP _ScreenSpaceOcclusionTexture 使用
    private void DoOutputAO(CommandBuffer cmd, RenderTargetIdentifier verticalid, RenderTargetIdentifier outputid, ComputeShader computeShader)
    {
        if (!computeShader.HasKernel(visualizeKernelName)) return;
        int visualzieKernel = computeShader.FindKernel(visualizeKernelName);
        cmd.SetComputeFloatParam(computeShader, _GTAOIntensityID, groundTruthAmbientOcclusion.intensity.value);

        computeShader.GetKernelThreadGroupSizes(visualzieKernel, out uint x, out uint y, out uint z);
        cmd.SetComputeTextureParam(computeShader, visualzieKernel, _GTAOTextureID, verticalid);
        cmd.SetComputeTextureParam(computeShader, visualzieKernel, _GTAORWVisualizeTextureID, outputid);
        cmd.DispatchCompute(computeShader, visualzieKernel,
                            Mathf.CeilToInt((float)fullRes.x / x),
                            Mathf.CeilToInt((float)fullRes.y / y),
                            1);
    }

    // 确保历史RT存在且尺寸匹配 (持久, 不走TemporaryRT)
    private void EnsureHistoryRT()
    {
        for (int i = 0; i < 2; i++)
        {
            if (historyRT[i] == null || historyRT[i].width != fullRes.x || historyRT[i].height != fullRes.y)
            {
                if (historyRT[i] != null)
                {
                    historyRT[i].Release();
                    CoreUtils.Destroy(historyRT[i]);
                }

                historyRT[i] = new RenderTexture(fullRes.x, fullRes.y, 0, RenderTextureFormat.ARGBHalf)
                {
                    enableRandomWrite = true,
                    name = "_GTAOHistory" + i
                };
                historyRT[i].Create();
                historyValid = false;
            }
        }
    }

    // Temporal(Variance Clip): 读当前spatial结果 + 历史 → 写新历史
    private void DoTemporal(CommandBuffer cmd, RenderTargetIdentifier currentSpatialId, RenderTexture historyRead, RenderTexture historyWrite, ComputeShader computeShader)
    {
        if (!computeShader.HasKernel(temporalKernelName)) return;
        int temporalKernel = computeShader.FindKernel(temporalKernelName);

        cmd.SetComputeFloatParam(computeShader, _GTAOTemporalBlendID, groundTruthAmbientOcclusion.temporalBlend.value);
        cmd.SetComputeFloatParam(computeShader, _GTAOVarianceClampScaleID, groundTruthAmbientOcclusion.varianceClamp.value);

        computeShader.GetKernelThreadGroupSizes(temporalKernel, out uint x, out uint y, out uint z);
        cmd.SetComputeTextureParam(computeShader, temporalKernel, _GTAOTextureID, currentSpatialId);
        cmd.SetComputeTextureParam(computeShader, temporalKernel, _GTAOHistoryTextureID, historyRead);
        cmd.SetComputeTextureParam(computeShader, temporalKernel, _GTAORWVisualizeTextureID, historyWrite);
        cmd.DispatchCompute(computeShader, temporalKernel,
                            Mathf.CeilToInt((float)fullRes.x / x),
                            Mathf.CeilToInt((float)fullRes.y / y),
                            1);
    }

    public void Dispose()
    {
        for (int i = 0; i < 2; i++)
        {
            if (historyRT[i] != null)
            {
                historyRT[i].Release();
                CoreUtils.Destroy(historyRT[i]);
                historyRT[i] = null;
            }
        }
        historyValid = false;
        historyCameraId = -1;
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (gtaoComputeShader == null || groundTruthAmbientOcclusion == null)
            return;

        // CPU Shader 全局可能仍是上一相机；从当前 renderer 取得已分配的深度副本。
        RTHandle depthCopy = renderingData.cameraData.renderer is UniversalRenderer
            ? CameraDepthCopyField?.GetValue(renderingData.cameraData.renderer) as RTHandle : null;
        RenderTexture cameraDepthTexture = depthCopy?.rt;
        if (cameraDepthTexture == null || cameraDepthTexture.width != fullRes.x || cameraDepthTexture.height != fullRes.y)
        {
            if (!warnedMissingDepth)
            {
                Debug.LogWarning("[GTAO] Current renderer depth texture is unavailable or does not match the camera target; AO skipped.", renderingData.cameraData.camera);
                warnedMissingDepth = true;
            }
            CommandBuffer skip = CommandBufferPool.Get("GTAO Missing Depth");
            skip.SetRenderTarget(visualizeTextureID);
            skip.ClearRenderTarget(false, true, Color.white);
            skip.SetGlobalTexture(_GTAOCompositeAOTextureID, Texture2D.whiteTexture);
            CoreUtils.SetKeyword(skip, k_ScreenSpaceOcclusionKeyword, false);
            skip.SetRenderTarget(renderingData.cameraData.renderer.cameraColorTargetHandle.nameID,
                RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                renderingData.cameraData.renderer.cameraDepthTargetHandle.nameID,
                RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            context.ExecuteCommandBuffer(skip);
            CommandBufferPool.Release(skip);
            return;
        }
        bool isSceneViewCamera = renderingData.cameraData.cameraType == CameraType.SceneView;

        if (!gtaoComputeShader.HasKernel(gtaoKernelName) || !gtaoComputeShader.HasKernel(blurHorizontalKernelName) || !gtaoComputeShader.HasKernel(blurVerticalKernelName) || !gtaoComputeShader.HasKernel(visualizeKernelName))
        {
            if (Time.frameCount % debugLogFrameInterval == 0)
                Debug.LogWarning("[GTAO] ComputeShader kernel缺失，GTAO未执行。请检查GTAOComputeShader引用与kernel命名。", renderingData.cameraData.camera);
            return;
        }

        CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
        // Blit 可能改变当前附件；恢复相机颜色与深度供后续 URP Pass 使用。
        cmd.SetRenderTarget(renderingData.cameraData.renderer.cameraColorTargetHandle.nameID,
            RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
            renderingData.cameraData.renderer.cameraDepthTargetHandle.nameID,
            RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
        context.ExecuteCommandBuffer(cmd);
        cmd.Clear();

        using (new ProfilingScope(cmd, profilingSampler))
        {
            using (new ProfilingScope(cmd, gtaoSampler))
            {
                // 使用URP生成的 _CameraDepthTexture (依赖深度预pass)
                DoGTAOCalculation(cmd, new RenderTargetIdentifier(cameraDepthTexture), new RenderTargetIdentifier(gtaoTextureID), gtaoComputeShader, !isSceneViewCamera);
            }

            using (new ProfilingScope(cmd, blurSampler))
            {
                DoBlur(cmd,
                    new RenderTargetIdentifier(gtaoTextureID),
                    new RenderTargetIdentifier(horizontalBlurTextureID),
                    new RenderTargetIdentifier(verticalBlurTextureID),
                    gtaoComputeShader);
            }

            // 暴露调试RT, 方便在Frame Debugger中确认AO是否被写入
            cmd.SetGlobalTexture(_GTAODebugRawAOTextureID, gtaoTextureID);
            cmd.SetGlobalTexture(_GTAODebugBlurAOTextureID, verticalBlurTextureID);

            // Temporal去噪(可选): spatial结果 + 历史 → 去噪结果, 作为visualize输入
            RenderTargetIdentifier visualizeInput = new RenderTargetIdentifier(verticalBlurTextureID);
            if (!isSceneViewCamera && groundTruthAmbientOcclusion.temporalEnabled.value && gtaoComputeShader.HasKernel(temporalKernelName))
            {
                int cameraId = renderingData.cameraData.camera.GetInstanceID();
                if (cameraId != historyCameraId)
                {
                    historyCameraId = cameraId;
                    historyValid = false;
                }
                EnsureHistoryRT();
                int readIdx = frameIndex & 1;
                int writeIdx = readIdx ^ 1;
                if (!historyValid)
                {
                    // Never sample undefined history on the first temporal frame,
                    // after a camera switch, or after a render-size change.
                    cmd.Blit(verticalBlurTextureID, historyRT[readIdx]);
                    historyValid = true;
                }
                using (new ProfilingScope(cmd, temporalSampler))
                {
                    DoTemporal(cmd,
                        new RenderTargetIdentifier(verticalBlurTextureID),
                        historyRT[readIdx], historyRT[writeIdx],
                        gtaoComputeShader);
                }
                visualizeInput = new RenderTargetIdentifier(historyRT[writeIdx]);
            }
            else if (!isSceneViewCamera)
            {
                historyValid = false;
            }

            using (new ProfilingScope(cmd, visualizeSampler))
            {
                // 计算最终AO(可见度)并写入输出纹理
                DoOutputAO(cmd,
                    visualizeInput,
                    new RenderTargetIdentifier(visualizeTextureID),
                    gtaoComputeShader);

                // 接入 URP 全局链路: 设置 SSAO 纹理 + 参数 + 开启关键字
                // URP 前向 Lit 会在不透明Pass自动采样并压暗环境光
                cmd.SetGlobalTexture(_GTAOCompositeAOTextureID, visualizeTextureID);

                if (groundTruthAmbientOcclusion.enableAO.value && groundTruthAmbientOcclusion.intensity.value > 0.0f)
                {
                    cmd.SetGlobalTexture(_SSAOTextureID, visualizeTextureID);
                    // URP14: x=1 使用 AO 纹理；x=0 会将采样值加 1，抵消遮蔽。
                    cmd.SetGlobalVector(_AmbientOcclusionParamID,
                        new Vector4(1f, 0f, 0f, groundTruthAmbientOcclusion.directLightingStrength.value));
                    CoreUtils.SetKeyword(cmd, k_ScreenSpaceOcclusionKeyword, true);
                }
                else
                {
                    cmd.SetGlobalVector(_AmbientOcclusionParamID, Vector4.zero);
                    CoreUtils.SetKeyword(cmd, k_ScreenSpaceOcclusionKeyword, false);
                }
            }
        }
        frameIndex = (++frameIndex) % 60;

        // Blit 可能改变当前附件；恢复相机颜色与深度供后续 URP Pass 使用。
        cmd.SetRenderTarget(renderingData.cameraData.renderer.cameraColorTargetHandle.nameID,
            RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
            renderingData.cameraData.renderer.cameraDepthTargetHandle.nameID,
            RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
        context.ExecuteCommandBuffer(cmd);
        cmd.Clear();
        CommandBufferPool.Release(cmd);
    }

    public override void OnCameraCleanup(CommandBuffer cmd)
    {
        // 关闭关键字, 避免影响后续相机/帧
        CoreUtils.SetKeyword(cmd, k_ScreenSpaceOcclusionKeyword, false);

        cmd.ReleaseTemporaryRT(gtaoTextureID);
        cmd.ReleaseTemporaryRT(horizontalBlurTextureID);
        cmd.ReleaseTemporaryRT(verticalBlurTextureID);
        cmd.ReleaseTemporaryRT(visualizeTextureID);
    }
}

/// <summary>
/// 调试Pass: 在不透明渲染之后, 把AO灰度图(_VisualizeBuffer)直接绘制到屏幕。
/// 仅当 Volume 的 debugAOToScreen 开启时由 Feature 入队。
/// </summary>
public class GTAODebugBlitPass : ScriptableRenderPass
{
    private static readonly int visualizeTextureID = Shader.PropertyToID("_VisualizeBuffer");
    private ScriptableRenderer renderer;
    private ProfilingSampler m_DebugProfilingSampler = new ProfilingSampler("GTAO Debug AO To Screen");

    public void Setup(ScriptableRenderer renderer)
    {
        this.renderer = renderer;
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        CommandBuffer cmd = CommandBufferPool.Get("GTAO Debug AO To Screen");
        using (new ProfilingScope(cmd, m_DebugProfilingSampler))
        {
            // _VisualizeBuffer 由主Pass分配且存活至本帧结束, 直接Blit到屏幕显示灰度AO
            cmd.Blit(visualizeTextureID, renderer.cameraColorTargetHandle.nameID);
        }
        // Blit 可能改变当前附件；恢复相机颜色与深度供后续 URP Pass 使用。
        cmd.SetRenderTarget(renderingData.cameraData.renderer.cameraColorTargetHandle.nameID,
            RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
            renderingData.cameraData.renderer.cameraDepthTargetHandle.nameID,
            RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
        context.ExecuteCommandBuffer(cmd);
        cmd.Clear();
        CommandBufferPool.Release(cmd);
    }
}
