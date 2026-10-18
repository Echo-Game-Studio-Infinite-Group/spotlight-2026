using Spotlight.VFX;
using UnityEngine;

// 玩家与「表面」的摩擦火花：地面高速蹭火花 / 滑铲 / 贴墙滑行。
// 一个组件管两种源（地面 + 墙面）：配方、视觉语言、发射方式完全一样，拆成两个组件只会让
// 「这一帧该谁亮」变成两处判断，还容易两个一起开。
//
// 实现要点：
//   · 发射用 rateOverDistance（按移动距离发射，不按秒）：速度越快单位路程火花越多，天然就是「摩擦强度」；
//   · 发射器每帧摆到脚下 / 墙面接触点，朝向速度反方向——火花像被脚或肩「蹭」出来；
//   · 粒子走世界坐标系，火花留在原地，不跟着人跑；
//   · 开世界碰撞 + 落地弹跳，金属摩擦的写实签名（与 CombatSparks 同一口径）；
//   · 门槛：地面取 GroundSpeedThreshold；滑铲例外（滑铲速度 ≈0.8×阈值，按速度一刀切会永远不出火花）；
//     贴墙只要在划墙就出，强度按墙面速度。
//
// 发射器可以落盘在 Player/Vfx 下（编辑器装配工具会建好并接线），也可以缺着——
// 缺就运行时补一个，两条路径走同一个 CreateEmitter，配置不会漂。
[RequireComponent(typeof(PlayerMotor))]
[DisallowMultipleComponent]
public sealed class FrictionSparks : MonoBehaviour
{
    /// <summary>火花材质在 Resources 下的路径（与 CombatSparks 共用，也是打包防裁剪的引用）。</summary>
    public const string SparkMaterialPath = "CombatVfx/SparkAdditive";

    /// <summary>尘土材质在 Resources 下的路径（由 GameJam/DustSoft 生成，手搓的软圆 shader）。</summary>
    public const string DustMaterialPath = "CombatVfx/FootDust";

    /// <summary>尘土着色器名（程序化软圆，不依赖贴图；材质缺失时用它兜底）。</summary>
    public const string DustShaderName = "GameJam/DustSoft";

    /// <summary>地面扬尘发射器节点名（挂 Player/Vfx 下）。</summary>
    public const string GroundedDustName = "FootDustGrounded";

    /// <summary>贴墙扬尘发射器节点名（挂 Player/Vfx 下）。</summary>
    public const string WallDustName = "FootDustWall";

    [Header("扬尘（只在移动时扬；地面 / 贴墙各一个发射器）")]
    [Tooltip("地面移动时的扬尘发射器（挂在 Player/Vfx 下，可空：空则运行时补）")]
    [SerializeField] private ParticleSystem _dustGroundedEmitter;

    [Tooltip("贴墙滑行时的扬尘发射器（挂在 Player/Vfx 下，可空：空则运行时补）")]
    [SerializeField] private ParticleSystem _dustWallEmitter;

    [Tooltip("地面满速时的扬尘速率（颗/秒）")]
    [SerializeField, Min(0f)] private float _dustGroundedRate = 35f;

    [Tooltip("贴墙满速时的扬尘速率（颗/秒）")]
    [SerializeField, Min(0f)] private float _dustWallRate = 30f;

    [Tooltip("扬尘拉满所需速度（m/s）；比它慢就按比例减少")]
    [SerializeField, Min(0.01f)] private float _dustFullSpeed = 10f;

    [Tooltip("低于这个速度完全不扬尘——站着不动冒灰会读成「粒子卡住了」")]
    [SerializeField, Min(0f)] private float _dustMinSpeed = 0.6f;

    [Header("爆发火花发射器（可空：空则运行时自动补，挂在 Player/Vfx 下）")]
    [SerializeField] private ParticleSystem _groundEmitter;
    [SerializeField] private ParticleSystem _wallEmitter;

    [Header("调参配方")]
    [Tooltip("调参入口 = RuinsFireConfig.asset 的 combatSparks 段（与火焰同一份配方）。留空 = 用内置默认（提案基线）。")]
    [SerializeField] private RuinsFireConfig _config;

    [Header("触发门槛")]
    [Tooltip("地面起步速度 = GroundSpeedThreshold × 此系数（提案 1.0）")]
    [SerializeField, Min(0f)] private float _speedThresholdFactor = 1f;

