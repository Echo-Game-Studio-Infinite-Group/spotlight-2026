using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    public static class ActionAuthoringGUI
    {
        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            {"ActionId", "动作 ID"}, {"DisplayName", "显示名称"}, {"Description", "说明"},
            {"Timeline", "分段时间轴"}, {"CancelWindows", "取消窗口（权限叠加）"}, {"Input", "输入与预输入"},
            {"StartConditions", "起招条件（全部满足）"}, {"EnergyCost", "起招耗能（接入方结算）"}, {"CooldownFrames", "冷却（玩家帧）"},
            {"SegmentId", "稳定子段 ID"}, {"Phase", "阶段"}, {"DurationFrames", "时长（玩家帧）"},
            {"Events", "帧事件"}, {"Control", "移动控制接口"}, {"Animation", "动画绑定接口"},
            {"Frame", "段内帧"}, {"EventKey", "事件键"}, {"HitGroup", "命中段编号"}, {"Value", "数值参数"},
            {"AllowMove", "允许移动输入"}, {"AllowTurn", "允许转向输入"}, {"GravityMultiplier", "重力倍率"},
            {"AllowJump", "允许原始跳跃输入"}, {"AllowSlide", "允许原始滑铲起手"}, {"AllowSprint", "允许冲刺输入"},
            {"Combat", "攻击判定"}, {"Enabled", "启用攻击上下文"}, {"Damage", "基础伤害"}, {"HitMask", "命中查询层"},
            {"MotorCommand", "运动命令类型"}, {"Layer", "动画层索引"},
            {"MovementCommand", "移动命令键"}, {"CommandValue", "命令数值"},
            {"AnimatorState", "Animator 状态完整路径"}, {"Clip", "参考动画片段"}, {"NormalizedStart", "片段归一化起点"},
            {"NormalizedEnd", "片段归一化终点"}, {"BlendFrames", "混合（玩家帧）"},
            {"WindowId", "稳定窗口 ID"}, {"Start", "窗口起点"}, {"End", "窗口终点（不含）"},
            {"RelativeTo", "参照边界"}, {"OffsetFrames", "偏移（玩家帧）"}, {"Targets", "允许转入的动作"},
            {"RequireAll", "全部条件键"}, {"RequireAny", "任一条件键（空则不限）"}, {"Priority", "选择优先级"},
            {"Steps", "有序输入步骤"}, {"MaxStepGapFrames", "步骤最大间隔（采样帧）"},
            {"PreInputFrames", "预输入（采样帧，0 仅立即）"}, {"BufferGroup", "预输入竞争组"},
            {"ReplacePolicy", "同组替换策略"}, {"KeepOnSourceCancel", "原动作取消后仍保留"},
            {"FreezeExpiryDuringHitStop", "顿帧暂停预输入过期"}, {"Button", "触发语义键"}, {"Trigger", "触发方式"},
            {"RequireHeld", "必须按住"}, {"ForbidHeld", "禁止按住"}, {"MinHoldFrames", "最短按住（采样帧）"},
            {"MaxHoldFrames", "最长按住（0 不限）"}, {"BufferCapacity", "请求缓冲容量"},
            {"InputHistoryCapacity", "输入事件历史容量"}, {"Actions", "动作资产列表"}
        };
        private static readonly Dictionary<string, string> EnumLabels = new Dictionary<string, string>
        {
            {"Startup", "发生"}, {"Active", "持续"}, {"Recovery", "收招"},
            {"SegmentStart", "子段开始"}, {"SegmentEnd", "子段结束"}, {"ActionStart", "动作开始"}, {"ActionEnd", "动作结束"},
            {"Pressed", "按下"}, {"Released", "松开"}, {"Held", "长按"},
            {"LatestInGroup", "保留同组最新"}, {"EarliestInGroup", "保留同组最早"}, {"Queue", "有限队列"},
            {"None", "无"}, {"Attack", "攻击"}, {"Skill", "技能修饰键"}, {"Jump", "跳跃"}, {"Sprint", "冲刺"},
            {"Slide", "下蹲/滑铲"}, {"Forward", "前"}, {"Backward", "后"}, {"Left", "左"}, {"Right", "右"},
            {"EnterSlide", "进入滑铲"}, {"ExitSlide", "退出滑铲"}, {"SetHorizontalSpeed", "设置水平速度"},
            {"LaunchVertical", "设置向上速度"}, {"ReverseHorizontal", "水平反向"}, {"AddForwardImpulse", "叠加向前速度"}, {"ClearHorizontal", "清空水平动量"}
        };

        public static string PhaseLabel(ActionPhase phase) => EnumLabels.TryGetValue(phase.ToString(), out string label) ? label : "无效阶段";

        public static bool Draw(SerializedObject serialized, ActionDefinition action = null, int selectedWindow = -1)
        {
            serialized.Update();
            SerializedProperty iterator = serialized.GetIterator();
            bool enter = true;
            while (iterator.NextVisible(enter))
            {
                enter = false;
                if (iterator.name == "m_Script") continue;
                DrawProperty(iterator.Copy(), action, selectedWindow);
            }
            bool changed = serialized.ApplyModifiedProperties();
            if (changed) EditorUtility.SetDirty(serialized.targetObject);
            return changed;
        }

        private static void DrawProperty(SerializedProperty property, ActionDefinition action, int selectedWindow)
        {
            string label = Labels.TryGetValue(property.name, out string translated) ? translated : property.displayName;
            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                DrawArray(property, action, selectedWindow, label);
                return;
            }
            if (property.propertyType == SerializedPropertyType.Generic)
            {
                property.isExpanded = EditorGUILayout.Foldout(property.isExpanded, label, true);
                if (!property.isExpanded) return;
                EditorGUI.indentLevel++;
                if (property.type == nameof(ActionFrameAnchor)) DrawAnchor(property, action);
                else if (property.type == nameof(ActionAnimationBinding)) ActionAnimatorAuthoring.DrawBinding(property);
                else
                {
                    SerializedProperty child = property.Copy();
                    SerializedProperty end = property.GetEndProperty();
                    if (child.NextVisible(true))
                        do { DrawProperty(child.Copy(), action, selectedWindow); }
                        while (child.NextVisible(false) && !SerializedProperty.EqualContents(child, end));
                }
                EditorGUI.indentLevel--;
                return;
            }
            if (property.name == "SegmentId" || property.name == "WindowId")
            {
                using (new EditorGUI.DisabledScope(true)) EditorGUILayout.PropertyField(property, new GUIContent(label));
            }
            else if (property.name == "RequireHeld" || property.name == "ForbidHeld")
            {
                ActionInputButtons flags = (ActionInputButtons)property.intValue;
                string[] names = Enum.GetNames(typeof(ActionInputButtons));
                string[] translatedNames = new string[names.Length - 1];
                for (int i = 1; i < names.Length; i++) translatedNames[i - 1] = EnumLabels[names[i]];
                property.intValue = EditorGUILayout.MaskField(label, (int)flags, translatedNames);
            }
            else if (property.propertyType == SerializedPropertyType.Enum)
            {
                string[] names = property.enumNames;
                var options = new string[names.Length];
                for (int i = 0; i < names.Length; i++) options[i] = EnumLabels.TryGetValue(names[i], out string value) ? value : names[i];
                property.enumValueIndex = EditorGUILayout.Popup(label, property.enumValueIndex, options);
            }
            else if (property.name == "EventKey")
            {
                EditorGUILayout.BeginHorizontal();
                property.stringValue = EditorGUILayout.TextField(label, property.stringValue);
                if (GUILayout.Button("选择", GUILayout.Width(45)))
                {
                    var menu = new GenericMenu();
                    SerializedObject owner = property.serializedObject; string path = property.propertyPath;
                    foreach (string key in new[] { "combat.hitbox.open", "combat.hitbox.close", "vfx.attack", "motor.command" })
                    {
                        string captured = key;
                        menu.AddItem(new GUIContent(key), property.stringValue == key, () =>
                        { owner.Update(); owner.FindProperty(path).stringValue = captured; owner.ApplyModifiedProperties(); EditorUtility.SetDirty(owner.targetObject); });
                    }
                    menu.ShowAsContext();
                }
                EditorGUILayout.EndHorizontal();
            }
            else EditorGUILayout.PropertyField(property, new GUIContent(label), true);
        }

        private static void DrawArray(SerializedProperty array, ActionDefinition action, int selectedWindow, string label)
        {
            EditorGUILayout.BeginHorizontal();
            array.isExpanded = EditorGUILayout.Foldout(array.isExpanded, label + "（" + array.arraySize + "）", true);
            if (GUILayout.Button("＋", GUILayout.Width(26)))
            {
                int index = array.arraySize;
                array.InsertArrayElementAtIndex(index);
                Initialize(array.GetArrayElementAtIndex(index), action);
                array.isExpanded = true;
            }
            EditorGUILayout.EndHorizontal();
            if (!array.isExpanded) return;
            EditorGUI.indentLevel++;
            for (int i = 0; i < array.arraySize; i++)
            {
                SerializedProperty element = array.GetArrayElementAtIndex(i);
                bool highlighted = array.name == "CancelWindows" && selectedWindow == i;
                Color original = GUI.backgroundColor;
                if (highlighted) GUI.backgroundColor = new Color(0.45f, 0.8f, 1f);
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                GUI.backgroundColor = original;
                EditorGUILayout.BeginHorizontal();
                string elementLabel = "第 " + (i + 1) + " 项";
                if (element.propertyType == SerializedPropertyType.Generic)
                {
                    SerializedProperty display = element.FindPropertyRelative("DisplayName");
                    if (display != null && !string.IsNullOrEmpty(display.stringValue)) elementLabel = display.stringValue;
                    element.isExpanded = EditorGUILayout.Foldout(element.isExpanded, elementLabel, true);
                }
                else EditorGUILayout.LabelField(elementLabel, GUILayout.Width(65));
                bool up = GUILayout.Button("↑", GUILayout.Width(25));
                bool down = GUILayout.Button("↓", GUILayout.Width(25));
                bool remove = GUILayout.Button("×", GUILayout.Width(25));
                EditorGUILayout.EndHorizontal();
                if (element.propertyType == SerializedPropertyType.Generic)
                {
                    if (element.isExpanded)
                    {
                        SerializedProperty child = element.Copy();
                        SerializedProperty end = element.GetEndProperty();
                        if (child.NextVisible(true))
                            do { DrawProperty(child.Copy(), action, selectedWindow); }
                            while (child.NextVisible(false) && !SerializedProperty.EqualContents(child, end));
                    }
                }
                else EditorGUILayout.PropertyField(element, GUIContent.none);
                EditorGUILayout.EndVertical();
                if (remove)
                {
                    int previous = array.arraySize;
                    array.DeleteArrayElementAtIndex(i);
                    if (array.arraySize == previous) array.DeleteArrayElementAtIndex(i);
                    break;
                }
                if (up && i > 0) { array.MoveArrayElement(i, i - 1); break; }
                if (down && i + 1 < array.arraySize) { array.MoveArrayElement(i, i + 1); break; }
            }
            EditorGUI.indentLevel--;
        }

        private static void DrawAnchor(SerializedProperty anchor, ActionDefinition action)
        {
            SerializedProperty boundary = anchor.FindPropertyRelative("RelativeTo");
            DrawProperty(boundary, action, -1);
            if (boundary.enumValueIndex <= 1)
            {
                SerializedProperty id = anchor.FindPropertyRelative("SegmentId");
                var ids = new List<string> { "" };
                var options = new List<string> { "请选择子段" };
                if (action?.Timeline != null)
                    foreach (ActionSegment segment in action.Timeline)
                        if (segment != null) { ids.Add(segment.SegmentId); options.Add(segment.DisplayName + " / " + PhaseLabel(segment.Phase)); }
                int current = ids.IndexOf(id.stringValue ?? "");
                if (current < 0) { current = ids.Count; ids.Add(id.stringValue); options.Add("已失效：" + id.stringValue); }
                int chosen = EditorGUILayout.Popup("参照子段", current, options.ToArray());
                id.stringValue = ids[chosen];
            }
            DrawProperty(anchor.FindPropertyRelative("OffsetFrames"), action, -1);
        }

        private static void Initialize(SerializedProperty property, ActionDefinition action)
        {
            ResetProperty(property);
            if (property.propertyType != SerializedPropertyType.Generic) return;
            SetString(property, "SegmentId", Guid.NewGuid().ToString("N"));
            SetString(property, "WindowId", Guid.NewGuid().ToString("N"));
            SetString(property, "DisplayName", property.type == nameof(ActionSegment) ? "新子段" : "新取消窗口");
            SerializedProperty duration = property.FindPropertyRelative("DurationFrames");
            if (duration != null) duration.intValue = 1;
            SerializedProperty control = property.FindPropertyRelative("Control");
            if (control != null)
            {
                control.FindPropertyRelative("AllowMove").boolValue = true;
                control.FindPropertyRelative("AllowTurn").boolValue = true;
                control.FindPropertyRelative("AllowJump").boolValue = true;
                control.FindPropertyRelative("AllowSlide").boolValue = true;
                control.FindPropertyRelative("AllowSprint").boolValue = true;
                control.FindPropertyRelative("GravityMultiplier").floatValue = 1;
            }
            SerializedProperty animation = property.FindPropertyRelative("Animation");
            if (animation != null) animation.FindPropertyRelative("NormalizedEnd").floatValue = 1;
            SerializedProperty end = property.FindPropertyRelative("End");
            if (end != null) end.FindPropertyRelative("RelativeTo").enumValueIndex = (int)ActionFrameAnchor.Boundary.ActionEnd;
            SerializedProperty start = property.FindPropertyRelative("Start");
            if (start != null && action?.Timeline != null && action.Timeline.Count > 0)
                start.FindPropertyRelative("SegmentId").stringValue = action.Timeline[action.Timeline.Count - 1]?.SegmentId ?? "";
            SerializedProperty button = property.FindPropertyRelative("Button");
            if (button != null) button.intValue = (int)ActionInputButtons.Attack;
        }

        private static void ResetProperty(SerializedProperty property)
        {
            if (property.isArray && property.propertyType != SerializedPropertyType.String) { property.ClearArray(); return; }
            switch (property.propertyType)
            {
                case SerializedPropertyType.Generic:
                    SerializedProperty child = property.Copy();
                    SerializedProperty end = property.GetEndProperty();
                    if (child.NextVisible(true))
                        do { ResetProperty(child.Copy()); }
                        while (child.NextVisible(false) && !SerializedProperty.EqualContents(child, end));
                    break;
                case SerializedPropertyType.String: property.stringValue = ""; break;
                case SerializedPropertyType.Boolean: property.boolValue = false; break;
                case SerializedPropertyType.Float: property.floatValue = 0; break;
                case SerializedPropertyType.Integer: property.intValue = 0; break;
                case SerializedPropertyType.Enum: property.enumValueIndex = 0; break;
                case SerializedPropertyType.ObjectReference: property.objectReferenceValue = null; break;
            }
            property.isExpanded = true;
        }

        private static void SetString(SerializedProperty parent, string key, string value)
        {
            SerializedProperty property = parent.FindPropertyRelative(key);
            if (property != null) property.stringValue = value;
        }
    }
}
