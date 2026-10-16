using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GibComponent))]
public sealed class GibComponentEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        EditorGUILayout.HelpBox("自动收集子物体的蒙皮网格与普通网格零件，无需合并。普通网格模型需开启 Read/Write；"
            + "非 Humanoid 模型指定 Waist，并在 Protected Arm Roots 按左、右顺序拖入上臂骨骼。", MessageType.Info);
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            if (GUILayout.Button("动态预览 Gib")) GibPreviewWindow.Open((GibComponent)target);
    }
}
