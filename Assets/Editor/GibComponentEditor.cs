using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GibComponent))]
public sealed class GibComponentEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            if (GUILayout.Button("动态预览 Gib")) GibPreviewWindow.Open((GibComponent)target);
    }
}
