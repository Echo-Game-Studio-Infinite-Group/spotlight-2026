using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// 给玩家和敌人装配「刀光拖尾」：在剑骨 / 右手骨下建 AttackTrail 发射器，接进 PlayerVFXManager、EnemyBase。
//   · 玩家：剑骨优先 joint10 → joint9 → CC_Base_R_Hand（美工模型里 joint9/joint10 就是剑的两根骨）
//   · EnemyTest：arm right wrist     · EnemyHeavy：hand_R → gun_R
//   · 顺手删掉玩家原来的 Vfx/Attack1、Vfx/Attack2（以玩家为原点播的网格刀光，已被拖尾取代）
//
// 为什么是编辑器脚本而不是手改 YAML：Unity 会把内存里的旧副本反写回文件，手改必被覆盖（已踩过）。
// 发射器参数走 AttackTrailVfx.Configure 这同一个入口，"预制体里的"与"运行时补出来的"不会漂成两套。
// 幂等：节点/组件已存在就只重配参数与接线；域重载后做一次便宜的完整性检查，缺才动资产。
[InitializeOnLoad]
public static class AttackTrailSetup
{
    private const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
    private const string EnemyTestPrefabPath = "Assets/Prefabs/EnemyTest.prefab";
    private const string EnemyHeavyPrefabPath = "Assets/Prefabs/EnemyHeavy.prefab";

    private const string MaterialPath = "Assets/Resources/CombatVfx/AttackTrail.mat";

    // 刀光贴图：工程现成的 Slash（拖动时沿拖尾拉伸，形成刃口形状）
    private const string SlashTexturePath = "Assets/Art/VFX/Textures/Slash.png";

    // 剑骨 / 右手骨候选（按优先级）。名字来自各模型的实际骨架。
    private static readonly string[] PlayerAnchors = { "joint10", "joint9", "CC_Base_R_Hand" };
    private static readonly string[] EnemyTestAnchors = { "arm right wrist", "arm right forearm" };
    private static readonly string[] EnemyHeavyAnchors = { "hand_R", "gun_R" };

    // 旧版攻击特效节点名（以玩家为原点播网格刀光的那两个）
    private static readonly string[] StaleAttackNodes = { "Attack1", "Attack2" };

    // 特效宿主节点名：与 Player.prefab 的 Vfx 层级约定一致，敌人也建同名节点放特效。
    private const string VfxNodeName = "Vfx";

    static AttackTrailSetup()
    {
        // 域重载刚结束时 AssetDatabase 未必就绪，推迟一帧再动资产。
        EditorApplication.delayCall += AutoSetup;
    }

