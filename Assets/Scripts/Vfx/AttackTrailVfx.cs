using UnityEngine;
using UnityEngine.Rendering;

// 刀光拖尾：剑（或右手）挥过时在空间里留下几条细弧，替代原来「以玩家为原点播一段网格刀光」的做法。
//
// 为什么用粒子 Trail 而不是一张平面刀光：
//   · 弧线由挥动轨迹本身生成——手怎么走，弧就怎么弯；不需要为每段攻击 K 网格刀光的角度。
//   · 粒子走世界坐标系，所以扫过的弧留在原地、不跟手，挥完自然淡出（与命中点火花同一套口径）。
//   · renderMode = Trail 只画拖尾不画粒子本体，多条粒子 = 多条细弧，正是参考图里的画法。
//
// 发射窗口：玩家攻击没有「结束事件」，靠 _defaultEmitSeconds 自己收尾；
// 敌人由动画事件 EnableHitbox / DisableHitbox 开关，万一漏了 Disable 也有 _maxEmitSeconds 兜底。
[DisallowMultipleComponent]
public sealed class AttackTrailVfx : MonoBehaviour
{
    /// <summary>发射器节点名（挂在剑骨 / 右手骨下）。</summary>
    public const string NodeName = "AttackTrail";

    /// <summary>材质在 Resources 下的路径（打包含引用，避免被裁掉）。</summary>
    public const string MaterialPath = "CombatVfx/AttackTrail";

    /// <summary>
    /// 刀光材质用的 shader：工程自研的 GameJam/SparkAdditive
    /// （URP 预乘 alpha + HDR 染色，摩擦火花/扬尘同款）。无光照计算，不会在 URP 下出紫黑。
    /// </summary>
    public const string MaterialShaderName = "GameJam/SparkAdditive";

    /// <summary>拖尾宽度（世界单位）。改这里时装配工具会按它校验预制体里的旧值。</summary>
    public const float TrailWidth = 0.12f;

    [Tooltip("实际的粒子系统；留空则取同节点上的 ParticleSystem")]
    [SerializeField] private ParticleSystem _system;

    [Tooltip("跟随目标（剑骨 / 右手骨）。刀光节点挂在 Vfx 下保持层级整洁，位置与朝向每帧跟这个骨骼走")]
    [SerializeField] private Transform _follow;

    [Tooltip("一次播放的默认发射窗口（秒）。玩家攻击没有结束事件，靠它自己收尾。")]
    [SerializeField, Min(0f)] private float _defaultEmitSeconds = 0.28f;

    [Tooltip("发射窗口上限（秒）。敌人靠 DisableHitbox 事件关；漏事件时不能一直喷。")]
    [SerializeField, Min(0.05f)] private float _maxEmitSeconds = 1.2f;

    // 剩余发射时间（秒）；>0 表示窗口还开着。
    private float _emitRemaining;

    public bool IsEmitting => _emitRemaining > 0f;
    public ParticleSystem System => _system != null ? _system : _system = GetComponent<ParticleSystem>();
    public Transform Follow => _follow;

    /// <summary>设置跟随骨骼（装配工具调用）。</summary>
    public void SetFollow(Transform follow) => _follow = follow;

    private void Awake()
    {
        if (_system == null) _system = GetComponent<ParticleSystem>();
        Stop();
    }

    private void LateUpdate()
    {
        // 节点挂在 Vfx 下，靠这里每帧贴到剑骨/右手骨上——层级整洁 + 轨迹正确两头都要。
        // LateUpdate 在动画之后，拿到的才是这一帧最终的骨骼世界变换。
        if (_follow == null) return;
        transform.SetPositionAndRotation(_follow.position, _follow.rotation);
    }

    private void Update()
    {
        if (_emitRemaining <= 0f) return;
        // 表现层按真实时间收窗口：时停 / 慢动作里刀光不该被拖成一团；
        // 而且那一帧剑本来也没在动，停发射不会缺画面。
        _emitRemaining -= Time.unscaledDeltaTime;
        if (_emitRemaining <= 0f) StopEmitting();
    }

