using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// 战斗系统装配工具：一键打通 D3–D5 首个战斗闭环的编辑器侧（设计 §七 写作顺序的配套）
//   · 创建示例战斗资产：Settings/Combat/ 下生成首版招式 SO + 取消表（数值全占位，内容待哈士奇核）
//   · 装配战斗组件到场景：给 TestScene 的角色挂全套战斗组件并接线，场景里补 DamageResolver；
//     角色还是裸对象时顺带提取为预制体（后续装配/刷怪复用，旧「提取 Player 为预制体」菜单已合并入此）
// seed-only 纪律（头注释承诺的兑现）：资产与组件只在首次创建时写入工具模板值，已存在的一律不碰——
//   手调数值不覆写；确要重置走「重置示例战斗资产（覆盖）」菜单（弹窗确认）。
//   唯一例外是引用接线（combat.NormalAttack = ... 一类）：把引用指对是工具的装配职责，每次执行幂等确保，数值不是
public static class CombatAssetBootstrap
{
    private const string CombatFolder = "Assets/Settings/Combat";
    private const string ScenePath = "Assets/Scenes/TestScene.unity";
    private const string PlayerPrefabFolder = "Assets/Prefabs/Player";
    private const string PlayerPrefabPath = PlayerPrefabFolder + "/Player.prefab";

    [MenuItem("超高速行者/战斗/创建示例战斗资产")]
    public static void CreateCombatAssets() => BuildCombatAssets(force: false);

    [MenuItem("超高速行者/战斗/重置示例战斗资产（覆盖）")]
    public static void ResetCombatAssets()
    {
        // 覆盖会吃掉手调值，必须显式确认；取消则什么都不动
        if (!EditorUtility.DisplayDialog("重置示例战斗资产",
                "将把 Settings/Combat 下示例资产与取消表重置为工具模板值，手调数值会被覆盖。继续？", "重置", "取消"))
        {
            return;
        }
        BuildCombatAssets(force: true);
    }

    private static void BuildCombatAssets(bool force)
    {
        EnsureFolder("Assets/Settings", "Combat");

        AttackDefinition normal = CreateAttack("NormalAttack", "常态普攻", def =>
        {
            def.DesignNote = "策划案 §5：1A 斜挥、小位移。后摇可被闪避/非原地跳取消（归 Motor 层），不可普攻自取消";
            SetPhase(def, 0, p =>
            {
                p.StartupSec = 0.12f; p.ActiveSec = 0.08f; p.RecoverySec = 0.22f;
                p.BaseDamage = 8f; p.Knockback = 2f; p.HitStopSec = 0.03f;
            });
        }, force);

        AttackDefinition fast = CreateAttack("FastAttack", "高速普攻", def =>
        {
            def.MinSpeedRatio = 0.8f;
            def.ClearMomentumOnLandIfUnderived = true;
            def.DesignNote = "策划案 §5：斜下挥、大位移、伤害随起手速度加成；落地未派生清动量；数值占位";
            SetPhase(def, 0, p =>
            {
                p.StartupSec = 0.10f; p.ActiveSec = 0.12f; p.RecoverySec = 0.30f;
                p.BaseDamage = 12f; p.SpeedDamageScale = 0.5f; p.Knockback = 4f; p.HitStopSec = 0.05f;
                p.Sweep = true;                       // 扫掠：高速不漏目标
                p.Motion = MotionIntentKind.Advance;  // 挥砍前移
                p.MotionSpeed = 7f;
            });
        }, force);

        AttackDefinition flash = CreateAttack("FlashSlash", "闪斩", def =>
        {
            def.DesignNote = "策划案 §5：parry/断肢后派生（窗口在 PlayerCombat.ParryDeriveWindowSec），伤害约为普攻 3 倍；可被普攻取消形成连续 parry 链";
            SetPhase(def, 0, p =>
            {
                p.StartupSec = 0.08f; p.ActiveSec = 0.10f; p.RecoverySec = 0.30f;
                p.BaseDamage = 24f; p.Knockback = 5f; p.HitStopSec = 0.08f;
            });
        }, force);

        AttackDefinition rashomon = CreateAttack("Rashomon", "连斩", def =>
        {
            def.EnergyCost = 100f;
            def.ClearSpeedOnStart = true;
            def.DesignNote = "策划案 §6（P2 项）：数刀后接大威力斩，滞空几乎无水平位移；无 parry 判定；收尾大斩后不可取消（收尾段恢复窗不开窗口即成立）";
            def.Phases = new AttackPhase[]
            {
                HoverPhase(0.08f, 0.06f, 0.06f, 6f),
                HoverPhase(0.06f, 0.06f, 0.06f, 6f),
                HoverPhase(0.06f, 0.06f, 0.06f, 6f),
                FinishPhase(),   // 收尾大斩：长恢复、不可取消
            };
        }, force);

        AttackDefinition push = CreateAttack("PushSlash", "推斩", def =>
        {
            def.EnergyCost = 100f;
            def.DesignNote = "策划案 §6（P1 项）：数道击退斩击、可 parry、与普攻互取消；耗能公式 max(0,150-20v) 待接速度变量，先占位";
            def.Phases = new AttackPhase[]
            {
                PushPhase(0.10f, 0.08f, 0.10f, 7f),
                PushPhase(0.08f, 0.08f, 0.20f, 9f),
            };
        }, force);

        AttackDefinition dash1 = CreateAttack("DashStage1", "冲刺一段", def =>
        {
            def.EnergyCost = 30f;
            def.DesignNote = "林晓风稿：位移 3 米起、伤害 1.5×；霸体待接（Health.SuperArmor 由状态机驱动，后续）；数值占位";
            SetPhase(def, 0, p => ConfigureDash(p, 0.10f, 0.18f, 12f, 15f));
        }, force);
        AttackDefinition dash2 = CreateAttack("DashStage2", "冲刺二段", def =>
        {
            def.EnergyCost = 50f;
            def.DesignNote = "林晓风稿：伤害 1.5×；数值占位";
            SetPhase(def, 0, p => ConfigureDash(p, 0.08f, 0.18f, 14f, 22f));
        }, force);
        AttackDefinition dash3 = CreateAttack("DashStage3", "冲刺三段", def =>
        {
            def.EnergyCost = 70f;
            def.DesignNote = "林晓风稿：伤害 2.5×（AOE 4× 为 P2）；三段后强制收招 0.5s = 长恢复窗且无取消规则；数值占位";
            SetPhase(def, 0, p => ConfigureDash(p, 0.10f, 0.22f, 16f, 35f, recoverySec: 0.50f));
        }, force);

        AttackCancelTable table = CreateCancelTable(force, normal, fast, flash, rashomon, push, dash1, dash2, dash3);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[CombatAssetBootstrap] 战斗资产已就绪（force={force}）：8 个招式 + 取消表（{table.Rules.Length} 条规则）→ {CombatFolder}");
    }

