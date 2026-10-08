using UnityEngine;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// TAA Render Feature (Intel TAA 完整优化版 - 含膨胀与速度拒绝)
/// </summary>
public class TAARenderFeature : ScriptableRendererFeature
{
    // 单例模式，完美解决跨程序集调用
    public static TAARenderFeature Instance { get; private set; }

    // 当前正在渲染的相机（用于 TAAShowcase 获取正确的相机）
    public static Camera CurrentRenderingCamera { get; private set; }

    // 自定义拦截开关，彻底避开 URP 版本底层的 SetActive/enabled 冲突
    public bool isPassActive = true;

    [System.Serializable]
    public class Setting
    {
        // Composite night-soul at 503 first, then resolve its edges together with scene color.
        public RenderPassEvent evt = (RenderPassEvent)504;

        [Header("Jitter")]
        [Tooltip("亚像素抖动幅度。越大抗锯齿越强但越容易鬼影。推荐 0.3~0.8")]
        [Range(0f, 2f)] public float jitter = 0.306f;

        [Header("Blend")]
        [Tooltip("当前帧权重。越大越锐利但抗锯齿弱。推荐 0.08~0.15")]
        [Range(0.01f, 1f)] public float blend = 0.142f;

        [Header("History Clamp")]
        [Tooltip("AABB 收紧系数。<1 收紧, =1 默认, >1 放宽。鬼影严重时设 0.5~0.8")]
        [Range(0.2f, 2f)] public float clampScale = 0.727f;

        [Header("Disocclusion (Velocity Rejection)")]
        [Tooltip("速度拒绝阈值。当当前速度与历史速度差异超过此值时，丢弃历史帧（防瞬间传送残影）。推荐 0.01~0.05")]
        [Range(0.001f, 0.1f)] public float velocityThreshold = 0.0379f;

        [HideInInspector] public bool skipWhenStatic = false; // Legacy serialized field; original implementation never used it. Retained for compatibility only.

        [Header("Debug Split View")]
        [Tooltip("启用左右分屏对比: 左=未经 TAA, 右=TAA 处理后")]
        public bool debugSplitView = false;
        [Tooltip("分屏 X 位置 (0~1), 0.5=屏幕中线")]
        [Range(0f, 1f)] public float debugSplitX = 0.5f;
        [Tooltip("分界线宽度 (像素)")]
        [Range(0, 8)] public int debugLineWidthPx = 2;

        [Header("Debug Visualization")]
        [Tooltip("调试模式：0=正常TAA, 1=MotionVector, 2=DilatedMV, 3=Depth, 4=HistoryFrame")]
        [Range(0, 4)] public int debugVisMode = 0;
        [Tooltip("关闭 Jitter（用于排查 Jitter 是否污染 Motion Vector）")]
        public bool disableJitter = false;
    }

