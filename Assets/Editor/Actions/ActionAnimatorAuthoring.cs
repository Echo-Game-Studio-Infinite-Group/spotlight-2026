using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    public static class ActionAnimatorAuthoring
    {
        public readonly struct StateInfo
        {
            public readonly string Path;
            public readonly int Layer;
            public readonly AnimatorState State;
            public StateInfo(string path, int layer, AnimatorState state) { Path = path; Layer = layer; State = state; }
        }
        private static AnimatorController _cachedController;
        private static readonly List<StateInfo> CachedStates = new List<StateInfo>();
        public static AnimatorController Controller;
        public static void Invalidate() => _cachedController = null;
        public static IReadOnlyList<StateInfo> GetStates(AnimatorController controller)
        {
            if (controller == null) { CachedStates.Clear(); _cachedController = null; return CachedStates; }
            if (_cachedController == controller) return CachedStates;
            _cachedController = controller; CachedStates.Clear();
            for (int layer = 0; layer < controller.layers.Length; layer++)
                Collect(controller.layers[layer].stateMachine, controller.layers[layer].name, layer);
            return CachedStates;
        }
        private static void Collect(AnimatorStateMachine machine, string path, int layer)
        {
            foreach (ChildAnimatorState child in machine.states) CachedStates.Add(new StateInfo(path + "." + child.state.name, layer, child.state));
            foreach (ChildAnimatorStateMachine child in machine.stateMachines) Collect(child.stateMachine, path + "." + child.stateMachine.name, layer);
        }
        public static void DrawBinding(SerializedProperty property)
        {
            SerializedProperty path = property.FindPropertyRelative("AnimatorState");
            SerializedProperty layer = property.FindPropertyRelative("Layer");
            SerializedProperty clip = property.FindPropertyRelative("Clip");
            IReadOnlyList<StateInfo> states = GetStates(Controller);
            var names = new List<string> { "使用 Motor 基础动画" };
            int current = string.IsNullOrWhiteSpace(path.stringValue) ? 0 : -1;
            for (int i = 0; i < states.Count; i++)
            {
                names.Add(states[i].Path);
                if (states[i].Path == path.stringValue && states[i].Layer == layer.intValue) current = i + 1;
            }
            if (current < 0) { current = names.Count; names.Add("未找到：" + path.stringValue); }
            EditorGUI.BeginChangeCheck();
            int chosen = EditorGUILayout.Popup("Animator 状态", current, names.ToArray());
            if (EditorGUI.EndChangeCheck())
            {
                if (chosen == 0) { path.stringValue = ""; clip.objectReferenceValue = null; layer.intValue = 0; }
                else if (chosen <= states.Count)
                {
                    StateInfo info = states[chosen - 1]; path.stringValue = info.Path; layer.intValue = info.Layer;
                    clip.objectReferenceValue = info.State.motion as AnimationClip;
                }
            }
            EditorGUILayout.PropertyField(path, new GUIContent("状态完整路径"));
            EditorGUILayout.PropertyField(layer, new GUIContent("动画层索引"));
            EditorGUILayout.PropertyField(clip, new GUIContent("动画片段"));
            SerializedProperty start = property.FindPropertyRelative("NormalizedStart");
            SerializedProperty end = property.FindPropertyRelative("NormalizedEnd");
            float a = start.floatValue, b = end.floatValue;
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.MinMaxSlider("播放区间", ref a, ref b, 0f, 1f);
            if (EditorGUI.EndChangeCheck()) { start.floatValue = a; end.floatValue = b; }
            EditorGUILayout.PropertyField(start, new GUIContent("归一化起点"));
            EditorGUILayout.PropertyField(end, new GUIContent("归一化终点"));
            EditorGUILayout.PropertyField(property.FindPropertyRelative("BlendFrames"), new GUIContent("过渡混合（逻辑帧）"));
        }
        public static List<ActionValidationIssue> Validate(ActionCatalog catalog, AnimatorController controller)
        {
            var issues = new List<ActionValidationIssue>();
            if (catalog == null || controller == null) return issues;
            IReadOnlyList<StateInfo> states = GetStates(controller);
            foreach (ActionDefinition action in catalog.Actions)
            {
                if (action?.Timeline == null) continue;
                foreach (ActionSegment segment in action.Timeline)
                {
                    ActionAnimationBinding binding = segment?.Animation;
                    if (binding == null || string.IsNullOrWhiteSpace(binding.AnimatorState)) continue;
                    if (binding.Layer != 0) issues.Add(new ActionValidationIssue(ActionValidationIssue.Severity.Error, "当前角色适配器使用第 0 层全身动作，多层占用需扩展适配器", action));
                    AnimatorState found = null;
                    foreach (StateInfo info in states)
                        if (info.Path == binding.AnimatorState && info.Layer == binding.Layer) { found = info.State; break; }
                    if (found == null) issues.Add(new ActionValidationIssue(ActionValidationIssue.Severity.Error, "动画状态不存在：" + binding.AnimatorState, action));
                    else
                    {
                        if (!found.timeParameterActive || found.timeParameter != binding.TimeParameter)
                            issues.Add(new ActionValidationIssue(ActionValidationIssue.Severity.Error, "动作状态需启用独立 Motion Time = " + binding.TimeParameter, action));
                        if (binding.Clip != null && found.motion != binding.Clip)
                            issues.Add(new ActionValidationIssue(ActionValidationIssue.Severity.Error, "配置片段与 Animator 状态的 Motion 不一致：" + binding.AnimatorState, action));
                    }
                    foreach (string parameter in new[] { ActionAnimatorBridge.TimeParameter, ActionAnimatorBridge.PlayingParameter, binding.TimeParameter })
                    {
                        AnimatorControllerParameterType type = parameter == ActionAnimatorBridge.PlayingParameter ? AnimatorControllerParameterType.Bool : AnimatorControllerParameterType.Float;
                        bool valid = false;
                        foreach (AnimatorControllerParameter value in controller.parameters) if (value.name == parameter && value.type == type) valid = true;
                        if (!valid) issues.Add(new ActionValidationIssue(ActionValidationIssue.Severity.Error, "动画参数缺失或类型错误：" + parameter, action));
                    }
                }
            }
            return issues;
        }
    }
}
