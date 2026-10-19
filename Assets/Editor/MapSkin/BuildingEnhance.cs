using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// Level1 灰盒 → 真实建筑的「几何增补」层。
    ///
    /// 铁律（用户口径）：**只新增 `MAPSKIN_*`，绝不改/删/移/改名任何原有对象**。材质换装与 UV 重投影
    /// 已由 `MapSkinApply` / `MapSkinUvBake` 做完，本工具**复用**它们的口径：新件的 UV 直接按
    /// "世界坐标 / 基准尺度"写进 UV0（墙 3.6m / 地 4.8m，与 MapSkinUvBake 基准一致），
    /// 程序化件的材质一律复用相邻件/被支撑件的 `sharedMaterial`（零新材质）；唯一新建的材质是
    /// 玻璃幕墙的 `OurFunction/NPR/Transparent Toon` 实例（规范 §2.2 —— 不透明母材质套玻璃会变白墙）。
    ///
    /// 四趟（优先级即块序）：
    ///   ① 结构交接检测 + 弧角补齐：同区块内相邻（AABB 逐轴间隙 ≤ gap）且长轴夹角 ∈ [90°,165°] 的两段墙，
    ///      在交接处补一件弧形过渡墙。**优先实例化 Blender 建筑细节件包的 `SM_ArcCorner_*`**
    ///      （`Assets/Art/Environment/MapSwap/Models/SM_HST_ArchTrim_v1.fbx`，A{90,120,135,150}×R{1,1p5,2}×H{3,6}），
    ///      缺档 / 未导入 / 关开关时用程序化弧面兜底（弧面每 ≤10° 一段、厚度取两墙较小值、高度取较小值、
    ///      两端 45° 内收）。命名 `MAPSKIN_Arc_<序号>`。
    ///   ② 玻璃幕墙围合：全部区块合并包围盒外扩 40m 的四个竖直外边界，竖挺每 6m、横梁每 4m、
    ///      玻璃面内缩 10mm；高度覆盖最高点 +8m；按橙/黄/洋红语义标记（进入点/离开点/滑铲标志）留进出口开口。
    ///   ③ 线脚与交接件：墙底踢脚线（厚 30mm、高 120mm）、墙顶顶角线（高 300mm、出挑 90mm）、
    ///      大面墙结构缝条（每 4.8m 一道、宽 60mm）。
    ///   ④ 合理性兜底：悬空小件（底面离下方支撑 >0.5m）补支撑柱，截面按支承件 30%。
    ///
    /// 幂等：名字由「内容 + 按区块/坐标排序定的序号」决定 → 重跑同名直接跳过；参数可用 `-be-*` 覆盖。
    /// 护栏：单次 ≤2000 对象、≤300 万三角，超限即停止生成并打印实际用量。
    ///
    /// 生成物挂在按区块分组的容器里：`MAPSKIN_Details/S_0x/<Arc|Glazing|Trim|Seam|Support>`；
    /// 容器与程序化件 transform 全 identity（网格顶点即世界坐标），故父链上不许有非单位变换。
    ///
    /// 菜单：MapSkin/建筑/…
    /// 批处理（均为 public static void、无参）：
    ///   -executeMethod MapSkin.BuildingEnhance.DryRun    只报数：零写盘、零新建场景对象
    ///   -executeMethod MapSkin.BuildingEnhance.Run       四趟全生成并写盘
    ///   -executeMethod MapSkin.BuildingEnhance.ClearAll  删除全部 MAPSKIN_Details
    /// 参数：-be-&lt;键&gt; &lt;值&gt;（键清单见 ParseArgs 的 switch）
    /// </summary>
    public static class BuildingEnhance
    {
        // ------------------------------------------------------------------ 常量

        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string RootName = "MAPSKIN_Details";
        const string MeshDir = "Assets/Art/EnvSkins/Building";
        const string SwapDir = "Assets/Art/Environment/MapSwap";
        const string ArchTrimModelHint = "SM_HST_ArchTrim";      // Blender 建筑细节件包（FBX 模型名）
        const string ArcCornerStem = "SM_ArcCorner_";

        const string GlazingShaderName = "OurFunction/NPR/Transparent Toon";
        const string GlazingMatPath = "Assets/Art/Materials/MapSkin/Glazing/MAPSKIN_Glazing.mat";
        const string DefaultFrameMatPath = "Assets/Art/Models/MikuGandam/materials/Material.014.mat";

        /// <summary>UV 基准尺度，必须与 MapSkinUvBake（墙/柱 3.6m、地 4.8m）一致，否则新件与旧件板缝对不齐。</summary>
        const float WallUvBase = 3.6f;
        const float FloorUvBase = 4.8f;

        /// <summary>结构件判定：名字里带这些词（与场景命名口径一致：S_02_Wall_001 / L1_Beam_16x1x1_20cfcc）。</summary>
        static readonly Regex StructNameRe = new Regex("wall|beam|block|pillar", RegexOptions.IgnoreCase);
        /// <summary>区块判定：名字里出现 S_01…S_07（BOX_S_02 容器 / S_02_Wall_001 自身都命中）。</summary>
        static readonly Regex BlockNameRe = new Regex(@"S_0([1-7])(?!\d)");

        /// <summary>进出口开口标记的语义材质（地图灰盒语义色.md：橙=进入点、黄=离开点、洋红=滑铲标志）。</summary>
        static readonly string[] AccentMats = { "Material.038", "Material.061", "Material.063" };

        /// <summary>细节件包的弧角档位。</summary>
        static readonly float[] ArcPrefabAngles = { 90f, 120f, 135f, 150f };
        static readonly float[] ArcPrefabRadii = { 1f, 1.5f, 2f };
        static readonly float[] ArcPrefabHeights = { 3f, 6f };

        const string CatArc = "Arc";
        const string CatGlazing = "Glazing";
        const string CatTrim = "Trim";
        const string CatSeam = "Seam";
        const string CatSupport = "Support";

        // ------------------------------------------------------------------ 参数（-be-* 可覆盖）

        // ① 交接判据 / 弧角
        static float P_Gap = 0.4f;             // -be-gap       相邻判定：两件 AABB 逐轴最大间隙（m）
        static float P_ArcMinDeg = 90f;        // -be-arcmin    夹角下界（度）
        static float P_ArcMaxDeg = 165f;       // -be-arcmax    夹角上界（度）
        static float P_ArcRadiusScale = 1.6f;  // -be-arck      弧半径 = k × 两墙较小厚度
        static float P_ArcRadiusMin = 1.0f;    // -be-arcrmin
        static float P_ArcRadiusMax = 5.0f;    // -be-arcrmax
        static float P_ArcSegDeg = 10f;        // -be-arcseg    程序化弧面分段上限（度/段）
        static float P_ArcMiter = 45f;         // -be-arcmiter  两端内收角（度；45 = 内收一个壁厚）
        const float P_WallUvBase = 3.6f;       // 弧角件重投影 UV 基准（与 MapSkinUvBake 墙基准一致）
        static float P_MinWallH = 1.5f;        // -be-minh      低于此高度不算墙状件
        static float P_MinLenRatio = 1.3f;     // -be-minlen    长/厚 比低于此值视为近方柱，不参与交接
        static int P_UseArcPrefab = 1;         // -be-arcpref   1 = 优先细节件包 SM_ArcCorner_*
        static int P_ArcPrefabScaleY = 1;      // -be-arcprefsy 1 = 预制体只缩 Y 对齐墙高（不缩 XZ，保住平面弧）

        // ② 幕墙
        static float P_Expand = 40f;           // -be-expand    合并包围盒外扩（m）
        static float P_Mullion = 6f;           // -be-mullion   竖挺间距（m）
        static float P_Transom = 4f;           // -be-transom   横梁间距（= 层高 m）
        static float P_TopExtra = 8f;          // -be-topxtra   幕墙超出最高点（m）
        static float P_GlassInset = 0.01f;     // -be-ginset    玻璃面相对框体内缩（m）
        static float P_MullionW = 0.12f;       // -be-mullw     竖挺宽（切向，m）
        static float P_MullionD = 0.20f;       // -be-mulld     竖挺厚（法向，m）
        static float P_TransomH = 0.10f;       // -be-tranh     横梁高（竖向，m）
        static float P_TransomD = 0.16f;       // -be-trand     横梁厚（法向，m）
        static float P_GlassAlpha = 0.30f;     // -be-galpha    玻璃 alpha（规范区间 0.25–0.35）
        static float P_GlassRough = 0.08f;     // -be-grough    玻璃 roughness（落成 _Smoothness = 1 - rough）
        static Color P_GlassColor = new Color(0.55f, 0.68f, 0.72f);   // -be-gcolor "r,g,b" 浅青
        static float P_OpenW = 12f;            // -be-openw     开口宽（切向，m；= 2 个开间）
        static float P_OpenH = 8f;             // -be-openh     开口高（m；= 2 层）

        // ③ 线脚
        static float P_TrimMinH = 2.5f;        // -be-trimminh  参与线脚的墙最低高度
        static float P_PlinthH = 0.12f;        // -be-plinthh   踢脚线高
        static float P_PlinthT = 0.03f;        // -be-plintht   踢脚线厚（外凸）
        static float P_CorniceH = 0.30f;       // -be-corniceh  顶角线高
        static float P_CorniceO = 0.09f;       // -be-corniceo  顶角线出挑
        static float P_SeamStep = 4.8f;        // -be-seamstep  结构缝间距
        static float P_SeamW = 0.06f;          // -be-seamw     缝条宽
        static float P_SeamD = 0.015f;         // -be-seamd     缝条深（灰盒只做加法：以 15mm 凸肋表达）
        static float P_SeamMinLen = 9.6f;      // -be-seamminlen 大面墙阈值（< 此长不出缝）

        // ④ 支撑
        static float P_SupportGap = 0.5f;      // -be-supgap    底面离支撑超过此值判悬空
        static float P_SupportMaxPiece = 8f;   // -be-supmax    只给"小件"补撑（最大边 ≤ 此值）
        static float P_SupportThickMax = 3f;   // -be-supthick  细长件判定：厚度 ≤ 此值也参与支撑检测（灰盒横梁多为 1–2m 厚）
        static float P_SupportRatio = 0.30f;   // -be-supk      支撑截面 = 支承件 footprint × 此比例

        // 护栏
        static int P_MaxObjects = 2000;        // -be-maxobjs
        static long P_MaxTris = 3000000;       // -be-maxtris
        static string P_FrameMatPath = DefaultFrameMatPath;   // -be-framemat

        // ------------------------------------------------------------------ 菜单 + 入口

        [MenuItem("MapSkin/建筑/① 干跑：结构交接检测(只报数)")]
        public static void DryRunMenu() => DryRun();

        [MenuItem("MapSkin/建筑/② 生成：弧角补齐")]
        public static void RunArcMenu() => RunCore(new[] { CatArc }, true);

        [MenuItem("MapSkin/建筑/③ 生成：玻璃幕墙")]
        public static void RunGlazingMenu() => RunCore(new[] { CatGlazing }, true);

        [MenuItem("MapSkin/建筑/④ 生成：线脚与交接件")]
        public static void RunTrimMenu() => RunCore(new[] { CatTrim, CatSeam }, true);

        [MenuItem("MapSkin/建筑/⑤ 生成：合理性兜底(支撑)")]
        public static void RunSupportMenu() => RunCore(new[] { CatSupport }, true);

        [MenuItem("MapSkin/建筑/⑥ 一键：生成全部(弧角→幕墙→线脚→支撑)")]
        public static void RunMenu() => RunCore(null, true);

        [MenuItem("MapSkin/建筑/清除全部 MAPSKIN_Details")]
        public static void ClearAllMenu()
        {
            if (!EditorUtility.DisplayDialog("清除 MAPSKIN_Details",
                    "删除场景内全部 MAPSKIN_* 生成件（含 MAPSKIN_Details 容器）与生成网格资产？\n" +
                    "原有对象不受影响。", "删除", "取消"))
                return;
            ClearAll();
        }

        /// <summary>干跑：四趟全算一遍（内存里建网格只为数三角形），**零写盘、零新建场景对象**。
        /// 批处理：-executeMethod MapSkin.BuildingEnhance.DryRun</summary>
        public static void DryRun()
        {
            ParseArgs();
            Sanitize();
            EnsureScene();
            var rep = new StringBuilder();
            var ctx = Scan(rep);
            var bud = new Budget { MaxObjects = P_MaxObjects, MaxTris = P_MaxTris };
            var pieces = PlanAll(ctx, rep, bud);
            rep.AppendLine();
            rep.AppendLine(bud.Describe());
            rep.AppendLine("[建筑·干跑] 未写盘、未新建场景对象。参数可用 -be-* 覆盖（见 BuildingEnhance.ParseArgs）。");
            Debug.Log($"[建筑·干跑] 计划生成 {pieces.Count} 件（对象上限 {P_MaxObjects}、三角上限 {P_MaxTris:N0}）\n{rep}");
        }

        /// <summary>生成全部四趟（无对话框，批处理入口）。
        /// 批处理：-executeMethod MapSkin.BuildingEnhance.Run</summary>
        public static void Run() => RunCore(null, false);

        /// <summary>跑指定类别（null = 四趟全跑）；showDialog 只给编辑器菜单用，批处理传 false。</summary>
        public static void RunCore(string[] cats, bool showDialog)
        {
            ParseArgs();
            Sanitize();
            EnsureScene();
            var rep = new StringBuilder();
            var ctx = Scan(rep);
            var bud = new Budget { MaxObjects = P_MaxObjects, MaxTris = P_MaxTris };
            var pieces = PlanAll(ctx, rep, bud);
            if (cats != null) pieces = pieces.Where(p => cats.Contains(p.category)).ToList();

            if (showDialog)
            {
                long tris = pieces.Sum(p => (long)p.tris);
                if (!EditorUtility.DisplayDialog("建筑几何增补",
                        $"将生成 {pieces.Count} 件（共 {tris:N0} 三角形）：\n" +
                        string.Join("\n", pieces.GroupBy(p => p.category)
                            .Select(g => $"  {g.Key}: {g.Count()} 件 / {g.Sum(p => (long)p.tris):N0} 三角")) +
                        $"\n\n护栏：≤{P_MaxObjects} 对象、≤{P_MaxTris:N0} 三角\n继续？", "生成", "取消"))
                {
                    Debug.Log("[建筑] 用户取消");
                    return;
                }
            }

            int made = Execute(pieces, ctx, rep);
            AssetDatabase.SaveAssets();

            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scene);
                if (scene.path == ScenePath) EditorSceneManager.SaveScene(scene);
            }
            rep.AppendLine();
            rep.AppendLine(bud.Describe());
            rep.AppendLine($"[建筑] 实际新建 {made} 件 → {RootName}/<区块>/<类别>；场景已存盘：{ScenePath}");
            Debug.Log($"[建筑] 完成：新建 {made} 件\n{rep}");
        }

        /// <summary>清除全部生成物（对象 + 生成网格资产）。原对象不动。
        /// 批处理：-executeMethod MapSkin.BuildingEnhance.ClearAll</summary>
        public static void ClearAll()
        {
            ParseArgs();
            EnsureScene();
            var targets = new List<GameObject>();
            foreach (var t in Object.FindObjectsOfType<Transform>())
            {
                if (t == null) continue;
                if (t.parent == null && t.name == RootName) targets.Add(t.gameObject);
            }
            foreach (var t in Object.FindObjectsOfType<Transform>())
            {
                if (t == null) continue;
                if (t.name.StartsWith("MAPSKIN_") && !targets.Contains(t.gameObject)) targets.Add(t.gameObject);
            }
            var roots = new List<GameObject>();
            foreach (var g in targets)
            {
                if (g == null) continue;
                bool nested = false;
                for (var t = g.transform.parent; t != null; t = t.parent)
                    if (t.name.StartsWith("MAPSKIN_")) { nested = true; break; }
                if (!nested) roots.Add(g);
            }
            int n = 0;
            foreach (var go in roots)
            {
                if (go == null) continue;
                n += go.GetComponentsInChildren<Transform>(true).Length;
                Object.DestroyImmediate(go);
            }
            if (AssetDatabase.IsValidFolder(MeshDir)) AssetDatabase.DeleteAsset(MeshDir);
            AssetDatabase.SaveAssets();

            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scene);
                if (scene.path == ScenePath) EditorSceneManager.SaveScene(scene);
            }
            Debug.Log($"[建筑·清除] 删除 MAPSKIN 生成件 {n} 个对象节点（{roots.Count} 个根）、生成网格目录 {MeshDir}；原有对象未动");
        }

        // ------------------------------------------------------------------ 场景扫描

        class Box
        {
            public GameObject go;
            public string name;
            public string block;
            public Material mat;
            public Vector3 c;       // AABB 中心（世界）
            public Vector3 size;    // AABB 尺寸（世界）
            public Vector3 uLong;   // 水平长轴（轴对齐单位向量）
            public Vector3 uShort;  // 水平短轴（厚度方向）
            public bool wallLike;
            public float Y0 => c.y - size.y * 0.5f;
            public float Y1 => c.y + size.y * 0.5f;
            public float Thick => Mathf.Min(size.x, size.z);
            public float Len => Mathf.Max(size.x, size.z);
            public float H => size.y;
        }

        class Ctx
        {
            public readonly List<Box> all = new List<Box>();     // 全部带网格的渲染件（含地板/道具）：只用于"下方支撑"查询
            public readonly List<Box> walls = new List<Box>();   // 名字命中结构词且墙状
            public Bounds merged;
            public bool hasMerged;
            public GameObject root;                              // MAPSKIN_Details（惰性创建/查找）
            public readonly HashSet<string> existing = new HashSet<string>();
            public readonly Dictionary<string, Bounds> blockBounds = new Dictionary<string, Bounds>();
            public ArcCatalog catalog;
        }

        static void EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return;
            if (cur.IsValid() && cur.isDirty)
            {
                Debug.LogWarning("[建筑] 当前场景有未保存改动，不替你切场景；请在 Level1 打开时跑");
                return;
            }
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        static Ctx Scan(StringBuilder rep)
        {
            var ctx = new Ctx();

            foreach (var t in Object.FindObjectsOfType<Transform>())
                if (t != null && t.name.StartsWith("MAPSKIN_")) ctx.existing.Add(t.name);

            foreach (var mf in Object.FindObjectsOfType<MeshFilter>())
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var r = mf.GetComponent<Renderer>();
                if (r == null || !r.enabled) continue;
                var go = mf.gameObject;
                if (go == null) continue;
                // 本工具自己的生成件不参与扫描：否则重跑时幕墙会把合并包围盒越撑越大（幂等破坏）
                if (go.name.StartsWith("MAPSKIN_")) continue;
                // 分关装饰件也不参与：DEC_* 里有大量 WallPanel/Wall/Pillar 命名的装饰盒，
                // 会把"墙角"数量从 ~160 撑到 ~455，弧角件随即铺满整个房间。
                if (go.name.StartsWith("DEC_") || go.name.StartsWith("BEVEL_") || go.name.StartsWith("UV_")) continue;
                var b = r.bounds;
                if (b.size.sqrMagnitude < 1e-8f) continue;

                var box = new Box
                {
                    go = go, name = go.name, mat = r.sharedMaterial, c = b.center, size = b.size,
                };
                if (box.size.x >= box.size.z) { box.uLong = Vector3.right; box.uShort = Vector3.forward; }
                else { box.uLong = Vector3.forward; box.uShort = Vector3.right; }

                box.wallLike = StructNameRe.IsMatch(box.name)
                               && box.H >= P_MinWallH
                               && box.Thick >= 0.05f
                               && box.Len >= P_MinLenRatio * Mathf.Max(box.Thick, 1e-3f);

                if (!ctx.hasMerged) { ctx.merged = b; ctx.hasMerged = true; }
                else ctx.merged.Encapsulate(b);

                ctx.all.Add(box);
                if (box.wallLike) ctx.walls.Add(box);
            }

            foreach (var t in Object.FindObjectsOfType<Transform>())
            {
                if (t == null || t.parent != null) continue;
                var key = BlockKeyOfName(t.name);
                if (key == null) continue;
                var bb = new Bounds();
                bool any = false;
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !r.enabled) continue;
                    if (!any) { bb = r.bounds; any = true; } else bb.Encapsulate(r.bounds);
                }
                if (any) ctx.blockBounds[key] = bb;
            }

            foreach (var b in ctx.walls) b.block = BlockOf(ctx, b.go, b.c);

            rep.AppendLine($"[扫描] 渲染件 {ctx.all.Count} 个（墙状结构件 {ctx.walls.Count} 个）；" +
                           $"区块 {ctx.blockBounds.Count} 个；已存在生成件名 {ctx.existing.Count} 个");
            if (ctx.hasMerged)
                rep.AppendLine($"[扫描] 全图合并包围盒 center=({ctx.merged.center.x:F1},{ctx.merged.center.y:F1},{ctx.merged.center.z:F1})" +
                               $" size=({ctx.merged.size.x:F1},{ctx.merged.size.y:F1},{ctx.merged.size.z:F1})");
            return ctx;
        }

        static string BlockKeyOfName(string name)
        {
            var m = BlockNameRe.Match(name ?? "");
            return m.Success ? "S_0" + m.Groups[1].Value : null;
        }

        /// <summary>件属于哪个区块：先沿父链匹配 S_0x（BOX_S_02 容器 / S_02_Wall_001 自身都命中），
        /// 找不到就就近归到某个区块盒子，再不行归 S_00。</summary>
        static string BlockOf(Ctx ctx, GameObject go, Vector3 pos)
        {
            for (var t = go != null ? go.transform : null; t != null; t = t.parent)
            {
                var k = BlockKeyOfName(t.name);
                if (k != null) return k;
            }
            string best = null;
            float bestD = float.MaxValue;
            foreach (var kv in ctx.blockBounds)
            {
                float d = (kv.Value.ClosestPoint(pos) - pos).sqrMagnitude;
                if (d < bestD) { bestD = d; best = kv.Key; }
            }
            return best ?? "S_00";
        }

        // ------------------------------------------------------------------ 计划（四趟）

        static List<Piece> PlanAll(Ctx ctx, StringBuilder rep, Budget bud)
        {
            var list = new List<Piece>();
            list.AddRange(PlanArcs(ctx, rep, bud));
            list.AddRange(PlanGlazing(ctx, rep, bud));
            list.AddRange(PlanTrim(ctx, rep, bud));
            list.AddRange(PlanSupports(ctx, rep, bud));
            return list;
        }

        // ================================================================ ① 弧角补齐

        class Junction
        {
            public Box A, B;
            public Vector3 I;          // 两墙中轴线交点（XZ）
            public float angleDeg;
            public string sortKey;
        }

        static List<Piece> PlanArcs(Ctx ctx, StringBuilder rep, Budget bud)
        {
            var walls = ctx.walls;
            var found = new List<Junction>();
            var seen = new HashSet<string>();
            var hist = new SortedDictionary<int, int>();
            int pairs = 0, adj = 0, angleOk = 0, rejected = 0;

            for (int i = 0; i < walls.Count; i++)
                for (int j = i + 1; j < walls.Count; j++)
                {
                    var A = walls[i];
                    var B = walls[j];
                    pairs++;
                    if (A.block != B.block) continue;
                    if (Gap(A, B) > P_Gap) continue;
                    float vOverlap = Mathf.Min(A.Y1, B.Y1) - Mathf.Max(A.Y0, B.Y0);
                    if (vOverlap < 0.4f * Mathf.Min(A.H, B.H)) continue;
                    adj++;

                    float dot = Mathf.Abs(Vector3.Dot(A.uLong, B.uLong));
                    float ang = Mathf.Acos(Mathf.Clamp(dot, 0f, 1f)) * Mathf.Rad2Deg;
                    if (ang < P_ArcMinDeg || ang > P_ArcMaxDeg) continue;
                    angleOk++;
                    int bin = Mathf.FloorToInt(ang / 10f) * 10;
                    int have;
                    if (hist.TryGetValue(bin, out have)) hist[bin] = have + 1; else hist[bin] = 1;

                    // 两中轴线（XZ）求交：P + s·u = Q + r·v → s = (d × v) / (u × v)
                    var d2 = new Vector2(B.c.x - A.c.x, B.c.z - A.c.z);
                    var u2 = new Vector2(A.uLong.x, A.uLong.z);
                    var v2 = new Vector2(B.uLong.x, B.uLong.z);
                    float den = u2.x * v2.y - u2.y * v2.x;
                    if (Mathf.Abs(den) < 1e-3f) { rejected++; continue; }
                    float s = (d2.x * v2.y - d2.y * v2.x) / den;
                    var Ip = new Vector3(A.c.x + u2.x * s, 0f, A.c.z + u2.y * s);

                    float dA = new Vector2(Ip.x - A.c.x, Ip.z - A.c.z).magnitude;
                    float dB = new Vector2(Ip.x - B.c.x, Ip.z - B.c.z).magnitude;
                    if (dA > A.Len * 0.5f + A.Thick + P_Gap + 1f ||
                        dB > B.Len * 0.5f + B.Thick + P_Gap + 1f) { rejected++; continue; }

                    // 交点还必须贴近两墙的**真实矩形**：两组 AABB 相交很容易把"平行擦肩"或
                    // "延长线相交"误判成墙角，弧角件就会飘在房间正中（实测 S_01 出现空中黑弧）。
                    float tol = 1.0f + 0.75f * Mathf.Min(A.Thick, B.Thick);
                    if (XzRectDistance(A, Ip) > tol || XzRectDistance(B, Ip) > tol) { rejected++; continue; }

                    string key = PairKey(A, B) + "|" + Mathf.RoundToInt(Ip.x) + "_" + Mathf.RoundToInt(Ip.z);
                    if (!seen.Add(key)) continue;
                    found.Add(new Junction { A = A, B = B, I = Ip, angleDeg = ang, sortKey = key });
                }

            found.Sort((x, y) =>
            {
                int c = string.CompareOrdinal(x.A.block, y.A.block);
                if (c != 0) return c;
                c = x.I.x.CompareTo(y.I.x);
                if (c != 0) return c;
                c = x.I.z.CompareTo(y.I.z);
                if (c != 0) return c;
                return string.CompareOrdinal(x.sortKey, y.sortKey);
            });

            rep.AppendLine();
            rep.AppendLine($"[① 弧角] 候选对 {pairs} → 相邻 {adj} → 夹角达标 {angleOk} → 交点/去重后 {found.Count} 处（弃用 {rejected}）");
            rep.AppendLine($"        判据：同区块 + AABB 逐轴最大间隙 ≤ {P_Gap}m + 竖向重叠 ≥ 40% + 长轴夹角 ∈ [{P_ArcMinDeg}°,{P_ArcMaxDeg}°]");
            if (hist.Count > 0)
            {
                rep.Append("        角度分布：");
                foreach (var kv in hist) rep.Append($"{kv.Key}–{kv.Key + 10}°: {kv.Value}    ");
                rep.AppendLine();
            }

            ctx.catalog = ArcCatalog.Load(rep, P_UseArcPrefab != 0);

            var pieces = new List<Piece>();
            int n = 0, idx = 0, skipped = 0, byPrefab = 0, byProc = 0, angleWarn = 0;
            foreach (var jc in found)
            {
                idx++;
                var A = jc.A;
                var B = jc.B;
                float thick = Mathf.Min(A.Thick, B.Thick);
                float radius = Mathf.Clamp(P_ArcRadiusScale * thick, P_ArcRadiusMin, P_ArcRadiusMax);
                radius = Mathf.Min(radius, 0.45f * Mathf.Min(A.Len, B.Len));
                float y0 = Mathf.Max(A.Y0, B.Y0);
                float h = Mathf.Min(A.H, B.H);
                if (radius < 0.4f || h < 0.5f) { skipped++; continue; }

                var geo = ArcGeometry(A, B, jc, radius, thick);
                if (geo == null) { skipped++; continue; }

                string name = $"MAPSKIN_Arc_{idx:000}";
                // 弧角件必须跟随所在墙的语义族材质：细节件包 FBX 自带的是导入默认灰（NPRBase 变体），
                // 在 Deferred + GTAO 下会渲染成纯黑巨弧。这里优先取两墙中较亮的一档，
                // 让弧角读作"柱/包边"而不是黑挡板。
                var mat = PickLighterMaterial(A.mat, B.mat);
                var piece = TryArcPrefabPiece(ctx.catalog, geo, jc, name, A.block, h, ref angleWarn);
                if (piece == null)
                {
                    piece = new Piece
                    {
                        name = name, block = A.block, category = CatArc, mat = mat,
                        mesh = BuildArcMesh(geo, P_ArcSegDeg, P_ArcMiter), pos = geo.oCenter,
                    };
                    piece.tris = piece.mesh.triangles.Length / 3;
                    byProc++;
                }
                else byPrefab++;
                piece.mat = mat;   // 细节件包件也统一挂语义族材质（FBX 默认灰会变黑弧）

                if (!bud.Allow(piece.tris)) { skipped++; break; }
                pieces.Add(piece);
                n++;
            }

            rep.AppendLine($"        → 计划生成 {n} 件弧形过渡墙（细节件包预制体 {byPrefab} 件 / 程序化兜底 {byProc} 件；弃用 {skipped} 处）");
            if (angleWarn > 0)
                rep.AppendLine($"        [注意] {angleWarn} 处的预制体朝向残差 > 45°：FBX 里弧角件的原点/朝向约定与" +
                               "\"局部原点=抹角圆心(地面)、+y 朝上\"不符；用 -be-arcpref 0 改走程序化弧面即可规避");
            rep.AppendLine($"        程序化弧参数：半径 = clamp({P_ArcRadiusScale}×较小厚度, {P_ArcRadiusMin}, {P_ArcRadiusMax})，" +
                           $"厚度取两墙较小值、高度取较小值、两端 {P_ArcMiter}° 内收、弧面每 ≤{P_ArcSegDeg}° 一段");
            return pieces;
        }

        /// <summary>点到墙体 XZ 矩形（沿长轴/厚轴）的距离；在矩形内为 0。用于剔除"延长线相交"的假墙角。</summary>
        static float XzRectDistance(Box b, Vector3 p)
        {
            float dx = p.x - b.c.x, dz = p.z - b.c.z;
            float along = Mathf.Abs(dx * b.uLong.x + dz * b.uLong.z) - b.Len * 0.5f;
            float across = Mathf.Abs(-dx * b.uLong.z + dz * b.uLong.x) - b.Thick * 0.5f;
            float ox = Mathf.Max(0f, along), oy = Mathf.Max(0f, across);
            return Mathf.Sqrt(ox * ox + oy * oy);
        }

        /// <summary>两墙材质取较亮的一档（近线性亮度），避免弧角件继承黑族后变成纯黑挡板。</summary>
        static Material PickLighterMaterial(Material a, Material b)
        {
            a = BaseFamilyMaterial(a);
            b = BaseFamilyMaterial(b);
            // 六边形墙皮/贴片的 M_panel_*/M_dark_*/M_metal_* 是"深色底图 + 亮色描线"，
            // _BaseColor 看着亮但出图是黑块 —— 按材质家族直接排除。
            if (IsMapSwapPanel(a)) a = null;
            if (IsMapSwapPanel(b)) b = null;
            if (a == null && b == null) a = FrameMaterial();
            if (a == null) return b;
            if (b == null) return a;
            var pick = Luminance(a) >= Luminance(b) ? a : b;
            if (Luminance(pick) < 0.18f) return FrameMaterial() ?? pick;
            return pick;
        }

        static bool IsMapSwapPanel(Material m) =>
            m != null && (m.name.StartsWith("M_panel") || m.name.StartsWith("M_dark") || m.name.StartsWith("M_metal"));

        static Material FrameMaterial() =>
            AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Models/MikuGandam/materials/Material.014.mat");

        /// <summary>
        /// 弧角件不能挂刻线分派材质（带底纹的深色底图，远看就是一块黑）。
        /// 名字形如 `Material.014_LineWhite_Wall_A_Ortho` → 取回素面基底 `Material.014.mat`。
        /// </summary>
        static Material BaseFamilyMaterial(Material m)
        {
            if (m == null) return null;
            string n = m.name;
            int i = n.IndexOf("_Line", System.StringComparison.Ordinal);
            if (i > 0)
            {
                string stem = n.Substring(0, i);
                var baseMat = AssetDatabase.LoadAssetAtPath<Material>(
                    $"Assets/Art/Models/MikuGandam/materials/{stem}.mat");
                if (baseMat != null) return baseMat;
            }
            return m;
        }

        static float Luminance(Material m)
        {
            Color c = Color.gray;
            if (m.HasProperty("_BaseColor")) c = m.GetColor("_BaseColor");
            else if (m.HasProperty("_Color")) c = m.GetColor("_Color");
            return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
        }

        static string PairKey(Box a, Box b)
        {
            var ka = Key(a);
            var kb = Key(b);
            return string.CompareOrdinal(ka, kb) <= 0 ? ka + "~" + kb : kb + "~" + ka;
        }

        static string Key(Box b) =>
            $"{b.name}@{Mathf.RoundToInt(b.c.x)}_{Mathf.RoundToInt(b.c.y)}_{Mathf.RoundToInt(b.c.z)}";

        /// <summary>AABB 逐轴间隙取最大（切比雪夫）——各轴都贴近才算"相邻"。</summary>
        static float Gap(Box a, Box b)
        {
            float gx = Mathf.Max(0f, Mathf.Abs(a.c.x - b.c.x) - (a.size.x + b.size.x) * 0.5f);
            float gy = Mathf.Max(0f, Mathf.Abs(a.c.y - b.c.y) - (a.size.y + b.size.y) * 0.5f);
            float gz = Mathf.Max(0f, Mathf.Abs(a.c.z - b.c.z) - (a.size.z + b.size.z) * 0.5f);
            return Mathf.Max(gx, Mathf.Max(gy, gz));
        }

        class ArcGeo
        {
            public Vector3 oCenter;   // 弧心（Y = 弧底）
            public float radius;
            public float a0;
            public float sweep;
            public float thick;
            public float y0, h;
            public Vector3 dirToCorner;   // 弧心 → 交点（判定"件体该往哪一侧"）
        }

        /// <summary>弧角几何：弧放在两墙"内侧楔形"里（切于两墙中轴线）。两个候选弧心各采样一次，
        /// 取"弧体落进墙体内最少"的那个 —— 不靠人工判内外，靠几何自证。</summary>
        static ArcGeo ArcGeometry(Box A, Box B, Junction jc, float radius, float thick)
        {
            var I = jc.I;
            var wedge = new Vector3((A.c.x - I.x) + (B.c.x - I.x), 0f, (A.c.z - I.z) + (B.c.z - I.z));
            if (wedge.sqrMagnitude < 1e-4f) return null;
            wedge.Normalize();

            float half = Mathf.Max(jc.angleDeg * 0.5f, 1f) * Mathf.Deg2Rad;
            float k = radius / Mathf.Max(Mathf.Sin(half), 1e-3f);
            var c1 = I + wedge * k;
            var c2 = I - wedge * k;
            var o = InsideScore(A, B, c1, radius, thick) <= InsideScore(A, B, c2, radius, thick) ? c1 : c2;

            var ta = FootOnCenterLine(A, o);
            var tb = FootOnCenterLine(B, o);
            float a0 = Mathf.Atan2(ta.z - o.z, ta.x - o.x);
            float a1 = Mathf.Atan2(tb.z - o.z, tb.x - o.x);
            float sweep = Mathf.DeltaAngle(a0 * Mathf.Rad2Deg, a1 * Mathf.Rad2Deg) * Mathf.Deg2Rad;
            if (Mathf.Abs(sweep) < 0.05f) return null;

            var dirToCorner = new Vector3(I.x - o.x, 0f, I.z - o.z);
            if (dirToCorner.sqrMagnitude < 1e-6f) dirToCorner = -wedge; else dirToCorner.Normalize();

            return new ArcGeo
            {
                oCenter = new Vector3(o.x, Mathf.Max(A.Y0, B.Y0), o.z),
                radius = radius, a0 = a0, sweep = sweep, thick = thick,
                y0 = Mathf.Max(A.Y0, B.Y0), h = Mathf.Min(A.H, B.H), dirToCorner = dirToCorner,
            };
        }

        static Vector3 FootOnCenterLine(Box b, Vector3 p)
        {
            var dir = new Vector3(b.uLong.x, 0f, b.uLong.z);
            var rel = new Vector3(p.x - b.c.x, 0f, p.z - b.c.z);
            float s = Vector3.Dot(rel, dir);
            return new Vector3(b.c.x + dir.x * s, 0f, b.c.z + dir.z * s);
        }

        /// <summary>弧心候选的弧体采样点落进两墙 AABB 的个数（越少越说明这个弧心把弧摆在墙间/墙外）。</summary>
        static int InsideScore(Box A, Box B, Vector3 o, float radius, float thick)
        {
            var ta = FootOnCenterLine(A, o);
            var tb = FootOnCenterLine(B, o);
            float a0 = Mathf.Atan2(ta.z - o.z, ta.x - o.x);
            float a1 = Mathf.Atan2(tb.z - o.z, tb.x - o.x);
            float sweep = Mathf.DeltaAngle(a0 * Mathf.Rad2Deg, a1 * Mathf.Rad2Deg) * Mathf.Deg2Rad;
            float rr = radius + thick * 0.5f;
            float y = (A.Y0 + A.Y1 + B.Y0 + B.Y1) * 0.25f;
            int inside = 0;
            const int N = 12;
            for (int i = 0; i <= N; i++)
            {
                float a = a0 + sweep * (i / (float)N);
                var p = new Vector3(o.x + Mathf.Cos(a) * rr, y, o.z + Mathf.Sin(a) * rr);
                if (InAabb(A, p, -0.02f)) inside++;
                if (InAabb(B, p, -0.02f)) inside++;
            }
            return inside;
        }

        static bool InAabb(Box b, Vector3 p, float shrink)
        {
            return Mathf.Abs(p.x - b.c.x) <= b.size.x * 0.5f + shrink
                && Mathf.Abs(p.y - b.c.y) <= b.size.y * 0.5f + shrink
                && Mathf.Abs(p.z - b.c.z) <= b.size.z * 0.5f + shrink;
        }

        // ---------------------------------------------------------------- 细节件包（SM_ArcCorner_*）

        /// <summary>
        /// Blender「建筑细节件包」（`SM_HST_ArchTrim_v1.fbx`）的弧角件目录：按件名索引 FBX 子对象，
        /// 顺带把每件的三角形数与局部包围盒量出来（都在资产内存里算、不实例化 → 干跑也能报数）。
        /// 档名：`SM_ArcCorner_A{90,120,135,150}_R{1,1p5,2}_H{3,6}`（小数点写成 p）。
        /// </summary>
        class ArcCatalog
        {
            public bool enabled;
            public bool tried;
            public readonly List<GameObject> prefabAssets = new List<GameObject>();   // 真 .prefab（若有，最优先）
            public readonly Dictionary<string, GameObject> models = new Dictionary<string, GameObject>();

            public static ArcCatalog Load(StringBuilder rep, bool enable)
            {
                var cat = new ArcCatalog { enabled = enable, tried = true };
                if (!enable)
                {
                    rep.AppendLine("        [细节件包] -be-arcpref 0 → 强制程序化弧面（不查预制体）");
                    return cat;
                }
                if (!AssetDatabase.IsValidFolder(SwapDir))
                {
                    rep.AppendLine("        [细节件包] 目录不存在 → 程序化兜底");
                    return cat;
                }

                foreach (var guid in AssetDatabase.FindAssets(ArcCornerStem + " t:Prefab", new[] { SwapDir }))
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    var g = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                    if (g != null) cat.prefabAssets.Add(g);
                }
                if (cat.prefabAssets.Count > 0)
                    rep.AppendLine($"        [细节件包] 命中 {cat.prefabAssets.Count} 个 {ArcCornerStem}* .prefab → 优先实例化预制体");

                foreach (var guid in AssetDatabase.FindAssets(ArchTrimModelHint + " t:Model", new[] { SwapDir }))
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    var root = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                    if (root == null)
                    {
                        rep.AppendLine($"        [细节件包] {p} 还没被 Unity 导入（或导入失败）→ 程序化兜底");
                        continue;
                    }
                    int cnt = 0;
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == null || !t.name.StartsWith(ArcCornerStem)) continue;
                        var key = Normalize(t.name);
                        if (!cat.models.ContainsKey(key)) { cat.models[key] = t.gameObject; cnt++; }
                    }
                    rep.AppendLine($"        [细节件包] FBX {p} 已导入，索引到 {cnt} 件 {ArcCornerStem}*");
                }
                if (cat.prefabAssets.Count == 0 && cat.models.Count == 0)
                    rep.AppendLine("        [细节件包] 未发现可用弧角件（件包未到场 / 未导入）→ 程序化弧面兜底");
                return cat;
            }

            public static string Normalize(string s)
            {
                var sb = new StringBuilder();
                foreach (var c in s ?? "")
                {
                    if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                    else if (c == '_' || c == '.' || c == '-') sb.Append('_');
                }
                return sb.ToString().Trim('_');
            }

            /// <summary>找"档名 stem 前缀命中"的件（件名带材质后缀 _q6，故用前缀匹配；键更短的优先）。</summary>
            public bool TryFind(string stem, out GameObject go, out bool isAssetPrefab)
            {
                go = null;
                isAssetPrefab = false;
                string best = null;
                foreach (var kv in models)
                {
                    if (!kv.Key.StartsWith(stem)) continue;
                    if (best == null || kv.Key.Length < best.Length) { best = kv.Key; go = kv.Value; }
                }
                if (go != null) return true;
                foreach (var g in prefabAssets)
                {
                    if (g == null || !Normalize(g.name).StartsWith(stem)) continue;
                    go = g;
                    isAssetPrefab = true;
                    return true;
                }
                return false;
            }
        }

        /// <summary>把交接处映射到细节件包档位并做成一件 Piece；缺档返回 null（调用方走程序化兜底）。</summary>
        static Piece TryArcPrefabPiece(ArcCatalog cat, ArcGeo geo, Junction jc, string name, string block,
            float h, ref int angleWarn)
        {
            if (cat == null || !cat.enabled) return null;

            float pickAng = Nearest(ArcPrefabAngles, jc.angleDeg);
            float pickR = Nearest(ArcPrefabRadii,
                Mathf.Clamp(geo.radius, ArcPrefabRadii[0], ArcPrefabRadii[ArcPrefabRadii.Length - 1]));
            float pickH = h >= (ArcPrefabHeights[0] + ArcPrefabHeights[ArcPrefabHeights.Length - 1]) * 0.5f
                ? ArcPrefabHeights[ArcPrefabHeights.Length - 1] : ArcPrefabHeights[0];

            string stem = ArcCatalog.Normalize($"{ArcCornerStem}A{NumToken(pickAng)}_R{NumToken(pickR)}_H{NumToken(pickH)}");
            GameObject src;
            bool isPrefab;
            if (!cat.TryFind(stem, out src, out isPrefab) || src == null) return null;

            var bounds = BoundsOf(src);            // 资产内存里量（模型根 identity → 即局部包围盒）
            if (bounds.size.y < 0.01f) return null;
            var centerDir = new Vector3(bounds.center.x, 0f, bounds.center.z);
            if (centerDir.sqrMagnitude < 1e-4f) centerDir = Vector3.forward;
            centerDir.Normalize();

            // 朝向：4 个 90° 候选里挑"件体中心方向"最贴近"弧心→交点"的那个（把 90/180° 歧义交给几何判）
            float bestYaw = 0f, bestAng = float.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                float cand = k * 90f;
                var d = Quaternion.Euler(0f, cand, 0f) * centerDir;
                float ang = Vector3.Angle(d, geo.dirToCorner);
                if (ang < bestAng) { bestAng = ang; bestYaw = cand; }
            }
            if (bestAng > 45f) angleWarn++;

            float scaleY = 1f;
            if (P_ArcPrefabScaleY != 0) scaleY = Mathf.Clamp(h / bounds.size.y, 0.2f, 3f);

            int tris = 0;
            foreach (var mf in src.GetComponentsInChildren<MeshFilter>(true))
                if (mf != null && mf.sharedMesh != null) tris += mf.sharedMesh.triangles.Length / 3;

            return new Piece
            {
                name = name, block = block, category = CatArc,
                prefabSrc = src, prefabIsAsset = isPrefab,
                pos = geo.oCenter, yaw = bestYaw, scaleY = scaleY, tris = tris,
            };
        }

        static Bounds BoundsOf(GameObject go)
        {
            var b = new Bounds();
            bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.enabled) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return b;
        }

        static float Nearest(float[] arr, float v)
        {
            float best = arr[0], bd = float.MaxValue;
            foreach (var a in arr)
            {
                float d = Mathf.Abs(a - v);
                if (d < bd) { bd = d; best = a; }
            }
            return best;
        }

        /// <summary>1 → "1"、1.5 → "1p5"、90 → "90"（件名里小数点写成 p）。</summary>
        static string NumToken(float v)
        {
            if (Mathf.Abs(v - Mathf.Round(v)) < 0.01f)
                return Mathf.RoundToInt(v).ToString(CultureInfo.InvariantCulture);
            return v.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', 'p');
        }

        // ---------------------------------------------------------------- 程序化弧面

        static Mesh BuildArcMesh(ArcGeo g, float segDeg, float miterDeg)
        {
            int segs = Mathf.Max(3, Mathf.CeilToInt(Mathf.Abs(g.sweep) * Mathf.Rad2Deg / Mathf.Max(segDeg, 2f)));
            var mb = new MeshBuilder();
            float ri = Mathf.Max(g.radius - g.thick * 0.5f, 0.02f);
            float ro = g.radius + g.thick * 0.5f;
            float y0 = g.y0, y1 = g.y0 + g.h;
            float miter = g.thick / Mathf.Max(Mathf.Tan(miterDeg * Mathf.Deg2Rad), 1e-3f);

            for (int i = 0; i < segs; i++)
            {
                float a0 = g.a0 + g.sweep * (i / (float)segs);
                float a1 = g.a0 + g.sweep * ((i + 1) / (float)segs);
                float am = (a0 + a1) * 0.5f;
                var dM = new Vector3(Mathf.Cos(am), 0f, Mathf.Sin(am));
                var iA = Pt(g.oCenter, ri, a0);
                var iB = Pt(g.oCenter, ri, a1);
                var oA = Pt(g.oCenter, ro, a0);
                var oB = Pt(g.oCenter, ro, a1);

                mb.Quad(At(oA, y0), At(oB, y0), At(oB, y1), At(oA, y1), dM);
                mb.Quad(At(iA, y0), At(iA, y1), At(iB, y1), At(iB, y0), -dM);
                mb.Quad(At(iA, y1), At(iB, y1), At(oB, y1), At(oA, y1), Vector3.up);
                mb.Quad(At(iA, y0), At(oA, y0), At(oB, y0), At(iB, y0), Vector3.down);
            }

            // 两端封口：外缘沿弧向内收 miter（45° = 内收一个壁厚）
            float sgn = Mathf.Sign(g.sweep);
            var st = Tan(g.a0) * sgn;
            var en = Tan(g.a0 + g.sweep) * sgn;
            var iS = Pt(g.oCenter, ri, g.a0);
            var oS = Pt(g.oCenter, ro, g.a0) + st * miter;
            var iE = Pt(g.oCenter, ri, g.a0 + g.sweep);
            var oE = Pt(g.oCenter, ro, g.a0 + g.sweep) - en * miter;
            mb.Quad(At(iS, y0), At(oS, y0), At(oS, y1), At(iS, y1), -st);
            mb.Quad(At(iE, y0), At(oE, y0), At(oE, y1), At(iE, y1), en);

            return mb.Build("Arc");
        }

        static Vector3 Pt(Vector3 o, float r, float a) =>
            new Vector3(o.x + Mathf.Cos(a) * r, 0f, o.z + Mathf.Sin(a) * r);

        static Vector3 Tan(float a) => new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a));

        static Vector3 At(Vector3 p, float y) => new Vector3(p.x, y, p.z);

        // ================================================================ ② 玻璃幕墙

        class Side
        {
            public string suffix;
            public bool alongX;     // true: 幕墙面是 x 常数，跨度沿 z
            public float plane;
            public float s0, s1;
            public Vector3 outN;    // 向外法线
        }

        static List<Piece> PlanGlazing(Ctx ctx, StringBuilder rep, Budget bud)
        {
            var pieces = new List<Piece>();
            rep.AppendLine();
            if (!ctx.hasMerged)
            {
                rep.AppendLine("[② 幕墙] 场景无渲染件，跳过");
                return pieces;
            }
            var b = ctx.merged;
            float xMin = b.min.x - P_Expand, xMax = b.max.x + P_Expand;
            float zMin = b.min.z - P_Expand, zMax = b.max.z + P_Expand;
            float y0 = Mathf.Floor(b.min.y);
            float yTop = b.max.y + P_TopExtra;

            var sides = new List<Side>
            {
                new Side { suffix = "XP", alongX = true,  plane = xMax, s0 = zMin, s1 = zMax, outN = Vector3.right },
                new Side { suffix = "XN", alongX = true,  plane = xMin, s0 = zMin, s1 = zMax, outN = Vector3.left },
                new Side { suffix = "ZP", alongX = false, plane = zMax, s0 = xMin, s1 = xMax, outN = Vector3.forward },
                new Side { suffix = "ZN", alongX = false, plane = zMin, s0 = xMin, s1 = xMax, outN = Vector3.back },
            };

            var openings = Openings(ctx, sides, rep);
            var frameMat = AssetDatabase.LoadAssetAtPath<Material>(P_FrameMatPath);
            if (frameMat == null && ctx.walls.Count > 0) frameMat = ctx.walls[0].mat;
            if (frameMat == null) rep.AppendLine($"        [注意] 框体母材质 {P_FrameMatPath} 取不到，框体可能无材质");

            int cells = 0, mull = 0, tran = 0, cut = 0;
            foreach (var s in sides)
            {
                var gm = new MeshBuilder();
                var fm = new MeshBuilder();
                float span = s.s1 - s.s0;
                int cols = Mathf.Max(1, Mathf.RoundToInt(span / P_Mullion));
                int rows = Mathf.Max(1, Mathf.RoundToInt((yTop - y0) / P_Transom));
                float step = span / cols;
                float rowH = (yTop - y0) / rows;
                // 玻璃面：沿"向内"方向内缩 P_GlassInset（向内 = -outN）
                float gPlane = s.plane - (s.alongX ? s.outN.x : s.outN.z) * P_GlassInset;

                for (int ci = 0; ci < cols; ci++)
                    for (int ri = 0; ri < rows; ri++)
                    {
                        float a = s.s0 + ci * step;
                        float aa = a + step;
                        float ya = y0 + ri * rowH;
                        float yb = ya + rowH;
                        if (Hit(openings, PointOn(s, (a + aa) * 0.5f, (ya + yb) * 0.5f))) { cut++; continue; }
                        gm.Quad(PointOnPlane(s, gPlane, a, ya), PointOnPlane(s, gPlane, aa, ya),
                                PointOnPlane(s, gPlane, aa, yb), PointOnPlane(s, gPlane, a, yb), -s.outN);
                        cells++;
                    }

                for (int ci = 0; ci <= cols; ci++)
                {
                    float a = s.s0 + ci * step;
                    if (Hit(openings, PointOn(s, a, (y0 + yTop) * 0.5f))) { cut++; continue; }
                    AddBar(fm, s, a, a, y0, yTop, P_MullionW, P_MullionD, true);
                    mull++;
                }
                for (int ri = 0; ri <= rows; ri++)
                {
                    float y = y0 + ri * rowH;
                    if (Hit(openings, PointOn(s, (s.s0 + s.s1) * 0.5f, y))) { cut++; continue; }
                    AddBar(fm, s, s.s0, s.s1, y, y, P_TransomH, P_TransomD, false);
                    tran++;
                }

                int gtri = gm.TriCount;
                int ftri = fm.TriCount;
                if (gtri > 0)
                {
                    var pcG = new Piece
                    {
                        name = $"MAPSKIN_Glazing_Glass_{s.suffix}", block = "Global", category = CatGlazing,
                        glassMat = true, mesh = gm.Build("Glass_" + s.suffix), pos = Vector3.zero, tris = gtri,
                    };
                    if (bud.Allow(gtri)) pieces.Add(pcG);
                }
                if (ftri > 0)
                {
                    var pcF = new Piece
                    {
                        name = $"MAPSKIN_Glazing_Frame_{s.suffix}", block = "Global", category = CatGlazing,
                        mat = frameMat, mesh = fm.Build("Frame_" + s.suffix), pos = Vector3.zero, tris = ftri,
                    };
                    if (bud.Allow(ftri)) pieces.Add(pcF);
                }
            }

            rep.AppendLine($"[② 幕墙] 合并包围盒外扩 {P_Expand}m → 边界 x[{xMin:F0},{xMax:F0}] z[{zMin:F0},{zMax:F0}]，高 {y0:F0}→{yTop:F0}（最高点 +{P_TopExtra}m）");
            rep.AppendLine($"        竖挺 {P_Mullion}m 一道（{mull} 根）、横梁 {P_Transom}m 一道（{tran} 根）、" +
                           $"玻璃面内缩 {P_GlassInset * 1000f:F0}mm（{cells} 格 = 6×4m 开间，与件包 SM_GlassUnit_* 同模数）");
            rep.AppendLine($"        进出口开口：{openings.Count} 处（{P_OpenW}m 宽 × {P_OpenH}m 高），被挖掉的格/杆 {cut} 个");
            rep.AppendLine($"        → 计划生成 {pieces.Count} 件（每边 1 个玻璃合并网格 + 1 个框体合并网格；" +
                           $"并入网格是为守住 2000 对象护栏 —— 上万开间逐件实例化必爆护栏）；玻璃 = {GlazingShaderName}，框体 = {P_FrameMatPath}");
            return pieces;
        }

        static Vector3 PointOn(Side s, float a, float y) =>
            s.alongX ? new Vector3(s.plane, y, a) : new Vector3(a, y, s.plane);

        static Vector3 PointOnPlane(Side s, float plane, float a, float y) =>
            s.alongX ? new Vector3(plane, y, a) : new Vector3(a, y, plane);

        /// <summary>竖挺（a0=a1 同一坐标、y0..y1 竖向）或横梁（y0=y1、a0..a1 展开）；都是轴对齐盒子。</summary>
        static void AddBar(MeshBuilder mb, Side s, float a0, float a1, float y0, float y1,
            float cross, float depth, bool vertical)
        {
            float loA, hiA, loY, hiY;
            if (vertical) { loA = a0 - cross * 0.5f; hiA = a0 + cross * 0.5f; loY = y0; hiY = y1; }
            else { loA = a0; hiA = a1; loY = y0 - cross * 0.5f; hiY = y0 + cross * 0.5f; }
            float loD = s.plane - depth * 0.5f, hiD = s.plane + depth * 0.5f;
            if (s.alongX) mb.Box(new Vector3(loD, loY, loA), new Vector3(hiD, hiY, hiA));
            else mb.Box(new Vector3(loA, loY, loD), new Vector3(hiA, hiY, hiD));
        }

        /// <summary>进出口开口：橙（进入点）/黄（离开点）/洋红（滑铲）语义件投到最近的幕墙面，开 12m×8m 洞。</summary>
        static List<Bounds> Openings(Ctx ctx, List<Side> sides, StringBuilder rep)
        {
            var list = new List<Bounds>();
            var marks = new List<Vector3>();
            foreach (var bx in ctx.all)
                if (IsAccent(bx.mat)) marks.Add(bx.c);

            foreach (var m in marks)
            {
                Side best = null;
                float bd = float.MaxValue;
                foreach (var s in sides)
                {
                    float d = s.alongX ? Mathf.Abs(m.x - s.plane) : Mathf.Abs(m.z - s.plane);
                    if (d < bd) { bd = d; best = s; }
                }
                if (best == null) continue;
                float a = Mathf.Clamp(best.alongX ? m.z : m.x, best.s0 + P_OpenW * 0.5f, best.s1 - P_OpenW * 0.5f);
                var ctr = PointOn(best, a, m.y);
                var size = best.alongX ? new Vector3(2f, P_OpenH, P_OpenW) : new Vector3(P_OpenW, P_OpenH, 2f);
                list.Add(new Bounds(ctr, size));
            }
            if (marks.Count == 0)
                rep.AppendLine("        [开口] 场景内没找到橙/黄/洋红语义标记件 → 不开口（整圈封闭）");
            return list;
        }

        static bool Hit(List<Bounds> openings, Vector3 p)
        {
            foreach (var o in openings) if (o.Contains(p)) return true;
            return false;
        }

        static bool IsAccent(Material m)
        {
            for (var cur = m; cur != null; cur = cur.parent)
                foreach (var a in AccentMats)
                    if (cur.name == a || cur.name.StartsWith(a + "_")) return true;
            return false;
        }

        // ================================================================ ③ 线脚 + 结构缝条

        static List<Piece> PlanTrim(Ctx ctx, StringBuilder rep, Budget bud)
        {
            rep.AppendLine();
            var trimGroups = new Dictionary<string, MeshBuilder>();
            var seamGroups = new Dictionary<string, MeshBuilder>();
            var groupMat = new Dictionary<string, Material>();
            int trimWalls = 0, seamWalls = 0, plinths = 0, cornices = 0, seams = 0;

            foreach (var w in ctx.walls)
            {
                if (w.H < P_TrimMinH) continue;
                trimWalls++;
                string key = w.block + "|" + MatPath(w.mat);
                if (!trimGroups.ContainsKey(key))
                {
                    trimGroups[key] = new MeshBuilder();
                    seamGroups[key] = new MeshBuilder();
                    groupMat[key] = w.mat;
                }
                var tm = trimGroups[key];
                var sm = seamGroups[key];

                var pl = ExpandBox(w, P_PlinthT, w.Y0, w.Y0 + P_PlinthH);
                tm.Box(pl.Item1, pl.Item2);
                plinths++;
                var co = ExpandBox(w, P_CorniceO, w.Y1 - P_CorniceH, w.Y1);
                tm.Box(co.Item1, co.Item2);
                cornices++;

                if (w.Len >= P_SeamMinLen)
                {
                    seamWalls++;
                    float half = w.Len * 0.5f - P_SeamW;
                    for (float t = -half; t <= half + 1e-3f; t += P_SeamStep)
                    {
                        for (int sgn = -1; sgn <= 1; sgn += 2)
                        {
                            var ctr = w.c + w.uLong * t + w.uShort * (sgn * (w.Thick * 0.5f + P_SeamD * 0.5f));
                            var size = new Vector3(
                                Mathf.Abs(w.uLong.x) * P_SeamW + Mathf.Abs(w.uShort.x) * P_SeamD, w.size.y,
                                Mathf.Abs(w.uLong.z) * P_SeamW + Mathf.Abs(w.uShort.z) * P_SeamD);
                            sm.Box(ctr - size * 0.5f, ctr + size * 0.5f);
                            seams++;
                        }
                    }
                }
            }

            var pieces = new List<Piece>();
            int gi = 0;
            foreach (var key in trimGroups.Keys.OrderBy(k => k, System.StringComparer.Ordinal))
            {
                var parts = key.Split('|');
                gi++;
                var tmb = trimGroups[key];
                if (tmb.TriCount > 0)
                {
                    int tri = tmb.TriCount;
                    var p = new Piece
                    {
                        name = $"MAPSKIN_Trim_{parts[0]}_{gi:00}", block = parts[0], category = CatTrim,
                        mat = groupMat[key], mesh = tmb.Build("Trim"), pos = Vector3.zero, tris = tri,
                    };
                    if (bud.Allow(tri)) pieces.Add(p);
                }
                var smb = seamGroups[key];
                if (smb.TriCount > 0)
                {
                    int tri = smb.TriCount;
                    var p = new Piece
                    {
                        name = $"MAPSKIN_Seam_{parts[0]}_{gi:00}", block = parts[0], category = CatSeam,
                        mat = groupMat[key], mesh = smb.Build("Seam"), pos = Vector3.zero, tris = tri,
                    };
                    if (bud.Allow(tri)) pieces.Add(p);
                }
            }

            rep.AppendLine($"[③ 线脚] 参与墙 {trimWalls} 面：踢脚线 {plinths} 道（厚 {P_PlinthT * 1000f:F0}mm、高 {P_PlinthH * 1000f:F0}mm）、" +
                           $"顶角线 {cornices} 道（高 {P_CorniceH * 1000f:F0}mm、出挑 {P_CorniceO * 1000f:F0}mm）");
            rep.AppendLine($"        结构缝条：大面墙（长 ≥ {P_SeamMinLen}m）{seamWalls} 面，每 {P_SeamStep}m 一道 × 两面 → {seams} 道" +
                           $"（宽 {P_SeamW * 1000f:F0}mm、深 {P_SeamD * 1000f:F0}mm）");
            rep.AppendLine("        缝条口径：灰盒只做加法（不改原对象、不布尔开槽），故以 15mm **外凸肋**表达凹缝；真凹陷需减材建模，本工具不做");
            rep.AppendLine($"        → 计划生成 {pieces.Count} 件（按 区块×材质 合并，共 {gi} 组）");
            return pieces;
        }

        static string MatPath(Material m)
        {
            if (m == null) return "<null>";
            var p = AssetDatabase.GetAssetPath(m);
            return string.IsNullOrEmpty(p) ? "<builtin>" + m.name : p;
        }

        /// <summary>墙 AABB 水平四周外扩 out、竖向裁到 [ya,yb]。</summary>
        static (Vector3, Vector3) ExpandBox(Box w, float outR, float ya, float yb)
        {
            var min = w.c - w.size * 0.5f;
            var max = w.c + w.size * 0.5f;
            var lo = new Vector3(min.x - outR, Mathf.Min(ya, yb), min.z - outR);
            var hi = new Vector3(max.x + outR, Mathf.Max(ya, yb), max.z + outR);
            return (lo, hi);
        }

        // ================================================================ ④ 合理性兜底（支撑）

        static List<Piece> PlanSupports(Ctx ctx, StringBuilder rep, Budget bud)
        {
            rep.AppendLine();
            var groups = new Dictionary<string, MeshBuilder>();
            var groupMat = new Dictionary<string, Material>();
            var log = new List<string>();
            int floating = 0, supported = 0, noBase = 0;

            foreach (var p in ctx.walls)
            {
                // "小件"两项判定取或：整体小（最大边 ≤ P_SupportMaxPiece），或**细长杆件**（厚度 ≤ P_SupportThickMax）。
                // 灰盒里横梁普遍是 1×1×16 / 2×2×100 这类细长条，只按最大边卡会把它们全漏掉（实测 0 命中）。
                bool small = p.Len <= P_SupportMaxPiece && p.H <= P_SupportMaxPiece;
                if (!small && p.Thick > P_SupportThickMax) continue;
                float bestTop = float.MinValue;
                Box bestBox = null;
                foreach (var q in ctx.all)
                {
                    if (ReferenceEquals(q, p)) continue;
                    if (q.Y1 > p.Y0 + 0.05f) continue;
                    if (p.Y0 - q.Y1 > P_SupportMaxPiece * 2f) continue;
                    float ox = Mathf.Min(p.c.x + p.size.x * 0.5f, q.c.x + q.size.x * 0.5f)
                             - Mathf.Max(p.c.x - p.size.x * 0.5f, q.c.x - q.size.x * 0.5f);
                    float oz = Mathf.Min(p.c.z + p.size.z * 0.5f, q.c.z + q.size.z * 0.5f)
                             - Mathf.Max(p.c.z - p.size.z * 0.5f, q.c.z - q.size.z * 0.5f);
                    float need = Mathf.Min(p.size.x, p.size.z) * 0.3f;
                    if (ox < need || oz < need) continue;
                    if (q.Y1 > bestTop) { bestTop = q.Y1; bestBox = q; }
                }
                if (bestBox == null) { noBase++; continue; }
                float gap = p.Y0 - bestTop;
                if (gap <= P_SupportGap) { supported++; continue; }
                floating++;

                float sx = Mathf.Clamp(p.size.x * P_SupportRatio, 0.15f, 2.0f);
                float sz = Mathf.Clamp(p.size.z * P_SupportRatio, 0.15f, 2.0f);
                var ctr = new Vector3(p.c.x, (bestTop + p.Y0) * 0.5f, p.c.z);
                var size = new Vector3(sx, Mathf.Max(p.Y0 - bestTop, 0.05f), sz);
                string key = p.block + "|" + MatPath(p.mat);
                if (!groups.ContainsKey(key)) { groups[key] = new MeshBuilder(); groupMat[key] = p.mat; }
                groups[key].Box(ctr - size * 0.5f, ctr + size * 0.5f);

                if (log.Count < 40)
                    log.Add($"        {p.name}（{p.size.x:F1}×{p.size.y:F1}×{p.size.z:F1}m）底 {p.Y0:F2} → 下方 {bestBox.name} 顶 {bestTop:F2}，" +
                            $"悬空 {gap:F2}m，补柱 {sx:F2}×{sz:F2}m × 高 {p.Y0 - bestTop:F2}m");
            }

            var pieces = new List<Piece>();
            int gi = 0;
            foreach (var key in groups.Keys.OrderBy(k => k, System.StringComparer.Ordinal))
            {
                var parts = key.Split('|');
                gi++;
                int tri = groups[key].TriCount;
                var pc = new Piece
                {
                    name = $"MAPSKIN_Support_{parts[0]}_{gi:00}", block = parts[0], category = CatSupport,
                    mat = groupMat[key], mesh = groups[key].Build("Support"), pos = Vector3.zero, tris = tri,
                };
                if (bud.Allow(tri)) pieces.Add(pc);
            }

            rep.AppendLine($"[④ 支撑] 悬空件（小件 = 最大边 ≤ {P_SupportMaxPiece}m **或** 厚度 ≤ {P_SupportThickMax}m 的细长杆；底离支撑 > {P_SupportGap}m）{floating} 件；" +
                           $"贴地/有支撑 {supported} 件；下方无落点 {noBase} 件（不补）");
            rep.AppendLine($"        支撑截面 = 支承件 footprint × {P_SupportRatio:P0}（夹 0.15–2.0m）");
            foreach (var l in log) rep.AppendLine(l);
            if (floating > log.Count) rep.AppendLine($"        …（其余 {floating - log.Count} 件略）");
            rep.AppendLine($"        → 计划生成 {pieces.Count} 件（按 区块×材质 合并，共 {gi} 组）");
            return pieces;
        }

        // ================================================================ 落地（生成 / 资产）

        class Piece
        {
            public string name;
            public string block;
            public string category;
            public Mesh mesh;
            public Material mat;
            public bool glassMat;
            public GameObject prefabSrc;      // 细节件包件（FBX 子对象）或 .prefab 资产
            public bool prefabIsAsset;        // true → PrefabUtility.InstantiatePrefab
            public Vector3 pos;
            public float yaw;
            public float scaleY = 1f;
            public int tris;
        }

        class Budget
        {
            public int MaxObjects = 2000;
            public long MaxTris = 3000000;
            public int Objects;
            public long Tris;
            public int Skipped;
            public bool Allow(long tris)
            {
                if (Objects + 1 > MaxObjects || Tris + tris > MaxTris) { Skipped++; return false; }
                Objects++;
                Tris += tris;
                return true;
            }
            public string Describe() =>
                $"[护栏] 计划用量：对象 {Objects}/{MaxObjects}、三角形 {Tris:N0}/{MaxTris:N0}；因触顶放弃 {Skipped} 件";
        }

        static int Execute(List<Piece> pieces, Ctx ctx, StringBuilder rep)
        {
            if (pieces.Count == 0) return 0;
            EnsureFolder("Assets/Art", "EnvSkins");
            EnsureFolder("Assets/Art/EnvSkins", "Building");
            var root = RootForWrite(ctx);

            int made = 0;
            Material glazing = null;
            foreach (var pc in pieces)
            {
                if (ctx.existing.Contains(pc.name))
                {
                    rep.AppendLine($"  [幂等跳过] {pc.name} 已存在");
                    continue;
                }
                var container = ContainerFor(root, pc.block, pc.category);
                GameObject go;
                if (pc.prefabSrc != null)
                {
                    if (pc.prefabIsAsset)
                        go = (GameObject)PrefabUtility.InstantiatePrefab(pc.prefabSrc, container.transform);
                    else
                        go = Object.Instantiate(pc.prefabSrc, container.transform);
                    go.name = pc.name;
                    go.transform.position = pc.pos;
                    go.transform.rotation = Quaternion.Euler(0f, pc.yaw, 0f);
                    go.transform.localScale = new Vector3(1f, pc.scaleY, 1f);
                    RetexturePrefabInstance(go, pc, rep);
                }
                else
                {
                    go = new GameObject(pc.name);
                    go.transform.SetParent(container.transform, false);
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one;
                    var mf = go.AddComponent<MeshFilter>();
                    var mr = go.AddComponent<MeshRenderer>();
                    var path = $"{MeshDir}/{pc.name}.asset";
                    if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
                    pc.mesh.name = pc.name;
                    AssetDatabase.CreateAsset(pc.mesh, path);
                    mf.sharedMesh = pc.mesh;
                    if (pc.glassMat)
                    {
                        if (glazing == null) glazing = EnsureGlazingMaterial(rep);
                        mr.sharedMaterial = glazing;
                    }
                    else
                    {
                        mr.sharedMaterial = pc.mat != null
                            ? pc.mat
                            : AssetDatabase.LoadAssetAtPath<Material>(P_FrameMatPath);
                    }
                }
                ctx.existing.Add(pc.name);
                made++;
                rep.AppendLine($"  + {pc.name}  ({pc.block}/{pc.category}, {pc.tris} tri)");
            }
            return made;
        }

        /// <summary>
        /// 细节件包实例化后的二次贴皮：FBX 子件没有可用 UV、材质是导入默认灰。
        /// 逐 MeshFilter 复制一份 mesh，按世界三平面重写 UV0（墙基准），再挂语义族材质。
        /// 只处理弧角件，量级 100 余件 × 1–2 子网格。
        /// </summary>
        static void RetexturePrefabInstance(GameObject go, Piece pc, StringBuilder rep)
        {
            if (go == null) return;
            int mi = 0;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var copy = Object.Instantiate(mf.sharedMesh);
                copy.name = $"{pc.name}_m{mi}";
                FixOutwardNormals(copy);
                BakeWorldUv0(copy, mf.transform, P_WallUvBase);
                var path = $"{MeshDir}/{pc.name}_m{mi}.asset";
                if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
                AssetDatabase.CreateAsset(copy, path);
                mf.sharedMesh = copy;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr != null && pc.mat != null) mr.sharedMaterial = pc.mat;
                mi++;
            }
            if (mi > 0) rep.AppendLine($"      弧角贴皮：{pc.name} 复制 {mi} 份 mesh，UV0 世界三平面/{P_WallUvBase}m + 语义族材质");
        }

        /// <summary>
        /// 弧角 FBX 的法线朝向不可靠（Blender 导出实测朝内），NPR 的 halfLambert 直接给 0 → 整弧纯黑。
        /// 弧角件的局部原点就是抹角圆心、地面高度，所以"外法线"应背离 XZ 原点；用法线均值判向，
        /// 反了就同时翻法线与绕序（只翻法线会被背面剔除挡掉）。
        /// </summary>
        static void FixOutwardNormals(Mesh m)
        {
            var vs = m.vertices;
            var ns = m.normals;
            if (vs == null || ns == null || vs.Length != ns.Length || vs.Length == 0) return;
            double sum = 0; int n = 0;
            for (int i = 0; i < vs.Length; i++)
            {
                var radial = new Vector3(vs[i].x, 0f, vs[i].z);
                if (radial.sqrMagnitude < 1e-8f) continue;
                var nn = ns[i];
                if (nn.sqrMagnitude < 1e-12f) continue;
                sum += Vector3.Dot(nn.normalized, radial.normalized);
                n++;
            }
            if (n == 0 || sum >= 0) return;
            for (int i = 0; i < ns.Length; i++) ns[i] = -ns[i];
            m.normals = ns;
            var tris = m.triangles;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int t = tris[i + 1]; tris[i + 1] = tris[i + 2]; tris[i + 2] = t;
            }
            m.triangles = tris;
        }

        /// <summary>与 MapSkinUvBake 同口径：按顶点法线主轴投影，UV = 世界坐标 / 基准尺度。</summary>
        static void BakeWorldUv0(Mesh m, Transform t, float baseLen)
        {
            var vs = m.vertices;
            var ns = m.normals;
            if (vs == null || vs.Length == 0) return;
            if (ns == null || ns.Length != vs.Length) { m.RecalculateNormals(); ns = m.normals; }
            var mtx = t.localToWorldMatrix;
            var nmtx = mtx.inverse.transpose;
            var uv = new Vector2[vs.Length];
            for (int i = 0; i < vs.Length; i++)
            {
                var wp = mtx.MultiplyPoint3x4(vs[i]);
                var n = nmtx.MultiplyVector(ns[i]);
                if (n.sqrMagnitude < 1e-12f) n = Vector3.up; else n.Normalize();
                float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
                if (ay >= ax && ay >= az) uv[i] = new Vector2(wp.x / baseLen, wp.z / baseLen);
                else if (ax >= az) uv[i] = new Vector2(wp.z / baseLen, wp.y / baseLen);
                else uv[i] = new Vector2(wp.x / baseLen, wp.y / baseLen);
            }
            m.SetUVs(0, uv);
            m.RecalculateBounds();
        }

        static GameObject RootForWrite(Ctx ctx)
        {
            if (ctx.root != null) return ctx.root;
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
                foreach (var g in scene.GetRootGameObjects())
                    if (g != null && g.name == RootName) { ctx.root = g; return g; }
            var root = new GameObject(RootName);
            ctx.root = root;
            return root;
        }

        static GameObject ContainerFor(GameObject root, string block, string category)
        {
            return Child(Child(root, string.IsNullOrEmpty(block) ? "S_00" : block), category);
        }

        static GameObject Child(GameObject parent, string name)
        {
            var t = parent.transform.Find(name);
            if (t != null) return t.gameObject;
            var g = new GameObject(name);
            g.transform.SetParent(parent.transform, false);
            return g;
        }

        /// <summary>玻璃材质：`OurFunction/NPR/Transparent Toon` 实例（规范 §2.2 半透件唯一跑法）。</summary>
        static Material EnsureGlazingMaterial(StringBuilder rep)
        {
            var sh = Shader.Find(GlazingShaderName);
            if (sh == null)
            {
                Debug.LogError($"[建筑] 找不到 shader {GlazingShaderName}，玻璃件将没有材质");
                return null;
            }
            EnsureFolder("Assets/Art/Materials", "MapSkin");
            EnsureFolder("Assets/Art/Materials/MapSkin", "Glazing");
            var m = AssetDatabase.LoadAssetAtPath<Material>(GlazingMatPath);
            if (m == null)
            {
                m = new Material(sh);
                AssetDatabase.CreateAsset(m, GlazingMatPath);
            }
            else if (m.shader != sh) m.shader = sh;

            var col = new Color(Mathf.Min(P_GlassColor.r, 1f), Mathf.Min(P_GlassColor.g, 1f), Mathf.Min(P_GlassColor.b, 1f),
                                Mathf.Clamp(P_GlassAlpha, 0f, 1f));
            float smooth = Mathf.Clamp01(1f - P_GlassRough);
            m.SetColor("_BaseColor", col);
            m.SetFloat("_Metallic", 0f);
            m.SetFloat("_Smoothness", smooth);        // rough 0.08 → smoothness 0.92
            // `_RoughnessMap` 会顶掉 `_Smoothness`（shader 注释：overrides Smoothness）→ 显式清槽 + 关 keyword
            foreach (var kw in new[] { "_ROUGHNESSMAP", "_METALLICSPECGLOSSMAP", "_NORMALMAP", "_OCCLUSIONMAP" })
                m.DisableKeyword(kw);
            foreach (var tex in new[] { "_RoughnessMap", "_MetallicGlossMap", "_BumpMap", "_OcclusionMap", "_BaseMap" })
                m.SetTexture(tex, null);
            m.SetFloat("_Surface", 1f);   // 半透
            m.SetFloat("_Cull", 0f);      // 双面：关卡内看内面、外面看外面
            EditorUtility.SetDirty(m);

            rep.AppendLine($"  [玻璃材质] {GlazingMatPath} 写入：_BaseColor=({col.r:F2},{col.g:F2},{col.b:F2},a={col.a:F2})、" +
                           $"_Metallic=0、_Smoothness={smooth:F2}(rough {P_GlassRough})、_Cull=Off、贴图槽清空");
            return m;
        }

        static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{child}")) AssetDatabase.CreateFolder(parent, child);
        }

        // ================================================================ 网格工具（世界坐标直写）

        /// <summary>
        /// 顶点即世界坐标的三角形汤构建器（父链全 identity，故不需要局部变换）。
        /// UV 直接按 MapSkinUvBake 的口径写 UV0：竖向面 (世界/3.6)、水平面 (世界/4.8)。
        /// </summary>
        class MeshBuilder
        {
            readonly List<Vector3> v = new List<Vector3>();
            readonly List<Vector3> n = new List<Vector3>();
            readonly List<Vector2> uv = new List<Vector2>();
            readonly List<int> tri = new List<int>();

            public int TriCount => tri.Count / 3;

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outwardHint)
            {
                var nrm = Vector3.Cross(b - a, c - a);
                if (nrm.sqrMagnitude < 1e-12f) return;
                nrm.Normalize();
                if (Vector3.Dot(nrm, outwardHint) < 0f) { var tmp = b; b = d; d = tmp; nrm = -nrm; }
                int i0 = v.Count;
                Add(a, nrm); Add(b, nrm); Add(c, nrm); Add(d, nrm);
                tri.Add(i0); tri.Add(i0 + 1); tri.Add(i0 + 2);
                tri.Add(i0); tri.Add(i0 + 2); tri.Add(i0 + 3);
            }

            void Add(Vector3 p, Vector3 nrm)
            {
                v.Add(p);
                n.Add(nrm);
                float ax = Mathf.Abs(nrm.x), ay = Mathf.Abs(nrm.y), az = Mathf.Abs(nrm.z);
                if (ay >= ax && ay >= az) uv.Add(new Vector2(p.x / FloorUvBase, p.z / FloorUvBase));
                else if (ax >= az) uv.Add(new Vector2(p.z / WallUvBase, p.y / WallUvBase));
                else uv.Add(new Vector2(p.x / WallUvBase, p.y / WallUvBase));
            }

            public void Box(Vector3 min, Vector3 max)
            {
                var a = Vector3.Min(min, max);
                var b = Vector3.Max(min, max);
                if (b.x - a.x < 1e-4f || b.y - a.y < 1e-4f || b.z - a.z < 1e-4f) return;
                Quad(new Vector3(b.x, a.y, a.z), new Vector3(b.x, a.y, b.z), new Vector3(b.x, b.y, b.z), new Vector3(b.x, b.y, a.z), Vector3.right);
                Quad(new Vector3(a.x, a.y, a.z), new Vector3(a.x, a.y, b.z), new Vector3(a.x, b.y, b.z), new Vector3(a.x, b.y, a.z), Vector3.left);
                Quad(new Vector3(a.x, b.y, a.z), new Vector3(a.x, b.y, b.z), new Vector3(b.x, b.y, b.z), new Vector3(b.x, b.y, a.z), Vector3.up);
                Quad(new Vector3(a.x, a.y, a.z), new Vector3(a.x, a.y, b.z), new Vector3(b.x, a.y, b.z), new Vector3(b.x, a.y, a.z), Vector3.down);
                Quad(new Vector3(a.x, a.y, b.z), new Vector3(b.x, a.y, b.z), new Vector3(b.x, b.y, b.z), new Vector3(a.x, b.y, b.z), Vector3.forward);
                Quad(new Vector3(a.x, a.y, a.z), new Vector3(b.x, a.y, a.z), new Vector3(b.x, b.y, a.z), new Vector3(a.x, b.y, a.z), Vector3.back);
            }

            public Mesh Build(string tag)
            {
                var m = new Mesh { name = "MAPSKIN_" + tag, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                m.SetVertices(v);
                m.SetNormals(n);
                m.SetUVs(0, uv);
                m.SetTriangles(tri, 0);
                m.RecalculateBounds();
                return m;
            }
        }

        // ================================================================ 参数解析 / 夹取兜底

        static void Sanitize()
        {
            P_Gap = Mathf.Clamp(P_Gap, 0f, 5f);
            P_ArcSegDeg = Mathf.Clamp(P_ArcSegDeg, 2f, 45f);
            P_Mullion = Mathf.Max(P_Mullion, 0.5f);
            P_Transom = Mathf.Max(P_Transom, 0.5f);
            P_SeamStep = Mathf.Max(P_SeamStep, 0.1f);      // 防死循环
            P_SeamW = Mathf.Max(P_SeamW, 0.005f);
            P_SeamD = Mathf.Max(P_SeamD, 0.001f);
            P_PlinthH = Mathf.Max(P_PlinthH, 0.01f);
            P_CorniceH = Mathf.Max(P_CorniceH, 0.01f);
            P_GlassAlpha = Mathf.Clamp(P_GlassAlpha, 0.01f, 1f);
            P_SupportThickMax = Mathf.Max(P_SupportThickMax, 0.05f);
            P_MaxObjects = Mathf.Clamp(P_MaxObjects, 1, 2000);          // 硬顶：单次 ≤2000 对象
            P_MaxTris = System.Math.Max(1L, System.Math.Min(P_MaxTris, 3000000L));   // 硬顶：≤300 万三角
        }

        static void ParseArgs()
        {
            var a = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
            {
                switch (a[i])
                {
                    case "-be-gap": P_Gap = Fl(a[i + 1], P_Gap); break;
                    case "-be-arcmin": P_ArcMinDeg = Fl(a[i + 1], P_ArcMinDeg); break;
                    case "-be-arcmax": P_ArcMaxDeg = Fl(a[i + 1], P_ArcMaxDeg); break;
                    case "-be-arck": P_ArcRadiusScale = Fl(a[i + 1], P_ArcRadiusScale); break;
                    case "-be-arcrmin": P_ArcRadiusMin = Fl(a[i + 1], P_ArcRadiusMin); break;
                    case "-be-arcrmax": P_ArcRadiusMax = Fl(a[i + 1], P_ArcRadiusMax); break;
                    case "-be-arcseg": P_ArcSegDeg = Fl(a[i + 1], P_ArcSegDeg); break;
                    case "-be-arcmiter": P_ArcMiter = Fl(a[i + 1], P_ArcMiter); break;
                    case "-be-minh": P_MinWallH = Fl(a[i + 1], P_MinWallH); break;
                    case "-be-minlen": P_MinLenRatio = Fl(a[i + 1], P_MinLenRatio); break;
                    case "-be-arcpref": P_UseArcPrefab = In(a[i + 1], P_UseArcPrefab); break;
                    case "-be-arcprefsy": P_ArcPrefabScaleY = In(a[i + 1], P_ArcPrefabScaleY); break;
                    case "-be-expand": P_Expand = Fl(a[i + 1], P_Expand); break;
                    case "-be-mullion": P_Mullion = Fl(a[i + 1], P_Mullion); break;
                    case "-be-transom": P_Transom = Fl(a[i + 1], P_Transom); break;
                    case "-be-topxtra": P_TopExtra = Fl(a[i + 1], P_TopExtra); break;
                    case "-be-ginset": P_GlassInset = Fl(a[i + 1], P_GlassInset); break;
                    case "-be-mullw": P_MullionW = Fl(a[i + 1], P_MullionW); break;
                    case "-be-mulld": P_MullionD = Fl(a[i + 1], P_MullionD); break;
                    case "-be-tranh": P_TransomH = Fl(a[i + 1], P_TransomH); break;
                    case "-be-trand": P_TransomD = Fl(a[i + 1], P_TransomD); break;
                    case "-be-galpha": P_GlassAlpha = Fl(a[i + 1], P_GlassAlpha); break;
                    case "-be-grough": P_GlassRough = Fl(a[i + 1], P_GlassRough); break;
                    case "-be-gcolor":
                        {
                            var p = a[i + 1].Split(',');
                            if (p.Length >= 3)
                                P_GlassColor = new Color(Fl(p[0], P_GlassColor.r), Fl(p[1], P_GlassColor.g), Fl(p[2], P_GlassColor.b));
                            break;
                        }
                    case "-be-openw": P_OpenW = Fl(a[i + 1], P_OpenW); break;
                    case "-be-openh": P_OpenH = Fl(a[i + 1], P_OpenH); break;
                    case "-be-trimminh": P_TrimMinH = Fl(a[i + 1], P_TrimMinH); break;
                    case "-be-plinthh": P_PlinthH = Fl(a[i + 1], P_PlinthH); break;
                    case "-be-plintht": P_PlinthT = Fl(a[i + 1], P_PlinthT); break;
                    case "-be-corniceh": P_CorniceH = Fl(a[i + 1], P_CorniceH); break;
                    case "-be-corniceo": P_CorniceO = Fl(a[i + 1], P_CorniceO); break;
                    case "-be-seamstep": P_SeamStep = Fl(a[i + 1], P_SeamStep); break;
                    case "-be-seamw": P_SeamW = Fl(a[i + 1], P_SeamW); break;
                    case "-be-seamd": P_SeamD = Fl(a[i + 1], P_SeamD); break;
                    case "-be-seamminlen": P_SeamMinLen = Fl(a[i + 1], P_SeamMinLen); break;
                    case "-be-supgap": P_SupportGap = Fl(a[i + 1], P_SupportGap); break;
                    case "-be-supmax": P_SupportMaxPiece = Fl(a[i + 1], P_SupportMaxPiece); break;
                    case "-be-supthick": P_SupportThickMax = Fl(a[i + 1], P_SupportThickMax); break;
                    case "-be-supk": P_SupportRatio = Fl(a[i + 1], P_SupportRatio); break;
                    case "-be-maxobjs": P_MaxObjects = In(a[i + 1], P_MaxObjects); break;
                    case "-be-maxtris": P_MaxTris = InL(a[i + 1], P_MaxTris); break;
                    case "-be-framemat": P_FrameMatPath = a[i + 1]; break;
                }
            }
        }

        static float Fl(string s, float d)
        {
            float x;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out x) ? x : d;
        }

        static int In(string s, int d)
        {
            int x;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out x) ? x : d;
        }

        static long InL(string s, long d)
        {
            long x;
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out x) ? x : d;
        }
    }
}
