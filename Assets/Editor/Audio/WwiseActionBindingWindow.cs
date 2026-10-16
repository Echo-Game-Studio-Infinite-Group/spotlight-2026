using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Generic editor for ActionId -> AudioActionDefinition / Wwise Event /
/// StreamingAssets source binding. It removes the need to add per-action code
/// or edit WwiseActionBindings.asset by hand.
/// </summary>
public sealed class WwiseActionBindingWindow : EditorWindow
{
    private const string BindingAssetPath =
        "Assets/Resources/Audio/WwiseActionBindings.asset";

    private AudioActionDefinition _definition;
    private string _actionId = string.Empty;
    private string _playEvent = string.Empty;
    private string _stopEvent = string.Empty;
    private string _sourceRelativePath = string.Empty;
    private Vector2 _scroll;

    [MenuItem("超高速行者/音频/Wwise 动作绑定管理器")]
    public static void Open(
        AudioActionDefinition initialDefinition = null)
    {
        WwiseActionBindingWindow window =
            GetWindow<WwiseActionBindingWindow>("Wwise 动作绑定");
        window.minSize = new Vector2(460f, 360f);
        if (initialDefinition != null)
        {
            window._definition = initialDefinition;
            window.LoadDefinition(initialDefinition);
        }
        else
        {
            window.LoadFromSelection();
        }
    }

    private void OnEnable()
    {
        LoadFromSelection();
    }

    private void OnSelectionChange()
    {
        if (Selection.activeObject is AudioActionDefinition definition)
        {
            _definition = definition;
            LoadDefinition(definition);
            Repaint();
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(6f);
        EditorGUILayout.HelpBox(
            "使用顺序：\n" +
            "1. 选择 AudioActionDefinition\n" +
            "2. 填 ActionId 和 Wwise Play Event\n" +
            "3. 点击“从 Clip 自动生成源 WAV”\n" +
            "4. 点击“创建 / 更新绑定”",
            MessageType.Info);
        AudioActionDefinition previous = _definition;
        _definition = (AudioActionDefinition)EditorGUILayout.ObjectField(
            "AudioActionDefinition",
            _definition,
            typeof(AudioActionDefinition),
            false);
        if (_definition != previous)
        {
            LoadDefinition(_definition);
        }
        if (_definition == null)
        {
            EditorGUILayout.HelpBox(
                "选择或拖入一个 AudioActionDefinition。",
                MessageType.Info);
            return;
        }

        EditorGUI.BeginChangeCheck();
        _actionId = EditorGUILayout.TextField("ActionId", _actionId);
        _playEvent = EditorGUILayout.TextField("Play Event", _playEvent);
        _stopEvent = EditorGUILayout.TextField("Stop Event", _stopEvent);
        _sourceRelativePath = EditorGUILayout.TextField(
            "Source Relative Path",
            _sourceRelativePath);
        if (EditorGUI.EndChangeCheck())
        {
            Repaint();
        }

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("从 Clip 自动生成源 WAV"))
        {
            RegenerateSource();
        }
        if (GUILayout.Button("重新载入绑定"))
        {
            LoadDefinition(_definition);
        }
        EditorGUILayout.EndHorizontal();

        if (string.IsNullOrEmpty(_sourceRelativePath))
        {
            EditorGUILayout.HelpBox(
                "还没有源 WAV。可点击“从 Clip 自动生成源 WAV”。",
                MessageType.Warning);
        }
        else
        {
            EditorGUILayout.HelpBox(
                "源文件：" + _sourceRelativePath,
                MessageType.None);
        }

        EditorGUILayout.Space(6f);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("创建 / 更新绑定"))
        {
            SaveBinding();
        }
        if (GUILayout.Button("删除这个绑定"))
        {
            RemoveBinding();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("现有动态动作绑定", EditorStyles.boldLabel);
        DrawExistingEntries();
    }

    private void LoadFromSelection()
    {
        if (Selection.activeObject is AudioActionDefinition definition)
        {
            _definition = definition;
            LoadDefinition(definition);
        }
    }

    private void LoadDefinition(AudioActionDefinition definition)
    {
        if (definition == null) return;
        WwiseActionBindings bindings = LoadBindings();
        WwiseActionBindings.Entry entry = bindings != null
            ? bindings.Find(definition)
            : null;

        _actionId = entry != null && !string.IsNullOrEmpty(entry.ActionId)
            ? entry.ActionId
            : (!string.IsNullOrEmpty(definition.Id)
                ? definition.Id
                : definition.name);
        _playEvent = entry != null ? entry.PlayEvent : string.Empty;
        _stopEvent = entry != null ? entry.StopEvent : string.Empty;
        _sourceRelativePath = entry != null
            ? entry.SourceRelativePath
            : string.Empty;
        Repaint();
    }

