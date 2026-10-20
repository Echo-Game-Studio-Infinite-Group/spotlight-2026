using System.Collections.Generic;
using Spotlight.VFX;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// 把玩家身上的全部特效装配进 Player.prefab 并接好线（不需要手拖任何引用）：
//   · Vfx/FootSparks    —— 地面高速摩擦 / 滑铲火花（持续发射器，FrictionSparks 驱动）
//   · Vfx/WallSparks    —— 贴墙滑行火花（同上，另一个发射器）
//   · Vfx/Hurt          —— 受击爆火花（一次性 burst，PlayerVFXManager 播）
//   · Vfx/WallImpact    —— 撞墙爆火花（一次性 burst，同上，锥体朝墙外）
//   · Vfx/AttackSlashes —— Hovl「Sword Slash Combo 1」解开后的四组刀光 AttackTrail1~4，
//     每组时间轴已平移到 0 秒、激活即播；PlayerVFXManager 每次只激活并播放其中一组
//   · 根节点上的 FrictionSparks 组件（缺就补）并把上面两个发射器 + 配方资产接进去
//
// 为什么是编辑器脚本而不是手改 YAML：Unity 会把内存里的旧副本反写回文件，手改必被覆盖（已经踩过）。
// 幂等：节点已存在就只重新配参数与接线；域重载后做一次便宜的完整性检查，缺才动资产，
// 失败（例如预制体开在 Prefab 模式里）只警告，菜单可手动重跑。
//
// 发射器配置都走运行时代码里的同一个工厂（FrictionSparks.CreateEmitter / 本文件的 ConfigureBurst），
// 所以"预制体里的"与"运行时补出来的"不会漂成两套。
[InitializeOnLoad]
public static class PlayerVfxSetup
{
    private const string PrefabPath = "Assets/Prefabs/Player.prefab";
    private const string EnemyTestPrefabPath = "Assets/Prefabs/EnemyTest.prefab";
    private const string EnemyHeavyPrefabPath = "Assets/Prefabs/EnemyHeavy.prefab";
    private const string SparkMaterialPath = "Assets/Resources/CombatVfx/SparkAdditive.mat";
    private const string DustMaterialPath = "Assets/Resources/CombatVfx/FootDust.mat";
    private const string FireConfigPath = "Assets/Art/VFX/Fire/RuinsFireConfig.asset";
    private const string ComboPrefabPath = "Assets/Art/Hovl Studio/Sword slash VFX/Prefabs/Sword Slash Combo 1.prefab";
    private const string VfxNodeName = "Vfx";
    private const string FootName = "FootSparks";
    private const string WallSparksName = "WallSparks";
    private const string StaleDustName = "FootDust";   // 旧版单发射器的名字，装配时清掉
    private const string HurtName = "Hurt";
    private const string WallImpactName = "WallImpact";
    private const string AttackVfxName = "AttackSlashes";

    // 已删除的旧刀光（AttackTrail 动态 Mesh 版 / 更早的网格刀光）：三个预制体上都不该再留着。
    private static readonly string[] StaleAttackNodeNames = { "AttackTrail", "Attack1", "Attack2" };

    static PlayerVfxSetup()
    {
        // 域重载刚结束时 AssetDatabase 未必就绪，推迟一帧再动资产。
        EditorApplication.delayCall += AutoSetup;
    }

