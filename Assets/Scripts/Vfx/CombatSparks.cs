using System.Collections.Generic;
using Spotlight.VFX;
using UnityEngine;

// 战斗火花特效库 v2 —— 按「Functional / Industrial Chamfer Sci-Fi Hard Surface」基调重做。
//   命中火花 / 近战 parry 闪光 / 弹道 parry 弹开 三类一次性爆发的统一入口
//   （脚底摩擦火花是持续发射器，归 FrictionSparks 组件）。
//
// 全程序化：贴图运行时生成、粒子系统代码装配、实例池化——零美术资产、零外部包。
//
// 风格语言（对齐工业硬表面的取舍，调研依据见外层任务文档）：
//   · 层次构成按业界通行三层：白热闪光（短促过曝，宁短勿长）+ 拉伸火花（主角）+ 结构件（细冲击环/电弧）；
//   · 混合口径用预乘 alpha（见 SparkAdditive.shader）：白超净室的亮面上 additive 会洗白丢形色，
//     预乘亮面保形色、暗面等价加光；HDR 染色保证有 Bloom 出辉光、没有也不失真；
//   · 颜色纪律：金属火花＝白热→金→橙（写实金属摩擦）；能量弹开＝白热→青→蓝（对应场景青色机能指示灯）。
//     不引入奇幻元素——四角星闪光已移除，换成「白热圆闪 + 极细冲击环」，细线环呼应本作场景的细线纹样；
//   · 金属火花开世界碰撞 + 落地弹跳：火花触地蹦跳后变暗变小，是「金属感」的关键特征。
//
// 调用入口（业务语义不在这里，只收「在哪、什么方向」）：
//   CombatSparks.PlayHit(point, direction, damage);              // 玩家攻击命中敌人（PlayerCombat.OnLandedHit 已接）
//   CombatSparks.PlayParry(point);                               // 近战 parry 成功（PlayerCombat.ReceiveParry 已接）
//   CombatSparks.PlayParryProjectile(point, incomingDirection);  // 对远程道具 parry 成功（同上，按 SourceType 分流）
//
// 注：数值为提案基线（参照业界经验值区间：火花 2–5cm、6–15m/s、Bounce 0.3–0.6 / 闪光 0.05–0.1s），
// 待 3C 与美术定稿后微调。
public static class CombatSparks
{
    // ===== 调参（RuinsFireConfig.asset 的 combatSparks 段——与火焰同一份配方） =====
    // FrictionSparks 接到配方后经 ApplyTune 注入；不接时用 CombatSparkTune 的字段默认值
    // （= 2026-10-15 v2 字面量，行为与并入配置前一致）。模板与灯光参数是**首用快照**：
    // 改了资产要重进 Play / 重开场景才对模板生效。

    private static CombatSparkTune _tune;

    /// <summary>注入配方（FrictionSparks.Awake 调用；传 null = 回到内置默认）。</summary>
    public static void ApplyTune(RuinsFireConfig config) { _tune = config != null ? config.combatSparks : null; }

    /// <summary>当前配方（懒初始化为内置默认，读-only；模板装配期取值一次）。</summary>
    private static CombatSparkTune T => _tune ?? (_tune = new CombatSparkTune());

    private static GameObject _poolRoot;
    private static Material _sparkMaterial;
    private static Material _popMaterial;
    private static Material _ringMaterial;
    private static Material _arcMaterial;
    private static readonly Dictionary<CombatSparksInternal.Kind, GameObject> Templates = new Dictionary<CombatSparksInternal.Kind, GameObject>();
    private static readonly Dictionary<CombatSparksInternal.Kind, List<SparkInstance>> Instances = new Dictionary<CombatSparksInternal.Kind, List<SparkInstance>>();

    /// <summary>火花点材质（FrictionSparks 的持续发射器与这里的爆发特效用同一份，工程里只有这一份火花贴图）。
    /// public：编辑器装配/自检工具（Assembly-CSharp-Editor）也要取它。</summary>
    public static Material SparkMaterial { get { EnsureMaterials(); return _sparkMaterial; } }

