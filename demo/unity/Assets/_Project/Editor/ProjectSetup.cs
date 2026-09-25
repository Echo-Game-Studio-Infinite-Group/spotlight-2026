using UnityEditor;
using UnityEngine;

// 工程规范强制（AGENTS.md 第 1 条）：逻辑帧 60Hz，Fixed Timestep = 1/60
// [InitializeOnLoad] 保证每次打开/重编译工程都重新校验；只写 Time.fixedDeltaTime（m_FixedDeltaTime），不碰其他 ProjectSettings
[InitializeOnLoad]
public static class ProjectSetup
{
    private const float TargetFixedTimestep = 1f / 60f;
    private const string TimeManagerAssetPath = "ProjectSettings/TimeManager.asset";

    static ProjectSetup()
    {
        // 直接在加载时应用：batchmode -quit 下 delayCall 可能来不及触发；引擎默认值此时已加载完毕
        ApplyFixedTimestep();
    }

    [MenuItem("超高速行者/强制 60Hz 逻辑帧")]
    public static void ApplyFixedTimestep()
    {
        // 幂等：仅在偏离目标时写入；约定只改 Fixed Timestep 这一个字段，其余 ProjectSettings 一律不碰
        // 以磁盘序列化值为准：Time.fixedDeltaTime 的 setter 会同步内存中的 TimeManager 对象，
        // 若先改内存再查序列化值会把“已改”误判为“已达标”
        bool changed = false;

        Object timeManager = GetTimeManagerAsset();
        if (timeManager == null)
        {
            Debug.LogWarning("[ProjectSetup] 未找到 ProjectSettings/TimeManager.asset，跳过 Fixed Timestep 强制");
            return;
        }

        SerializedObject serialized = new SerializedObject(timeManager);
        SerializedProperty prop = serialized.FindProperty("m_FixedDeltaTime");
        if (prop == null) prop = serialized.FindProperty("Fixed Timestep"); // ProjectSettings YAML 用友好名
        if (prop != null && Mathf.Abs(prop.floatValue - TargetFixedTimestep) > 1e-6f)
        {
            prop.floatValue = TargetFixedTimestep;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            // 序列化修改不自动落盘，必须显式保存 ProjectSettings
            EditorUtility.SetDirty(timeManager);
            AssetDatabase.SaveAssets();
            changed = true;
        }

        if (Mathf.Abs(Time.fixedDeltaTime - TargetFixedTimestep) > 1e-6f)
        {
            Time.fixedDeltaTime = TargetFixedTimestep; // 内存值同步，保证编辑器内立刻一致
            changed = true;
        }

        if (changed)
        {
            Debug.Log("[ProjectSetup] Fixed Timestep 已强制为 1/60");
        }
    }

    private static Object GetTimeManagerAsset()
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath(TimeManagerAssetPath);
        return assets != null && assets.Length > 0 ? assets[0] : null;
    }
}
