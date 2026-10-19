using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// 把 Hierarchy 里选中的对象导出成可读清单（剪贴板 + 文件），给主控定位要改的件。
    /// 输出每行：完整层级路径 | 世界中心 | 世界尺寸 | 材质名 | 三角形数
    /// 菜单：MapSkin/工具/复制选中对象清单
    /// </summary>
    public static class SelectionGrab
    {
        const string OutPath = "F:/VSCode/taptap-game-jem/_session_audit/selection.txt";

        [MenuItem("MapSkin/工具/复制选中对象清单")]
        public static void Grab()
        {
            var sel = Selection.gameObjects;
            if (sel == null || sel.Length == 0)
            {
                Debug.LogWarning("[选择] Hierarchy 里没有选中对象");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# 选中 {sel.Length} 个根对象（含子件） {System.DateTime.Now:yyyy-MM-dd HH:mm}");
            var seen = new HashSet<int>();
            foreach (var root in sel)
            {
                if (root == null) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;
                    if (!seen.Add(r.GetInstanceID())) continue;
                    var b = r.bounds;
                    string mat = r.sharedMaterial != null ? r.sharedMaterial.name : "-";
                    int tris = 0;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) tris = mf.sharedMesh.triangles.Length / 3;
                    sb.AppendLine($"{PathOf(r.transform)} | c=({b.center.x:F2},{b.center.y:F2},{b.center.z:F2}) | " +
                                  $"size=({b.size.x:F2},{b.size.y:F2},{b.size.z:F2}) | mat={mat} | {tris} tri");
                }
            }

            string text = sb.ToString();
            Directory.CreateDirectory(Path.GetDirectoryName(OutPath));
            File.WriteAllText(OutPath, text);
            EditorGUIUtility.systemCopyBuffer = text;
            Debug.Log($"[选择] 已复制 {sel.Length} 个根对象（含子件）到剪贴板，并写入 {OutPath}\n{text}");
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var c = t; c != null; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