    // ===== 对外入口 =====

    /// <summary>攻击命中敌人的火花：白热圆闪 + 沿反弹方向的锥形火花（带落地弹跳），伤害越大火花越猛。</summary>
    public static void PlayHit(Vector3 point, Vector3 direction, float damage)
    {
        SparkInstance spark = Acquire(CombatSparksInternal.Kind.Hit);
        if (spark == null) return;
        // 火花沿「反弹方向」喷：命中方向指向目标，火花朝来路反回去，锥形铺开
        Vector3 spray = direction.sqrMagnitude > 0.0001f ? -direction.normalized : Vector3.up;
        spark.transform.SetPositionAndRotation(point, Quaternion.LookRotation(spray, Vector3.up));
        spark.Play(Mathf.Clamp01(damage / T.hitDamageFullScale));
    }

    /// <summary>近战 parry 成功的闪光：白热圆闪 + 极细白环 + 环形火花——金属对金属，偏白热接地。</summary>
    public static void PlayParry(Vector3 point, Vector3 incomingDirection = default)
    {
        SparkInstance spark = Acquire(CombatSparksInternal.Kind.Parry);
        if (spark == null) return;
        Quaternion rotation = incomingDirection.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(-incomingDirection.normalized) : Quaternion.identity;
        spark.transform.SetPositionAndRotation(point, rotation);
        spark.Play(1f);
        SparkFlashTune light = T.parryLight;
        SparkLightFader.Flash(point, light.color, light.intensity, light.range, light.duration);
    }

    /// <summary>对远程道具 parry 成功的弹开：白热圆闪 + 青色细环 + 沿弹道来路反喷的冷色碎屑 + 电弧——能量放电感。</summary>
    public static void PlayParryProjectile(Vector3 point, Vector3 incomingDirection)
    {
        SparkInstance spark = Acquire(CombatSparksInternal.Kind.ParryProjectile);
        if (spark == null) return;
        Vector3 deflect = incomingDirection.sqrMagnitude > 0.0001f ? -incomingDirection.normalized : Vector3.up;
        spark.transform.SetPositionAndRotation(point, Quaternion.LookRotation(deflect, Vector3.up));
        spark.Play(1f);
        SparkFlashTune light = T.parryProjectileLight;
        SparkLightFader.Flash(point, light.color, light.intensity, light.range, light.duration);
    }

    // ===== 池 =====

