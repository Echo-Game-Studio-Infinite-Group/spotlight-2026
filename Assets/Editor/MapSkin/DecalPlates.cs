// DecalPlates.cs — "装饰贴板/铭牌"：把 25 套刻线贴图当贴纸**稀疏点缀**在光滑板面上（绝不平铺）。
//
// 为什么不是 deferred decal：真实项目里"贴板/铭牌"的通行做法就是**一块略微凸起的小板**——
// 省掉 alpha 排序与投影开销（NPR 延迟管线尤其如此），代价是几何可见（板厚 6mm + 12mm 浮起）。
// 关键约束是**板必须完整落在目标面板内部、不跨缝、不被裁切**，所以：
//   · 候选点一律离面轮廓边界 ≥ 0.55m（> 板的最坏半展 0.50m：0.95 方板转 3° 时的半对角）
//   · 板最大边 ≤ 0.95m（地/墙板模数最小 1.2m，四周至少留 0.12m 余量）
//
// 口径（与派单一致）：
//   ① 目标面：地面（n.y>0.6、面积 ≥25㎡，isWalkable 优先）+ 墙面（|n.y|<0.35、面积 ≥40㎡）；
//      跳过 objectPath 含 DEC_ / MAPSKIN_ / Gate / Rack / SM_ / HST_ 的（生成件上不再贴生成件）。
//   ② 候选格：面的 u/v 上撒 1.6m 网格，逐面按 seed 抖动相位 ±0.4m；轮廓内缩 0.55m。
//   ③ 命中率：按 seed 随机保留 6%（地面）/ 3%（墙面）；再排除"离门中心 <4m"与"离已放贴板 <1.4m"。
//   ④ 贴板尺寸：两条边各自独立取 0.55–0.95m、长宽比 ≤1.6:1、最大边 ≤0.95m；厚度 6mm、
//      四棱 45° 倒角 1.5mm；正面贴图 UV 0–1（图案完整不裁切）。
//   ⑤ 姿态：+Z 沿面法线 n 外凸，面内对齐面的 u/v 轴（±3° 抖动）；位置 = 候选点世界坐标 + n×0.012
//      （躲开地面铺板顶面 / 墙板面，防 z-fighting）。贴板原点 = **背面中心**，几何沿 +n 长出 6mm。
//   ⑥ 图案分配：25 套按 seed 选；同一面上的贴板不重复同一套，全局每套出现次数大致均衡（打印直方图）；
//      地面优先 *_Floor 变体、墙面优先非 Floor。
//   ⑦ 上限：-maxdecals（默认 160）、-budget（默认 40000 三角，单板 28 三角）。
//      摊到更多面上：逐轮每面最多落 1 块——先把候选铺到尽可能多的面（不让最大的几个地面吃光预算），
//      一轮下来还有空位再回填下一轮，直到上限 / 预算 / 候选用尽；-perface 可限每面最多几块。
//
// 生成物（全部挂在 identity 根 MAPSKIN_Decal 下）：
//   根 MAPSKIN_Decal → 每组一个对象 <区块>_<类别>（Floor / Wall），
//   每组一个合并 Mesh（世界坐标烘进顶点）+ 一个 MeshRenderer + **每套材质一个子网格**（少 draw call）；
//   mesh 资产 → Assets/Art/EnvSkins/Decal/<区块>_<类别>.asset；
//   材质 → Assets/Art/Materials/MapSkin/Decal/M_Decal_<套名>.mat（shader = OurFunction/NPR/Deferred Toon，
//   toon 三件套照抄 MapSkinApply.cs；已存在则复用；tiling 固定 1,1 —— 不平铺）。
//
// 不碰什么（硬约束）：任何 Collider、场景原有对象、别人生成的根、其它工具的材质/贴图资产、Level1.unity；
//   不写 prefab。Clear 只删自己名下的根、MeshDir 下的合并 mesh、MatDir 下本工具的材质。
//
// 幂等：规划全部由 seed 哈希决定（同参数同结果）；重跑先删自己的根与 mesh 资产再建。
//
// 菜单：MapSkin/贴纸/① 干跑  ② 执行  ③ 清除
// 批处理（public static void、无参，配合 -executeMethod）：
//   -executeMethod MapSkin.DecalPlates.DryRun    只读：面筛选 / 候选 / 命中 / 直方图 / 逐组三角，零写盘
//   -executeMethod MapSkin.DecalPlates.Run       建材质 + 合并 mesh + 装配 + 存场景 + 落盘清单
//   -executeMethod MapSkin.DecalPlates.Clear     删 MAPSKIN_Decal 根与生成的 mesh / 材质资产
// 参数：-scene / -faces / -out / -maxdecals / -budget / -floorpct / -wallpct / -onlyfloors 1|0
//       / -gates（默认取 -faces 同目录的 GATE_Manifest.json；缺则退回扫场景 Gate 容器）/ -perface
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MapSkin
{
    public static class DecalPlates
    {
        // ==================================================================== 常量

        /// <summary>本工具生成物根名（幂等重跑与 Clear 的唯一抓手）。</summary>
        const string RootName = "MAPSKIN_Decal";

        const string DefaultScenePath = "Assets/Scenes/Level1_Skin.unity";
        const string DefaultFacesPath = "F:/VSCode/taptap-game-jem/_session_audit/face_flat.json";
        const string DefaultOutPath = "F:/VSCode/taptap-game-jem/_session_audit/decal_plates.json";

        /// <summary>贴图集所在（25 套刻线贴图，见 MapSkinLineDispatch）。</summary>
        const string ImgRoot = "Assets/Art/Images/HST_Prags";
        /// <summary>贴板材质落点（本工具独占目录）。</summary>
        const string MatDir = "Assets/Art/Materials/MapSkin/Decal";
        const string MatPrefix = "M_Decal_";
        /// <summary>合并 mesh 落点（本工具独占目录）。</summary>
        const string MeshDir = "Assets/Art/EnvSkins/Decal";

        /// <summary>NPR 母 shader（与 MapSkinApply.cs 同口径）。</summary>
        const string NprShaderName = "OurFunction/NPR/Deferred Toon";

        // ---- 面筛选 ----
        const float FloorNy = 0.6f;          // n.y > 此值 = 地面
        const float WallNyAbs = 0.35f;       // |n.y| < 此值 = 竖直墙
        const float FloorMinArea = 25f;      // 地面最小面积（㎡）
        const float WallMinArea = 40f;       // 墙面最小面积（㎡）
        /// <summary>可走地面少于这么多面时，退回"全部地面"（否则小场景会一块都落不下）。</summary>
        const int WalkableFaceMin = 25;
        /// <summary>路径黑名单：生成件 / 门 / 机架 / 共享件上不再贴生成件。</summary>
        static readonly string[] SkipPathKeys = { "DEC_", "MAPSKIN_", "Gate", "Rack", "SM_", "HST_" };

        // ---- 候选格 ----
        const float GridStep = 1.6f;         // 网格步长（米）
        const float PhaseAmp = 0.4f;         // 每轴相位抖动 ±0.4m
        const float EdgeMargin = 0.55f;      // 候选点离轮廓边界的最小距离（保证板完整落在面内）

        // ---- 贴板几何 ----
        const float PlateMin = 0.55f;        // 短边下限
        const float PlateMax = 0.95f;        // 长边硬上限（地/墙板模数最小 1.2m → 四周 ≥0.12m 余量）
        const float PlateRatioMax = 1.6f;    // 长宽比上限
        const float PlateThick = 0.006f;     // 厚度 6mm
        const float PlateBevel = 0.0015f;    // 四棱 45° 倒角 1.5mm
        const float SurfaceOffset = 0.012f;  // 沿 n 外移 12mm（躲开铺板顶面 / 墙板面）
        const float YawJitterDeg = 3f;       // 面内旋转抖动 ±3°
        const int TrisPerPlate = 28;         // 单板三角（正面 2 + 三圈环 24 + 背面 2）

        // ---- 排除 ----
        const float DoorClear = 4f;          // 门中心 3D 半径 4m 内不落
        const float MinSpacing = 1.4f;       // 贴板中心最小间距

        // ---- NPR 三件套（规范 §2.1 标准参数，数值照抄 MapSkinApply.cs）----
        static readonly Color ToonShadowColor = new Color(0.30f, 0.48f, 0.53f);
        const float ToonSoftness = 0.15f;
        const float ToonThreshold = 1.0f;

        // ---- 贴图套 ----
        static readonly string[] Letters = { "A_Ortho", "B_Step", "C_Island", "D_Jog", "E_Bulk" };
        static readonly string[] Variants = { "White", "White_Floor", "Black", "Black_Floor", "Cyan" };
        /// <summary>全部 25 套（T_HS_Line_&lt;族&gt;_&lt;变体&gt;），顺序定死 → 直方图与平衡选套可复现。</summary>
        static readonly string[] AllSets = BuildAllSets();
        /// <summary>地面优先池：10 套 *_Floor 变体。</summary>
        static readonly string[] FloorPool = BuildPool(floorOnly: true);
        /// <summary>墙面优先池：15 套非 Floor 变体（含青族）。</summary>
        static readonly string[] WallPool = BuildPool(floorOnly: false);

        // ==================================================================== 参数（-xxx 可覆盖）

        static string P_Scene = DefaultScenePath;
        static string P_Faces = DefaultFacesPath;
        static string P_Out = DefaultOutPath;
        static string P_Gates = null;        // null = 取 -faces 同目录的 GATE_Manifest.json
        static int P_MaxDecals = 160;        // -maxdecals  贴板总数上限
        static long P_Budget = 40000;        // -budget     三角预算
        static float P_FloorPct = 6f;        // -floorpct   地面候选命中率（%）
        static float P_WallPct = 3f;         // -wallpct    墙面候选命中率（%）
        static int P_OnlyFloors = 0;         // -onlyfloors 1 = 只做地面
        static int P_PerFace = 0;            // -perface    每面最多几块（0 = 不限，按上限/预算收）

        // ==================================================================== 菜单

        [MenuItem("MapSkin/贴纸/① 干跑")]
        public static void DryRunMenu() => DryRun();

        [MenuItem("MapSkin/贴纸/② 执行")]
        public static void RunMenu()
        {
            ParseArgs();
            if (!Application.isBatchMode && !EditorUtility.DisplayDialog("装饰贴板",
                    $"把刻线贴图当贴板点缀进 {P_Scene}：\n" +
                    $"· 面表 {P_Faces}\n" +
                    $"· 上限 {P_MaxDecals} 块 / 预算 {P_Budget:N0} 三角；命中率 地面 {P_FloorPct:0.#}% 、墙面 {P_WallPct:0.#}%\n" +
                    $"· 材质 {MatDir}/{MatPrefix}<套名>.mat\n" +
                    $"· 合并 mesh {MeshDir}/<区块>_<类别>.asset，根 {RootName}\n\n继续？",
                    "执行", "取消")) return;
            Run();
        }

        [MenuItem("MapSkin/贴纸/③ 清除")]
        public static void ClearMenu()
        {
            if (!Application.isBatchMode && !EditorUtility.DisplayDialog("清除装饰贴板",
                    $"删除场景里的 {RootName} 根对象，以及\n· {MeshDir}/ 下的合并 mesh\n· {MatDir}/ 下本工具建的材质\n\n清除？",
                    "清除", "取消")) return;
            Clear();
        }

        // ==================================================================== 批处理入口

        /// <summary>干跑：面筛选 / 候选 / 命中 / 排除 / 直方图 / 逐组三角，**零写盘零改场景**。
        /// 批处理：-executeMethod MapSkin.DecalPlates.DryRun</summary>
        public static void DryRun()
        {
            ParseArgs();
            if (!EnsureScene(false)) return;

            var plan = BuildPlan();
            var groups = GroupPlates(plan);
            var sb = new StringBuilder();
            sb.AppendLine($"[贴纸] 干跑（零写盘）：scene={P_Scene}");
            AppendReport(sb, plan, groups, dryRun: true);
            Debug.Log(sb.ToString());
        }

        /// <summary>执行：建材质 + 合并 mesh + 装配 + 存场景 + 落盘清单。
        /// 批处理：-executeMethod MapSkin.DecalPlates.Run</summary>
        public static void Run()
        {
            var t0 = DateTime.Now;
            ParseArgs();
            if (!EnsureScene(true)) return;

            var plan = BuildPlan();
            var groups = GroupPlates(plan);

            // 材质母 shader 不在位就别动场景（宁可什么都不做，也不要留下半套生成物）
            if (groups.Count > 0 && Shader.Find(NprShaderName) == null)
            {
                Debug.LogError($"[贴纸] 找不到 shader {NprShaderName}（{MatDir} 的材质建不起来），本轮中止，场景未改动。");
                return;
            }

            // 幂等：先删自己的根与 mesh 资产（材质复用，不删）
            int removedRoot = RemoveRoot();
            int removedMesh = ClearMeshAssets();
            if (removedRoot > 0) Debug.Log($"[贴纸] 幂等：先删旧根 {RootName} ×{removedRoot}");
            if (removedMesh > 0) Debug.Log($"[贴纸] 幂等：先删旧 mesh 资产 ×{removedMesh}");

            var sb = new StringBuilder();
            sb.AppendLine($"[贴纸] 执行完成，用时 {(DateTime.Now - t0).TotalSeconds:F1}s");

            if (groups.Count == 0)
            {
                sb.AppendLine("[贴纸] 没有可放置的贴板（面表为空 / 全被过滤 / 上限或预算为 0），未生成任何对象。");
                AppendReport(sb, plan, groups, dryRun: false);
                if (removedRoot > 0) SaveScene();
                Debug.Log(sb.ToString());
                return;
            }

            // 材质（每套用到的贴图一份，已存在则复用）
            var mats = EnsureMaterials(plan, out int matMade, out int matReused);

            // 逐组合并 mesh → 建对象
            BuildGroupAssets(groups, mats);

            SaveScene();
            WriteOut(plan, groups, mats);

            AppendReport(sb, plan, groups, dryRun: false);
            sb.AppendLine($"· 材质：新建 {matMade} 份、复用 {matReused} 份 → {MatDir}/{MatPrefix}<套名>.mat（tiling 1,1 —— 不平铺）");
            sb.AppendLine($"· mesh 资产：{groups.Count} 份 → {MeshDir}/；场景对象：1 + {groups.Count} 个（根 {RootName} + <区块>_<类别>）");
            sb.AppendLine($"· 清单：{P_Out}");
            sb.AppendLine($"· 场景已存盘：{P_Scene}");
            Debug.Log(sb.ToString());
        }

        /// <summary>清除：删场景里的 MAPSKIN_Decal 根 + 本工具生成的合并 mesh / 材质资产。
        /// 批处理：-executeMethod MapSkin.DecalPlates.Clear</summary>
        public static void Clear()
        {
            ParseArgs();
            if (!EnsureScene(false)) return;

            int objs = RemoveRoot();
            int meshes = ClearMeshAssets();
            int matN = ClearMaterials();
            if (objs > 0) SaveScene();
            Debug.Log($"[贴纸] 已清除：根对象 {objs} 个（{RootName}）、mesh 资产 {meshes} 份（{MeshDir}/）、" +
                      $"材质 {matN} 份（{MatDir}/）。贴图与场景原有物件未动。");
        }

        // ==================================================================== 面表 / 面筛选

        [Serializable] public class FaceDoc { public int version; public List<FaceRec> faces = new List<FaceRec>(); }

        /// <summary>face_flat.json 的一条扁平面（字段与 JsonUtility 直读对齐；seed 在 JSON 里是字符串）。</summary>
        [Serializable]
        public class FaceRec
        {
            public string id, objectPath, style, shape, sourceMaterial, seed, adjacentStyle;
            public int rendererIndex, submesh, panelIndex, panelCount, variantIndex;
            public float w, h, wallHeightM;
            public bool small, isWalkable;
            public float[] origin, u, v, n, outline, worldCenter, baseColor;
        }

        [Serializable] class GateDoc { public List<GateRec> gates = new List<GateRec>(); }

        [Serializable] class GateRec
        {
            public string block, type, container;
            public float[] pos;
            public float yaw, width, height, surfaceTopY, surfaceArea;
        }

        /// <summary>筛选后的目标面（世界坐标已解算成 u/v/n 正交基）。</summary>
        class Face
        {
            public string id, block, seed;
            public bool isFloor, walkable;
            public Vector3 origin, U, V, N;
            public float w, h, area;
            public Vector2[] poly;                    // 轮廓（面局部 u/v 坐标，逆/顺时针都行）
            public readonly List<Cand> cands = new List<Cand>();   // 过命中率与门排除的候选（落点顺序）
            public int cursor;
        }

        struct Cand
        {
            public float lu, lv;
            public Vector3 world;                     // 已含沿 n 的 12mm 外移
        }

        /// <summary>一块贴板（规划结果）。</summary>
        class Plate
        {
            public Face face;
            public string setId;
            public Vector3 pos, N, U, V;
            public float w, h, yawDeg;
        }

        /// <summary>按 区块_类别 分好的一组（每组一个 mesh / 一个 MeshRenderer / 每套材质一个子网格）。</summary>
        class Group
        {
            public string name;                       // <区块>_<类别>
            public readonly List<Plate> plates = new List<Plate>();
            public readonly List<string> slots = new List<string>();   // 子网格序（材质名升序）
            public Mesh mesh;
            public Material[] mats;
        }

        class Plan
        {
            public readonly List<Face> floors = new List<Face>();
            public readonly List<Face> walls = new List<Face>();
            public readonly List<Plate> plates = new List<Plate>();
            public readonly Dictionary<string, int> setUse = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<string, HashSet<string>> faceUsed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            public readonly List<Vector3> gates = new List<Vector3>();
            public string gateSource = "";
            public string floorRule = "";
            public float floorArea, wallArea;
            public int gridPoints, candPoints, hitPoints, doorOut, spacingOut;
            public bool cappedByMax, cappedByBudget;
            public int passes;                                // 实际轮数（每轮每面最多 1 块）
        }

        static List<FaceRec> LoadFaces()
        {
            if (!File.Exists(P_Faces)) { Debug.LogError($"[贴纸] 面表不存在：{P_Faces}"); return null; }
            try
            {
                var doc = JsonUtility.FromJson<FaceDoc>(File.ReadAllText(P_Faces));
                if (doc == null || doc.faces == null || doc.faces.Count == 0)
                {
                    Debug.LogError($"[贴纸] 面表解析为空：{P_Faces}");
                    return null;
                }
                return doc.faces;
            }
            catch (Exception e)
            {
                Debug.LogError($"[贴纸] 面表读取失败：{e.Message}");
                return null;
            }
        }

        /// <summary>一条面记录 → 目标面（不合格返回 null）。</summary>
        static Face MakeFace(FaceRec r)
        {
            if (r == null || r.origin == null || r.u == null || r.v == null || r.n == null) return null;
            if (r.origin.Length < 3 || r.u.Length < 3 || r.v.Length < 3 || r.n.Length < 3) return null;
            if (r.w <= 0f || r.h <= 0f) return null;

            var n = new Vector3(r.n[0], r.n[1], r.n[2]);
            if (n.sqrMagnitude < 1e-8f) return null;
            n.Normalize();
            var u = new Vector3(r.u[0], r.u[1], r.u[2]);
            u -= Vector3.Dot(u, n) * n;                       // 就地正交化（数据里 u/v/n 理论正交，防脏数据）
            if (u.sqrMagnitude < 1e-8f) return null;
            u.Normalize();

            bool floor = n.y > FloorNy;
            bool wall = Mathf.Abs(n.y) < WallNyAbs;
            if (!floor && !wall) return null;                 // 天花 / 斜面不做

            float area = r.w * r.h;
            if (floor && area < FloorMinArea) return null;
            if (wall && area < WallMinArea) return null;

            var path = r.objectPath ?? "";
            foreach (var key in SkipPathKeys)
                if (path.IndexOf(key, StringComparison.Ordinal) >= 0) return null;

            var f = new Face
            {
                id = r.id ?? path,
                seed = r.seed ?? "0",
                block = BlockOf(path, r.id),
                isFloor = floor,
                walkable = r.isWalkable,
                origin = new Vector3(r.origin[0], r.origin[1], r.origin[2]),
                U = u,
                V = Vector3.Cross(n, u),                      // 数据约定 n = u × v → v = n × u
                N = n,
                w = r.w,
                h = r.h,
                area = area,
                poly = PolyOf(r),
            };
            return f;
        }

        /// <summary>区块名：取 objectPath 首段并去掉 BOX_ 前缀（BOX_S_02 → S_02）。</summary>
        static string BlockOf(string objectPath, string id)
        {
            var s = !string.IsNullOrEmpty(objectPath) ? objectPath : (id ?? "");
            int slash = s.IndexOf('/');
            if (slash > 0) s = s.Substring(0, slash);
            if (s.StartsWith("BOX_", StringComparison.Ordinal)) s = s.Substring(4);
            return string.IsNullOrEmpty(s) ? "NOBLOCK" : s;
        }

        /// <summary>面轮廓（局部 u/v）。outline 缺/过短时退回矩形四角。</summary>
        static Vector2[] PolyOf(FaceRec r)
        {
            var o = r.outline;
            if (o != null && o.Length >= 6 && o.Length % 2 == 0)
            {
                var pts = new Vector2[o.Length / 2];
                for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(o[i * 2], o[i * 2 + 1]);
                return pts;
            }
            float hw = r.w * 0.5f, hh = r.h * 0.5f;
            return new[]
            {
                new Vector2(-hw, -hh), new Vector2(hw, -hh), new Vector2(hw, hh), new Vector2(-hw, hh),
            };
        }

        // ==================================================================== 候选格 / 命中 / 排除

        /// <summary>
        /// 撒候选点：u/v 上的 1.6m 网格（逐面 seed 抖动相位 ±0.4m），要求点落轮廓内且离边界 ≥0.55m；
        /// 再按 seed 命中率（地面 6% / 墙面 3%）保留，最后剔掉门口 4m 内的点。
        /// 相位、命中骰子、尺寸、朝向全部来自 seed 哈希 → 同参数每次跑结果逐位一致。
        /// </summary>
        static void FillCandidates(Face f, float pct, Plan plan)
        {
            string salt = f.seed + "|" + f.id;
            float pu = (Hash01(salt + "|pu") * 2f - 1f) * PhaseAmp;
            float pv = (Hash01(salt + "|pv") * 2f - 1f) * PhaseAmp;
            float hw = f.w * 0.5f, hh = f.h * 0.5f;
            float startU = -hw + GridStep * 0.5f + pu;
            float startV = -hh + GridStep * 0.5f + pv;
            float hit = Mathf.Clamp01(pct * 0.01f);

            int k = 0;
            for (float lu = startU; lu <= hw + 1e-4f; lu += GridStep)
            {
                for (float lv = startV; lv <= hh + 1e-4f; lv += GridStep)
                {
                    plan.gridPoints++;
                    int myK = k++;
                    if (!InsidePoly(f.poly, lu, lv)) continue;
                    if (DistToPolyEdge(f.poly, lu, lv) < EdgeMargin) continue;
                    plan.candPoints++;
                    if (Hash01(salt + "|hit" + myK) >= hit) continue;
                    plan.hitPoints++;

                    var world = f.origin + f.U * lu + f.V * lv + f.N * SurfaceOffset;
                    if (NearPoint(plan.gates, world, DoorClear)) { plan.doorOut++; continue; }
                    f.cands.Add(new Cand { lu = lu, lv = lv, world = world });
                }
            }
        }

        /// <summary>
        /// 落点：逐轮、每面最多 1 块 —— 先把候选摊到尽可能多的面上（避免最大的几个地面吃光预算），
        /// 一轮下来还有空位再回填第二轮、第三轮……直到上限或候选用尽（-perface 可限每面块数）。
        /// 每次落点都要过 1.4m 间距筛。轮数、上限、预算三者都记在日志里。
        /// </summary>
        static void PlaceAll(Plan plan)
        {
            var order = new List<Face>();
            order.AddRange(plan.floors);                      // 地面优先（间距争用里地面先占）
            if (P_OnlyFloors == 0) order.AddRange(plan.walls);
            if (order.Count == 0) return;

            int maxPass = P_PerFace > 0 ? P_PerFace : int.MaxValue;
            bool progress = true;
            for (int pass = 0; pass < maxPass && progress; pass++)
            {
                progress = false;
                foreach (var f in order)
                {
                    if (plan.plates.Count >= P_MaxDecals) { plan.cappedByMax = true; break; }
                    if ((plan.plates.Count + 1L) * TrisPerPlate > P_Budget) { plan.cappedByBudget = true; break; }

                    while (f.cursor < f.cands.Count)
                    {
                        var c = f.cands[f.cursor++];
                        if (TooClose(plan.plates, c.world)) { plan.spacingOut++; continue; }
                        plan.plates.Add(MakePlate(plan, f, c));
                        progress = true;
                        break;
                    }
                }
                plan.passes++;
                if (plan.cappedByMax || plan.cappedByBudget) break;
            }
        }

        /// <summary>造一块贴板：尺寸 / 朝向抖动 / 图案（同面不重复 + 全局均衡）。</summary>
        static Plate MakePlate(Plan plan, Face f, Cand c)
        {
            int idx = plan.plates.Count;
            string salt = f.seed + "|" + f.id + "|p" + idx;

            // 尺寸：两条边各自独立取 0.55–0.95，超 1.6:1 压回来；长边沿 u（面的 u 轴）
            float a = Mathf.Lerp(PlateMin, PlateMax, Hash01(salt + "|w"));
            float b = Mathf.Lerp(PlateMin, PlateMax, Hash01(salt + "|h"));
            float longSide = Mathf.Max(a, b), shortSide = Mathf.Min(a, b);
            if (longSide > shortSide * PlateRatioMax) longSide = shortSide * PlateRatioMax;
            float pw = Mathf.Min(longSide, PlateMax);                   // 最大边 ≤ 0.95 硬约束
            float ph = Mathf.Clamp(shortSide, PlateMin, pw);

            var p = new Plate
            {
                face = f,
                setId = PickSet(plan, f, salt),
                pos = c.world,
                N = f.N,
                U = f.U,
                V = f.V,
                w = pw,
                h = ph,
                yawDeg = (Hash01(salt + "|yaw") * 2f - 1f) * YawJitterDeg,
            };

            if (!plan.setUse.ContainsKey(p.setId)) plan.setUse[p.setId] = 0;
            plan.setUse[p.setId]++;
            if (!plan.faceUsed.TryGetValue(f.id, out var used))
            {
                used = new HashSet<string>(StringComparer.Ordinal);
                plan.faceUsed[f.id] = used;
            }
            used.Add(p.setId);
            return p;
        }

        /// <summary>选套：同面不重复 → 全局用量最少（并列时按哈希蓄水池抽样，稳定可复现）。</summary>
        static string PickSet(Plan plan, Face f, string salt)
        {
            var prefer = f.isFloor ? FloorPool : WallPool;    // 地面优先 *_Floor、墙面优先非 Floor
            var alt = f.isFloor ? WallPool : FloorPool;
            plan.faceUsed.TryGetValue(f.id, out var used);

            var pick = ChooseBalanced(plan, prefer, used, salt)
                    ?? ChooseBalanced(plan, alt, used, salt)
                    ?? ChooseBalanced(plan, prefer, null, salt)
                    ?? ChooseBalanced(plan, alt, null, salt)
                    ?? prefer[0];
            return pick;
        }

        static string ChooseBalanced(Plan plan, string[] pool, HashSet<string> exclude, string salt)
        {
            string best = null;
            int bestUse = int.MaxValue, ties = 0;
            foreach (var s in pool)
            {
                if (exclude != null && exclude.Contains(s)) continue;
                if (LoadTex(s, "BC") == null) continue;                       // 贴图不在位的不选
                int use = plan.setUse.TryGetValue(s, out var c) ? c : 0;
                if (use < bestUse) { bestUse = use; best = s; ties = 1; }
                else if (use == bestUse) { ties++; if (Hash01(salt + "|" + s) < 1f / ties) best = s; }
            }
            return best;
        }

        // ==================================================================== 规划总入口

        static Plan BuildPlan()
        {
            var plan = new Plan();
            plan.gates.AddRange(LoadGates(out plan.gateSource));

            var recs = LoadFaces();
            if (recs == null) return plan;

            // ① 面筛选（地面 / 墙面）
            var allFloors = new List<Face>();
            var allWalls = new List<Face>();
            foreach (var r in recs)
            {
                var f = MakeFace(r);
                if (f == null) continue;
                if (f.isFloor) { allFloors.Add(f); plan.floorArea += f.area; }
                else { allWalls.Add(f); plan.wallArea += f.area; }
            }
            // 可走地面优先（太少则退回全部地面，否则小场景一块都落不下）
            var walk = allFloors.FindAll(f => f.walkable);
            var floorSel = walk.Count >= WalkableFaceMin ? walk : allFloors;
            plan.floorRule = walk.Count >= WalkableFaceMin
                ? $"可走地面 {walk.Count}/{allFloors.Count} 面（可走 ≥{WalkableFaceMin} 面，取可走）"
                : $"全部地面 {allFloors.Count} 面（可走仅 {walk.Count} 面 < {WalkableFaceMin}，退回全量）";

            // ② 候选（面按"面积降序 + id"定序，保证可复现）
            floorSel.Sort(CompareFace);
            allWalls.Sort(CompareFace);
            plan.floors.AddRange(floorSel);
            plan.walls.AddRange(allWalls);

            foreach (var f in plan.floors) FillCandidates(f, P_FloorPct, plan);
            if (P_OnlyFloors == 0)
                foreach (var f in plan.walls) FillCandidates(f, P_WallPct, plan);

            // ③ 落点
            PlaceAll(plan);
            return plan;
        }

        static int CompareFace(Face a, Face b)
        {
            int c = b.area.CompareTo(a.area);                 // 大面先撒（视觉优先）
            return c != 0 ? c : string.CompareOrdinal(a.id, b.id);
        }

        // ==================================================================== 门中心

        /// <summary>读门位（默认 -faces 同目录的 GATE_Manifest.json）；读不到就扫场景里的 Gate 容器。</summary>
        static List<Vector3> LoadGates(out string source)
        {
            var list = new List<Vector3>();
            var path = P_Gates;
            if (string.IsNullOrEmpty(path))
            {
                var dir = Path.GetDirectoryName(P_Faces);
                path = Path.Combine(dir ?? "", "GATE_Manifest.json");
            }
            if (File.Exists(path))
            {
                try
                {
                    var doc = JsonUtility.FromJson<GateDoc>(File.ReadAllText(path));
                    if (doc != null && doc.gates != null)
                        foreach (var g in doc.gates)
                            if (g.pos != null && g.pos.Length >= 3)
                                list.Add(new Vector3(g.pos[0], g.pos[1], g.pos[2]));
                    if (list.Count > 0) { source = $"{path}（{list.Count} 樘门）"; return list; }
                }
                catch (Exception e) { Debug.LogWarning($"[贴纸] 门表读取失败：{e.Message}"); }
            }

            // 兜底：场景里的 Gate / GateV2 容器，取各子渲染器包围盒中心
            foreach (var name in new[] { "DEC_Shared/Gate", "DEC_Shared/GateV2" })
            {
                var go = GameObject.Find(name);
                if (go == null) continue;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    if (r != null && r.enabled) list.Add(r.bounds.center);
            }
            source = list.Count > 0 ? $"场景 Gate 容器（{list.Count} 个中心）" : "无（门排除已跳过）";
            return list;
        }

        // ==================================================================== 几何：贴板

        /// <summary>
        /// 往组缓冲追加一块贴板。局部坐标：+X = 长边（面的 u）、+Y = 短边（面的 v）、+Z = 外法线；
        /// 原点 = 背面中心（板从原点沿 +Z 长出 6mm）。28 三角 = 正面 2 + 前棱倒角 8 + 侧壁 8 +
        /// 背棱倒角 8 + 背面 2；顶点按面拆开 → 硬边、法线 flat。
        /// 正面 UV 0–1（贴图完整不裁切），倒角环 / 侧壁 UV 钳在边界（1.5mm 倒角，视觉不可辨）。
        /// </summary>
        static void AppendPlate(List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<Vector4> tans,
                                List<int> tris, Matrix4x4 rot, Vector3 pos, float w, float h)
        {
            float w2 = w * 0.5f, h2 = h * 0.5f;
            float b = PlateBevel, t = PlateThick;
            float ax = Mathf.Max(0.02f, w2 - b), ay = Mathf.Max(0.02f, h2 - b);

            // 四个角环（逆时针，从 +Z 看）：A 正面内圈 / B 前外圈 / C 后外圈 / D 背面内圈
            var A = new[] { new Vector3(-ax, -ay, t), new Vector3(ax, -ay, t), new Vector3(ax, ay, t), new Vector3(-ax, ay, t) };
            var B = new[] { new Vector3(-w2, -h2, t - b), new Vector3(w2, -h2, t - b), new Vector3(w2, h2, t - b), new Vector3(-w2, h2, t - b) };
            var C = new[] { new Vector3(-w2, -h2, b), new Vector3(w2, -h2, b), new Vector3(w2, h2, b), new Vector3(-w2, h2, b) };
            var D = new[] { new Vector3(-ax, -ay, 0f), new Vector3(ax, -ay, 0f), new Vector3(ax, ay, 0f), new Vector3(-ax, ay, 0f) };
            var auv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };

            var tanW = rot.MultiplyVector(Vector3.right);      // uv.x 沿局部 +X → 全板同一根切线
            var tangent = new Vector4(tanW.x, tanW.y, tanW.z, 1f);

            void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                      Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3)
            {
                Vector3 w0 = pos + rot.MultiplyVector(p0);
                Vector3 w1 = pos + rot.MultiplyVector(p1);
                Vector3 w2v = pos + rot.MultiplyVector(p2);
                Vector3 w3 = pos + rot.MultiplyVector(p3);
                Vector3 nn = Vector3.Cross(w1 - w0, w2v - w0).normalized;
                int baseIdx = verts.Count;
                verts.Add(w0); verts.Add(w1); verts.Add(w2v); verts.Add(w3);
                for (int i = 0; i < 4; i++) { norms.Add(nn); tans.Add(tangent); }
                uvs.Add(uv0); uvs.Add(uv1); uvs.Add(uv2); uvs.Add(uv3);
                tris.Add(baseIdx); tris.Add(baseIdx + 1); tris.Add(baseIdx + 2);
                tris.Add(baseIdx); tris.Add(baseIdx + 2); tris.Add(baseIdx + 3);
            }

            // 环：P = 靠前（z 大）的一圈，Q = 靠后的一圈；(P_i, Q_i, Q_j, P_j) 即外法线朝外的绕序
            void Ring(Vector3[] P, Vector3[] Q)
            {
                for (int i = 0; i < 4; i++)
                {
                    int j = (i + 1) & 3;
                    Quad(P[i], Q[i], Q[j], P[j], auv[i], auv[i], auv[j], auv[j]);
                }
            }

            Quad(A[0], A[1], A[2], A[3], auv[0], auv[1], auv[2], auv[3]);   // 正面（UV 0–1）
            Quad(D[0], D[3], D[2], D[1], auv[0], auv[3], auv[2], auv[1]);   // 背面（朝 -Z）
            Ring(A, B);                                                     // 前棱 45° 倒角
            Ring(B, C);                                                     // 侧壁
            Ring(C, D);                                                     // 背棱 45° 倒角
        }

        // ==================================================================== 分组 / 合并 mesh / 建对象

        /// <summary>按 <区块>_<类别> 分组（类别 = Floor / Wall），组名与子网格序都定死。</summary>
        static List<Group> GroupPlates(Plan plan)
        {
            var map = new Dictionary<string, Group>(StringComparer.Ordinal);
            var list = new List<Group>();
            foreach (var p in plan.plates)
            {
                string key = p.face.block + "_" + (p.face.isFloor ? "Floor" : "Wall");
                if (!map.TryGetValue(key, out var g))
                {
                    g = new Group { name = key };
                    map[key] = g;
                    list.Add(g);
                }
                g.plates.Add(p);
            }
            list.Sort((x, y) => string.CompareOrdinal(x.name, y.name));
            foreach (var g in list)
            {
                foreach (var p in g.plates)
                    if (!g.slots.Contains(p.setId)) g.slots.Add(p.setId);
                g.slots.Sort(StringComparer.Ordinal);          // 子网格序 = 套名升序（可复现）
            }
            return list;
        }

        /// <summary>建材质（每套用到的贴图一份）并逐组合并 mesh、装配对象。</summary>
        static void BuildGroupAssets(List<Group> groups, Dictionary<string, Material> mats)
        {
            EnsureFolder(MeshDir);
            var root = new GameObject(RootName);
            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var g in groups) BuildGroupMesh(g);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            foreach (var g in groups)
            {
                var go = new GameObject(g.name);
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                go.AddComponent<MeshFilter>().sharedMesh = g.mesh;

                var arr = new Material[g.slots.Count];
                for (int i = 0; i < arr.Length; i++) mats.TryGetValue(g.slots[i], out arr[i]);
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = arr;
                mr.shadowCastingMode = ShadowCastingMode.On;
                mr.receiveShadows = true;
            }
            // 合并 mesh 已是世界坐标、根 identity → 每层都保持 identity
        }

        /// <summary>把一组贴板合并成一个多子网格 mesh（按套分 submesh），写进 MeshDir。</summary>
        static void BuildGroupMesh(Group g)
        {
            var verts = new List<Vector3>(g.plates.Count * 56);
            var norms = new List<Vector3>(g.plates.Count * 56);
            var uvs = new List<Vector2>(g.plates.Count * 56);
            var tans = new List<Vector4>(g.plates.Count * 56);
            var tris = new List<int>[g.slots.Count];
            for (int i = 0; i < tris.Length; i++) tris[i] = new List<int>(g.plates.Count * 6);

            var slotOf = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < g.slots.Count; i++) slotOf[g.slots[i]] = i;

            foreach (var p in g.plates)
            {
                var rot = Matrix4x4.Rotate(PlateRotation(p));
                AppendPlate(verts, norms, uvs, tans, tris[slotOf[p.setId]], rot, p.pos, p.w, p.h);
            }

            var mesh = new Mesh
            {
                name = g.name,
                indexFormat = IndexFormat.UInt32,              // 顶点数不受 64k 限制（-maxdecals 放大时也安全）
            };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTangents(tans);
            mesh.subMeshCount = tris.Length;
            for (int i = 0; i < tris.Length; i++) mesh.SetTriangles(tris[i], i);
            mesh.RecalculateBounds();

            AssetDatabase.CreateAsset(mesh, $"{MeshDir}/{g.name}.asset");
            g.mesh = mesh;
        }

        /// <summary>贴板姿态：+Z = 面法线 n、+X = 面的 u 轴（再绕 n 抖 ±3°）、+Y = v 轴。</summary>
        static Quaternion PlateRotation(Plate p) =>
            Quaternion.LookRotation(p.N, p.V) * Quaternion.AngleAxis(p.yawDeg, Vector3.forward);

        // ==================================================================== 材质

        /// <summary>为本轮用到的每套贴图建材质（已存在则复用），返回 套名 → 材质。</summary>
        static Dictionary<string, Material> EnsureMaterials(Plan plan, out int made, out int reused)
        {
            made = 0; reused = 0;
            var mats = new Dictionary<string, Material>(StringComparer.Ordinal);
            var shader = Shader.Find(NprShaderName);
            if (shader == null) { Debug.LogError($"[贴纸] 找不到 shader {NprShaderName}，跳过材质与装配"); return mats; }

            EnsureFolder(MatDir);
            var sets = new List<string>(plan.setUse.Keys);
            sets.Sort(StringComparer.Ordinal);
            foreach (var set in sets)
            {
                var path = $"{MatDir}/{MatPrefix}{set}.mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null)
                {
                    m = new Material(shader) { name = MatPrefix + set };
                    AssetDatabase.CreateAsset(m, path);
                    made++;
                }
                else
                {
                    if (m.shader != shader) m.shader = shader;
                    reused++;
                }
                ApplyMaterial(m, set);
                EditorUtility.SetDirty(m);
                mats[set] = m;
            }
            AssetDatabase.SaveAssets();
            return mats;
        }

        /// <summary>材质属性（幂等写死）：NPR 三件套 + BC/N/MS 三槽 + 族底色；tiling 固定 1,1（不平铺）。</summary>
        static void ApplyMaterial(Material m, string set)
        {
            m.SetColor("_ShadowColor", ToonShadowColor);
            m.SetFloat("_ToonSoftness", ToonSoftness);
            m.SetFloat("_ToonThreshold", ToonThreshold);
            m.SetColor("_BaseColor", FamilyTint(set));

            var bc = LoadTex(set, "BC");
            var nn = LoadTex(set, "N");
            var ms = LoadTex(set, "MS");

            if (bc != null)
            {
                m.SetTexture("_BaseMap", bc);
                m.SetTextureScale("_BaseMap", Vector2.one);
                m.SetTextureOffset("_BaseMap", Vector2.zero);
            }
            if (nn != null)
            {
                m.SetTexture("_BumpMap", nn);
                m.SetTextureScale("_BumpMap", Vector2.one);
                m.SetTextureOffset("_BumpMap", Vector2.zero);
                m.SetFloat("_BumpScale", 1f);
                m.EnableKeyword("_NORMALMAP");
            }
            if (ms != null)
            {
                m.SetTexture("_MetallicGlossMap", ms);
                m.SetTextureScale("_MetallicGlossMap", Vector2.one);
                m.SetTextureOffset("_MetallicGlossMap", Vector2.zero);
                m.SetFloat("_SmoothnessTextureChannel", 0f);   // 0 = 取 MS 的 A（smoothness）
                m.EnableKeyword("_METALLICSPECGLOSSMAP");
            }
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Smoothness", set.EndsWith("_Floor", StringComparison.Ordinal) ? 1f : 0.5f);
            m.SetFloat("_Surface", 0f);                        // 不透明
            m.SetFloat("_AlphaClip", 0f);
            m.DisableKeyword("_ALPHATEST_ON");
        }

        /// <summary>族底色：白族 0.85 / 黑族 0.35 / 青族 0.55+青（压住贴图自带的青，保住"青 = 标注"语义）。</summary>
        static Color FamilyTint(string set)
        {
            if (set.EndsWith("_Cyan", StringComparison.Ordinal)) return new Color(0.55f, 0.95f, 1.00f);
            if (set.IndexOf("_Black", StringComparison.Ordinal) >= 0) return new Color(0.35f, 0.35f, 0.35f);
            return new Color(0.85f, 0.85f, 0.85f);
        }

        // ==================================================================== 贴图加载（带缓存）

        static readonly Dictionary<string, Texture2D> _texCache = new Dictionary<string, Texture2D>(StringComparer.Ordinal);

        /// <summary>取工程内贴图 Assets/Art/Images/HST_Prags/&lt;套&gt;/&lt;套&gt;_&lt;通道&gt;.png（缺则 null，不建不拷）。</summary>
        static Texture2D LoadTex(string set, string channel)
        {
            var key = set + "|" + channel;
            if (_texCache.TryGetValue(key, out var cached)) return cached;
            var path = $"{ImgRoot}/{set}/{set}_{channel}.png";
            var t = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (t == null) Debug.LogWarning($"[贴纸] 贴图缺失：{path}（该套不参与）");
            _texCache[key] = t;
            return t;
        }

        // ==================================================================== 报告

        static void AppendReport(StringBuilder sb, Plan plan, List<Group> groups, bool dryRun)
        {
            sb.AppendLine($"· 面表：{P_Faces}；门位：{plan.gateSource}");
            sb.AppendLine($"· 入选面：地面 {plan.floors.Count} 面 / {plan.floorArea:N0} ㎡（{plan.floorRule}）；" +
                          $"墙面 {plan.walls.Count} 面 / {plan.wallArea:N0} ㎡" +
                          $"（口径：地面 n.y>{FloorNy} 面积≥{FloorMinArea:0}㎡、墙面 |n.y|<{WallNyAbs} 面积≥{WallMinArea:0}㎡，" +
                          $"已剔除路径含 {'{'} {string.Join(" ", SkipPathKeys)} {'}'} 的面）");
            sb.AppendLine($"· 候选点：网格点 {plan.gridPoints:N0} → 轮廓内缩 {EdgeMargin:0.##}m 后有效 {plan.candPoints:N0}" +
                          $"（{GridStep:0.#}m 网格、逐面 seed 抖动相位 ±{PhaseAmp:0.#}m）");
            sb.AppendLine($"· 命中：{plan.hitPoints:N0} 点（地面 {P_FloorPct:0.#}% / 墙面 {P_WallPct:0.#}%）" +
                          $"；排除 门口 {DoorClear:0}m 内 {plan.doorOut} 点、间距 {MinSpacing:0.#}m 内 {plan.spacingOut} 点");
            sb.AppendLine($"· 放置：{plan.plates.Count} 块 / 上限 {P_MaxDecals}" +
                          $"（{plan.passes} 轮、每轮每面最多 1 块{(P_PerFace > 0 ? "、-perface 限轮" : "")}）" +
                          (plan.cappedByMax ? "，被上限截断" : "") + (plan.cappedByBudget ? "，被预算截断" : ""));
            long tris = (long)plan.plates.Count * TrisPerPlate;
            double pctUsed = P_Budget > 0 ? tris * 100.0 / P_Budget : 0.0;
            sb.AppendLine($"· 三角：{plan.plates.Count} × {TrisPerPlate} = {tris:N0} / 预算 {P_Budget:N0}（{pctUsed:F1}%）");

            // 每套使用次数（25 套全列，未用的记 0）
            var sbUse = new StringBuilder();
            foreach (var set in AllSets)
                sbUse.Append($"{(plan.setUse.TryGetValue(set, out var c) ? c : 0)} ");
            sb.AppendLine($"· 每套使用次数（A/B/C/D/E 族 × White/White_Floor/Black/Black_Floor/Cyan）：{sbUse.ToString().Trim()}");

            // 逐组三角
            if (groups.Count == 0) sb.AppendLine("· 分组：无（0 块）");
            else
            {
                sb.AppendLine($"· 分组 {groups.Count} 组（每组合并 mesh + 每套材质一个子网格；子网格合计 {CountSlots(groups)} ≈ draw call）：");
                foreach (var g in groups)
                    sb.AppendLine($"    {g.name,-16} {g.plates.Count,4} 块  {g.plates.Count * TrisPerPlate,6} 三角  " +
                                  $"{g.slots.Count} 子网格 [{string.Join(", ", g.slots)}]");
            }
            if (dryRun) sb.AppendLine("· 干跑不写盘：重跑同参数结果逐位一致（规划全部由 seed 哈希决定）");
        }

        static int CountSlots(List<Group> groups)
        {
            int n = 0;
            foreach (var g in groups) n += g.slots.Count;
            return n;
        }

        // ==================================================================== 清单落盘

        [Serializable] public class OutDoc
        {
            public string tool = "DecalPlates";
            public string scene, faces, gates, generatedUtc, floorRule;
            public int eligibleFloorFaces, eligibleWallFaces;
            public float eligibleFloorArea, eligibleWallArea;
            public int gridPoints, candidatePoints, hitPoints, doorExcluded, spacingExcluded;
            public int maxDecals, passes, budget, maxDecalsUsed, totalTris;
            public bool cappedByMax, cappedByBudget;
            public float gridStep, edgeMargin, plateMin, plateMax, plateThick, plateBevel, surfaceOffset, yawJitterDeg;
            public float doorClear, minSpacing;
            public List<OutSet> sets = new List<OutSet>();
            public List<OutGroup> groups = new List<OutGroup>();
            public List<OutPlate> plates = new List<OutPlate>();
        }

        [Serializable] public class OutSet { public string set; public int count; public string material; }

        [Serializable] public class OutGroup
        {
            public string group, mesh, block, category;
            public int plates, tris;
            public List<string> slots = new List<string>();
        }

        [Serializable] public class OutPlate
        {
            public string face, block, category, set;
            public float[] pos, normal, size;
            public float yawDeg;
        }

        static void WriteOut(Plan plan, List<Group> groups, Dictionary<string, Material> mats)
        {
            var doc = new OutDoc
            {
                scene = P_Scene,
                faces = P_Faces,
                gates = plan.gateSource,
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                floorRule = plan.floorRule,
                eligibleFloorFaces = plan.floors.Count,
                eligibleWallFaces = plan.walls.Count,
                eligibleFloorArea = plan.floorArea,
                eligibleWallArea = plan.wallArea,
                gridPoints = plan.gridPoints,
                candidatePoints = plan.candPoints,
                hitPoints = plan.hitPoints,
                doorExcluded = plan.doorOut,
                spacingExcluded = plan.spacingOut,
                maxDecals = P_MaxDecals,
                passes = plan.passes,
                budget = (int)P_Budget,
                maxDecalsUsed = plan.plates.Count,
                totalTris = plan.plates.Count * TrisPerPlate,
                cappedByMax = plan.cappedByMax,
                cappedByBudget = plan.cappedByBudget,
                gridStep = GridStep, edgeMargin = EdgeMargin,
                plateMin = PlateMin, plateMax = PlateMax, plateThick = PlateThick, plateBevel = PlateBevel,
                surfaceOffset = SurfaceOffset, yawJitterDeg = YawJitterDeg,
                doorClear = DoorClear, minSpacing = MinSpacing,
            };

            foreach (var set in AllSets)
                doc.sets.Add(new OutSet
                {
                    set = set,
                    count = plan.setUse.TryGetValue(set, out var c) ? c : 0,
                    material = mats.ContainsKey(set) ? $"{MatDir}/{MatPrefix}{set}.mat" : "",
                });

            foreach (var g in groups)
                doc.groups.Add(new OutGroup
                {
                    group = g.name,
                    mesh = $"{MeshDir}/{g.name}.asset",
                    block = g.plates.Count > 0 ? g.plates[0].face.block : "",
                    category = g.name.EndsWith("_Floor", StringComparison.Ordinal) ? "Floor" : "Wall",
                    plates = g.plates.Count,
                    tris = g.plates.Count * TrisPerPlate,
                    slots = new List<string>(g.slots),
                });

            foreach (var p in plan.plates)
                doc.plates.Add(new OutPlate
                {
                    face = p.face.id,
                    block = p.face.block,
                    category = p.face.isFloor ? "Floor" : "Wall",
                    set = p.setId,
                    pos = new[] { p.pos.x, p.pos.y, p.pos.z },
                    normal = new[] { p.N.x, p.N.y, p.N.z },
                    size = new[] { p.w, p.h },
                    yawDeg = p.yawDeg,
                });

            try
            {
                var dir = Path.GetDirectoryName(P_Out);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(P_Out, JsonUtility.ToJson(doc, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[贴纸] 清单落盘失败：{e.Message}");
            }
        }

        // ==================================================================== 场景 / 生成物管理

        /// <summary>确保打开的是 -scene 指定的场景（batchmode 下 Unity 默认开空场景，不先打开就会建错地方）。</summary>
        static bool EnsureScene(bool forWrite)
        {
            if (!File.Exists(AssetPathToAbs(P_Scene)))
            {
                Debug.LogError($"[贴纸] 场景不存在：{P_Scene}");
                return false;
            }
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == P_Scene) return true;

            if (cur.IsValid() && cur.isDirty && !string.IsNullOrEmpty(cur.path))
            {
                if (forWrite) EditorSceneManager.SaveScene(cur);
                else
                {
                    Debug.LogError($"[贴纸] 当前场景 {cur.path} 有未保存改动；干跑要求零写盘，已中止。" +
                                   "请先保存并打开目标场景。");
                    return false;
                }
            }
            EditorSceneManager.OpenScene(P_Scene, OpenSceneMode.Single);
            Debug.Log($"[贴纸] 已打开场景 {P_Scene}");
            return true;
        }

        static void SaveScene()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid()) return;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        static string AssetPathToAbs(string assetPath) =>
            Path.Combine(Directory.GetCurrentDirectory(), assetPath).Replace('\\', '/');

        /// <summary>删场景里名叫 MAPSKIN_Decal 的对象（只认这个名字，绝不碰别的）。</summary>
        static int RemoveRoot()
        {
            var scene = EditorSceneManager.GetActiveScene();
            var doomed = new List<GameObject>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                if (t == null) continue;
                if (!t.gameObject.scene.IsValid() || t.gameObject.scene != scene) continue;
                if (t.name != RootName) continue;
                doomed.Add(t.gameObject);
            }
            foreach (var go in doomed) UnityEngine.Object.DestroyImmediate(go);
            return doomed.Count;
        }

        /// <summary>删 MeshDir 下本工具生成的 mesh 资产（只删该目录直属文件，不递归）。</summary>
        static int ClearMeshAssets()
        {
            if (!AssetDatabase.IsValidFolder(MeshDir)) return 0;
            var paths = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Mesh", new[] { MeshDir }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(p)) continue;
                if (!p.StartsWith(MeshDir + "/", StringComparison.Ordinal)) continue;
                if (p.Substring(MeshDir.Length + 1).Contains("/")) continue;
                paths.Add(p);
            }
            int n = 0;
            foreach (var p in paths) if (AssetDatabase.DeleteAsset(p)) n++;
            return n;
        }

        /// <summary>删 MatDir 下本工具的材质（只删 M_Decal_ 前缀的直属文件）。</summary>
        static int ClearMaterials()
        {
            if (!AssetDatabase.IsValidFolder(MatDir)) return 0;
            var paths = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { MatDir }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(p)) continue;
                if (!p.StartsWith(MatDir + "/", StringComparison.Ordinal)) continue;
                var file = Path.GetFileNameWithoutExtension(p);
                if (!file.StartsWith(MatPrefix, StringComparison.Ordinal)) continue;
                paths.Add(p);
            }
            int n = 0;
            foreach (var p in paths) if (AssetDatabase.DeleteAsset(p)) n++;
            return n;
        }

        /// <summary>递归建 AssetDatabase 文件夹（父级先建）。</summary>
        static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            int i = folder.LastIndexOf('/');
            var parent = i > 0 ? folder.Substring(0, i) : "Assets";
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, folder.Substring(i + 1));
        }

        // ==================================================================== 几何小工具

        /// <summary>点在多边形内（射线法，凹多边形也成立）。</summary>
        static bool InsidePoly(Vector2[] poly, float x, float y)
        {
            bool inside = false;
            int n = poly.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float xi = poly[i].x, yi = poly[i].y, xj = poly[j].x, yj = poly[j].y;
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }

        /// <summary>点到轮廓各边的最近距离（米）。</summary>
        static float DistToPolyEdge(Vector2[] poly, float x, float y)
        {
            float best = float.MaxValue;
            int n = poly.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float x1 = poly[j].x, y1 = poly[j].y, x2 = poly[i].x, y2 = poly[i].y;
                float dx = x2 - x1, dy = y2 - y1;
                float len2 = dx * dx + dy * dy;
                float t = len2 < 1e-12f ? 0f : Mathf.Clamp01(((x - x1) * dx + (y - y1) * dy) / len2);
                float px = x1 + t * dx, py = y1 + t * dy;
                float d = Mathf.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                if (d < best) best = d;
            }
            return best;
        }

        static bool TooClose(List<Plate> placed, Vector3 p)
        {
            float lim = MinSpacing * MinSpacing;
            for (int i = 0; i < placed.Count; i++)
                if ((placed[i].pos - p).sqrMagnitude < lim) return true;
            return false;
        }

        static bool NearPoint(List<Vector3> pts, Vector3 p, float radius)
        {
            float lim = radius * radius;
            for (int i = 0; i < pts.Count; i++)
                if ((pts[i] - p).sqrMagnitude < lim) return true;
            return false;
        }

        // ==================================================================== 哈希（确定性随机）

        /// <summary>FNV-1a 32 位：同一字符串每次跑都给同一串随机数（幂等的根）。</summary>
        static uint Hash32(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < s.Length; i++) h = (h ^ (uint)s[i]) * 16777619u;
                return h;
            }
        }

        /// <summary>[0,1) 均匀分布。</summary>
        static float Hash01(string s) => Hash32(s) / 4294967296f;

        // ==================================================================== 套表

        static string[] BuildAllSets()
        {
            var list = new List<string>(Letters.Length * Variants.Length);
            foreach (var l in Letters)
                foreach (var v in Variants)
                    list.Add($"T_HS_Line_{l}_{v}");
            return list.ToArray();
        }

        static string[] BuildPool(bool floorOnly)
        {
            var list = new List<string>();
            foreach (var l in Letters)
                foreach (var v in Variants)
                {
                    bool isFloor = v.EndsWith("_Floor", StringComparison.Ordinal);
                    if (isFloor == floorOnly) list.Add($"T_HS_Line_{l}_{v}");
                }
            return list.ToArray();
        }

        // ==================================================================== 参数解析

        static void ParseArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
            {
                switch (a[i])
                {
                    case "-scene": P_Scene = a[i + 1]; break;
                    case "-faces": P_Faces = a[i + 1]; break;
                    case "-out": P_Out = a[i + 1]; break;
                    case "-gates": P_Gates = a[i + 1]; break;
                    case "-maxdecals": P_MaxDecals = In(a[i + 1], P_MaxDecals); break;
                    case "-budget": P_Budget = In(a[i + 1], (int)P_Budget); break;
                    case "-floorpct": P_FloorPct = Fl(a[i + 1], P_FloorPct); break;
                    case "-wallpct": P_WallPct = Fl(a[i + 1], P_WallPct); break;
                    case "-onlyfloors": P_OnlyFloors = In(a[i + 1], P_OnlyFloors); break;
                    case "-perface": P_PerFace = In(a[i + 1], P_PerFace); break;
                }
            }
            if (P_MaxDecals < 0) P_MaxDecals = 0;
            if (P_Budget < 0) P_Budget = 0;
        }

        static float Fl(string s, float d) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;

        static int In(string s, int d) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;
    }
}