    [MenuItem("超高速行者/战斗/装配战斗组件到场景")]
    public static void WireCombatToScene()
    {
        EditorGuard.RunWhenEditing(WireCombatToSceneInner, "CombatAssetBootstrap");
    }

    private static void WireCombatToSceneInner()
    {
        // OpenScene(Single) 会静默丢弃当前场景未保存改动——先征求处理意愿，取消即中止
        if (!EditorGuard.ConfirmSaveModifiedScenes()) return;

        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        // 角色实例按 PlayerMotor 组件定位：prefab 根名随版本变过（"Player" → 随文件名的 "character"），不依赖名字
        GameObject player = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            PlayerMotor motor = root.GetComponentInChildren<PlayerMotor>();
            if (motor != null)
            {
                player = motor.gameObject;
                break;
            }
        }
        if (player == null)
        {
            Debug.LogError("[CombatAssetBootstrap] 场景缺少角色（未找到 PlayerMotor），装配中止");
            return;
        }

        // 资产引用（先跑「创建示例战斗资产」）
        AttackDefinition Load(string name)
        {
            AttackDefinition def = AssetDatabase.LoadAssetAtPath<AttackDefinition>($"{CombatFolder}/{name}.asset");
            if (def == null) Debug.LogError($"[CombatAssetBootstrap] 缺少 {name}.asset——先执行「创建示例战斗资产」");
            return def;
        }
        AttackCancelTable table = AssetDatabase.LoadAssetAtPath<AttackCancelTable>($"{CombatFolder}/CancelTable.asset");
        if (table == null)
        {
            Debug.LogError("[CombatAssetBootstrap] 缺少 CancelTable.asset——先执行「创建示例战斗资产」");
            return;
        }

        // 1) 输入采样 + 血量 + 受击框（自备触发碰撞体，与移动胶囊分离）
        if (player.GetComponent<InputSampler>() == null) player.AddComponent<InputSampler>();

        bool healthCreated = false;
        HealthComponent health = player.GetComponent<HealthComponent>();
        if (health == null)
        {
            // seed-only：数值默认只随组件新建赋一次，已存在组件上的手调值不覆写
            healthCreated = true;
            health = player.AddComponent<HealthComponent>();
            health.Layer = TimeLayer.Player;
            health.MaxHealth = 100f;
        }