    private void RegenerateSource()
    {
        if (!AudioActionSourceLocator.EnsureFromClip(
                _definition,
                out string relativePath,
                out string error))
        {
            EditorUtility.DisplayDialog("源 WAV 失败", error, "确定");
            return;
        }

        _sourceRelativePath = relativePath;
        Repaint();
    }

    private void SaveBinding()
    {
        if (string.IsNullOrWhiteSpace(_actionId))
        {
            EditorUtility.DisplayDialog(
                "绑定失败",
                "ActionId 不能为空。",
                "确定");
            return;
        }
        if (string.IsNullOrWhiteSpace(_playEvent))
        {
            EditorUtility.DisplayDialog(
                "绑定失败",
                "Play Event 不能为空。",
                "确定");
            return;
        }

        if (string.IsNullOrEmpty(_sourceRelativePath))
        {
            RegenerateSource();
            if (string.IsNullOrEmpty(_sourceRelativePath)) return;
        }

        WwiseActionBindings bindings = EnsureBindings();
        if (bindings == null) return;

        WwiseActionBindings.Entry duplicate =
            bindings.Find(_actionId);
        if (duplicate != null && duplicate.Definition != _definition)
        {
            EditorUtility.DisplayDialog(
                "ActionId 冲突",
                $"ActionId '{_actionId}' 已被 " +
                $"{(duplicate.Definition != null ? duplicate.Definition.name : "<missing>")} 使用。",
                "确定");
            return;
        }

        Undo.RecordObject(bindings, "更新 Wwise 动作绑定");
        var entries = new List<WwiseActionBindings.Entry>(
            bindings.Entries ?? new WwiseActionBindings.Entry[0]);
        int existing = entries.FindIndex(entry =>
            entry != null && entry.Definition == _definition);
        var binding = new WwiseActionBindings.Entry
        {
            ActionId = _actionId.Trim(),
            Definition = _definition,
            PlayEvent = _playEvent.Trim(),
            StopEvent = _stopEvent?.Trim() ?? string.Empty,
            SourceRelativePath = _sourceRelativePath
        };

        if (existing >= 0)
        {
            entries[existing] = binding;
        }
        else
        {
            entries.Add(binding);
        }

        bindings.Entries = entries.ToArray();
        EditorUtility.SetDirty(bindings);
        AssetDatabase.SaveAssets();
        Debug.Log(
            $"Wwise 动作绑定已更新：{binding.ActionId} -> " +
            $"{binding.Definition.name} / {binding.PlayEvent}");
    }

    private void RemoveBinding()
    {
        WwiseActionBindings bindings = LoadBindings();
        if (bindings == null || _definition == null) return;

        var entries = new List<WwiseActionBindings.Entry>(
            bindings.Entries ?? new WwiseActionBindings.Entry[0]);
        int removed = entries.RemoveAll(entry =>
            entry != null && entry.Definition == _definition);
        if (removed == 0) return;

        Undo.RecordObject(bindings, "删除 Wwise 动作绑定");
        bindings.Entries = entries.ToArray();
        EditorUtility.SetDirty(bindings);
        AssetDatabase.SaveAssets();
        _actionId = string.Empty;
        _playEvent = string.Empty;
        _stopEvent = string.Empty;
        _sourceRelativePath = string.Empty;
    }

    private void DrawExistingEntries()
    {
        WwiseActionBindings bindings = LoadBindings();
        if (bindings == null || bindings.Entries == null ||
            bindings.Entries.Length == 0)
        {
            EditorGUILayout.LabelField("暂无绑定。");
            return;
        }

        _scroll = EditorGUILayout.BeginScrollView(
            _scroll,
            GUILayout.MaxHeight(180f));
        for (int i = 0; i < bindings.Entries.Length; i++)
        {
            WwiseActionBindings.Entry entry = bindings.Entries[i];
            if (entry == null) continue;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                $"{entry.ActionId}  ->  " +
                $"{(entry.Definition != null ? entry.Definition.name : "<missing>")}" +
                $"  /  {entry.PlayEvent}");
            if (GUILayout.Button("选择", GUILayout.Width(52f)) &&
                entry.Definition != null)
            {
                Selection.activeObject = entry.Definition;
                EditorGUIUtility.PingObject(entry.Definition);
            }
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();
    }

    private static WwiseActionBindings LoadBindings()
    {
        return AssetDatabase.LoadAssetAtPath<WwiseActionBindings>(
            BindingAssetPath);
    }

    private static WwiseActionBindings EnsureBindings()
    {
        WwiseActionBindings bindings = LoadBindings();
        if (bindings != null) return bindings;

        string directory = Path.GetDirectoryName(BindingAssetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        bindings = ScriptableObject.CreateInstance<WwiseActionBindings>();
        AssetDatabase.CreateAsset(bindings, BindingAssetPath);
        AssetDatabase.SaveAssets();
        return bindings;
    }
}
