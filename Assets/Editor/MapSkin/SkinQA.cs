// SkinQA.cs — 换皮场景的自动体检（Editor-only，只读，不写场景）
//
// 为什么需要：换皮是「加几何」的活，最容易出的错不是画面而是**功能**——
// 新构件伸进门口 / 出生点被装饰埋住 / mesh 体积失控导致加载爆内存。
// 本工具只读场景 + 只读外部清单，把可验证的事实打成 JSON 与日志，供每轮巡检对比。
//
// 检查项：
//   ① 场景规摸：渲染器数、唯一 mesh 数、视觉三角数、网格资产预估字节、灯光数、碰撞体分类计数
//   ② 门口净空：读 _session_audit/GATE_Manifest.json 的 14 樘门，把门洞矩形（宽×高）离散成
//      6×5 个采样点，逐点测试是否被任何 MAPSKIN_* 渲染器的世界 AABB 包住 → 报"侵入门口"清单
//   ③ 出生点安全：SPAWN_GOAL_Manifest.json 的 Spawn/Goal 位置周围 1.2m 立方体采样 27 点，
//      同样测试 MAPSKIN_* AABB → 报被埋点
//   ④ 体积排行：按 mesh 预估字节列出前 15 个 mesh（点名到对象），供减重
//
// 菜单：MapSkin/体检/① 跑一遍
// 批处理：-executeMethod MapSkin.SkinQA.Run [-scene ...] [-out ...]
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    public static class SkinQA
    {
        static string ScenePath = "Assets/Scenes/Level1_Skin.unity";
        static string OutPath = "F:/VSCode/taptap-game-jem/_session_audit/skin_qa.json";
        const string GateManifest = "F:/VSCode/taptap-game-jem/_session_audit/GATE_Manifest.json";
        const string SpawnManifest = "F:/VSCode/taptap-game-jem/_session_audit/SPAWN_GOAL_Manifest.json";

        [MenuItem("MapSkin/体检/① 跑一遍（只读）")]
        public static void RunMenu() => Run();

        public static void Run()
        {
            ParseArgs();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var rep = new StringBuilder();
            rep.AppendLine($"[体检] {DateTime.Now:yyyy-MM-dd HH:mm} 场景 {ScenePath}");

            // ---------- ① 规摸 ----------
            var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            var meshSet = new HashSet<Mesh>();
            var objs = new List<(string name, Mesh mesh)>();
            long tris = 0, bytes = 0;
            foreach (var r in renderers)
            {
                var mf = r.GetComponent<MeshFilter>();
                var m = mf != null ? mf.sharedMesh : null;
                if (m == null) continue;
                objs.Add((r.gameObject.name, m));
                if (!meshSet.Add(m)) continue;
                long v = m.vertexCount;
                tris += m.triangles.Length / 3;
                bytes += v * 32 + (long)m.triangles.Length * 4;   // pos+normal+uv 估 32B/顶点
            }
            int colMesh = 0, colBox = 0, colOther = 0;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider>(true))
            {
                if (c is MeshCollider) colMesh++;
                else if (c is BoxCollider) colBox++;
                else colOther++;
            }
            var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
            rep.AppendLine($"① 渲染器 {renderers.Length}；唯一 mesh {meshSet.Count}；三角 {tris:N0}；网格估算 {bytes / 1048576.0:F0} MB；" +
                           $"灯光 {lights.Length}（方向 {lights.Count(l => l.type == LightType.Directional)} / 点 {lights.Count(l => l.type == LightType.Point)}）；" +
                           $"碰撞体 Mesh {colMesh} / Box {colBox} / 其他 {colOther}");

            // MAPSKIN_* 的几何（用于门洞/出生点的真实侵入判定）
            // 注意：不能用 renderer.bounds —— 整区块合并 meshes 的 AABB 覆盖整个区块，必然误报。
            // 这里逐个 mesh 读顶点（只读被隐藏/可见的 MAPSKIN_* 渲染器），按"顶点是否落进门洞矩形 / 出生点体积"判定。
            var mapMeshes = new List<(string name, Mesh mesh, Matrix4x4 m)>();
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled) continue;
                if (!r.gameObject.name.StartsWith("MAPSKIN_") && !AncestorStarts(r.transform, "MAPSKIN_")) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                mapMeshes.Add((Path(r.transform), mf.sharedMesh, r.localToWorldMatrix));
            }

            // ---------- ② 门口净空：顶点落进门洞矩形 = 侵入 ----------
            var intrudes = new List<string>();
            var gateList = ReadGates().Where(g => g != null && g.pos != null && g.pos.Length >= 3).ToList();
            foreach (var g in gateList)
            {
                var gp = new Vector3(g.pos[0], g.pos[1], g.pos[2]);
                var yaw = g.yaw * Mathf.Deg2Rad;
                var right = new Vector3(Mathf.Cos(yaw), 0, -Mathf.Sin(yaw));
                var fwd = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));
                float halfW = g.width * 0.5f, h = g.height;
                foreach (var (nm, mesh, l2w) in mapMeshes)
                {
                    var verts = mesh.vertices;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var wp = l2w.MultiplyPoint3x4(verts[i]);
                        var d = wp - gp;
                        float u = Vector3.Dot(d, right), y = d.y, dep = Vector3.Dot(d, fwd);
                        if (Mathf.Abs(u) < halfW - 0.05f && y > 0.35f && y < h - 0.05f && Mathf.Abs(dep) < 1.0f)
                        {
                            intrudes.Add($"{g.block}/{g.type} 顶点 ({wp.x:F1},{wp.y:F1},{wp.z:F1}) 落在门洞内 → {nm}");
                            break;
                        }
                    }
                }
            }
            rep.AppendLine($"② 门口净空：{gateList.Count} 樘门，顶点侵入门洞 {intrudes.Count} 处");
            foreach (var s in intrudes.Take(12)) rep.AppendLine($"    - {s}");

            // ---------- ③ 出生点/终点安全：顶点落在站位体积内 = 埋点 ----------
            var buried = new List<string>();
            foreach (var s in ReadSpawnGoals())
            {
                foreach (var (nm, mesh, l2w) in mapMeshes)
                {
                    var verts = mesh.vertices;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var wp = l2w.MultiplyPoint3x4(verts[i]);
                        var dxz = new Vector2(wp.x - s.pos.x, wp.z - s.pos.z).magnitude;
                        if (dxz < 0.9f && wp.y > s.pos.y + 0.15f && wp.y < s.pos.y + 2.4f)
                        {
                            buried.Add($"{s.kind}/{s.block} 顶点 ({wp.x:F1},{wp.y:F1},{wp.z:F1}) 落在站位体积 → {nm}");
                            break;
                        }
                    }
                }
            }
            rep.AppendLine($"③ 出生/终点：埋点 {buried.Count} 处");
            foreach (var s in buried.Take(12)) rep.AppendLine($"    - {s}");

            // ---------- ④ 体积排行 ----------
            var top = objs.GroupBy(o => o.mesh)
                          .Select(gp => (mesh: gp.Key, name: gp.First().name, bytes: gp.Key.vertexCount * 32L + gp.Key.triangles.Length * 4L))
                          .OrderByDescending(t => t.bytes).Take(15).ToList();
            rep.AppendLine("④ 体积排行（前 15，按顶点+索引估算）：");
            foreach (var t in top)
                rep.AppendLine($"    {t.bytes / 1048576.0,7:F1} MB  {t.mesh.name,-42} 例：{t.name}");

            // ---------- 落盘 ----------
            var doc = new QaDoc
            {
                scene = ScenePath,
                renderers = renderers.Length,
                meshes = meshSet.Count,
                triangles = tris,
                meshMB = (float)(bytes / 1048576.0),
                lights = lights.Length,
                colMesh = colMesh, colBox = colBox, colOther = colOther,
                gateSamples = intrudes.Count,
                spawnBuried = buried.Count,
                gateIntrudes = intrudes.Take(50).ToArray(),
                spawnIntrudes = buried.Take(50).ToArray(),
                topMeshes = top.Select(t => $"{t.bytes / 1048576.0:F1}MB {t.mesh.name} ({t.name})").ToArray(),
            };
            File.WriteAllText(OutPath, JsonUtility.ToJson(doc, true), new UTF8Encoding(false));
            rep.AppendLine($"→ {OutPath}");
            Debug.Log(rep.ToString());
        }

        [Serializable] class QaDoc
        {
            public string scene;
            public int renderers, meshes, lights, colMesh, colBox, colOther, gateSamples, spawnBuried;
            public long triangles;
            public float meshMB;
            public string[] gateIntrudes, spawnIntrudes, topMeshes;
        }

        static string Path(Transform t)
        {
            var s = t.name; var p = t.parent;
            while (p != null) { s = p.name + "/" + s; p = p.parent; }
            return s;
        }

        static bool AncestorStarts(Transform t, string prefix)
        {
            var p = t.parent;
            while (p != null) { if (p.name.StartsWith(prefix)) return true; p = p.parent; }
            return false;
        }

        // ---- 外部清单（只取需要的字段；GATE/SPAWN_GOAL 是普通对象数组，JsonUtility 能吃）----
        [Serializable] class GateDoc { public List<Gate> gates = new List<Gate>(); }
        [Serializable] class Gate { public string block, type; public float[] pos; public float yaw, width, height; }
        [Serializable] class GateWrap { public GateDoc doc; }

        static List<Gate> ReadGates()
        {
            try
            {
                var t = File.ReadAllText(GateManifest);
                var doc = JsonUtility.FromJson<GateDoc>(t);
                return doc?.gates ?? new List<Gate>();
            }
            catch (Exception e) { Debug.LogWarning($"[体检] 门清单读取失败：{e.Message}"); return new List<Gate>(); }
        }

        [Serializable] class SgDoc { public List<SgKit> kits = new List<SgKit>(); }
        [Serializable] class SgKit { public string type, block, status; public float[] pos; public float yaw; }

        static List<(string kind, string block, Vector3 pos)> ReadSpawnGoals()
        {
            var list = new List<(string, string, Vector3)>();
            try
            {
                var doc = JsonUtility.FromJson<SgDoc>(File.ReadAllText(SpawnManifest));
                foreach (var k in doc?.kits ?? new List<SgKit>())
                {
                    if (k.pos == null || k.pos.Length < 3) continue;
                    list.Add((k.type ?? "?", k.block ?? "?", new Vector3(k.pos[0], k.pos[1], k.pos[2])));
                }
            }
            catch (Exception e) { Debug.LogWarning($"[体检] 出生/终点清单读取失败：{e.Message}"); }
            return list;
        }

        static void ParseArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
            {
                if (a[i] == "-scene") ScenePath = a[i + 1];
                else if (a[i] == "-out") OutPath = a[i + 1];
            }
        }
    }
}
