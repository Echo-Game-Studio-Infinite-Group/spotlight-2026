using System.Collections.Generic;
using UnityEngine;

// 战斗火花特效库：命中火花 / parry 闪光 / 弹道 parry 闪光，三类一次性爆发的统一入口。
// 全程序化：贴图运行时生成、粒子系统代码装配、实例按需克隆入池——零美术资产、零外部包。
//
// 为什么不引开源粒子包：这批都是玩法反馈级的小特效（几十粒子、零点几秒），代码生成足够；
// 外部包要背授权登记与 .meta GUID 冲突两笔账，GameJam 不值当。
//
// 时钟口径（与 DamagePopup 同一套考量）：
//   · 命中火花走缩放时间——命中自带 hit-stop，火花跟着一起凝住正是要的打击感；
//   · parry 闪光走 UnscaledTime——parry 成功会触发时缓，反馈必须穿透减速打出来。
//
// 调用方只管「在哪、什么方向」，业务语义不在这里：
//   CombatSparks.PlayHit(point, direction, damage);              // 玩家攻击命中敌人（PlayerCombat.OnLandedHit 已接）
//   CombatSparks.PlayParry(point);                               // 近战 parry 成功
//   CombatSparks.PlayParryProjectile(point, incomingDirection);  // 对远程道具 parry 成功
// parry 判定本体尚未落地（dev 侧待移植），落点接进判定处即可；当前由 DebugHUD 的 F5/F6 演示键驱动。
//
// 注：以下所有数值（粒子数/速度/颜色/时长）均为提案基线，待 3C 与美术定稿后调。
public static class CombatSparks
{
    // 一次性爆发特效的种类（滑铲火花是持续发射器，归 SlideSparks 组件，不进这个枚举）。
    // 枚举本体放在 CombatSparksInternal：SparkInstance 要公开持有它，不能是本类的私有类型。

    // 池策略：每类封顶缓存，播完的实例原地隐藏待复用；上限满了就复用最早的实例（重启播放）。
    private const int MaxPerKind = 10;

    private static GameObject _poolRoot;
    private static Material _sparkMaterial;
    private static Material _flashMaterial;
    private static readonly Dictionary<CombatSparksInternal.Kind, GameObject> Templates = new Dictionary<CombatSparksInternal.Kind, GameObject>();
    private static readonly Dictionary<CombatSparksInternal.Kind, List<SparkInstance>> Instances = new Dictionary<CombatSparksInternal.Kind, List<SparkInstance>>();

    /// <summary>全战斗火花共用的粒子材质（火花点贴图 + Sprites/Default，混合口径见 BuildSparkTexture 注释）。
    /// SlideSparks 的持续发射器与这里的爆发特效用同一份，工程里只有这一张火花贴图。
    /// public：编辑器装配/自检工具（Assembly-CSharp-Editor）也要取它。</summary>
    public static Material SparkMaterial
    {
        get { if (_sparkMaterial == null) _sparkMaterial = new Material(Shader.Find("Sprites/Default")) { mainTexture = BuildSparkTexture() }; return _sparkMaterial; }
    }

    // ===== 对外入口 =====

    /// <summary>攻击命中敌人的火花。direction 为结算传回的命中方向（攻击者→目标），伤害越大火花越猛。</summary>
    public static void PlayHit(Vector3 point, Vector3 direction, float damage)
    {
        SparkInstance spark = Acquire(CombatSparksInternal.Kind.Hit);
        if (spark == null) return;
        // 火花沿「反弹方向」喷：命中方向指向目标，火花朝来路反回去，锥形随机铺开
        Vector3 spray = direction.sqrMagnitude > 0.0001f ? -direction.normalized : Vector3.up;
        spark.transform.SetPositionAndRotation(point, Quaternion.LookRotation(spray, Vector3.up));
        spark.Play(Mathf.Clamp01(damage / 40f));
    }

    /// <summary>近战 parry 成功的闪光：星形闪光 + 环形火花，暖金色（与弹道 parry 的冷白区分）。</summary>
    public static void PlayParry(Vector3 point)
    {
        SparkInstance spark = Acquire(CombatSparksInternal.Kind.Parry);
        if (spark == null) return;
        spark.transform.SetPositionAndRotation(point, Quaternion.identity);
        spark.Play(1f);
        SparkLightFader.Flash(point, new Color(1f, 0.85f, 0.55f), 6f, 7f, 0.18f);
    }

