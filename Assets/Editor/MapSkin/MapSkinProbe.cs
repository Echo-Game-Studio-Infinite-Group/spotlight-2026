using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// 出图探针：用与 MapSkinRender 相同的正交机位，从指定视口坐标发一条射线，
    /// 按 Renderer.bounds 做 AABB 求交，打印最近若干件的名字/材质/着色器。
    /// 用途：出图里"这一块黑的是什么"——不猜，直接问场景。
    ///
    /// 批处理：-executeMethod MapSkin.MapSkinProbe.Run -probe 0.47,0.63 -probebox BOX_S_01
    ///   -probe 的 x/y 是**视口坐标 0–1**（左下 0,0 / 右上 1,1）。
    /// </summary>
    public static class MapSkinProbe
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        static readonly Vector3 IsoDir = new Vector3(0.5f, 0.70710678f, 0.5f);
        static float PX = 0.5f, PY = 0.5f;
        static string BoxName = "BOX_S_01";

        [MenuItem("MapSkin/工具/探针：这里是什么")]
        public static void RunMenu() => Run();

        /// <summary>
        /// 列出某关的大面（墙/柱/地），按尺寸降序：给"样板角"选面用。
        /// -executeMethod MapSkin.MapSkinProbe.ListWalls -probebox BOX_S_01
        /// </summary>
        public static void ListWalls()
        {
            ParseArgs();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            GameObject root = null;
            foreach (var g in scene.GetRootGameObjects())
                if (g.name == BoxName) { root = g; break; }
            if (root == null) { Debug.LogError($"[探针] 找不到 {BoxName}"); return; }

            var rows = new List<(float area, string name, Vector3 c, Vector3 s, string mat)>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.enabled) continue;
                var b = r.bounds;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                float area = Mathf.Max(b.size.x * b.size.y, Mathf.Max(b.size.y * b.size.z, b.size.x * b.size.z));
                if (area < 40f) continue;
                rows.Add((area, r.gameObject.name, b.center, b.size, r.sharedMaterial != null ? r.sharedMaterial.name : "-"));
            }
            rows.Sort((a, b) => b.area.CompareTo(a.area));
            var rep = new StringBuilder();
            rep.AppendLine($"[探针] {BoxName} 大面（面积 > 40m²，前 20）：");
            foreach (var x in rows.GetRange(0, Mathf.Min(20, rows.Count)))
                rep.AppendLine($"  {x.name,-28} c=({x.c.x:F1},{x.c.y:F1},{x.c.z:F1}) size=({x.s.x:F1},{x.s.y:F1},{x.s.z:F1}) mat={x.mat}");
            Debug.Log(rep.ToString());
        }

        public static void Run()
        {
            ParseArgs();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            GameObject root = null;
            foreach (var g in scene.GetRootGameObjects())
                if (g.name == BoxName) { root = g; break; }
            if (root == null) { Debug.LogError($"[探针] 找不到 {BoxName}"); return; }

            var b = BoundsOf(root);
            float span = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            float dist = span * 2.5f + 100f;
            var camGo = new GameObject("__ProbeCam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = dist * 3f;
            cam.transform.position = b.center + IsoDir.normalized * dist;
            cam.transform.LookAt(b.center, Vector3.up);
            float up = 0.7071f * b.size.y + 0.3536f * (b.size.x + b.size.z);
            cam.orthographicSize = Mathf.Max(up * 0.5f * 1.15f, 1f);

            var ray = cam.ViewportPointToRay(new Vector3(PX, PY, 0f));
            var hits = new List<(float d, string name, string mat, string shader, int tris)>();
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled) continue;
                if (r.GetComponent<Camera>() != null) continue;
                if (!r.bounds.IntersectRay(ray, out float d)) continue;
                string matName = "-", shader = "-";
                var m = r.sharedMaterial;
                if (m != null) { matName = m.name; shader = m.shader != null ? m.shader.name : "-"; }
                int tris = 0;
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) tris = mf.sharedMesh.triangles.Length / 3;
                hits.Add((d, r.gameObject.name, matName, shader, tris));
            }
            hits.Sort((a, c) => a.d.CompareTo(c.d));

            var rep = new StringBuilder();
            rep.AppendLine($"[探针] {BoxName} 视口 ({PX:F2},{PY:F2}) ray o=({ray.origin.x:F1},{ray.origin.y:F1},{ray.origin.z:F1}) " +
                           $"dir=({ray.direction.x:F2},{ray.direction.y:F2},{ray.direction.z:F2})，命中的前 10 件：");
            for (int i = 0; i < Mathf.Min(10, hits.Count); i++)
                rep.AppendLine($"  #{i + 1} d={hits[i].d:F1}  {hits[i].name}   mat={hits[i].mat}   shader={hits[i].shader}   {hits[i].tris} tri");
            if (hits.Count == 0) rep.AppendLine("  （无命中）");
            Debug.Log(rep.ToString());
            Object.DestroyImmediate(camGo);
        }


        /// <summary>列出所有区块里的大块水平板（地板/平台），用于挑"浮空地板"样板。</summary>
        public static void ListFloors()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var rep = new StringBuilder();
            rep.AppendLine("[探针] 全部区块大块水平板（面积 ≥ 16m²，法线向上）：");
            foreach (var root in scene.GetRootGameObjects())
            {
                if (!root.name.StartsWith("BOX_S_")) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var b = r.bounds;
                    float area = b.size.x * b.size.z;
                    if (area < 16f) continue;
                    if (b.size.y > Mathf.Min(b.size.x, b.size.z) * 0.6f) continue; // 只要板，不要墙
                    string mat = r.sharedMaterial != null ? r.sharedMaterial.name : "-";
                    rep.AppendLine($"  {root.name}  {r.gameObject.name,-30} c=({b.center.x:F1},{b.center.y:F1},{b.center.z:F1}) size=({b.size.x:F1},{b.size.y:F1},{b.size.z:F1}) mat={mat}");
                }
            }
            Debug.Log(rep.ToString());
        }


        /// <summary>打印 S_04 地板层所有件的变换/网格局部包围盒，用于核对并集形状。</summary>
        public static void DumpFloorXform()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var rep = new StringBuilder();
            rep.AppendLine("[探针] S_04 地板层变换：");
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name != "BOX_S_04") continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;
                    var b = r.bounds;
                    if (b.center.y < 34.5f || b.center.y > 37.0f) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var t = r.transform;
                    var lb = mf.sharedMesh.bounds;
                    rep.AppendLine($"  {r.gameObject.name,-32} pos=({t.position.x:F2},{t.position.y:F2},{t.position.z:F2}) " +
                                   $"rot=({t.eulerAngles.x:F1},{t.eulerAngles.y:F1},{t.eulerAngles.z:F1}) " +
                                   $"scale=({t.lossyScale.x:F2},{t.lossyScale.y:F2},{t.lossyScale.z:F2}) " +
                                   $"meshLocalSize=({lb.size.x:F2},{lb.size.y:F2},{lb.size.z:F2}) worldSize=({b.size.x:F2},{b.size.y:F2},{b.size.z:F2})");
                }
            }
            Debug.Log(rep.ToString());
        }


        /// <summary>列出高度 > 60m 的巨件：名字/父链/尺寸/碰撞，用来确认"外立面"到底是什么。</summary>
        public static void ListGiants()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var rep = new StringBuilder();
            rep.AppendLine("[探针] 高度 > 18m 且面积 > 300m² 的巨件：");
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled) continue;
                var b = r.bounds;
                if (b.size.y < 18f) continue;
                float area = Mathf.Max(b.size.x * b.size.y, Mathf.Max(b.size.y * b.size.z, b.size.x * b.size.z));
                if (area < 300f) continue;
                var path = new StringBuilder();
                for (var t = r.transform; t != null; t = t.parent)
                { path.Insert(0, "/" + t.name); }
                int cols = r.GetComponents<Collider>().Length + (r.GetComponent<Collider>() != null ? 1 : 0);
                var mf = r.GetComponent<MeshFilter>();
                string mesh = mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "-";
                rep.AppendLine($"  {path}  c=({b.center.x:F0},{b.center.y:F0},{b.center.z:F0}) size=({b.size.x:F0},{b.size.y:F0},{b.size.z:F0}) mat={(r.sharedMaterial != null ? r.sharedMaterial.name : "-")} mesh={mesh}");
            }
            Debug.Log(rep.ToString());
        }

        static Bounds BoundsOf(GameObject root)
        {
            bool any = false; var b = new Bounds();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.enabled || r.GetComponent<Camera>() != null) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return b;
        }

        static void ParseArgs()
        {
            var a = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == "-probe" && i + 1 < a.Length)
                {
                    var p = a[i + 1].Split(',');
                    if (p.Length >= 2 &&
                        float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) &&
                        float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y))
                    { PX = x; PY = y; }
                }
                else if (a[i] == "-probebox" && i + 1 < a.Length) BoxName = a[i + 1];
            }
        }
    }
}