    private static SparkInstance Acquire(CombatSparksInternal.Kind kind)
    {
        EnsureTemplates();

        if (!Instances.TryGetValue(kind, out List<SparkInstance> list))
            Instances.Add(kind, list = new List<SparkInstance>());

        // 优先取闲置实例
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] != null && !list[i].IsBusy) return list[i];
        }

        // 没有闲置且到上限：复用列表里最早的那个（最老的实例在最前面）
        if (list.Count >= T.maxPerKind)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null) return list[i];
            }
            list.Clear(); // 全是死引用（场景卸载带走）：清空重新克隆
        }

        SparkInstance clone = Object.Instantiate(Templates[kind], _poolRoot.transform).GetComponent<SparkInstance>();
        clone.gameObject.SetActive(true);
        clone.Kind = kind;
        list.Add(clone);
        return clone;
    }

    // ===== 模板与共享资产 =====

    private static void EnsureTemplates()
    {
        // 模板挂在池根下（Play 模式 DontDestroyOnLoad）；但编辑模式池根绑场景、
        // 以及任何「池根被卸载」的路径都会留下死引用——先验一遍，有死就重建。
        bool templatesAlive = Templates.Count > 0;
        foreach (KeyValuePair<CombatSparksInternal.Kind, GameObject> pair in Templates)
        {
            if (pair.Value == null) { templatesAlive = false; break; }
        }
        if (_poolRoot != null && templatesAlive) return;

        Templates.Clear(); // 清掉死引用后整组重建，静态字典自愈
        if (_poolRoot == null)
        {
            _poolRoot = new GameObject("CombatSparks(Pool)");
            if (Application.isPlaying) Object.DontDestroyOnLoad(_poolRoot);
        }
        EnsureMaterials();

        if (!Templates.ContainsKey(CombatSparksInternal.Kind.Hit))
            Templates.Add(CombatSparksInternal.Kind.Hit, BuildHitTemplate());
        if (!Templates.ContainsKey(CombatSparksInternal.Kind.Parry))
            Templates.Add(CombatSparksInternal.Kind.Parry, BuildParryTemplate(projectile: false));
        if (!Templates.ContainsKey(CombatSparksInternal.Kind.ParryProjectile))
            Templates.Add(CombatSparksInternal.Kind.ParryProjectile, BuildParryTemplate(projectile: true));
    }

    private static GameObject NewHost(string name)
    {
        GameObject host = new GameObject(name);
        host.SetActive(false);
        host.AddComponent<SparkInstance>();
        return host;
    }

    // 命中：白热圆闪 + 锥形火花（火花层随伤害力度缩放；闪光层不吃缩放，反馈强度恒定）
    private static GameObject BuildHitTemplate()
    {
        GameObject host = NewHost("Spark_Hit");
        SparkInstance instance = host.GetComponent<SparkInstance>();
        CombatSparkTune t = T;
        SparkPopTune pop = t.hitPop;
        SparkSprayTune spray = t.hitSparks;

        ParticleSystem popPs = NewChildSystem(host, "Pop");
        ConfigureLayer(popPs, new LayerConfig
        {
            Material = _popMaterial, Count = 1, ShapeType = ParticleSystemShapeType.Sphere,
            Size = new Vector2(pop.sizeX, pop.sizeY), Lifetime = new Vector2(pop.lifetime, pop.lifetime),
            Color = BuildGradient(pop.color),
        });
        instance.Register(popPs, scaleWithStrength: false);

        ParticleSystem sparks = NewChildSystem(host, "Sparks");
        ConfigureLayer(sparks, new LayerConfig
        {
            Material = _sparkMaterial, Count = spray.count, ShapeType = ParticleSystemShapeType.Cone, ConeAngle = spray.coneAngle,
            Speed = new Vector2(spray.speedMin, spray.speedMax), Size = new Vector2(spray.sizeMin, spray.sizeMax),
            Lifetime = new Vector2(spray.lifeMin, spray.lifeMax),
            Gravity = spray.gravity, Stretch = spray.stretch,
            Bounce = spray.bounce, Dampen = spray.dampen, LifetimeLoss = spray.lifetimeLoss,
            Color = BuildGradient(spray.color),
        });
        instance.Register(sparks, scaleWithStrength: true);
        return host;
    }

    // parry 共用骨架：白热圆闪 + 细冲击环 + 火花层；projectile=true 时整套换冷色并加电弧层
    private static GameObject BuildParryTemplate(bool projectile)
    {
        GameObject host = NewHost(projectile ? "Spark_ParryProjectile" : "Spark_Parry");
        SparkInstance instance = host.GetComponent<SparkInstance>();
        CombatSparkTune t = T;
        SparkPopTune pop = projectile ? t.parryProjectilePop : t.parryPop;
        SparkRingTune ring = projectile ? t.parryProjectileRing : t.parryRing;
        SparkSprayTune spray = projectile ? t.parryProjectileSpray : t.parrySpray;

        ParticleSystem popPs = NewChildSystem(host, "Pop");
        ConfigureLayer(popPs, new LayerConfig
        {
            Material = _popMaterial, Count = 1, ShapeType = ParticleSystemShapeType.Sphere,
            Size = new Vector2(pop.sizeX, pop.sizeY),
            Lifetime = new Vector2(pop.lifetime, pop.lifetime),
            Color = BuildGradient(pop.color),
        });
        instance.Register(popPs, scaleWithStrength: false);

        // 细冲击环：尺寸从 ~0.35m 扩到 ~1.4m 后隐去——极细线环是本作场景线纹语言的同族
        ParticleSystem ringPs = NewChildSystem(host, "Ring");
        ConfigureLayer(ringPs, new LayerConfig
        {
            Material = _ringMaterial, Count = 1, ShapeType = ParticleSystemShapeType.Sphere,
            Size = new Vector2(ring.size, ring.size),
            Lifetime = new Vector2(ring.lifetime, ring.lifetime),
            Color = BuildGradient(ring.color),
            SizeCurve = AnimationCurve.EaseInOut(0f, 0.55f, 1f, ring.expandEnd), // 扩张环
            Unscaled = true,
        });
        instance.Register(ringPs, scaleWithStrength: false);

        // 火花层：近战环形炸开带弹跳；弹道沿反弹方向锥喷、无重力带阻尼（能量碎屑）
        ParticleSystem sprayPs = NewChildSystem(host, "Spray");
        ConfigureLayer(sprayPs, new LayerConfig
        {
            Material = _sparkMaterial, Count = spray.count,
            ShapeType = spray.coneAngle > 0f ? ParticleSystemShapeType.Cone : ParticleSystemShapeType.Sphere,
            ConeAngle = spray.coneAngle,
            Speed = new Vector2(spray.speedMin, spray.speedMax),
            Size = new Vector2(spray.sizeMin, spray.sizeMax),
            Lifetime = new Vector2(spray.lifeMin, spray.lifeMax),
            Gravity = spray.gravity,
            Stretch = spray.stretch,
            Drag = spray.drag,
            Bounce = spray.bounce, Dampen = spray.dampen, LifetimeLoss = spray.lifetimeLoss,
            Color = BuildGradient(spray.color),
            Unscaled = true,
        });
        instance.Register(sprayPs, scaleWithStrength: false);

        // 招架点常被角色躯干遮挡，四散火花必须有时间越过轮廓，不能直接套短促的命中配方。
        SparkSprayTune metal = t.parryMetalSparks;
        ParticleSystem metalPs = NewChildSystem(host, "MetalSparks");
        ConfigureLayer(metalPs, new LayerConfig
        {
            Material = _sparkMaterial, Count = metal.count,
            ShapeType = metal.coneAngle > 0f ? ParticleSystemShapeType.Cone : ParticleSystemShapeType.Sphere,
            ConeAngle = metal.coneAngle,
            Speed = new Vector2(metal.speedMin, metal.speedMax),
            Size = new Vector2(metal.sizeMin, metal.sizeMax),
            Lifetime = new Vector2(metal.lifeMin, metal.lifeMax),
            Gravity = metal.gravity, Stretch = metal.stretch, Drag = metal.drag,
            Bounce = metal.bounce, Dampen = metal.dampen, LifetimeLoss = metal.lifetimeLoss,
            Color = BuildGradient(metal.color), Unscaled = true,
        });
        metalPs.GetComponent<ParticleSystemRenderer>().minParticleSize = t.parryMetalMinScreenSize;
        instance.Register(metalPs, scaleWithStrength: false);

        if (projectile)
        {
            // 电弧：横向锯齿短线、随机朝向、一瞬即逝——能量弹开的签名元素
            ParticleSystem arcs = NewChildSystem(host, "Arcs");
            ConfigureLayer(arcs, new LayerConfig
            {
                Material = _arcMaterial, Count = t.arcs.count, ShapeType = ParticleSystemShapeType.Sphere,
                Speed = new Vector2(t.arcs.speedMin, t.arcs.speedMax),
                Size = new Vector2(t.arcs.sizeX, t.arcs.sizeY),
                Lifetime = new Vector2(t.arcs.lifeMin, t.arcs.lifeMax),
                RandomRotation = true,
                Color = BuildGradient(t.arcs.color),
                Unscaled = true,
            });
            instance.Register(arcs, scaleWithStrength: false);
        }
        return host;
    }

    // 模板的粒子层统一走子节点（同物体挂第二个 ParticleSystem 会被 Unity 拒掉，AddComponent 返回 null）
    private static ParticleSystem NewChildSystem(GameObject host, string name)
    {
        GameObject child = new GameObject(name);
        child.transform.SetParent(host.transform, false);
        return child.AddComponent<ParticleSystem>();
    }

    private static ParticleSystem ConfigureLayer(ParticleSystem ps, LayerConfig config)
    {
        ps.playOnAwake = false;

        ParticleSystem.MainModule main = ps.main;
        main.duration = 0.05f;             // 一次 burst，duration 只是发射窗口
        main.loop = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(config.Lifetime.x, config.Lifetime.y);
        main.startSpeed = new ParticleSystem.MinMaxCurve(config.Speed.x, config.Speed.y);
        main.startSize = new ParticleSystem.MinMaxCurve(config.Size.x, config.Size.y);
        main.gravityModifier = config.Gravity;
        main.simulationSpace = ParticleSystemSimulationSpace.World;  // 火花不跟着目标走
        main.useUnscaledTime = config.Unscaled;                     // parry 反馈穿透时缓；命中火花跟着 hit-stop 凝住
        main.stopAction = ParticleSystemStopAction.None;            // 归还由 SparkInstance 轮询，不依赖回调
        main.maxParticles = 160;
        if (config.RandomRotation) main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f); // 弧度

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)config.Count) });

        ParticleSystem.ShapeModule shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = config.ShapeType;
        shape.angle = config.ConeAngle;
        shape.radius = 0.03f;
        shape.randomDirectionAmount = config.ShapeType == ParticleSystemShapeType.Cone ? 0.25f : 0f;

        ParticleSystem.ColorOverLifetimeModule color = ps.colorOverLifetime;
        color.enabled = true;
        color.color = new ParticleSystem.MinMaxGradient(config.Color);

        ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, config.SizeCurve != null ? config.SizeCurve : DefaultShrinkCurve);

        if (config.Drag > 0f)
        {
            // 拖拽：限速模块限速 0 + dampen = 速度按比例衰减（能量碎屑：无重力、靠阻尼收住）
            ParticleSystem.LimitVelocityOverLifetimeModule limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = new ParticleSystem.MinMaxCurve(0f);
            limit.dampen = config.Drag;
            limit.space = ParticleSystemSimulationSpace.Local;
        }

        if (config.Bounce > 0f)
        {
            // 世界碰撞 + 弹跳：火花触地蹦跳后变小变暗（LifetimeLoss 快耗寿命≈快熄灭）
            ParticleSystem.CollisionModule collision = ps.collision;
            collision.enabled = true;
            collision.type = ParticleSystemCollisionType.World;
            collision.mode = ParticleSystemCollisionMode.Collision3D;
            collision.dampen = config.Dampen;
            collision.bounce = config.Bounce;
            collision.lifetimeLoss = config.LifetimeLoss;
            collision.minKillSpeed = 0.4f;
            collision.radiusScale = 1f;
        }

        ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
        renderer.material = config.Material != null ? config.Material : SparkMaterial;
        renderer.renderMode = config.Stretch > 0f ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
        renderer.velocityScale = config.Stretch;
        renderer.lengthScale = 2f;
        renderer.alignment = ParticleSystemRenderSpace.View;
        return ps;
    }

    private static readonly AnimationCurve DefaultShrinkCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0.15f);

    // 颜色梯度：白热 → 主色 → 熄灭。RGB 常保亮度、alpha 提前收零——
    // 预乘混合下若 RGB 先熄黑而 alpha 残留，会在亮面上压出灰渍（"烟灰"）。
    private static Gradient BuildGradient(SparkGradient g)
        => BuildGradient(g.hot, g.mid, g.extinct);

    private static Gradient BuildGradient(Color hot, Color mid, Color extinguished)
    {
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[] {
                new GradientColorKey(hot, 0f),
                new GradientColorKey(mid, 0.4f),
                new GradientColorKey(extinguished, 0.8f),
            },
            new[] {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(0.95f, 0.4f),
                new GradientAlphaKey(0.45f, 0.75f),
                new GradientAlphaKey(0f, 0.95f),
            });
        return gradient;
    }

    private sealed class LayerConfig
    {
        public Material Material;
        public int Count;
        public ParticleSystemShapeType ShapeType;
        public float ConeAngle;
        public Vector2 Speed;
        public Vector2 Size;
        public Vector2 Lifetime;
        public float Gravity;
        public float Stretch;
        public float Drag;
        public float Bounce;
        public float Dampen;
        public float LifetimeLoss;
        public bool Unscaled;
        public bool RandomRotation;
        public Gradient Color;
        public AnimationCurve SizeCurve;
    }

    // ===== 材质与程序化贴图 =====
    // 贴图口径：RGB = 颜色（常保亮度），alpha = 轮廓塑形；混合与 HDR 染色说明见 SparkAdditive.shader。

    private static void EnsureMaterials()
    {
        if (_sparkMaterial != null) return;
        Shader shader = ResolveShader();
        if (shader == null) return;
        _sparkMaterial = MakeMaterial(shader, BuildSparkTexture(), 1.9f);
        _popMaterial = MakeMaterial(shader, BuildPopTexture(), 2.4f);
        _ringMaterial = MakeMaterial(shader, BuildRingTexture(), 2.1f);
        _arcMaterial = MakeMaterial(shader, BuildArcTexture(), 3f);
    }

    // 着色器经 Resources 下的材质资产引用加载：资产引用保证打包时着色器不被裁剪；
    // 引擎里没有 Bloom 时 HDR 染色也只是更亮，不会失真。
    private static Shader ResolveShader()
    {
        Material baseMaterial = Resources.Load<Material>("CombatVfx/SparkAdditive");
        if (baseMaterial != null && baseMaterial.shader != null) return baseMaterial.shader;
        Shader found = Shader.Find("GameJam/SparkAdditive");
        if (found == null)
        {
            Debug.LogWarning("[CombatSparks] 找不到 GameJam/SparkAdditive：请确认 Assets/Resources/CombatVfx/SparkAdditive.mat 在库内（打包防裁剪靠它）");
        }
        return found;
    }

    private static Material MakeMaterial(Shader shader, Texture2D texture, float tint)
    {
        Material material = new Material(shader);
        material.mainTexture = texture;
        material.SetColor("_Tint", new Color(tint, tint, tint, 1f));
        return material;
    }

    /// <summary>火花点贴图：紧实圆点，白热核心带一线暖边（颜色主要由粒子的寿命渐变决定）。</summary>
    private static Texture2D BuildSparkTexture()
    {
        const int size = 64;
        Texture2D tex = NewTexture(size, size);
        Color32[] pixels = new Color32[size * size];
        Vector2 center = new Vector2(size - 1, size - 1) * 0.5f;
        float radius = size * 0.5f - 1f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), center) / radius;
                // 紧实轮廓 + 白热硬核：软高斯轮廓在大尺寸叠加时会糊成"火球"，火花要的是细而亮
                float profile = Mathf.Clamp01(Mathf.Pow(Mathf.Clamp01(1f - d), 7f) + Mathf.Pow(Mathf.Clamp01(1f - d), 20f) * 0.55f);
                Color c = Color.Lerp(new Color(1f, 0.82f, 0.55f), new Color(1f, 1f, 1f), Mathf.Pow(Mathf.Clamp01(1f - d), 5f));
                pixels[y * size + x] = Pack(c, profile);
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        return tex;
    }

    /// <summary>白热圆闪贴图：软圆 + 硬核，一帧大白的本体。</summary>
    private static Texture2D BuildPopTexture()
    {
        const int size = 96;
        Texture2D tex = NewTexture(size, size);
        Color32[] pixels = new Color32[size * size];
        Vector2 center = new Vector2(size - 1, size - 1) * 0.5f;
        float radius = size * 0.5f - 1f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), center) / radius;
                float profile = Mathf.Clamp01(Mathf.Pow(Mathf.Clamp01(1f - d), 2.6f) + Mathf.Pow(Mathf.Clamp01(1f - d), 12f) * 0.6f);
                pixels[y * size + x] = Pack(Color.white, profile);
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        return tex;
    }

    /// <summary>细冲击环贴图：一圈极细亮环 + 内侧微弱辉光——硬表面语言里的「精密环」。</summary>
    private static Texture2D BuildRingTexture()
    {
        const int size = 128;
        Texture2D tex = NewTexture(size, size);
        Color32[] pixels = new Color32[size * size];
        Vector2 center = new Vector2(size - 1, size - 1) * 0.5f;
        float radius = size * 0.5f - 1f;
        const float ringRadius = 0.72f;   // 归一化半径：贴图留一圈余量给辉光
        const float sigma = 0.014f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), center) / radius;
                float ring = Mathf.Exp(-((d - ringRadius) * (d - ringRadius)) / (2f * sigma * sigma));
                float glow = Mathf.Pow(Mathf.Clamp01(1f - d), 4f) * 0.08f;
                pixels[y * size + x] = Pack(Color.white, Mathf.Clamp01(ring + glow));
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        return tex;
    }

    /// <summary>电弧贴图：横向随机折线 + 两端收尖，能量弹开的签名元素。固定种子保证每次会话外形一致。</summary>
    private static Texture2D BuildArcTexture()
    {
        const int width = 256, height = 64, segments = 9;
        Texture2D tex = NewTexture(width, height);
        Color32[] pixels = new Color32[width * height];

        // 控制点随机游走 + 每段亮度随机（有的段亮、有的段暗，更像真的放电）
        System.Random rng = new System.Random(20261014);
        float[] ys = new float[segments];
        float[] brightness = new float[segments];
        ys[0] = height * 0.5f;
        brightness[0] = 1f;
        for (int i = 1; i < segments; i++)
        {
            ys[i] = Mathf.Clamp(ys[i - 1] + (float)(rng.NextDouble() * 2.0 - 1.0) * height * 0.3f, height * 0.16f, height * 0.84f);
            brightness[i] = 0.55f + (float)rng.NextDouble() * 0.45f;
        }

        for (int x = 0; x < width; x++)
        {
            float t = (float)x / (width - 1) * (segments - 1);
            int segment = Mathf.Min((int)t, segments - 2);
            float f = t - segment;
            float lineY = Mathf.Lerp(ys[segment], ys[segment + 1], f);
            float energy = Mathf.Lerp(brightness[segment], brightness[segment + 1], f);
            // 两端收尖：电弧端头细、中段亮
            float taper = Mathf.Pow(Mathf.Sin(Mathf.PI * x / (width - 1)), 0.35f);
            for (int y = 0; y < height; y++)
            {
                float dy = y - lineY;
                float stroke = Mathf.Exp(-(dy * dy) / (2f * 2.2f * 2.2f));
                pixels[y * width + x] = Pack(Color.white, Mathf.Clamp01(stroke * energy * taper));
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        return tex;
    }

    private static Texture2D NewTexture(int width, int height)
    {
        Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        return tex;
    }

    // 打包一个像素：RGB 常保亮度（熄灭由 alpha 负责）
    private static Color32 Pack(Color rgb, float alpha) => (Color32)new Color(rgb.r, rgb.g, rgb.b, Mathf.Clamp01(alpha));
}
