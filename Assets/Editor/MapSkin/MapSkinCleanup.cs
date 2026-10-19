// 公共清理工具：一套逻辑扫七关，专门处理两类"用体量换细节"的坏味道：
//   1) 贴片类（贴在宿主表面上的小件海量叠加）——先支持 HexNode/HexFrame/HexGlow 白名单删除；
//   2) 浮空件（下无支撑、侧无贴墙）——默认只报告，-fix 1 才删。
//
// 设计口径：
//   · 类别按名字解析（DEC_S03_Xxx_001 / DEC_S_04_Xxx_001 两种命名都认），不按关卡写分支；
//   · "单件等效尺寸"不靠对象包围盒——合并对象会被整体尺寸掩盖，因此对网格做**位置焊接 + 连通分量**
//     近似单元数（每个环/盘/灯条是一个连通岛），等效尺寸 = 岛三轴尺寸的平均值；
//   · "贴合宿主面"用世界 AABB 近似：对象最薄轴的两端面在 0.35m 内与另一渲染件相接；
//   · 可见性按 8 个水平方向 + 上方做 AABB 射线遮挡（不用 Physics，场景可能无碰撞）；
//   · 只动 DEC_* / MAPSKIN_* 生成件；原始 S_0x_* 一律不删；
//   · 不写 prefab、不 new Material、不启动 Unity。
//
// 菜单：MapSkin/清理/*
// 批处理：
//   -executeMethod MapSkin.MapSkinCleanup.Scan                只读扫描，写 CLEANUP_Report.json
//   -executeMethod MapSkin.MapSkinCleanup.RemoveStuckOn      默认干跑；-run 1 删除白名单类别
//   -executeMethod MapSkin.MapSkinCleanup.RemoveFloating     默认只报告；-fix 1 删除无支撑无贴墙件
//   -executeMethod MapSkin.MapSkinCleanup.Support            默认干跑；-run 1 生成支托
//   -executeMethod MapSkin.MapSkinCleanup.Visibility         只读可见性报告
//   通用参数：-scene/-out/-tol/-size/-count/-ratio/-vis/-stuckcats/-floatmax/-supmax/-verbose/-save
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace MapSkin
{
    public static class MapSkinCleanup
    {
        // ------------------------------------------------------------------ 常量

        const string DefaultScene = "Assets/Scenes/Level1.unity";
        const string ReportName = "CLEANUP_Report.json";
        const string DetailRoot = "DEC_Details";
        const string MapskinRoot = "MAPSKIN_Details";
        const string SupMeshDir = "Assets/Art/EnvSkins/Building";

        // 贴片类分类阈值
        static float P_SizeMax = 0.35f;        // -size   单件等效尺寸上限（m）
        static int P_CountMin = 100;           // -count  单类单元数下限
        static float P_AttachTol = 0.35f;      // -tol    贴面判定容差（m）
        static float P_AttachRatio = 0.60f;    // -ratio  类内"贴面对象"占比下限
        static float P_FlatThin = 0.18f;       // 扁平贴片补充判据：厚度 ≤ 此值
        static float P_FlatSpan = 1.60f;       //                   且最大边 ≤ 此值

        // 可见性 / 浮空 / 支托
        static float P_VisDist = 150f;         // -visdist 可见性射线最大距离
        static float P_FloatMax = 0f;          // -floatmax 浮空删除尺寸上限（0 = 不设限）
        static float P_SupMax = 20f;           // -supmax  只为最大边 ≤ 此值的悬挑件补支托
        static bool P_Vis = false;             // -vis 1   扫描时附带可见性
        static bool P_SupClear = false;        // -supclear 1 重建已有 Support 容器

        // 运行控制
        static bool P_Run = false;             // -run 1   删除类操作真正执行
        static bool P_Fix = false;             // -fix 1   浮空修复真正执行
        static bool P_Dry = false;             // -dry 1   强制干跑
        static bool P_Save = true;             // -save 0  执行后不存场景
        static bool P_Verbose = false;         // -verbose 1
        static string P_Scene = DefaultScene;
        static string P_OutDir = null;
        static string[] P_Whitelist = { "HexNode", "HexFrame", "HexGlow" };

        // 可见性方向：8 个水平方向 + 上方
        static readonly Vector3[] VisDirs =
        {
            new Vector3(1f, 0f, 0f),
            new Vector3(0.7071068f, 0f, 0.7071068f),
            new Vector3(0f, 0f, 1f),
            new Vector3(-0.7071068f, 0f, 0.7071068f),
            new Vector3(-1f, 0f, 0f),
            new Vector3(-0.7071068f, 0f, -0.7071068f),
            new Vector3(0f, 0f, -1f),
            new Vector3(0.7071068f, 0f, -0.7071068f),
            new Vector3(0f, 1f, 0f),
        };

        // ------------------------------------------------------------------ 菜单入口

        [MenuItem("MapSkin/清理/① 扫描报告（只读）", false, 200)]
        static void ScanMenu() => Scan();

        [MenuItem("MapSkin/清理/② 删除贴片类（白名单）", false, 201)]
        static void RemoveStuckOnMenu()
        {
            ParseArgs();
            EnsureScene();
            RunStuckOn(true);
        }

        [MenuItem("MapSkin/清理/③ 浮空件报告（只读）", false, 202)]
        static void ReportFloatingMenu()
        {
            ParseArgs();
            EnsureScene();
            RunFloating(false);
        }

        [MenuItem("MapSkin/清理/④ 删除无支撑浮空件", false, 203)]
        static void RemoveFloatingMenu()
        {
            ParseArgs();
            EnsureScene();
            RunFloating(true);
        }

        [MenuItem("MapSkin/清理/⑤ 生成支托（悬挑件）", false, 204)]
        static void SupportMenu()
        {
            ParseArgs();
            EnsureScene();
            RunSupport(true);
        }

        [MenuItem("MapSkin/清理/⑥ 可见性报告（只读）", false, 205)]
        static void VisibilityMenu() => Visibility();

        // ------------------------------------------------------------------ 批处理入口

        /// <summary>只读扫描：统计贴片类候选、浮空件、可见性，写报告。</summary>
        public static void Scan()
        {
            ParseArgs();
            EnsureScene();
            var res = ScanScene(true);   // 扫描报告固定带可见性；-vis 0 也不省（Scan 的交付就是完整快照）
            WriteReport(res);
            LogSummary(res);
        }

        /// <summary>删除白名单贴片类。默认干跑，-run 1 执行。</summary>
        public static void RemoveStuckOn()
        {
            ParseArgs();
            EnsureScene();
            RunStuckOn(false);
        }

        /// <summary>浮空件。默认只报告，-fix 1 删除"既无下支撑也无侧贴"的件。</summary>
        public static void RemoveFloating()
        {
            ParseArgs();
            EnsureScene();
            RunFloating(false);
        }

        /// <summary>给保留的悬挑件补支托。默认干跑，-run 1 执行。</summary>
        public static void Support()
        {
            ParseArgs();
            EnsureScene();
            RunSupport(false);
        }

        /// <summary>只读可见性报告（8 水平 + 上方）。</summary>
        public static void Visibility()
        {
            ParseArgs();
            EnsureScene();
            var res = ScanScene(true);
            WriteReport(res);
            LogSummary(res);
        }

        // ------------------------------------------------------------------ 各操作

        static void RunStuckOn(bool interactive)
        {
            var res = ScanScene(P_Vis);
            WriteReport(res);
            LogSummary(res);

            var pieces = res.stuck;
            int tris = TrisOf(pieces);
            Debug.Log($"[清理·贴片] 白名单类别：{string.Join(", ", P_Whitelist)}\n" +
                      $"[清理·贴片] 待删除 {pieces.Count} 个对象，三角形 -{tris}（Run 前预览）");
            foreach (var g in pieces.GroupBy(p => p.block).OrderBy(g => g.Key))
                Debug.Log($"[清理·贴片]   {g.Key}: {g.Count()} 对象，-{TrisOf(g.ToList())} tri");

            if (interactive)
            {
                if (!EditorUtility.DisplayDialog("删除贴片类",
                        $"将删除 {pieces.Count} 个对象（三角形 -{tris}）。\n" +
                        $"类别：{string.Join(", ", P_Whitelist)}\n\n继续？", "删除", "取消")) return;
                DeletePieces(pieces, "RemoveStuckOn");
            }
            else if (P_Run && !P_Dry)
            {
                DeletePieces(pieces, "RemoveStuckOn");
            }
            else
            {
                Debug.Log("[清理·贴片] DryRun：未删除。加 -run 1 执行。");
            }
        }

        static void RunFloating(bool interactive)
        {
            var res = ScanScene(P_Vis);
            WriteReport(res);
            LogSummary(res);

            // -floatmax 只是安全阀（默认 0 = 全删）；超尺寸件仍留在报告里，不进入删除计划
            var pieces = P_FloatMax > 0f
                ? res.floating.Where(p => MaxExtent(p.b) <= P_FloatMax).ToList()
                : res.floating;
            int skipped = res.floating.Count - pieces.Count;
            int tris = TrisOf(pieces);
            Debug.Log($"[清理·浮空] 浮空件 {res.floating.Count}，删除计划 {pieces.Count}" +
                      (skipped > 0 ? $"（超尺寸跳过 {skipped}）" : "") +
                      $"；其中顶部有附着 {pieces.Count(x => x.topSupport)} 件（按口径仍算浮空，可在报告里筛掉）" +
                      $"，三角形 -{tris}（删除前预览）");
            foreach (var g in pieces.GroupBy(p => p.block).OrderBy(g => g.Key))
                Debug.Log($"[清理·浮空]   {g.Key}: {g.Count()} 件，-{TrisOf(g.ToList())} tri");

            if (interactive)
            {
                if (pieces.Count == 0)
                {
                    EditorUtility.DisplayDialog("删除浮空件", "没有浮空件。", "好");
                    return;
                }
                if (!EditorUtility.DisplayDialog("删除浮空件",
                        $"将删除 {pieces.Count} 件「下无支撑且侧无贴墙」的装饰件（三角形 -{tris}）。\n\n继续？",
                        "删除", "取消")) return;
                DeletePieces(pieces, "RemoveFloating");
            }
            else if (P_Fix && !P_Dry)
            {
                DeletePieces(pieces, "RemoveFloating");
            }
            else
            {
                Debug.Log("[清理·浮空] 只报告：未删除。加 -fix 1 执行删除。");
            }
        }

        static void RunSupport(bool interactive)
        {
            var res = ScanScene(P_Vis);
            WriteReport(res);
            LogSummary(res);

            var plan = BuildSupportPlan(res);
            int tris = 0;
            foreach (var g in plan.groups.Values) tris += g.Tris;
            Debug.Log($"[清理·支托] 计划为 {plan.supported} 件悬挑件生成 {plan.groups.Count} 个 Support 对象，" +
                      $"三角形 +{tris}（Run 前预览）；无落点 {plan.noHost} 件，超尺寸跳过 {plan.tooBig} 件");

            if (interactive)
            {
                if (plan.groups.Count == 0)
                {
                    EditorUtility.DisplayDialog("生成支托", "没有可生成的支托。", "好");
                    return;
                }
                if (!EditorUtility.DisplayDialog("生成支托",
                        $"将生成 {plan.groups.Count} 个 Support 对象（三角形 +{tris}）。\n\n继续？", "生成", "取消")) return;
                ExecuteSupport(plan);
            }
            else if (P_Run && !P_Dry)
            {
                ExecuteSupport(plan);
            }
            else
            {
                Debug.Log("[清理·支托] DryRun：未生成。加 -run 1 执行。");
            }
        }

        // ------------------------------------------------------------------ 场景扫描

        class Piece
        {
            public GameObject go;
            public Renderer renderer;
            public Mesh mesh;
            public string name, block, category, material;
            public Bounds b;
            public int tris;
            public bool isGenerated;
            public MeshStat stat;
            public float scale = 1f;

            public bool attached;
            public bool bottomSupport;
            public bool sideSupport;
            public bool topSupport;
            public bool visibilityComputed;
            public int visibleFaces;
        }

        class MeshStat
        {
            public int units = 1;
            public float avgEquiv, medEquiv, avgThin, avgMax;
        }

        class ScanResult
        {
            public List<Piece> all = new List<Piece>();
            public List<Piece> decor = new List<Piece>();
            public List<Piece> stuck = new List<Piece>();
            public List<Piece> floating = new List<Piece>();
            public List<Piece> buried = new List<Piece>();
            public List<StatBlock> blocks = new List<StatBlock>();
            public List<StatCat> cats = new List<StatCat>();
            public CleanupReport report;
        }

        static ScanResult ScanScene(bool withVisibility)
        {
            var res = new ScanResult();
            _meshCache.Clear();

            // 一次收集：渲染件（MeshFilter + MeshRenderer）
            foreach (var mf in UObject.FindObjectsOfType<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled) continue;
                var go = mf.gameObject;
                if (go == null) continue;

                var p = new Piece
                {
                    go = go,
                    renderer = mr,
                    mesh = mf.sharedMesh,
                    name = go.name,
                    block = BlockOf(go),
                    category = CategoryOf(go.name),
                    material = mr.sharedMaterial != null ? mr.sharedMaterial.name : "",
                    b = mr.bounds,
                    tris = TriCount(mf.sharedMesh),
                    isGenerated = IsGenerated(go.name),
                };
                var ls = go.transform.lossyScale;
                p.scale = (Mathf.Abs(ls.x) + Mathf.Abs(ls.y) + Mathf.Abs(ls.z)) / 3f;
                if (p.scale < 0.0001f) p.scale = 1f;
                res.all.Add(p);
            }

            foreach (var p in res.all)
            {
                if (!p.isGenerated) continue;
                p.stat = AnalyzeMesh(p.mesh, p.b.size);
                res.decor.Add(p);
            }

            ComputeAttach(res.all);
            ComputeSupport(res.all);
            if (withVisibility) ComputeVisibility(res.decor, res.all);

            res.stuck = res.decor
                .Where(p => CategoryWhitelisted(p.category))
                .OrderBy(p => p.block).ThenBy(p => p.category).ThenBy(p => p.name)
                .ToList();
            res.floating = res.decor
                .Where(p => !p.bottomSupport && !p.sideSupport)
                .OrderBy(p => p.block).ThenBy(p => p.category).ThenBy(p => p.name)
                .ToList();
            res.buried = res.decor
                .Where(p => p.visibilityComputed && p.visibleFaces == 0)
                .OrderBy(p => p.block).ThenBy(p => p.category).ThenBy(p => p.name)
                .ToList();

            BuildAggregates(res, withVisibility);
            return res;
        }

        // ------------------------------------------------------------------ 网格单元分析

        static readonly Dictionary<int, MeshStat> _meshCache = new Dictionary<int, MeshStat>();
        static readonly List<Vector3> _v = new List<Vector3>();
        static readonly List<int> _t = new List<int>();

        /// <summary>
        /// 把合并网格拆成"位置焊接后的连通岛"，用岛的尺寸近似单件尺寸。
        /// 合并件（几千个环/盘/灯）不拆开的话，外层包围盒会把所有小件平均成一个大件，
        /// 贴片判据永远命中不了——这是本工具与旧的按对象包围盒审计的关键区别。
        /// </summary>
        static MeshStat AnalyzeMesh(Mesh mesh, Vector3 worldSize)
        {
            MeshStat cached;
            if (mesh != null && _meshCache.TryGetValue(mesh.GetInstanceID(), out cached)) return cached;

            var st = FallbackStat(mesh, worldSize);
            if (mesh == null || !mesh.isReadable || mesh.vertexCount == 0 || mesh.vertexCount > 300000)
            {
                if (mesh != null) _meshCache[mesh.GetInstanceID()] = st;
                return st;
            }

            _v.Clear();
            _t.Clear();
            mesh.GetVertices(_v);
            var sub = new List<int>(1024);
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                sub.Clear();
                mesh.GetTriangles(sub, s);
                _t.AddRange(sub);
            }

            int n = _v.Count;
            if (n == 0 || _t.Count < 3)
            {
                if (mesh != null) _meshCache[mesh.GetInstanceID()] = st;
                return st;
            }

            var parent = new int[n];
            var rank = new int[n];
            for (int i = 0; i < n; i++) { parent[i] = i; rank[i] = 0; }

            // 位置焊接：生成器常常逐三角形写顶点，直接按索引求连通会得到"每三角形一个岛"。
            float span = Mathf.Max(mesh.bounds.size.x, Mathf.Max(mesh.bounds.size.y, mesh.bounds.size.z));
            float eps = Mathf.Clamp(span * 0.0005f, 0.0005f, 0.02f);
            float inv = 1f / eps;
            var weld = new Dictionary<Vector3Int, int>(n);
            for (int i = 0; i < n; i++)
            {
                var key = new Vector3Int(
                    Mathf.RoundToInt(_v[i].x * inv),
                    Mathf.RoundToInt(_v[i].y * inv),
                    Mathf.RoundToInt(_v[i].z * inv));
                int first;
                if (weld.TryGetValue(key, out first)) Union(parent, rank, i, first);
                else weld[key] = i;
            }
            for (int i = 0; i + 2 < _t.Count; i += 3)
            {
                Union(parent, rank, _t[i], _t[i + 1]);
                Union(parent, rank, _t[i + 1], _t[i + 2]);
            }

            // 统计连通岛尺寸
            var rootToComp = new Dictionary<int, int>(Mathf.Min(n, 2048));
            var mins = new List<Vector3>(256);
            var maxs = new List<Vector3>(256);
            bool overflow = false;
            for (int i = 0; i < n; i++)
            {
                int r = Find(parent, i);
                int ci;
                if (!rootToComp.TryGetValue(r, out ci))
                {
                    if (rootToComp.Count >= 50000) { overflow = true; break; }
                    ci = mins.Count;
                    rootToComp[r] = ci;
                    mins.Add(_v[i]);
                    maxs.Add(_v[i]);
                }
                else
                {
                    var lo = mins[ci]; var hi = maxs[ci]; var p = _v[i];
                    if (p.x < lo.x) lo.x = p.x; if (p.y < lo.y) lo.y = p.y; if (p.z < lo.z) lo.z = p.z;
                    if (p.x > hi.x) hi.x = p.x; if (p.y > hi.y) hi.y = p.y; if (p.z > hi.z) hi.z = p.z;
                    mins[ci] = lo; maxs[ci] = hi;
                }
            }

            if (overflow || mins.Count == 0)
            {
                if (mesh != null) _meshCache[mesh.GetInstanceID()] = st;
                return st;
            }

            var eq = new List<float>(mins.Count);
            double sumEq = 0, sumThin = 0, sumMax = 0;
            for (int i = 0; i < mins.Count; i++)
            {
                var s = maxs[i] - mins[i];
                float e = (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
                float thin = Mathf.Min(Mathf.Abs(s.x), Mathf.Min(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                float mx = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                eq.Add(e);
                sumEq += e; sumThin += thin; sumMax += mx;
            }
            eq.Sort();
            st.units = mins.Count;
            st.avgEquiv = (float)(sumEq / mins.Count);
            st.medEquiv = eq[eq.Count / 2];
            st.avgThin = (float)(sumThin / mins.Count);
            st.avgMax = (float)(sumMax / mins.Count);

            _meshCache[mesh.GetInstanceID()] = st;
            return st;
        }

        static MeshStat FallbackStat(Mesh mesh, Vector3 worldSize)
        {
            var s = mesh != null ? mesh.bounds.size : worldSize;
            float e = (s.x + s.y + s.z) / 3f;
            return new MeshStat
            {
                units = 1,
                avgEquiv = e,
                medEquiv = e,
                avgThin = Mathf.Min(s.x, Mathf.Min(s.y, s.z)),
                avgMax = Mathf.Max(s.x, Mathf.Max(s.y, s.z)),
            };
        }

        static int Find(int[] p, int x)
        {
            while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; }
            return x;
        }

        static void Union(int[] p, int[] rank, int a, int b)
        {
            a = Find(p, a); b = Find(p, b);
            if (a == b) return;
            if (rank[a] < rank[b]) { int tmp = a; a = b; b = tmp; }
            p[b] = a;
            if (rank[a] == rank[b]) rank[a]++;
        }

        // ------------------------------------------------------------------ 贴面 / 浮空

        /// <summary>
        /// 贴面判定（AABB 近似）：对象最薄轴的两端面在容差内与另一渲染件相接，即视为"贴花"。
        /// 合并的六边形墙条带最薄轴就是墙法线，所以整条会被判为贴面。
        /// </summary>
        static void ComputeAttach(List<Piece> all)
        {
            foreach (var p in all)
            {
                if (!p.isGenerated) continue;
                var b = p.b;
                int thin = ThinAxis(b.size);
                bool anyFace = false;
                bool thinFace = false;

                foreach (var o in all)
                {
                    if (ReferenceEquals(o, p)) continue;
                    if (o.b.size.sqrMagnitude < 1e-8f) continue;
                    var ob = o.b;
                    for (int a = 0; a < 3; a++)
                    {
                        int a1 = (a + 1) % 3, a2 = (a + 2) % 3;
                        float ov1 = Mathf.Min(GetMax(b, a1), GetMax(ob, a1)) - Mathf.Max(GetMin(b, a1), GetMin(ob, a1));
                        float ov2 = Mathf.Min(GetMax(b, a2), GetMax(ob, a2)) - Mathf.Max(GetMin(b, a2), GetMin(ob, a2));
                        if (ov1 <= 0.02f || ov2 <= 0.02f) continue;

                        bool below = Mathf.Abs(GetMax(ob, a) - GetMin(b, a)) <= P_AttachTol && GetMin(ob, a) < GetMin(b, a);
                        bool above = Mathf.Abs(GetMin(ob, a) - GetMax(b, a)) <= P_AttachTol && GetMax(ob, a) > GetMax(b, a);
                        if (!below && !above) continue;

                        anyFace = true;
                        if (a == thin) thinFace = true;
                        break;
                    }
                }

                float maxExtent = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                p.attached = thinFace || (anyFace && maxExtent <= P_FlatSpan);
            }
        }

        /// <summary>下支撑或侧贴墙，二者任一成立即不算浮空。</summary>
        static void ComputeSupport(List<Piece> all)
        {
            foreach (var p in all)
            {
                if (!p.isGenerated) continue;
                var b = p.b;
                bool bottom = false, side = false, top = false;

                foreach (var o in all)
                {
                    if (ReferenceEquals(o, p)) continue;
                    if (o.b.size.sqrMagnitude < 1e-8f) continue;
                    var ob = o.b;

                    // 下支撑：对方顶面接近本件底面，且 XZ 有重叠
                    float ox = Mathf.Min(b.max.x, ob.max.x) - Mathf.Max(b.min.x, ob.min.x);
                    float oz = Mathf.Min(b.max.z, ob.max.z) - Mathf.Max(b.min.z, ob.min.z);
                    if (ox > 0.02f && oz > 0.02f &&
                        ob.max.y <= b.min.y + 0.05f && ob.max.y >= b.min.y - P_AttachTol)
                        bottom = true;

                    // 顶部附着：只做报告提示（本工具按口径仍以"下支撑/侧贴"判定浮空）
                    if (ox > 0.02f && oz > 0.02f &&
                        ob.min.y >= b.max.y - 0.05f && ob.min.y <= b.max.y + P_AttachTol)
                        top = true;

                    // 侧贴：Y 重叠，X 或 Z 面在容差内相接
                    float oy = Mathf.Min(b.max.y, ob.max.y) - Mathf.Max(b.min.y, ob.min.y);
                    if (oy <= 0.02f) continue;
                    bool nearX = Mathf.Abs(ob.max.x - b.min.x) <= P_AttachTol || Mathf.Abs(ob.min.x - b.max.x) <= P_AttachTol;
                    bool nearZ = Mathf.Abs(ob.max.z - b.min.z) <= P_AttachTol || Mathf.Abs(ob.min.z - b.max.z) <= P_AttachTol;
                    if ((nearX && oz > 0.02f) || (nearZ && ox > 0.02f)) side = true;

                    if (bottom && side) break;
                }

                p.bottomSupport = bottom;
                p.sideSupport = side;
                p.topSupport = top;
            }
        }

        // ------------------------------------------------------------------ 可见性（AABB 射线）

        static void ComputeVisibility(List<Piece> decor, List<Piece> all)
        {
            if (decor.Count == 0) return;
            var grid = new OccluderGrid();
            grid.Build(all);

            var ids = new List<int>(256);
            foreach (var p in decor)
            {
                p.visibilityComputed = true;
                if (p.b.size.sqrMagnitude < 1e-8f) { p.visibleFaces = 0; continue; }

                var origin = p.b.center;
                int visible = 0;
                bool enclosed = false;
                for (int d = 0; d < VisDirs.Length; d++)
                {
                    var dir = VisDirs[d];
                    grid.Query(origin, dir, P_VisDist, ids);
                    bool blocked = false;
                    for (int k = 0; k < ids.Count; k++)
                    {
                        int oi = ids[k];
                        var o = all[oi];
                        if (ReferenceEquals(o, p)) continue;
                        if (o.b.size.sqrMagnitude < 1e-8f) continue;
                        if (o.b.Contains(origin))
                        {
                            // 原始体块把整件包住 = 真埋没；合并装饰件的大包围盒只跳过，不当遮挡物
                            if (!o.isGenerated && o.b.Contains(p.b.min) && o.b.Contains(p.b.max))
                            {
                                enclosed = true;
                                break;
                            }
                            continue;
                        }
                        if (RayBox(o.b, origin, dir, P_VisDist)) { blocked = true; break; }
                    }
                    if (enclosed) break;
                    if (!blocked) visible++;
                }
                p.visibleFaces = enclosed ? 0 : visible;
            }
        }

        class OccluderGrid
        {
            const float Cell = 24f;
            const float Step = 4f;
            const int LargeCells = 4096;

            readonly Dictionary<Vector3Int, List<int>> _map = new Dictionary<Vector3Int, List<int>>();
            readonly List<int> _large = new List<int>();
            int[] _stamp;
            int _tick;

            public void Build(List<Piece> all)
            {
                _stamp = new int[all.Count];
                _tick = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    var b = all[i].b;
                    if (b.size.sqrMagnitude < 1e-8f) continue;
                    int x0 = Mathf.FloorToInt(b.min.x / Cell), x1 = Mathf.FloorToInt(b.max.x / Cell);
                    int y0 = Mathf.FloorToInt(b.min.y / Cell), y1 = Mathf.FloorToInt(b.max.y / Cell);
                    int z0 = Mathf.FloorToInt(b.min.z / Cell), z1 = Mathf.FloorToInt(b.max.z / Cell);
                    long cells = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
                    if (cells <= 0 || cells > LargeCells) { _large.Add(i); continue; }
                    for (int x = x0; x <= x1; x++)
                        for (int y = y0; y <= y1; y++)
                            for (int z = z0; z <= z1; z++)
                            {
                                var key = new Vector3Int(x, y, z);
                                List<int> list;
                                if (!_map.TryGetValue(key, out list)) { list = new List<int>(8); _map[key] = list; }
                                list.Add(i);
                            }
                }
            }

            public void Query(Vector3 origin, Vector3 dir, float maxDist, List<int> outIds)
            {
                _tick++;
                outIds.Clear();
                for (float t = 0f; t <= maxDist; t += Step)
                {
                    var key = CellOf(origin + dir * t);
                    List<int> list;
                    if (!_map.TryGetValue(key, out list)) continue;
                    for (int k = 0; k < list.Count; k++)
                    {
                        int id = list[k];
                        if (_stamp[id] == _tick) continue;
                        _stamp[id] = _tick;
                        outIds.Add(id);
                    }
                }
                for (int k = 0; k < _large.Count; k++)
                {
                    int id = _large[k];
                    if (_stamp[id] == _tick) continue;
                    _stamp[id] = _tick;
                    outIds.Add(id);
                }
            }

            static Vector3Int CellOf(Vector3 p) => new Vector3Int(
                Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell), Mathf.FloorToInt(p.z / Cell));
        }

        static bool RayBox(Bounds b, Vector3 o, Vector3 d, float maxDist)
        {
            float tmin = 0f, tmax = maxDist;
            if (!Slab(o.x, d.x, b.min.x, b.max.x, ref tmin, ref tmax)) return false;
            if (!Slab(o.y, d.y, b.min.y, b.max.y, ref tmin, ref tmax)) return false;
            if (!Slab(o.z, d.z, b.min.z, b.max.z, ref tmin, ref tmax)) return false;
            return tmax > 0.02f && tmin < tmax;
        }

        static bool Slab(float o, float d, float lo, float hi, ref float tmin, ref float tmax)
        {
            if (Mathf.Abs(d) < 1e-7f) return o >= lo && o <= hi;
            float t1 = (lo - o) / d;
            float t2 = (hi - o) / d;
            if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
            if (t1 > tmin) tmin = t1;
            if (t2 < tmax) tmax = t2;
            return tmin <= tmax;
        }

        // ------------------------------------------------------------------ 聚合 / 报告

        static void BuildAggregates(ScanResult res, bool withVisibility)
        {
            // 区块统计
            foreach (var g in res.all.GroupBy(p => p.block).OrderBy(g => g.Key))
            {
                var decor = g.Where(p => p.isGenerated).ToList();
                res.blocks.Add(new StatBlock
                {
                    block = g.Key,
                    renderers = g.Count(),
                    decorObjects = decor.Count,
                    decorTris = decor.Sum(p => p.tris),
                    floating = decor.Count(p => !p.bottomSupport && !p.sideSupport),
                    stuckOn = decor.Count(p => CategoryWhitelisted(p.category)),
                    buried = withVisibility ? decor.Count(p => p.visibilityComputed && p.visibleFaces == 0) : 0,
                });
            }

            // 类别统计：单元数按连通岛累加，尺寸按单元加权
            foreach (var g in res.decor.GroupBy(p => new { p.block, p.category })
                                       .OrderBy(g => g.Key.block).ThenBy(g => g.Key.category))
            {
                int units = 0;
                double sumEq = 0, sumThin = 0, sumMax = 0;
                int attached = 0;
                foreach (var p in g)
                {
                    units += p.stat.units;
                    sumEq += p.stat.avgEquiv * p.scale * p.stat.units;
                    sumThin += p.stat.avgThin * p.scale * p.stat.units;
                    sumMax += p.stat.avgMax * p.scale * p.stat.units;
                    if (p.attached) attached++;
                }
                var c = new StatCat
                {
                    block = g.Key.block,
                    category = g.Key.category,
                    objects = g.Count(),
                    units = units,
                    tris = g.Sum(p => p.tris),
                    avgUnitSize = units > 0 ? (float)(sumEq / units) : 0f,
                    avgUnitThin = units > 0 ? (float)(sumThin / units) : 0f,
                    avgUnitMax = units > 0 ? (float)(sumMax / units) : 0f,
                    attachedRatio = g.Count() > 0 ? attached / (float)g.Count() : 0f,
                    whitelisted = CategoryWhitelisted(g.Key.category),
                    examples = g.OrderBy(p => p.name).Take(5).Select(p => p.name).ToArray(),
                };

                bool countOk = c.units > P_CountMin;
                bool attachOk = c.attachedRatio >= P_AttachRatio;
                bool strictSizeOk = c.avgUnitSize < P_SizeMax;
                bool flatSizeOk = c.avgUnitThin <= P_FlatThin && c.avgUnitMax <= P_FlatSpan;
                c.countOk = countOk;
                c.attachOk = attachOk;
                c.strictSizeOk = strictSizeOk;
                c.flatSizeOk = flatSizeOk;
                // 主判据严格按 -size（默认 0.35m）；白名单类别即使超尺寸也保留（HexFrame 环径 1.2m）
                c.qualifies = (countOk && attachOk && strictSizeOk) || c.whitelisted;
                // 扁平补充候选：厚度 ≤0.18m 且最大边 ≤1.6m 的大直径环/小面板，单独列出供主控确认
                c.qualifiesFlat = countOk && attachOk && flatSizeOk;

                if (c.whitelisted && !(countOk && attachOk && strictSizeOk))
                    c.reason = $"白名单覆盖：强制删除（单元 {c.units} / 贴面 {c.attachedRatio:P0} / 等效 {c.avgUnitSize:F2}m）";
                else if (!countOk) c.reason = $"单元数 {c.units} ≤ {P_CountMin}";
                else if (!attachOk) c.reason = $"贴面对象占比 {c.attachedRatio:P0} < {P_AttachRatio:P0}";
                else if (strictSizeOk) c.reason = "候选：小件海量贴面";
                else if (flatSizeOk)
                    c.reason = $"补充候选：扁平件海量贴面（厚 {c.avgUnitThin:F2}m / 最大边 {c.avgUnitMax:F2}m）";
                else
                    c.reason = $"单件等效尺寸 {c.avgUnitSize:F2}m（厚 {c.avgUnitThin:F2}m / 最大边 {c.avgUnitMax:F2}m）超阈值";

                res.cats.Add(c);
            }

            res.report = new CleanupReport
            {
                generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                scene = P_Scene,
                note = "贴片候选 = 单元数>阈值 且 贴面对象占比≥阈值 且（单件等效尺寸<阈值 或 扁平小件）；" +
                       "浮空 = 下无支撑且侧无贴墙；可见面 = 8 水平 + 上方 AABB 射线未被遮挡的方向数。",
                renderers = res.all.Count,
                decorObjects = res.decor.Count,
                decorTris = res.decor.Sum(p => p.tris),
                visibilityComputed = withVisibility,
                whitelist = P_Whitelist.ToArray(),
                blocks = res.blocks.ToArray(),
                stuckOnCandidates = res.cats.ToArray(),
                floating = res.floating.Select(p => ToPieceStat(p, "浮空：下无支撑且侧无贴墙")).ToArray(),
                buried = res.buried.Select(p => ToPieceStat(p, "9 个方向全被遮挡")).ToArray(),
                decorVisibility = res.decor
                    .OrderBy(p => p.block).ThenBy(p => p.category).ThenBy(p => p.name)
                    .Select(p => ToPieceStat(p, p.visibilityComputed ? null : "未计算"))
                    .ToArray(),
                plan = new CleanupPlan
                {
                    stuckOnObjects = res.stuck.Count,
                    stuckOnTris = TrisOf(res.stuck),
                    floatingObjects = res.floating.Count,
                    floatingTris = TrisOf(res.floating),
                    buriedObjects = res.buried.Count,
                    supportCandidates = res.floating.Count(p => MaxExtent(p.b) <= P_SupMax),
                },
            };
        }

        static StatPiece ToPieceStat(Piece p, string reason)
        {
            return new StatPiece
            {
                name = p.name,
                block = p.block,
                category = p.category,
                material = p.material,
                position = new[] { p.b.center.x, p.b.center.y, p.b.center.z },
                size = new[] { p.b.size.x, p.b.size.y, p.b.size.z },
                tris = p.tris,
                units = p.stat != null ? p.stat.units : 0,
                unitSize = p.stat != null ? p.stat.avgEquiv * p.scale : 0f,
                attached = p.attached,
                bottomSupport = p.bottomSupport,
                sideSupport = p.sideSupport,
                topSupport = p.topSupport,
                visibleFaces = p.visibleFaces,
                reason = reason,
            };
        }

        static void WriteReport(ScanResult res)
        {
            Directory.CreateDirectory(OutDir);
            string path = Path.Combine(OutDir, ReportName);
            File.WriteAllText(path, JsonUtility.ToJson(res.report, true), new UTF8Encoding(false));
            Debug.Log($"[清理] 报告已写入 {path}");
        }

        static void LogSummary(ScanResult res)
        {
            var rep = new StringBuilder();
            rep.AppendLine($"[清理扫描] 渲染件 {res.all.Count}（DEC_ {res.decor.Count}），装饰三角形 {res.decor.Sum(p => p.tris):N0}");
            rep.AppendLine("  区块     渲染件   装饰件   浮空   贴片(白名单)   埋没");
            foreach (var b in res.blocks)
                rep.AppendLine($"  {b.block,-8} {b.renderers,6} {b.decorObjects,8} {b.floating,6} {b.stuckOn,14} {b.buried,6}");

            var hits = res.cats.Where(c => c.qualifies).ToList();
            rep.AppendLine($"  贴片类候选（严格判据 + 白名单）{hits.Count} 类：");
            foreach (var c in hits.OrderBy(c => c.block).ThenByDescending(c => c.units))
                rep.AppendLine($"    {(c.whitelisted ? "[白名单] " : "[待确认] ")}{c.block} {c.category,-14} " +
                               $"{c.objects,4} 对象 / {c.units,6} 单元 / 等效 {c.avgUnitSize:F2}m / 厚 {c.avgUnitThin:F2}m / 贴面 {c.attachedRatio:P0}");

            var flats = res.cats.Where(c => !c.qualifies && c.qualifiesFlat).ToList();
            if (flats.Count > 0)
            {
                rep.AppendLine($"  扁平补充候选（大直径环/小面板，未计入主判据）{flats.Count} 类：");
                foreach (var c in flats.OrderBy(c => c.block).ThenByDescending(c => c.units))
                    rep.AppendLine($"    [待确认] {c.block} {c.category,-14} {c.objects,4} 对象 / {c.units,6} 单元 / " +
                                   $"等效 {c.avgUnitSize:F2}m / 厚 {c.avgUnitThin:F2}m / 最大边 {c.avgUnitMax:F2}m");
            }
            if (P_Verbose)
            {
                rep.AppendLine("  未入选类别（原因）：");
                foreach (var c in res.cats.Where(c => !c.qualifies).OrderBy(c => c.block).ThenByDescending(c => c.units))
                    rep.AppendLine($"    {c.block} {c.category,-14} {c.objects,4} 对象 / {c.units,6} 单元 / {c.reason}");
            }

            rep.AppendLine($"  白名单删除计划：{res.stuck.Count} 对象，-{TrisOf(res.stuck)} tri");
            foreach (var g in res.stuck.GroupBy(p => p.block).OrderBy(g => g.Key))
                rep.AppendLine($"    {g.Key}: {g.Count()} 对象，-{TrisOf(g.ToList())} tri");
            rep.AppendLine($"  浮空件：{res.floating.Count}（-fix 1 删除），-{TrisOf(res.floating)} tri");
            foreach (var g in res.floating.GroupBy(p => p.block).OrderBy(g => g.Key))
                rep.AppendLine($"    {g.Key}: {g.Count()} 件");
            if (res.report != null && res.report.visibilityComputed)
            {
                rep.AppendLine($"  可见面为 0（埋没/纯背面）：{res.buried.Count} 件");
                foreach (var g in res.buried.GroupBy(p => p.block).OrderBy(g => g.Key))
                    rep.AppendLine($"    {g.Key}: {g.Count()} 件  例: {string.Join(", ", g.Take(3).Select(p => p.name))}");
            }
            Debug.Log(rep.ToString());
        }

        // ------------------------------------------------------------------ 删除 / manifest

        static void DeletePieces(List<Piece> pieces, string op)
        {
            if (pieces == null || pieces.Count == 0)
            {
                Debug.Log($"[清理·{op}] 无对象可删。");
                return;
            }

            var man = new CleanupManifest
            {
                operation = op,
                scene = P_Scene,
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                count = pieces.Count,
                tris = TrisOf(pieces),
            };
            foreach (var p in pieces)
            {
                man.entries.Add(new ManifestEntry
                {
                    name = p.name,
                    block = p.block,
                    category = p.category,
                    material = p.material,
                    position = new[] { p.b.center.x, p.b.center.y, p.b.center.z },
                    size = new[] { p.b.size.x, p.b.size.y, p.b.size.z },
                    tris = p.tris,
                    reason = CategoryWhitelisted(p.category) ? "贴片类白名单" : "浮空件：下无支撑且侧无贴墙",
                });
            }
            WriteManifest(man, op);

            var seen = new HashSet<int>();
            int removed = 0;
            foreach (var p in pieces)
            {
                if (p.go == null) continue;
                int id = p.go.GetInstanceID();
                if (!seen.Add(id)) continue;
                UObject.DestroyImmediate(p.go);
                removed++;
            }

            CleanupEmptyContainers(pieces);
            if (P_Save) SaveScene();
            Debug.Log($"[清理·{op}] 已删除 {removed} 个对象，三角形 -{man.tris}；manifest: {Path.Combine(OutDir, $"CLEANUP_Manifest_{op}.json")}");
        }

        /// <summary>白名单类别被清空后，顺手删掉 DEC_Details 下的空类别/区块容器（只认空叶子，不碰原始对象）。</summary>
        static void CleanupEmptyContainers(List<Piece> pieces)
        {
            var cats = new HashSet<string>(pieces.Select(p => p.category));
            var root = GameObject.Find(DetailRoot);
            if (root == null) return;

            // 第一轮：删空的类别容器；第二轮：删空的区块容器
            for (int pass = 0; pass < 2; pass++)
            {
                var doomed = new List<GameObject>();
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null || t == root.transform || t.childCount != 0) continue;
                    if (t.GetComponents<Component>().Length > 1) continue;   // 只剩 Transform
                    bool isCat = pass == 0 && cats.Contains(t.name);
                    bool isBlock = pass == 1 && Regex.IsMatch(t.name, @"^S_?0[1-7]$");
                    if (isCat || isBlock) doomed.Add(t.gameObject);
                }
                foreach (var go in doomed) UObject.DestroyImmediate(go);
            }
        }

        static void WriteManifest(CleanupManifest man, string op)
        {
            Directory.CreateDirectory(OutDir);
            File.WriteAllText(Path.Combine(OutDir, $"CLEANUP_Manifest_{op}.json"),
                JsonUtility.ToJson(man, true), new UTF8Encoding(false));
        }

        // ------------------------------------------------------------------ 支托

        class SupportGroup
        {
            public string block, matName;
            public Material mat;
            public MeshBuilder mb = new MeshBuilder();
            public int count;
            public int Tris => mb.TriCount;
        }

        class SupportPlan
        {
            public Dictionary<string, SupportGroup> groups = new Dictionary<string, SupportGroup>();
            public List<SupportEntry> entries = new List<SupportEntry>();
            public int supported, noHost, tooBig;
        }

        class SupportEntry
        {
            public string piece, host, mode, generated;
            public int tris;
        }

        static SupportPlan BuildSupportPlan(ScanResult res)
        {
            var plan = new SupportPlan();
            foreach (var p in res.floating)
            {
                if (MaxExtent(p.b) > P_SupMax) { plan.tooBig++; continue; }

                Piece host;
                float topY;
                if (FindBottomHost(p, res.all, out host, out topY))
                {
                    float gap = p.b.min.y - topY;
                    if (gap < 0.05f) continue;
                    float r = Mathf.Clamp(Mathf.Min(p.b.size.x, p.b.size.z) * 0.18f, 0.08f, 0.5f);
                    var a = new Vector3(p.b.center.x, topY, p.b.center.z);
                    var b = new Vector3(p.b.center.x, p.b.min.y, p.b.center.z);
                    var g = GroupFor(plan, p);
                    g.mb.AddBeam(a, b, r, Mathf.Min(0.02f, r * 0.25f));
                    g.count++;
                    plan.entries.Add(new SupportEntry { piece = p.name, host = host.name, mode = "bottom-post", generated = g.block + "/Support" });
                    plan.supported++;
                    continue;
                }

                if (FindSideHost(p, res.all, out host))
                {
                    var anchor = NearestPointOnBox(host.b, p.b.center);
                    anchor.y = Mathf.Max(host.b.min.y, Mathf.Min(anchor.y, p.b.center.y - 1.5f));
                    var target = new Vector3(p.b.center.x, p.b.min.y + 0.05f, p.b.center.z);
                    if ((anchor - target).sqrMagnitude > 0.04f)
                    {
                        var g = GroupFor(plan, p);
                        g.mb.AddBeam(anchor, target, 0.12f, 0.02f);
                        g.count++;
                        plan.entries.Add(new SupportEntry { piece = p.name, host = host.name, mode = "side-brace", generated = g.block + "/Support" });
                        plan.supported++;
                        continue;
                    }
                }
                plan.noHost++;
            }
            return plan;
        }

        static SupportGroup GroupFor(SupportPlan plan, Piece p)
        {
            string key = p.block + "|" + p.material + "|" + (p.renderer != null && p.renderer.sharedMaterial != null
                ? p.renderer.sharedMaterial.GetInstanceID() : 0);
            SupportGroup g;
            if (!plan.groups.TryGetValue(key, out g))
            {
                g = new SupportGroup
                {
                    block = p.block,
                    matName = p.material,
                    mat = p.renderer != null ? p.renderer.sharedMaterial : null,
                };
                plan.groups[key] = g;
            }
            return g;
        }

        static bool FindBottomHost(Piece p, List<Piece> all, out Piece host, out float topY)
        {
            host = null; topY = float.MinValue;
            var b = p.b;
            foreach (var o in all)
            {
                if (ReferenceEquals(o, p)) continue;
                var ob = o.b;
                float ox = Mathf.Min(b.max.x, ob.max.x) - Mathf.Max(b.min.x, ob.min.x);
                float oz = Mathf.Min(b.max.z, ob.max.z) - Mathf.Max(b.min.z, ob.min.z);
                if (ox <= 0.05f || oz <= 0.05f) continue;
                if (ob.max.y > b.min.y + 0.05f) continue;
                if (b.min.y - ob.max.y > 40f) continue;
                if (ob.max.y > topY) { topY = ob.max.y; host = o; }
            }
            return host != null;
        }

        static bool FindSideHost(Piece p, List<Piece> all, out Piece host)
        {
            host = null;
            float best = 4.0f;
            var b = p.b;
            foreach (var o in all)
            {
                if (ReferenceEquals(o, p)) continue;
                var ob = o.b;
                float oy = Mathf.Min(b.max.y, ob.max.y) - Mathf.Max(b.min.y, ob.min.y);
                if (oy <= 0.1f) continue;

                float dx = Mathf.Max(0f, Mathf.Max(b.min.x - ob.max.x, ob.min.x - b.max.x));
                float dz = Mathf.Max(0f, Mathf.Max(b.min.z - ob.max.z, ob.min.z - b.max.z));
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < best) { best = d; host = o; }
            }
            return host != null;
        }

        static Vector3 NearestPointOnBox(Bounds b, Vector3 p)
        {
            return new Vector3(
                Mathf.Clamp(p.x, b.min.x, b.max.x),
                Mathf.Clamp(p.y, b.min.y, b.max.y),
                Mathf.Clamp(p.z, b.min.z, b.max.z));
        }

        static void ExecuteSupport(SupportPlan plan)
        {
            if (plan.groups.Count == 0)
            {
                Debug.Log("[清理·支托] 无可生成支托。");
                return;
            }

            var root = FindOrCreateRoot(MapskinRoot);
            if (!AssetDatabase.IsValidFolder(SupMeshDir)) AssetDatabase.CreateFolder("Assets/Art/EnvSkins", "Building");
            AssetDatabase.StartAssetEditing();
            int made = 0;
            int genTris = 0;
            try
            {
                foreach (var kv in plan.groups.OrderBy(k => k.Key))
                {
                    var g = kv.Value;
                    if (g.mat == null) continue;

                    var blockGo = FindOrCreateChild(root, g.block);
                    var sup = FindOrCreateChild(blockGo, "Support");
                    if (sup.transform.childCount > 0)
                    {
                        if (!P_SupClear)
                        {
                            Debug.Log($"[清理·支托] {g.block}/Support 已有内容，跳过（-supclear 1 可重建）。");
                            foreach (var e in plan.entries.Where(e => e.generated == g.block + "/Support"))
                                e.generated = "(skipped: existing Support)";
                            continue;
                        }
                        // 重建：先清旧支托（只动自己名下的 Support 子物体）
                        var old = new List<GameObject>();
                        for (int c = 0; c < sup.transform.childCount; c++) old.Add(sup.transform.GetChild(c).gameObject);
                        foreach (var o in old) UObject.DestroyImmediate(o);
                    }

                    string meshName = $"MAPSKIN_Support_Cleanup_{g.block}_{made:00}";
                    var mesh = g.mb.Build(meshName);
                    string path = $"{SupMeshDir}/{meshName}.asset";
                    if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
                    AssetDatabase.CreateAsset(mesh, path);

                    var go = new GameObject(meshName);
                    go.transform.SetParent(sup.transform, false);
                    go.transform.position = Vector3.zero;
                    go.transform.rotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one;
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = g.mat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    mr.receiveShadows = true;

                    foreach (var e in plan.entries.Where(e => e.generated == g.block + "/Support"))
                        e.generated = meshName;
                    made++;
                    genTris += g.Tris;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            var man = new CleanupManifest
            {
                operation = "Support",
                scene = P_Scene,
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                count = made,
                tris = genTris,
            };
            foreach (var e in plan.entries)
                man.entries.Add(new ManifestEntry
                {
                    name = e.piece,
                    block = "",
                    category = "Support",
                    material = "",
                    position = new[] { 0f, 0f, 0f },
                    size = new[] { 0f, 0f, 0f },
                    tris = 0,
                    reason = $"{e.mode}: {e.host} -> {e.generated}",
                });
            WriteManifest(man, "Support");

            if (P_Save) SaveScene();
            Debug.Log($"[清理·支托] 已生成 {made} 个 Support 对象，覆盖 {plan.supported} 件悬挑件；三角形 +{man.tris}");
        }

        static GameObject FindOrCreateRoot(string name)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
                foreach (var g in scene.GetRootGameObjects())
                    if (g != null && g.name == name) return g;
            return new GameObject(name);
        }

        static GameObject FindOrCreateChild(GameObject parent, string name)
        {
            var t = parent.transform.Find(name);
            if (t != null) return t.gameObject;
            var g = new GameObject(name);
            g.transform.SetParent(parent.transform, false);
            return g;
        }

        /// <summary>世界坐标三角形汤；八边形截面挤出，无需旋转对齐。</summary>
        class MeshBuilder
        {
            readonly List<Vector3> _verts = new List<Vector3>();
            readonly List<Vector3> _normals = new List<Vector3>();
            readonly List<int> _tris = new List<int>();

            public int TriCount => _tris.Count / 3;

            public void AddBeam(Vector3 a, Vector3 b, float radius, float bevel)
            {
                var axis = b - a;
                if (axis.sqrMagnitude < 1e-8f) return;
                axis.Normalize();
                var u = Vector3.Cross(axis, Vector3.up);
                if (u.sqrMagnitude < 1e-6f) u = Vector3.Cross(axis, Vector3.right);
                u.Normalize();
                var w = Vector3.Cross(axis, u).normalized;   // (u,w,axis) 右手系

                float r = Mathf.Max(radius, 0.01f);
                float c = Mathf.Clamp(bevel, 0.001f, r * 0.45f);
                var pts = new[]
                {
                    new Vector2(r, r - c), new Vector2(r - c, r),
                    new Vector2(-(r - c), r), new Vector2(-r, r - c),
                    new Vector2(-r, -(r - c)), new Vector2(-(r - c), -r),
                    new Vector2(r - c, -r), new Vector2(r, -(r - c)),
                };

                for (int i = 0; i < pts.Length; i++)
                {
                    var p0 = pts[i];
                    var p1 = pts[(i + 1) % pts.Length];
                    var a0 = a + u * p0.x + w * p0.y;
                    var a1 = a + u * p1.x + w * p1.y;
                    var b0 = b + u * p0.x + w * p0.y;
                    var b1 = b + u * p1.x + w * p1.y;
                    AddQuad(a0, a1, b1, b0, (a0 + a1) * 0.5f - a);
                }

                for (int i = 0; i < pts.Length; i++)
                {
                    var p0 = pts[i];
                    var p1 = pts[(i + 1) % pts.Length];
                    AddTri(a + u * p0.x + w * p0.y,
                           a + u * p1.x + w * p1.y,
                           a, -axis);
                    AddTri(b,
                           b + u * p1.x + w * p1.y,
                           b + u * p0.x + w * p0.y, axis);
                }
            }

            void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward)
            {
                var n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-12f) return;
                n.Normalize();
                if (Vector3.Dot(n, outward) < 0f) { var tmp = b; b = d; d = tmp; n = -n; }
                int i0 = _verts.Count;
                Add(a, n); Add(b, n); Add(c, n); Add(d, n);
                _tris.Add(i0); _tris.Add(i0 + 1); _tris.Add(i0 + 2);
                _tris.Add(i0); _tris.Add(i0 + 2); _tris.Add(i0 + 3);
            }

            void AddTri(Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
            {
                var n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-12f) return;
                n.Normalize();
                if (Vector3.Dot(n, outward) < 0f) { var tmp = b; b = c; c = tmp; n = -n; }
                int i0 = _verts.Count;
                Add(a, n); Add(b, n); Add(c, n);
                _tris.Add(i0); _tris.Add(i0 + 1); _tris.Add(i0 + 2);
            }

            void Add(Vector3 p, Vector3 n)
            {
                _verts.Add(p);
                _normals.Add(n);
            }

            public Mesh Build(string name)
            {
                var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                m.SetVertices(_verts);
                m.SetNormals(_normals);
                m.SetTriangles(_tris, 0);
                m.RecalculateBounds();
                return m;
            }
        }

        // ------------------------------------------------------------------ 工具

        static bool IsGenerated(string n)
        {
            return !string.IsNullOrEmpty(n) &&
                   (n.StartsWith("DEC_", StringComparison.Ordinal) || n.StartsWith("MAPSKIN_", StringComparison.Ordinal));
        }

        static bool CategoryWhitelisted(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return false;
            for (int i = 0; i < P_Whitelist.Length; i++)
                if (string.Equals(P_Whitelist[i], cat, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static readonly Regex BlockRe = new Regex(@"(?:^|[^A-Za-z0-9])S_?0([1-7])(?![0-9])", RegexOptions.Compiled);

        static string BlockOf(GameObject go)
        {
            for (var t = go.transform; t != null; t = t.parent)
            {
                var m = BlockRe.Match(t.name);
                if (m.Success) return "S_0" + m.Groups[1].Value;
            }
            var m2 = BlockRe.Match(go.name);
            return m2.Success ? "S_0" + m2.Groups[1].Value : "S_00";
        }

        /// <summary>
        /// 兼容两套生成器命名：
        ///   DEC_S03_HexNode_001（S03 连写）→ 第 2 段
        ///   DEC_S_04_Hub_163    （S_04 分开）→ 第 3 段
        ///   MAPSKIN_Arc_001                → 第 2 段
        /// </summary>
        static string CategoryOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            if (name.StartsWith("MAPSKIN_", StringComparison.Ordinal))
            {
                var p = name.Split('_');
                return p.Length >= 2 ? p[1] : "?";
            }
            if (!name.StartsWith("DEC_", StringComparison.Ordinal)) return "?";
            var rest = name.Substring(4);
            var parts = rest.Split('_');
            if (parts.Length >= 2 && parts[0].Length == 3 && parts[0][0] == 'S' && char.IsDigit(parts[0][1]))
                return parts[1];
            if (parts.Length >= 3 && parts[0] == "S" && parts[1].Length == 2 && char.IsDigit(parts[1][0]))
                return parts[2];
            return parts.Length >= 2 ? parts[1] : "?";
        }

        static int ThinAxis(Vector3 s)
        {
            if (s.y <= s.x && s.y <= s.z) return 1;
            if (s.z <= s.x && s.z <= s.y) return 2;
            return 0;
        }

        static float GetMin(Bounds b, int axis) => axis == 0 ? b.min.x : axis == 1 ? b.min.y : b.min.z;
        static float GetMax(Bounds b, int axis) => axis == 0 ? b.max.x : axis == 1 ? b.max.y : b.max.z;

        static float MaxExtent(Bounds b) => Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));

        static int TrisOf(List<Piece> pieces)
        {
            int n = 0;
            for (int i = 0; i < pieces.Count; i++) n += pieces[i].tris;
            return n;
        }

        static int TriCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            int n = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) n += (int)(mesh.GetIndexCount(s) / 3);
            return n;
        }

        static string OutDir
        {
            get
            {
                if (!string.IsNullOrEmpty(P_OutDir)) return P_OutDir;
                // Assets 的上两级 = 外层仓库根（F:/VSCode/taptap-game-jem），与既有审计工具口径一致
                var root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
                return Path.Combine(root, "_session_audit");
            }
        }

        static void EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == P_Scene) return;
            EditorSceneManager.OpenScene(P_Scene, OpenSceneMode.Single);
            Debug.Log($"[清理] 已打开场景 {P_Scene}");
        }

        static void SaveScene()
        {
            var sc = EditorSceneManager.GetActiveScene();
            if (sc.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(sc);
                EditorSceneManager.SaveScene(sc);
                Debug.Log($"[清理] 场景已存盘：{sc.path}");
            }
        }

        // ------------------------------------------------------------------ 参数

        static void ParseArgs()
        {
            // 每次重置，避免菜单调用被上一次批处理参数污染
            P_SizeMax = 0.35f; P_CountMin = 100; P_AttachTol = 0.35f; P_AttachRatio = 0.60f;
            P_FlatThin = 0.18f; P_FlatSpan = 1.60f;
            P_VisDist = 150f; P_FloatMax = 0f; P_SupMax = 20f; P_Vis = false; P_SupClear = false;
            P_Run = false; P_Fix = false; P_Dry = false; P_Save = true; P_Verbose = false;
            P_Scene = DefaultScene; P_OutDir = null;
            P_Whitelist = new[] { "HexNode", "HexFrame", "HexGlow" };

            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string k = a[i].ToLowerInvariant();
                string v = i + 1 < a.Length ? a[i + 1] : null;
                switch (k)
                {
                    case "-run": P_Run = Flag(v); i++; break;
                    case "-fix": P_Fix = Flag(v); i++; break;
                    case "-dry": P_Dry = Flag(v); i++; break;
                    case "-vis": P_Vis = Flag(v); i++; break;
                    case "-save": P_Save = Flag(v); i++; break;
                    case "-verbose": P_Verbose = Flag(v); i++; break;
                    case "-supclear": P_SupClear = Flag(v); i++; break;
                    case "-scene": if (v != null) P_Scene = v; i++; break;
                    case "-out": if (v != null) P_OutDir = v; i++; break;
                    case "-tol": P_AttachTol = Fl(v, P_AttachTol); i++; break;
                    case "-size": P_SizeMax = Fl(v, P_SizeMax); i++; break;
                    case "-count": P_CountMin = Mathf.RoundToInt(Fl(v, P_CountMin)); i++; break;
                    case "-ratio": P_AttachRatio = Fl(v, P_AttachRatio); i++; break;
                    case "-visdist": P_VisDist = Fl(v, P_VisDist); i++; break;
                    case "-floatmax": P_FloatMax = Fl(v, P_FloatMax); i++; break;
                    case "-supmax": P_SupMax = Fl(v, P_SupMax); i++; break;
                    case "-stuckcats":
                        if (!string.IsNullOrEmpty(v))
                            P_Whitelist = v.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                        i++; break;
                }
            }

            P_AttachTol = Mathf.Clamp(P_AttachTol, 0.01f, 5f);
            P_SizeMax = Mathf.Max(P_SizeMax, 0.01f);
            P_CountMin = Mathf.Max(P_CountMin, 1);
            P_AttachRatio = Mathf.Clamp01(P_AttachRatio);
            P_VisDist = Mathf.Clamp(P_VisDist, 5f, 1000f);
            P_SupMax = Mathf.Clamp(P_SupMax, 0.5f, 200f);
            if (P_Whitelist == null || P_Whitelist.Length == 0) P_Whitelist = new[] { "HexNode", "HexFrame", "HexGlow" };
        }

        static bool Flag(string v)
        {
            if (string.IsNullOrEmpty(v)) return true;
            return v != "0" && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
        }

        static float Fl(string v, float fallback)
        {
            float f;
            return v != null && float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out f) ? f : fallback;
        }

        // ------------------------------------------------------------------ 序列化结构

        [Serializable] public class StatBlock
        {
            public string block;
            public int renderers, decorObjects, decorTris, floating, stuckOn, buried;
        }

        [Serializable] public class StatCat
        {
            public string block, category, reason;
            public int objects, tris, units;
            public float avgUnitSize, avgUnitThin, avgUnitMax, attachedRatio;
            public bool qualifies, qualifiesFlat, whitelisted, countOk, attachOk, strictSizeOk, flatSizeOk;
            public string[] examples;
        }

        [Serializable] public class StatPiece
        {
            public string name, block, category, material, reason;
            public float[] position, size;
            public int tris, units, visibleFaces;
            public float unitSize;
            public bool attached, bottomSupport, sideSupport, topSupport;
        }

        [Serializable] public class CleanupPlan
        {
            public int stuckOnObjects, stuckOnTris;
            public int floatingObjects, floatingTris;
            public int buriedObjects, supportCandidates;
        }

        [Serializable] public class CleanupReport
        {
            public string generatedAt, scene, note;
            public int renderers, decorObjects, decorTris;
            public bool visibilityComputed;
            public string[] whitelist;
            public StatBlock[] blocks;
            public StatCat[] stuckOnCandidates;
            public StatPiece[] floating;
            public StatPiece[] buried;
            public StatPiece[] decorVisibility;
            public CleanupPlan plan;
        }

        [Serializable] public class ManifestEntry
        {
            public string name, block, category, material, reason;
            public float[] position, size;
            public int tris;
        }

        [Serializable] public class CleanupManifest
        {
            public string operation, scene, timestamp;
            public int count, tris;
            public List<ManifestEntry> entries = new List<ManifestEntry>();
        }
    }
}