    private static void AutoSetup()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (IsUpToDate())
        {
            Debug.Log("[AttackTrailSetup] 刀光拖尾已是最新，跳过");
            return;
        }
        Setup();
    }

    [MenuItem("超高速行者/战斗特效/装配刀光拖尾（玩家 + EnemyTest + EnemyHeavy）")]
    public static void SetupFromMenu() => Setup();

    private static bool IsUpToDate()
    {
        if (AssetDatabase.LoadAssetAtPath<Material>(MaterialPath) == null) return false;
        if (!PrefabWired(PlayerPrefabPath, out var player)) return false;
        if (!player) return false;
        // 玩家身上还留着旧的两个网格刀光节点，说明没跑过新装配。
        var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
        Transform vfx = playerPrefab != null ? playerPrefab.transform.Find("Vfx") : null;
        if (vfx != null)
            foreach (string stale in StaleAttackNodes)
                if (vfx.Find(stale) != null) return false;
        return PrefabWired(EnemyTestPrefabPath, out _) && PrefabWired(EnemyHeavyPrefabPath, out _);
    }

    // 预制体里有没有刀光组件 + 主人组件上的引用接没接上（只读资产，不 LoadPrefabContents）。
    private static bool PrefabWired(string path, out bool isPlayer)
    {
        isPlayer = false;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) return false;
        AttackTrailVfx trail = prefab.GetComponentInChildren<AttackTrailVfx>(true);
        if (trail == null) return false;
        // 粒子参数也一起校验：改了 Configure 之后，旧 prefab 里存的还是老参数，
        // 只查"组件在不在"会让自动装配一直跳过、坏参数永远留在预制体里。
        if (!TrailLooksRight(trail)) return false;

        PlayerVFXManager manager = prefab.GetComponent<PlayerVFXManager>();
        if (manager != null)
        {
            isPlayer = true;
            var serialized = new SerializedObject(manager);
            SerializedProperty property = serialized.FindProperty("_attackTrail");
            return property != null && property.objectReferenceValue != null;
        }

        EnemyVFXManager enemyManager = prefab.GetComponent<EnemyVFXManager>();
        if (enemyManager == null) return false;
        var enemySerialized = new SerializedObject(enemyManager);
        SerializedProperty enemyProperty = enemySerialized.FindProperty("_attackTrail");
        return enemyProperty != null && enemyProperty.objectReferenceValue != null;
    }

    // 刀光粒子系统的关键参数是否达标（写错一项屏幕上就什么都看不到）。
    private static bool TrailLooksRight(AttackTrailVfx trail)
    {
        ParticleSystem system = trail != null ? trail.System : null;
        if (system == null) return false;
        ParticleSystem.MainModule main = system.main;
        ParticleSystem.TrailModule trails = system.trails;
        var renderer = system.GetComponent<ParticleSystemRenderer>();
        if (renderer == null) return false;
        if (main.startSize.constant <= 0f) return false;           // 0 = 粒子被整条跳过，拖尾一起没
        if (!trails.enabled || !trails.worldSpace) return false;    // 局部空间 = 弧线缩成一个点
        // 宽度也校验：改了 TrailWidth 之后预制体里存的是旧值，只查开关会导致改参数不生效。
        if (Mathf.Abs(trails.widthOverTrail.curveMultiplier - AttackTrailVfx.TrailWidth) > 0.001f) return false;
        if (renderer.renderMode == ParticleSystemRenderMode.None) return false;  // None = 整个渲染器不画
        if (renderer.sharedMaterial == null) return false;          // 没材质 = 什么都看不到
        if (trail.Follow == null) return false;                     // 没跟随目标 = 停在原地不动
        if (trail.transform.parent == null || trail.transform.parent.name != "Vfx") return false;  // 必须挂在 Vfx 下
        return true;
    }

    private static void Setup()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[AttackTrailSetup] 先退出 Play 模式再装配预制体");
            return;
        }
        Material material = EnsureMaterial();
        if (material == null)
        {
            Debug.LogWarning("[AttackTrailSetup] 生成刀光材质失败，跳过装配（宁可不出图，也不要紫黑一片）");
            return;
        }

        SetupPlayer(material);
        SetupEnemy(EnemyTestPrefabPath, "EnemyTest", EnemyTestAnchors, material);
        SetupEnemy(EnemyHeavyPrefabPath, "EnemyHeavy", EnemyHeavyAnchors, material);
    }

    // 刀光材质：GameJam/SparkAdditive（预乘 alpha + HDR 染色，火花同款，无光照）。
    // 参数集中在 AttackTrailVfx.CreateMaterial；装配工具负责落盘并补上 Slash 贴图，
    // 运行时代码在资产缺失时用同一套参数现场搓一个，两边不会漂。
    private static Material EnsureMaterial()
    {
        Shader expected = Shader.Find(AttackTrailVfx.MaterialShaderName);
        if (expected == null)
        {
            Debug.LogWarning("[AttackTrailSetup] 找不到 shader " + AttackTrailVfx.MaterialShaderName + "（工程不是 URP？）");
            return null;
        }

        Material existing = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (existing == null)
        {
            Material created = AttackTrailVfx.CreateMaterial();
            if (created == null) return null;
            var slashNew = AssetDatabase.LoadAssetAtPath<Texture2D>(SlashTexturePath);
            if (slashNew != null) created.SetTexture("_MainTex", slashNew);
            AssetDatabase.CreateAsset(created, MaterialPath);
            AssetDatabase.SaveAssets();
            Debug.Log("[AttackTrailSetup] 已生成刀光材质 " + MaterialPath + "（" + AttackTrailVfx.MaterialShaderName + "）");
            return AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        }

        // 已存在：shader 不对（例如上一版是 URP Lit）就换回目标 shader 并按基线重写参数，
        // 否则"紫黑"这类问题会一直挂着。
        if (existing.shader != expected) existing.shader = expected;
        Material baseline = AttackTrailVfx.CreateMaterial();
        if (baseline != null)
        {
            existing.CopyPropertiesFromMaterial(baseline);
            Object.DestroyImmediate(baseline);
        }
        // 基线材质没有贴图，复制会把它清空，这里补回 Slash
        var slash = AssetDatabase.LoadAssetAtPath<Texture2D>(SlashTexturePath);
        if (slash != null) existing.SetTexture("_MainTex", slash);
        EditorUtility.SetDirty(existing);
        AssetDatabase.SaveAssets();
        return existing;
    }

    // 刀光节点统一放在 <根>/Vfx/AttackTrail 下（与 FootSparks / Hurt 等同一层级约定），
    // 位置与朝向靠 AttackTrailVfx._follow 每帧跟随剑骨 / 右手骨——层级整洁，轨迹照样正确。
    private static AttackTrailVfx EnsureTrailUnderVfx(GameObject root, string[] anchors, string label)
    {
        Transform bone = FindAnchor(root.transform, anchors);
        if (bone == null)
        {
            Debug.LogWarning($"[AttackTrailSetup] {label} 里找不到跟随骨骼（{string.Join(" / ", anchors)}），跳过");
            return null;
        }

        Transform vfxRoot = root.transform.Find(VfxNodeName);
        if (vfxRoot == null)
        {
            var host = new GameObject(VfxNodeName);
            vfxRoot = host.transform;
            vfxRoot.SetParent(root.transform, false);
            vfxRoot.localPosition = Vector3.zero;
            Debug.Log($"[AttackTrailSetup] {label} 补建 {VfxNodeName} 节点（与玩家预制体同一层级约定）");
        }

        // 清掉早期版本挂在骨骼下、或者重复的 AttackTrail 节点
        foreach (Transform candidate in root.GetComponentsInChildren<Transform>(true))
        {
            if (candidate == null || candidate.name != AttackTrailVfx.NodeName) continue;
            if (candidate.parent == vfxRoot) continue;
            Debug.Log($"[AttackTrailSetup] {label} 清掉错位的旧刀光节点 {PathOf(candidate, root.transform)}");
            Object.DestroyImmediate(candidate.gameObject);
        }

        AttackTrailVfx trail = AttackTrailVfx.Ensure(vfxRoot);
        trail.SetFollow(bone);
        EditorUtility.SetDirty(trail);
        Debug.Log($"[AttackTrailSetup] {label} 刀光节点 {VfxNodeName}/{AttackTrailVfx.NodeName}，跟随 {PathOf(bone, root.transform)}");
        return trail;
    }

    private static void SetupPlayer(Material material)
    {
        GameObject root = null;
        try
        {
            root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            if (root == null) { Debug.LogWarning("[AttackTrailSetup] 读不到 " + PlayerPrefabPath); return; }

            AttackTrailVfx trail = EnsureTrailUnderVfx(root, PlayerAnchors, "Player");
            if (trail == null) return;
            ApplyRendererMaterial(trail, material);
            Debug.Log("[AttackTrailSetup]   粒子参数: " + AttackTrailVfx.Describe(trail.System));

            PlayerVFXManager manager = root.GetComponent<PlayerVFXManager>();
            if (manager != null)
            {
                var serialized = new SerializedObject(manager);
                SerializedProperty property = serialized.FindProperty("_attackTrail");
                if (property != null)
                {
                    property.objectReferenceValue = trail;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            else
            {
                Debug.LogWarning("[AttackTrailSetup] Player.prefab 根节点没有 PlayerVFXManager，玩家刀光只建了节点未接线");
            }

            RemoveStaleAttackNodes(root.transform);
            PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
            Debug.Log("[AttackTrailSetup] Player.prefab 已装配 Vfx/AttackTrail 并接线到 PlayerVFXManager");
        }
        catch (System.Exception error)
        {
            Debug.LogWarning("[AttackTrailSetup] 玩家刀光装配失败（预制体可能开在 Prefab 模式里）：" + error.Message);
        }
        finally
        {
            if (root != null) PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static void SetupEnemy(string prefabPath, string label, string[] anchors, Material material)
    {
        GameObject root = null;
        try
        {
            root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null) { Debug.LogWarning("[AttackTrailSetup] 读不到 " + prefabPath); return; }

            AttackTrailVfx trail = EnsureTrailUnderVfx(root, anchors, label);
            if (trail == null) return;
            ApplyRendererMaterial(trail, material);
            Debug.Log("[AttackTrailSetup]   粒子参数: " + AttackTrailVfx.Describe(trail.System));

            // 与玩家侧同构：表现层归 EnemyVFXManager，EnemyBase 只转发判定窗口的开合。
            EnemyVFXManager manager = root.GetComponent<EnemyVFXManager>();
            if (manager == null)
            {
                manager = root.AddComponent<EnemyVFXManager>();
                Debug.Log("[AttackTrailSetup] " + label + " 补上 EnemyVFXManager（与 PlayerVFXManager 同职责）");
            }
            manager.Configure(trail);
            EditorUtility.SetDirty(manager);

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Debug.Log("[AttackTrailSetup] " + label + " 已装配 " + VfxNodeName + "/" + AttackTrailVfx.NodeName
                + " 并接线到 EnemyVFXManager（判定窗口开合时启停）");
        }
        catch (System.Exception error)
        {
            Debug.LogWarning("[AttackTrailSetup] " + label + " 装配失败（预制体可能开在 Prefab 模式里）：" + error.Message);
        }
        finally
        {
            if (root != null) PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // 按优先级找第一个同名节点；同名有多个（左右手）时也不容易选错——这里给的都是带 "R/right" 的名字。
    private static Transform FindAnchor(Transform root, string[] priority)
    {
        foreach (string name in priority)
        {
            Transform found = FindByName(root, name);
            if (found != null) return found;
        }
        return null;
    }

    private static Transform FindByName(Transform root, string name)
    {
        foreach (Transform candidate in root.GetComponentsInChildren<Transform>(true))
            if (candidate.name == name) return candidate;
        return null;
    }

    private static void ApplyRendererMaterial(AttackTrailVfx trail, Material material)
    {
        if (trail == null) return;
        ParticleSystemRenderer renderer = trail.GetComponent<ParticleSystemRenderer>();
        if (renderer == null) return;
        renderer.sharedMaterial = material;
        renderer.enabled = material != null;
        EditorUtility.SetDirty(renderer);
    }

    // 旧的 Vfx/Attack1、Vfx/Attack2 是以玩家为原点播网格刀光的实现，拖尾接手后它们只会变成
    // "没人驱动但还占着两个材质引用"的死对象。
    private static void RemoveStaleAttackNodes(Transform playerRoot)
    {
        Transform vfx = playerRoot.Find("Vfx");
        if (vfx == null) return;
        foreach (string stale in StaleAttackNodes)
        {
            Transform node = vfx.Find(stale);
            if (node == null) continue;
            Debug.Log("[AttackTrailSetup] 清掉旧攻击特效节点 Vfx/" + stale);
            Object.DestroyImmediate(node.gameObject);
        }
    }

    private static string PathOf(Transform target, Transform root)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (Transform current = target; current != null && current != root; current = current.parent)
            parts.Add(current.name);
        parts.Reverse();
        return string.Join("/", parts);
    }
}
