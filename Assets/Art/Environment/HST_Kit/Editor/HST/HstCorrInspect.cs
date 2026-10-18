#if UNITY_EDITOR
// 走廊场景只读普查器 (2026-10-11)
// 用途: ARCH/FORGE 网格是"顶点烤死世界坐标"(GO 变换全在原点),YAML/清单坐标都不可信 ——
//       唯一权威是运行时的 Renderer.bounds。本工具一次落盘全部几何/灯光/相机实测数据,
//       供后续改动前定基线、改完后做判据对比。**只读, 不写场景。**
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HST
{
    public static class HstCorrInspect
    {
        const string ScenePath = "Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity";
        const string Out = "F:/VSCode/工业级渲染/tmp/corr_inspect.txt";

        [MenuItem("HST/走廊场景普查(只读)")]
        public static void Dump()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var sb = new StringBuilder();
            sb.AppendLine("# 走廊场景普查 " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("# scene=" + scene.name + " path=" + scene.path);
            sb.AppendLine();

            // ---------------- 1. 网格件 ----------------
            var mrs = new List<MeshRenderer>(Object.FindObjectsOfType<MeshRenderer>(true));
            mrs.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            sb.AppendLine("## 1. 网格件 (" + mrs.Count + ")");
            sb.AppendLine("name | parent | center | size | ymin..ymax | mat | static | active");
            foreach (var r in mrs)
            {
                var b = r.bounds;
                sb.AppendLine(string.Format("{0} | {1} | ({2:F3},{3:F3},{4:F3}) | ({5:F3},{6:F3},{7:F3}) | {8:F3}..{9:F3} | {10} | {11} | {12}",
                    r.name, r.transform.parent != null ? r.transform.parent.name : "-",
                    b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z,
                    b.min.y, b.max.y,
                    r.sharedMaterial != null ? r.sharedMaterial.name : "null",
                    GameObjectUtility.GetStaticEditorFlags(r.gameObject),
                    r.gameObject.activeSelf));
            }
            sb.AppendLine();

            // ---------------- 2. 分组汇总(通用前缀) ----------------
            sb.AppendLine("## 2. 分组汇总 (group | count | union bounds center | size)");
            var groups = new Dictionary<string, List<MeshRenderer>>();
            foreach (var r in mrs)
            {
                var key = GroupKey(r.name, 2);
                List<MeshRenderer> lst;
                if (!groups.TryGetValue(key, out lst)) { lst = new List<MeshRenderer>(); groups[key] = lst; }
                lst.Add(r);
            }
            var keys = new List<string>(groups.Keys); keys.Sort();
            foreach (var k in keys)
            {
                var lst = groups[k];
                var b = lst[0].bounds;
                foreach (var r in lst) b.Encapsulate(r.bounds);
                sb.AppendLine(string.Format("{0} | {1} | ({2:F2},{3:F2},{4:F2}) | ({5:F2},{6:F2},{7:F2})",
                    k, lst.Count, b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z));
            }
            sb.AppendLine();

            // ---------------- 3. 地板区诊断(|z|<1.2 且 y<0.35) ----------------
            sb.AppendLine("## 3. 地板中线区物件 (|center.z|<1.2 且 center.y<0.35)");
            foreach (var r in mrs)
            {
                var b = r.bounds;
                if (Mathf.Abs(b.center.z) < 1.2f && b.center.y < 0.35f)
                    sb.AppendLine(string.Format("{0} | c=({1:F3},{2:F3},{3:F3}) | s=({4:F3},{5:F3},{6:F3}) | y {7:F3}..{8:F3} | {9}",
                        r.name, b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z,
                        b.min.y, b.max.y, r.sharedMaterial != null ? r.sharedMaterial.name : "null"));
            }
            sb.AppendLine();

            // ---------------- 4. 天花区诊断 ----------------
            sb.AppendLine("## 4. 天花区物件 (center.y>2.5)");
            foreach (var r in mrs)
            {
                var b = r.bounds;
                if (b.center.y > 2.5f)
                    sb.AppendLine(string.Format("{0} | c=({1:F3},{2:F3},{3:F3}) | s=({4:F3},{5:F3},{6:F3}) | y {7:F3}..{8:F3}",
                        r.name, b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z, b.min.y, b.max.y));
            }
            sb.AppendLine();

            // ---------------- 5. 灯 ----------------
            var lights = new List<Light>(Object.FindObjectsOfType<Light>(true));
            lights.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            sb.AppendLine("## 5. 灯 (" + lights.Count + ")");
            sb.AppendLine("name | type | intensity | range | spot | color | pos | bake | shadows | on");
            foreach (var l in lights)
            {
                var p = l.transform.position;
                sb.AppendLine(string.Format("{0} | {1} | {2:F5} | {3:F3} | {4:F1} | ({5:F2},{6:F2},{7:F2}) | ({8:F3},{9:F3},{10:F3}) | {11} | {12} | {13}",
                    l.name, l.type, l.intensity, l.range, l.spotAngle,
                    l.color.r, l.color.g, l.color.b, p.x, p.y, p.z,
                    l.lightmapBakeType, l.shadows, l.enabled));
            }
            sb.AppendLine();
            sb.AppendLine("## 5b. 灯分组(前缀3段) count / avg intensity / x 跨度 / z 值集合 / y");
            var lg = new Dictionary<string, List<Light>>();
            foreach (var l in lights)
            {
                var key = GroupKey(l.name, 3);
                List<Light> lst;
                if (!lg.TryGetValue(key, out lst)) { lst = new List<Light>(); lg[key] = lst; }
                lst.Add(l);
            }
            var lk = new List<string>(lg.Keys); lk.Sort();
            foreach (var k in lk)
            {
                var lst = lg[k];
                float lsum = 0, lxmin = float.MaxValue, lxmax = float.MinValue;
                var zs = new SortedSet<string>(); float ysum = 0;
                foreach (var l in lst)
                {
                    lsum += l.intensity;
                    var p = l.transform.position;
                    if (p.x < lxmin) lxmin = p.x;
                    if (p.x > lxmax) lxmax = p.x;
                    zs.Add(p.z.ToString("F2")); ysum += p.y;
                }
                sb.AppendLine(string.Format("{0} | n={1} | avgI={2:F5} | x {3:F2}..{4:F2} | z={{{5}}} | avgY={6:F2}",
                    k, lst.Count, lsum / lst.Count, lxmin, lxmax, string.Join(",", zs), ysum / lst.Count));
            }
            sb.AppendLine();

            // ---------------- 6. 相机 ----------------
            var cams = new List<Camera>(Object.FindObjectsOfType<Camera>(true));
            cams.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            sb.AppendLine("## 6. 相机 (" + cams.Count + ")");
            foreach (var c in cams)
            {
                var p = c.transform.position; var e = c.transform.rotation.eulerAngles;
                sb.AppendLine(string.Format("{0} | pos=({1:F3},{2:F3},{3:F3}) | euler=({4:F1},{5:F1},{6:F1}) | fov={7:F1} | near={8:F3} far={9:F1} | depth={10} | {11} | on={12}",
                    c.name, p.x, p.y, p.z, e.x, e.y, e.z, c.fieldOfView, c.nearClipPlane, c.farClipPlane,
                    c.depth, c.clearFlags, c.enabled));
            }
            sb.AppendLine();

            // ---------------- 7. 玩家/特殊对象 ----------------
            sb.AppendLine("## 7. HST_ 顶层对象");
            foreach (var go in scene.GetRootGameObjects())
            {
                if (!go.name.StartsWith("HST_")) continue;
                var b = new Bounds(go.transform.position, Vector3.zero);
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true)) b.Encapsulate(r.bounds);
                sb.AppendLine(string.Format("{0} | children={1} | pos=({2:F3},{3:F3},{4:F3}) | bounds c=({5:F2},{6:F2},{7:F2}) s=({8:F2},{9:F2},{10:F2})",
                    go.name, go.GetComponentsInChildren<Transform>(true).Length,
                    go.transform.position.x, go.transform.position.y, go.transform.position.z,
                    b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z));
            }
            var player = GameObject.Find("HST_Player");
            if (player != null)
            {
                sb.AppendLine("HST_Player pos=" + player.transform.position.ToString("F3") +
                              " rot=" + player.transform.rotation.eulerAngles.ToString("F1") +
                              " children=" + player.GetComponentsInChildren<Transform>(true).Length);
                foreach (var c in player.GetComponentsInChildren<Collider>(true))
                    sb.AppendLine("   collider: " + c.name + " " + c.GetType().Name + " bounds c=(" +
                        c.bounds.center.ToString("F2") + ") s=(" + c.bounds.size.ToString("F2") + ")");
            }
            else sb.AppendLine("HST_Player 不在场景中");
            sb.AppendLine();

            // ---------------- 8. 渲染设置 ----------------
            sb.AppendLine("## 8. RenderSettings");
            sb.AppendLine("skybox=" + (RenderSettings.skybox != null ? RenderSettings.skybox.name : "null") +
                          " ambientMode=" + RenderSettings.ambientMode +
                          " ambientLight=" + RenderSettings.ambientLight.ToString("F3") +
                          " ambientIntensity=" + RenderSettings.ambientIntensity +
                          " defaultReflectionMode=" + RenderSettings.defaultReflectionMode +
                          " defaultReflectionResolution=" + RenderSettings.defaultReflectionResolution +
                          " reflectionsIntensity=" + RenderSettings.reflectionIntensity);
            sb.AppendLine("fog=" + RenderSettings.fog + " fogMode=" + RenderSettings.fogMode +
                          " fogColor=" + RenderSettings.fogColor.ToString("F3") +
                          " fogStart=" + RenderSettings.fogStartDistance + " fogEnd=" + RenderSettings.fogEndDistance);
            sb.AppendLine();

            // ---------------- 9. 走廊内空尺寸推导 ----------------
            float xmin = float.MaxValue, xmax = float.MinValue, ytop = float.MinValue, zspan = 0;
            foreach (var r in mrs)
            {
                if (!(r.name.StartsWith("ARCH_Panel_") || r.name.StartsWith("ARCH_EndWall") ||
                      r.name.StartsWith("ARCH_DoorWallFill"))) continue;
                var b = r.bounds;
                if (b.center.x < xmin) xmin = b.center.x;
                if (b.center.x > xmax) xmax = b.center.x;
                if (b.center.y > ytop) ytop = b.center.y;
                zspan = Mathf.Max(zspan, Mathf.Abs(b.center.z));
            }
            float cyTop = float.MinValue, cyBot = float.MaxValue;
            foreach (var r in mrs)
            {
                if (!r.name.StartsWith("ARCH_Ceil_") && !r.name.StartsWith("ARCH_Cove") && !r.name.StartsWith("ARCH_LightPane")) continue;
                if (r.bounds.min.y < cyBot) cyBot = r.bounds.min.y;
                if (r.bounds.max.y > cyTop) cyTop = r.bounds.max.y;
            }
            sb.AppendLine("## 9. 派生尺寸");
            sb.AppendLine(string.Format("墙板 x 中心跨度: {0:F3} .. {1:F3} (跨度 {2:F3})", xmin, xmax, xmax - xmin));
            sb.AppendLine(string.Format("墙板 |z| 最大中心: {0:F3}", zspan));
            sb.AppendLine(string.Format("天花/灯槽 y: {0:F3} .. {1:F3}", cyBot, cyTop));
            sb.AppendLine(string.Format("地面 y(ARCH_Floor*): 取 ARCH 地板 min.y"));
            foreach (var r in mrs)
                if (r.name.StartsWith("ARCH_Floor"))
                    sb.AppendLine(string.Format("  {0} c=({1:F3},{2:F3},{3:F3}) s=({4:F3},{5:F3},{6:F3})", r.name,
                        r.bounds.center.x, r.bounds.center.y, r.bounds.center.z,
                        r.bounds.size.x, r.bounds.size.y, r.bounds.size.z));

            File.WriteAllText(Out, sb.ToString(), Encoding.UTF8);
            Debug.Log("[HST-INS] 普查落盘 " + Out + "  (" + sb.Length + " 字符)");
        }

        // 取前 n 段下划线作为分组键
        static string GroupKey(string name, int n)
        {
            var parts = name.Split('_');
            if (parts.Length <= n) return name;
            return string.Join("_", parts, 0, n);
        }
    }
}
#endif
