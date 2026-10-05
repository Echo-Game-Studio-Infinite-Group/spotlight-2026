using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace GameJam.Actions.Editor
{
    public sealed class ActionSequenceWindow : EditorWindow
    {
        [SerializeField] private ActionCatalog _catalog;
        [SerializeField] private ActionDefinition _selected;
        [SerializeField] private int _tab;
        [SerializeField] private string _search;
        [SerializeField] private int _selectedWindow = -1;
        private SerializedObject _actionSerialized;
        private SerializedObject _catalogSerialized;
        private ActionDefinition _addExisting;
        private Vector2 _listScroll;
        private Vector2 _detailScroll;
        private ActionCancelGraphView _graph;
        private ActionSequencePreview _preview;
        private List<ActionValidationIssue> _issues = new List<ActionValidationIssue>();
        private bool _cacheDirty = true;
        private float _scrub;
        private Vector2 _graphSize;
        private bool _fitGraph;

        [MenuItem("超高速行者/动作序列配表")]
        public static void Open() => Show(null, null);

        public static ActionSequenceWindow Show(ActionCatalog catalog, ActionDefinition action)
        {
            ActionSequenceWindow window = GetWindow<ActionSequenceWindow>("动作序列配表");
            window.minSize = new Vector2(960, 650);
            if (catalog != null) window.SetCatalog(catalog);
            if (action != null) window.SelectAction(action, -1);
            window.Show();
            return window;
        }

        private void OnEnable()
        {
            _graph = new ActionCancelGraphView();
            Undo.undoRedoPerformed += Changed;
            EditorApplication.projectChanged += Changed;
            EditorApplication.update += UpdatePreview;
            if (_catalog == null)
            {
                string[] guids = AssetDatabase.FindAssets("t:ActionCatalog");
                if (guids.Length > 0) _catalog = AssetDatabase.LoadAssetAtPath<ActionCatalog>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }
            SetCatalog(_catalog);
        }
        private void OnDisable()
        {
            _graph?.SaveLayout();
            _preview?.Reset();
            Undo.undoRedoPerformed -= Changed;
            EditorApplication.projectChanged -= Changed;
            EditorApplication.update -= UpdatePreview;
        }
        private void Changed()
        {
            _cacheDirty = true;
            _preview?.Reset();
            if (_catalog == null)
            {
                _preview = null;
                _catalogSerialized = null;
                SelectAction(null, -1);
            }
            Repaint();
        }
        private void UpdatePreview() { if (_preview != null && _preview.Update()) Repaint(); }
        private void SetCatalog(ActionCatalog catalog)
        {
            _preview?.Reset();
            _catalog = catalog;
            _catalogSerialized = catalog != null ? new SerializedObject(catalog) : null;
            _preview = catalog != null ? new ActionSequencePreview(catalog) : null;
            if (_selected == null || catalog == null || !catalog.Actions.Contains(_selected))
                SelectAction(catalog != null && catalog.Actions.Count > 0 ? catalog.Actions[0] : null, -1);
            _cacheDirty = true;
            _fitGraph = true;
        }
        private void SelectAction(ActionDefinition action, int window)
        {
            bool changed = _selected != action;
            _selected = action;
            _selectedWindow = window;
            _actionSerialized = action != null ? new SerializedObject(action) : null;
            if (changed) { _detailScroll = Vector2.zero; _scrub = 0; }
            if (window >= 0 && _actionSerialized != null)
            {
                SerializedProperty windows = _actionSerialized.FindProperty("CancelWindows");
                if (window < windows.arraySize)
                {
                    windows.isExpanded = true;
                    windows.GetArrayElementAtIndex(window).isExpanded = true;
                }
            }
            Repaint();
        }
        private void RefreshCache()
        {
            if (!_cacheDirty) return;
            _issues = ActionCatalogValidator.Validate(_catalog);
            _graph.Refresh(_catalog, ActionCancelGraph.Build(_catalog));
            _cacheDirty = false;
        }

        private void OnGUI()
        {
            DrawToolbar();
            if (_catalog == null)
            {
                EditorGUILayout.HelpBox("先新建动作集，或创建独立示例。所有配置均为资产，不自动挂到当前角色。", MessageType.Info);
                return;
            }
            RefreshCache();
            EditorGUILayout.BeginHorizontal();
            DrawActionList();
            EditorGUILayout.BeginVertical();
            _tab = GUILayout.Toolbar(_tab, new[] { "动作配表", "取消链有向图", "独立执行预览", "动作集与检查" });
            if (_tab == 1) DrawGraph();
            else if (_tab == 2)
            {
                bool invalid = _issues.Exists(issue => issue.Level == ActionValidationIssue.Severity.Error);
                if (invalid) EditorGUILayout.HelpBox("配置存在错误，先在「动作集与检查」修复后运行沙盒。", MessageType.Error);
                else _preview.Draw(_selected);
            }
            else
            {
                _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);
                if (_tab == 0) DrawAction(); else DrawCatalog();
                EditorGUILayout.EndScrollView();
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            if (Event.current.type == EventType.Used) Repaint();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            ActionCatalog chosen = (ActionCatalog)EditorGUILayout.ObjectField(_catalog, typeof(ActionCatalog), false, GUILayout.MinWidth(180));
            if (chosen != _catalog) SetCatalog(chosen);
            if (GUILayout.Button("新建动作集", EditorStyles.toolbarButton, GUILayout.Width(90))) CreateCatalog();
            using (new EditorGUI.DisabledScope(_catalog == null))
            {
                if (GUILayout.Button("新建动作", EditorStyles.toolbarButton, GUILayout.Width(80))) CreateAction();
                if (GUILayout.Button("保存", EditorStyles.toolbarButton, GUILayout.Width(55))) Save();
                if (GUILayout.Button("全量检查", EditorStyles.toolbarButton, GUILayout.Width(75))) { _cacheDirty = true; _tab = 3; }
            }
            if (GUILayout.Button("打开 / 创建示例", EditorStyles.toolbarButton, GUILayout.Width(120))) SetCatalog(ActionSequenceExamples.Create());
            EditorGUILayout.EndHorizontal();
        }
        private void DrawActionList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(220));
            EditorGUILayout.LabelField(_catalog.DisplayName, EditorStyles.boldLabel);
            _search = EditorGUILayout.TextField(_search ?? "", EditorStyles.toolbarSearchField);
            _listScroll = EditorGUILayout.BeginScrollView(_listScroll);
            foreach (ActionDefinition action in _catalog.Actions)
            {
                if (action == null) { EditorGUILayout.LabelField("空动作引用", EditorStyles.miniLabel); continue; }
                if (!string.IsNullOrEmpty(_search) && action.Label.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (action.ActionId ?? "").IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool error = _issues.Exists(issue => issue.Action == action && issue.Level == ActionValidationIssue.Severity.Error);
                string text = (error ? "! " : "") + action.Label + "\n" + action.ActionId;
                var style = new GUIStyle(EditorStyles.miniButton) { alignment = TextAnchor.MiddleLeft, fontStyle = action == _selected ? FontStyle.Bold : FontStyle.Normal };
                Color previous = GUI.backgroundColor;
                if (action == _selected) GUI.backgroundColor = new Color(0.4f, 0.75f, 1);
                if (GUILayout.Button(text, style, GUILayout.Height(44))) SelectAction(action, -1);
                GUI.backgroundColor = previous;
            }
            EditorGUILayout.EndScrollView();
            _addExisting = (ActionDefinition)EditorGUILayout.ObjectField("已有动作", _addExisting, typeof(ActionDefinition), false);
            using (new EditorGUI.DisabledScope(_addExisting == null || _catalog.Actions.Contains(_addExisting)))
                if (GUILayout.Button("添加引用"))
                {
                    Undo.RecordObject(_catalog, "添加动作引用");
                    _catalog.Actions.Add(_addExisting);
                    EditorUtility.SetDirty(_catalog);
                    SelectAction(_addExisting, -1);
                    Changed();
                }
            using (new EditorGUI.DisabledScope(_selected == null || !_catalog.Actions.Contains(_selected)))
                if (GUILayout.Button("从动作集移除引用"))
                {
                    Undo.RecordObject(_catalog, "移除动作引用");
                    _catalog.Actions.Remove(_selected);
                    EditorUtility.SetDirty(_catalog);
                    SelectAction(_catalog.Actions.Count > 0 ? _catalog.Actions[0] : null, -1);
                    Changed();
                }
            EditorGUILayout.HelpBox("移除只改变动作集引用。Ctrl+Z 可撤销配表修改。", MessageType.None);
            EditorGUILayout.EndVertical();
        }
        private void DrawAction()
        {
            if (_selected == null) { EditorGUILayout.HelpBox("请选择或新建动作。", MessageType.Info); return; }
            EditorGUILayout.LabelField(_selected.Label + " · " + _selected.TotalFrames + " 玩家动作帧", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("在 Project 中定位", GUILayout.Width(140))) EditorGUIUtility.PingObject(_selected);
            _scrub = EditorGUILayout.Slider("预览帧", _scrub, 0, Mathf.Max(1, _selected.TotalFrames));
            EditorGUILayout.EndHorizontal();
            ActionTimelineView.Draw(_selected, _scrub, _selectedWindow, index => SelectAction(_selected, index));
            foreach (ActionValidationIssue issue in _issues)
                if (issue.Action == _selected) EditorGUILayout.HelpBox(issue.Message, issue.Level == ActionValidationIssue.Severity.Error ? MessageType.Error : MessageType.Warning);
            EditorGUILayout.HelpBox("取消窗口采用左闭右开区间；有效窗口授予权限的并集。条件键由未来接入方解释。预输入使用输入采样帧，时间轴与取消窗口使用玩家动作帧。", MessageType.None);
            if (_actionSerialized == null || _actionSerialized.targetObject != _selected) _actionSerialized = new SerializedObject(_selected);
            if (ActionAuthoringGUI.Draw(_actionSerialized, _selected, _selectedWindow)) Changed();
        }
        private void DrawGraph()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            _graph.ShowLabels = GUILayout.Toggle(_graph.ShowLabels, "窗口标签", EditorStyles.toolbarButton);
            _graph.OnlyRelated = GUILayout.Toggle(_graph.OnlyRelated, "仅选中相关", EditorStyles.toolbarButton);
            if (GUILayout.Button("自动布局", EditorStyles.toolbarButton)) _graph.AutoLayout();
            if (GUILayout.Button("适配视图", EditorStyles.toolbarButton)) _graph.Fit(_graphSize);
            if (GUILayout.Button("导出 SVG", EditorStyles.toolbarButton))
            {
                string file = EditorUtility.SaveFilePanel("导出取消链", "", "动作取消链", "svg");
                if (!string.IsNullOrEmpty(file)) File.WriteAllText(file, _graph.ExportSvg(), new System.Text.UTF8Encoding(false));
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("拖动节点调整布局 · 空白拖动平移 · 滚轮缩放 · 点击边标签选中来源窗口", EditorStyles.miniLabel);
            Rect canvas = GUILayoutUtility.GetRect(200, 200, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            _graphSize = canvas.size;
            if (_fitGraph && Event.current.type == EventType.Repaint)
            {
                _graph.Fit(canvas.size);
                _fitGraph = false;
            }
            _graph.Draw(canvas, _selected, SelectAction);
            if (_selected != null && _selectedWindow >= 0 && _selectedWindow < _selected.CancelWindows.Count)
            {
                ActionCancelWindow window = _selected.CancelWindows[_selectedWindow];
                EditorGUILayout.LabelField(_selected.Label + " → " + window.DisplayName, EditorStyles.boldLabel);
                EditorGUILayout.LabelField("全部：" + string.Join(", ", window.RequireAll) + "  任一：" + string.Join(", ", window.RequireAny), EditorStyles.wordWrappedMiniLabel);
                if (GUILayout.Button("编辑这个窗口", GUILayout.Width(150))) _tab = 0;
            }
        }
        private void DrawCatalog()
        {
            if (_catalogSerialized == null) _catalogSerialized = new SerializedObject(_catalog);
            if (ActionAuthoringGUI.Draw(_catalogSerialized)) { SelectAction(_selected, -1); Changed(); }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("校验结果", EditorStyles.boldLabel);
            if (_issues.Count == 0) EditorGUILayout.HelpBox("全部检查通过。窗口重叠、双向边和取消环均允许。", MessageType.Info);
            foreach (ActionValidationIssue issue in _issues)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox((issue.Action != null ? issue.Action.Label + "：" : "") + issue.Message,
                    issue.Level == ActionValidationIssue.Severity.Error ? MessageType.Error : MessageType.Warning);
                if (issue.Action != null && GUILayout.Button("定位", GUILayout.Width(50))) { SelectAction(issue.Action, issue.WindowIndex); _tab = 0; }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void CreateCatalog()
        {
            ActionSequenceExamples.EnsureFolder("Assets/Settings/ActionSequences");
            string path = EditorUtility.SaveFilePanelInProject("新建动作集", "ActionCatalog", "asset", "选择动作集保存位置", "Assets/Settings/ActionSequences");
            if (string.IsNullOrEmpty(path)) return;
            ActionCatalog catalog = CreateInstance<ActionCatalog>();
            AssetDatabase.CreateAsset(catalog, path);
            Undo.RegisterCreatedObjectUndo(catalog, "新建动作集");
            SetCatalog(catalog);
        }
        private void CreateAction()
        {
            string directory = Path.GetDirectoryName(AssetDatabase.GetAssetPath(_catalog)).Replace('\\', '/');
            string path = EditorUtility.SaveFilePanelInProject("新建动作", "Action", "asset", "选择动作保存位置", directory);
            if (string.IsNullOrEmpty(path)) return;
            ActionDefinition action = CreateInstance<ActionDefinition>();
            action.ActionId = "action_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            action.DisplayName = Path.GetFileNameWithoutExtension(path);
            action.Timeline.Add(new ActionSegment { DisplayName = "发生段", DurationFrames = 1 });
            AssetDatabase.CreateAsset(action, path);
            Undo.RegisterCreatedObjectUndo(action, "新建动作");
            Undo.RecordObject(_catalog, "添加动作");
            _catalog.Actions.Add(action);
            EditorUtility.SetDirty(_catalog);
            SelectAction(action, -1);
            Changed();
        }
        private void Save()
        {
            _actionSerialized?.ApplyModifiedProperties();
            _catalogSerialized?.ApplyModifiedProperties();
            AssetDatabase.SaveAssetIfDirty(_catalog);
            foreach (ActionDefinition action in _catalog.Actions) if (action != null) AssetDatabase.SaveAssetIfDirty(action);
            _graph.SaveLayout();
            ShowNotification(new GUIContent("动作配置已保存"));
        }

        [OnOpenAsset]
        public static bool OpenAsset(int instanceId, int line)
        {
            UnityEngine.Object asset = EditorUtility.InstanceIDToObject(instanceId);
            if (asset is ActionCatalog catalog) { Show(catalog, null); return true; }
            if (!(asset is ActionDefinition action)) return false;
            foreach (string guid in AssetDatabase.FindAssets("t:ActionCatalog"))
            {
                ActionCatalog found = AssetDatabase.LoadAssetAtPath<ActionCatalog>(AssetDatabase.GUIDToAssetPath(guid));
                if (found != null && found.Actions.Contains(action)) { Show(found, action); return true; }
            }
            return false;
        }
    }
}