        // 受击框按整个角色层级找：兼容根布局（旧场景现状）与子物体布局（下方迁移后）两种形态
        Hurtbox hurtbox = player.GetComponentInChildren<Hurtbox>();
        if (hurtbox == null)
        {
            // 先按旧布局挂根（与 CharacterController 同物体）；下方迁移段会把根布局搬去子物体
            hurtbox = player.AddComponent<Hurtbox>();
        }
        // 受击形状默认值只在该物体的碰撞体新建时赋；已有形状（可能手调过）不碰
        BoxCollider hurtCollider = hurtbox.GetComponent<BoxCollider>();
        if (hurtCollider == null)
        {
            hurtCollider = hurtbox.gameObject.AddComponent<BoxCollider>();
            hurtCollider.center = new Vector3(0f, 0.95f, 0f);
            hurtCollider.size = new Vector3(0.8f, 1.8f, 0.8f);
            hurtCollider.isTrigger = true;
        }
        hurtbox.Health = health;

        // 2) 两个判定框（伤害/parry 各一，无碰撞体——形状由招式段数据驱动）
        Hitbox damageBox = EnsureHitbox(player, HitboxKind.Damage);
        Hitbox parryBox = EnsureHitbox(player, HitboxKind.Parry);

        // 3) M-3 层迁移：根上的受击框 → 子物体"Hurtbox"（层隔离，伤害查询掩码才能只查受击层）；
        //    根 GO 设层会连移动胶囊一起改层，所以受击形状要有自己的子物体。幂等——子物体已存在则跳过
        Transform hurtChild = player.transform.Find("Hurtbox");
        if (hurtChild == null && player.GetComponent<Hurtbox>() != null)
        {
            Hurtbox old = player.GetComponent<Hurtbox>();
            BoxCollider oldCol = player.GetComponent<BoxCollider>();
            GameObject child = new GameObject("Hurtbox");
            child.transform.SetParent(player.transform, false);
            BoxCollider col = child.AddComponent<BoxCollider>();
            if (oldCol != null) { col.center = oldCol.center; col.size = oldCol.size; }
            col.isTrigger = true;
            Hurtbox hb = child.AddComponent<Hurtbox>();
            hb.Health = old.Health; hb.ImmuneTypes = old.ImmuneTypes;
            if (oldCol != null) UnityEngine.Object.DestroyImmediate(oldCol);
            UnityEngine.Object.DestroyImmediate(old);
            hurtChild = child.transform;
            Debug.Log("[CombatAssetBootstrap] 受击框已迁移至子物体 Hurtbox（层隔离）");
        }
        int hurtLayer = EnsureUserLayer("Hurtbox");
        if (hurtLayer >= 0 && hurtChild != null)
        {
            hurtChild.gameObject.layer = hurtLayer;
            // 查询掩码 seed：只在仍为默认全部时收紧，不覆写手改
            if (damageBox != null && damageBox.QueryMask.value == ~0) damageBox.QueryMask = 1 << hurtLayer;
        }
        // 迁移会销毁旧根组件，统一改取迁移后的实际受击框继续用
        hurtbox = player.GetComponentInChildren<Hurtbox>();

        // 4) 状态机 + 资产接线（引用接线每次幂等确保；数值字段不在这里碰）
        PlayerCombat combat = player.GetComponent<PlayerCombat>();
        if (combat == null) combat = player.AddComponent<PlayerCombat>();
        combat.NormalAttack = Load("NormalAttack");
        combat.FastAttack = Load("FastAttack");
        combat.FlashSlash = Load("FlashSlash");
        combat.Rashomon = Load("Rashomon");
        combat.PushSlash = Load("PushSlash");
        combat.DashStage1 = Load("DashStage1");
        combat.DashStage2 = Load("DashStage2");
        combat.DashStage3 = Load("DashStage3");
        combat.CancelTable = table;
        combat.DamageHitbox = damageBox;
        combat.ParryHitbox = parryBox;

        // 5) 场景级结算器（每 tick 命中结算）
        DamageResolver resolver = FindInScene(scene, "DamageResolver")?.GetComponent<DamageResolver>();
        if (resolver == null)
        {
            GameObject resolverGo = new GameObject("DamageResolver");
            SceneManager.MoveGameObjectToScene(resolverGo, scene);
            resolver = resolverGo.AddComponent<DamageResolver>();
        }

        // SetDirty 只落在确实新建/改动的对象上：combat 与 hurtbox 有引用接线必动；health 仅新建时动
        EditorUtility.SetDirty(combat);
        EditorUtility.SetDirty(hurtbox);
        if (healthCreated) EditorUtility.SetDirty(health);