    public Setting setting = new Setting();
    [SerializeField, HideInInspector] private Shader shader;
    [SerializeField] private Shader motionCopyShader;
    readonly Dictionary<Camera, TAARenderPass> passes = new Dictionary<Camera, TAARenderPass>();
    // URP14 exposes getters but no public setter for its separate jitter matrix.
    // A pinned compatibility bridge changes CameraData only, never Camera.projectionMatrix.
    static readonly MethodInfo SetCameraJitter = typeof(CameraData).GetMethod("SetViewProjectionAndJitterMatrix", BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,
        null,new[]{typeof(Matrix4x4),typeof(Matrix4x4),typeof(Matrix4x4)},null);
    bool warnedApi;
    readonly HashSet<int> warned = new HashSet<int>();
    public override void Create()
    {
        Release(); Instance=this;
        if(shader==null)shader=Shader.Find("Hidden/TAA");
        if(motionCopyShader==null)motionCopyShader=Shader.Find("Hidden/OurFunction/TAA/MotionHistoryCopy");
    }
    bool Eligible(Camera camera)
    {
        if(!isActive || !isPassActive || camera.cameraType!=CameraType.Game) return false;
        if(!camera.TryGetComponent<UniversalAdditionalCameraData>(out var data))return false;
        if(data.antialiasing==AntialiasingMode.TemporalAntiAliasing)
        {
            if(warned.Add(camera.GetInstanceID()))Debug.LogWarning("[TAA] Camera already uses URP Temporal AA; custom TAA is skipped to prevent double jitter/history.",camera);
            return false;
        }
        return data.renderType==CameraRenderType.Base && data.renderPostProcessing;
    }
    TAARenderPass GetPass(Camera camera)
    {
        if(!passes.TryGetValue(camera,out var pass)){pass=new TAARenderPass(this);passes.Add(camera,pass);}return pass;
    }
    public override void AddRenderPasses(ScriptableRenderer renderer,ref RenderingData data)
    {
        Camera camera=data.cameraData.camera;
        var stale=new List<Camera>();foreach(var pair in passes)if(pair.Key==null){pair.Value.Dispose();stale.Add(pair.Key);}foreach(var key in stale)passes.Remove(key);
        if(!Eligible(camera)){if(passes.TryGetValue(camera,out var disabled))disabled.Invalidate();return;}
        if(SetCameraJitter==null){if(!warnedApi){Debug.LogError("[TAA] URP CameraData jitter API unavailable; custom TAA skipped. No camera projection was changed.");warnedApi=true;}return;}
        var pass=GetPass(camera);if(!pass.IsReady())return;
        pass.PrepareFrame(camera);
        Matrix4x4 jitter=Matrix4x4.identity;
        if(!setting.disableJitter)
        {
            pass.AdvanceHalton();Vector2 h=pass.GetCurrentHalton();
            var descriptor=data.cameraData.cameraTargetDescriptor;
            jitter=Matrix4x4.Translate(new Vector3((h.x-.5f)*setting.jitter*2f/Mathf.Max(1,descriptor.width),
                (h.y-.5f)*setting.jitter*2f/Mathf.Max(1,descriptor.height),0));
        }
        try
        {
            object boxed=data.cameraData;
            // URP14's internal GetGPUProjectionMatrix(bool) applies its separate jitter
            // twice (J * GL(J * P)), unlike the public getter used by our NPR pass.
            // Fold jitter into CameraData's projection once so rasterization and all
            // inverse-projection consumers agree. URP has already cached the original
            // non-jittered motion matrices before AddRenderPasses; Camera is untouched.
            SetCameraJitter.Invoke(boxed,new object[]{data.cameraData.GetViewMatrix(),jitter * data.cameraData.GetProjectionMatrix(),Matrix4x4.identity});
            data.cameraData=(CameraData)boxed;
        }
        catch(System.Exception exception){if(!warnedApi){Debug.LogError("[TAA] URP14 jitter bridge failed; TAA skipped: "+exception.Message);warnedApi=true;}pass.Invalidate();return;}
        CurrentRenderingCamera=camera;pass.renderPassEvent=setting.evt;
        pass.ConfigureInput(ScriptableRenderPassInput.Motion|ScriptableRenderPassInput.Depth);renderer.EnqueuePass(pass);
    }
    public override void SetupRenderPasses(ScriptableRenderer renderer,in RenderingData data)
    {
        if(passes.TryGetValue(data.cameraData.camera,out var pass)){pass.src=renderer.cameraColorTargetHandle;pass.depth=renderer.cameraDepthTargetHandle;}
    }
    void Release()
    {
        foreach(var pass in passes.Values)pass.Dispose();passes.Clear();warned.Clear();warnedApi=false;
    }
    protected override void Dispose(bool disposing){Release();if(Instance==this)Instance=null;CurrentRenderingCamera=null;}

