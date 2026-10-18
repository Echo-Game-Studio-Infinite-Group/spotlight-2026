using Spotlight.VFX;
using UnityEditor;
using UnityEngine;

// 战斗火花特效接线工具：幂等地把 SlideSparks（脚底摩擦火花）加到 Player.prefab 组件表里，
// 并把调参配方 RuinsFireConfig.asset 接进组件（combatSparks 段 = 火花的唯一调参入口）。
//
// 为什么只有这个组件要动预制体：命中火花挂在 PlayerCombat.OnLandedHit、parry 火花挂在 PlayerCombat.TryParry，
// 都是代码路径，随脚本生效；脚底摩擦火花是独立组件，必须落到玩家身上才有东西可跑。
// 走预制体而不是逐场景改实例：所有用 Player.prefab 实例的场景自动继承，一处接线处处生效。
// 注意「完全解包」的玩家副本不吃预制体改动——那些场景要单独接（当前测试场景均为预制体实例）。
public static class CombatVfxWiring
{
    private const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
    private const string ConfigPath = "Assets/Art/VFX/Fire/RuinsFireConfig.asset";

    [MenuItem("超高速行者/战斗特效/接线脚底火花到Player预制体")]
    public static void WireFromMenu() => Wire();

    /// <summary>批处理编排：接线 → 自检（一次 Unity 启动全做完）。</summary>
    public static void WireAndSelfCheck()
    {
        Wire();
        CombatVfxSelfCheck.Run();
    }

    /// <summary>批处理入口：-executeMethod CombatVfxWiring.Wire（幂等，重复跑只报「无事可做」）。</summary>
    public static void Wire()
    {
        RuinsFireConfig config = AssetDatabase.LoadAssetAtPath<RuinsFireConfig>(ConfigPath);
        if (config == null)
            Debug.LogWarning("[CombatVfxWiring] 找不到配方资产（" + ConfigPath + "）：火花将用内置默认参数，跳过配方接线");

        bool dirty = false;
        GameObject root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
        try
        {
            if (root.GetComponent<SlideSparks>() == null)
            {
                root.AddComponent<SlideSparks>();
                dirty = true;
                Debug.Log("[CombatVfxWiring] Player.prefab 已加 SlideSparks（脚底摩擦火花）");
            }
            if (config != null)
            {
                SerializedObject so = new SerializedObject(root.GetComponent<SlideSparks>());
                SerializedProperty prop = so.FindProperty("_config");
                if (prop != null && prop.objectReferenceValue != (Object)config)
                {
                    prop.objectReferenceValue = config;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    dirty = true;
                    Debug.Log("[CombatVfxWiring] Player.prefab 的 SlideSparks 已接 RuinsFireConfig（combatSparks 段）");
                }
            }
            if (dirty)
                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
            else
                Debug.Log("[CombatVfxWiring] 装配完成：Player.prefab 已就位，无事可做");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        // 把 combatSparks 段（含默认值）序列化进配方资产：调参入口在版本库里可见、可 diff
        if (config != null)
        {
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
        }
    }
}