    /// <summary>开一个发射窗口。<paramref name="seconds"/> ≤ 0 时用组件上的默认值。</summary>
    public void Play(float seconds = -1f)
    {
        ParticleSystem system = System;
        if (system == null) return;
        float window = seconds > 0f ? seconds : _defaultEmitSeconds;
        _emitRemaining = Mathf.Clamp(window, 0.02f, _maxEmitSeconds);
        ParticleSystem.EmissionModule emission = system.emission;
        emission.enabled = true;
        if (!system.isPlaying) system.Play(true);
    }

    /// <summary>关窗口：停止发射，已经画出来的弧自然淡出。</summary>
    public void Stop() => StopEmitting();

    /// <summary>立刻收干净（死亡 / 回收进池时用，避免复用对象带着旧弧）。</summary>
    public void ClearNow()
    {
        _emitRemaining = 0f;
        ParticleSystem system = System;
        if (system != null) system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private void StopEmitting()
    {
        ParticleSystem system = System;
        if (system != null && system.isPlaying) system.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    // ================= 工厂：编辑器装配与运行时补挂走同一套参数，不会漂成两套 =================

    /// <summary>在 <paramref name="owner"/> 下取（或建）发射器节点并配好，返回驱动组件。</summary>
    public static AttackTrailVfx Ensure(Transform owner, string nodeName = NodeName)
    {
        if (owner == null) return null;
        Transform node = owner.Find(nodeName);
        if (node == null)
        {
            var host = new GameObject(nodeName);
            node = host.transform;
            node.SetParent(owner, false);
            node.localPosition = Vector3.zero;
        }

        ParticleSystem system = node.GetComponent<ParticleSystem>();
        if (system == null) system = node.gameObject.AddComponent<ParticleSystem>();
        Configure(system);

        AttackTrailVfx trail = node.GetComponent<AttackTrailVfx>();
        if (trail == null) trail = node.gameObject.AddComponent<AttackTrailVfx>();
        trail._system = system;
        return trail;
    }

    /// <summary>把粒子系统配成「细弧刀光」。参数集中在这里，预制体与运行时补挂共用。</summary>
    public static void Configure(ParticleSystem system)
    {
        if (system == null) return;

        ParticleSystem.MainModule main = system.main;
        main.playOnAwake = false;
        main.loop = true;                                   // 窗口由 Play/Stop 控，loop 只负责"持续发射"
        main.duration = 1f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.16f, 0.22f);
        main.startSpeed = 0f;                               // 弧线靠发射点自己移动画出，粒子不该自己飞
        main.startSize = 0.02f;                             // 极小但非 0：0 会让粒子被整条跳过，拖尾一起消失
        main.startColor = Color.white;
        // 世界坐标：截取的是剑扫过的空间轨迹，手停下以后弧还留在原处淡出。
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 600;
        main.stopAction = ParticleSystemStopAction.None;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;

        ParticleSystem.EmissionModule emission = system.emission;
        emission.enabled = true;
        emission.rateOverTime = 240f;                       // 手几乎不动时也有基本密度
        emission.rateOverDistance = 26f;                    // 主要靠它：挥得越快、单位路程上的弧越密
        emission.SetBursts(new ParticleSystem.Burst[0]);

        // 发射点就是发射器自身位置（剑尖 / 手心），不需要形状。
        ParticleSystem.ShapeModule shape = system.shape;
        shape.enabled = false;

        ParticleSystem.TrailModule trail = system.trails;
        trail.enabled = true;
        trail.mode = ParticleSystemTrailMode.Ribbon;
        trail.ratio = 1f;
        trail.lifetime = new ParticleSystem.MinMaxCurve(0.16f, 0.22f);
        trail.minVertexDistance = 0.012f;                   // 越小弧越顺滑（代价是顶点多）
        // 关键设置：拖尾必须在世界空间模拟。留 false（默认）时拖尾跟着骨骼走，
        // 挂在快速摆动的右手上就等于缩成一个点，屏幕上什么都看不到。
        trail.worldSpace = true;
        trail.dieWithParticles = true;
        trail.sizeAffectsWidth = false;
        trail.sizeAffectsLifetime = false;
        trail.inheritParticleColor = true;
        trail.colorOverTrail = new ParticleSystem.MinMaxGradient(FadeAlpha(Color.white, 0.95f, 0f));
        trail.colorOverLifetime = new ParticleSystem.MinMaxGradient(FadeAlpha(Color.white, 1f, 0f));
        trail.widthOverTrail = new ParticleSystem.MinMaxCurve(TrailWidth, TaperCurve());
        trail.generateLightingData = false;
        trail.textureMode = ParticleSystemTrailTextureMode.Stretch;

        // 颜色随粒子寿命整体收淡，避免 Stop 后弧线"啪"地消失。
        ParticleSystem.ColorOverLifetimeModule color = system.colorOverLifetime;
        color.enabled = true;
        color.color = new ParticleSystem.MinMaxGradient(FadeAlpha(Color.white, 1f, 0f));

        ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
        if (renderer != null)
        {
            // Render Mode 必须是"会画东西"的模式：Render Mode = None 会把整个渲染器关掉，
            // 连 TrailModule 的拖尾一起不画（这是"完全看不见"的直接原因之一）。
            // 本体尺寸给 0.02（肉眼几乎不可见）就能只留下拖尾。
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Material material = Resources.Load<Material>(MaterialPath);
            // 资产没建出来（装配工具没跑）时用同一套参数现场搓一个，绝不落到 URP 的默认紫黑材质上。
            if (material == null) material = s_fallbackMaterial != null ? s_fallbackMaterial : s_fallbackMaterial = CreateMaterial();
            renderer.sharedMaterial = material;
            renderer.enabled = material != null;
        }
    }

    /// <summary>把关键设置打一行日志：装配/自检时用来确认"真的写进粒子系统了"。</summary>
    public static string Describe(ParticleSystem system)
    {
        if (system == null) return "system=null";
        ParticleSystem.MainModule main = system.main;
        ParticleSystem.TrailModule trail = system.trails;
        ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
        string material = renderer != null && renderer.sharedMaterial != null ? renderer.sharedMaterial.name : "null";
        return $"startSize={main.startSize.constant} simSpace={main.simulationSpace} "
            + $"trail(enabled={trail.enabled} mode={trail.mode} worldSpace={trail.worldSpace} ratio={trail.ratio} "
            + $"ribbons={trail.ribbonCount} minVertex={trail.minVertexDistance}) "
            + $"render:{renderer?.renderMode} material={material} rendererEnabled={renderer?.enabled}";
    }

    // 现场兜底的材质（只在资产缺失时用；装配工具会把它落盘成 Assets/Resources/CombatVfx/AttackTrail.mat）
    private static Material s_fallbackMaterial;

    /// <summary>
    /// 刀光材质：GameJam/SparkAdditive（URP 预乘 alpha + HDR 染色），与摩擦火花同一套口径。
    /// 之前试过 URP Lit（透明+加法+自发光），实测在这个工程的粒子拖尾上会渲染成紫黑，
    /// 所以固定用无光照的粒子 shader；颜色交给 _Tint（>1 = 有 Bloom 时出辉光）。
    /// </summary>
    public static Material CreateMaterial(string name = "AttackTrail")
    {
        Shader shader = Shader.Find(MaterialShaderName);
        if (shader == null) return null;
        var material = new Material(shader) { name = name };
        material.SetColor("_Tint", new Color(3f, 3f, 3f, 1f));
        material.renderQueue = (int)RenderQueue.Transparent;
        return material;
    }

    // 尾宽从头到尾收窄，看起来才像"刃口划出来的线"而不是飘带。
    private static AnimationCurve TaperCurve()
    {
        var curve = new AnimationCurve(
            new Keyframe(0f, 1f),
            new Keyframe(0.35f, 0.9f),
            new Keyframe(1f, 0.4f));
        return curve;
    }

    // 白到透明的渐变：RGB 常保亮度，alpha 负责熄灭（与 CombatSparks 同一套预乘口径）。
    private static Gradient FadeAlpha(Color color, float startAlpha, float endAlpha)
    {
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
            new[] { new GradientAlphaKey(startAlpha, 0f), new GradientAlphaKey(startAlpha, 0.25f), new GradientAlphaKey(endAlpha, 1f) });
        return gradient;
    }
}
