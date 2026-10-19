// ArtInventory.cs — 只读出图用的场景清单（Editor-only，不写场景）
//
// 目的：在改美术前把「每个区块有哪些可见件、尺寸多少、用的什么材质、是不是我们已经
//       换过的 MapSkin 材质」一次性落盘，避免反复凭记忆猜对象。
//
// 输出：F:/VSCode/taptap-game-jem/_session_audit/art_inventory.txt
// 菜单：MapSkin/工具/美术清单
// 批处理：-executeMethod MapSkin.ArtInventory.Run
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    public static class ArtInventory
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string OutPath = "F:/VSCode/taptap-game-jem/_session_audit/art_inventory.txt";

        [MenuItem("MapSkin/工具/美术清单")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var sb = new StringBuilder();
            sb.AppendLine($"# Level1 美术清单 {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"# 渲染件总数 {UnityEngine.Object.FindObjectsOfType<Renderer>().Length}");
            sb.AppendLine();

            // 灯光
            sb.AppendLine("== 灯光 ==");
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>().OrderBy(l => l.type).ThenBy(l => l.gameObject.name))
            {
                sb.AppendLine($"  {l.type,-11} {l.gameObject.name,-28} I={l.intensity,7:F1} " +
                              $"range={l.range,6:F1} col=({l.color.r:F2},{l.color.g:F2},{l.color.b:F2}) " +
                              $"shadows={l.shadows} pos={Fmt(l.transform.position)}");
            }
            sb.AppendLine();

            // 反射探针
            sb.AppendLine("== 反射探针 ==");
            foreach (var rp in UnityEngine.Object.FindObjectsOfType<ReflectionProbe>())
                sb.AppendLine($"  {rp.gameObject.name} mode={rp.mode} res={rp.resolution} " +
                              $"pos={Fmt(rp.transform.position)} size={Fmt(rp.size)} intensity={rp.intensity}");
            sb.AppendLine();

            // 区块
            var blocks = scene.GetRootGameObjects().Where(g => g.name.StartsWith("BOX_S_")).OrderBy(g => g.name);
            foreach (var b in blocks)
            {
                sb.AppendLine($"===== {b.name} =====");
                var rs = b.GetComponentsInChildren<Renderer>(true)
                          .Where(r => r.enabled && r.gameObject.activeInHierarchy)
                          .OrderByDescending(r => r.bounds.size.x * r.bounds.size.y + r.bounds.size.z * r.bounds.size.y)
                          .ToList();
                sb.AppendLine($"  渲染件 {rs.Count}");
                foreach (var r in rs.Take(80))
                {
                    var m = r.sharedMaterial;
                    string matName = m != null ? m.name : "<无>";
                    string shader = m != null && m.shader != null ? m.shader.name : "-";
                    string tags = "";
                    if (matName.StartsWith("T_CUSTOM") || matName.Contains("MetalWall") ||
                        matName.Contains("RackWall") || matName.Contains("DECK_") || matName.Contains("Gate") ||
                        matName.Contains("Spawn") || matName.Contains("Goal"))
                        tags = "  *SKIN*";
                    sb.AppendLine($"    {r.gameObject.name,-52} size={Fmt(r.bounds.size)} c={Fmt(r.bounds.center)} " +
                                  $"mat={matName} [{shader}]{tags}");
                }
                if (rs.Count > 80) sb.AppendLine($"    ... 省略 {rs.Count - 80} 件");
                sb.AppendLine();
            }

            // S_04 地面专项（只读顶层，不含 DEC_Kit 子件）
            sb.AppendLine("== S_04 地面原始件（排除 DEC_Kit 视觉层） ==");
            var s04 = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "BOX_S_04");
            if (s04 != null)
            {
                foreach (var r in s04.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.enabled) continue;
                    var t = r.transform;
                    bool underKit = false;
                    for (var p = t; p != null; p = p.parent)
                        if (p.name.StartsWith("DEC_Kit")) { underKit = true; break; }
                    if (underKit) continue;
                    // 只列水平大件（地面）
                    var s = r.bounds.size;
                    if (s.x * s.z < 40f) continue;
                    sb.AppendLine($"  {Hier(r.transform),-70} pos={Fmt(t.position)} rotY={t.eulerAngles.y:F1} " +
                                  $"size={Fmt(s)} mat={(r.sharedMaterial ? r.sharedMaterial.name : "无")}");
                    sb.AppendLine($"      localScale={Fmt(t.localScale)} lossy={Fmt(t.lossyScale)}");
                }
            }

            // 六边形相关
            sb.AppendLine();
            sb.AppendLine("== 所有大水平面（enabled + disabled 都列，面积≥100m²） ==");
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
            {
                var b = r.bounds;
                if (b.size.x * b.size.z < 100f) continue;
                if (b.size.y > Mathf.Min(b.size.x, b.size.z) * 0.6f) continue;
                sb.AppendLine($"  {(r.enabled ? "ON " : "OFF")} {Hier(r.transform),-72} c={Fmt(b.center)} size={Fmt(b.size)} " +
                              $"mat={(r.sharedMaterial ? r.sharedMaterial.name : "无")}");
            }

            sb.AppendLine();
            sb.AppendLine("== 大水平 Collider（面积≥100m²，用于确认地面碰撞还在） ==");
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider>(true))
            {
                var b = c.bounds;
                if (b.size.x * b.size.z < 100f) continue;
                if (b.size.y > Mathf.Min(b.size.x, b.size.z) * 0.6f) continue;
                sb.AppendLine($"  {Hier(c.transform),-72} c={Fmt(b.center)} size={Fmt(b.size)} type={c.GetType().Name}");
            }

            sb.AppendLine();
            sb.AppendLine("== 名字含 Hex / 六边 的件 ==");
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t.name.IndexOf("hex", StringComparison.OrdinalIgnoreCase) < 0 &&
                    t.name.IndexOf("六边", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (t.GetComponent<Renderer>() == null) continue;
                sb.AppendLine($"  {Hier(t),-60} pos={Fmt(t.position)} size={Fmt(t.GetComponent<Renderer>().bounds.size)} " +
                              $"mat={(t.GetComponent<Renderer>().sharedMaterial ? t.GetComponent<Renderer>().sharedMaterial.name : "无")}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(OutPath) ?? ".");
            File.WriteAllText(OutPath, sb.ToString(), Encoding.UTF8);
            Debug.Log($"[ArtInventory] -> {OutPath}");
        }

        static string Fmt(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";

        static string Hier(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null; p = p.parent) parts.Add(p.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