        // 角色是裸对象则提取为预制体（幂等：已是预制体实例则跳过）——合并自旧「提取 Player 为预制体」菜单（旧菜单按名字找 Player，根名已变必失效）
        if (!PrefabUtility.IsPartOfPrefabInstance(player))
        {
            EnsureFolder("Assets/Prefabs", "Player");
            PrefabUtility.SaveAsPrefabAssetAndConnect(player, PlayerPrefabPath, InteractionMode.AutomatedAction);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[CombatAssetBootstrap] 战斗组件装配完成：InputSampler/Health/Hurtbox/Hitbox×2/PlayerCombat/DamageResolver");
    }

    // —— 资产构造辅助（seed-only：只在新建或 force 时写入模板值）——

    private static AttackDefinition CreateAttack(string fileName, string displayName,
        System.Action<AttackDefinition> configure, bool force = false)
    {
        string path = $"{CombatFolder}/{fileName}.asset";
        AttackDefinition def = AssetDatabase.LoadAssetAtPath<AttackDefinition>(path);
        if (def != null && !force)
        {
            // seed-only：资产已存在直接返回、不执行 configure——手调数值不覆写，重置走「重置示例战斗资产」菜单
            return def;
        }
        if (def == null)
        {
            def = ScriptableObject.CreateInstance<AttackDefinition>();
            AssetDatabase.CreateAsset(def, path);
        }
        def.DisplayName = displayName;
        configure?.Invoke(def);
        EditorUtility.SetDirty(def);
        return def;
    }

    private static void SetPhase(AttackDefinition def, int index, System.Action<AttackPhase> configure)
    {
        if (def.Phases == null || def.Phases.Length <= index)
        {
            var list = new List<AttackPhase>(def.Phases ?? new AttackPhase[0]);
            while (list.Count <= index) list.Add(new AttackPhase());
            def.Phases = list.ToArray();
        }
        configure?.Invoke(def.Phases[index]);
    }

    private static AttackPhase HoverPhase(float startup, float active, float recovery, float damage) =>
        new AttackPhase
        {
            StartupSec = startup, ActiveSec = active, RecoverySec = recovery,
            BaseDamage = damage, Knockback = 1f, HitStopSec = 0.03f,
            Motion = MotionIntentKind.Hover,  // 滞空：悬挂重力
        };

    private static AttackPhase FinishPhase() =>
        new AttackPhase
        {
            StartupSec = 0.10f, ActiveSec = 0.12f, RecoverySec = 0.50f, // 长恢复 = 收尾不可取消
            BaseDamage = 20f, Knockback = 6f, HitStopSec = 0.08f,
            Motion = MotionIntentKind.Hover,
        };

    private static AttackPhase PushPhase(float startup, float active, float recovery, float damage) =>
        new AttackPhase
        {
            StartupSec = startup, ActiveSec = active, RecoverySec = recovery,
            BaseDamage = damage, Knockback = 8f, HitStopSec = 0.04f,
            HasParry = true,
            ParryProfile = new HitboxProfile
            {
                Shape = HitboxShape.Capsule,
                CapsuleRadius = 0.55f,   // parry 框比伤害框大（给得宽松）
                CapsuleHeight = 1.6f,
                LocalOffset = new Vector3(0f, 1.0f, 0.6f),
            },
        };

    private static void ConfigureDash(AttackPhase p, float startup, float active, float motionSpeed,
        float damage, float recoverySec = 0.25f)
    {
        p.StartupSec = startup; p.ActiveSec = active; p.RecoverySec = recoverySec;
        p.BaseDamage = damage; p.Knockback = 3f; p.HitStopSec = 0.05f;
        p.Sweep = true;                        // 起终点连线判定免逐帧（林晓风稿）
        p.Motion = MotionIntentKind.Dash;
        p.MotionSpeed = motionSpeed;
    }