    /// <summary>对远程道具 parry 成功的闪光：冷白色，火花沿「弹道来路的反方向」锥形喷出，表意弹开。</summary>
    public static void PlayParryProjectile(Vector3 point, Vector3 incomingDirection)
    {
        SparkInstance spark = Acquire(CombatSparksInternal.Kind.ParryProjectile);
        if (spark == null) return;
        Vector3 deflect = incomingDirection.sqrMagnitude > 0.0001f ? -incomingDirection.normalized : Vector3.up;
        spark.transform.SetPositionAndRotation(point, Quaternion.LookRotation(deflect, Vector3.up));
        spark.Play(1f);
        SparkLightFader.Flash(point, new Color(0.75f, 0.92f, 1f), 7f, 8f, 0.16f);
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
        if (list.Count >= MaxPerKind)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null) return list[i];
            }
            // 列表里全是死引用（场景卸载把实例带走了）：清空重新克隆
            list.Clear();
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
        // 共享材质不在这里造：ConfigureBurst 取 SparkMaterial 属性时惰性构造（Sprites/Default 必在着色器，
        // 预乘混合口径见 BuildSparkTexture 注释）

        if (!Templates.ContainsKey(CombatSparksInternal.Kind.Hit))
            Templates.Add(CombatSparksInternal.Kind.Hit, BuildTemplate(CombatSparksInternal.Kind.Hit));
        if (!Templates.ContainsKey(CombatSparksInternal.Kind.Parry))
            Templates.Add(CombatSparksInternal.Kind.Parry, BuildTemplate(CombatSparksInternal.Kind.Parry));
        if (!Templates.ContainsKey(CombatSparksInternal.Kind.ParryProjectile))
            Templates.Add(CombatSparksInternal.Kind.ParryProjectile, BuildTemplate(CombatSparksInternal.Kind.ParryProjectile));
    }

    // 三类特效各一个模板节点：
    //   命中 = 单层锥形火花；parry = 星形闪光 + 环形火花两层；弹道 parry = 星形闪光 + 定向锥形火花两层。
    private static GameObject BuildTemplate(CombatSparksInternal.Kind kind)
    {
        GameObject host = new GameObject($"Spark_{kind}");
        host.SetActive(false);
        SparkInstance instance = host.AddComponent<SparkInstance>();

        if (kind == CombatSparksInternal.Kind.Hit)
        {
            ParticleSystem sparks = host.AddComponent<ParticleSystem>();
            ConfigureBurst(sparks, new BurstConfig
            {
                Count = 26, Speed = new Vector2(3.5f, 8f), ConeAngle = 42f,
                Size = new Vector2(0.05f, 0.13f), Lifetime = new Vector2(0.14f, 0.38f),
                Gravity = 2.6f, Stretch = 0.16f, Unscaled = false,
                Color = BuildGradient(new Color(1f, 0.97f, 0.88f), new Color(1f, 0.5f, 0.1f), new Color(0.45f, 0.08f, 0.01f)),
            });
            instance.Register(sparks);
            return host;
        }

        bool isMelee = kind == CombatSparksInternal.Kind.Parry;

        // 一个 GameObject 只能挂一个 ParticleSystem——双层结构必须拆成子节点（局部旋转保持 identity，
        // 让锥形方向完全继承根节点的朝向）。
        // 闪光层：一颗大粒子快涨快缩，星形贴图就是「闪光」主体
        ParticleSystem flash = NewChildSystem(host, "Flash");
        ConfigureBurst(flash, new BurstConfig
        {
            Count = 1, Speed = new Vector2(0f, 0f), ConeAngle = 0f,
            Size = new Vector2(2.0f, 2.4f), Lifetime = new Vector2(0.13f, 0.13f),
            Gravity = 0f, Stretch = 0f, Unscaled = true,
            Material = GetFlashMaterial(),
            Color = BuildGradient(
                isMelee ? new Color(1f, 0.95f, 0.8f) : new Color(0.9f, 0.98f, 1f),
                isMelee ? new Color(1f, 0.72f, 0.25f) : new Color(0.55f, 0.85f, 1f),
                new Color(0f, 0f, 0f)),
        });
        instance.Register(flash);

        // 火花层：近战环形炸开；弹道沿反弹方向锥形喷
        ParticleSystem spray = NewChildSystem(host, "Spray");
        ConfigureBurst(spray, new BurstConfig
        {
            Count = isMelee ? 30 : 22,
            Speed = new Vector2(isMelee ? 2.5f : 5f, isMelee ? 6.5f : 11f),
            ConeAngle = isMelee ? 75f : 24f,
            Size = new Vector2(0.04f, 0.1f), Lifetime = new Vector2(0.18f, 0.5f),
            Gravity = 1.4f, Stretch = 0.2f, Unscaled = true,
            Color = BuildGradient(
                isMelee ? new Color(1f, 0.93f, 0.75f) : new Color(0.88f, 0.97f, 1f),
                isMelee ? new Color(1f, 0.6f, 0.15f) : new Color(0.4f, 0.7f, 1f),
                new Color(0f, 0f, 0f)),
        });
        instance.Register(spray);
        return host;
    }

    // 模板的粒子层统一走子节点（同物体挂第二个 ParticleSystem 会被 Unity 拒掉，AddComponent 返回 null）
    private static ParticleSystem NewChildSystem(GameObject host, string name)
    {
        GameObject child = new GameObject(name);
        child.transform.SetParent(host.transform, false);
        return child.AddComponent<ParticleSystem>();
    }

    private static ParticleSystem ConfigureBurst(ParticleSystem ps, BurstConfig config)
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

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)config.Count) });

        ParticleSystem.ShapeModule shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = config.ConeAngle > 0f ? ParticleSystemShapeType.Cone : ParticleSystemShapeType.Sphere;
        shape.angle = config.ConeAngle;
        shape.radius = 0.04f;
        shape.randomDirectionAmount = 0.25f; // 保留方向性但带点散乱，火花不至于太「整齐」

        ParticleSystem.ColorOverLifetimeModule color = ps.colorOverLifetime;
        color.enabled = true;
        color.color = new ParticleSystem.MinMaxGradient(config.Color);

        ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0.15f));

        ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
        renderer.material = config.Material != null ? config.Material : SparkMaterial;
        renderer.renderMode = config.Stretch > 0f ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
        renderer.velocityScale = config.Stretch;
        renderer.lengthScale = 2.5f;
        renderer.alignment = ParticleSystemRenderSpace.View;
        return ps;
    }

    // 颜色梯度：白热核心 → 主色 → 熄灭。RGB 同步衰减到黑（不是只掉 alpha），配合预乘混合才会真正「暗下去」。
    private static Gradient BuildGradient(Color hot, Color mid, Color extinguished)
    {
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[] {
                new GradientColorKey(hot, 0f),
                new GradientColorKey(mid, 0.45f),
                new GradientColorKey(extinguished, 1f),
            },
            new[] {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(0.85f, 0.5f),
                new GradientAlphaKey(0f, 1f),
            });
        return gradient;
    }

    private sealed class BurstConfig
    {
        public int Count;
        public Vector2 Speed;
        public float ConeAngle;
        public Vector2 Size;
        public Vector2 Lifetime;
        public float Gravity;
        public float Stretch;
        public bool Unscaled;
        public Gradient Color;
        public Material Material;
    }

    // ===== 程序化贴图 =====

    /// <summary>火花点贴图：白热核心向橙红衰减的圆点，预乘风格（RGB 随强度衰减到黑）。</summary>
    private static Texture2D BuildSparkTexture()
    {
        const int size = 64;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        Color32[] pixels = new Color32[size * size];
        Vector2 center = new Vector2(size - 1, size - 1) * 0.5f;
        float radius = size * 0.5f - 1f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), center) / radius;
                float intensity = Mathf.Pow(Mathf.Clamp01(1f - d), 2.6f);
                Color c = Color.Lerp(new Color(0.5f, 0.09f, 0.01f), new Color(1f, 0.6f, 0.16f), intensity);
                c = Color.Lerp(c, new Color(1f, 0.98f, 0.92f), Mathf.Pow(intensity, 4f));
                pixels[y * size + x] = (Color32)(c * intensity); // 预乘：亮度衰减的同时 RGB 收黑
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply(false, true); // 上传并设为只读，省一点显存
        return tex;
    }

    /// <summary>闪光星形贴图：径向核心 + 十字光针，parry「闪光」的主体。</summary>
    private static Material GetFlashMaterial()
    {
        if (_flashMaterial != null) return _flashMaterial;
        const int size = 128;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        Color32[] pixels = new Color32[size * size];
        float center = (size - 1) * 0.5f;
        float radius = size * 0.5f - 1f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - center) / radius;
                float dy = (y - center) / radius;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float core = Mathf.Pow(Mathf.Clamp01(1f - d), 3.2f);
                // 十字光针：沿轴向窄、横向快的衰减，两轴取大者
                float streakH = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(dx) * 1.1f), 6f)
                              * Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(dy) * 2.2f), 1.4f);
                float streakV = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(dy) * 1.1f), 6f)
                              * Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(dx) * 2.2f), 1.4f);
                float intensity = Mathf.Clamp01(Mathf.Max(core, Mathf.Max(streakH, streakV) * 0.85f));
                Color c = Color.Lerp(new Color(1f, 0.8f, 0.4f), new Color(1f, 1f, 0.97f), core);
                pixels[y * size + x] = (Color32)(c * intensity);
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        _flashMaterial = new Material(Shader.Find("Sprites/Default")) { mainTexture = tex };
        return _flashMaterial;
    }
}
