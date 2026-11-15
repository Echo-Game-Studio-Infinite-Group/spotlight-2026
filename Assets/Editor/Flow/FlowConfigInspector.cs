using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace GameJam.FlowEditor
{
    public abstract class FlowConfigInspector : UnityEditor.Editor
    {
        private readonly Dictionary<string, ReorderableList> _lists = new Dictionary<string, ReorderableList>();

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            string description = FlowEditorLabels.Description(target);
            if (!string.IsNullOrEmpty(description)) EditorGUILayout.HelpBox(description, MessageType.Info);
            float previousWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Clamp(EditorGUIUtility.currentViewWidth * .35f, 180f, 250f);
            try
            {
                SerializedProperty property = serializedObject.GetIterator();
                bool enter = true;
                while (property.NextVisible(enter))
                {
                    enter = false;
                    if (property.propertyPath == "m_Script" || !ShouldDrawProperty(property)) continue;
                    GUIContent label = FlowEditorLabels.Field(target.GetType(), property.name);
                    if (property.isArray && property.propertyType != SerializedPropertyType.String) DrawArray(property, label);
                    else EditorGUILayout.PropertyField(property, label, true);
                }
                if (serializedObject.ApplyModifiedProperties())
                    foreach (Object item in targets) EditorUtility.SetDirty(item);
            }
            finally { EditorGUIUtility.labelWidth = previousWidth; }
        }

        protected virtual bool ShouldDrawProperty(SerializedProperty property) => true;

        private void DrawArray(SerializedProperty property, GUIContent label)
        {
            if (!_lists.TryGetValue(property.propertyPath, out ReorderableList list))
            {
                list = new ReorderableList(serializedObject, property.Copy(), true, true, true, true);
                list.drawHeaderCallback = rect => EditorGUI.LabelField(rect,
                    new GUIContent(label.text + "（" + list.serializedProperty.arraySize + " 项）", label.tooltip));
                list.drawNoneElementCallback = rect => EditorGUI.LabelField(rect, "暂无条目，点击右下角“+”添加");
                list.elementHeightCallback = index => EditorGUI.GetPropertyHeight(list.serializedProperty.GetArrayElementAtIndex(index), true) + 4f;
                list.drawElementCallback = (rect, index, active, focused) =>
                {
                    SerializedProperty element = list.serializedProperty.GetArrayElementAtIndex(index);
                    rect.y += 2f;
                    rect.height = EditorGUI.GetPropertyHeight(element, true);
                    // 引用值保留资产身份；列表标签统一使用中文，不改编号或序列化字段名。
                    EditorGUI.PropertyField(rect, element, new GUIContent("条目 " + (index + 1)), true);
                };
                _lists.Add(property.propertyPath, list);
            }
            list.serializedProperty = property.Copy();
            list.DoLayoutList();
        }
    }
}
