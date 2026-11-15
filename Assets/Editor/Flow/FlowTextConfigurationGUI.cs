using System.Collections.Generic;
using System.Linq;
using GameJam.Flow;
using UnityEditor;
using UnityEngine;

namespace GameJam.FlowEditor
{
    public sealed class FlowTextConfigurationGUI
    {
        private readonly HashSet<string> _expanded = new HashSet<string>();
        private FlowRoomTemplate _template;
        private FlowTextPoint[] _points = new FlowTextPoint[0];
        private bool _refresh = true;
        public void Invalidate() => _refresh = true;

        public void Draw(RoomVariantDefinition variant, SerializedObject serialized)
        {
            EditorGUILayout.Space(); EditorGUILayout.LabelField("文字点与内容", EditorStyles.boldLabel);
            FlowRoomTemplate template = FlowEnemySpawnAuthoring.ResolveTemplate(variant);
            if (template == null) { EditorGUILayout.HelpBox("先指定所属箱庭及地图模板，才能读取文字点。", MessageType.Info); return; }
            if (_refresh || _template != template) { _template = template; _points = FlowTextAuthoring.Points(template); _refresh = false; }
            EditorGUILayout.HelpBox("位置与朝向保存在地图模板，文字和播放参数仅写入当前差分。预加载不播放；一次性文字每次访问触发一次，死亡重建后可重播。", MessageType.Info);
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("刷新并同步文字点")) { FlowTextPreview.Stop(); FlowTextAuthoring.Synchronize(variant, _points, false); serialized.Update(); }
                    if (GUILayout.Button("新增文字点")) { FlowTextPreview.Stop(); FlowTextAuthoring.AddPoint(variant); GUIUtility.ExitGUI(); }
                    if (GUILayout.Button("打开地图模板")) { FlowEnemySpawnAuthoring.OpenTemplate(variant); GUIUtility.ExitGUI(); }
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("预览当前差分文字")) { FlowTextPreview.Start(variant, false); GUIUtility.ExitGUI(); }
                    if (GUILayout.Button("播放文字效果")) { FlowTextPreview.Start(variant, true); GUIUtility.ExitGUI(); }
                    if (GUILayout.Button("结束预览")) FlowTextPreview.Stop();
                }
                if (_points.Any(point => string.IsNullOrWhiteSpace(point.Id)) || _points.Select(point => point.Id).Distinct().Count() != _points.Length)
                {
                    EditorGUILayout.HelpBox("文字点存在缺失或重复编号。修复保留已有编号的第一个点，其余分配新编号。", MessageType.Warning);
                    if (GUILayout.Button("修复文字点缺失／重复编号"))
                    {
                        FlowTextPreview.Stop(); FlowRoomTemplate opened = FlowEnemySpawnAuthoring.OpenTemplate(variant);
                        if (opened != null) FlowTextAuthoring.RepairIds(opened, variant.Room);
                        Invalidate(); GUIUtility.ExitGUI();
                    }
                }
                DrawRows(variant, serialized);
            }
            foreach (string error in FlowTextValidation.Template(template).Concat(FlowTextValidation.Variant(variant, template)))
                EditorGUILayout.HelpBox(error, MessageType.Error);
            if (_points.Length == 0) EditorGUILayout.HelpBox("暂无文字点。点击“新增文字点”，或将文字点预制体拖入地图模板。", MessageType.Info);
        }

        private void DrawRows(RoomVariantDefinition variant, SerializedObject serialized)
        {
            serialized.Update(); SerializedProperty bindings = serialized.FindProperty(nameof(RoomVariantDefinition.Texts));
            var seen = new HashSet<string>();
            foreach (FlowTextPoint point in _points)
            {
                bool valid = !string.IsNullOrWhiteSpace(point.Id) && seen.Add(point.Id);
                SerializedProperty binding = Find(bindings, point.Id);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(!valid))
                        {
                            bool enabled = binding != null && binding.FindPropertyRelative(nameof(FlowTextBinding.Enabled)).boolValue;
                            EditorGUI.BeginChangeCheck();
                            bool next = EditorGUILayout.Toggle(enabled, GUILayout.Width(20));
                            if (EditorGUI.EndChangeCheck())
                            {
                                if (binding == null)
                                {
                                    FlowTextAuthoring.Synchronize(variant, _points, false); serialized.Update();
                                    bindings = serialized.FindProperty(nameof(RoomVariantDefinition.Texts)); binding = Find(bindings, point.Id);
                                }
                                binding.FindPropertyRelative(nameof(FlowTextBinding.Enabled)).boolValue = next;
                            }
                        }
                        string id = point.Id ?? "";
                        string content = binding?.FindPropertyRelative(nameof(FlowTextBinding.Content)).stringValue ?? "未填写";
                        if (content.Length > 36) content = content.Substring(0, 36) + "…";
                        bool expanded = EditorGUILayout.Foldout(_expanded.Contains(id), new GUIContent(point.DisplayName + " · " + content.Replace('\n', ' '),
                            "编号：" + id + "\n模板局部位置：" + _template.transform.InverseTransformPoint(point.transform.position)), true);
                        if (expanded) _expanded.Add(id); else _expanded.Remove(id);
                        if (GUILayout.Button("定位", GUILayout.Width(42))) { FlowTextAuthoring.Locate(variant, id); GUIUtility.ExitGUI(); }
                    }
                    if (binding != null && _expanded.Contains(point.Id ?? ""))
                    {
                        EditorGUI.indentLevel++;
                        EditorGUILayout.LabelField("文字点编号", point.Id);
                        SerializedProperty child = binding.Copy(); SerializedProperty end = child.GetEndProperty();
                        if (child.NextVisible(true))
                            do
                            {
                                if (SerializedProperty.EqualContents(child, end)) break;
                                if (child.name == nameof(FlowTextBinding.PointId) || child.name == nameof(FlowTextBinding.Enabled)) continue;
                                EditorGUILayout.PropertyField(child, FlowEditorLabels.Field(typeof(FlowTextBinding), child.name), true);
                            } while (child.NextVisible(false));
                        EditorGUI.indentLevel--;
                    }
                }
            }
            if (serialized.ApplyModifiedProperties()) { FlowTextPreview.Stop(); EditorUtility.SetDirty(variant); }
            if ((variant.Texts ?? new FlowTextBinding[0]).Any(binding => binding == null || !seen.Contains(binding.PointId ?? "")))
                if (GUILayout.Button("清理失效文字配置")) { FlowTextAuthoring.Synchronize(variant, _points, true); serialized.Update(); }
        }

        private static SerializedProperty Find(SerializedProperty bindings, string id)
        {
            for (int i = 0; i < bindings.arraySize; i++)
            {
                SerializedProperty binding = bindings.GetArrayElementAtIndex(i);
                if (binding.FindPropertyRelative(nameof(FlowTextBinding.PointId)).stringValue == id) return binding;
            }
            return null;
        }
    }
}
