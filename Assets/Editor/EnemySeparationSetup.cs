using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

// 幂等地把 EnemySeparation 挂到敌人预制体上，并按各自的胶囊尺寸写好分离间距。
//
// 为什么要有这个工具而不是手拖：
//   · 漏挂不会报错，只会静默失效 —— 表现就是敌人照旧叠在一起，很难判断是"没挂"还是"参数不对"；
//   · 分离间距必须跟胶囊半径挂钩，三个敌人尺寸差一倍以上（轻型 radius 0.382 / 重型 radius 1），
//     手填容易漏改。
public static class EnemySeparationSetup
{
    private struct Target
    {
        public string Path;
        public float Separation;   // 期望的中心间距，约等于两倍胶囊半径
    }

    // 间距取值依据（读自预制体的 CharacterController）：
    //   轻型 / 远程：radius 0.382 → 2×0.382 = 0.765，取 0.8 留一点余量
    //   重型：       radius 1.0   → 2×1.0   = 2.0，  取 2.2 留一点余量
    private static readonly Target[] Targets =
    {
        new Target { Path = "Assets/Prefabs/EnemyTest.prefab",      Separation = 0.8f },
        new Target { Path = "Assets/Prefabs/EnemyTestRange.prefab", Separation = 0.8f },
        new Target { Path = "Assets/Prefabs/EnemyHeavy.prefab",     Separation = 2.2f },
    };

    [MenuItem("超高速行者/敌人/挂载敌人分离")]
    public static void Apply()
    {
        // 播放模式下改预制体会被运行中的实例覆盖，先退出。
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[敌人分离] 请先退出播放模式再执行。");
            return;
        }

        int added = 0, updated = 0, failed = 0;
        foreach (Target target in Targets)
        {
            GameObject contents = null;
            try
            {
                contents = PrefabUtility.LoadPrefabContents(target.Path);
                if (contents == null) { failed++; Debug.LogError($"[敌人分离] 载入失败：{target.Path}"); continue; }

                if (contents.GetComponent<NavMeshAgent>() == null)
                {
                    failed++;
                    Debug.LogError($"[敌人分离] {target.Path} 没有 NavMeshAgent，无法分离（分离必须靠 agent.Move）。", contents);
                    continue;
                }

                EnemySeparation separation = contents.GetComponent<EnemySeparation>();
                bool isNew = separation == null;
                if (isNew) separation = contents.AddComponent<EnemySeparation>();

                var so = new SerializedObject(separation);
                SerializedProperty prop = so.FindProperty("_separation");
                if (prop != null) prop.floatValue = target.Separation;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(separation);

                PrefabUtility.SaveAsPrefabAsset(contents, target.Path);
                if (isNew) added++; else updated++;
                Debug.Log($"[敌人分离] {(isNew ? "新增" : "更新")}：{target.Path}，分离间距 {target.Separation:0.##}m");
            }
            catch (Exception e)
            {
                failed++;
                Debug.LogError($"[敌人分离] {target.Path} 处理失败：{e.Message}");
            }
            finally
            {
                if (contents != null) PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[敌人分离] 完成：新增 {added}，更新 {updated}，失败 {failed}。");
    }
}
