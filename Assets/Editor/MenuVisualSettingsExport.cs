using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class MenuVisualSettingsExport
{
    private const string Folder = "Assets/Art/UI/CRT/SettingsExport";

    [MenuItem("超高速行者/UI/导出当前菜单视觉设置")]
    private static void ExportCurrent()
    {
        foreach (Canvas canvas in Object.FindObjectsOfType<Canvas>(true))
            if (canvas.isRootCanvas) Export(canvas.gameObject);
        Debug.Log("菜单视觉设置已导出到 " + Folder);
    }

    public static void Export(GameObject root)
    {
        Directory.CreateDirectory(Folder);
        var entries = new List<string>();
        var materials = new HashSet<Material>();
        foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
        {
            string path = root.name + "/" + AnimationUtility.CalculateTransformPath(node, root.transform);
            foreach (Component component in node.GetComponents<Component>())
            {
                if (component == null) continue;
                entries.Add("{\"path\":" + Quote(path) + ",\"activeSelf\":" +
                    (node.gameObject.activeSelf ? "true" : "false") + ",\"type\":" +
                    Quote(component.GetType().FullName) + ",\"settings\":" + EditorJsonUtility.ToJson(component, true) + "}");
            }
            foreach (Graphic graphic in node.GetComponents<Graphic>())
                if (graphic.material != null) materials.Add(graphic.material);
        }
        foreach (Material material in materials)
            entries.Add("{\"material\":" + Quote(AssetDatabase.GetAssetPath(material)) +
                ",\"shader\":" + Quote(material.shader.name) + ",\"settings\":" + EditorJsonUtility.ToJson(material, true) + "}");
        // 保留 Unity 原始序列化字段，包括隐藏组件与材质参数，便于和 Inspector 对照。
        File.WriteAllText(Folder + "/" + root.name + ".json", "[\n" + string.Join(",\n", entries) + "\n]", new UTF8Encoding(false));
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}
