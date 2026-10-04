using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Xml.Linq;
using GameJam.Actions;
using GameJam.Actions.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class ActionAuthoringTests
{
    private string _folder;
    [SetUp]
    public void SetUp()
    {
        _folder = "Assets/__ActionSequenceTests_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", _folder.Substring("Assets/".Length));
    }
    [TearDown]
    public void TearDown()
    {
        if (_folder.StartsWith("Assets/__ActionSequenceTests_", StringComparison.Ordinal)) AssetDatabase.DeleteAsset(_folder);
    }

    [Test]
    public void ExampleCatalogIsValidAndDoesNotAttachComponents()
    {
        int before = Resources.FindObjectsOfTypeAll<PlayerCombat>().Length;
        ActionCatalog catalog = ActionSequenceExamples.Create(_folder);
        List<ActionValidationIssue> issues = ActionCatalogValidator.Validate(catalog);
        Assert.IsFalse(issues.Exists(issue => issue.Level == ActionValidationIssue.Severity.Error), string.Join("\n", issues.ConvertAll(issue => issue.Message)));
        Assert.AreEqual(10, catalog.Actions.Count);
        Assert.AreEqual(before, Resources.FindObjectsOfTypeAll<PlayerCombat>().Length);
        Assert.Greater(ActionCancelGraph.Build(catalog).Count, 10);
        Assert.AreEqual(ActionPhase.Startup, catalog.Actions[0].Timeline[0].Phase);
        Assert.AreEqual(ActionPhase.Startup, catalog.Actions[0].Timeline[1].Phase);
    }

    [Test]
    public void ExampleCreationPreservesExistingEdits()
    {
        ActionCatalog first = ActionSequenceExamples.Create(_folder);
        first.Actions[0].Input.PreInputFrames = 19;
        ActionCatalog second = ActionSequenceExamples.Create(_folder);
        Assert.AreSame(first, second); Assert.AreEqual(19, second.Actions[0].Input.PreInputFrames);
    }

    [Test]
    public void SerializedEditingSupportsUndoAndDiskReload()
    {
        ActionCatalog catalog = ActionSequenceExamples.Create(_folder);
        ActionDefinition action = catalog.Actions[0];
        int original = action.Input.PreInputFrames;
        Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
        var serialized = new SerializedObject(action);
        serialized.FindProperty("Input").FindPropertyRelative("PreInputFrames").intValue = 17;
        serialized.ApplyModifiedProperties();
        Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(group); Undo.PerformUndo();
        Assert.AreEqual(original, action.Input.PreInputFrames);
        serialized.Update(); serialized.FindProperty("Input").FindPropertyRelative("PreInputFrames").intValue = 13;
        serialized.ApplyModifiedProperties(); EditorUtility.SetDirty(action);
        string path = AssetDatabase.GetAssetPath(action); AssetDatabase.SaveAssetIfDirty(action);
        string yaml = File.ReadAllText(path); StringAssert.Contains("PreInputFrames: 13", yaml);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        ActionDefinition loaded = AssetDatabase.LoadAssetAtPath<ActionDefinition>(path);
        Assert.AreEqual(13, loaded.Input.PreInputFrames);
        Assert.AreEqual(action.CancelWindows[0].Targets[0], loaded.CancelWindows[0].Targets[0]);
    }

    [Test]
    public void GraphSvgExportsArrowPathsAndConditionalWindowLabels()
    {
        ActionCatalog catalog = ActionSequenceExamples.Create(_folder);
        ActionDefinition action = catalog.Actions[0];
        action.CancelWindows.Add(new ActionCancelWindow { DisplayName = "自取消窗口甲", Targets = new List<ActionDefinition> { action } });
        action.CancelWindows.Add(new ActionCancelWindow { DisplayName = "自取消窗口乙", Targets = new List<ActionDefinition> { action } });
        var graph = new ActionCancelGraphView();
        List<ActionCancelEdge> edges = ActionCancelGraph.Build(catalog);
        graph.Refresh(catalog, edges);
        XDocument svg = XDocument.Parse(graph.ExportSvg());
        XNamespace ns = "http://www.w3.org/2000/svg";
        Assert.AreEqual(edges.Count, Count(svg.Descendants(ns + "path"), element => element.Attribute("marker-end") != null));
        string text = svg.ToString(); StringAssert.Contains("parry", text); StringAssert.Contains("闪斩", text);
        var loops = new List<string>();
        foreach (XElement path in svg.Descendants(ns + "path"))
            if (path.Element(ns + "title")?.Value.Contains("自取消窗口") == true) loops.Add(path.Attribute("d").Value);
        Assert.AreEqual(2, loops.Count);
        Assert.AreNotEqual(loops[0], loops[1], "重叠的自取消窗口也需要独立的可见有向环");
        string[] bounds = svg.Root.Attribute("viewBox").Value.Split(' ');
        Assert.Less(float.Parse(bounds[1], System.Globalization.CultureInfo.InvariantCulture), -90, "导出范围必须包含节点上方的取消环");
    }

    [UnityTest]
    public IEnumerator AuthoringWindowRendersAllTabsWithoutErrors()
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            Assert.Ignore("界面绘制检查需要图形设备，EditMode 验证脚本默认启用图形设备");
        ActionCatalog catalog = ActionSequenceExamples.Create(_folder);
        ActionSequenceWindow window = ActionSequenceWindow.Show(catalog, catalog.Actions[0]);
        window.position = new Rect(30, 30, 1100, 760);
        try
        {
            for (int tab = 0; tab < 4; tab++)
            {
                var serialized = new SerializedObject(window);
                serialized.FindProperty("_tab").intValue = tab;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                window.Repaint();
                yield return null;
                yield return null;
            }
        }
        finally { window.Close(); }
    }
    private static int Count(IEnumerable<XElement> elements, Func<XElement, bool> predicate)
    { int count = 0; foreach (XElement element in elements) if (predicate(element)) count++; return count; }
}