    [Tooltip("滑铲强度倍率（提案 2.0）")]
    [SerializeField, Min(1f)] private float _slideBoost = 2f;

    [Tooltip("贴墙起步速度 = GroundSpeedThreshold × 此系数（贴墙本来就慢，门槛更低）")]
    [SerializeField, Range(0f, 1f)] private float _wallSpeedThresholdFactor = 0.35f;

    [Tooltip("贴墙时的强度倍率（墙面只蹭一侧，比地面略收）")]
    [SerializeField, Min(0f)] private float _wallBoost = 0.7f;

    [Header("发射量")]
    [Tooltip("低速档每米路程的火花数（提案 1.2）")]
    [SerializeField, Min(0f)] private float _sparksPerMeter = 1.2f;

    [Tooltip("高速档每米路程的火花数（提案 8，防高速时糊屏）")]
    [SerializeField, Min(1f)] private float _maxSparksPerMeter = 8f;

    private PlayerMotor _motor;
    private CharacterController _controller;

    private void Awake()
    {
        _motor = GetComponent<PlayerMotor>();
        _controller = GetComponent<CharacterController>();
        ApplyTune(_config);
    }

    private void OnEnable() => EnsureEmitters();

    private void OnDisable()
    {
        // 关闭（死亡 / 重开接线）时把残留粒子清掉，避免火花悬在半空
        StopEmitter(_groundEmitter);
        StopEmitter(_wallEmitter);
    }

    /// <summary>
    /// 接配方：给了资产就覆盖组件上已序列化的数值（滑铲段），并把命中 / parry 火花段交给 CombatSparks。
    /// 运行时补挂（PlayerVFXManager.EnsureFrictionSparks）也走这里，配方仍是唯一调参入口。
    /// </summary>
    public void ApplyTune(RuinsFireConfig config)
    {
        _config = config;
        if (config != null)
        {
            SlideSparkTune slide = config.combatSparks.slide;
            _speedThresholdFactor = slide.speedThresholdFactor;
            _slideBoost = slide.slideBoost;
            _sparksPerMeter = slide.sparksPerMeter;
            _maxSparksPerMeter = slide.maxSparksPerMeter;
        }
        CombatSparks.ApplyTune(config);
    }

    private void LateUpdate()
    {
        PlayerMotor motor = _motor;
        if (motor == null || motor.Params == null) return;
        EnsureEmitters();

        UpdateDust(motor);

        float speed = motor.HorizontalSpeed;
        float threshold = motor.Params.GroundSpeedThreshold;
        float maxSpeed = motor.Params.MaxSpeed;

        // 墙面优先：贴墙时不该再从脚下蹭（人已经离地了）
        SetRate(_groundEmitter, 0f);
        SetRate(_wallEmitter, 0f);

        if (motor.IsWallSliding)
        {
            if (speed < threshold * _wallSpeedThresholdFactor) return;
            Vector3 point = motor.WallPoint;
            if (point == Vector3.zero) point = motor.transform.position + motor.WallNormal * 0.35f;
            PlaceEmitter(_wallEmitter, point + motor.WallNormal * 0.02f, -MovementMath.Horizontal(motor.Velocity));
            SetRate(_wallEmitter, Rate(speed, maxSpeed, _wallBoost));
            return;
        }

        if (!motor.IsGrounded) return;
        // 滑铲例外：策划案的滑铲速度 ≈0.8×阈值，按速度一刀切会让滑铲永远不出火花，
        // 而滑铲恰恰是最该有摩擦感的动作。所以滑铲只要求接地。
        bool sliding = motor.IsSliding;
        if (!sliding && speed < threshold * _speedThresholdFactor) return;

        PlaceEmitter(_groundEmitter, FootPoint(), -MovementMath.Horizontal(motor.Velocity));
        SetRate(_groundEmitter, Rate(speed, maxSpeed, sliding ? _slideBoost : 1f));
    }

    // 扬尘：地面走脚底，贴墙走墙面接触点；两边都只在「真的在动」时扬，速率随速度线性涨
    // （站着不动冒灰会读成「粒子卡住了」）。
    private void UpdateDust(PlayerMotor motor)
    {
        UpdateDustEmitter(_dustGroundedEmitter, motor, false);
        UpdateDustEmitter(_dustWallEmitter, motor, true);
    }

