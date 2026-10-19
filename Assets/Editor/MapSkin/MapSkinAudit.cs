using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// 场景质量审计（只读，不写盘）：
    ///   A 叠加件：分关装饰按类别计数，标出"小件海量"的贴片式做法；
    ///   B 浮空件：既没有下方支撑、也没有侧向贴墙的装饰件；
    ///   C 离群件：中心点跑出本区块包围盒 + 边距的件（多半是锚点算错）；
    ///   D 材质色阶：各关实际用到的材质名与基色，检查同类地形是否同色；
    ///   E 出入口套件：各关有没有门/闸/入口标记，是否同一套设计语言；
    ///   F UV 口径：世界三平面（会随机截断）vs 本对象 0–1（定制贴图）的件数。
    ///
    /// 批处理：-executeMethod MapSkin.MapSkinAudit.Run
    /// </summary>
    public static class MapSkinAudit
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string OutPath = "F:/VSCode/taptap-game-jem/_session_audit/audit_scene.txt";

        class Rec
        {
            public string name, block, cat;
            public Bounds b;
            public Material mat;
            public int tris;
            public bool isDec, isMapskin;
        }

        [MenuItem("MapSkin/工具/场景质量审计")]
        public static void Run() => RunCore();

        public static void RunCore()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var all = new List<Rec>();
            foreach (var mf in Object.FindObjectsOfType<MeshFilter>())
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var r = mf.GetComponent<Renderer>();
                if (r == null || !r.enabled) continue;
                var go = mf.gameObject;
                var rec = new Rec
                {
                    name = go.name, b = r.bounds, mat = r.sharedMaterial,
                    tris = mf.sharedMesh.triangles.Length / 3,
                    isDec = go.name.StartsWith("DEC_"),
                    isMapskin = go.name.StartsWith("MAPSKIN_"),
                };
                rec.block = BlockOf(go);
                rec.cat = CatOf(go.name);
                all.Add(rec);
            }

            var rep = new StringBuilder();
            rep.AppendLine($"[审计] 渲染件 {all.Count}（分关装饰 {all.Count(a => a.isDec)}，建筑增强 {all.Count(a => a.isMapskin)}）");
            // 缺材质（撤回后被删的派生材质引用）：Unity 里解析为 null
            int missMat = 0; var missByBlock = new Dictionary<string, int>();
            foreach (var mf in Object.FindObjectsOfType<MeshFilter>())
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var rr = mf.GetComponent<Renderer>();
                if (rr == null || !rr.enabled) continue;
                bool miss = rr.sharedMaterials == null || rr.sharedMaterials.Length == 0;
                if (!miss)
                    foreach (var mm in rr.sharedMaterials) if (mm == null) { miss = true; break; }
                if (!miss) continue;
                missMat++;
                var bk = BlockOf(mf.gameObject);
                missByBlock[bk] = missByBlock.TryGetValue(bk, out var c) ? c + 1 : 1;
            }
            rep.AppendLine($"[缺材质] {missMat} 件：" +
                string.Join("  ", missByBlock.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
            rep.AppendLine();

            // ---- A 叠加件：分关类别计数
            rep.AppendLine("== A. 分关装饰类别（件数） ==");
            foreach (var g in all.Where(a => a.isDec).GroupBy(a => a.block).OrderBy(g => g.Key))
            {
                rep.AppendLine($"  {g.Key}：{g.Count()} 件");
                foreach (var c in g.GroupBy(a => a.cat).OrderByDescending(c => c.Count()).Take(14))
                {
                    float avgSize = c.Average(x => x.b.size.magnitude);
                    rep.AppendLine($"      {c.Key,-14} {c.Count(),5} 件   平均尺寸 {avgSize,5:F2}m");
                }
            }
            rep.AppendLine();

            // ---- B 浮空件（无下支撑、无侧贴）
            rep.AppendLine("== B. 浮空件（下无支撑且侧无贴墙，容差 0.35m） ==");
            var decs = all.Where(a => a.isDec).ToList();
            var support = all.Where(a => !a.isDec || true).ToList();
            var floating = new List<Rec>();
            foreach (var d in decs)
            {
                if (HasSupport(d, support)) continue;
                floating.Add(d);
            }
            foreach (var g in floating.GroupBy(a => a.block).OrderBy(g => g.Key))
            {
                rep.AppendLine($"  {g.Key}：{g.Count()} 件   例: {string.Join(", ", g.Take(5).Select(x => x.name))}");
            }
            rep.AppendLine($"  合计浮空 {floating.Count} 件");
            rep.AppendLine();

            // ---- C 离群件
            rep.AppendLine("== C. 离群件（出区块包围盒 + 30m） ==");
            var blockBoxes = new Dictionary<string, Bounds>();
            foreach (var g in all.Where(a => !a.isDec && !a.isMapskin).GroupBy(a => a.block))
            {
                var bb = g.First().b; foreach (var x in g) bb.Encapsulate(x.b);
                blockBoxes[g.Key] = bb;
            }
            foreach (var g in decs.GroupBy(a => a.block).OrderBy(g => g.Key))
            {
                if (!blockBoxes.TryGetValue(g.Key, out var bb)) continue;
                bb.Expand(60f);
                var outl = g.Where(x => !bb.Contains(x.b.center)).ToList();
                if (outl.Count > 0)
                    rep.AppendLine($"  {g.Key}：{outl.Count} 件   例: {string.Join(", ", outl.Take(4).Select(x => x.name))}");
            }
            rep.AppendLine();

            // ---- D 材质色阶
            rep.AppendLine("== D. 材质使用（分关，前 12） ==");
            foreach (var g in all.Where(a => a.isDec || a.isMapskin || a.name.StartsWith("S_"))
                                   .GroupBy(a => a.block).OrderBy(g => g.Key))
            {
                rep.AppendLine($"  {g.Key}：");
                foreach (var m in g.Where(x => x.mat != null).GroupBy(x => x.mat.name)
                                   .OrderByDescending(x => x.Count()).Take(12))
                    rep.AppendLine($"      {m.Key,-48} {m.Count(),5} 件   基色 {BaseColor(m.First().mat)}");
            }
            rep.AppendLine();

            // ---- E 出入口套件
            rep.AppendLine("== E. 出入口/门类构件（按名字） ==");
            var doorRe = new System.Text.RegularExpressions.Regex("Gate|Door|Portal|Entrance|Exit|闸|门");
            foreach (var g in all.GroupBy(a => a.block).OrderBy(g => g.Key))
            {
                var doors = g.Where(x => doorRe.IsMatch(x.name)).ToList();
                rep.AppendLine($"  {g.Key}：{doors.Count} 件   {string.Join(", ", doors.Take(6).Select(x => x.name))}");
            }
            rep.AppendLine();

            // ---- F UV 口径
            rep.AppendLine("== F. UV 口径（世界三平面 vs 本对象 0–1） ==");
            int worldUv = 0, custom = 0, other = 0;
            foreach (var mf in Object.FindObjectsOfType<MeshFilter>())
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var uv = mf.sharedMesh.uv;
                if (uv == null || uv.Length == 0) { other++; continue; }
                float min = float.MaxValue, max = float.MinValue;
                for (int i = 0; i < uv.Length; i++)
                { if (uv[i].x < min) min = uv[i].x; if (uv[i].x > max) max = uv[i].x; }
                if (max - min <= 1.001f && min >= -0.001f) custom++; else worldUv++;
            }
            rep.AppendLine($"  本对象 0–1：{custom} 件；超出 0–1（世界平铺/随机截断）：{worldUv} 件；无 UV：{other} 件");

            System.IO.File.WriteAllText(OutPath, rep.ToString());
            Debug.Log(rep.ToString());
        }

        static string BaseColor(Material m)
        {
            Color c = Color.gray;
            if (m.HasProperty("_BaseColor")) c = m.GetColor("_BaseColor");
            else if (m.HasProperty("_Color")) c = m.GetColor("_Color");
            return $"({c.r:F3},{c.g:F3},{c.b:F3})";
        }

        static bool HasSupport(Rec d, List<Rec> all)
        {
            var db = d.b;
            float bottom = db.min.y;
            foreach (var o in all)
            {
                if (ReferenceEquals(o, d)) continue;
                var ob = o.b;
                bool xzOverlap = ob.min.x < db.max.x && ob.max.x > db.min.x &&
                                 ob.min.z < db.max.z && ob.max.z > db.min.z;
                if (!xzOverlap) continue;
                // 下方支撑：对方顶面接近本件底面
                if (ob.max.y <= bottom + 0.05f && ob.max.y >= bottom - 0.35f) return true;
            }
            foreach (var o in all)
            {
                if (ReferenceEquals(o, d)) continue;
                var ob = o.b;
                bool yOverlap = ob.min.y < db.max.y && ob.max.y > db.min.y;
                if (!yOverlap) continue;
                // 侧向贴墙：X 或 Z 方向贴面
                bool nearX = Mathf.Abs(ob.max.x - db.min.x) < 0.35f || Mathf.Abs(ob.min.x - db.max.x) < 0.35f;
                bool nearZ = Mathf.Abs(ob.max.z - db.min.z) < 0.35f || Mathf.Abs(ob.min.z - db.max.z) < 0.35f;
                bool zOverlap = ob.min.z < db.max.z && ob.max.z > db.min.z;
                bool xOverlap = ob.min.x < db.max.x && ob.max.x > db.min.x;
                if ((nearX && zOverlap) || (nearZ && xOverlap)) return true;
            }
            return false;
        }

        static string CatOf(string name)
        {
            var p = name.Split('_');
            return p.Length >= 3 ? p[2] : "?";
        }

        static string BlockOf(GameObject go)
        {
            for (var t = go.transform; t != null; t = t.parent)
            {
                var m = System.Text.RegularExpressions.Regex.Match(t.name, @"S_?0([1-7])");
                if (m.Success) return "S_0" + m.Groups[1].Value;
            }
            return "S_00";
        }
    }
}