    private static AttackCancelTable CreateCancelTable(bool force, params AttackDefinition[] defs)
    {
        string path = $"{CombatFolder}/CancelTable.asset";
        AttackCancelTable table = AssetDatabase.LoadAssetAtPath<AttackCancelTable>(path);
        if (table == null)
        {
            table = ScriptableObject.CreateInstance<AttackCancelTable>();
            AssetDatabase.CreateAsset(table, path);
        }
        else if (!force && table.Rules != null && table.Rules.Length > 0)
        {
            // seed-only：表已建且已有规则即视为播种完成，不重建——手调规则不覆写
            return table;
        }

        AttackDefinition ByName(string name) => System.Array.Find(defs, d => d.name == name);

        var rules = new List<CancelRule>
        {
            // 高速普攻 Recovery → 连斩/推斩（折返归 Motor 层）；不可普攻自取消（无规则即不成立）
            Rule(ByName("FastAttack"), ByName("Rashomon"), InputIntent.Rashomon),
            Rule(ByName("FastAttack"), ByName("PushSlash"), InputIntent.PushSlash),
            // 闪斩 Recovery → 常态/高速普攻（连续 parry 链）
            Rule(ByName("FlashSlash"), ByName("NormalAttack"), InputIntent.Attack),
            Rule(ByName("FlashSlash"), ByName("FastAttack"), InputIntent.Attack, minSpeedRatio: 0.8f),
            // 连斩前段（段 0-2）→ 普攻；收尾段（3）无规则 = 不可取消
            Rule(ByName("Rashomon"), ByName("NormalAttack"), InputIntent.Attack, sourceSegment: 0),
            Rule(ByName("Rashomon"), ByName("NormalAttack"), InputIntent.Attack, sourceSegment: 1),
            Rule(ByName("Rashomon"), ByName("NormalAttack"), InputIntent.Attack, sourceSegment: 2),
            // 推斩 Recovery → 普攻（互取消）
            Rule(ByName("PushSlash"), ByName("NormalAttack"), InputIntent.Attack),
            Rule(ByName("PushSlash"), ByName("FastAttack"), InputIntent.Attack, minSpeedRatio: 0.8f),
            // 冲刺 Recovery → 下一段 / 普攻（闪避归 Motor 层）；第三段后无规则 = 强制收招
            Rule(ByName("DashStage1"), ByName("DashStage2"), InputIntent.Dash),
            Rule(ByName("DashStage1"), ByName("NormalAttack"), InputIntent.Attack),
            Rule(ByName("DashStage2"), ByName("DashStage3"), InputIntent.Dash),
            Rule(ByName("DashStage2"), ByName("NormalAttack"), InputIntent.Attack),
        };
        rules.RemoveAll(r => r.SourceAttack == null || r.TargetAttack == null); // 资产缺失时跳过该行

        table.Rules = rules.ToArray();
        table.name = "CancelTable";
        EditorUtility.SetDirty(table);
        return table;
    }

    private static CancelRule Rule(AttackDefinition source, AttackDefinition target, InputIntent intent,
        float minSpeedRatio = 0f, int sourceSegment = -1) =>
        new CancelRule
        {
            SourceAttack = source,
            TargetAttack = target,
            RequiredIntent = intent,
            MinSpeedRatio = minSpeedRatio,
            SourceSegment = sourceSegment,
            SourcePhase = AttackPhaseKind.Recovery,
        };

    private static Hitbox EnsureHitbox(GameObject player, HitboxKind kind)
    {
        foreach (Hitbox existing in player.GetComponents<Hitbox>())
        {
            if (existing.Kind == kind) return existing;
        }
        Hitbox hitbox = player.AddComponent<Hitbox>();
        hitbox.Kind = kind;
        return hitbox;
    }

    // M-3：确保 TagManager 注册了指定 User Layer 并返回层号（已有同名层直接返回）；层槽满返回 -1（调用方跳过设层，不致命）
    private static int EnsureUserLayer(string layerName)
    {
        // SerializedObject 写 TagManager 的 User Layer 槽（第 8 槽起）；直接改文本易错，走序列化接口
        Object[] tagManagerAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (tagManagerAssets == null || tagManagerAssets.Length == 0)
        {
            Debug.LogError("[CombatAssetBootstrap] 未能加载 ProjectSettings/TagManager.asset，无法注册层");
            return -1;
        }
        var tagManager = new SerializedObject(tagManagerAssets[0]);
        SerializedProperty layers = tagManager.FindProperty("layers");
        int firstFree = -1;
        for (int i = 8; i < layers.arraySize; i++)
        {
            SerializedProperty slot = layers.GetArrayElementAtIndex(i);
            if (slot.stringValue == layerName) return i;
            if (string.IsNullOrEmpty(slot.stringValue) && firstFree < 0) firstFree = i;
        }
        if (firstFree < 0)
        {
            Debug.LogError($"[CombatAssetBootstrap] TagManager User Layer 已满（8–31），无法注册层 {layerName}");
            return -1;
        }
        layers.GetArrayElementAtIndex(firstFree).stringValue = layerName;
        tagManager.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        return firstFree;
    }

    private static GameObject FindInScene(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == name) return root;
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == name) return child.gameObject;
            }
        }
        return null;
    }

    private static void EnsureFolder(string parent, string name)
    {
        if (!AssetDatabase.IsValidFolder($"{parent}/{name}"))
        {
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