    private void UpdateDustEmitter(ParticleSystem emitter, PlayerMotor motor, bool wall)
    {
        if (emitter == null) return;
        ParticleSystem.EmissionModule emission = emitter.emission;
        float speed = motor.HorizontalSpeed;
        // 贴墙时人已经离地，所以两边是互斥的：
        //   地面扬尘 = 接地且不在划墙；墙面扬尘 = 在划墙
        bool active = wall ? motor.IsWallSliding : motor.IsGrounded && !motor.IsWallSliding;
        if (!active || speed < _dustMinSpeed)
        {
            emission.rateOverTime = 0f;
            return;
        }
        emission.rateOverTime = (wall ? _dustWallRate : _dustGroundedRate) * Mathf.Clamp01(speed / _dustFullSpeed);

        if (!wall)
        {
            emitter.transform.position = FootPoint();
            return;
        }
        // 墙面：尘土从接触点顺着墙面向后飘，再带一点向外的分量——只朝外喷的话会糊在墙上、被身体挡住看不见。
        // Circle 形状沿本地 +Z 喷，所以把 +Z 指向「向后 + 向外」的合成方向。
        Vector3 normal = motor.WallNormal;
        if (normal.sqrMagnitude < 0.0001f) normal = -motor.transform.forward;
        Vector3 tangent = MovementMath.Horizontal(motor.Velocity);
        Vector3 spray = normal * 0.4f - tangent;
        if (spray.sqrMagnitude < 0.0001f) spray = normal;
        Vector3 point = motor.WallPoint;
        if (point == Vector3.zero) point = motor.transform.position + normal * 0.35f;
        emitter.transform.SetPositionAndRotation(point + normal * 0.12f,
            Quaternion.LookRotation(spray.normalized, Vector3.up));
    }

    // 每米火花数：随速度在基础档与高速档之间线性涨，再乘状态倍率（滑铲 / 贴墙）
    private float Rate(float speed, float maxSpeed, float multiplier)
        => Mathf.Lerp(_sparksPerMeter, _maxSparksPerMeter, Mathf.Clamp01(maxSpeed > 0f ? speed / maxSpeed : 0f)) * multiplier;

    // 脚底：用碰撞体 world 包围盒底边，别用 position.y ± height/2 手算
    // （CharacterController 的 center 抬在半高处，手算会把发射器塞到地面以下，火花直接在地底烧完）。
    private Vector3 FootPoint()
    {
        float y = (_controller != null ? _controller.bounds.min.y : transform.position.y) + 0.06f;
        return new Vector3(transform.position.x, y, transform.position.z);
    }

    private static void PlaceEmitter(ParticleSystem system, Vector3 point, Vector3 direction)
    {
        if (system == null) return;
        // 粒子锥默认沿本地 +Z 喷；朝向速度反方向，火花顺着被「蹭」出去
        Quaternion rotation = direction.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(direction.normalized, Vector3.up) : system.transform.rotation;
        system.transform.SetPositionAndRotation(point, rotation);
    }

    private static void SetRate(ParticleSystem system, float rate)
    {
        if (system == null) return;
        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverDistance = new ParticleSystem.MinMaxCurve(rate);
    }

