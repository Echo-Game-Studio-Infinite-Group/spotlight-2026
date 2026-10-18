using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GameJam.Flow;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameJam.FlowEditor
{
    public sealed class FlowConfigWindow : EditorWindow
    {
        [SerializeField] private RunFlowConfig _config;
        private readonly string[] _tabs = { "全局流程", "箱庭", "差分", "地图池", "循环", "生成预览", "检查" };
        private int _tab, _seed = 2026, _chaos, _previewLength = 16, _visitPreview;
        private Vector2 _scroll, _listScroll;
        private Object _selected;
        private UnityEditor.Editor _editor;
        private List<Object> _assets = new List<Object>();
        private List<string> _messages = new List<string>();
        private readonly List<string> _preview = new List<string>();

        [MenuItem("超高速行者/流程与地图/配表窗口")]
        public static void Open() => GetWindow<FlowConfigWindow>("流程与地图配表");
        private void OnEnable()
        {
            minSize = new Vector2(850f, 540f);
            if (_config == null) _config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>(FlowExampleSetup.ConfigPath);
            RefreshAssets(); EditorApplication.projectChanged += RefreshAssets;
        }
        private void OnDisable() { EditorApplication.projectChanged -= RefreshAssets; if (_editor != null) DestroyImmediate(_editor); }
        private void RefreshAssets() { _assets = FlowConfigAssets.Collect(_config); Repaint(); }

        private void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            _config = (RunFlowConfig)EditorGUILayout.ObjectField("全局流程配置", _config, typeof(RunFlowConfig), false);
            if (EditorGUI.EndChangeCheck()) { _selected = null; RefreshAssets(); }
            if (_config == null)
            {
                EditorGUILayout.HelpBox("先生成白盒样例，或创建全局流程配置。", MessageType.Info);
                if (GUILayout.Button("生成白盒配表示例")) { FlowExampleSetup.Build(); _config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>(FlowExampleSetup.ConfigPath); RefreshAssets(); }
                return;
            }
            int tab = GUILayout.Toolbar(_tab, _tabs);
            if (tab != _tab) { _tab = tab; _selected = null; }
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_tab == 5) DrawPreview();
            else if (_tab == 6) DrawValidation();
            else
            {
                EditorGUILayout.BeginHorizontal();
                _listScroll = EditorGUILayout.BeginScrollView(_listScroll, GUILayout.Width(250));
                foreach (Object asset in _assets.Where(MatchesTab))
                    if (GUILayout.Toggle(_selected == asset, new GUIContent(FlowEditorLabels.Asset(asset), AssetDatabase.GetAssetPath(asset)), "Button")) _selected = asset;
                EditorGUILayout.EndScrollView();
                EditorGUILayout.BeginVertical();
                if (_selected == null) _selected = _assets.FirstOrDefault(MatchesTab);
                if (_selected != null)
                {
                    UnityEditor.Editor.CreateCachedEditor(_selected, null, ref _editor);
                    _editor.OnInspectorGUI();
                    if (_selected is RoomPoolDefinition pool) DrawWeights(pool);
                    if (GUILayout.Button("定位资产")) { Selection.activeObject = _selected; EditorGUIUtility.PingObject(_selected); }
                }
                EditorGUILayout.EndVertical(); EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("保存配表")) { AssetDatabase.SaveAssets(); RefreshAssets(); }
            if (GUILayout.Button("导出地图池表格（CSV）")) ExportCsv();
            if (GUILayout.Button("导入地图池表格（CSV）")) ImportCsv();
            if (GUILayout.Button("打开流程白盒")) FlowExampleSetup.Open();
            EditorGUILayout.EndHorizontal();
        }
        private bool MatchesTab(Object value) => _tab == 0 ? value is RunFlowConfig : _tab == 1 ? value is RoomDefinition :
            _tab == 2 ? value is RoomVariantDefinition : _tab == 3 ? value is RoomPoolDefinition : value is LoopRuleDefinition;
        private void DrawWeights(RoomPoolDefinition pool)
        {
            EditorGUILayout.Space(); _visitPreview = Mathf.Clamp(EditorGUILayout.IntField("假设各条目进入次数", _visitPreview), 0, 100);
            _chaos = EditorGUILayout.IntField("概率预览混乱度", _chaos);
            var planner = new RoomSelectionPlanner(_config, _seed);
            for (int i = 0; i < _visitPreview; i++) foreach (RoomPoolEntry entry in pool.Entries) if (entry.Variant != null) planner.RegisterEntry(entry.Variant);
            double total = pool.Entries.Where(entry => entry.Enabled && entry.Variant != null && entry.Variant.Supports(_chaos)).Sum(planner.EffectiveWeight);
            foreach (RoomPoolEntry entry in pool.Entries)
            {
                if (entry.Variant == null) continue;
                double weight = entry.Enabled && entry.Variant.Supports(_chaos) ? planner.EffectiveWeight(entry) : 0;
                EditorGUILayout.LabelField(new GUIContent(FlowEditorLabels.Asset(entry.Variant)), new GUIContent("有效权重 " + weight.ToString("G4") + " / 概率 " + (total > 0 ? weight / total : 0).ToString("P1")));
            }
        }
        private void DrawPreview()
        {
            _seed = EditorGUILayout.IntField("随机种子", _seed); _chaos = EditorGUILayout.IntField("混乱度", _chaos);
            _previewLength = Mathf.Clamp(EditorGUILayout.IntField("预览房间数", _previewLength), 1, 200);
            EditorGUILayout.HelpBox("预览流程抽取及循环；真实空间、碰撞与导航在运行样例中验证。", MessageType.Info);
            if (GUILayout.Button("生成预览"))
            {
                _preview.Clear();
                var errors = FlowConfigValidator.Validate(_config);
                if (errors.Count > 0) { _preview.AddRange(errors); return; }
                var planner = new RoomSelectionPlanner(_config, _seed);
                planner.RegisterEntry(_config.InitialRoom);
                for (int i = 0; i < _previewLength; i++)
                {
                    RoomVariantDefinition next = planner.Plan(_chaos);
                    if (next == null) { _preview.Add("错误：" + planner.LastError); break; }
                    _preview.Add((i + 1) + ". " + FlowEditorLabels.Asset(next) + "　循环=" + (string.IsNullOrEmpty(planner.LoopId) ? "无" : planner.LoopId) + " 序列位置=" + (planner.CycleIndex + 1));
                    planner.CommitSuccessor(); planner.RegisterEntry(next);
                    if (next == _config.FinalRoom) break;
                }
            }
            foreach (string line in _preview) EditorGUILayout.LabelField(line);
        }
        private void DrawValidation()
        {
            if (GUILayout.Button("检查当前配置"))
            {
                _messages = FlowConfigValidator.Validate(_config);
                if (_messages.Count == 0) _messages.Add("通过：配置引用、编号、差分、地图池、循环、门接口、出生点、敌人及文字点检查。");
            }
            foreach (string message in _messages) EditorGUILayout.HelpBox(message, message.StartsWith("通过") ? MessageType.Info : MessageType.Error);
        }
        private void ExportCsv()
        {
            string path = EditorUtility.SaveFilePanel("导出地图池", "", "地图池配表", "csv");
            if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, RoomPoolCsv.Export(_config), new UTF8Encoding(true));
        }
        private void ImportCsv()
        {
            string path = EditorUtility.OpenFilePanel("导入地图池", "", "csv");
            if (string.IsNullOrEmpty(path)) return;
            try { RoomPoolCsv.Import(_config, File.ReadAllText(path)); RefreshAssets(); }
            catch (Exception exception) { _messages = new List<string> { exception.Message }; _tab = 6; }
        }
    }
}