    private class TAARenderPass : ScriptableRenderPass
    {
        public RTHandle src, depth;
        public int RenderWidth {get;private set;}
        public int RenderHeight {get;private set;}
        private Matrix4x4 lastProjection;
        private int lastFrame=-1;
        private Vector3 lastPosition;
        private Quaternion lastRotation;
        public void Invalidate(){m_HistoryValid=false;}
        public void PrepareFrame(Camera camera){if(lastFrame>=0 && (Time.frameCount-lastFrame>1 || Vector3.Distance(camera.transform.position,lastPosition)>5f || Quaternion.Angle(camera.transform.rotation,lastRotation)>60f))m_HistoryValid=false;if(lastFrame>=0){for(int i=0;i<16;i++)if(Mathf.Abs(lastProjection[i]-camera.projectionMatrix[i])>.005f){m_HistoryValid=false;break;}}lastProjection=camera.projectionMatrix;lastFrame=Time.frameCount;lastPosition=camera.transform.position;lastRotation=camera.transform.rotation;}

        private static readonly Vector2[] s_Halton9 = new Vector2[]
        {
            new Vector2(0.5f,    1.0f/3.0f),  new Vector2(0.25f,   2.0f/3.0f),
            new Vector2(0.75f,   1.0f/9.0f),  new Vector2(0.125f,  4.0f/9.0f),
            new Vector2(0.625f,  7.0f/9.0f),  new Vector2(0.375f,  2.0f/9.0f),
            new Vector2(0.875f,  5.0f/9.0f),  new Vector2(0.0625f, 8.0f/9.0f),
            new Vector2(0.5625f, 1.0f/27.0f),
        };

        private const string k_ShaderName = "Hidden/TAA";
        private static readonly int s_BlendID       = Shader.PropertyToID("_Blend");
        private static readonly int s_ClampScaleID  = Shader.PropertyToID("_ClampScale");
        private static readonly int s_PreTexID      = Shader.PropertyToID("_PreTex");
        private static readonly int s_PreMvTexID    = Shader.PropertyToID("_PreMvTex");
        private static readonly int s_VelThreshID   = Shader.PropertyToID("_VelocityThreshold");
        private static readonly int s_RawTexID      = Shader.PropertyToID("_RawTex");
        private static readonly int s_DebugParamsID = Shader.PropertyToID("_DebugParams");
        private static readonly int s_TempID        = Shader.PropertyToID("_TAATemp");
        private static readonly int s_RawCopyID     = Shader.PropertyToID("_TAARaw");

        private int m_HaltonIdx;
        private readonly TAARenderFeature m_Feature;
        private Material m_Mat, m_MotionCopy;
        private RenderTexture m_PreRT;
        private RenderTexture m_PreMvRT;
        private Camera m_Cam;
        private int m_LastCameraID = -1;   // 追踪当前相机，切换时重置历史
        private bool m_HistoryValid;
        private readonly ProfilingSampler m_Sampler = new ProfilingSampler("TAA");

        public TAARenderPass(TAARenderFeature f) { m_Feature = f; }

        // Halton 序列管理（供 AddRenderPasses 调用，在几何体渲染前注入 Jitter）
        public void AdvanceHalton() { m_HaltonIdx = (m_HaltonIdx + 1) % 9; }
        public Vector2 GetCurrentHalton() { return s_Halton9[m_HaltonIdx % 9]; }

        public void Dispose()
        {
            if (m_PreRT != null) { m_PreRT.Release(); Object.DestroyImmediate(m_PreRT); m_PreRT = null; }
            if (m_PreMvRT != null) { m_PreMvRT.Release(); Object.DestroyImmediate(m_PreMvRT); m_PreMvRT = null; }
            CoreUtils.Destroy(m_Mat); CoreUtils.Destroy(m_MotionCopy);
        }

        private bool EnsureMaterial()
        {
            if(m_MotionCopy==null && m_Feature.motionCopyShader!=null)m_MotionCopy=CoreUtils.CreateEngineMaterial(m_Feature.motionCopyShader);
            if (m_Mat != null) return m_MotionCopy!=null;
            var sh = m_Feature.shader != null ? m_Feature.shader : Shader.Find(k_ShaderName);
            if (sh == null) return false;
            m_Mat = CoreUtils.CreateEngineMaterial(sh);
            return m_Mat != null && m_MotionCopy!=null;
        }

        public bool IsReady() { return EnsureMaterial(); }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            if (!EnsureMaterial()) return;

            m_Cam = renderingData.cameraData.camera;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (m_Mat == null || !renderingData.cameraData.postProcessEnabled) return;