    private static void StopEmitter(ParticleSystem system)
    {
        if (system != null) system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    /// <summary>缺发射器就补。预制体里预放了空占位节点（FootSparks / WallSparks / FootDust*），
    /// 粒子组件在运行时补上并配好；节点也缺才新建。两条路走同一个配置函数，不会漂。</summary>
    public void EnsureEmitters()
    {
        Transform vfxRoot = transform.Find("Vfx");
        Transform parent = vfxRoot != null ? vfxRoot : transform;
        if (_groundEmitter == null) _groundEmitter = EnsureEmitterNode(parent, "FootSparks", false, false, _config);
        if (_wallEmitter == null) _wallEmitter = EnsureEmitterNode(parent, "WallSparks", false, true, _config);
        if (_dustGroundedEmitter == null) _dustGroundedEmitter = EnsureEmitterNode(parent, GroundedDustName, true, false, _config);
        if (_dustWallEmitter == null) _dustWallEmitter = EnsureEmitterNode(parent, WallDustName, true, true, _config);
        // 常驻发射器：一直开着，靠 rateOverDistance / rateOverTime = 0 静默
        if (!_groundEmitter.isPlaying) _groundEmitter.Play();
        if (!_wallEmitter.isPlaying) _wallEmitter.Play();
        if (!_dustGroundedEmitter.isPlaying) _dustGroundedEmitter.Play();
        if (!_dustWallEmitter.isPlaying) _dustWallEmitter.Play();
    }

    private static ParticleSystem EnsureEmitterNode(Transform parent, string name, bool dust, bool wall, RuinsFireConfig config)
    {
        Transform node = parent.Find(name);
        if (node == null)
        {
            var created = new GameObject(name);
            created.transform.SetParent(parent, false);
            created.transform.localPosition = Vector3.zero;
            node = created.transform;
        }
        ParticleSystem system = node.GetComponent<ParticleSystem>();
        if (system != null) return system;   // 已装配过（预制体里带的）就别覆盖美术调好的参数
        system = node.gameObject.AddComponent<ParticleSystem>();
        if (dust) ConfigureDustEmitter(system, wall);
        else ConfigureSparkEmitter(system, wall, config);
        return system;
    }

    /// <summary>建扬尘发射器（地面 / 贴墙各一个，名字分别是 FootDustGrounded / FootDustWall）。</summary>
    public static ParticleSystem CreateDustEmitter(Transform parent, bool wall)
    {
        var host = new GameObject(wall ? WallDustName : GroundedDustName);
        host.transform.SetParent(parent, false);
        host.transform.localPosition = Vector3.zero;
        ParticleSystem system = host.AddComponent<ParticleSystem>();
        ConfigureDustEmitter(system, wall);
        return system;
    }

    public static void ConfigureDustEmitter(ParticleSystem system, bool wall)
    {
        ParticleSystem.MainModule main = system.main;
        main.loop = true;
        main.playOnAwake = false;
        main.duration = 2f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.0f, 1.6f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.35f, 0.8f);
        // 尺寸给足：这一版被反馈"不够明显"，尘土直径干到半米级才看得出形
        main.startSize = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.startColor = new Color(0.86f, 0.82f, 0.76f, 0.75f);   // 灰偏暖的土色，亮场景里也不发黑
        main.gravityModifier = -0.02f;                                  // 极轻上浮，尘土不是石头
        main.simulationSpace = ParticleSystemSimulationSpace.World;     // 尘土留在原地，人跑开
        main.maxParticles = 64;
        main.stopAction = ParticleSystemStopAction.None;

        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverTime = 0f;          // 由 FrictionSparks 按速度驱动（不动就是 0）
        emission.rateOverDistance = 0f;

        ParticleSystem.ShapeModule shape = system.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = wall ? 0.18f : 0.16f;
        // 地面：Circle 躺平（贴地铺开）；墙面：保持本地 XY 面，运行时让 +Z 对准墙面法线
        shape.rotation = wall ? Vector3.zero : new Vector3(-90f, 0f, 0f);

        // 尺寸：从小涨到大（尘土扩散），而不是缩小
        ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.5f, 1f, 2.2f));

        // 透明度：淡入淡出，避免凭空出现
        ParticleSystem.ColorOverLifetimeModule color = system.colorOverLifetime;
        color.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, 0.12f),
                new GradientAlphaKey(0.8f, 0.55f),
                new GradientAlphaKey(0f, 1f),
            });
        color.color = new ParticleSystem.MinMaxGradient(gradient);

        ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
        Material material = ResolveDustMaterial();
        renderer.sharedMaterial = material;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sortMode = ParticleSystemSortMode.Distance;   // 半透明尘土按距离排序，避免前后穿帮
        renderer.alignment = ParticleSystemRenderSpace.View;
        // 材质没接上时宁可不显示，也不要露出内置默认粒子材质（URP 下就是一片品红/紫黑）。
        if (material == null) renderer.enabled = false;
    }

    /// <summary>
    /// 尘土材质：优先 Resources 里的资产（预制体只能存资产材质）；
    /// 没有就现场用 GameJam/DustSoft 搓一个（该 shader 不依赖任何贴图，兜底不会变品红）。
    /// </summary>
    public static Material ResolveDustMaterial()
    {
        Material asset = Resources.Load<Material>(DustMaterialPath);
        if (asset != null) return asset;
        Shader shader = Shader.Find(DustShaderName);
        if (shader == null)
        {
            Debug.LogWarning("[FrictionSparks] 找不到 " + DustShaderName + " 着色器，扬尘先不显示");
            return null;
        }
        var runtime = new Material(shader) { name = "FootDust(Runtime)" };
        runtime.SetFloat("_Opacity", 0.8f);
        return runtime;
    }

    /// <summary>建一个摩擦火花发射器（地面 / 墙面共用同一套装配，只有形状与半径不同）。</summary>
    public static ParticleSystem CreateEmitter(Transform parent, string name, bool wall, RuinsFireConfig config)
    {
        var host = new GameObject(name);
        host.transform.SetParent(parent, false);
        host.transform.localPosition = Vector3.zero;
        ParticleSystem system = host.AddComponent<ParticleSystem>();
        ConfigureSparkEmitter(system, wall, config);
        return system;
    }

    public static void ConfigureSparkEmitter(ParticleSystem system, bool wall, RuinsFireConfig config)
    {
        // 参数源：配方（RuinsFireConfig 的 combatSparks.slide）；没接配方时
        // SlideSparkTune 的字段默认值就是原字面量提案基线，行为不变
        SlideSparkTune s = config != null ? config.combatSparks.slide : new SlideSparkTune();

        ParticleSystem.MainModule main = system.main;
        main.loop = true;
        main.playOnAwake = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(s.lifeMin, s.lifeMax);
        main.startSpeed = new ParticleSystem.MinMaxCurve(s.speedMin, s.speedMax);
        main.startSize = new ParticleSystem.MinMaxCurve(s.sizeMin, s.sizeMax);
        main.gravityModifier = s.gravity;        // 火花蹭出去后有明显的下坠弧线
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.stopAction = ParticleSystemStopAction.None;
        main.maxParticles = s.maxParticles;      // 高速滑铲时的存活上限，防糊屏

        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverTime = 0f;
        emission.rateOverDistance = new ParticleSystem.MinMaxCurve(0f); // 等移动状态驱动

        ParticleSystem.ShapeModule shape = system.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = wall ? s.coneAngle * 0.6f : s.coneAngle;   // 贴墙的锥更窄，看起来更「擦」而不是「喷」
        shape.radius = wall ? 0.06f : 0.04f;
        shape.rotation = new Vector3(wall ? 0f : s.coneTiltX, 0f, 0f); // 地面锥略微上抬：向后上方掠过地面

        ParticleSystem.ColorOverLifetimeModule color = system.colorOverLifetime;
        color.enabled = true;
        var gradient = new Gradient();
        // RGB 常保亮度、alpha 负责熄灭（预乘混合口径，与 CombatSparks.BuildGradient 同一套）
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(s.color.hot, 0f),
                new GradientColorKey(s.color.mid, 0.4f),
                new GradientColorKey(s.color.extinct, 0.8f),
            },
            new[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(0.95f, 0.4f),
                new GradientAlphaKey(0.45f, 0.75f),
                new GradientAlphaKey(0f, 0.95f),
            });
        color.color = new ParticleSystem.MinMaxGradient(gradient);

        ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0.1f));

        // 世界碰撞 + 落地弹跳：贴着地面蹦跳、LifetimeLoss 快耗寿命 ≈ 触地后快熄灭
        ParticleSystem.CollisionModule collision = system.collision;
        collision.enabled = true;
        collision.type = ParticleSystemCollisionType.World;
        collision.mode = ParticleSystemCollisionMode.Collision3D;
        collision.dampen = s.dampen;
        collision.bounce = s.bounce;
        collision.lifetimeLoss = s.lifetimeLoss;
        collision.minKillSpeed = 0.4f;
        collision.radiusScale = 1f;

        ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
        Material material = ResolveSparkMaterial();
        renderer.sharedMaterial = material;
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        renderer.velocityScale = s.stretch;
        renderer.lengthScale = s.lengthScale;
        renderer.alignment = ParticleSystemRenderSpace.View;
        // 材质拿不到就不显示：内置默认粒子材质在 URP 下是一整片品红/紫黑，比不显示更难查。
        if (material == null) renderer.enabled = false;
    }

    // 预制体里只能存资产材质：运行时 new 出来的材质存不进 .prefab（存了也会变成空引用），
    // 所以优先取 Resources 里那份（CombatSparks 也用它做打包防裁剪的引用）。
    private static Material ResolveSparkMaterial()
    {
        Material asset = Resources.Load<Material>(SparkMaterialPath);
        return asset != null ? asset : CombatSparks.SparkMaterial;
    }
}
