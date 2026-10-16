using UnityEditor;
using UnityEngine;

namespace DeferredOutline.Editor
{
    [CustomPropertyDrawer(typeof(DeferredOutlineFeature.FeatureSettings))]
    public sealed class OutlineFeatureSettingsDrawer : PropertyDrawer
    {
        static readonly string[] Names = CreateNames();
        static string[] CreateNames()
        {
            var names = new string[32];
            for (int i = 0; i < names.Length; i++) names[i] = "Rendering Layer " + i;
            names[0] += " (Default)";
            names[7] += " (Outline: 128)";
            return names;
        }
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
            => (EditorGUIUtility.singleLineHeight + 2f) * 7f;
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var row = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            EditorGUI.LabelField(row, label, EditorStyles.boldLabel);
            row.y += row.height + 2f;
            SerializedProperty mask = property.FindPropertyRelative("renderingLayerMask");
            EditorGUI.BeginChangeCheck();
            int value = EditorGUI.MaskField(row, new GUIContent("Rendering Layers", "筛选Renderer的Rendering Layer，不筛选GameObject Layer。"), unchecked((int)mask.longValue), Names);
            if (EditorGUI.EndChangeCheck()) mask.longValue = unchecked((uint)value);
            foreach (string field in new[] { "group", "presetOverride", "individualTargets", "renderPassEvent", "sceneView" })
            {
                row.y += row.height + 2f;
                EditorGUI.PropertyField(row, property.FindPropertyRelative(field));
            }
            EditorGUI.EndProperty();
        }
    }
}