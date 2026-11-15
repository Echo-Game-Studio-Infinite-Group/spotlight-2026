using GameJam.Flow;
using UnityEditor;
using UnityEngine;

namespace GameJam.FlowEditor
{
    [CustomPropertyDrawer(typeof(RoomPoolEntry))]
    public sealed class RoomPoolEntryDrawer : PropertyDrawer
    {
        private static readonly string[] Fields = { nameof(RoomPoolEntry.Enabled), nameof(RoomPoolEntry.Variant), nameof(RoomPoolEntry.Weight) };

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float height = EditorGUIUtility.singleLineHeight;
            if (property.isExpanded)
                foreach (string name in Fields)
                    height += EditorGUIUtility.standardVerticalSpacing + EditorGUI.GetPropertyHeight(property.FindPropertyRelative(name), true);
            return height;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            int previousIndent = EditorGUI.indentLevel;
            try
            {
                position.height = EditorGUIUtility.singleLineHeight;
                property.isExpanded = EditorGUI.Foldout(position, property.isExpanded, label, true);
                if (!property.isExpanded) return;
                EditorGUI.indentLevel++;
                foreach (string name in Fields)
                {
                    SerializedProperty child = property.FindPropertyRelative(name);
                    position.y += position.height + EditorGUIUtility.standardVerticalSpacing;
                    position.height = EditorGUI.GetPropertyHeight(child, true);
                    EditorGUI.PropertyField(position, child, FlowEditorLabels.Field(typeof(RoomPoolEntry), name), true);
                }
            }
            finally { EditorGUI.indentLevel = previousIndent; EditorGUI.EndProperty(); }
        }
    }
}
