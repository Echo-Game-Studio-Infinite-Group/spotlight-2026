using UnityEditor;
using UnityEngine;

// EnemySpawner 的 Scene 视图编辑器。
//
// 运行时 Gizmos 已经画了圆环，但 Gizmos 只能"看"不能"改"；
// 策划调范围时必须能量着拖。这里补 Handles：
//   - 外圈半径手柄：拖它改 Spawn Range
//   - 内圈半径手柄：拖它改 Inner Range
//   - 位置手柄：拖它移动圆心
//
// 刻意不画满整圈的点阵预览：那需要真跑一次落地查询（含物理射线），
// 在 OnSceneGUI 里每帧执行会把编辑器拖卡（见 AGENTS.md 的 OnGUI 性能坑）。
[CustomEditor(typeof(EnemySpawner))]
public sealed class EnemySpawnerEditor : Editor
{
    private SerializedProperty _spawnRange;
    private SerializedProperty _innerRange;
    private SerializedProperty _groundMode;
    private SerializedProperty _maxAlive;
    private SerializedProperty _spawnObject;

    private void OnEnable()
    {
        _spawnRange = serializedObject.FindProperty("_spawnRange");
        _innerRange = serializedObject.FindProperty("_innerRange");
        _groundMode = serializedObject.FindProperty("_groundMode");
        _maxAlive = serializedObject.FindProperty("_maxAlive");
        _spawnObject = serializedObject.FindProperty("_spawnObject");
    }

    private void OnSceneGUI()
    {
        var spawner = (EnemySpawner)target;
        Vector3 center = spawner.transform.position;
        // 手柄画在地面附近而不是 Spawner 自身高度：圆心可能被摆在上方，
        // 画在高处的圆和实际地面上的生成范围看起来会差很远。
        Vector3 handleCenter = new Vector3(center.x, center.y + 0.05f, center.z);

        // ---- 移动圆心 ----
        EditorGUI.BeginChangeCheck();
        Vector3 movedCenter = Handles.PositionHandle(handleCenter, Quaternion.identity);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(spawner.transform, "移动生成器");
            spawner.transform.position = new Vector3(movedCenter.x, center.y, movedCenter.z);
        }

        // ---- 外圈半径 ----
        float range = Mathf.Max(0f, _spawnRange.floatValue);
        EditorGUI.BeginChangeCheck();
        float newRange = DrawRadiusHandle(handleCenter, range, new Color(1f, 0.45f, 0.15f), "生成范围");
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(spawner, "调整生成范围");
            _spawnRange.floatValue = Mathf.Max(0f, newRange);
            // 外圈缩到比内圈还小时把内圈一起压下来，避免出现"内圈大于外圈"的无效状态。
            if (_innerRange.floatValue > _spawnRange.floatValue)
                _innerRange.floatValue = _spawnRange.floatValue;
        }

        // ---- 内圈半径 ----
        float inner = Mathf.Clamp(_innerRange.floatValue, 0f, range);
        EditorGUI.BeginChangeCheck();
        float newInner = DrawRadiusHandle(handleCenter, inner, new Color(1f, 0.85f, 0.25f), "内圈范围");
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(spawner, "调整内圈范围");
            _innerRange.floatValue = Mathf.Clamp(newInner, 0f, _spawnRange.floatValue);
        }

        serializedObject.ApplyModifiedProperties();
    }

    // 沿 +X 方向摆一个半径手柄，拖动即改半径。
    // 用 HandleUtility.GetHandleSize 缩放尺寸，让手柄在拉远拉近时视觉大小恒定。
    private static float DrawRadiusHandle(Vector3 center, float radius, Color color, string label)
    {
        Vector3 handlePos = center + Vector3.right * radius;
        float size = HandleUtility.GetHandleSize(handlePos) * 0.12f;

        Handles.color = color;
        // 先画一圈提示线：手柄本身很小，没有参考线就看不出它在拖什么。
        Handles.DrawWireDisc(center, Vector3.up, Mathf.Max(radius, 0.001f));

        float result = radius;
        EditorGUI.BeginChangeCheck();
        // 用不带 rotation 的重载：带 rotation 的那个在 2022.3 已标记过时。
        Vector3 newPos = Handles.FreeMoveHandle(handlePos, size, Vector3.zero, Handles.SphereHandleCap);
        if (EditorGUI.EndChangeCheck())
        {
            // 半径 = 新位置到圆心的水平距离。只取水平分量，竖直拖动不应该改变半径。
            Vector3 flat = new Vector3(newPos.x - center.x, 0f, newPos.z - center.z);
            result = flat.magnitude;
        }

        Handles.Label(center + Vector3.right * radius + Vector3.up * 0.35f, $"{label}: {result:0.##}m");
        return result;
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawDefaultInspector();

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            // 实时状态只读展示：不进序列化，所以显示为禁用态。
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.IntField("当前存活", Application.isPlaying ? ((EnemySpawner)target).AliveCount : 0);
            }
        }

        if (_spawnObject.objectReferenceValue == null)
        {
            EditorGUILayout.HelpBox("未指定 Spawn Object，生成器不会工作。", MessageType.Warning);
        }

        if (_groundMode.enumValueIndex == (int)SpawnGroundMode.NavMeshOnly)
        {
            EditorGUILayout.HelpBox(
                "当前只走 NavMesh 采样：场景必须先烘焙 NavMesh，否则所有生成都会失败。",
                MessageType.Info);
        }

        serializedObject.ApplyModifiedProperties();
    }
}
