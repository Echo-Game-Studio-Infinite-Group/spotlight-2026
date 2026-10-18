using GameJam.Flow;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GameJam.FlowEditor
{
    [CustomEditor(typeof(RoomVariantDefinition)), CanEditMultipleObjects]
    public sealed class RoomVariantDefinitionInspector : FlowConfigInspector
    {
        private FlowRoomTemplate _template;
        private FlowEnemySpawnPoint[] _points = new FlowEnemySpawnPoint[0];
        private bool _refresh = true;

        private void OnEnable()
        {
            EditorApplication.projectChanged += Invalidate;
            EditorApplication.hierarchyChanged += Invalidate;
            Undo.undoRedoPerformed += Invalidate;
        }
        private void OnDisable()
        {
            EditorApplication.projectChanged -= Invalidate;
            EditorApplication.hierarchyChanged -= Invalidate;
            Undo.undoRedoPerformed -= Invalidate;
        }
        private void Invalidate() { _refresh = true; Repaint(); }
        protected override bool ShouldDrawProperty(SerializedProperty property) => property.name != nameof(RoomVariantDefinition.EnemySpawns);

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            EditorGUILayout.Space(); EditorGUILayout.LabelField("出生点与敌人", EditorStyles.boldLabel);
            if (targets.Length != 1)
            { EditorGUILayout.HelpBox("逐点配置敌人时请只选择一个差分。", MessageType.Info); return; }
            var variant = (RoomVariantDefinition)target;
            FlowRoomTemplate template = FlowEnemySpawnAuthoring.ResolveTemplate(variant);
            if (template == null)
            { EditorGUILayout.HelpBox("先指定所属箱庭及其地图模板，才能读取出生点。", MessageType.Info); return; }
            if (_refresh || _template != template)
            { _template = template; _points = template.GetComponentsInChildren<FlowEnemySpawnPoint>(true); _refresh = false; }
            EditorGUILayout.HelpBox("每个启用点每次访问生成一只敌人。新增点默认关闭，配置只写入当前差分。修改点位后请保存地图模板；被内容组关闭的点位不会生成。", MessageType.Info);
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("刷新并同步出生点"))
                    {
                        _points = template.GetComponentsInChildren<FlowEnemySpawnPoint>(true);
                        FlowEnemySpawnAuthoring.Synchronize(variant, _points, false); serializedObject.Update();
                    }
                    if (GUILayout.Button("打开地图模板")) { FlowEnemySpawnAuthoring.OpenTemplate(variant); GUIUtility.ExitGUI(); }
                    if (GUILayout.Button("新增出生点")) { FlowEnemySpawnAuthoring.AddPoint(variant); GUIUtility.ExitGUI(); }
                }
                bool invalidIds = _points.Any(point => string.IsNullOrWhiteSpace(point.Id)) ||
                    _points.Select(point => point.Id).Distinct().Count() != _points.Length;
                if (invalidIds)
                {
                    EditorGUILayout.HelpBox("存在未编号或重复编号的出生点。修复会保留每个已有编号的第一个点，其他点使用新编号。", MessageType.Warning);
                    if (GUILayout.Button("修复缺失／重复编号"))
                    {
                        FlowRoomTemplate opened = FlowEnemySpawnAuthoring.OpenTemplate(variant);
                        if (opened != null) FlowEnemySpawnAuthoring.RepairIds(opened, variant.Room);
                        Invalidate(); GUIUtility.ExitGUI();
                    }
                }
                DrawRows(variant);
            }
            foreach (string error in FlowEnemySpawnValidation.Template(template).Concat(FlowEnemySpawnValidation.Variant(variant, template)))
                EditorGUILayout.HelpBox(error, MessageType.Error);
            if (_points.Length == 0) EditorGUILayout.HelpBox("模板尚无出生点。点击“新增出生点”，或将出生点预制体拖入地图模板。", MessageType.Info);
        }

        private void DrawRows(RoomVariantDefinition variant)
        {
            serializedObject.Update();
            SerializedProperty bindings = serializedObject.FindProperty(nameof(RoomVariantDefinition.EnemySpawns));
            var seen = new HashSet<string>();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("启用", GUILayout.Width(28));
                GUILayout.Label("出生点／备注", GUILayout.Width(157));
                GUILayout.Label("对应敌人预制体");
            }
            foreach (FlowEnemySpawnPoint point in _points)
            {
                bool validId = !string.IsNullOrWhiteSpace(point.Id) && seen.Add(point.Id);
                SerializedProperty binding = FindBinding(bindings, point.Id);
                bool enabled = binding != null && binding.FindPropertyRelative(nameof(FlowEnemySpawnBinding.Enabled)).boolValue;
                GameObject prefab = binding?.FindPropertyRelative(nameof(FlowEnemySpawnBinding.EnemyPrefab)).objectReferenceValue as GameObject;
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    EditorGUI.BeginChangeCheck();
                    bool nextEnabled;
                    GameObject nextPrefab;
                    using (new EditorGUI.DisabledScope(!validId))
                    {
                        nextEnabled = EditorGUILayout.Toggle(new GUIContent("", "是否在本差分中启用此出生点"), enabled, GUILayout.Width(20));
                        using (new EditorGUILayout.VerticalScope(GUILayout.Width(165)))
                        {
                            EditorGUILayout.LabelField(new GUIContent(string.IsNullOrWhiteSpace(point.DisplayName) ? point.name : point.DisplayName,
                                "模板局部位置：" + TemplatePosition(point) + "\n编号：" + point.Id));
                            EditorGUILayout.LabelField(string.IsNullOrWhiteSpace(point.Id) ? "未编号" : point.Id, EditorStyles.miniLabel);
                        }
                        nextPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("", "对应敌人预制体"), prefab, typeof(GameObject), false, GUILayout.MinWidth(90));
                    }
                    if (EditorGUI.EndChangeCheck())
                    {
                        if (binding == null)
                        {
                            int index = bindings.arraySize++; binding = bindings.GetArrayElementAtIndex(index);
                            binding.FindPropertyRelative(nameof(FlowEnemySpawnBinding.PointId)).stringValue = point.Id;
                        }
                        binding.FindPropertyRelative(nameof(FlowEnemySpawnBinding.Enabled)).boolValue = nextEnabled;
                        binding.FindPropertyRelative(nameof(FlowEnemySpawnBinding.EnemyPrefab)).objectReferenceValue = nextPrefab;
                    }
                    if (GUILayout.Button("定位", GUILayout.Width(42)))
                    { FlowEnemySpawnAuthoring.Locate(variant, point); GUIUtility.ExitGUI(); }
                }
            }
            if (serializedObject.ApplyModifiedProperties()) EditorUtility.SetDirty(variant);
            if ((variant.EnemySpawns ?? new FlowEnemySpawnBinding[0]).Any(binding => binding == null || !seen.Contains(binding.PointId ?? string.Empty)))
                if (GUILayout.Button("清理失效配置"))
                { FlowEnemySpawnAuthoring.Synchronize(variant, _points, true); serializedObject.Update(); }
        }

        private Vector3 TemplatePosition(FlowEnemySpawnPoint point) => _template.transform.InverseTransformPoint(point.transform.position);
        private static SerializedProperty FindBinding(SerializedProperty bindings, string id)
        {
            for (int i = 0; i < bindings.arraySize; i++)
            {
                SerializedProperty item = bindings.GetArrayElementAtIndex(i);
                if (item.FindPropertyRelative(nameof(FlowEnemySpawnBinding.PointId)).stringValue == id) return item;
            }
            return null;
        }
    }
}
