using UnityEditor;
using UnityEngine;

// 给玩家和敌人装配「刀光拖尾」（动态 Mesh 版）：在 Vfx 下建 AttackTrail 节点（MeshFilter + MeshRenderer
// + AttackTrailVfx），跟随剑骨 / 右手骨采样刀根 / 刀尖轨迹，接进 PlayerVFXManager / EnemyVFXManager。
//   · 玩家：剑骨优先 joint10 → joint9 → CC_Base_R_Hand     · EnemyTest：arm right wrist     · EnemyHeavy：hand_R → gun_R
//   · 顺手清掉节点上可能残留的 ParticleSystem / ParticleSystemRenderer（粒子版旧件）
//   · 顺手删掉玩家原来的 Vfx/Attack1、Vfx/Attack2（以玩家为原点播的网格刀光，已被拖尾取代）
//
// 为什么是编辑器脚本而不是手改 YAML：Unity 会把内存里的旧副本反写回文件，手改必被覆盖（已踩过）。
// 幂等：节点/组件已存在就只重配参数与接线；域重载后做一次便宜的完整性检查，缺才动资产。
[InitializeOnLoad]
public static class AttackTrailSetup
{
    private const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
    private const string EnemyTestPrefabPath = "Assets/Prefabs/EnemyTest.prefab";
    private const string EnemyHeavyPrefabPath = "Assets/Prefabs/EnemyHeavy.prefab";

    private const string MaterialPath = "Assets/Resources/CombatVfx/AttackTrail.mat";

    // 刀光贴图：工程现成的 Slash（u 沿轨迹拉伸，形成刃口形状）
    private const string SlashTexturePath = "Assets/Art/VFX/Textures/Slash.png";

    // 剑骨 / 右手骨候选（按优先级）。名字来自各模型的实际骨架。
    private static readonly string[] PlayerAnchors = { "joint10", "joint9", "CC_Base_R_Hand" };
    private static readonly string[] EnemyTestAnchors = { "arm right wrist", "arm right forearm" };
    private static readonly string[] EnemyHeavyAnchors = { "hand_R", "gun_R" };

    // 刀尖偏移按跟随者体型各配各的（局部坐标，数值来自 AttackTrailDebug 的 BakeMesh/AABB 探测日志）。
    //   玩家：joint10 是剑尖骨，剑身朝局部 +Z（BakeMesh 最远顶点方向，44/67.5≈0.65m 到护手）
    //   EnemyTest：arm right wrist 缩放 0.0241，(0,0,20) ≈ 0.48m 沿腕骨 +Z
    //   EnemyHeavy：hand_R 缩放 50，(0,0,0.04) = 2m 沿手骨 +Z（枪身长度）
    private static readonly Vector3 PlayerTipOffset = new Vector3(0f, 0f, 0.65f);
    private static readonly Vector3 EnemyTestTipOffset = new Vector3(0f, 0f, 20f);
    private static readonly Vector3 EnemyHeavyTipOffset = new Vector3(0f, 0f, 0.04f);

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