    private static void AutoSetup()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        // 不用「一个会话只跑一次」的标记：那样一改工具（例如后来补了扬尘节点）就再也跑不到了，
        // 预制体永远缺新节点。改成每次域重载做一次便宜的完整性检查，缺才动资产。
        if (IsUpToDate())
        {
            Debug.Log("[PlayerVfxSetup] Player.prefab 玩家特效已是最新，跳过");
            return;
        }
        Setup();
    }

    /// <summary>便宜的完整性检查：直接读预制体资产（不 LoadPrefabContents、不写盘）。</summary>
    private static bool IsUpToDate()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null) return false;

        Transform vfxRoot = prefab.transform.Find(VfxNodeName);
        if (vfxRoot == null) return false;
        string[] expected =
        {
            FootName, WallSparksName, FrictionSparks.GroundedDustName, FrictionSparks.WallDustName,
            HurtName, WallImpactName, AttackVfxName,
        };
        foreach (string name in expected)
            if (vfxRoot.Find(name) == null) return false;

        // 刀光必须是解开的普通节点：四组 AttackTrail1~4，各自 playOnAwake 且从 0 秒开始。
        // 仍嵌套预制体或还带着原来的时间轴偏移，都算没装配好。
        Transform attackVfx = vfxRoot.Find(AttackVfxName);
        if (attackVfx.gameObject.activeSelf) return false;
        if (PrefabUtility.GetPrefabInstanceStatus(attackVfx.gameObject) != PrefabInstanceStatus.NotAPrefab) return false;
        if (!HasAttackTrailGroups(attackVfx)) return false;
        if (!AttackGroupsStartImmediately(attackVfx)) return false;

        // 旧的 AttackTrail / 网格刀光节点（玩家和敌人都算）
        if (HasStaleAttackNodes(PrefabPath)) return false;
        if (HasStaleAttackNodes(EnemyTestPrefabPath)) return false;
        if (HasStaleAttackNodes(EnemyHeavyPrefabPath)) return false;

        FrictionSparks friction = prefab.GetComponent<FrictionSparks>();
        PlayerVFXManager manager = prefab.GetComponent<PlayerVFXManager>();
        if (friction == null || manager == null) return false;

        // 节点在还不够：引用也得接上，否则运行起来是「有对象没人驱动」
        var frictionSerialized = new SerializedObject(friction);
        if (!HasReference(frictionSerialized, "_groundEmitter")) return false;
        if (!HasReference(frictionSerialized, "_wallEmitter")) return false;
        if (!HasReference(frictionSerialized, "_dustGroundedEmitter")) return false;
        if (!HasReference(frictionSerialized, "_dustWallEmitter")) return false;

        var managerSerialized = new SerializedObject(manager);
        if (!HasArrayItem(managerSerialized, "_hurtEffects")) return false;
        if (!HasArrayItem(managerSerialized, "_wallImpactEffects")) return false;
        if (!HasReference(managerSerialized, "_attackVfx")) return false;
        if (!HasArrayItems(managerSerialized, "_attackSlashes", 4)) return false;
        return true;
    }

    private static bool HasStaleAttackNodes(string prefabPath)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null) return false;
        foreach (Transform candidate in prefab.GetComponentsInChildren<Transform>(true))
            if (IsStaleAttackNode(candidate.name)) return true;
        return false;
    }

    private static bool IsStaleAttackNode(string name)
    {
        for (int i = 0; i < StaleAttackNodeNames.Length; i++)
            if (name == StaleAttackNodeNames[i]) return true;
        return false;
    }

    private static int CountDirectSlashes(Transform attackVfx)
    {
        int count = 0;
        foreach (Transform child in attackVfx)
            if (child.GetComponent<ParticleSystem>() != null) count++;
        return count;
    }

    private static bool HasAttackTrailGroups(Transform attackVfx)
    {
        int index = 0;
        foreach (Transform child in attackVfx)
        {
            if (child.GetComponent<ParticleSystem>() == null) continue;
            index++;
            if (child.name != "AttackTrail" + index) return false;
        }
        return index >= 4;
    }

    // 四组必须是一次性播放：组内所有发射器关掉 loop，主粒子首个 burst 在 0 秒。
    // 否则单段播放要等 Hovl 原始时间轴，播完还会按自身长度（2 秒）反复循环。
    private static bool AttackGroupsStartImmediately(Transform attackVfx)
    {
        foreach (Transform child in attackVfx)
        {
            ParticleSystem root = child.GetComponent<ParticleSystem>();
            if (root == null) continue;

            ParticleSystem[] systems = child.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem.MainModule main = systems[i].main;
                if (!main.playOnAwake || main.loop) return false;
            }

            if (root.emission.burstCount == 0) continue;
            var bursts = new ParticleSystem.Burst[root.emission.burstCount];
            root.emission.GetBursts(bursts);
            float first = float.MaxValue;
            for (int i = 0; i < bursts.Length; i++) first = Mathf.Min(first, bursts[i].time);
            if (first > 0.001f) return false;
        }
        return true;
    }

    private static bool HasReference(SerializedObject serialized, string propertyName)
    {
        SerializedProperty property = serialized.FindProperty(propertyName);
        return property != null && property.objectReferenceValue != null;
    }

    private static bool HasArrayItem(SerializedObject serialized, string propertyName)
    {
        SerializedProperty property = serialized.FindProperty(propertyName);
        if (property == null || !property.isArray || property.arraySize == 0) return false;
        return property.GetArrayElementAtIndex(0).objectReferenceValue != null;
    }

    private static bool HasArrayItems(SerializedObject serialized, string propertyName, int minCount)
    {
        SerializedProperty property = serialized.FindProperty(propertyName);
        if (property == null || !property.isArray || property.arraySize < minCount) return false;
        for (int i = 0; i < minCount; i++)
            if (property.GetArrayElementAtIndex(i).objectReferenceValue == null) return false;
        return true;
    }

    [MenuItem("超高速行者/战斗特效/装配玩家特效（摩擦 / 受击 / 撞墙 / 刀光）")]
    public static void SetupFromMenu() => Setup();

    private static void Setup()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[PlayerVfxSetup] 先退出 Play 模式再装配预制体");
            return;
        }
        Material spark = AssetDatabase.LoadAssetAtPath<Material>(SparkMaterialPath);
        if (spark == null)
        {
            Debug.LogWarning("[PlayerVfxSetup] 找不到 " + SparkMaterialPath + "，跳过装配");
            return;
        }
        var fireConfig = AssetDatabase.LoadAssetAtPath<RuinsFireConfig>(FireConfigPath);
        Material dustMaterial = EnsureDustMaterial();
        var comboPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ComboPrefabPath);
        if (comboPrefab == null)
            Debug.LogWarning("[PlayerVfxSetup] 找不到 " + ComboPrefabPath + "，刀光先不装配");

        GameObject root = null;
        try
        {
            root = PrefabUtility.LoadPrefabContents(PrefabPath);
            if (root == null)
            {
                Debug.LogWarning("[PlayerVfxSetup] 读不到 " + PrefabPath);
                return;
            }
            Transform vfxRoot = EnsureChild(root.transform, VfxNodeName);
            RemoveStaleAttackNodes(root.transform);

            // ---- 摩擦火花（持续发射器，配置来自 FrictionSparks 自己的工厂）----
            ParticleSystem foot = EnsureFrictionEmitter(vfxRoot, FootName, false, fireConfig);
            ParticleSystem wallSparks = EnsureFrictionEmitter(vfxRoot, WallSparksName, true, fireConfig);

            // ---- 扬尘（地面 / 贴墙各一个，都挂在 Vfx 下）----
            ParticleSystem dustGrounded = EnsureDustEmitter(vfxRoot, false, dustMaterial);
            ParticleSystem dustWall = EnsureDustEmitter(vfxRoot, true, dustMaterial);
            // 旧版的单发射器节点：留着会变成没人驱动的死对象，直接清掉
            RemoveChild(vfxRoot, StaleDustName);
            RemoveChild(vfxRoot, "FootDust(Clone)");

            // ---- 一次性爆火花 ----
            ParticleSystem hurt = EnsureBurstEmitter(vfxRoot, HurtName, coneAlongForward: false, spark);
            ParticleSystem wallImpact = EnsureBurstEmitter(vfxRoot, WallImpactName, coneAlongForward: true, spark);

            // ---- 攻击刀光（Hovl 四段整套播放）----
            GameObject attackVfx = comboPrefab != null ? EnsureAttackVfx(vfxRoot, comboPrefab) : null;
            ParticleSystem[] attackSlashes = attackVfx != null
                ? CollectDirectSlashes(attackVfx)
                : new ParticleSystem[0];

            // ---- 接线 ----
            FrictionSparks friction = root.GetComponent<FrictionSparks>();
            if (friction == null) friction = root.AddComponent<FrictionSparks>();
            var frictionSerialized = new SerializedObject(friction);
            SetReference(frictionSerialized, "_groundEmitter", foot);
            SetReference(frictionSerialized, "_wallEmitter", wallSparks);
            SetReference(frictionSerialized, "_dustGroundedEmitter", dustGrounded);
            SetReference(frictionSerialized, "_dustWallEmitter", dustWall);
            if (fireConfig != null) SetReference(frictionSerialized, "_config", fireConfig);
            frictionSerialized.ApplyModifiedPropertiesWithoutUndo();

            PlayerVFXManager manager = root.GetComponent<PlayerVFXManager>();
            if (manager == null)
            {
                Debug.LogWarning("[PlayerVfxSetup] Player.prefab 根节点没有 PlayerVFXManager，受击 / 撞墙只装配了节点未接线");
            }
            else
            {
                var managerSerialized = new SerializedObject(manager);
                WireArray(managerSerialized, "_hurtEffects", hurt);
                WireArray(managerSerialized, "_wallImpactEffects", wallImpact);
                if (fireConfig != null) SetReference(managerSerialized, "_sparkTune", fireConfig);
                if (attackVfx != null)
                {
                    SetReference(managerSerialized, "_attackVfx", attackVfx);
                    WireArray(managerSerialized, "_attackSlashes", attackSlashes);
                }
                managerSerialized.ApplyModifiedPropertiesWithoutUndo();
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[PlayerVfxSetup] Player.prefab 已装配 " + VfxNodeName + "/" + FootName + "、" + WallSparksName
                + "、" + HurtName + "、" + WallImpactName + "、" + AttackVfxName
                + "（" + attackSlashes.Length + " 段刀光），并接线到 FrictionSparks 与 PlayerVFXManager");

            // 敌人不再有刀光：清掉旧节点与已删除组件的空壳。
            CleanupPrefab(EnemyTestPrefabPath);
            CleanupPrefab(EnemyHeavyPrefabPath);
        }
        catch (System.Exception error)
        {
            Debug.LogWarning("[PlayerVfxSetup] 装配失败（预制体可能在 Prefab 模式中打开着）：" + error.Message);
        }
        finally
        {
            if (root != null) PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // 尘土材质：用自己搓的 GameJam/DustSoft（程序化软圆，不依赖贴图）。
    // 之前复用火烟材质会出问题——一旦材质没接上就退回内置默认粒子材质，URP 下是一整片品红/紫黑。
    // 注意：这是「工具生成并托管」的材质，每次装配都会把基线参数写回去
    //（"不够明显"这类反馈要能一键修正）；美术想自己调请改 shader 默认值或另存一份新材质再接。
    private static Material EnsureDustMaterial()
    {
        Shader shader = Shader.Find(FrictionSparks.DustShaderName);
        if (shader == null)
        {
            Debug.LogWarning("[PlayerVfxSetup] 找不到 " + FrictionSparks.DustShaderName + "，扬尘先不装配材质");
            return null;
        }

        Material existing = AssetDatabase.LoadAssetAtPath<Material>(DustMaterialPath);
        if (existing != null)
        {
            // 已有资产：shader 不对就换回来（上一版是火烟材质的复制品），参数按最新基线重写
            if (existing.shader != shader) existing.shader = shader;
            existing.SetFloat("_Softness", 2.0f);
            existing.SetFloat("_Opacity", 0.8f);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            return existing;
        }

        var material = new Material(shader) { name = "FootDust" };
        material.SetFloat("_Softness", 2.0f);
        material.SetFloat("_Opacity", 0.8f);      // 0.55 时反馈"不够明显"，直接拉高
        AssetDatabase.CreateAsset(material, DustMaterialPath);
        AssetDatabase.SaveAssets();
        Debug.Log("[PlayerVfxSetup] 已生成尘土材质 " + DustMaterialPath + "（shader " + FrictionSparks.DustShaderName + "）");
        return AssetDatabase.LoadAssetAtPath<Material>(DustMaterialPath);
    }

    private static ParticleSystem EnsureDustEmitter(Transform parent, bool wall, Material dustMaterial)
    {
        string name = wall ? FrictionSparks.WallDustName : FrictionSparks.GroundedDustName;
        Transform existing = parent.Find(name);
        ParticleSystem system;
        if (existing == null)
        {
            system = FrictionSparks.CreateDustEmitter(parent, wall);
        }
        else
        {
            system = existing.GetComponent<ParticleSystem>();
            if (system == null) system = existing.gameObject.AddComponent<ParticleSystem>();
            FrictionSparks.ConfigureDustEmitter(system, wall);
        }
        // 材质显式补一次：CreateAsset 之后 Resources.Load 未必当帧就能取到
        ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
        if (renderer != null)
        {
            if (dustMaterial != null) renderer.sharedMaterial = dustMaterial;
            renderer.enabled = renderer.sharedMaterial != null;   // 没材质就别显示成紫黑
        }
        return system;
    }

    private static void RemoveChild(Transform parent, string name)
    {
        Transform stale = parent.Find(name);
        if (stale == null) return;
        Debug.Log("[PlayerVfxSetup] 清掉旧的扬尘节点 " + name);
        Object.DestroyImmediate(stale.gameObject);
    }

    // AttackTrail 已整体删除：旧节点只剩 MeshFilter/Renderer 和一个丢失脚本的组件。
    private static bool RemoveStaleAttackNodes(Transform root)
    {
        var stale = new List<GameObject>();
        foreach (Transform candidate in root.GetComponentsInChildren<Transform>(true))
            if (IsStaleAttackNode(candidate.name)) stale.Add(candidate.gameObject);
        foreach (GameObject node in stale)
        {
            if (node == null) continue;
            Debug.Log("[PlayerVfxSetup] 清掉旧刀光节点 " + node.name);
            Object.DestroyImmediate(node);
        }
        return stale.Count > 0;
    }

    private static void CleanupPrefab(string prefabPath)
    {
        GameObject root = null;
        try
        {
            root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null) return;
            bool changed = RemoveStaleAttackNodes(root.transform);
            // EnemyVFXManager 已删除，敌人预制体上留下的是丢失脚本的空组件。
            int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);
            if (removed > 0)
            {
                changed = true;
                Debug.Log("[PlayerVfxSetup] " + prefabPath + " 清掉 " + removed + " 个丢失脚本的组件");
            }
            if (changed) PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        catch (System.Exception error)
        {
            Debug.LogWarning("[PlayerVfxSetup] 清理 " + prefabPath + " 失败（预制体可能在 Prefab 模式中打开着）：" + error.Message);
        }
        finally
        {
            if (root != null) PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // 用户要求解开 Hovl 预制体：四组刀光做成 Player.prefab 里的普通节点，直接可选中/调参。
    private static GameObject EnsureAttackVfx(Transform vfxRoot, GameObject comboPrefab)
    {
        Transform existing = vfxRoot.Find(AttackVfxName);
        GameObject attackVfx;
        if (existing != null && CountDirectSlashes(existing) >= 4)
        {
            attackVfx = existing.gameObject;
            if (PrefabUtility.GetPrefabInstanceStatus(attackVfx) != PrefabInstanceStatus.NotAPrefab)
                PrefabUtility.UnpackPrefabInstance(attackVfx, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        }
        else
        {
            if (existing != null) Object.DestroyImmediate(existing.gameObject);
            attackVfx = (GameObject)PrefabUtility.InstantiatePrefab(comboPrefab, vfxRoot);
            attackVfx.name = AttackVfxName;
            Transform t = attackVfx.transform;
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
            PrefabUtility.UnpackPrefabInstance(attackVfx, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        }
        // 根节点常驻激活会让四组 playOnAwake 在玩家出生瞬间一起播；攻击时由管理器点亮。
        attackVfx.SetActive(false);
        ConfigureAttackGroups(attackVfx.transform);
        return attackVfx;
    }

    // 原预制体四段排在同一条 5 秒时间轴上（主粒子 burst 在 0.1 / 0.45 / 0.6 / 1.15 秒），
    // 而且所有发射器都开着 loop。这里每组整体减去主粒子的首个 burst，组内烟/火/扭曲的
    // 相对节奏不变，同时关掉 loop、恢复 playOnAwake：激活当帧出刀，且只出一次。
    private static void ConfigureAttackGroups(Transform attackVfx)
    {
        int index = 0;
        foreach (Transform child in attackVfx)
        {
            ParticleSystem root = child.GetComponent<ParticleSystem>();
            if (root == null) continue;
            index++;
            child.name = "AttackTrail" + index;

            float offset = FirstBurstTime(root.emission);
            ParticleSystem[] systems = child.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem.MainModule main = systems[i].main;
                main.playOnAwake = true;
                main.loop = false;

                ParticleSystem.EmissionModule emission = systems[i].emission;
                if (emission.burstCount == 0) continue;
                var bursts = new ParticleSystem.Burst[emission.burstCount];
                emission.GetBursts(bursts);
                for (int b = 0; b < bursts.Length; b++)
                    bursts[b].time = Mathf.Max(0f, bursts[b].time - offset);
                emission.SetBursts(bursts);
            }
        }
    }

    private static float FirstBurstTime(ParticleSystem.EmissionModule emission)
    {
        int count = emission.burstCount;
        if (count == 0) return 0f;
        var bursts = new ParticleSystem.Burst[count];
        emission.GetBursts(bursts);
        float first = float.MaxValue;
        for (int i = 0; i < bursts.Length; i++) first = Mathf.Min(first, bursts[i].time);
        return first;
    }

    private static ParticleSystem[] CollectDirectSlashes(GameObject attackVfx)
    {
        var slashes = new List<ParticleSystem>(4);
        foreach (Transform child in attackVfx.transform)
        {
            ParticleSystem system = child.GetComponent<ParticleSystem>();
            if (system != null) slashes.Add(system);
        }
        return slashes.ToArray();
    }

    private static Transform EnsureChild(Transform parent, string name)
    {
        Transform existing = parent.Find(name);
        if (existing != null) return existing;
        var host = new GameObject(name);
        Transform created = host.transform;
        created.SetParent(parent, false);
        created.localPosition = Vector3.zero;
        return created;
    }

    // 摩擦火花发射器：预制体里预放了同名占位节点（只有 Transform），这里补粒子组件并配置；
    // 节点也不存在时才新建。配置统一走 FrictionSparks 的公开配置入口，不会漂成两套。
    private static ParticleSystem EnsureFrictionEmitter(Transform parent, string name, bool wall, RuinsFireConfig config)
    {
        Transform existing = parent.Find(name);
        if (existing == null) return FrictionSparks.CreateEmitter(parent, name, wall, config);
        ParticleSystem system = existing.GetComponent<ParticleSystem>();
        if (system == null) system = existing.gameObject.AddComponent<ParticleSystem>();
        FrictionSparks.ConfigureSparkEmitter(system, wall, config);
        return system;
    }

    private static ParticleSystem EnsureBurstEmitter(Transform parent, string name, bool coneAlongForward, Material material)
    {
        Transform existing = parent.Find(name);
        ParticleSystem system = existing != null ? existing.GetComponent<ParticleSystem>() : null;
        if (system == null)
        {
            if (existing != null) Object.DestroyImmediate(existing.gameObject);
            var host = new GameObject(name);
            host.transform.SetParent(parent, false);
            host.transform.localPosition = Vector3.zero;
            system = host.AddComponent<ParticleSystem>();
        }
        ConfigureBurst(system, coneAlongForward, material);
        return system;
    }

    // 一簇一次性火花：撞击点爆开、下坠、很快熄灭。位置与朝向在运行时按命中点/墙面现摆。
    private static void ConfigureBurst(ParticleSystem system, bool coneAlongForward, Material material)
    {
        ParticleSystem.MainModule main = system.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = 0.25f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, 0.30f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(coneAlongForward ? 3f : 2.5f, coneAlongForward ? 7f : 6f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.07f);
        main.startColor = Color.white;
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.gravityModifier = 1.6f;                   // 火花蹭出去后有明显的下坠弧线
        main.simulationSpace = ParticleSystemSimulationSpace.World;  // 粒子留在撞击点，不跟着玩家跑
        main.maxParticles = 64;
        main.stopAction = ParticleSystemStopAction.None;

        ParticleSystem.EmissionModule emission = system.emission;
        emission.rateOverTime = 0f;
        emission.rateOverDistance = 0f;
        // 播放当帧一次喷一簇（最小/最大 12–18 颗）
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 12, 18, 1, 0f) });

        ParticleSystem.ShapeModule shape = system.shape;
        shape.enabled = true;
        if (coneAlongForward)
        {
            // 撞墙：锥体沿本地 +Z 喷（运行时把 +Z 转向墙面外侧）
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 32f;
            shape.radius = 0.03f;
        }
        else
        {
            // 受击：球形四散，看不出方向也不违和
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.06f;
        }
        shape.rotation = Vector3.zero;

        ParticleSystem.ColorOverLifetimeModule color = system.colorOverLifetime;
        color.enabled = true;
        var gradient = new Gradient();
        // RGB 常保亮度、alpha 负责熄灭（预乘混合口径，与 CombatSparks 同一套）
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(1f, 0.98f, 0.92f), 0f),
                new GradientColorKey(new Color(1f, 0.66f, 0.26f), 0.45f),
                new GradientColorKey(new Color(0.85f, 0.28f, 0.05f), 0.85f),
            },
            new[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(0.95f, 0.4f),
                new GradientAlphaKey(0.4f, 0.8f),
                new GradientAlphaKey(0f, 1f),
            });
        color.color = new ParticleSystem.MinMaxGradient(gradient);

        ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0.15f));

        ParticleSystemRenderer renderer = system.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Stretch;   // 拉成细长火花，比圆点更像"蹭"出来的
        renderer.velocityScale = 0.045f;
        renderer.lengthScale = 2.2f;
        renderer.alignment = ParticleSystemRenderSpace.View;
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    private static void SetReference(SerializedObject serialized, string propertyName, Object value)
    {
        SerializedProperty property = serialized.FindProperty(propertyName);
        if (property == null)
        {
            Debug.LogWarning("[PlayerVfxSetup] 找不到字段 " + propertyName);
            return;
        }
        property.objectReferenceValue = value;
    }

    private static void WireArray(SerializedObject serialized, string propertyName, ParticleSystem system)
        => WireArray(serialized, propertyName, new[] { system });

    private static void WireArray(SerializedObject serialized, string propertyName, ParticleSystem[] systems)
    {
        SerializedProperty array = serialized.FindProperty(propertyName);
        if (array == null)
        {
            Debug.LogWarning("[PlayerVfxSetup] 找不到数组字段 " + propertyName);
            return;
        }
        array.arraySize = systems.Length;
        for (int i = 0; i < systems.Length; i++)
            array.GetArrayElementAtIndex(i).objectReferenceValue = systems[i];
    }
}
