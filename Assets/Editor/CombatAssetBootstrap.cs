using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// 战斗系统装配工具：一键打通 D3–D5 首个战斗闭环的编辑器侧（设计 §七 写作顺序的配套）
//   · 创建示例战斗资产：Settings/Combat/ 下生成首版招式 SO + 取消表（数值全占位，内容待哈士奇核）
//   · 装配战斗组件到场景：给 TestScene 的 Player 挂全套战斗组件并接线，场景里补 DamageResolver
//   · 提取 Player 为预制体：场景裸对象 → Assets/Prefabs/Player/Player.prefab（后续装配/刷怪复用）
// 全部幂等：已有资产/组件复用不覆盖手动调过的开关，与 ProjectBootstrap 同一纪律
public static class CombatAssetBootstrap
{
    private const string CombatFolder = "Assets/Settings/Combat";
    private const string ScenePath = "Assets/Scenes/TestScene.unity";
    private const string PlayerPrefabFolder = "Assets/Prefabs/Player";
    private const string PlayerPrefabPath = PlayerPrefabFolder + "/Player.prefab";

    [MenuItem("超高速行者/战斗/创建示例战斗资产")]
    public static void CreateCombatAssets()
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
        });

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
        });

        AttackDefinition flash = CreateAttack("FlashSlash", "闪斩", def =>
        {
            def.DesignNote = "策划案 §5：parry/断肢后派生（窗口在 PlayerCombat.ParryDeriveWindowSec），伤害约为普攻 3 倍；可被普攻取消形成连续 parry 链";
            SetPhase(def, 0, p =>
            {
                p.StartupSec = 0.08f; p.ActiveSec = 0.10f; p.RecoverySec = 0.30f;
                p.BaseDamage = 24f; p.Knockback = 5f; p.HitStopSec = 0.08f;
            });
        });

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
        });

        AttackDefinition push = CreateAttack("PushSlash", "推斩", def =>
        {
            def.EnergyCost = 100f;
            def.DesignNote = "策划案 §6（P1 项）：数道击退斩击、可 parry、与普攻互取消；耗能公式 max(0,150-20v) 待接速度变量，先占位";
            def.Phases = new AttackPhase[]
            {
                PushPhase(0.10f, 0.08f, 0.10f, 7f),
                PushPhase(0.08f, 0.08f, 0.20f, 9f),
            };
        });

        AttackDefinition dash1 = CreateAttack("DashStage1", "冲刺一段", def =>
        {
            def.EnergyCost = 30f;
            def.DesignNote = "林晓风稿：位移 3 米起、伤害 1.5×；霸体待接（Health.SuperArmor 由状态机驱动，后续）；数值占位";
            SetPhase(def, 0, p => ConfigureDash(p, 0.10f, 0.18f, 12f, 15f));
        });
        AttackDefinition dash2 = CreateAttack("DashStage2", "冲刺二段", def =>
        {
            def.EnergyCost = 50f;
            def.DesignNote = "林晓风稿：伤害 1.5×；数值占位";
            SetPhase(def, 0, p => ConfigureDash(p, 0.08f, 0.18f, 14f, 22f));
        });
        AttackDefinition dash3 = CreateAttack("DashStage3", "冲刺三段", def =>
        {
            def.EnergyCost = 70f;
            def.DesignNote = "林晓风稿：伤害 2.5×（AOE 4× 为 P2）；三段后强制收招 0.5s = 长恢复窗且无取消规则；数值占位";
            SetPhase(def, 0, p => ConfigureDash(p, 0.10f, 0.22f, 16f, 35f, recoverySec: 0.50f));
        });

        AttackCancelTable table = CreateCancelTable(normal, fast, flash, rashomon, push, dash1, dash2, dash3);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[CombatAssetBootstrap] 战斗资产已就绪：8 个招式 + 取消表（{table.Rules.Length} 条规则）→ {CombatFolder}");
    }

    [MenuItem("超高速行者/战斗/装配战斗组件到场景")]
    public static void WireCombatToScene()
    {
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

        HealthComponent health = player.GetComponent<HealthComponent>();
        if (health == null) health = player.AddComponent<HealthComponent>();
        health.Layer = TimeLayer.Player;
        health.MaxHealth = 100f;

        Hurtbox hurtbox = player.GetComponent<Hurtbox>();
        if (hurtbox == null) hurtbox = player.AddComponent<Hurtbox>();
        BoxCollider hurtCollider = player.GetComponent<BoxCollider>();
        if (hurtCollider == null) hurtCollider = player.AddComponent<BoxCollider>();
        hurtCollider.center = new Vector3(0f, 0.95f, 0f);
        hurtCollider.size = new Vector3(0.8f, 1.8f, 0.8f);
        hurtCollider.isTrigger = true;
        hurtbox.Health = health;

        // 2) 两个判定框（伤害/parry 各一，无碰撞体——形状由招式段数据驱动）
        Hitbox damageBox = EnsureHitbox(player, HitboxKind.Damage);
        Hitbox parryBox = EnsureHitbox(player, HitboxKind.Parry);

        // 3) 状态机 + 资产接线
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

        // 4) 场景级结算器（每 tick 命中结算）
        DamageResolver resolver = FindInScene(scene, "DamageResolver")?.GetComponent<DamageResolver>();
        if (resolver == null)
        {
            GameObject resolverGo = new GameObject("DamageResolver");
            SceneManager.MoveGameObjectToScene(resolverGo, scene);
            resolver = resolverGo.AddComponent<DamageResolver>();
        }

        EditorUtility.SetDirty(combat);
        EditorUtility.SetDirty(health);
        EditorUtility.SetDirty(hurtbox);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[CombatAssetBootstrap] 战斗组件装配完成：InputSampler/Health/Hurtbox/Hitbox×2/PlayerCombat/DamageResolver");
    }

    [MenuItem("超高速行者/提取 Player 为预制体")]
    public static void ExtractPlayerPrefab()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        GameObject player = FindInScene(scene, "Player");
        if (player == null)
        {
            Debug.LogError("[CombatAssetBootstrap] 场景缺少 Player");
            return;
        }
        if (PrefabUtility.IsPartOfPrefabInstance(player))
        {
            Debug.Log("[CombatAssetBootstrap] Player 已是预制体实例，跳过");
            return;
        }

        EnsureFolder("Assets/Prefabs", "Player");
        PrefabUtility.SaveAsPrefabAssetAndConnect(player, PlayerPrefabPath, InteractionMode.AutomatedAction);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[CombatAssetBootstrap] Player 已提取为预制体 → {PlayerPrefabPath}");
    }

    // —— 资产构造辅助 ——

    private static AttackDefinition CreateAttack(string fileName, string displayName, System.Action<AttackDefinition> configure)
    {
        string path = $"{CombatFolder}/{fileName}.asset";
        AttackDefinition def = AssetDatabase.LoadAssetAtPath<AttackDefinition>(path);
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

    private static AttackCancelTable CreateCancelTable(params AttackDefinition[] defs)
    {
        string path = $"{CombatFolder}/CancelTable.asset";
        AttackCancelTable table = AssetDatabase.LoadAssetAtPath<AttackCancelTable>(path);
        if (table == null)
        {
            table = ScriptableObject.CreateInstance<AttackCancelTable>();
            AssetDatabase.CreateAsset(table, path);
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
