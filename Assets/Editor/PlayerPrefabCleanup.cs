using UnityEditor;
using UnityEngine;

// 一次性清理：删掉 Player.prefab 的 Vfx/Attack3 节点。
// 代码侧已经没有任何引用（动作序列的 vfx.attack 帧事件只会传 1 / 2），节点留着只会让人以为还有第三段。
//
// 为什么用编辑器脚本而不是直接改 YAML：Unity 会把内存里的旧副本反写回文件，手改必被覆盖
// （本项目已经踩过一次：文件删了，回到编辑器后 10 分钟又被写回 484 块）。只有编辑器自己删才作数。
//
// 幂等 + 自动：节点不存在就什么都不做；每个编辑器会话最多自动跑一次（SessionState 记标记），
// 失败（例如预制体正开在 Prefab 模式里）只打警告，不影响任何玩法逻辑，菜单可手动重跑。
[InitializeOnLoad]
public static class PlayerPrefabCleanup
{
    private const string PrefabPath = "Assets/Prefabs/Player.prefab";
    private const string NodePath = "Vfx/Attack3";

    static PlayerPrefabCleanup()
    {
        // 域重载刚结束时 AssetDatabase 未必就绪，推迟一帧再动资产。
        EditorApplication.delayCall += AutoCleanup;
    }

    private static void AutoCleanup()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        // 不用「一个会话只跑一次」的标记：Unity 有可能把内存里的旧副本反写回文件，
        // 那样一次性的标记会让节点永远删不掉。每次域重载做一次便宜的存在性检查，在才删。
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null || prefab.transform.Find(NodePath) == null) return;
        RemoveAttackNode(true);
    }

    [MenuItem("超高速行者/清理/删除 Player 上的 Attack3 节点")]
    public static void RemoveAttackNodeFromMenu() => RemoveAttackNode(false);

    private static void RemoveAttackNode(bool silentWhenClean)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[PlayerPrefabCleanup] 先退出 Play 模式再清理预制体");
            return;
        }

        GameObject root = null;
        try
        {
            root = PrefabUtility.LoadPrefabContents(PrefabPath);
            if (root == null)
            {
                Debug.LogWarning("[PlayerPrefabCleanup] 读不到 " + PrefabPath);
                return;
            }
            Transform node = root.transform.Find(NodePath);
            if (node == null)
            {
                if (!silentWhenClean) Debug.Log("[PlayerPrefabCleanup] Player.prefab 已经是干净的（没有 " + NodePath + "）");
                return;
            }
            Object.DestroyImmediate(node.gameObject);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[PlayerPrefabCleanup] 已从 Player.prefab 删除 " + NodePath
                + "（若场景里还看得到，重开该场景或右键预制体 Reimport）");
        }
        catch (System.Exception error)
        {
            // 预制体正开在 Prefab 模式里时改不动，交给菜单手动重跑。
            Debug.LogWarning("[PlayerPrefabCleanup] 清理失败（预制体可能在 Prefab 模式中打开着）：" + error.Message);
        }
        finally
        {
            if (root != null) PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