            m_Cam = renderingData.cameraData.camera;
            int w = renderingData.cameraData.cameraTargetDescriptor.width;
            int h = renderingData.cameraData.cameraTargetDescriptor.height;
            RenderWidth=w;RenderHeight=h;

            // 【修复】检测 Timeline 切换相机，立即重置历史帧
            int currentCamID = m_Cam.GetInstanceID();
            if (m_LastCameraID != -1 && m_LastCameraID != currentCamID)
            {
                // 相机已切换，释放旧历史 RT，迫使下面重新创建
                if (m_PreRT != null) { m_PreRT.Release(); Object.DestroyImmediate(m_PreRT); m_PreRT = null; }
                if (m_PreMvRT != null) { m_PreMvRT.Release(); Object.DestroyImmediate(m_PreMvRT); m_PreMvRT = null; }
                m_HistoryValid = false;
            }
            m_LastCameraID = currentCamID;

            bool needInit = false;
            
            if (m_PreRT == null || m_PreRT.width != w || m_PreRT.height != h)
            {
                if (m_PreRT != null) { m_PreRT.Release(); Object.DestroyImmediate(m_PreRT); }
                m_PreRT = new RenderTexture(w, h, 0, RenderTextureFormat.DefaultHDR)
                {
                    name = "TAA_History", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
                };
                m_PreRT.Create();
                needInit = true;
                m_HistoryValid = false;
            }

            if (m_PreMvRT == null || m_PreMvRT.width != w || m_PreMvRT.height != h)
            {
                if (m_PreMvRT != null) { m_PreMvRT.Release(); Object.DestroyImmediate(m_PreMvRT); }
                m_PreMvRT = new RenderTexture(w, h, 0, RenderTextureFormat.RGHalf)
                {
                    name = "TAA_HistoryMV", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
                };
                m_PreMvRT.Create();
            }

            var s = m_Feature.setting;
            Vector4 dbg = new Vector4(s.debugSplitView ? 1f : 0f, s.debugSplitX, s.debugLineWidthPx, (float)s.debugVisMode);

            m_Mat.SetFloat(s_BlendID, s.blend);
            m_Mat.SetFloat(s_ClampScaleID, s.clampScale);
            m_Mat.SetFloat(s_VelThreshID, s.velocityThreshold);
            m_Mat.SetTexture(s_PreTexID, m_PreRT);
            m_Mat.SetTexture(s_PreMvTexID, m_PreMvRT);
            m_Mat.SetVector(s_DebugParamsID, dbg);

            var cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, m_Sampler))
            {
                if (needInit || !m_HistoryValid)
                {
                    cmd.Blit(src.nameID, m_PreRT);
                    cmd.SetRenderTarget(m_PreMvRT);
                    cmd.DrawProcedural(Matrix4x4.identity,m_MotionCopy,0,MeshTopology.Triangles,3,1);
                    m_HistoryValid = true;
                }
                else
                {
                    var desc = renderingData.cameraData.cameraTargetDescriptor;
                    desc.depthBufferBits = 0;
                    desc.msaaSamples = 1;

                    cmd.GetTemporaryRT(s_RawCopyID, desc, FilterMode.Bilinear);
                    cmd.Blit(src.nameID, s_RawCopyID);
                    cmd.SetGlobalTexture(s_RawTexID, s_RawCopyID);

                    cmd.GetTemporaryRT(s_TempID, desc, FilterMode.Bilinear);
                    cmd.Blit(src.nameID, s_TempID);

                    cmd.Blit(s_TempID, src.nameID, m_Mat, 0);
                    
                    cmd.Blit(src.nameID, m_PreRT);
                    cmd.SetRenderTarget(m_PreMvRT);
                    cmd.DrawProcedural(Matrix4x4.identity,m_MotionCopy,0,MeshTopology.Triangles,3,1);

                    cmd.ReleaseTemporaryRT(s_TempID);
                    cmd.ReleaseTemporaryRT(s_RawCopyID);
                }
            }
            cmd.SetRenderTarget(src.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, depth.nameID, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

    }
}
