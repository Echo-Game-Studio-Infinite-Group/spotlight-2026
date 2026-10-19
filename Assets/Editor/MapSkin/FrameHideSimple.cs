using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// 框架梁视觉隐藏（简单可靠版）：只关 MeshRenderer，保留 GameObject 与 Collider。
    /// 判据：BOX_S_* 下、名字以 L1_Beam_ 开头、最长边 ≥ 20m、最短边 ≤ 2.5m。
    /// 菜单：MapSkin/框架清理/隐藏长梁(保留碰撞) · 恢复
    /// 批处理：-executeMethod MapSkin.FrameHideSimple.Hide -run 1
    /// </summary>
    public static class FrameHideSimple
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string Manifest = "F:/VSCode/taptap-game-jem/_session_audit/FRAME_SIMPLE_Hidden.txt";

        public static void Hide()
        {
            bool run = HasArg("-run", "1");
            OpenScene();
            var targets = new List<Renderer>();
            var lines = new List<string>();
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (!root.name.StartsWith("BOX_S_")) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;
                    var n = r.gameObject.name;
                    if (!n.StartsWith("L1_Beam_")) continue;
                    var b = r.bounds;
                    float max = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                    float min = Mathf.Min(b.size.x, Mathf.Min(b.size.y, b.size.z));
                    if (max < 20f || min > 2.5f) continue;
                    targets.Add(r);
                    lines.Add($"{PathOf(r.transform)} | size=({b.size.x:F1},{b.size.y:F1},{b.size.z:F1})");
                }
            }
            var rep = new StringBuilder();
            rep.AppendLine($"[框架·简单] 命中 {targets.Count} 件（最长边≥20、最短边≤2.5 的 L1_Beam_）");
            if (!run)
            {
                foreach (var l in lines) rep.AppendLine("  " + l);
                rep.AppendLine("干跑：加 -run 1 执行");
                Debug.Log(rep.ToString());
                return;
            }
            foreach (var r in targets) r.enabled = false;
            File.WriteAllLines(Manifest, lines);
            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            rep.AppendLine($"已关闭 {targets.Count} 个 Renderer（Collider 未动），manifest → {Manifest}");
            Debug.Log(rep.ToString());
        }

        public static void Unhide()
        {
            OpenScene();
            if (!File.Exists(Manifest)) { Debug.LogWarning("[框架·简单] 没有 manifest"); return; }
            var names = new HashSet<string>(File.ReadAllLines(Manifest));
            int n = 0;
            foreach (var t in Object.FindObjectsOfType<Transform>(true))
            {
                if (t == null) continue;
                var p = PathOf(t);
                if (!names.Contains(p)) continue;
                var r = t.GetComponent<Renderer>();
                if (r != null && !r.enabled) { r.enabled = true; n++; }
            }
            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[框架·简单] 恢复 {n} 件");
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var c = t; c != null; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static bool HasArg(string k, string v)
        {
            var a = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
                if (a[i] == k && i + 1 < a.Length && a[i + 1] == v) return true;
            return false;
        }

        static void OpenScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }
    }
}
