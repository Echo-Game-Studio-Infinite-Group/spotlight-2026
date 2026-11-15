using System;
using GameJam.Flow;
using UnityEditor;
using UnityEngine;

namespace GameJam.FlowEditor
{
    [CustomEditor(typeof(FlowTextAnimation))]
    public sealed class FlowTextAnimationInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox("读取现有文字动画的材质和变换曲线。更换来源或修改动画后，请重新读取并保存。", MessageType.Info);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(FlowTextAnimation.Source)));
            serializedObject.ApplyModifiedProperties();
            var effect = (FlowTextAnimation)target;
            EditorGUILayout.LabelField("已读取时长", effect.Duration.ToString("0.###") + " 秒");
            if (GUILayout.Button("重新读取动画曲线"))
                try { FlowTextAuthoring.Bake(effect); AssetDatabase.SaveAssets(); }
                catch (Exception exception) { Debug.LogError(exception.Message, effect); }
        }
    }
}
