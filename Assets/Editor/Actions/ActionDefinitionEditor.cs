using UnityEditor;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    [CustomEditor(typeof(ActionDefinition))]
    public sealed class ActionDefinitionEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            ActionDefinition action = (ActionDefinition)target;
            if (GUILayout.Button("在动作序列配表工具中打开"))
                if (!ActionSequenceWindow.OpenAsset(action.GetInstanceID(), 0)) ActionSequenceWindow.Show(null, action);
            ActionTimelineView.Draw(action, -1, -1, null);
            ActionAuthoringGUI.Draw(serializedObject, action);
        }
    }
}
