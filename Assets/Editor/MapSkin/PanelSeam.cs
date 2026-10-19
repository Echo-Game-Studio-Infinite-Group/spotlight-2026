// =====================================================================================
// PanelSeam —— 拼接板/凹槽几何治理：把"该由法线贴图表达的浅槽"从几何里拿掉，
// 把"同一平面内被随意切碎的拼接板"按材质/共面/间隙合并，并对生成件做 3 Lines Rule 门禁。
//
// 背景（用户口径）：
//   · 浅凹槽/分缝应当由贴图法线表达，不应生成一片片薄几何条；
//   · 拼接板不能胡乱切成碎块，切分必须服从贴图分格与硬表面刻线规范；
//   · 所有关卡共用同一套逻辑（本工具不写任何 per-level 分支）。
//
// ── Skill 规则落码（unity-panel-line-scribing-architect，只摘与几何门禁相关的部分）──
//   R1 钝角铁律：连续转折内角必须 ≥ 90°，禁止 <90° 锐角折返（Z 字尖刺）。
//      → SkillObtusePass()：内角 < 90°-tol 记不合格；AuditAngles 只报告，不改几何。
//   R2 135° 量化：矩形/平面结构的主干线只允许水平/垂直，斜切只允许 45° 斜率
//      （与主干形成 135° 钝角），即内角集合 = {90°, 135°}；曲线件单独剔除。
//      → SkillQuantDeviation()：内角到 {90,135} 的最小偏差；> 1° 记量化不合格。
//   R3 转角走弧/倒角：转角不允许裸 90° 尖角，必须由 45° 切面（本工程用 chamfer 代 arc）过渡。
//      → SkillChamferProxy()：统计 45° 斜率面；无 45° 面的方盒件记"未倒角"。
//   R4 平行内缩保持等宽：同一缝/压条的线宽极差必须为 0（工程容差内）。
//      → SkillEqualWidthPass()：widthMax-widthMin ≤ tol。
//   R5 边缘正交汇入：分割线端点必须以 90° 汇入外框/边界，不得斜插边界。
//      → SkillRightAngleTermination()：入边方向与外框法线点积 ≈ 0。
//   R6 三线法则：有效刻线单元至少 3 段线 + 2 个钝角转折；单折（2 段 1 折）不算单元。
//      → SkillThreeLineUnit()：segments ≥ 3 且 corners ≥ 2。
//   另外遵守 §5 拼接协议：贴图按 3.6m（墙）/4.8m（地）基准分格，合并后必须保留每块
//   拼接板自己的 UV 岛（0–1 独立分区），不得把整面重新拉伸成 0–1。
//
// ── 本工具的四个动作 ──
//   Scan            扫描 DEC_*/MAPSKIN_* 里厚度 ≤12mm、宽度 ≤60mm 的 Seam/Trim/Joint 类薄条
//                   （计数 + 三角；并按类别/区块/对象汇总）。只读。
//   Flatten         删除这些薄条；宿主大面不做任何几何补偿（凹槽交给贴图法线）；
//                   输出 manifest（世界位置/尺寸/朝向/宿主/UV 范围），供贴图侧在对应位置画槽。
//   MergeCoplanar   同平面、同材质、间隙 ≤2mm 的相邻拼接板合并成一个 mesh，减少对象与描边碎线；
//                   合并后每块板仍保留自己的 UV 岛（0–1 独立分区），不是整面拉伸。
//   AuditAngles     对生成件多边形做 R1/R2/R3/R4/R6 门禁，只报告不合格项（不写盘、不改场景）。
//
// ── 纪律 ──
//   · 只处理名字以 DEC_ / MAPSKIN_ 开头的生成件；原始 S_0x_* 只读（仅用于找"宿主大面"）。
//   · 不写 prefab、不 new Material、不启动 Unity；材质只做已有引用重排。
//   · 改动可回退：改动前把原 mesh / 原材质数组 / 原 active 记进状态文件；
//     Clear 逐条还原并删掉本工具生成的 mesh 资产（备份 mesh 保留，用于还原内嵌网格）。
//   · 幂等：Flatten 后薄条消失、Merge 后同组只剩一个对象，重复跑不会重复处理。
//
// 菜单：MapSkin/拼接板/① 扫描薄条(只读)
//       MapSkin/拼接板/② 干跑（薄条压平 + 拼接合并）
//       MapSkin/拼接板/③ 执行（薄条压平 + 拼接合并）
//       MapSkin/拼接板/④ 角度门禁审计(只读)
//       MapSkin/拼接板/⑤ 清除本工具改动
// 批处理：-executeMethod MapSkin.PanelSeam.Scan / .DryRun / .Run / .AuditAngles / .Clear
// 可选参数：-ps-thickmm 12  -ps-widthmm 60  -ps-gapmm 2  -ps-exemptmm 20  -ps-uvisland 1
//           -ps-dec 1  -ps-mapskin 1  -ps-maxtris 300000  -ps-verbose 0
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MapSkin
{
    public static class PanelSeam
    {
        // ================================================================ 常量 / 参数

        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string OutDir = "Assets/Art/EnvSkins/PanelSeam";
        const string BackupDir = OutDir + "/Backup";
        const string ManifestPath = OutDir + "/panel_seam_manifest.json";
        const string StatePath = OutDir + "/panel_seam_state.json";

        /// <summary>薄条厚度上限（米）。用户口径 12mm；Seam/Joint 类额外给 20mm 豁免（见 P_ExemptMm）。</summary>
        static float P_ThickMm = 12f;
        /// <summary>薄条宽度上限（米）。用户口径 60mm。</summary>
        static float P_WidthMm = 60f;
        /// <summary>拼接板"相邻"间隙上限（米）。用户口径 2mm。</summary>
        static float P_GapMm = 2f;
        /// <summary>同类薄条的宽度极差容差（毫米），用于 R4 等宽校验。</summary>
        static float P_WidthTolMm = 0.05f;
        /// <summary>Seam/Joint 类的厚度豁免：实际生成的结构缝是 60×15mm，严格 12mm 会漏掉它们。</summary>
        static float P_ExemptMm = 20f;
        /// <summary>合并时是否把每块板的 UV 收进独立 0–1 岛（1=是；0=原样保留）。</summary>
        static int P_UvIsland = 1;
        static int P_ScanDec = 1;
        static int P_ScanMapskin = 1;
        static int P_Verbose = 0;
        /// <summary>单网格三角上限：超过只报跳过，防止误扫换皮浮雕/地形网格把批处理拖死。</summary>
        static int P_MaxTrisPerMesh = 300000;

        const float WeldEps = 1e-5f;          // 0.01mm：焊接同一件的面顶点，不做跨件吸附
        const float MinStripLen = 0.05f;      // 5cm：短于此不算"条"
        const float UvPad = 0.02f;            // UV 岛四周留 2% 安全边

        // 薄条类别 token（对象名/容器名按 _ 切分后逐段匹配）
        static readonly string[] ThinCatTokens =
        {
            "SEAM", "SEAMLINE", "TRIM", "TRIMLINE", "JOINT", "GAP",
            "GROOVE", "REVEAL", "SCRIBE", "PANELLINE", "PANEL_LINE"
        };

        // 合并时明确排除的类别 token：这些即使片状也不做"拼接板"合并
        static readonly string[] NeverMergeTokens =
        {
            "LIGHT", "GLOW", "EMISSIVE", "PIPE", "BOLT", "RIB", "FRAME",
            "SCREEN", "EQUIP", "FOCUS", "VENT", "RAIL", "TRUSS", "CABLE",
            "SEAM", "TRIM", "JOINT", "GAP", "GROOVE", "REVEAL", "SCRIBE"
        };

        // ================================================================ Skill 校验函数

        /// <summary>R1 钝角铁律：内角 ≥ 90°（含容差）。返回 false = 出现锐角折返。</summary>
        static bool SkillObtusePass(float interiorDeg, float tolDeg = 0.5f)
            => interiorDeg >= 90f - tolDeg;

        /// <summary>
        /// R2 135° 量化偏差：矩形/平面结构的内角只允许 90° 或 135°。
        /// 返回到最近合法角度的偏差（度）；曲线件（多边形端盖）不调用本函数。
        /// </summary>
        static float SkillQuantDeviation(float interiorDeg)
        {
            float d90 = Mathf.Abs(interiorDeg - 90f);
            float d135 = Mathf.Abs(interiorDeg - 135f);
            return Mathf.Min(d90, d135);
        }

        /// <summary>R3 转角走弧（本工程以 45° chamfer 代弧）：法线与任一主轴夹角 ≈45°。</summary>
        static bool SkillChamferProxy(Vector3 faceNormal, Vector3[] obbAxes)
        {
            const float cos45 = 0.70710678f;
            for (int i = 0; i < obbAxes.Length; i++)
            {
                float d = Mathf.Abs(Vector3.Dot(faceNormal.normalized, obbAxes[i].normalized));
                if (Mathf.Abs(d - cos45) <= 0.03f) return true;
            }
            return false;
        }

        /// <summary>R4 平行内缩保持等宽：同一组缝/压条的宽度极差 ≤ tol。</summary>
        static bool SkillEqualWidthPass(float minW, float maxW, float tolM)
            => (maxW - minW) <= Mathf.Max(1e-6f, tolM);

        /// <summary>R5 边缘正交汇入：入边方向与外框法线点积 ≈0（即沿边框切向，90° 汇入）。</summary>
        static bool SkillRightAngleTermination(Vector3 edgeDir, Vector3 frameNormal, float tol = 0.05f)
            => Mathf.Abs(Vector3.Dot(edgeDir.normalized, frameNormal.normalized)) <= tol;

        /// <summary>R6 三线法则：有效刻线单元至少 3 段线、2 个转角（单折不算）。</summary>
        static bool SkillThreeLineUnit(int segments, int corners)
            => segments >= 3 && corners >= 2;

        // ================================================================ 入口

        [MenuItem("MapSkin/拼接板/① 扫描薄条(只读)")]
        public static void ScanMenu() => Scan();

        [MenuItem("MapSkin/拼接板/② 干跑（薄条压平 + 拼接合并）")]
        public static void DryRunMenu() => DryRun();

        [MenuItem("MapSkin/拼接板/③ 执行（薄条压平 + 拼接合并）")]
        public static void RunMenu()
        {
            ParseArgs();
            if (!EnsureScene()) return;
            var scan = Collect(null);
            if (!EditorUtility.DisplayDialog("拼接板治理",
                    $"将删除 {scan.ThinCount} 条薄几何（{scan.ThinTris:N0} 三角），并把同平面同材质的相邻拼接板合并。\n" +
                    $"只处理 DEC_*/MAPSKIN_* 生成件，原始 S_0x_* 不动。\n\n继续？", "执行", "取消"))
            {
                Debug.Log("[拼接板] 用户取消");
                return;
            }
            Run();
        }

        [MenuItem("MapSkin/拼接板/④ 角度门禁审计(只读)")]
        public static void AuditAnglesMenu() => AuditAngles();

        [MenuItem("MapSkin/拼接板/⑤ 清除本工具改动")]
        public static void ClearMenu()
        {
            if (!EditorUtility.DisplayDialog("清除拼接板改动",
                    "按状态文件还原 mesh/材质/active，并删除本工具生成的 mesh 资产？\n" +
                    "（备份 mesh 会保留，供内嵌网格还原）", "清除", "取消"))
                return;
            Clear();
        }

        /// <summary>只读：扫描薄条候选与近失项，打印计数/三角/分布。批处理入口。</summary>
        public static void Scan()
        {
            ParseArgs();
            if (!EnsureScene()) return;
            var scan = Collect(null);
            var rep = new StringBuilder();
            ReportScan(scan, rep);
            Debug.Log(rep.ToString());
        }

        /// <summary>干跑：打印 Flatten/Merge 计划，零写盘。批处理入口。</summary>
        public static void DryRun()
        {
            ParseArgs();
            if (!EnsureScene()) return;
            Execute(true);
        }

        /// <summary>执行：Flatten + MergeCoplanar，写 manifest/状态/场景。批处理入口。</summary>
        public static void Run()
        {
            ParseArgs();
            if (!EnsureScene()) return;
            Execute(false);
        }

        /// <summary>只读：3 Lines Rule 门禁审计（R1/R2/R3/R4/R6）。批处理入口。</summary>
        public static void AuditAngles()
        {
            ParseArgs();
            if (!EnsureScene()) return;
            AuditCore();
        }

        /// <summary>清除：按状态文件还原，再删本工具生成的 mesh 资产。批处理入口。</summary>
        public static void Clear()
        {
            ParseArgs();
            if (!EnsureScene()) return;
            ClearCore();
        }

        /// <summary>单独执行"薄条压平"（会重新扫描；写 manifest）。</summary>
        public static void Flatten()
        {
            ParseArgs();
            if (!EnsureScene()) return;
            var rep = new StringBuilder();
            var scan = Collect(rep);
            var state = LoadState();
            FlattenCore(scan, state, rep);
            SaveManifest(scan, rep);
            SaveState(state);
            SaveScene();
            Debug.Log(rep.ToString());
        }

        /// <summary>单独执行"共面拼接板合并"（会先重新扫描当前场景）。</summary>
        public static void MergeCoplanar()
        {
            ParseArgs();
            if (!EnsureScene()) return;
            var rep = new StringBuilder();
            var scan = Collect(rep);
            var state = LoadState();
            MergeCore(scan, state, rep);
            SaveState(state);
            SaveScene();
            Debug.Log(rep.ToString());
        }

        // ================================================================ 总流程

        static void Execute(bool dry)
        {
            var rep = new StringBuilder();
            rep.AppendLine($"[拼接板] {(dry ? "干跑（零写盘）" : "执行")}  场景={ScenePath}  " +
                           $"薄条≤{P_ThickMm:F0}mm×{P_WidthMm:F0}mm  间隙≤{P_GapMm:F1}mm  " +
                           $"扫描 {(P_ScanDec == 1 ? "DEC_*" : "")}{(P_ScanMapskin == 1 ? " MAPSKIN_*" : "")}");

            var scan = Collect(rep);
            ReportScan(scan, rep);
            ReportNearMiss(scan, rep);

            if (dry)
            {
                PlanFlatten(scan, rep);
                PlanMerge(scan, rep);
                Debug.Log(rep.ToString());
                return;
            }

            var state = LoadState();
            int flatObjects = FlattenCore(scan, state, rep);
            SaveManifest(scan, rep);

            // Flatten 改了 mesh，重新扫描后再做合并（避免拿旧 part 索引操作新 mesh）
            var scan2 = Collect(rep);
            int mergeObjects = MergeCore(scan2, state, rep);

            SaveState(state);
            SaveScene();
            rep.AppendLine($"[完成] Flatten 改动对象 {flatObjects} 个；Merge 改动对象 {mergeObjects} 个；" +
                           $"manifest → {ManifestPath}");
            Debug.Log(rep.ToString());
        }

        // ================================================================ 扫描与几何分解

        sealed class ScanResult
        {
            public readonly List<Part> All = new List<Part>();
            public readonly List<Part> Thin = new List<Part>();
            public readonly List<Part> NearMiss = new List<Part>();
            public readonly List<Part> Plates = new List<Part>();
            public readonly List<RendererRec> Hosts = new List<RendererRec>();
            public int ModifiableObjects;
            public int SkippedBigMeshes;
            public int SkippedInactive;
            public int TotalTris;

            public int ThinTris { get { int n = 0; for (int i = 0; i < Thin.Count; i++) n += Thin[i].TriCount; return n; } }
            public int ThinCount { get { return Thin.Count; } }
        }

        sealed class Part
        {
            public MeshFilter mf;
            public Mesh mesh;
            public Transform tr;
            public string ownerName;
            public string ownerPath;
            public string block;
            public string category;
            public Material material;
            public RawPart raw;
            public int indexInMesh;

            public Vector3 oCenter;          // OBB 中心（世界）
            public Vector3[] axes;           // OBB 轴，按 ext 升序
            public float[] ext;              // OBB 三边（升序：厚度/宽度/长度）
            public Vector3 aabbMin, aabbMax; // 世界 AABB
            public Vector2 uvMin, uvMax;

            public bool strictThin;          // 严格符合 12mm/60mm
            public bool exemptThin;          // Seam/Joint 类 20mm 豁免
            public bool categoryThin;        // 名字/容器命中 Seam/Trim/Joint...
            public bool nearMiss;
            public bool hasMpb;              // 渲染器带 MaterialPropertyBlock（逐对象色调），跨对象合并会丢参数

            public RendererRec host;         // 宿主大面（manifest 用；只读引用，不改宿主）

            public int TriCount { get { return raw == null ? 0 : raw.triIndices.Length; } }
            public string Key { get { return ownerPath + "#" + indexInMesh; } }
            public Vector3 ThinNormal { get { return axes[0]; } }
            public float ThicknessM { get { return ext[0]; } }
            public float WidthM { get { return ext[1]; } }
            public float LengthM { get { return ext[2]; } }
        }

        sealed class RawPart
        {
            public int[] triIndices;    // 三角形序号（0..triCount-1）
            public int[] verts;         // 用到的原始顶点索引（去重）
            public int submesh;
        }

        sealed class RendererRec
        {
            public Renderer r;
            public string name;
            public string path;
            public string block;
            public Bounds bounds;
            public bool modifiable;
        }

        static ScanResult Collect(StringBuilder rep)
        {
            var res = new ScanResult();
            var meshFilters = Object.FindObjectsOfType<MeshFilter>(true);
            var seenMeshes = new Dictionary<int, List<RawPart>>();

            foreach (var mf in meshFilters)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var go = mf.gameObject;
                if (go == null) continue;
                var n = go.name ?? string.Empty;
                bool modifiable = IsModifiableName(n);
                if (!modifiable) continue;
                if (!go.activeInHierarchy) { res.SkippedInactive++; continue; }

                var mesh = mf.sharedMesh;
                int triCount = mesh.triangles.Length / 3;
                if (triCount == 0) continue;
                if (triCount > P_MaxTrisPerMesh) { res.SkippedBigMeshes++; continue; }

                var r = mf.GetComponent<Renderer>();
                if (r == null || !r.enabled) continue;

                List<RawPart> raws;
                if (!seenMeshes.TryGetValue(mesh.GetInstanceID(), out raws))
                {
                    raws = Decompose(mesh);
                    seenMeshes[mesh.GetInstanceID()] = raws;
                }

                string ownerPath = PathOf(mf.transform);
                string block = BlockOf(mf.transform);
                string category = CategoryOf(mf.transform);
                var mats = r.sharedMaterials;
                var tr = mf.transform;
                bool hasMpb = r.HasPropertyBlock();

                for (int pi = 0; pi < raws.Count; pi++)
                {
                    var raw = raws[pi];
                    if (raw == null || raw.triIndices == null || raw.triIndices.Length == 0) continue;
                    var p = new Part
                    {
                        mf = mf, mesh = mesh, tr = tr,
                        ownerName = n, ownerPath = ownerPath, block = block, category = category,
                        material = MaterialOf(mats, raw.submesh), raw = raw, indexInMesh = pi,
                        hasMpb = hasMpb,
                    };
                    ComputeObb(p);
                    ComputeUvBounds(p);
                    p.categoryThin = IsThinCategory(category) || IsThinCategory(n);
                    ClassifyThin(p);
                    res.All.Add(p);
                    res.TotalTris += p.TriCount;
                    if (p.strictThin || p.exemptThin) res.Thin.Add(p);
                    else if (p.nearMiss) res.NearMiss.Add(p);
                    else if (IsPlate(p)) res.Plates.Add(p);
                }
                res.ModifiableObjects++;
            }

            // 宿主候选：包含原始 S_0x_* 在内的所有渲染件（只读，仅取 bounds）
            foreach (var r in Object.FindObjectsOfType<Renderer>(true))
            {
                if (r == null || !r.enabled) continue;
                if (r.GetComponent<Camera>() != null) continue;
                if (!r.gameObject.activeInHierarchy) continue;
                var rec = new RendererRec
                {
                    r = r, name = r.gameObject.name, path = PathOf(r.transform),
                    block = BlockOf(r.transform), bounds = r.bounds,
                    modifiable = IsModifiableName(r.gameObject.name),
                };
                res.Hosts.Add(rec);
            }

            AssignHosts(res);
            if (rep != null)
                rep.AppendLine($"[扫描] 生成件对象 {res.ModifiableObjects} 个、部件 {res.All.Count:N0} 个、" +
                               $"三角 {res.TotalTris:N0}；跳过（超三角 {res.SkippedBigMeshes} / 未激活 {res.SkippedInactive}）");
            return res;
        }

        static bool IsModifiableName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            if (P_ScanDec == 1 && n.StartsWith("DEC_", StringComparison.Ordinal)) return true;
            if (P_ScanMapskin == 1 && n.StartsWith("MAPSKIN_", StringComparison.Ordinal)) return true;
            return false;
        }

        static Material MaterialOf(Material[] mats, int submesh)
        {
            if (mats == null || mats.Length == 0) return null;
            if (submesh >= 0 && submesh < mats.Length) return mats[submesh];
            return mats[0];
        }

        // ---------------------------------------------------------------- 网格 → 连通件

        /// <summary>
        /// 按"位置焊接 + 三角形连通"把一个合并网格拆成单个生成件。
        /// 生成器是平直硬边（每面独立顶点），所以必须先按位置焊接；焊接容差 0.01mm，
        /// 只合并同一件的面顶点，不把间隙 ≥2mm 的两块板吸成一件。
        /// </summary>
        static List<RawPart> Decompose(Mesh mesh)
        {
            var vs = mesh.vertices;
            var tris = mesh.triangles;
            int vc = vs.Length;
            int tc = tris.Length / 3;
            var outp = new List<RawPart>();
            if (vc == 0 || tc == 0) return outp;

            // 1) 位置焊接
            var canon = new int[vc];
            var weld = new Dictionary<Key3, int>(vc);
            for (int i = 0; i < vc; i++)
            {
                var k = Key3.Of(vs[i], 1f / WeldEps);
                int id;
                if (!weld.TryGetValue(k, out id))
                {
                    id = i;
                    weld[k] = id;
                }
                canon[i] = id;
            }

            // 2) 三角形连通（并查集）
            var uf = new UnionFind(vc);
            for (int t = 0; t < tc; t++)
            {
                int a = canon[tris[t * 3]], b = canon[tris[t * 3 + 1]], c = canon[tris[t * 3 + 2]];
                uf.Union(a, b);
                uf.Union(a, c);
            }

            // 3) 三角形所属子网格
            var submeshOfTri = new int[tc];
            int cursor = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var sub = mesh.GetTriangles(s);
                int subTris = sub.Length / 3;
                for (int k = 0; k < subTris; k++) submeshOfTri[cursor + k] = s;
                cursor += subTris;
            }
            while (cursor < tc) { submeshOfTri[cursor] = Mathf.Max(0, mesh.subMeshCount - 1); cursor++; }

            // 4) 按根分组
            var builders = new Dictionary<int, RawBuilder>();
            for (int t = 0; t < tc; t++)
            {
                int root = uf.Find(canon[tris[t * 3]]);
                RawBuilder b;
                if (!builders.TryGetValue(root, out b))
                {
                    b = new RawBuilder { root = root };
                    builders[root] = b;
                }
                b.triIndices.Add(t);
                b.verts.Add(tris[t * 3]);
                b.verts.Add(tris[t * 3 + 1]);
                b.verts.Add(tris[t * 3 + 2]);
                b.submeshVotes[submeshOfTri[t]] = b.submeshVotes.TryGetValue(submeshOfTri[t], out var cnt) ? cnt + 1 : 1;
            }

            foreach (var kv in builders.OrderBy(k => k.Key))
            {
                var b = kv.Value;
                int sub = 0, best = -1;
                foreach (var s in b.submeshVotes)
                    if (s.Value > best) { best = s.Value; sub = s.Key; }
                outp.Add(new RawPart
                {
                    triIndices = b.triIndices.ToArray(),
                    verts = b.verts.ToArray(),
                    submesh = sub,
                });
            }
            return outp;
        }

        sealed class RawBuilder
        {
            public int root;
            public readonly List<int> triIndices = new List<int>();
            public readonly HashSet<int> verts = new HashSet<int>();
            public readonly Dictionary<int, int> submeshVotes = new Dictionary<int, int>();
        }

        // ---------------------------------------------------------------- OBB / AABB / UV

        static void ComputeObb(Part p)
        {
            var vs = p.mesh.vertices;
            var l2w = p.tr.localToWorldMatrix;
            var verts = p.raw.verts;

            // 世界坐标点集（按位置去重）
            var pts = new List<Vector3>(verts.Length);
            var seen = new HashSet<Key3>();
            Vector3 mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < verts.Length; i++)
            {
                var wp = l2w.MultiplyPoint3x4(vs[verts[i]]);
                var k = Key3.Of(wp, 100000f);       // 0.01mm 去重
                if (!seen.Add(k)) continue;
                pts.Add(wp);
                mn = Vector3.Min(mn, wp);
                mx = Vector3.Max(mx, wp);
            }
            if (pts.Count == 0) { mn = mx = l2w.MultiplyPoint3x4(Vector3.zero); pts.Add(mn); }
            p.aabbMin = mn; p.aabbMax = mx;

            if (pts.Count < 3)
            {
                p.axes = new[] { Vector3.right, Vector3.up, Vector3.forward };
                p.ext = new[] { mx.x - mn.x, mx.y - mn.y, mx.z - mn.z };
                p.oCenter = (mn + mx) * 0.5f;
                return;
            }

            // 协方差 → Jacobi 特征向量 = OBB 轴
            // 优先用"主面法线"定轴：方板/方柱的 PCA 在面内两个特征值相等时轴会乱转，
            // 而盒体的 6 个主面法线能给出真正的盒轴（对 45° 旋转件也一样）。
            Vector3[] axes;
            if (!TryFaceAxes(p, out axes))
            {
                Vector3 mean = Vector3.zero;
                for (int i = 0; i < pts.Count; i++) mean += pts[i];
                mean /= pts.Count;

                double[,] cov = new double[3, 3];
                for (int i = 0; i < pts.Count; i++)
                {
                    Vector3 d = pts[i] - mean;
                    cov[0, 0] += d.x * d.x; cov[0, 1] += d.x * d.y; cov[0, 2] += d.x * d.z;
                    cov[1, 1] += d.y * d.y; cov[1, 2] += d.y * d.z; cov[2, 2] += d.z * d.z;
                }
                cov[1, 0] = cov[0, 1]; cov[2, 0] = cov[0, 2]; cov[2, 1] = cov[1, 2];

                double[] evals;
                JacobiEigen(cov, out evals, out axes);
            }

            // 按投影范围排序（升序），并做符号规范化
            var proj = new float[3, 2];
            for (int a = 0; a < 3; a++)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = 0; i < pts.Count; i++)
                {
                    float d = Vector3.Dot(pts[i], axes[a]);
                    if (d < lo) lo = d;
                    if (d > hi) hi = d;
                }
                proj[a, 0] = lo; proj[a, 1] = hi;
            }
            var order = new[] { 0, 1, 2 };
            Array.Sort(order, (a, b) => (proj[a, 1] - proj[a, 0]).CompareTo(proj[b, 1] - proj[b, 0]));

            var axes2 = new Vector3[3];
            var ext = new float[3];
            var lo2 = new float[3];
            var hi2 = new float[3];
            for (int i = 0; i < 3; i++)
            {
                int a = order[i];
                var ax = axes[a];
                CanonicalSign(ref ax);
                axes2[i] = ax;
                float lo = proj[a, 0], hi = proj[a, 1];
                if (Vector3.Dot(ax, axes[a]) < 0f) { float tmp = lo; lo = -hi; hi = -tmp; }
                lo2[i] = lo; hi2[i] = hi;
                ext[i] = Mathf.Max(0f, hi - lo);
            }
            // 右手系
            if (Vector3.Dot(Vector3.Cross(axes2[0], axes2[1]), axes2[2]) < 0f) axes2[2] = -axes2[2];

            Vector3 center = Vector3.zero;
            for (int i = 0; i < 3; i++)
                center += axes2[i] * ((lo2[i] + hi2[i]) * 0.5f);

            p.axes = axes2;
            p.ext = ext;
            p.oCenter = center;
        }

        sealed class AxisCluster
        {
            public Vector3 dir;
            public float weight;
        }

        /// <summary>
        /// 用面积加权的主面法线找盒体的三条正交轴。找到 ≥3 个互相垂直的强法线簇才返回 true；
        /// 圆柱/球等回转件会出现大量非正交法线，返回 false 交给 PCA。
        /// </summary>
        static bool TryFaceAxes(Part p, out Vector3[] axes)
        {
            axes = null;
            var mesh = p.mesh;
            var vs = mesh.vertices;
            var tris = mesh.triangles;
            var l2w = p.tr.localToWorldMatrix;
            var clusters = new List<AxisCluster>();

            for (int k = 0; k < p.raw.triIndices.Length; k++)
            {
                int t = p.raw.triIndices[k];
                var a = l2w.MultiplyPoint3x4(vs[tris[t * 3]]);
                var b = l2w.MultiplyPoint3x4(vs[tris[t * 3 + 1]]);
                var c = l2w.MultiplyPoint3x4(vs[tris[t * 3 + 2]]);
                var n = Vector3.Cross(b - a, c - a);
                float area = n.magnitude;
                if (area < 1e-12f) continue;
                n /= area;

                AxisCluster hit = null;
                for (int i = 0; i < clusters.Count; i++)
                {
                    if (Mathf.Abs(Vector3.Dot(clusters[i].dir, n)) < 0.999f) continue;
                    hit = clusters[i];
                    break;
                }
                if (hit == null)
                {
                    clusters.Add(new AxisCluster { dir = n, weight = area });
                }
                else
                {
                    // 反向面并入同一簇（翻符号后再加权平均）
                    if (Vector3.Dot(hit.dir, n) < 0f) n = -n;
                    hit.dir = (hit.dir * hit.weight + n * area).normalized;
                    hit.weight += area;
                }
            }

            if (clusters.Count < 3) return false;
            clusters.Sort((x, y) => y.weight.CompareTo(x.weight));

            var a0 = clusters[0].dir.normalized;
            Vector3 a1 = Vector3.zero;
            float best = -1f;
            for (int i = 1; i < clusters.Count; i++)
            {
                if (Mathf.Abs(Vector3.Dot(clusters[i].dir, a0)) > 0.01f) continue;
                if (clusters[i].weight <= best) continue;
                best = clusters[i].weight;
                a1 = clusters[i].dir.normalized;
            }
            if (a1 == Vector3.zero) return false;

            var a2 = Vector3.Cross(a0, a1).normalized;
            bool hasA2 = false;
            for (int i = 0; i < clusters.Count; i++)
            {
                if (Mathf.Abs(Vector3.Dot(clusters[i].dir, a2)) > 0.99f) { hasA2 = true; break; }
            }
            if (!hasA2) return false;

            axes = new[] { a0, a1, a2 };
            return true;
        }

        static void CanonicalSign(ref Vector3 ax)
        {
            if (ax.x < -1e-5f || (Mathf.Abs(ax.x) <= 1e-5f && ax.y < -1e-5f) ||
                (Mathf.Abs(ax.x) <= 1e-5f && Mathf.Abs(ax.y) <= 1e-5f && ax.z < 0f))
                ax = -ax;
        }

        static void ComputeUvBounds(Part p)
        {
            var uv = p.mesh.uv;
            if (uv == null || uv.Length != p.mesh.vertexCount || p.raw.verts.Length == 0)
            {
                p.uvMin = p.uvMax = Vector2.zero;
                return;
            }
            Vector2 mn = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 mx = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < p.raw.verts.Length; i++)
            {
                var v = uv[p.raw.verts[i]];
                mn = Vector2.Min(mn, v);
                mx = Vector2.Max(mx, v);
            }
            p.uvMin = mn; p.uvMax = mx;
        }

        /// <summary>3×3 对称矩阵 Jacobi 特征分解（列向量 = 特征向量）。</summary>
        static void JacobiEigen(double[,] a, out double[] eval, out Vector3[] evec)
        {
            var m = (double[,])a.Clone();
            var v = new double[3, 3];
            v[0, 0] = v[1, 1] = v[2, 2] = 1.0;

            for (int sweep = 0; sweep < 32; sweep++)
            {
                double off = Math.Abs(m[0, 1]) + Math.Abs(m[0, 2]) + Math.Abs(m[1, 2]);
                if (off < 1e-14) break;

                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(m[p, q]) < 1e-16) continue;
                        double theta = (m[q, q] - m[p, p]) / (2.0 * m[p, q]);
                        double t = theta >= 0
                            ? 1.0 / (theta + Math.Sqrt(theta * theta + 1.0))
                            : -1.0 / (-theta + Math.Sqrt(theta * theta + 1.0));
                        double c = 1.0 / Math.Sqrt(t * t + 1.0);
                        double s = t * c;

                        for (int k = 0; k < 3; k++)
                        {
                            double mkp = m[k, p], mkq = m[k, q];
                            m[k, p] = c * mkp - s * mkq;
                            m[k, q] = s * mkp + c * mkq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double mpk = m[p, k], mqk = m[q, k];
                            m[p, k] = c * mpk - s * mqk;
                            m[q, k] = s * mpk + c * mqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq;
                            v[k, q] = s * vkp + c * vkq;
                        }
                    }
                }
            }

            eval = new[] { m[0, 0], m[1, 1], m[2, 2] };
            evec = new Vector3[3];
            for (int i = 0; i < 3; i++)
            {
                var ax = new Vector3((float)v[0, i], (float)v[1, i], (float)v[2, i]);
                if (ax.sqrMagnitude < 1e-12f) ax = i == 0 ? Vector3.right : (i == 1 ? Vector3.up : Vector3.forward);
                evec[i] = ax.normalized;
            }
            // 去除特征向量之间的浮点不正交
            evec[1] = (evec[1] - Vector3.Dot(evec[1], evec[0]) * evec[0]).normalized;
            evec[2] = Vector3.Cross(evec[0], evec[1]).normalized;
        }

        // ---------------------------------------------------------------- 分类

        /// <summary>名字/容器名按 _ 切 token（同时拆 CamelCase），返回命中的薄条类别；没有则返回首个非前缀 token。</summary>
        static string CategoryOf(Transform t)
        {
            var names = new List<string>();
            for (var cur = t; cur != null && names.Count < 5; cur = cur.parent) names.Add(cur.name ?? string.Empty);

            string fallback = "?";
            for (int n = 0; n < names.Count; n++)
            {
                var tokens = Tokenize(names[n]);
                for (int i = 0; i < tokens.Count; i++)
                {
                    var up = tokens[i].ToUpperInvariant();
                    for (int k = 0; k < ThinCatTokens.Length; k++)
                        if (up == ThinCatTokens[k]) return tokens[i];
                }
                if (n == 0)
                {
                    // 取类别位：DEC_S_01_<Cat>_... / DEC_S01_<Cat>_... / MAPSKIN_<Cat>_...
                    for (int i = 0; i < tokens.Count; i++)
                    {
                        var up = tokens[i].ToUpperInvariant();
                        if (up == "DEC" || up == "MAPSKIN" || up.StartsWith("S0") || up.StartsWith("S_")) continue;
                        if (IsNumeric(up) || up.Length <= 1) continue;
                        fallback = tokens[i];
                        break;
                    }
                }
            }
            return fallback;
        }

        static List<string> Tokenize(string name)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(name)) return list;
            var parts = name.Split(new[] { '_', '-', '.', ' ', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                int start = 0;
                for (int k = 1; k <= p.Length; k++)
                {
                    bool cut = k == p.Length ||
                               (k < p.Length && char.IsUpper(p[k]) && char.IsLower(p[k - 1]));
                    if (!cut) continue;
                    var piece = p.Substring(start, k - start);
                    if (piece.Length > 0) list.Add(piece);
                    start = k;
                }
            }
            return list;
        }

        static bool IsNumeric(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++) if (!char.IsDigit(s[i])) return false;
            return true;
        }

        static bool IsThinCategory(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return false;
            var up = cat.ToUpperInvariant();
            for (int i = 0; i < ThinCatTokens.Length; i++)
                if (up == ThinCatTokens[i]) return true;
            return false;
        }

        static bool IsNeverMergeCategory(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return false;
            var up = cat.ToUpperInvariant();
            for (int i = 0; i < NeverMergeTokens.Length; i++)
                if (up == NeverMergeTokens[i]) return true;
            return false;
        }

        static void ClassifyThin(Part p)
        {
            float thick = p.ext[0], wide = p.ext[1], len = p.ext[2];
            float thickLim = P_ThickMm * 0.001f;
            float wideLim = P_WidthMm * 0.001f;
            float exempt = P_ExemptMm * 0.001f;

            bool elongated = len >= Mathf.Max(MinStripLen, wide * 2f);
            p.strictThin = p.categoryThin && elongated && thick <= thickLim + 1e-5f && wide <= wideLim + 1e-5f;
            p.exemptThin = !p.strictThin && p.categoryThin && elongated &&
                           IsSeamOrJoint(p.category) && thick <= exempt && wide <= wideLim + 1e-5f;

            // 近失项：只用于报告，提示改阈值（12→20 / 60→80）
            p.nearMiss = !p.strictThin && !p.exemptThin && p.categoryThin && elongated &&
                         thick <= exempt + 1e-5f && wide <= wideLim * 1.5f + 1e-5f;
        }

        static bool IsSeamOrJoint(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return false;
            var up = cat.ToUpperInvariant();
            return up == "SEAM" || up == "SEAMLINE" || up == "JOINT" || up == "GAP" ||
                   up == "GROOVE" || up == "REVEAL" || up == "SCRIBE";
        }

        /// <summary>拼接板判定：薄片（厚度 ≤80mm）+ 两个面内尺寸 ≥50mm；排除灯/管/肋等。</summary>
        static bool IsPlate(Part p)
        {
            if (p.categoryThin || IsNeverMergeCategory(p.category)) return false;
            if (p.ext[0] > 0.08f) return false;
            if (p.ext[1] < 0.05f || p.ext[2] < 0.05f) return false;
            // 面积太小/太碎不做合并（描边碎线本来就是这些小件）
            if (p.ext[1] * p.ext[2] < 0.01f) return false;
            return true;
        }

        // ---------------------------------------------------------------- 宿主

        static void AssignHosts(ScanResult res)
        {
            // 先按 block + 2m 网格给宿主建索引，避免 1414 条缝 × 全场渲染件的全量比较
            var grid = new Dictionary<long, List<RendererRec>>();
            foreach (var h in res.Hosts)
            {
                var c = h.bounds.center;
                long key = GridKey(c.x, c.y, c.z, 2f);
                List<RendererRec> list;
                if (!grid.TryGetValue(key, out list)) { list = new List<RendererRec>(); grid[key] = list; }
                list.Add(h);
            }

            foreach (var p in res.Thin)
            {
                p.host = FindHost(p, grid, res.Hosts);
            }
            foreach (var p in res.NearMiss)
            {
                p.host = FindHost(p, grid, res.Hosts);
            }
        }

        static RendererRec FindHost(Part p, Dictionary<long, List<RendererRec>> grid, List<RendererRec> all)
        {
            var c = p.oCenter;
            RendererRec best = null;
            float bestScore = float.MaxValue;

            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        long key = GridKey(c.x + dx * 2f, c.y + dy * 2f, c.z + dz * 2f, 2f);
                        List<RendererRec> list;
                        if (!grid.TryGetValue(key, out list)) continue;
                        for (int i = 0; i < list.Count; i++)
                        {
                            var h = list[i];
                            if (h.r == null || h.r.transform == p.tr) continue;
                            var b = h.bounds;
                            var bb = b;
                            bb.Expand(0.05f);
                            if (!bb.Contains(c)) continue;
                            // 宿主必须够大：至少两个方向大于薄条宽度
                            if (b.size.x < p.WidthM * 1.5f && b.size.z < p.WidthM * 1.5f) continue;
                            float score = (h.modifiable ? 1000f : 0f) + b.size.magnitude;
                            if (h.block == p.block) score -= 0.5f;
                            if (score < bestScore) { bestScore = score; best = h; }
                        }
                    }

            if (best != null) return best;
            // 兜底：全场找包含中心的最近件（大面优先）
            for (int i = 0; i < all.Count; i++)
            {
                var h = all[i];
                if (h.r == null || h.r.transform == p.tr) continue;
                var bb = h.bounds; bb.Expand(0.05f);
                if (!bb.Contains(c)) continue;
                float score = (h.modifiable ? 1000f : 0f) + h.bounds.size.magnitude;
                if (score < bestScore) { bestScore = score; best = h; }
            }
            return best;
        }

        // ================================================================ Scan 报告

        static void ReportScan(ScanResult s, StringBuilder rep)
        {
            int strict = 0, exempt = 0, strictTris = 0, exemptTris = 0;
            for (int i = 0; i < s.Thin.Count; i++)
            {
                var p = s.Thin[i];
                if (p.strictThin) { strict++; strictTris += p.TriCount; }
                else { exempt++; exemptTris += p.TriCount; }
            }

            rep.AppendLine("== A. 薄条扫描（Seam/Trim/Joint 类，几何门限） ==");
            rep.AppendLine($"  严格门限（厚 ≤{P_ThickMm:F0}mm、宽 ≤{P_WidthMm:F0}mm）：{strict} 条 / {strictTris:N0} 三角");
            rep.AppendLine($"  类别豁免（Seam/Joint 厚 ≤{P_ExemptMm:F0}mm、宽 ≤{P_WidthMm:F0}mm）：{exempt} 条 / {exemptTris:N0} 三角");
            rep.AppendLine($"  合计待压平：{s.Thin.Count} 条 / {s.ThinTris:N0} 三角；近失项（建议调阈值）：{s.NearMiss.Count} 条");

            // 按类别
            var cats = new SortedDictionary<string, int>();
            var catTris = new SortedDictionary<string, int>();
            for (int i = 0; i < s.Thin.Count; i++)
            {
                var p = s.Thin[i];
                string k = string.IsNullOrEmpty(p.category) ? "?" : p.category;
                cats[k] = cats.TryGetValue(k, out var c) ? c + 1 : 1;
                catTris[k] = catTris.TryGetValue(k, out var t) ? t + p.TriCount : p.TriCount;
            }
            foreach (var kv in cats)
                rep.AppendLine($"      {kv.Key,-16} {kv.Value,6} 条 / {catTris[kv.Key],8:N0} 三角");

            // 按区块
            var blocks = new SortedDictionary<string, int>();
            for (int i = 0; i < s.Thin.Count; i++)
            {
                var b = s.Thin[i].block ?? "?";
                blocks[b] = blocks.TryGetValue(b, out var c) ? c + 1 : 1;
            }
            var bk = new List<string>(blocks.Keys);
            var bl = new List<string>();
            foreach (var k in bk) bl.Add($"{k}:{blocks[k]}");
            rep.AppendLine($"  按区块：{string.Join("  ", bl)}");

            // 按对象（前 12）
            var objs = new SortedDictionary<string, int>();
            for (int i = 0; i < s.Thin.Count; i++)
            {
                var k = s.Thin[i].ownerPath;
                objs[k] = objs.TryGetValue(k, out var c) ? c + 1 : 1;
            }
            rep.AppendLine("  薄条最多的对象（前 12）：");
            foreach (var kv in objs.OrderByDescending(x => x.Value).Take(12))
                rep.AppendLine($"      {kv.Value,6} 条   {kv.Key}");
        }

        static void ReportNearMiss(ScanResult s, StringBuilder rep)
        {
            if (s.NearMiss.Count == 0) return;
            rep.AppendLine("== A2. 近失项（未达到门限，不参与压平；如需处理请调 -ps-thickmm / -ps-widthmm） ==");
            foreach (var kv in s.NearMiss.GroupBy(p => p.category).OrderByDescending(g => g.Count()).Take(8))
            {
                float mnT = kv.Min(p => p.ThicknessM) * 1000f;
                float mxT = kv.Max(p => p.ThicknessM) * 1000f;
                float mnW = kv.Min(p => p.WidthM) * 1000f;
                float mxW = kv.Max(p => p.WidthM) * 1000f;
                rep.AppendLine($"      {kv.Key,-14} {kv.Count(),6} 条  厚 {mnT:F1}–{mxT:F1}mm  宽 {mnW:F1}–{mxW:F1}mm");
            }
        }

        // ================================================================ Flatten

        static void PlanFlatten(ScanResult scan, StringBuilder rep)
        {
            var byObj = scan.Thin.GroupBy(p => p.mf).ToDictionary(g => g.Key, g => g.ToList());
            int emptyObjs = 0, changedObjs = 0;
            foreach (var kv in byObj)
            {
                int removed = kv.Value.Sum(p => p.TriCount);
                int total = kv.Key.sharedMesh.triangles.Length / 3;
                if (removed >= total) emptyObjs++;
                else changedObjs++;
            }
            rep.AppendLine("== B. Flatten 计划（只删薄条，宿主大面零几何补偿） ==");
            rep.AppendLine($"  受影响对象 {byObj.Count} 个（需改 mesh {changedObjs} 个 / 整对象清空后停用 {emptyObjs} 个）");
            rep.AppendLine($"  删除薄条 {scan.Thin.Count} 条 / {scan.ThinTris:N0} 三角；manifest 条目 {scan.Thin.Count} 条 → {ManifestPath}");
            int i = 0;
            foreach (var kv in byObj.OrderByDescending(x => x.Value.Count).Take(8))
            {
                string host = kv.Value.FirstOrDefault(p => p.host != null)?.host.name ?? "(未匹配)";
                rep.AppendLine($"      {kv.Value.Count,6} 条  {kv.Key.gameObject.name}   宿主≈{host}");
                if (++i >= 8) break;
            }
        }

        /// <summary>删除薄条；返回被改动的对象数。宿主大面不做任何几何补偿。</summary>
        static int FlattenCore(ScanResult scan, State state, StringBuilder rep)
        {
            if (scan.Thin.Count == 0)
            {
                rep.AppendLine("[Flatten] 未发现薄条，跳过（可能已压平过）。");
                return 0;
            }

            var byObj = scan.Thin.GroupBy(p => p.mf).ToDictionary(g => g.Key, g => g.ToList());
            int changed = 0, emptied = 0, trisRemoved = 0;

            foreach (var kv in byObj)
            {
                var mf = kv.Key;
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                int triCount = mesh.triangles.Length / 3;
                var drop = new bool[triCount];
                foreach (var p in kv.Value)
                    foreach (var t in p.raw.triIndices)
                        if (t >= 0 && t < drop.Length) drop[t] = true;

                int remain = 0;
                for (int i = 0; i < drop.Length; i++) if (!drop[i]) remain++;
                int removed = triCount - remain;
                trisRemoved += removed;

                RecordObject(state, mf, changedMesh: true, changedActive: true, changedMaterials: false);

                if (remain == 0)
                {
                    // 整个对象都是"本该由法线表达"的薄几何 → 退役（有碰撞/子物体时只关渲染器）
                    RetireObject(mf.gameObject);
                    emptied++;
                    changed++;
                    continue;
                }

                var newMesh = RemoveTriangles(mesh, drop, "PS_Flat_" + mf.gameObject.name);
                string path = MeshAssetPath("PS_Flat", PathOf(mf.transform));
                if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
                AssetDatabase.CreateAsset(newMesh, path);
                mf.sharedMesh = newMesh;
                MarkChanged(mf);
                changed++;
            }

            AssetDatabase.SaveAssets();
            rep.AppendLine($"[Flatten] 删除薄条 {scan.Thin.Count} 条 / {trisRemoved:N0} 三角；" +
                           $"改动对象 {changed} 个（其中整对象停用 {emptied} 个）；宿主大面未做几何补偿。");
            return changed;
        }

        /// <summary>从合并网格里删掉指定三角形，保留其余子网格与顶点属性。</summary>
        static Mesh RemoveTriangles(Mesh src, bool[] drop, string name)
        {
            var v = src.vertices;
            var tris = src.triangles;
            int tc = tris.Length / 3;

            // 标记保留三角形用到的顶点
            var used = new bool[v.Length];
            for (int t = 0; t < tc; t++)
            {
                if (drop[t]) continue;
                used[tris[t * 3]] = true;
                used[tris[t * 3 + 1]] = true;
                used[tris[t * 3 + 2]] = true;
            }

            var remap = new int[v.Length];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;
            int nv = 0;
            for (int i = 0; i < v.Length; i++) if (used[i]) remap[i] = nv++;

            var nvpos = new Vector3[nv];
            for (int i = 0; i < v.Length; i++) if (remap[i] >= 0) nvpos[remap[i]] = v[i];

            var m = new Mesh
            {
                name = name,
                indexFormat = nv > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : src.indexFormat,
            };
            m.SetVertices(nvpos);

            CopyAttr(src.normals, v.Length, remap, nv, a => m.SetNormals(a));
            CopyAttr(src.tangents, v.Length, remap, nv, a => m.SetTangents(a));
            CopyAttr(src.uv, v.Length, remap, nv, a => m.SetUVs(0, a));
            CopyAttr(src.uv2, v.Length, remap, nv, a => m.SetUVs(1, a));
            CopyAttr(src.colors32, v.Length, remap, nv, a => m.SetColors(a));

            // 子网格：保持原 subMesh 布局
            int cursor = 0;
            var subs = new List<int>[src.subMeshCount];
            for (int s = 0; s < subs.Length; s++) subs[s] = new List<int>();
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var sub = src.GetTriangles(s);
                int st = sub.Length / 3;
                for (int k = 0; k < st; k++)
                {
                    int t = cursor + k;
                    if (t >= tc || drop[t]) continue;
                    subs[s].Add(remap[sub[k * 3]]);
                    subs[s].Add(remap[sub[k * 3 + 1]]);
                    subs[s].Add(remap[sub[k * 3 + 2]]);
                }
                cursor += st;
            }
            m.subMeshCount = subs.Length;
            for (int s = 0; s < subs.Length; s++) m.SetTriangles(subs[s], s);

            m.RecalculateBounds();
            if (src.normals == null || src.normals.Length != v.Length) m.RecalculateNormals();
            if (src.tangents == null || src.tangents.Length != v.Length) m.RecalculateTangents();
            return m;
        }

        static void CopyAttr<T>(T[] src, int srcLen, int[] remap, int nv, Action<T[]> set)
        {
            if (src == null || src.Length != srcLen) return;
            var dst = new T[nv];
            for (int i = 0; i < srcLen; i++)
                if (remap[i] >= 0) dst[remap[i]] = src[i];
            set(dst);
        }

        // ================================================================ MergeCoplanar

        static void PlanMerge(ScanResult scan, StringBuilder rep)
        {
            var groups = BuildMergeGroups(scan, rep);
            int srcObjs = 0, dstObjs = 0;
            var touched = new HashSet<MeshFilter>();
            foreach (var g in groups)
                foreach (var p in g.Parts) touched.Add(p.mf);
            srcObjs = touched.Count;
            dstObjs = groups.Select(g => g.Keeper.mf).Distinct().Count();

            rep.AppendLine("== C. MergeCoplanar 计划（同平面 / 同材质 / 间隙 ≤2mm） ==");
            rep.AppendLine($"  可合并拼接板 {groups.Sum(g => g.Parts.Count)} 块 → {groups.Count} 组；" +
                           $"涉及对象 {srcObjs} 个 → {dstObjs} 个（每组保留 1 个 keeper，其余停用）");
            foreach (var g in groups.OrderByDescending(x => x.Parts.Count).Take(8))
            {
                rep.AppendLine($"      {g.Parts.Count,6} 块 / {g.Parts.Sum(p => p.TriCount),7:N0} 三角  " +
                               $"mat={MatName(g.Material)}  keeper={g.Keeper.mf.gameObject.name}");
            }
            if (groups.Count == 0)
                rep.AppendLine("      （无满足条件的相邻拼接板；当前场景可能只有 Seam/Arc/Support，或拼接板已合并）");
        }

        sealed class MergeGroup
        {
            public int id;
            public Part Keeper;
            public Material Material;
            public List<Part> Parts = new List<Part>();
            public Vector3 Normal;
            public Vector3 BasisU;
            public Vector3 BasisV;
        }

        static List<MergeGroup> BuildMergeGroups(ScanResult scan, StringBuilder rep)
        {
            var plates = new List<Part>();
            int mpbSkipped = 0;
            for (int i = 0; i < scan.All.Count; i++)
            {
                var p = scan.All[i];
                if (p.strictThin || p.exemptThin) continue;
                if (!IsPlate(p)) continue;
                // 预制体实例（如 MAPSKIN_Arc 的 FBX 皮）不做跨对象合并，避免把实例改动摊到 prefab 语义上
                if (PrefabUtility.IsPartOfPrefabInstance(p.mf)) continue;
                // MPB 是逐 Renderer 的（S06 的刻线色调），跨对象合并会把参数摊成 keeper 的一份 → 跳过
                if (p.hasMpb) { mpbSkipped++; continue; }
                plates.Add(p);
            }
            if (rep != null && mpbSkipped > 0)
                rep.AppendLine($"[Merge] 跳过带 MaterialPropertyBlock 的板件 {mpbSkipped} 个（跨对象合并会丢失逐对象色调参数）");
            var result = new List<MergeGroup>();
            if (plates.Count < 2) return result;

            // 按 材质 × 区块 × 平面桶 分组
            var buckets = new Dictionary<string, List<Part>>();
            for (int i = 0; i < plates.Count; i++)
            {
                var p = plates[i];
                string key = MatKey(p.material) + "|" + (p.block ?? "?") + "|" + PlaneBucket(p);
                List<Part> list;
                if (!buckets.TryGetValue(key, out list)) { list = new List<Part>(); buckets[key] = list; }
                list.Add(p);
            }

            float gap = P_GapMm * 0.001f;
            foreach (var kv in buckets)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;

                var basisPart = list[0];
                var n = basisPart.ThinNormal.normalized;
                var u = basisPart.axes[2].normalized;
                var v = basisPart.axes[1].normalized;
                if (Vector3.Dot(Vector3.Cross(u, v), n) < 0f) v = -v;

                // 面内 2D 包围盒 + 网格哈希
                var rects = new Rect2[list.Count];
                var ok = new bool[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    ok[i] = InPlaneAxesAligned(list[i], u, v, n);
                    rects[i] = ProjectRect(list[i], u, v);
                }

                var uf = new UnionFind(list.Count);
                var cellMap = new Dictionary<long, List<int>>();
                const float cell = 3.0f;
                for (int i = 0; i < list.Count; i++)
                {
                    if (!ok[i]) continue;
                    long ck = CellKey(rects[i].min.x, rects[i].min.y, cell);
                    List<int> bucket;
                    if (!cellMap.TryGetValue(ck, out bucket)) { bucket = new List<int>(); cellMap[ck] = bucket; }
                    bucket.Add(i);
                }

                for (int i = 0; i < list.Count; i++)
                {
                    if (!ok[i]) continue;
                    int cx = Mathf.FloorToInt(rects[i].min.x / cell);
                    int cy = Mathf.FloorToInt(rects[i].min.y / cell);
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            List<int> bucket;
                            if (!cellMap.TryGetValue(CellKeyInt(cx + dx, cy + dy), out bucket)) continue;
                            for (int bi = 0; bi < bucket.Count; bi++)
                            {
                                int j = bucket[bi];
                                if (j <= i || !ok[j]) continue;
                                if (!Coplanar(list[i], list[j], gap)) continue;
                                if (!Adjacent(rects[i], rects[j], gap)) continue;
                                uf.Union(i, j);
                            }
                        }
                }

                var groups = new Dictionary<int, List<int>>();
                for (int i = 0; i < list.Count; i++)
                {
                    if (!ok[i]) continue;
                    int root = uf.Find(i);
                    List<int> g;
                    if (!groups.TryGetValue(root, out g)) { g = new List<int>(); groups[root] = g; }
                    g.Add(i);
                }

                foreach (var g in groups.Values)
                {
                    if (g.Count < 2) continue;
                    // 必须跨对象，否则本来就是同一个 mesh（合并没有对象收益）
                    int distinct = g.Select(i => list[i].mf).Distinct().Count();
                    if (distinct < 2) continue;

                    var parts = g.Select(i => list[i]).OrderBy(p => p.ownerPath).ThenBy(p => p.indexInMesh).ToList();
                    var keeper = parts[0];
                    result.Add(new MergeGroup
                    {
                        id = result.Count,
                        Keeper = keeper,
                        Material = keeper.material,
                        Normal = n,
                        BasisU = u,
                        BasisV = v,
                        Parts = parts,
                    });
                }
            }
            return result;
        }

        struct Rect2
        {
            public Vector2 min, max;
        }

        static bool InPlaneAxesAligned(Part p, Vector3 u, Vector3 v, Vector3 n)
        {
            // 板的两条面内轴必须与平面基轴平行（容差 ≈1°），否则就是"随机斜切"，不合并
            var a1 = Mathf.Abs(Vector3.Dot(p.axes[1], u));
            var a2 = Mathf.Abs(Vector3.Dot(p.axes[1], v));
            var a3 = Mathf.Abs(Vector3.Dot(p.axes[2], u));
            var a4 = Mathf.Abs(Vector3.Dot(p.axes[2], v));
            bool ok1 = (a1 > 0.9998f && a4 > 0.9998f) || (a2 > 0.9998f && a3 > 0.9998f);
            if (!ok1) return false;
            return Vector3.Dot(p.ThinNormal, n) > 0.9998f;
        }

        static Rect2 ProjectRect(Part p, Vector3 u, Vector3 v)
        {
            Vector2 mn = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 mx = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = p.oCenter
                    + p.axes[0] * ((i & 1) == 0 ? -p.ext[0] : p.ext[0]) * 0.5f
                    + p.axes[1] * ((i & 2) == 0 ? -p.ext[1] : p.ext[1]) * 0.5f
                    + p.axes[2] * ((i & 4) == 0 ? -p.ext[2] : p.ext[2]) * 0.5f;
                Vector2 q = new Vector2(Vector3.Dot(corner, u), Vector3.Dot(corner, v));
                mn = Vector2.Min(mn, q);
                mx = Vector2.Max(mx, q);
            }
            return new Rect2 { min = mn, max = mx };
        }

        static bool Coplanar(Part a, Part b, float tol)
        {
            // 同向才算同一张面（背靠背的两块板不是拼接关系）
            if (Vector3.Dot(a.ThinNormal, b.ThinNormal) < 0.9998f) return false;
            return Mathf.Abs(Vector3.Dot(b.oCenter - a.oCenter, a.ThinNormal)) <= tol;
        }

        static bool Adjacent(Rect2 a, Rect2 b, float gap)
        {
            float sepX = Mathf.Max(a.min.x - b.max.x, b.min.x - a.max.x);
            float sepY = Mathf.Max(a.min.y - b.max.y, b.min.y - a.max.y);
            float overlapX = Mathf.Min(a.max.x, b.max.x) - Mathf.Max(a.min.x, b.min.x);
            float overlapY = Mathf.Min(a.max.y, b.max.y) - Mathf.Max(a.min.y, b.min.y);
            if (sepX <= gap && overlapY > 0.01f) return true;
            if (sepY <= gap && overlapX > 0.01f) return true;
            // 已重叠（≤2mm 的压边）也算相邻
            if (overlapX > 0.01f && overlapY > 0.01f && sepX <= gap && sepY <= gap) return true;
            return false;
        }

        static string PlaneBucket(Part p)
        {
            var n = p.ThinNormal;
            int nx = Mathf.RoundToInt(n.x * 50f);
            int ny = Mathf.RoundToInt(n.y * 50f);
            int nz = Mathf.RoundToInt(n.z * 50f);
            int d = Mathf.RoundToInt(Vector3.Dot(n, p.oCenter) * 500f);   // 2mm 桶
            return nx + "_" + ny + "_" + nz + "_" + d;
        }

        /// <summary>合并执行；返回被改动的对象数。每组保留 keeper，其余对象停用。</summary>
        static int MergeCore(ScanResult scan, State state, StringBuilder rep)
        {
            var groups = BuildMergeGroups(scan, rep);
            if (groups.Count == 0)
            {
                rep.AppendLine("[Merge] 无可合并的相邻拼接板，跳过。");
                return 0;
            }

            // part → group（一个 part 只属于一个组）
            var partGroup = new Dictionary<Part, MergeGroup>();
            foreach (var g in groups)
                foreach (var p in g.Parts)
                    partGroup[p] = g;

            // 每个对象要保留哪些 part
            var byObj = scan.All.GroupBy(p => p.mf).ToDictionary(x => x.Key, x => x.ToList());
            var touched = new HashSet<MeshFilter>();
            foreach (var g in groups)
            {
                touched.Add(g.Keeper.mf);
                foreach (var p in g.Parts) touched.Add(p.mf);
            }

            int changed = 0, deactivated = 0;
            long trisBefore = 0, trisAfter = 0;

            foreach (var mf in touched)
            {
                List<Part> own;
                if (!byObj.TryGetValue(mf, out own)) continue;
                if (mf.sharedMesh == null) continue;

                // 该对象保留的部件：
                //   · 不属于任何 merge group 的自有部件；
                //   · 自己当 keeper 的组：整组部件（跨对象搬入）；
                var keep = new List<Part>();
                var island = new HashSet<Part>();
                for (int i = 0; i < own.Count; i++)
                {
                    var p = own[i];
                    MergeGroup g;
                    if (!partGroup.TryGetValue(p, out g)) keep.Add(p);
                    else if (ReferenceEquals(g.Keeper.mf, mf)) { /* 组内件在下面统一加 */ }
                }

                bool isKeeper = false;
                foreach (var g in groups)
                {
                    if (!ReferenceEquals(g.Keeper.mf, mf)) continue;
                    isKeeper = true;
                    for (int i = 0; i < g.Parts.Count; i++)
                    {
                        keep.Add(g.Parts[i]);
                        island.Add(g.Parts[i]);
                    }
                }

                trisBefore += own.Sum(p => p.TriCount);
                trisAfter += keep.Sum(p => p.TriCount);

                if (!isKeeper)
                {
                    // 只删掉搬走的组内件；如果全空就停用
                    if (keep.Count == 0)
                    {
                        RecordObject(state, mf, changedMesh: false, changedActive: true, changedMaterials: false);
                        RetireObject(mf.gameObject);
                        deactivated++;
                        changed++;
                        continue;
                    }

                    int triCount = mf.sharedMesh.triangles.Length / 3;
                    var drop = new bool[triCount];
                    for (int i = 0; i < own.Count; i++)
                    {
                        MergeGroup g;
                        if (!partGroup.TryGetValue(own[i], out g)) continue;
                        foreach (var t in own[i].raw.triIndices)
                            if (t >= 0 && t < triCount) drop[t] = true;
                    }
                    var nm = RemoveTriangles(mf.sharedMesh, drop, "PS_Merge_" + mf.gameObject.name);
                    string path = MeshAssetPath("PS_Merge", mf.gameObject.name + "_" + StableHash(PathOf(mf.transform)));
                    if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
                    AssetDatabase.CreateAsset(nm, path);
                    RecordObject(state, mf, changedMesh: true, changedActive: true, changedMaterials: false);
                    mf.sharedMesh = nm;
                    MarkChanged(mf);
                    changed++;
                    continue;
                }

                // keeper：把整组（可能来自多个对象）+ 自有非组件 合成世界空间一个 mesh
                RecordObject(state, mf, changedMesh: true, changedActive: true, changedMaterials: true);
                var merged = BuildWorldMesh(keep, island, "PS_Merge_" + mf.gameObject.name, out var mats);
                if (merged == null) continue;

                string mpath = MeshAssetPath("PS_Merge", mf.gameObject.name + "_" + StableHash(PathOf(mf.transform) + "|keeper"));
                if (AssetDatabase.LoadAssetAtPath<Mesh>(mpath) != null) AssetDatabase.DeleteAsset(mpath);
                AssetDatabase.CreateAsset(merged, mpath);
                mf.sharedMesh = merged;
                var r = mf.GetComponent<Renderer>();
                if (r != null) r.sharedMaterials = mats.ToArray();
                MarkChanged(mf);
                changed++;
            }

            AssetDatabase.SaveAssets();
            rep.AppendLine($"[Merge] 合并 {groups.Count} 组；改动对象 {changed} 个（其中停用 {deactivated} 个）；" +
                           $"参与件三角 {trisBefore:N0} → 保留件三角 {trisAfter:N0}（描边碎线随壳数下降）。");
            return changed;
        }

        /// <summary>
        /// 把多个 part（可跨对象）烘成一个世界空间 mesh。
        /// UV 岛模式：每块板自己的 UV 包围盒重新映射到 0–1（等比例，不整面拉伸）。
        /// </summary>
        static Mesh BuildWorldMesh(List<Part> parts, HashSet<Part> islandParts, string name, out List<Material> mats)
        {
            mats = new List<Material>();
            if (parts == null || parts.Count == 0) return null;

            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tangents = new List<Vector4>();
            bool allNormals = true, allUv = true, allTangents = true;
            var subLists = new List<List<int>>();
            var subIndex = new Dictionary<int, int>();

            foreach (var p in parts)
            {
                var src = p.mesh;
                var sv = src.vertices;
                var sn = src.normals;
                var su = src.uv;
                var st = src.tangents;
                bool partHasNormals = sn != null && sn.Length == sv.Length;
                bool partHasUv = su != null && su.Length == sv.Length;
                bool partHasTangents = st != null && st.Length == sv.Length;
                if (!partHasNormals) allNormals = false;
                if (!partHasUv) allUv = false;
                if (!partHasTangents) allTangents = false;

                var l2w = p.tr.localToWorldMatrix;
                var nMat = l2w.inverse.transpose;
                bool flip = l2w.determinant < 0f;

                // 本件 UV 岛（仅在合并组内件上启用）
                bool island = P_UvIsland == 1 && islandParts.Contains(p);
                Vector2 uvMin = Vector2.zero;
                float uvScale = 1f;
                if (island && partHasUv)
                {
                    uvMin = new Vector2(float.MaxValue, float.MaxValue);
                    Vector2 uvMax = new Vector2(float.MinValue, float.MinValue);
                    for (int i = 0; i < p.raw.verts.Length; i++)
                    {
                        var uv = su[p.raw.verts[i]];
                        uvMin = Vector2.Min(uvMin, uv);
                        uvMax = Vector2.Max(uvMax, uv);
                    }
                    float du = uvMax.x - uvMin.x, dv = uvMax.y - uvMin.y;
                    uvScale = (1f - 2f * UvPad) / Mathf.Max(1e-6f, Mathf.Max(du, dv));
                }

                int sub;
                if (!subIndex.TryGetValue(MatId(p.material), out sub))
                {
                    sub = subLists.Count;
                    subIndex[MatId(p.material)] = sub;
                    subLists.Add(new List<int>());
                    mats.Add(p.material);
                }

                var map = new Dictionary<int, int>(p.raw.verts.Length);
                for (int i = 0; i < p.raw.verts.Length; i++)
                {
                    int oi = p.raw.verts[i];
                    int ni = verts.Count;
                    map[oi] = ni;
                    verts.Add(l2w.MultiplyPoint3x4(sv[oi]));
                    norms.Add(partHasNormals ? nMat.MultiplyVector(sn[oi]).normalized : Vector3.up);
                    if (partHasUv)
                    {
                        var uv = su[oi];
                        if (island) uv = new Vector2((uv.x - uvMin.x) * uvScale + UvPad,
                                                     (uv.y - uvMin.y) * uvScale + UvPad);
                        uvs.Add(uv);
                    }
                    else uvs.Add(Vector2.zero);
                    if (partHasTangents)
                    {
                        var t = st[oi];
                        var tw = l2w.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
                        tangents.Add(new Vector4(tw.x, tw.y, tw.z, t.w));
                    }
                    else tangents.Add(new Vector4(1f, 0f, 0f, 1f));
                }

                var tris = src.triangles;
                var tgt = subLists[sub];
                for (int k = 0; k < p.raw.triIndices.Length; k++)
                {
                    int t = p.raw.triIndices[k];
                    int a = map[tris[t * 3]];
                    int b = map[tris[t * 3 + 1]];
                    int c = map[tris[t * 3 + 2]];
                    if (flip) { int tmp = b; b = c; c = tmp; }
                    tgt.Add(a); tgt.Add(b); tgt.Add(c);
                }
            }

            if (verts.Count == 0) return null;
            var m = new Mesh
            {
                name = name,
                indexFormat = verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16,
            };
            m.SetVertices(verts);
            if (allNormals) m.SetNormals(norms);
            m.SetUVs(0, uvs);
            if (allTangents) m.SetTangents(tangents);
            m.subMeshCount = subLists.Count;
            for (int i = 0; i < subLists.Count; i++) m.SetTriangles(subLists[i], i);
            if (!allNormals) m.RecalculateNormals();
            if (!allTangents && allUv) m.RecalculateTangents();
            m.RecalculateBounds();
            return m;
        }

        // ================================================================ AuditAngles

        sealed class PartAudit
        {
            public Part part;
            public int corners;
            public int acute;
            public int quantFail;
            public float maxDev;
            public float sumDev;
            public int faces;
            public int chamferFaces;
            public bool curved;
            public int openLoops;
            public int badLoops;
            public int twoSegmentLoops;
        }

        static void AuditCore()
        {
            var rep = new StringBuilder();
            var scan = Collect(rep);
            var audits = new List<PartAudit>(scan.All.Count);

            foreach (var p in scan.All)
            {
                if (p.TriCount < 2) continue;
                var a = AnalyzeAngles(p);
                if (a != null) audits.Add(a);
            }

            int acuteTotal = audits.Sum(a => a.acute);
            int quantFailTotal = audits.Sum(a => a.quantFail);
            int cornersTotal = audits.Sum(a => a.corners);
            int curvedParts = audits.Count(a => a.curved);
            float maxDev = audits.Count > 0 ? audits.Max(a => a.maxDev) : 0f;
            float meanDev = cornersTotal > 0 ? audits.Sum(a => a.sumDev) / Mathf.Max(1, cornersTotal) : 0f;

            rep.AppendLine("== D. 3 Lines Rule 门禁审计（只报告，不修改任何东西） ==");
            rep.AppendLine($"  审计部件 {audits.Count:N0} 个（曲线/回转件剔除量化检查 {curvedParts} 个）；" +
                           $"多边形内角 {cornersTotal:N0} 个");
            rep.AppendLine($"  R1 钝角铁律：内角 <90° 共 {acuteTotal} 个（{(cornersTotal > 0 ? 100f * acuteTotal / cornersTotal : 0f):F3}%）");
            rep.AppendLine($"  R2 135° 量化：不合格 {quantFailTotal} 个（{100f * quantFailTotal / Mathf.Max(1, cornersTotal):F2}%）；" +
                           $"最大偏差 {maxDev:F2}° / 平均偏差 {meanDev:F3}°（合法角 90°/135°）");
            rep.AppendLine($"  R3 转角倒角代理：无任何 45° 切面的方盒件 {audits.Count(a => !a.curved && a.faces >= 6 && a.chamferFaces == 0)} 个");
            rep.AppendLine($"  R6 三线法则：仅 2 段 1 折的环 {audits.Sum(a => a.twoSegmentLoops)} 个；" +
                           $"非流形/开放环 {audits.Sum(a => a.badLoops + a.openLoops)} 个");

            // 线宽极差（R4）：薄条厚度/宽度
            if (scan.Thin.Count > 0)
            {
                var byCat = scan.Thin.GroupBy(p => p.category ?? "?");
                rep.AppendLine("  R4 等宽校验（薄条）：");
                foreach (var g in byCat.OrderByDescending(x => x.Count()))
                {
                    float minW = g.Min(p => p.WidthM), maxW = g.Max(p => p.WidthM);
                    float minT = g.Min(p => p.ThicknessM), maxT = g.Max(p => p.ThicknessM);
                    bool pass = SkillEqualWidthPass(minW, maxW, P_WidthTolMm * 0.001f);
                    rep.AppendLine($"      {g.Key,-14} {g.Count(),6} 条  宽 {minW * 1000f:F2}–{maxW * 1000f:F2}mm" +
                                   $"（极差 {(maxW - minW) * 1000f:F2}mm，{(pass ? "PASS" : "FAIL")}）；" +
                                   $"厚 {minT * 1000f:F2}–{maxT * 1000f:F2}mm");
                }
            }
            else
            {
                rep.AppendLine("  R4 等宽校验：当前场景无薄条（已压平或未生成）。");
            }

            // 拼接板间隙（合并后的"缝宽"读数）
            var gaps = CollectSeamGaps(scan);
            if (gaps.Count > 0)
            {
                gaps.Sort();
                rep.AppendLine($"  拼接板相邻缝宽（共面、间隙 ≤50mm 的板对）：{gaps.Count} 对；" +
                               $"最小 {gaps[0]:F2}mm / 中位 {gaps[gaps.Count / 2]:F2}mm / 最大 {gaps[gaps.Count - 1]:F2}mm；" +
                               $"极差 {gaps[gaps.Count - 1] - gaps[0]:F2}mm");
            }

            // 问题对象 Top
            rep.AppendLine("  R1/R2 问题最多的对象（前 10）：");
            foreach (var a in audits.Where(x => x.acute > 0 || x.quantFail > 0)
                                    .OrderByDescending(x => x.acute * 10 + x.quantFail).Take(10))
            {
                rep.AppendLine($"      {a.part.ownerPath}  cat={a.part.category}  " +
                               $"锐角 {a.acute} / 量化不合格 {a.quantFail} / 内角 {a.corners}");
            }

            if (acuteTotal == 0 && quantFailTotal == 0)
                rep.AppendLine("  结论：R1/R2 全过（未发现 <90° 内角与 90/135° 量化偏差）。");
            else
                rep.AppendLine("  结论：存在不合格项——按用户口径只报告，不自动改几何；" +
                               "优先检查上面列出的对象/部件。");

            Debug.Log(rep.ToString());
        }

        /// <summary>对单个 part 的每个平面多边形做内角/量化/倒角统计。</summary>
        static PartAudit AnalyzeAngles(Part p)
        {
            var mesh = p.mesh;
            var vs = mesh.vertices;
            var tris = mesh.triangles;
            var l2w = p.tr.localToWorldMatrix;

            // 焊接（part 内，世界坐标）
            var weld = new Dictionary<Key3, int>();
            var wpos = new List<Vector3>();
            var vmap = new int[vs.Length];
            for (int i = 0; i < vs.Length; i++) vmap[i] = -1;
            for (int i = 0; i < p.raw.verts.Length; i++)
            {
                int oi = p.raw.verts[i];
                var wp = l2w.MultiplyPoint3x4(vs[oi]);
                var k = Key3.Of(wp, 100000f);
                int id;
                if (!weld.TryGetValue(k, out id))
                {
                    id = wpos.Count;
                    weld[k] = id;
                    wpos.Add(wp);
                }
                vmap[oi] = id;
            }

            // 三角形 → 平面分组
            var planeTris = new Dictionary<PlaneKey, List<int>>();
            var planeNormals = new Dictionary<PlaneKey, Vector3>();
            for (int k = 0; k < p.raw.triIndices.Length; k++)
            {
                int t = p.raw.triIndices[k];
                int a = vmap[tris[t * 3]], b = vmap[tris[t * 3 + 1]], c = vmap[tris[t * 3 + 2]];
                if (a < 0 || b < 0 || c < 0) continue;
                var pa = wpos[a]; var pb = wpos[b]; var pc = wpos[c];
                var n = Vector3.Cross(pb - pa, pc - pa);
                if (n.sqrMagnitude < 1e-12f) continue;
                n.Normalize();
                var key = PlaneKey.Of(n, pa);
                List<int> list;
                if (!planeTris.TryGetValue(key, out list)) { list = new List<int>(); planeTris[key] = list; planeNormals[key] = n; }
                list.Add(t);
            }

            var audit = new PartAudit { part = p, faces = planeTris.Count };
            audit.curved = DetectCurved(p, planeTris.Count, planeNormals.Values);

            foreach (var kv in planeTris)
            {
                var normal = planeNormals[kv.Key];
                if (SkillChamferProxy(normal, p.axes)) audit.chamferFaces++;

                // 边计数（part 内、同一平面组）
                var count = new Dictionary<long, int>();
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    int t = kv.Value[i];
                    int a = vmap[tris[t * 3]], b = vmap[tris[t * 3 + 1]], c = vmap[tris[t * 3 + 2]];
                    AddEdge(count, a, b); AddEdge(count, b, c); AddEdge(count, c, a);
                }

                // 边界边
                var boundary = new List<long>();
                foreach (var e in count) if (e.Value == 1) boundary.Add(e.Key);
                if (boundary.Count < 3) continue;

                // 邻接
                var adj = new Dictionary<int, List<int>>();
                for (int i = 0; i < boundary.Count; i++)
                {
                    int a, b; UnEdge(boundary[i], out a, out b);
                    AddAdj(adj, a, b); AddAdj(adj, b, a);
                }

                // 追踪环
                var used = new HashSet<long>(boundary);
                while (used.Count > 0)
                {
                    long first = used.First();
                    int a, b; UnEdge(first, out a, out b);
                    used.Remove(first);
                    var loop = new List<int> { a, b };
                    int prev = a, cur = b;
                    bool closed = false;
                    int guard = 0;
                    while (guard++ < 100000)
                    {
                        List<int> nb;
                        if (!adj.TryGetValue(cur, out nb)) break;
                        if (nb.Count != 2) { audit.badLoops++; break; }
                        int next = nb[0] == prev ? nb[1] : nb[0];
                        long e = EdgeKey(cur, next);
                        if (used.Remove(e))
                        {
                            if (next == loop[0]) { closed = true; break; }
                            loop.Add(next);
                            prev = cur; cur = next;
                        }
                        else
                        {
                            long rev = EdgeKey(next, cur);
                            if (used.Remove(rev))
                            {
                                if (next == loop[0]) { closed = true; break; }
                                loop.Add(next);
                                prev = cur; cur = next;
                            }
                            else break;
                        }
                    }

                    if (!closed) { audit.openLoops++; continue; }
                    if (loop.Count < 3) continue;

                    int corners = loop.Count;
                    int segments = loop.Count;
                    audit.corners += corners;
                    if (!SkillThreeLineUnit(segments, corners)) audit.twoSegmentLoops++;

                    for (int i = 0; i < loop.Count; i++)
                    {
                        var pa = wpos[loop[(i - 1 + loop.Count) % loop.Count]];
                        var pb = wpos[loop[i]];
                        var pc = wpos[loop[(i + 1) % loop.Count]];
                        float deg = Vector3.Angle(pa - pb, pc - pb);
                        if (deg > 179.5f) continue;                 // 共线点不算转角
                        if (!SkillObtusePass(deg)) audit.acute++;

                        if (!audit.curved)
                        {
                            float dev = SkillQuantDeviation(deg);
                            if (dev > 1.0f)
                            {
                                audit.quantFail++;
                                audit.sumDev += dev;
                                if (dev > audit.maxDev) audit.maxDev = dev;
                            }
                        }
                    }
                }
            }

            return audit.corners > 0 ? audit : null;
        }

        /// <summary>
        /// 回转/曲线件判定：面数多，且与 OBB 三轴对齐的主面不足 6 个（圆柱侧面/圆弧段）。
        /// 方柱/方板即使面数多，6 个轴向主面仍在 → 照常做 90/135° 量化检查。
        /// </summary>
        static bool DetectCurved(Part p, int faceCount, IEnumerable<Vector3> normals)
        {
            if (faceCount < 16) return false;
            int aligned = 0;
            foreach (var n in normals)
            {
                float ax = Mathf.Abs(Vector3.Dot(n, p.axes[0]));
                float ay = Mathf.Abs(Vector3.Dot(n, p.axes[1]));
                float az = Mathf.Abs(Vector3.Dot(n, p.axes[2]));
                if (ax > 0.999f || ay > 0.999f || az > 0.999f) aligned++;
            }
            return aligned < 6;
        }

        static List<float> CollectSeamGaps(ScanResult scan)
        {
            var gaps = new List<float>();
            var plates = scan.Plates;
            if (plates.Count < 2) return gaps;

            // 仅在同一材质/同一区块的平面桶内两两比较（桶内通常只有几块到几十块）
            var buckets = new Dictionary<string, List<Part>>();
            for (int i = 0; i < plates.Count; i++)
            {
                var p = plates[i];
                string key = MatKey(p.material) + "|" + (p.block ?? "?") + "|" + PlaneBucket(p);
                List<Part> list;
                if (!buckets.TryGetValue(key, out list)) { list = new List<Part>(); buckets[key] = list; }
                list.Add(p);
            }
            foreach (var list in buckets.Values)
            {
                if (list.Count < 2 || list.Count > 400) continue;
                var n = list[0].ThinNormal;
                var u = list[0].axes[2];
                var v = list[0].axes[1];
                for (int i = 0; i < list.Count; i++)
                {
                    if (!InPlaneAxesAligned(list[i], u, v, n)) continue;
                    var ri = ProjectRect(list[i], u, v);
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (!Coplanar(list[i], list[j], 0.05f)) continue;
                        if (!InPlaneAxesAligned(list[j], u, v, n)) continue;
                        var rj = ProjectRect(list[j], u, v);
                        float sepX = Mathf.Max(ri.min.x - rj.max.x, rj.min.x - ri.max.x);
                        float sepY = Mathf.Max(ri.min.y - rj.max.y, rj.min.y - ri.max.y);
                        float overlapX = Mathf.Min(ri.max.x, rj.max.x) - Mathf.Max(ri.min.x, rj.min.x);
                        float overlapY = Mathf.Min(ri.max.y, rj.max.y) - Mathf.Max(ri.min.y, rj.min.y);
                        float gap;
                        if (sepX >= 0f && overlapY > 0.005f) gap = sepX;
                        else if (sepY >= 0f && overlapX > 0.005f) gap = sepY;
                        else continue;
                        if (gap <= 0.05f) gaps.Add(gap * 1000f);
                    }
                }
            }
            return gaps;
        }

        // ================================================================ 状态 / 资产 / 场景

        [Serializable]
        sealed class State
        {
            public int version = 1;
            public List<StateRec> records = new List<StateRec>();
        }

        [Serializable]
        sealed class StateRec
        {
            public string globalId;
            public string objectPath;
            public string objectName;
            public string originalMeshPath;
            public string backupMeshPath;
            public string[] originalMaterialPaths;
            public bool originalActive;
            public bool originalRendererEnabled = true;
            public bool changedMesh;
            public bool changedActive;
            public bool changedMaterials;
        }

        static State LoadState()
        {
            string disk = DiskPath(StatePath);
            if (!File.Exists(disk)) return new State();
            try
            {
                var s = JsonUtility.FromJson<State>(File.ReadAllText(disk));
                if (s == null) return new State();
                if (s.records == null) s.records = new List<StateRec>();
                return s;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[拼接板] 状态文件读取失败，按空状态继续：{e.Message}");
                return new State();
            }
        }

        static void SaveState(State s)
        {
            if (s == null || s.records == null || s.records.Count == 0) return;
            EnsureDir();
            File.WriteAllText(DiskPath(StatePath), JsonUtility.ToJson(s, true));
            AssetDatabase.ImportAsset(StatePath);
        }

        static void RecordObject(State state, MeshFilter mf, bool changedMesh, bool changedActive, bool changedMaterials)
        {
            if (mf == null) return;
            string gid = GlobalIdOf(mf);
            string path = PathOf(mf.transform);
            StateRec rec = null;
            for (int i = 0; i < state.records.Count; i++)
            {
                var r = state.records[i];
                if (r == null) continue;
                if ((!string.IsNullOrEmpty(gid) && r.globalId == gid) ||
                    (string.IsNullOrEmpty(r.globalId) && r.objectPath == path))
                {
                    rec = r; break;
                }
            }
            if (rec == null)
            {
                rec = new StateRec
                {
                    globalId = gid,
                    objectPath = path,
                    objectName = mf.gameObject.name,
                    originalActive = mf.gameObject.activeSelf,
                    originalRendererEnabled = mf.GetComponent<Renderer>() != null
                        ? mf.GetComponent<Renderer>().enabled : true,
                    originalMeshPath = AssetDatabase.GetAssetPath(mf.sharedMesh),
                    originalMaterialPaths = MaterialPaths(mf.GetComponent<Renderer>()),
                };
                if (string.IsNullOrEmpty(rec.originalMeshPath) && mf.sharedMesh != null)
                {
                    // 内嵌/场景网格：复制一份备份资产（Clear 时用来还原）
                    var backup = Object.Instantiate(mf.sharedMesh);
                    backup.name = "PS_Backup_" + SafeName(mf.gameObject.name) + "_" + StableHash(path);
                    EnsureFolder(BackupDir);
                    string bp = BackupDir + "/" + backup.name + ".asset";
                    if (AssetDatabase.LoadAssetAtPath<Mesh>(bp) != null) AssetDatabase.DeleteAsset(bp);
                    AssetDatabase.CreateAsset(backup, bp);
                    rec.backupMeshPath = bp;
                }
                state.records.Add(rec);
            }
            rec.changedMesh |= changedMesh;
            rec.changedActive |= changedActive;
            rec.changedMaterials |= changedMaterials;
        }

        static string[] MaterialPaths(Renderer r)
        {
            if (r == null) return new string[0];
            var mats = r.sharedMaterials;
            var list = new List<string>(mats.Length);
            for (int i = 0; i < mats.Length; i++) list.Add(mats[i] != null ? AssetDatabase.GetAssetPath(mats[i]) : string.Empty);
            return list.ToArray();
        }

        static string GlobalIdOf(Object o)
        {
            try { return GlobalObjectId.GetGlobalObjectIdSlow(o).ToString(); }
            catch { return string.Empty; }
        }

        static void ClearCore()
        {
            var rep = new StringBuilder();
            var state = LoadState();
            int restored = 0, missing = 0;

            foreach (var rec in state.records)
            {
                var mf = Resolve(rec);
                if (mf == null) { missing++; continue; }

                Mesh mesh = null;
                if (!string.IsNullOrEmpty(rec.originalMeshPath))
                    mesh = AssetDatabase.LoadAssetAtPath<Mesh>(rec.originalMeshPath);
                if (mesh == null && !string.IsNullOrEmpty(rec.backupMeshPath))
                    mesh = AssetDatabase.LoadAssetAtPath<Mesh>(rec.backupMeshPath);
                if (mesh != null) mf.sharedMesh = mesh;

                if (rec.changedMaterials && rec.originalMaterialPaths != null && rec.originalMaterialPaths.Length > 0)
                {
                    var r = mf.GetComponent<Renderer>();
                    if (r != null)
                    {
                        var mats = new Material[rec.originalMaterialPaths.Length];
                        for (int i = 0; i < mats.Length; i++)
                        {
                            if (string.IsNullOrEmpty(rec.originalMaterialPaths[i])) continue;
                            mats[i] = AssetDatabase.LoadAssetAtPath<Material>(rec.originalMaterialPaths[i]);
                        }
                        r.sharedMaterials = mats;
                    }
                }

                mf.gameObject.SetActive(rec.originalActive);
                var rr = mf.GetComponent<Renderer>();
                if (rr != null) rr.enabled = rec.originalRendererEnabled;
                MarkChanged(mf);
                restored++;
            }

            // 删掉本工具生成的 mesh（保留 Backup/）
            int deleted = 0;
            if (AssetDatabase.IsValidFolder(OutDir))
            {
                var guids = AssetDatabase.FindAssets("t:Mesh", new[] { OutDir });
                foreach (var g in guids)
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    if (string.IsNullOrEmpty(p)) continue;
                    if (p.Replace('\\', '/').Contains("/Backup/")) continue;
                    if (!System.IO.Path.GetFileName(p).StartsWith("PS_", StringComparison.Ordinal)) continue;
                    AssetDatabase.DeleteAsset(p);
                    deleted++;
                }
            }
            AssetDatabase.DeleteAsset(ManifestPath);
            AssetDatabase.DeleteAsset(StatePath);
            // AssetDatabase 只认同已导入资产；再补一刀磁盘删除，确保下次不会读到旧状态
            try { File.Delete(DiskPath(ManifestPath)); } catch { }
            try { File.Delete(DiskPath(StatePath)); } catch { }

            AssetDatabase.SaveAssets();
            SaveScene();
            rep.AppendLine($"[清除] 还原对象 {restored} 个（找不到 {missing} 个）；删除生成 mesh {deleted} 个；" +
                           $"备份 mesh 保留在 {BackupDir}");
            Debug.Log(rep.ToString());
        }

        static MeshFilter Resolve(StateRec rec)
        {
            if (rec == null) return null;
            if (!string.IsNullOrEmpty(rec.globalId))
            {
                try
                {
                    if (GlobalObjectId.TryParse(rec.globalId, out var gid))
                    {
                        var o = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                        var mf = o as MeshFilter;
                        if (mf != null && mf.gameObject.scene == EditorSceneManager.GetActiveScene()) return mf;
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(rec.objectPath)) return null;
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid()) return null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (PathOf(root.transform) == rec.objectPath) return root.GetComponent<MeshFilter>();
                // PathOf 含根名；Transform.Find 需要去掉根名前缀
                var prefix = root.name + "/";
                if (!rec.objectPath.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var t = root.transform.Find(rec.objectPath.Substring(prefix.Length));
                if (t != null) return t.GetComponent<MeshFilter>();
            }
            return null;
        }

        static void SaveManifest(ScanResult scan, StringBuilder rep)
        {
            if (scan == null) return;
            if (scan.Thin.Count == 0)
            {
                // 幂等重跑时不要用空 manifest 覆盖上一轮的槽位表（贴图侧还要用）
                rep.AppendLine("[manifest] 本轮无薄条，保留现有 manifest 不覆盖。");
                return;
            }
            EnsureDir();
            var mf = new Manifest
            {
                scene = ScenePath,
                thicknessLimitMm = P_ThickMm,
                widthLimitMm = P_WidthMm,
                gapLimitMm = P_GapMm,
                stripCount = scan.Thin.Count,
                stripTriangles = scan.ThinTris,
                seams = new List<SeamEntry>(scan.Thin.Count),
            };
            for (int i = 0; i < scan.Thin.Count; i++)
            {
                var p = scan.Thin[i];
                var host = p.host;
                mf.seams.Add(new SeamEntry
                {
                    sourceObject = p.ownerName,
                    sourcePath = p.ownerPath,
                    category = p.category,
                    block = p.block,
                    host = host != null ? host.name : string.Empty,
                    hostPath = host != null ? host.path : string.Empty,
                    worldCenter = F3(p.oCenter),
                    worldSize = F3(new Vector3(p.ext[0], p.ext[1], p.ext[2])),
                    axisNormal = F3(p.axes[0]),
                    axisWidth = F3(p.axes[1]),
                    axisLength = F3(p.axes[2]),
                    hostCenter = host != null ? F3(host.bounds.center) : new float[3],
                    hostSize = host != null ? F3(host.bounds.size) : new float[3],
                    thicknessMm = p.ext[0] * 1000f,
                    widthMm = p.ext[1] * 1000f,
                    lengthMm = p.ext[2] * 1000f,
                    triangles = p.TriCount,
                    uvMin = new[] { p.uvMin.x, p.uvMin.y },
                    uvMax = new[] { p.uvMax.x, p.uvMax.y },
                });
            }
            File.WriteAllText(DiskPath(ManifestPath), JsonUtility.ToJson(mf, true));
            AssetDatabase.ImportAsset(ManifestPath);
            rep.AppendLine($"[manifest] {mf.stripCount} 条槽位（世界位置/朝向/尺寸/宿主/UV 范围）→ {ManifestPath}");
        }

        [Serializable]
        sealed class Manifest
        {
            public string scene;
            public string tool = "PanelSeam";
            public int version = 1;
            public float thicknessLimitMm;
            public float widthLimitMm;
            public float gapLimitMm;
            public int stripCount;
            public int stripTriangles;
            public List<SeamEntry> seams = new List<SeamEntry>();
        }

        [Serializable]
        sealed class SeamEntry
        {
            public string sourceObject;
            public string sourcePath;
            public string category;
            public string block;
            public string host;
            public string hostPath;
            public float[] worldCenter;
            public float[] worldSize;      // 厚度, 宽度, 长度（米）
            public float[] axisNormal;     // 槽法线（薄轴）
            public float[] axisWidth;
            public float[] axisLength;
            public float[] hostCenter;
            public float[] hostSize;
            public float thicknessMm;
            public float widthMm;
            public float lengthMm;
            public int triangles;
            public float[] uvMin;
            public float[] uvMax;
        }

        static float[] F3(Vector3 v) { return new[] { v.x, v.y, v.z }; }

        static string MeshAssetPath(string prefix, string key)
        {
            EnsureFolder(OutDir);
            return OutDir + "/" + prefix + "_" + SafeName(key) + "_" + StableHash(key) + ".asset";
        }

        static void EnsureDir()
        {
            EnsureFolder(OutDir);
        }

        /// <summary>把 "Assets/..." 资产路径转成磁盘绝对路径（从 Application.dataPath 推工程根，不依赖 cwd）。</summary>
        static string DiskPath(string assetPath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return System.IO.Path.Combine(projectRoot,
                assetPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parts = path.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }

        /// <summary>
        /// 让一个生成件"退出渲染"：无碰撞、无子物体时直接停用；否则只关 Renderer，
        /// 保住碰撞体/子物体（合并/压平不能悄悄改掉可行走面）。
        /// </summary>
        static void RetireObject(GameObject go)
        {
            if (go == null) return;
            bool hasCollider = go.GetComponent<Collider>() != null ||
                               go.GetComponentsInChildren<Collider>(true).Length > 0;
            bool hasChildren = go.transform.childCount > 0;
            if (!hasCollider && !hasChildren)
            {
                go.SetActive(false);
                return;
            }
            var r = go.GetComponent<Renderer>();
            if (r != null) r.enabled = false;
        }

        static void MarkChanged(MeshFilter mf)
        {
            if (mf == null) return;
            EditorUtility.SetDirty(mf);
            if (PrefabUtility.IsPartOfPrefabInstance(mf))
                PrefabUtility.RecordPrefabInstancePropertyModifications(mf);
        }

        static void SaveScene()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) return;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        static bool EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return true;
            if (cur.IsValid() && cur.isDirty && !string.IsNullOrEmpty(cur.path))
            {
                Debug.LogError($"[拼接板] 当前场景有未保存改动（{cur.path}），拒绝切场景。请先保存或确认后再跑。");
                return false;
            }
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log($"[拼接板] 已打开场景 {ScenePath}");
            return true;
        }

        // ================================================================ 工具函数

        static string PathOf(Transform t)
        {
            if (t == null) return string.Empty;
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        static string BlockOf(Transform t)
        {
            for (var cur = t; cur != null; cur = cur.parent)
            {
                var m = System.Text.RegularExpressions.Regex.Match(cur.name ?? string.Empty, @"S_?0([1-7])");
                if (m.Success) return "S_0" + m.Groups[1].Value;
            }
            return "S_00";
        }

        static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "x";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length && sb.Length < 80; i++)
            {
                char c = s[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')
                    sb.Append(c);
                else sb.Append('_');
            }
            return sb.ToString();
        }

        /// <summary>进程无关的稳定 hash（不用 string.GetHashCode，避免每次启动路径名变化）。</summary>
        static uint StableHash(string s)
        {
            uint h = 2166136261u;
            if (s != null)
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
            return h;
        }

        static string MatKey(Material m)
        {
            if (m == null) return "<null>";
            return m.GetInstanceID().ToString(CultureInfo.InvariantCulture);
        }

        static int MatId(Material m)
        {
            return m == null ? 0 : m.GetInstanceID();
        }

        static string MatName(Material m) { return m == null ? "<null>" : m.name; }

        static long GridKey(float x, float y, float z, float cell)
        {
            long ix = (long)Mathf.Floor(x / cell);
            long iy = (long)Mathf.Floor(y / cell);
            long iz = (long)Mathf.Floor(z / cell);
            return (ix * 73856093L) ^ (iy * 19349663L) ^ (iz * 83492791L);
        }

        static long CellKey(float x, float y, float cell)
        {
            return CellKeyInt(Mathf.FloorToInt(x / cell), Mathf.FloorToInt(y / cell));
        }

        static long CellKeyInt(int x, int y)
        {
            return ((long)x << 32) ^ (uint)y;
        }

        static void AddEdge(Dictionary<long, int> map, int a, int b)
        {
            long k = EdgeKey(a, b);
            map[k] = map.TryGetValue(k, out var c) ? c + 1 : 1;
        }

        static long EdgeKey(int a, int b)
        {
            int lo = Mathf.Min(a, b), hi = Mathf.Max(a, b);
            return ((long)lo << 32) ^ (uint)hi;
        }

        static void UnEdge(long key, out int a, out int b)
        {
            a = (int)(key >> 32);
            b = (int)(key & 0xFFFFFFFFL);
        }

        static void AddAdj(Dictionary<int, List<int>> adj, int a, int b)
        {
            List<int> list;
            if (!adj.TryGetValue(a, out list)) { list = new List<int>(); adj[a] = list; }
            if (!list.Contains(b)) list.Add(b);
        }

        // ---------------------------------------------------------------- 基础结构

        struct Key3 : IEquatable<Key3>
        {
            public int x, y, z;
            public static Key3 Of(Vector3 v, float inv)
            {
                return new Key3
                {
                    x = Mathf.RoundToInt(v.x * inv),
                    y = Mathf.RoundToInt(v.y * inv),
                    z = Mathf.RoundToInt(v.z * inv),
                };
            }
            public bool Equals(Key3 o) { return x == o.x && y == o.y && z == o.z; }
            public override bool Equals(object o) { return o is Key3 && Equals((Key3)o); }
            public override int GetHashCode()
            {
                unchecked { return (x * 73856093) ^ (y * 19349663) ^ (z * 83492791); }
            }
        }

        struct PlaneKey : IEquatable<PlaneKey>
        {
            public int nx, ny, nz, d;
            public static PlaneKey Of(Vector3 n, Vector3 p)
            {
                return new PlaneKey
                {
                    nx = Mathf.RoundToInt(n.x * 500f),
                    ny = Mathf.RoundToInt(n.y * 500f),
                    nz = Mathf.RoundToInt(n.z * 500f),
                    d = Mathf.RoundToInt(Vector3.Dot(n, p) * 10000f),
                };
            }
            public bool Equals(PlaneKey o) { return nx == o.nx && ny == o.ny && nz == o.nz && d == o.d; }
            public override bool Equals(object o) { return o is PlaneKey && Equals((PlaneKey)o); }
            public override int GetHashCode()
            {
                unchecked { return ((nx * 73856093) ^ (ny * 19349663) ^ (nz * 83492791) ^ d); }
            }
        }

        sealed class UnionFind
        {
            readonly int[] parent;
            public UnionFind(int n)
            {
                parent = new int[n];
                for (int i = 0; i < n; i++) parent[i] = i;
            }
            public int Find(int x)
            {
                while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
                return x;
            }
            public void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[rb] = ra;
            }
        }

        // ---------------------------------------------------------------- 参数

        static void ParseArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                switch (a[i])
                {
                    case "-ps-thickmm": P_ThickMm = Fl(a, i, P_ThickMm); break;
                    case "-ps-widthmm": P_WidthMm = Fl(a, i, P_WidthMm); break;
                    case "-ps-gapmm": P_GapMm = Fl(a, i, P_GapMm); break;
                    case "-ps-exemptmm": P_ExemptMm = Fl(a, i, P_ExemptMm); break;
                    case "-ps-widthtolmm": P_WidthTolMm = Fl(a, i, P_WidthTolMm); break;
                    case "-ps-uvisland": P_UvIsland = In(a, i, P_UvIsland); break;
                    case "-ps-dec": P_ScanDec = In(a, i, P_ScanDec); break;
                    case "-ps-mapskin": P_ScanMapskin = In(a, i, P_ScanMapskin); break;
                    case "-ps-verbose": P_Verbose = In(a, i, P_Verbose); break;
                    case "-ps-maxtris": P_MaxTrisPerMesh = In(a, i, P_MaxTrisPerMesh); break;
                    case "-ps-maxverts": P_MaxTrisPerMesh = In(a, i, P_MaxTrisPerMesh); break;   // 旧名兼容
                }
            }
            P_ThickMm = Mathf.Max(0.1f, P_ThickMm);
            P_WidthMm = Mathf.Max(0.5f, P_WidthMm);
            P_GapMm = Mathf.Max(0f, P_GapMm);
            P_ExemptMm = Mathf.Max(P_ThickMm, P_ExemptMm);
        }

        static float Fl(string[] a, int i, float def)
        {
            if (i + 1 >= a.Length) return def;
            return float.TryParse(a[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
        }

        static int In(string[] a, int i, int def)
        {
            if (i + 1 >= a.Length) return def;
            return int.TryParse(a[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
        }
    }
}
