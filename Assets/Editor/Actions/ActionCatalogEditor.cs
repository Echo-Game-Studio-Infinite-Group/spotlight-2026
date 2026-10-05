using UnityEditor;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    [CustomEditor(typeof(ActionCatalog))]
    public sealed class ActionCatalogEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            if (GUILayout.Button("打开动作序列配表工具")) ActionSequenceWindow.Show((ActionCatalog)target, null);
            ActionAuthoringGUI.Draw(serializedObject);
        }
    }
}
