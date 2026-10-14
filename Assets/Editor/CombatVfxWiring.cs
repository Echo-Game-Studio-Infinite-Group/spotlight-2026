using UnityEditor;
using UnityEngine;

// 战斗火花特效接线工具：幂等地把 SlideSparks（脚底摩擦火花）加到 Player.prefab 组件表里。
//
// 为什么只有这个组件要动预制体：命中火花挂在 PlayerCombat.OnLandedHit、parry 演示键挂在 DebugHUD，
// 都是代码路径，随脚本生效；脚底摩擦火花是独立组件，必须落到玩家身上才有东西可跑。
// 走预制体而不是逐场景改实例：所有用 Player.prefab 实例的场景自动继承，一处接线处处生效。
// 注意「完全解包」的玩家副本不吃预制体改动——那些场景要单独接（当前测试场景均为预制体实例）。
public static class CombatVfxWiring
{
    private const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";

    [MenuItem("超高速行者/战斗特效/接线脚底火花到Player预制体")]
    public static void WireFromMenu() => Wire();

    /// <summary>批处理入口：-executeMethod CombatVfxWiring.Wire（幂等，重复跑只报「无事可做」）。</summary>
    public static void Wire()
    {
        GameObject root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
        try
        {
            if (root.GetComponent<SlideSparks>() != null)
            {
                Debug.Log("[CombatVfxWiring] 装配完成：Player.prefab 已有 SlideSparks，无事可做");
                return;
            }
            root.AddComponent<SlideSparks>();
            PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
            Debug.Log("[CombatVfxWiring] 装配完成：Player.prefab 已加 SlideSparks（脚底摩擦火花）");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