    // 出图自证工具（AttackTrailDebug.RunFromCLI）会先调它，保证预制体参数是最新的。
    public static void Setup()
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
        SetupEnemy(EnemyTestPrefabPath, "EnemyTest", EnemyTestAnchors, EnemyTestTipOffset, material);
        SetupEnemy(EnemyHeavyPrefabPath, "EnemyHeavy", EnemyHeavyAnchors, EnemyHeavyTipOffset, material);
    }

    private static bool IsUpToDate()
    {
        if (AssetDatabase.LoadAssetAtPath<Material>(MaterialPath) == null) return false;
        if (!PrefabWired(PlayerPrefabPath)) return false;
        // 玩家身上还留着旧的两个网格刀光节点，说明没跑过新装配。
        var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
        Transform vfx = playerPrefab != null ? playerPrefab.transform.Find("Vfx") : null;
        if (vfx != null)
            foreach (string stale in StaleAttackNodes)
                if (vfx.Find(stale) != null) return false;
        return PrefabWired(EnemyTestPrefabPath) && PrefabWired(EnemyHeavyPrefabPath);
    }

    // 预制体里有没有刀光组件 + 主人组件上的引用接没接上（只读资产，不 LoadPrefabContents）。
    private static bool PrefabWired(string path)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) return false;
        AttackTrailVfx trail = prefab.GetComponentInChildren<AttackTrailVfx>(true);
        if (trail == null) return false;
        // 参数也一起校验：改了配置之后旧 prefab 里存的还是老值，只查"组件在不在"会让坏参数一直留在预制体里。
        if (!TrailLooksRight(trail)) return false;

        PlayerVFXManager playerManager = prefab.GetComponent<PlayerVFXManager>();
        if (playerManager != null)
        {
            var serialized = new SerializedObject(playerManager);
            SerializedProperty property = serialized.FindProperty("_attackTrail");
            return property != null && property.objectReferenceValue != null;
        }

        EnemyVFXManager enemyManager = prefab.GetComponent<EnemyVFXManager>();
        if (enemyManager == null) return false;
        var enemySerialized = new SerializedObject(enemyManager);
        SerializedProperty enemyProperty = enemySerialized.FindProperty("_attackTrail");
        return enemyProperty != null && enemyProperty.objectReferenceValue != null;
    }

    // 刀光组件的关键配置是否达标（写错一项屏幕上就什么都看不到）。
    private static bool TrailLooksRight(AttackTrailVfx trail)
    {
        if (trail == null) return false;
        if (trail.Follow == null) return false;                     // 没跟随目标 = 停在原地不动
        if (trail.TipOffset == Vector3.zero) return false;          // 零偏移 = 带宽为 0
        if (trail.transform.parent == null || trail.transform.parent.name != "Vfx") return false;  // 必须挂在 Vfx 下
        if (trail.GetComponent<ParticleSystem>() != null) return false;                            // 粒子版残留
        MeshRenderer meshRenderer = trail.GetComponent<MeshRenderer>();
        if (meshRenderer == null || meshRenderer.sharedMaterial == null) return false;             // 没材质 = 什么都看不到
        MeshFilter filter = trail.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null) return false;
        return true;
    }

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
            Material created = CreateMaterial();
            if (created == null) return null;
            AssetDatabase.CreateAsset(created, MaterialPath);
            AssetDatabase.SaveAssets();
            Debug.Log("[AttackTrailSetup] 已生成刀光材质 " + MaterialPath + "（" + AttackTrailVfx.MaterialShaderName + "）");
            return AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        }

        // 已存在：shader 不对（例如上一版是 URP Lit）就换回目标 shader 并按基线重写参数，
        // 否则"紫黑"这类问题会一直挂着。
        if (existing.shader != expected) existing.shader = expected;
        existing.SetColor("_Tint", new Color(3f, 3f, 3f, 1f));
        existing.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        var slash = AssetDatabase.LoadAssetAtPath<Texture2D>(SlashTexturePath);
        if (slash != null) existing.SetTexture("_MainTex", slash);
        EditorUtility.SetDirty(existing);
        AssetDatabase.SaveAssets();
        return existing;
    }

    private static Material CreateMaterial()
    {
        Shader shader = Shader.Find(AttackTrailVfx.MaterialShaderName);
        if (shader == null) return null;
        var material = new Material(shader) { name = "AttackTrail" };
        material.SetColor("_Tint", new Color(3f, 3f, 3f, 1f));      // HDR：有 Bloom 出辉光，无 Bloom 也更亮
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        var slash = AssetDatabase.LoadAssetAtPath<Texture2D>(SlashTexturePath);
        if (slash != null) material.SetTexture("_MainTex", slash);
        return material;
    }

    // 刀光节点统一放在 <根>/Vfx/AttackTrail 下（与 FootSparks / Hurt 等同一层级约定）。
    private static AttackTrailVfx EnsureTrailUnderVfx(GameObject root, string[] anchors, string label, Vector3 tipOffset, Material material)
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

        // 清掉错位的旧刀光节点（早期版本挂在骨骼下、或重复的）
        foreach (Transform candidate in root.GetComponentsInChildren<Transform>(true))
        {
            if (candidate == null || candidate.name != AttackTrailVfx.NodeName) continue;
            if (candidate.parent == vfxRoot) continue;
            Debug.Log($"[AttackTrailSetup] {label} 清掉错位的旧刀光节点 {PathOf(candidate, root.transform)}");
            Object.DestroyImmediate(candidate.gameObject);
        }

        Transform node = vfxRoot.Find(AttackTrailVfx.NodeName);
        if (node == null)
        {
            node = new GameObject(AttackTrailVfx.NodeName).transform;
            node.SetParent(vfxRoot, false);
            node.localPosition = Vector3.zero;
        }

        // 粒子版残留整个移除，再补 Mesh 版组件。
        ParticleSystem staleParticles = node.GetComponent<ParticleSystem>();
        if (staleParticles != null) Object.DestroyImmediate(staleParticles, true);
        ParticleSystemRenderer staleRenderer = node.GetComponent<ParticleSystemRenderer>();
        if (staleRenderer != null) Object.DestroyImmediate(staleRenderer, true);

        AttackTrailVfx trail = node.GetComponent<AttackTrailVfx>();
        if (trail == null) trail = node.gameObject.AddComponent<AttackTrailVfx>();  // RequireComponent 自动补 MeshFilter/MeshRenderer
        trail.SetFollow(bone);
        trail.SetTipOffset(tipOffset);

        MeshRenderer meshRenderer = node.GetComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;

        EditorUtility.SetDirty(trail);
        Debug.Log($"[AttackTrailSetup] {label} 刀光节点 {VfxNodeName}/{AttackTrailVfx.NodeName}，跟随 {PathOf(bone, root.transform)}，刀尖偏移 {tipOffset}");
        return trail;
    }

    private static void SetupPlayer(Material material)
    {
        GameObject root = null;
        try
        {
            root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            if (root == null) { Debug.LogWarning("[AttackTrailSetup] 读不到 " + PlayerPrefabPath); return; }

            AttackTrailVfx trail = EnsureTrailUnderVfx(root, PlayerAnchors, "Player", PlayerTipOffset, material);
            if (trail == null) return;

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

    private static void SetupEnemy(string prefabPath, string label, string[] anchors, Vector3 tipOffset, Material material)
    {
        GameObject root = null;
        try
        {
            root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null) { Debug.LogWarning("[AttackTrailSetup] 读不到 " + prefabPath); return; }

            AttackTrailVfx trail = EnsureTrailUnderVfx(root, anchors, label, tipOffset, material);
            if (trail == null) return;

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

    // 按优先级找第一个同名节点；候选都带 "R/right" 前缀，左右手不会选错。
    private static Transform FindAnchor(Transform root, string[] priority)
    {
        foreach (string name in priority)
        {
            foreach (Transform candidate in root.GetComponentsInChildren<Transform>(true))
                if (candidate.name == name) return candidate;
        }
        return null;
    }

    // 旧的 Vfx/Attack1、Vfx/Attack2 是以玩家为原点播网格刀光的实现，拖尾接手后只剩死对象。
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
