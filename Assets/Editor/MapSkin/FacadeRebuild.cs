// FacadeRebuild.cs — Level1 灰盒大面 → 真实工业围护结构的模块化包板几何（3D 构件，不是贴图）
//
// 干什么
//   读 `_session_audit/face_flat.json`（FaceSkin 导出的「一面一记录」扁平面表），把每个大面
//   换成一套**真几何**的围护结构：背板 + 模数网格大板（板缝暗槽 + 内缩压边线 + 四角螺栓）+
//   顶部檐口 + 底部踢脚（可带警示带）+ 转角肋；天花做背板 + 井格（格内凹、格间肋、四周圈梁）；
//   地面（可选）铺板。目标是把「灰盒毛坯房」推进到「GateKit 那樘门」的分层/厚度/倒角/缝的质量。
//
// v2 相对 v1 的差别（画面评审后升级：面板尺度 + 材质层次）
//   1) 面板改成「模数网格 + 合并」：1.2–2.4m 的模数铺满剖分场（`-panelmin/-panelmax` 可调，
//      两个方向格数都是整数、余量均摊到各格），再用 seed 把相邻格合并成 2×1/1×2/2×2/3×1… 的
//      大板（合并后单板上限 9㎡，大面靠「公平份额」合并压到单面 ≤60 块）—— 一面墙里既有
//      1m 级小板也有 2–4m 级大板，不再是 5–10m 的大平板；
//   2) 板缝 14–20mm（露出深色背板当暗槽）、正面倒角 8–12mm、板厚 0.03–0.06 抖动；
//      面积 ≥2.5㎡ 的板再加一圈「内缩 60mm、0.02m 宽、凸 8mm」的浅压边线（三线缝：暗槽 + 亮边）；
//   3) 螺栓改成每块板四角各 1 颗（角内缩 60mm，单面 ≤120 颗），不再沿边密排；
//   4) 材质从 4 个扩到 7 个（Base/PaintLight/PaintMid/MetalDark/Trim/Hazard/Emissive，v3 再按明度档
//      展开成 19 个），每面墙按 seed 抽墙族 paint/metal/mixed 决定基调色；1/3 的墙踢脚带用 Hazard、
//      1/4 的墙在离地 2.2–2.6m 的板缝里嵌一条发光腰线；天花格板改 PaintMid；
//      地面铺板改 MetalDark + 缝里露 Base；
//   5) UV0 全部按米制（面内米数 / 2，2m 周期）：板与板之间贴图连续；螺栓给 0–1 岛。
//
// v4（选项化 + 地面改白模口径）
//   · -paneltone 0（默认）= v2 逐块三色轮换（棋盘，用户认可版）；1 = v3 一墙一基调 + 明度微变 + 强调板。
//     两套配色逻辑都保留，共用同一份 19 子网格材质表（模式 0 只用各基调的 1.00 档）。
//   · 地面（-floors 1）不再用等模数格：沿面的轮廓按 1.5–4.0m 随机分行、行内再按 1.5–4.0m 独立分板
//     （每行独立 → 竖缝不贯通、允许 T 形接头），缝宽 12mm，四边各内缩 6mm 严格贴住轮廓（绝不外扩），
//     周边包边照旧。日志与 manifest 会逐组打印地面面数 / 板数 / 板宽 min-p50-max。
//
// v3（修「板面明暗棋盘」）：一面墙只用一个基调色
//   · 墙族 → 基调色：paint→PaintLight / metal→MetalDark / mixed→PaintMid，同面所有板默认都用它，
//     墙族差异只体现在整面气质上，不再逐块换色（v2 的三色轮换是棋盘格的根源）；
//   · 逐板明度微变 ±10%：每块板在 0.90/0.95/1.00/1.05/1.10 五档里挑，且整个面只用其中一个
//     「3 档窗口」（本面最大差 ≤ 0.10/0.90 = 11.1%），相邻板再额外限 ≤2 档 —— 不会黑白相间；
//   · 强调板：每面按 seed 抽 0–20% 的板跳到另一种基调色（亮漆 ↔ 深金属，中漆脸 seed 二选一），
//     互不相邻（共享边就换下一块），读作「局部换了几块板」；
//   · 明度只靠材质：NPR shader 不读顶点色（源码无 COLOR 语义），所以子网格扩到 19 个 =
//     4 个结构件（Base/Trim/Hazard/Emissive）+ 3 基调 × 5 明度档（4 个结构件材质 + 15 个板材质，
//     其中 1.00 档复用原有的 M_Facade_<Tone>）。每组仍是一个 Mesh + 一个 MeshRenderer。
//   · 实测分布（本关 797 个板场面）：明度微变部分同面最大差 p50 9.5% / p95 10.0% / max 10.0%；
//     含强调板时 p50 50% / p95 83% / max 83%（强调板是刻意的换板对比）；强调板相邻违规 0。
//
// 为什么按面表而不是直接读场景 mesh
//   FaceSkin 已经把场景里所有平面按「同法线 + 连通」聚成了大面，并给出世界平面口径
//   （origin/u/v/n + 摊平的多边形），拿这份数据生成构件是纯数学，不依赖原 mesh 的拓扑，
//   原物件一个字节都不用动（不改 mesh、不改材质、不碰 Collider、不动 Transform）。
//
// 硬约束（与地编口径一致）
//   · 只加视觉层：所有生成物挂在 identity 变换的根 `MAPSKIN_Facade` 下，按 `区块_类别` 分组
//     （S_01_Wall / S_01_Ceil / S_01_Floor），每组**合并成一个 Mesh + 一个 MeshRenderer**
//     （19 个子网格：0 背板 Base / 1 压条 Trim / 2 警示 Hazard / 3 发光 Emissive /
//      4..18 = 3 基调 × 5 明度档；见文件头 v3 段）。
//   · 绝不改：Collider（MeshCollider/BoxCollider 一律不碰）、对象 transform、原有对象名、
//     原有材质资产、prefab 资产；
//   · 原渲染器只在「该 renderer 的全部面都被处理」或「未处理残面 < 总面积 15% 且都是
//     -minarea 以下的碎面」时才 enabled=false（避免露洞）；`-hide 0` 全局关闭；
//   · 幂等：Run 先删同名根与同名 mesh 资产再重建；Clear 删根 + 删本工具 mesh 资产 +
//     按 manifest 把上一轮被隐藏的 renderer 恢复可见。
//
// 三角预算
//   默认 1500000（-budget 可改）。生成前先「只计数不存顶点」跑一遍（几何函数完全同一套，
//   不存在估算漂移），超预算就把「每板螺栓额度」在 [0,4] 颗上二分，取装得进预算的最大额度：
//   4 = 四角、3 = 三角、2 = 对角、1 = 居中；额度是小数、逐面发牌（余数结转），所以整场密度
//   平滑贴合预算而不是粗档位跳变。额度归零仍超预算则停止生成并打印实际用量。
//   本关标定的量级（-minarea 4）：墙面 84,000㎡ → 1.68 万块板（中位 2.8㎡）；四角全给要 2.07M
//   （-floors 1 则 2.20M），所以 1.5M 预算会解到约 1.5–1.8 颗/板、合计 ≈1.49M（预算占用 99%）。
//   想满配四角：-budget 2100000。地面是另一笔账：-floors 1 多花约 14 万三角。
//
// 板尺度（v2）
//   模数网格 1.2–2.4m（`-panelmin/-panelmax`），两个方向格数取整、余量均摊到各格；再用 seed
//   随机合并相邻格（2×1 / 1×2 / 2×2 / 3×1 …，面积上限 9㎡）出 2–4m 大板。单面板数上限 60：
//   随机合并后仍超 60 的面（本关 85/797 面）改走「按行铺条 + 隔行错半块」的砖缝排布，
//   由二维搜索取最小面积的一块条把板数压进上限 —— 代价是这些超大面（>540㎡）单板会被迫做大。
//
// 薄高面与窄高条
//   h < 1.2m 的面（梁侧/压顶）走「结构包板」：两条通长压条 + 1–2 条通长凹槽，不做板场；
//   模数网格塞不下的窄条面（例如 0.3×39m 的竖鳍）也走这条，只是压条改竖着走，读作竖梁而不是
//   横着切两刀。两者都只做结构件，不做大板与构图宏（0.72m 高的窄条装不下 1.2m 的模数）。
//
// 菜单：MapSkin/立面重建/① 干跑  ② 执行  ③ 清除
// 批处理（均为 public static void、无参）：
//   -executeMethod MapSkin.FacadeRebuild.DryRun   只读面表：零写盘、零建资产、零改场景
//   -executeMethod MapSkin.FacadeRebuild.Run      生成包板几何 + 写 mesh 资产 + 存场景
//   -executeMethod MapSkin.FacadeRebuild.Clear    删生成件 + 删 mesh 资产 + 恢复 renderer
// 参数（Environment.GetCommandLineArgs 解析）：
//   -scene <path>    默认 Assets/Scenes/Level1_Skin.unity
//   -faces <json>    默认 F:/VSCode/taptap-game-jem/_session_audit/face_flat.json
//   -out <json>      manifest，默认 F:/VSCode/taptap-game-jem/_session_audit/facade_rebuild.json
//   -minarea 4       面积门槛（㎡）
//   -budget 1500000  三角预算
//   -hero 50         hero 面面积门槛（㎡，达到即加构图宏）
//   -hide 1          1 = 允许隐藏原渲染器
//   -floors 0        1 = 处理地面（默认关，甲板 kit 已覆盖 S_04）
//   -panelmin 1.2    v2：模数网格下界（米）
//   -panelmax 2.4    v2：模数网格上界（米）
//   -paneltone 0     v4：0 = 逐块三色轮换（棋盘，默认）；1 = 一墙一基调 + 明度微变 + 强调板
//   -only <子串>     只处理 objectPath 含该子串的面（样张用）
//   -limit <N>       只处理排序后的前 N 个面（样张用）
//   -skip a,b,c      额外跳过的 objectPath 子串
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    public static class FacadeRebuild
    {
        // ------------------------------------------------------------------ 常量

        const string RootName = "MAPSKIN_Facade";
        const string NprShaderName = "OurFunction/NPR/Deferred Toon";
        const string MeshDir = "Assets/Art/EnvSkins/Facade";
        const string MatDir = "Assets/Art/Materials/MapSkin/Facade";
        const string DefaultScene = "Assets/Scenes/Level1_Skin.unity";
        const string DefaultFaces = "F:/VSCode/taptap-game-jem/_session_audit/face_flat.json";
        const string DefaultOut = "F:/VSCode/taptap-game-jem/_session_audit/facade_rebuild.json";

        /// <summary>
        /// 子网格口径（v3 十九个）：
        ///   0 背板 Base / 1 压条·螺栓 Trim / 2 警示 Hazard / 3 发光 Emissive
        ///   4.. 漆面大板的「三基调 × 五明度档」：4 + tone*5 + level（tone 0=亮漆 1=中漆 2=深金属），
        ///   明度档见 ToneScales（0.90/0.95/1.00/1.05/1.10）。
        /// 为什么要这么多子网格：NPR shader 不读顶点色（源码里没有 COLOR 语义），逐块明度只能靠材质；
        /// 而「一面墙一个基调色 + 逐板 ±10% 明度」正是靠 tone×level 这 15 个材质拼出来的，
        /// 立面本身仍是每组一个 Mesh + 一个 MeshRenderer。
        /// </summary>
        const int SubBase = 0, SubTrim = 1, SubHazard = 2, SubEmissive = 3, SubToneStart = 4;
        const int ToneCount = 3, ToneLevels = 5;
        const int SubCount = SubToneStart + ToneCount * ToneLevels;   // 19

        /// <summary>明度档系数（±10%）：下标 2 是基调色本体（1.00），与已有的 M_Facade_* 材质复用。</summary>
        static readonly float[] ToneScales = { 0.90f, 0.95f, 1.00f, 1.05f, 1.10f };
        const int ToneBaseLevel = 2;                 // 1.00 档
        static readonly string[] ToneNames = { "PaintLight", "PaintMid", "MetalDark" };
        static readonly Color[] ToneColors =
        {
            new Color(0.56f, 0.55f, 0.52f, 1f),      // PaintLight 暖白漆面
            new Color(0.30f, 0.305f, 0.31f, 1f),     // PaintMid 中灰漆面（v3：0.34 → 0.30，与深金属拉开）
            new Color(0.10f, 0.102f, 0.11f, 1f),     // MetalDark 深金属（v3：0.16 → 0.10）
        };
        static readonly float[] ToneMetallic = { 0.10f, 0.15f, 0.85f };
        static readonly float[] ToneSmooth = { 0.40f, 0.42f, 0.35f };
        /// <summary>墙族 → 基调色：paint→亮漆 / metal→深金属 / mixed→中漆（一面墙只用一个基调）。</summary>
        static readonly int[] FamilyTone = { 0, 2, 1 };
        const float ToneAdjacentMaxSteps = 2f;       // 相邻板明度档最多差 2 档（0.10/0.90 = 11% ≤ 15%）
        const float AccentMaxRatio = 0.20f;          // 强调板最多占本面 20%
        const float AdjacentTol = 0.05f;             // 判「共享边」的坐标容差（板缝 14–20mm）
        /// <summary>栅格基调色（非大板）：石墨 / 钢 / 警示黄 / 冷白发光。</summary>
        static readonly Color MatBaseColor = new Color(0.20f, 0.21f, 0.23f, 1f);
        static readonly Color MatTrimColor = new Color(0.55f, 0.56f, 0.58f, 1f);
        static readonly Color MatHazardColor = new Color(0.45f, 0.33f, 0.05f, 1f);
        static readonly Color MatEmissiveColor = new Color(0.70f, 0.75f, 0.80f, 1f);

        /// <summary>19 个材质名：前 4 个是结构件，后面按 tone×level 展开（1.00 档复用已有的 M_Facade_&lt;Tone&gt;）。</summary>
        static string[] BuildMatNames()
        {
            var names = new string[SubCount];
            names[SubBase] = "M_Facade_Base";
            names[SubTrim] = "M_Facade_Trim";
            names[SubHazard] = "M_Facade_Hazard";
            names[SubEmissive] = "M_Facade_Emissive";
            for (int t = 0; t < ToneCount; t++)
                for (int l = 0; l < ToneLevels; l++)
                    names[SubToneStart + t * ToneLevels + l] = l == ToneBaseLevel
                        ? $"M_Facade_{ToneNames[t]}"
                        : $"M_Facade_{ToneNames[t]}_d{Mathf.RoundToInt(ToneScales[l] * 100f)}";
            return names;
        }

        static readonly string[] MatNames = BuildMatNames();
        static readonly Color[] MatColors = BuildMatColors();

        static Color[] BuildMatColors()
        {
            var cols = new Color[SubCount];
            cols[SubBase] = MatBaseColor;
            cols[SubTrim] = MatTrimColor;
            cols[SubHazard] = MatHazardColor;
            cols[SubEmissive] = MatEmissiveColor;
            for (int t = 0; t < ToneCount; t++)
                for (int l = 0; l < ToneLevels; l++)
                {
                    var c = ToneColors[t] * ToneScales[l];
                    cols[SubToneStart + t * ToneLevels + l] = new Color(
                        Mathf.Min(c.r, 1f), Mathf.Min(c.g, 1f), Mathf.Min(c.b, 1f), 1f);
                }
            return cols;
        }

        static int SubOfTone(int tone, int level) =>
            SubToneStart + Mathf.Clamp(tone, 0, ToneCount - 1) * ToneLevels + Mathf.Clamp(level, 0, ToneLevels - 1);

        static readonly float[] MatMetallic = BuildMatFloats(true);
        static readonly float[] MatSmooth = BuildMatFloats(false);

        static float[] BuildMatFloats(bool metallic)
        {
            var a = new float[SubCount];
            a[SubBase] = 0.50f; a[SubTrim] = 0.85f; a[SubHazard] = 0.20f; a[SubEmissive] = 0.00f;
            for (int t = 0; t < ToneCount; t++)
                for (int l = 0; l < ToneLevels; l++)
                    a[SubToneStart + t * ToneLevels + l] = metallic ? ToneMetallic[t] : ToneSmooth[t];
            return a;
        }

        const float EmissiveStrength = 2.0f;  // 发光材质的 _EmissionIntensity / _EmissionColor 倍数
        static readonly Color ShadowColor = new Color(0.30f, 0.48f, 0.53f, 1f);
        const float ToonSoftness = 0.15f;
        const float ToonThreshold = 1.0f;

        // 几何口径（米）
        const float Surf = 0.006f;            // 可见面沿 n 外移，防与原面 z-fighting
        const float PlateT = 0.02f;           // 背板厚
        const float PlateMinRemain = 0.008f;  // 挖槽后至少要留的背板余量
        const float PanelT = 0.045f;          // 大板基准厚
        const float PanelTJitter = 0.015f;    // 大板厚抖动 ±（0.03–0.06）
        const float PanelBevelMin = 0.008f;   // 板正面 45° 倒角宽（seed 在 8–12mm 里挑）
        const float PanelBevelMax = 0.012f;
        const float SeamMin = 0.014f;         // 板缝 14–20mm（露出深色背板当暗槽）
        const float SeamMax = 0.020f;
        const int PanelCapPerFace = 60;       // 单面板数上限（大面靠合并压到这个数）
        const float PanelSoftAreaCap = 9f;    // 合并后单板面积上限（软：超大面积的面会放宽）
        const float PanelMergeChance = 0.38f; // 随机合并概率
        const float EdgeLineInset = 0.06f;    // 内缩压边线：离板边 60mm
        const float EdgeLineWidth = 0.02f;    // 宽 20mm
        const float EdgeLineRise = 0.008f;    // 凸 8mm
        const float EdgeLineMinArea = 2.5f;   // 板面积 ≥ 此值才加压边线
        const float BoltR = 0.012f;           // 螺栓六棱柱半径
        const float BoltH = 0.012f;           // 螺栓凸出（v2 由 8→12mm：让螺栓压住 8mm 压边线、避免顶面共面）
        const float BoltInset = 0.06f;        // 螺栓离板角内缩
        const float BoltMinGap = 0.45f;       // 薄高面压条上的螺栓间距下限
        const float BoltMaxGap = 0.70f;       // 上限
        const int BoltCapPerFace = 120;       // 单面螺栓颗数上限
        const int BoltTris = 16;              // 闭合六棱柱：6 侧面 12 + 顶盖 4
        const float UvBase = 2.0f;            // UV0 = 面内米数 / 2（2m 周期，板与板之间连续）

        // 分类
        const float WallNyAbs = 0.35f;        // |n.y| < 0.35 → 墙
        const float CeilNy = -0.6f;           // n.y < -0.6 → 天花
        const float FloorNy = 0.6f;           // n.y > 0.6 → 地面

        /// <summary>名字里带这些子串的对象不归本工具管（门/机架/装饰件/生成件/已倒角件）。</summary>
        static readonly string[] ObjSkipTokens = { "DEC_", "MAPSKIN_", "Gate", "Rack", "SM_", "HST_", "ART_", "BEVEL_" };

        static readonly Regex BlockRe = new Regex("S_0[1-7]", RegexOptions.Compiled);

        // ------------------------------------------------------------------ 参数

        static string ScenePath = DefaultScene;
        static string FacesPath = DefaultFaces;
        static string OutPath = DefaultOut;
        static float P_MinArea = 4f;
        static long P_Budget = 1500000L;
        static float P_Hero = 50f;
        static int P_Hide = 1;
        static int P_Floors = 0;
        static string P_Only = null;
        static int P_Limit = 0;
        static string[] P_Skip = new string[0];
        static float P_PanelMin = 1.2f;       // -panelmin：模数网格的下界（米）
        static float P_PanelMax = 2.4f;       // -panelmax：模数网格的上界（米）
        /// <summary>-paneltone：0 = v2 逐块三色轮换（棋盘，默认，用户认可版）；1 = v3 一墙一基调 + 明度微变 + 强调板。</summary>
        static int P_ToneMode = 0;
        const float FloorPanelMin = 1.5f;     // 地面分板：短边下限（米）
        const float FloorPanelMax = 4.0f;     // 地面分板：短边上限（米）
        const float FloorSeam = 0.012f;       // 地面板缝 12mm

        /// <summary>预算求解出来的「每块板螺栓颗数」（4 = 四角；3/2/1 = 按预算抽稀）。</summary>
        static int BoltPerPanel = 4;

        // ------------------------------------------------------------------ 菜单

        [MenuItem("MapSkin/立面重建/① 干跑：面清单/分组/三角预算(零写盘)")]
        public static void DryRunMenu() => DryRun();

        [MenuItem("MapSkin/立面重建/② 执行：生成包板几何并替换可见性(写盘)")]
        public static void RunMenu()
        {
            ParseArgs();
            if (!EditorUtility.DisplayDialog("立面重建",
                    $"读 {FacesPath}\n" +
                    $"把 ≥{P_MinArea:F1}㎡ 的墙面/天花换成模块化包板几何，合并进 {RootName} 根下，\n" +
                    $"mesh 资产写 {MeshDir}/，三角预算 {P_Budget:N0}。\n" +
                    $"不改 Collider / Transform / 原对象名 / 原材质；原渲染器仅在残面很少时隐藏。\n\n继续？", "执行", "取消"))
            {
                Debug.Log("[立面重建] 用户取消");
                return;
            }
            Run();
        }

        [MenuItem("MapSkin/立面重建/③ 清除：删生成件与 mesh 资产并恢复 renderer")]
        public static void ClearMenu()
        {
            ParseArgs();
            if (!EditorUtility.DisplayDialog("清除立面重建",
                    $"删除场景里的 {RootName} 根及其分组对象、删除 {MeshDir}/ 下本工具生成的 mesh 资产，\n" +
                    "并按 manifest 把上一轮被隐藏的原渲染器恢复可见。\n" +
                    "Collider / Transform / 原对象名 / 原材质不受影响。\n\n继续？", "清除", "取消"))
                return;
            Clear();
        }

        // ------------------------------------------------------------------ 入口

        /// <summary>干跑：解析面表、分类、估算三角，全程不碰场景/资产/磁盘。
        /// 批处理：-executeMethod MapSkin.FacadeRebuild.DryRun</summary>
        public static void DryRun()
        {
            ParseArgs();
            var doc = LoadDoc();
            if (doc == null) return;
            var plan = MakePlan(doc);
            var stats = new BuildStats();
            long tris = SolveBudget(plan, stats, out long structural);
            var rep = new StringBuilder();
            rep.AppendLine($"[立面重建·干跑 v2] 面表 {FacesPath}");
            rep.AppendLine($"  面总数 {doc.faces.Count}；入选 {plan.works.Count}（墙 {plan.nWall} / 天花 {plan.nCeil} / 地面 {plan.nFloor}，hero {plan.nHero}，柱面 {plan.nPillar}）");
            AppendSkipReport(rep, plan);
            rep.AppendLine($"  分组 {plan.groups.Count} 组，三角合计 {tris:N0} / 预算 {P_Budget:N0}" + BudgetNote());
            rep.AppendLine($"    结构件 {structural:N0} + 螺栓 {stats.bolts * BoltTris:N0}（{stats.bolts:N0} 颗，每板 {BoltPerPanel} 颗）");
            rep.AppendLine(StatsLine(stats));
            foreach (var g in plan.groups)
                rep.AppendLine($"    {g.key,-14} 面 {g.works.Count,5}  三角 {g.tris,10:N0}  板 {stats.GroupPanels(g.key),6}  → {MeshDir}/{g.key}.asset");
            rep.AppendLine(FloorLine(plan, stats));
            rep.AppendLine(ModulusCheck());
            rep.AppendLine("  （干跑不改场景、不建资产、不写盘）");
            Debug.Log(rep.ToString());
        }

        /// <summary>执行：生成几何 + 写 mesh 资产 + 装配场景 + 写 manifest + 存场景。
        /// 批处理：-executeMethod MapSkin.FacadeRebuild.Run</summary>
        public static void Run()
        {
            ParseArgs();
            var doc = LoadDoc();
            if (doc == null) return;

            var plan = MakePlan(doc);
            var stats = new BuildStats();
            long tris = SolveBudget(plan, stats, out long structural);
            if (plan.works.Count == 0)
            {
                Debug.LogWarning($"[立面重建] 没有入选的面（-minarea {P_MinArea:F1} / -only / -skip 把面全滤掉了？），" +
                                 $"本轮不动场景。跳过统计：{SkipSummary(plan)}");
                return;
            }
            if (tris > P_Budget)
            {
                Debug.LogError($"[立面重建] 三角超预算：{tris:N0} > {P_Budget:N0}（螺栓已降到每板 {BoltPerPanel} 颗仍不够）。" +
                               "请调高 -budget 或调高 -minarea，本轮未生成任何东西。");
                return;
            }

            if (!EnsureScene()) return;
            var scene = EditorSceneManager.GetActiveScene();

            var shader = Shader.Find(NprShaderName);
            if (shader == null)
                Debug.LogWarning($"[立面重建] 找不到 shader {NprShaderName}，材质将退回 URP/Lit 并只设能设的属性");

            // 上一轮隐藏过的 renderer 先恢复，保证重跑幂等（本轮按新口径重新裁决）
            int restored = RestoreHidden(ReadManifest()?.hidden);
            // 旧根先删（含其上挂的 mesh 引用）
            int removedRoots = RemoveRoots();

            var res = new Dictionary<string, ObjResult>();  // 组 → (mesh, 三角)
            var buildStats = new BuildStats();
            AssetDatabase.StartAssetEditing();
            try
            {
                EnsureFolder(MeshDir);
                EnsureFolder(MatDir);
                EnsureMaterials(shader);
                foreach (var g in plan.groups)
                {
                    var mb = new MeshBuilder(true);
                    BuildGroup(g, buildStats, mb);
                    var mesh = mb.ToMesh($"{RootName}_{g.key}");
                    string path = $"{MeshDir}/{g.key}.asset";
                    if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
                    AssetDatabase.CreateAsset(mesh, path);
                    res[g.key] = new ObjResult { mesh = mesh, tris = mb.TotalTris, path = path };
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
            }

            // ---- 装配场景：根 + 分组 + MeshRenderer（7 个材质槽，没有 Collider）
            var root = new GameObject(RootName);
            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;
            var matsOrder = EnsureMaterials(shader);     // 资产已落盘，这里拿的是同一批材质引用
            foreach (var kv in res.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var go = new GameObject(kv.Key);
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = kv.Value.mesh;
                var mr = go.AddComponent<MeshRenderer>();
                var arr = new Material[SubCount];
                for (int i = 0; i < SubCount; i++) arr[i] = matsOrder[i];
                mr.sharedMaterials = arr;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                mr.receiveShadows = true;
            }

            // ---- 隐藏原渲染器（逐 renderer 裁决）
            var hiddenList = new List<HiddenRenderer>();
            int hidden = P_Hide == 1 ? HideRenderers(plan, out hiddenList) : 0;

            // ---- manifest
            BuildManifest(doc, plan, res, hiddenList, buildStats, tris, structural, hidden, restored);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            var rep = new StringBuilder();
            rep.AppendLine($"[立面重建] 完成：根 {RootName}（{res.Count} 组 / {res.Count} 个 MeshRenderer）");
            rep.AppendLine($"  面 {doc.faces.Count} → 处理 {plan.works.Count}（墙 {plan.nWall} / 天花 {plan.nCeil} / 地面 {plan.nFloor}；hero {plan.nHero}；柱面 {plan.nPillar}）");
            AppendSkipReport(rep, plan);
            rep.AppendLine($"  三角 {tris:N0} / 预算 {P_Budget:N0}（结构 {structural:N0} + 螺栓 {buildStats.bolts * BoltTris:N0}）" + BudgetNote());
            rep.AppendLine(StatsLine(buildStats));
            foreach (var g in plan.groups)
            {
                var r = res[g.key];
                rep.AppendLine($"    {g.key,-14} 面 {g.works.Count,5}  三角 {r.tris,10:N0}  → {r.path}");
            }
            rep.AppendLine($"  原渲染器：隐藏 {hidden} 个" + (restored > 0 ? $"，恢复上一轮 {restored} 个" : "") +
                           (removedRoots > 0 ? $"，删旧根 {removedRoots} 个" : "") +
                           (P_Hide == 1 ? "" : "（-hide 0，本轮不隐藏）"));
            rep.AppendLine(FloorLine(plan, buildStats));
            rep.AppendLine($"  manifest → {OutPath}");
            rep.AppendLine($"  场景已存盘：{scene.path}");
            Debug.Log(rep.ToString());
        }

        /// <summary>清除：删根、删本工具 mesh 资产、恢复上一轮被隐藏的 renderer。
        /// 批处理：-executeMethod MapSkin.FacadeRebuild.Clear</summary>
        public static void Clear()
        {
            ParseArgs();
            if (!EnsureScene()) return;
            var scene = EditorSceneManager.GetActiveScene();

            var man = ReadManifest();
            int restored = RestoreHidden(man != null ? man.hidden : null);
            int removed = RemoveRoots();
            int assets = DeleteMeshes(man);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[立面重建·清除] 删除根 {removed} 个、mesh 资产 {assets} 个、恢复渲染器 {restored} 个；场景已存盘：{scene.path}");
        }

        // ------------------------------------------------------------------ 数据模型（字段名与 face_flat.json 完全一致）

        [Serializable] public class FaceDoc
        {
            public int version;
            public List<Face> faces = new List<Face>();
        }

        [Serializable] public class Face
        {
            public string id;
            public string objectPath;
            public int rendererIndex;
            public int submesh;
            public string style;
            public string shape;
            public float w;
            public float h;
            public float[] origin;
            public float[] u;
            public float[] v;
            public float[] n;
            public float[] outline;
            public float[] worldCenter;
            public string sourceMaterial;
            public float[] baseColor;
            public string seed;
            public int panelIndex;
            public int panelCount;
            public bool small;
            public int variantIndex;
            public float wallHeightM;
            public bool isWalkable;
            public string adjacentStyle;

            public bool Valid =>
                origin != null && origin.Length >= 3 &&
                u != null && u.Length >= 3 &&
                v != null && v.Length >= 3 &&
                n != null && n.Length >= 3 &&
                outline != null && outline.Length >= 6;
        }

        [Serializable] public class ManifestDoc
        {
            public int version = 2;
            public string generatedAt;
            public string scene;
            public string facesFile;
            public string root;
            public int facesTotal;
            public int facesProcessed;
            public int wallsProcessed;
            public int ceilsProcessed;
            public int floorsProcessed;
            public int heroFaces;
            public int pillarFaces;
            public long usedTris;
            public long budget;
            public int boltPerPanel;          // 4 = 四角；预算不够时降到 3/2/1
            public int boltCount;
            public int thinFaces;             // 薄高面/窄条（走结构包板，没有板场）
            public int panelFaces;            // 有大板场的面
            public int panelCount;            // 大板总数
            public int maxPanelsPerFace;
            public float maxPanelArea;        // 最大单板面积 ㎡
            public float meanPanelArea;
            public int hazardFaces;           // 用了警示色的面
            public int emissiveFaces;         // 真加了发光腰线的面
            public int accentPanels;          // 强调板总数
            public int panelToneMode;         // 0 = v2 逐块三色轮换（棋盘）；1 = 一墙一基调 + 微变 + 强调板
            public int floorFaces;            // 地面面数
            public int floorPanels;           // 地面铺板块数
            public float floorWidthMin;       // 地面板宽（短边）min/p50/max（m）
            public float floorWidthP50;
            public float floorWidthMax;
            public float toneSpreadP50;       // 同面最大明度差（含强调板）p50/p95/max（0–1）
            public float toneSpreadP95;
            public float toneSpreadMax;
            public float toneBaseSpreadP95;   // 同上，不含强调板（纯明度微变，应 ≤0.15）
            public float toneBaseSpreadMax;
            public int hiddenRenderers;
            public List<FamilyStat> families = new List<FamilyStat>();
            public List<GroupStat> groups = new List<GroupStat>();
            public List<SkipStat> skips = new List<SkipStat>();
            public List<HiddenRenderer> hidden = new List<HiddenRenderer>();
            public ParamSnap param = new ParamSnap();
        }

        [Serializable] public class FamilyStat
        {
            public string family;             // paint / metal / mixed
            public int faces;
        }

        [Serializable] public class GroupStat
        {
            public string group;
            public string block;
            public string klass;
            public int faces;
            public long tris;
            public int panels;                // 该组的板数（抽查板尺度用）
            public float maxPanelArea;
            public int floorPanels;           // 地面组：铺板块数
            public float boardWidthMin;       // 地面组：板宽（短边）min/p50/max（m）
            public float boardWidthP50;
            public float boardWidthMax;
            public string mesh;
        }

        [Serializable] public class SkipStat
        {
            public string reason;
            public int faces;
            public float area;
        }

        [Serializable] public class HiddenRenderer
        {
            public string objectPath;
            public int rendererIndex;
        }

        [Serializable] public class ParamSnap
        {
            public string scene;
            public string faces;
            public string outPath;
            public float minArea;
            public long budget;
            public float hero;
            public int hide;
            public int floors;
            public string only;
            public int limit;
            public string skip;
            public float panelMin;
            public float panelMax;
            public int boltPerPanel;
        }

        /// <summary>
        /// 生成期统计（干跑与实跑共用）：墙族 / 板数 / 最大板面积 / 螺栓 / 警示 / 发光 / 逐组板数。
        /// 干跑拿它印预估，实跑拿它写 manifest —— 两边数字同源。
        /// </summary>
        class BuildStats
        {
            public readonly int[] family = new int[3];
            public int panels;                 // 板场里的板总数
            public int panelFaces;             // 有板场的面数
            public int maxPanelsPerFace;
            public int thinFaces;
            public float maxPanelArea;
            public double panelAreaSum;
            public int heroFaces;
            public int hazard;
            public int emissive;               // 抽中发光腰线的面
            public int emissiveDrawn;          // 真落了几何的（高度落在面内才算）
            public int accentPanels;           // 强调板总数
            public int floorFaces;             // 处理的地面面数
            public int floorPanels;            // 地面铺板块数
            public readonly List<float> floorWidths = new List<float>();   // 每块地面板的短边（m）
            readonly Dictionary<string, List<float>> groupFloorWidths = new Dictionary<string, List<float>>();
            public readonly List<float> spreads = new List<float>();      // 每面「最大明度差 / 最大明度」（含强调板）
            public readonly List<float> baseSpreads = new List<float>();  // 同上，但不含强调板（纯明度微变部分）
            public long bolts;
            public double BoltAllowance = 4.0; // 预算求解出的「每板螺栓额度」（小数）
            public double boltCarry;           // 额度余数结转（保证整面总颗数贴合额度）
            readonly Dictionary<string, int> groupPanels = new Dictionary<string, int>();
            readonly Dictionary<string, float> groupMaxArea = new Dictionary<string, float>();
            public string curGroup;

            public void Reset()
            {
                panels = 0; panelFaces = 0; maxPanelsPerFace = 0; thinFaces = 0;
                maxPanelArea = 0f; panelAreaSum = 0; heroFaces = 0; hazard = 0;
                emissive = 0; emissiveDrawn = 0; accentPanels = 0; bolts = 0; boltCarry = 0;
                floorFaces = 0; floorPanels = 0;
                spreads.Clear(); baseSpreads.Clear(); floorWidths.Clear(); groupFloorWidths.Clear();
                for (int i = 0; i < 3; i++) family[i] = 0;
                groupPanels.Clear(); groupMaxArea.Clear();
            }

            /// <summary>记一面的明度差（相对最大明度）；all 含强调板，baseOnly 不含。</summary>
            public void NoteSpread(float all, float baseOnly)
            {
                spreads.Add(all);
                baseSpreads.Add(baseOnly);
            }

            public void NoteGroup(string key) => curGroup = key;

            /// <summary>一块板落进统计（板数 / 最大板面积 / 逐组板数）。</summary>
            public void NotePanel(float area)
            {
                panels++;
                panelAreaSum += area;
                if (area > maxPanelArea) maxPanelArea = area;
                if (curGroup != null)
                {
                    groupPanels[curGroup] = groupPanels.TryGetValue(curGroup, out var v) ? v + 1 : 1;
                    groupMaxArea[curGroup] = groupMaxArea.TryGetValue(curGroup, out var m) ? Mathf.Max(m, area) : area;
                }
            }

            public void NotePanelFace(int count)
            {
                panelFaces++;
                if (count > maxPanelsPerFace) maxPanelsPerFace = count;
            }

            public void NoteFloorFace() => floorFaces++;

            /// <summary>记一块地面铺板（width = 短边），同时按分组记账。</summary>
            public void NoteFloorPanel(float width)
            {
                floorPanels++;
                floorWidths.Add(width);
                if (curGroup == null) return;
                if (!groupFloorWidths.TryGetValue(curGroup, out var list))
                {
                    list = new List<float>();
                    groupFloorWidths[curGroup] = list;
                }
                list.Add(width);
            }

            public List<float> GroupFloorWidths(string group) =>
                groupFloorWidths.TryGetValue(group, out var v) ? v : null;

            public int GroupPanels(string group) => groupPanels.TryGetValue(group, out var v) ? v : 0;

            public float GroupMaxArea(string group) => groupMaxArea.TryGetValue(group, out var v) ? v : 0f;

            public float MeanPanelArea => panels > 0 ? (float)(panelAreaSum / panels) : 0f;
        }

        // ------------------------------------------------------------------ 计划（分类 / 过滤 / 分组）

        enum Klass { Wall = 0, Ceil = 1, Floor = 2 }

        /// <summary>一个待加工的面：面表条目 + 世界平面框架 + 摊平包围盒 + 分类结论。</summary>
        class Work
        {
            public Face f;
            public Frame fr;
            public float lu0, lu1, lv0, lv1, w, h, area;
            public Klass klass;
            public string block;
            public bool hero;          // 面积 ≥ -hero，加构图宏
            public bool pillar;        // 名字含 Pillar，加腰线
        }

        class Group
        {
            public string key, block, klassName;
            public List<Work> works = new List<Work>();
            public long tris;          // 干跑估算用
        }

        /// <summary>单个 renderer（objectPath + 同一对象上的下标）的面面积账，用来裁决是否隐藏。</summary>
        class RendererAgg
        {
            public string objectPath;
            public int index;
            public float total;         // 该 renderer 全部面（含被跳过的）
            public float processed;     // 本轮处理了的面
            public float leftoverMinArea; // 跳过原因是「面积 < -minarea」
            public float leftoverOther;   // 跳过原因是别的（非矩形/斜面/名字排除/-skip/-only/-limit）
        }

        class Plan
        {
            public List<Work> works = new List<Work>();
            public List<Group> groups = new List<Group>();
            public Dictionary<string, RendererAgg> renderers = new Dictionary<string, RendererAgg>();
            public Dictionary<string, int[]> skip = new Dictionary<string, int[]>();   // reason → [面数, 面积×1000]
            public int nWall, nCeil, nFloor, nHero, nPillar;
        }

        static void SkipAdd(Plan plan, string reason, float area)
        {
            if (!plan.skip.TryGetValue(reason, out var v)) { v = new int[2]; plan.skip[reason] = v; }
            v[0] += 1;
            v[1] += (int)Mathf.Round(area * 1000f);
        }

        /// <summary>renderer 台账键：objectPath + 该对象上的渲染器下标（与 FaceSkin 导出口径一致）。</summary>
        static string RKey(Face f) => RKey(f.objectPath, f.rendererIndex);

        static string RKey(string objectPath, int index) => objectPath + "#" + index.ToString(CultureInfo.InvariantCulture);

        static Plan MakePlan(FaceDoc doc)
        {
            var plan = new Plan();
            foreach (var f in doc.faces)
            {
                float area = (f.w > 0f && f.h > 0f) ? f.w * f.h : 0f;
                string key = RKey(f);
                if (!plan.renderers.TryGetValue(key, out var agg))
                {
                    agg = new RendererAgg { objectPath = f.objectPath, index = f.rendererIndex };
                    plan.renderers[key] = agg;
                }
                agg.total += area;

                string reason = RejectReason(f);
                if (reason != null)
                {
                    SkipAdd(plan, reason, area);
                    if (reason == "面积 < -minarea") agg.leftoverMinArea += area; else agg.leftoverOther += area;
                    continue;
                }

                var fr = MakeFrame(f);
                float a0, a1, b0, b1;
                OutlineExtents(f.outline, out a0, out a1, out b0, out b1);
                var wk = new Work
                {
                    f = f,
                    fr = fr,
                    lu0 = a0, lu1 = a1, lv0 = b0, lv1 = b1,
                    w = Mathf.Max(0f, a1 - a0), h = Mathf.Max(0f, b1 - b0),
                    area = area,
                    klass = Klassify(f.n),
                    block = BlockOf(f.objectPath),
                    pillar = f.objectPath.IndexOf("Pillar", StringComparison.Ordinal) >= 0,
                };
                wk.hero = wk.klass == Klass.Wall && wk.area >= P_Hero;
                if (wk.klass == Klass.Floor && P_Floors != 1)
                {
                    SkipAdd(plan, "地面(-floors 0)", area);
                    agg.leftoverOther += area;
                    continue;
                }
                plan.works.Add(wk);
                agg.processed += area;
            }

            // 确定性排序（id 序）→ -limit 截断
            plan.works.Sort((x, y) => string.CompareOrdinal(x.f.id, y.f.id));
            if (P_Limit > 0 && plan.works.Count > P_Limit)
            {
                for (int i = P_Limit; i < plan.works.Count; i++)
                {
                    var wk = plan.works[i];
                    SkipAdd(plan, "超出 -limit", wk.area);
                    var agg = plan.renderers[RKey(wk.f)];
                    agg.leftoverOther += wk.area;
                    agg.processed -= wk.area;      // 截断掉的面不算已处理，隐藏裁决口径要一致
                }
                plan.works.RemoveRange(P_Limit, plan.works.Count - P_Limit);
            }

            // 分组：区块_类别
            var byKey = new Dictionary<string, Group>();
            foreach (var wk in plan.works)
            {
                string kl = wk.klass == Klass.Wall ? "Wall" : wk.klass == Klass.Ceil ? "Ceil" : "Floor";
                string key = wk.block + "_" + kl;
                if (!byKey.TryGetValue(key, out var g))
                {
                    g = new Group { key = key, block = wk.block, klassName = kl };
                    byKey[key] = g;
                }
                g.works.Add(wk);
                if (wk.klass == Klass.Wall) plan.nWall++;
                else if (wk.klass == Klass.Ceil) plan.nCeil++;
                else plan.nFloor++;
                if (wk.hero) plan.nHero++;
                if (wk.pillar) plan.nPillar++;
            }
            plan.groups = byKey.Values.OrderBy(g => g.key, StringComparer.Ordinal).ToList();
            return plan;
        }

        /// <summary>逐面拒绝原因；null = 收下。</summary>
        static string RejectReason(Face f)
        {
            if (!f.Valid) return "数据不完整";
            string path = f.objectPath ?? "";
            for (int i = 0; i < ObjSkipTokens.Length; i++)
                if (path.IndexOf(ObjSkipTokens[i], StringComparison.Ordinal) >= 0) return "对象名排除";
            for (int i = 0; i < P_Skip.Length; i++)
                if (P_Skip[i].Length > 0 && path.IndexOf(P_Skip[i], StringComparison.Ordinal) >= 0) return "-skip 命中";
            if (!string.IsNullOrEmpty(P_Only) && path.IndexOf(P_Only, StringComparison.Ordinal) < 0) return "不在 -only 内";
            if (!string.Equals(f.shape, "rect", StringComparison.Ordinal)) return "shape 非 rect";
            float ny = f.n[1];
            if (!(Mathf.Abs(ny) < WallNyAbs) && !(ny < CeilNy) && !(ny > FloorNy)) return "斜面临时跳过";
            if (f.w * f.h < P_MinArea) return "面积 < -minarea";
            return null;
        }

        static Klass Klassify(float[] n)
        {
            float ny = n[1];
            if (Mathf.Abs(ny) < WallNyAbs) return Klass.Wall;
            return ny < CeilNy ? Klass.Ceil : Klass.Floor;
        }

        static Frame MakeFrame(Face f) => new Frame
        {
            o = new Vector3(f.origin[0], f.origin[1], f.origin[2]),
            u = new Vector3(f.u[0], f.u[1], f.u[2]).normalized,
            v = new Vector3(f.v[0], f.v[1], f.v[2]).normalized,
            n = new Vector3(f.n[0], f.n[1], f.n[2]).normalized,
        };

        /// <summary>摊平多边形的 (u,v) 包围盒（rect 面就是 ±w/2 / ±h/2）。</summary>
        static void OutlineExtents(float[] outline, out float u0, out float u1, out float v0, out float v1)
        {
            u0 = v0 = float.MaxValue;
            u1 = v1 = float.MinValue;
            for (int i = 0; i + 1 < outline.Length; i += 2)
            {
                float a = outline[i], b = outline[i + 1];
                if (a < u0) u0 = a;
                if (a > u1) u1 = a;
                if (b < v0) v0 = b;
                if (b > v1) v1 = b;
            }
            if (u1 < u0) { u0 = u1 = 0f; }
            if (v1 < v0) { v0 = v1 = 0f; }
        }

        static string BlockOf(string objectPath)
        {
            if (!string.IsNullOrEmpty(objectPath))
            {
                var m = BlockRe.Match(objectPath);
                if (m.Success) return m.Value;
            }
            return "S_XX";
        }

        // ------------------------------------------------------------------ 预算

        /// <summary>
        /// 预算求解：先把「结构件」量出来，再把剩下的预算折成**每板螺栓额度**（小数，逐面发牌：
        /// 每板 4 颗 = 四角，2 颗 = 对角，1 颗 = 居中）。额度在 [0, 4] 上二分，取「装得进预算的
        /// 最大额度」，预算用满而不浪费；额度归零还超预算就交给调用方停止（用户口径：先抽稀再停）。
        /// 每次试算都是「只计数不存顶点」跑一遍，几何函数与正式生成同一套，不存在估算漂移。
        /// </summary>
        static long SolveBudget(Plan plan, BuildStats stats, out long structural)
        {
            stats.BoltAllowance = 4.0;                       // 先量四角全给的结构件与满配总量
            long tris = Tally(plan, stats, out structural);
            BoltPerPanel = 4;
            if (tris <= P_Budget) return tris;

            long best = tris, bestStruct = structural;
            double lo = 0.0, hi = 4.0;
            for (int iter = 0; iter < 8; iter++)
            {
                double mid = (lo + hi) * 0.5;
                if (hi - lo < 0.05) break;
                stats.BoltAllowance = mid;
                long t = Tally(plan, stats, out long s);
                if (t <= P_Budget)
                {
                    best = t; bestStruct = s; lo = mid;
                    BoltPerPanel = Mathf.Clamp(Mathf.RoundToInt((float)mid), 0, 4);
                }
                else hi = mid;
            }
            structural = bestStruct;
            BoltPerPanel = Mathf.Max(BoltPerPanel, 1);

            // 收尾：用最终额度再量一遍，让日志/manifest 的数字与实际生成逐位一致
            stats.BoltAllowance = (float)Mathf.Clamp((float)lo, 0.05f, 4f);
            long finalTris = Tally(plan, stats, out long finalStruct);
            if (finalTris <= P_Budget)
            {
                best = finalTris;
                structural = finalStruct;
                BoltPerPanel = Mathf.Clamp(Mathf.RoundToInt((float)stats.BoltAllowance), 0, 4);
                BoltPerPanel = Mathf.Max(BoltPerPanel, 1);
            }
            return best;
        }

        static long Tally(Plan plan, BuildStats stats, out long structural)
        {
            long all = 0, bolts = 0;
            if (stats != null) stats.Reset();
            for (int gi = 0; gi < plan.groups.Count; gi++)
            {
                var mb = new MeshBuilder(false);
                BuildGroup(plan.groups[gi], stats, mb);
                plan.groups[gi].tris = mb.TotalTris;
                all += mb.TotalTris;
                bolts += mb.BoltTris;
            }
            structural = all - bolts;
            return all;
        }

        /// <summary>每面发多少颗螺栓（每板几颗）：额度 × 本面板数 + 上次余数 = 这一面该有几颗，
        /// 再折成「每板几颗」的整数（0–4），差额结转给下一面 —— 整场平均密度贴合额度，
        /// 而每一面都是规整的四角/对角/单颗，不会出现「前几块有螺栓、后面一颗没有」。</summary>
        static int PerFaceBolts(BuildStats st, int panels)
        {
            if (st == null) return Mathf.Clamp(BoltPerPanel, 0, 4);
            panels = Mathf.Max(1, panels);
            double want = st.boltCarry + st.BoltAllowance * panels;                 // 这一面该有几颗
            int per = Mathf.Clamp(Mathf.RoundToInt((float)(want / panels)), 0, 4);   // 折成每板几颗
            st.boltCarry = want - per * panels;
            if (st.boltCarry > 8.0) st.boltCarry = 8.0;
            else if (st.boltCarry < -8.0) st.boltCarry = -8.0;
            return Mathf.Min(per, Mathf.Max(1, BoltCapPerFace / panels));
        }

        static void BuildGroup(Group g, BuildStats stats, MeshBuilder mb)
        {
            if (stats != null) stats.NoteGroup(g.key);
            for (int i = 0; i < g.works.Count; i++)
            {
                var wk = g.works[i];
                mb.SetFrame(wk.fr);
                if (wk.klass == Klass.Wall)
                {
                    long boltLeft = BoltCapPerFace;
                    BuildWall(mb, wk, stats, ref boltLeft, out _);
                }
                else if (wk.klass == Klass.Ceil) BuildCeil(mb, wk);
                else BuildFloor(mb, wk, stats);
            }
        }

        // ------------------------------------------------------------------ 墙面

        /// <summary>
        /// 墙面 v2：背板（暗槽底）+ 模数网格大板场（1.2–2.4m 格、seed 合并出 2–4m 大板、板缝 14–20mm、
        /// 板厚 0.03–0.06 抖动、正面 45° 倒角 8–12mm、≥2.5㎡ 的板加内缩 60mm 的 8mm 浅压边线、四角螺栓）
        /// + 檐口/踢脚/转角肋 + 墙族材质（paint/metal/mixed）+ Hazard 踢脚带 / Emissive 腰线 + hero 构图宏。
        /// </summary>
        static void BuildWall(MeshBuilder mb, Work wk, BuildStats stats, ref long boltLeft, out int panelCount)
        {
            var fr = wk.fr;
            var rng = new Rng(SeedOf(wk.f.seed, "wall"));
            float lu0 = wk.lu0, lu1 = wk.lu1, lv0 = wk.lv0, lv1 = wk.lv1;
            float W = wk.w, H = wk.h;
            panelCount = 0;
            long boltStart = boltLeft;

            // 板场边距：<3m 的面用小边距，否则模数网格铺不下
            float margin = Mathf.Min(W, H) < 3f
                ? Mathf.Min(0.10f, 0.12f * Mathf.Min(W, H))
                : 0.20f;
            float fu0 = lu0 + margin, fu1 = lu1 - margin, fv0 = lv0 + margin, fv1 = lv1 - margin;
            bool thin = H < 1.2f || (fu1 - fu0) < 0.6f || (fv1 - fv0) < 0.6f;

            // ---- 编辑决策：抽签顺序固定（墙族 → 警示踢脚 → 发光腰线 → hero 宏 → 板缝/倒角 → 网格 → 合并），
            //      预算阶梯换档重算时同一面的构图完全一致。
            int family = FamilyOf(wk.f, rng);
            bool hazard = rng.Next() < 0.33f;
            bool emissive = rng.Next() < 0.25f;
            if (stats != null) { stats.family[family]++; if (hazard) stats.hazard++; if (emissive) stats.emissive++; }

            int cols = 1, rows = 1;
            float mu = 0f, mv = 0f;
            var rects = new List<PanelRect>();
            bool mIsland = false, mBand = false, mRing = false, mPerf = false;
            float bandLv0 = 0f, bandLv1 = 0f;
            if (wk.hero && !thin)
            {
                int pickCount = 2 + rng.NextInt(2);            // 2–3 个宏
                var order = Shuffle4(rng);
                for (int i = 0; i < pickCount; i++)
                {
                    if (order[i] == 0) mIsland = true;
                    else if (order[i] == 1) mBand = true;
                    else if (order[i] == 2) mRing = true;
                    else mPerf = true;
                }
                if (mBand)
                {
                    float fh = fv1 - fv0;
                    float c = Mathf.Clamp(rng.Range(0.25f, 0.65f), 0.25f, 0.65f);
                    bandLv1 = Mathf.Clamp(fv0 + c * fh, fv0 + 0.20f, fv1 - 0.20f);
                    bandLv0 = Mathf.Max(fv0 + 0.05f, bandLv1 - 0.30f);
                    bandLv1 = Mathf.Min(fv1 - 0.05f, bandLv0 + 0.30f);
                    if (bandLv1 - bandLv0 < 0.12f) mBand = false;
                }
            }
            float seam = rng.Range(SeamMin, SeamMax);
            float bevel = rng.Range(PanelBevelMin, PanelBevelMax);
            if (!thin)
            {
                float target = rng.Range(P_PanelMin * 1.08f, P_PanelMax * 0.83f);
                Grid(fu1 - fu0, fv1 - fv0, target, out cols, out rows, out mu, out mv);
                rects = MergeCells(cols, rows, mu, mv, fu0, fv0, seam, rng);
                if (mBand) DropBandPanels(rects, bandLv0, bandLv1);   // 槽道带处的板让开
                panelCount = rects.Count;
                if (stats != null) stats.NotePanelFace(rects.Count);
            }

            // ---- 背板（薄高面的通长槽 / hero 的横向槽道要在背板上挖开）
            bool verticalThin = thin && H > W;      // 窄高条：压条竖着走，也就不做横槽
            var bands = new List<Band>();
            if (thin && !verticalThin)
            {
                int n = 1 + rng.NextInt(2);                    // 1–2 条通长槽
                for (int i = 0; i < n; i++)
                {
                    float centre = Mathf.Lerp(lv0 + 0.12f * H, lv1 - 0.12f * H, (i + 1f) / (n + 1f));
                    bands.Add(new Band
                    {
                        lu0 = lu0, lu1 = lu1,
                        lv0 = centre - 0.025f, lv1 = centre + 0.025f,
                        depth = 0.02f, fieldOnly = false, sub = SubTrim,
                    });
                }
            }
            else if (mBand)
            {
                bands.Add(new Band
                {
                    lu0 = fu0, lu1 = fu1, lv0 = bandLv0, lv1 = bandLv1,
                    depth = 0.03f, fieldOnly = true, sub = SubBase,
                });
            }
            float maxDepth = 0f;
            for (int i = 0; i < bands.Count; i++) maxDepth = Mathf.Max(maxDepth, bands[i].depth);
            float back = Surf - PlateT - Mathf.Max(0f, maxDepth - (PlateT - PlateMinRemain));
            AddBacking(mb, fr, lu0, lu1, lv0, lv1, back, Surf, bands, SubBase);

            // hero 槽道两端端板（槽的左右尽头各一块，把槽封住）
            if (mBand)
            {
                AddChamferBox(mb, fr, SubTrim, fu0, fu0 + 0.05f, bandLv0, bandLv1, back, Surf + PanelT + 0.006f, 0.008f);
                AddChamferBox(mb, fr, SubTrim, fu1 - 0.05f, fu1, bandLv0, bandLv1, back, Surf + PanelT + 0.006f, 0.008f);
            }

            // ---- 大板场 或 结构包板
            float ribW = H >= 0.8f ? 0.10f : 0f;
            if (thin)
            {
                BuildThinClad(mb, fr, rng, lu0, lu1, lv0, lv1, W, H, ribW, stats, ref boltLeft);
            }
            else
            {
                int islandIdx = mIsland && rects.Count > 0 ? rng.NextInt(rects.Count) : -1;
                // 两套板面配色都留着（用独立随机流，不受螺栓额度影响）：
                //   -paneltone 0（默认）= v2 逐块三色轮换（棋盘，用户认可版）
                //   -paneltone 1        = v3 一面一基调 + 逐板明度微变 + 互不相邻的强调板
                int[] panelSubs = P_ToneMode == 1
                    ? AssignTones(rects, family, new Rng(SeedOf(wk.f.seed, "tone")), stats)
                    : LegacySubs(rects, family, stats);
                // 每面螺栓颗数：由预算额度发牌（额度是小数，逐面进整、余数结转），再受整面上限压
                int perFace = PerFaceBolts(stats, rects.Count);
                for (int i = 0; i < rects.Count; i++)
                {
                    var rc = rects[i];
                    float t = i == islandIdx ? 0.10f : PanelT + rng.Range(-PanelTJitter, PanelTJitter);
                    float front = Surf + t;
                    float bv = Mathf.Min(bevel, 0.30f * Mathf.Min(Mathf.Min(rc.W, rc.H), t));
                    AddChamferBox(mb, fr, panelSubs[i], rc.u0, rc.u1, rc.v0, rc.v1, Surf - 0.005f, front, bv);
                    if (rc.Area >= EdgeLineMinArea) AddEdgeLine(mb, fr, rc, front);
                    if (i == islandIdx) AddIslandTrim(mb, fr, rc, front);
                    AddPanelBolts(mb, fr, rc, front, perFace, rng, ref boltLeft);
                    if (stats != null) stats.NotePanel(rc.Area);
                }
                // 宏压条坐在「标称板面」上（板厚 ±15mm 抖动：都不至于浮空或齐平）
                if (mRing) AddRing(mb, fr, fu0, fu1, fv0, fv1, Surf + PanelT);
                if (mPerf) AddPerfBand(mb, fr, rng, fu0, fu1, fv0, fv1, Surf + PanelT, stats, ref boltLeft);
            }

            // ---- 檐口 / 踢脚 / 转角肋（结构件，贯通全宽）
            // eps：压条与背板侧面、压条与压条之间都错开 2mm——重叠共面是 z-fighting 的源头。
            const float eps = 0.002f;
            float trimBv = Mathf.Max(bevel, 0.010f);
            if (H >= 1.4f)
            {
                AddChamferBox(mb, fr, SubTrim, lu0, lu1, lv1 - 0.14f, lv1, Surf - 0.01f, Surf + 0.08f, trimBv);   // 顶部檐口 0.14×0.09
                float kickH = hazard ? rng.Range(0.18f, 0.22f) : 0.18f;                                          // 警示带面踢脚 0.18–0.22m
                AddChamferBox(mb, fr, hazard ? SubHazard : SubTrim, lu0, lu1, lv0, lv0 + kickH, Surf - 0.01f, Surf + 0.06f, trimBv);
            }
            if (ribW > 0f)
            {
                AddChamferBox(mb, fr, SubTrim, lu0 + eps, lu0 + eps + ribW, lv0, lv1, Surf - 0.01f, Surf + 0.04f, trimBv);  // 转角肋
                AddChamferBox(mb, fr, SubTrim, lu1 - eps - ribW, lu1 - eps, lv0, lv1, Surf - 0.01f, Surf + 0.04f, trimBv);
            }

            // ---- 发光腰线：离地 2.2–2.6m 一条 0.06m 高通长条，嵌在板缝里（不超出板面）
            if (emissive)
            {
                float lvT = WaistLv(fr, lv0, lv1, rng, rects);
                float eu0 = lu0 + eps + ribW, eu1 = lu1 - eps - ribW;
                if (lvT > lv0 + 0.15f && lvT < lv1 - 0.15f && eu1 - eu0 > 0.20f)
                {
                    AddPlainBox(mb, fr, SubEmissive, eu0, eu1, lvT - 0.03f, lvT + 0.03f, Surf - 0.002f, Surf + 0.008f);
                    if (stats != null) stats.emissiveDrawn++;
                }
            }

            // ---- 柱：每 3m 一圈腰线（落在两条转角肋之间，免得与肋共面）
            if (wk.pillar)
            {
                float wu0 = lu0 + eps + ribW, wu1 = lu1 - eps - ribW;
                if (wu1 - wu0 > 0.10f)
                    for (float lv = lv0 + 3f; lv < lv1 - 0.20f; lv += 3f)
                        AddChamferBox(mb, fr, SubTrim, wu0, wu1, lv - 0.03f, lv + 0.03f, Surf - 0.01f, Surf + 0.03f, trimBv);
            }

            if (stats != null)
            {
                if (thin) stats.thinFaces++;
                if (wk.hero && !thin) stats.heroFaces++;
                stats.bolts += boltStart - boltLeft;
            }
        }

        /// <summary>墙族：同区块多数面同族（75% 跟随区块，25% 自己抽），保证同类地形一眼看出是一类。</summary>
        static int FamilyOf(Face f, Rng rng)
        {
            int blockFam = (int)(Fnv1a("fam|" + BlockOf(f.objectPath)) % 3u);
            return rng.Next() < 0.75f ? blockFam : rng.NextInt(3);
        }

        /// <summary>
        /// 一面墙一个基调色 + 逐板明度微变 + 少量强调板（v3，修「板面明暗棋盘」）：
        ///   · 基调色 = 墙族决定（paint→亮漆 / metal→深金属 / mixed→中漆），同面所有板默认都用它，
        ///     墙族差异只体现在「整面气质」上，不再逐块换色；
        ///   · 明度微变：每块板在 5 档（0.90/0.95/1.00/1.05/1.10）里挑，且与**已定的相邻板**最多差 2 档
        ///     （0.10/0.90 = 11.1% ≤ 15% 上限），所以相邻板绝不会有黑白跳变；
        ///   · 强调板：最多占本面 20%，seed 挑；跳到另一种基调色（亮漆 ↔ 深金属，中漆脸由 seed 二选一），
        ///     且**互不相邻** —— 与已选强调板共享边就跳过这块、换下一块。
        /// 结果写进 subs（每块板用哪个子网格），并把「同面最大明度差」记进 stats 供抽查。
        /// </summary>
        static int[] AssignTones(List<PanelRect> rects, int family, Rng rng, BuildStats stats)
        {
            int n = rects.Count;
            var subs = new int[n];
            if (n == 0) return subs;

            int tone = FamilyTone[Mathf.Clamp(family, 0, 2)];
            int accentTone = tone == 0 ? 2 : 0;                       // 亮漆 ↔ 深金属
            if (tone == 1) accentTone = rng.Next() < 0.5f ? 0 : 2;    // 中漆脸：seed 二选一

            var level = new int[n];
            var isAccent = new bool[n];
            for (int i = 0; i < n; i++) level[i] = ToneBaseLevel;

            // 本面明度窗口：整面只在一个「3 档窗口」里浮动（0.90–1.00 / 0.95–1.05 / 1.00–1.10），
            // 于是「同面最大明度差」≤ 0.10/0.90 = 11.1%，一眼看过去仍是一整片同材质。
            int faceCentre = 1 + rng.NextInt(3);
            int winLo = Mathf.Clamp(faceCentre - 1, 0, ToneLevels - 1);
            int winHi = Mathf.Clamp(faceCentre + 1, 0, ToneLevels - 1);

            // 1) 明度档：按洗牌顺序逐块定，只看已定的邻居（局部差 ≤ 2 档，双保险）
            var order = new List<int>(n);
            for (int i = 0; i < n; i++) order.Add(i);
            for (int i = n - 1; i > 0; i--)
            {
                int j = rng.NextInt(i + 1);
                int t = order[i]; order[i] = order[j]; order[j] = t;
            }
            var assigned = new bool[n];
            for (int oi = 0; oi < n; oi++)
            {
                int i = order[oi];
                int lo = winLo, hi = winHi;
                for (int j = 0; j < n; j++)
                {
                    if (j == i || !assigned[j] || !Adjacent(rects[i], rects[j])) continue;
                    lo = Mathf.Max(lo, level[j] - (int)ToneAdjacentMaxSteps);
                    hi = Mathf.Min(hi, level[j] + (int)ToneAdjacentMaxSteps);
                }
                int pick = hi >= lo ? Mathf.Clamp(lo + rng.NextInt(hi - lo + 1), 0, ToneLevels - 1) : ToneBaseLevel;
                level[i] = pick;
                assigned[i] = true;
            }

            // 2) 强调板：每面按 seed 抽一个 0–20% 的比例（不是每面都顶满），互不相邻
            float accRate = rng.Range(0f, AccentMaxRatio);
            int maxAccent = Mathf.FloorToInt(n * accRate);
            int acc = 0;
            for (int oi = 0; oi < n && acc < maxAccent; oi++)
            {
                int i = order[oi];
                bool clash = false;
                for (int j = 0; j < n && !clash; j++)
                    if (isAccent[j] && Adjacent(rects[i], rects[j])) clash = true;
                if (clash) continue;
                isAccent[i] = true;
                acc++;
            }

            // 3) 子网格 + 同面明度差（相对最大明度）
            float maxL = 0f, minL = float.MaxValue, maxB = 0f, minB = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                subs[i] = isAccent[i] ? SubOfTone(accentTone, ToneBaseLevel) : SubOfTone(tone, level[i]);
                float lum = Luminance(MatColors[subs[i]]);
                if (lum > maxL) maxL = lum;
                if (lum < minL) minL = lum;
                if (!isAccent[i])
                {
                    if (lum > maxB) maxB = lum;
                    if (lum < minB) minB = lum;
                }
            }
            if (stats != null)
            {
                stats.accentPanels += acc;
                if (maxL > 1e-5f) stats.NoteSpread((maxL - minL) / maxL, (maxB - minB) / Mathf.Max(1e-5f, maxB));
            }
            return subs;
        }

        /// <summary>
        /// v2 的板面配色（-paneltone 0，默认）：按「墙族 + 格位散列」逐块轮换三色 ——
        /// mixed 面是 PaintLight/PaintMid/MetalDark 三色按格位循环（用户认可的棋盘读法），
        /// paint 面以亮漆为主掺 20% 中漆，metal 面以深金属为主掺 20% 中漆。
        /// 配色只用格位散列、不消耗随机流，所以预算阶梯换档时构图与配色都不漂。
        /// </summary>
        static int[] LegacySubs(List<PanelRect> rects, int family, BuildStats stats)
        {
            int n = rects.Count;
            var subs = new int[n];
            float maxL = 0f, minL = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                subs[i] = LegacyPanelSub(family, rects[i]);
                float lum = Luminance(MatColors[subs[i]]);
                if (lum > maxL) maxL = lum;
                if (lum < minL) minL = lum;
            }
            if (stats != null && n >= 2 && maxL > 1e-5f)
                stats.NoteSpread((maxL - minL) / maxL, (maxL - minL) / maxL);   // 本模式没有「微变/强调」之分
            return subs;
        }

        static int LegacyPanelSub(int family, PanelRect rc)
        {
            int h = (rc.row * 7 + rc.col * 13) % 10;
            switch (family)
            {
                case 0: return h < 2 ? SubOfTone(1, ToneBaseLevel) : SubOfTone(0, ToneBaseLevel);
                case 1: return h < 2 ? SubOfTone(1, ToneBaseLevel) : SubOfTone(2, ToneBaseLevel);
                default:
                    int k = ((rc.row + rc.col) % 3 + 3) % 3;
                    return k == 0 ? SubOfTone(0, ToneBaseLevel)
                         : k == 1 ? SubOfTone(1, ToneBaseLevel)
                         : SubOfTone(2, ToneBaseLevel);
            }
        }

        static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        /// <summary>两块板是否共享边（板缝 14–20mm，容差 50mm）；对角相接不算相邻。</summary>
        static bool Adjacent(PanelRect a, PanelRect b)
        {
            float uOverlap = Mathf.Min(a.u1, b.u1) - Mathf.Max(a.u0, b.u0);
            float vOverlap = Mathf.Min(a.v1, b.v1) - Mathf.Max(a.v0, b.v0);
            bool vTouch = Mathf.Abs(a.v0 - b.v1) < AdjacentTol || Mathf.Abs(b.v0 - a.v1) < AdjacentTol;
            bool uTouch = Mathf.Abs(a.u0 - b.u1) < AdjacentTol || Mathf.Abs(b.u0 - a.u1) < AdjacentTol;
            return (uOverlap > 0.02f && vTouch) || (vOverlap > 0.02f && uTouch);
        }

        /// <summary>升序取分位（p ∈ [0,1]）——日志/manifest 里的 p50/p95/max 都用它。</summary>
        static float Percentile(List<float> values, float p)
        {
            if (values == null || values.Count == 0) return 0f;
            var s = new List<float>(values);
            s.Sort();
            int i = Mathf.Clamp(Mathf.RoundToInt(p * (s.Count - 1)), 0, s.Count - 1);
            return s[i];
        }

        /// <summary>发光腰线的高度（面内 lv）：按世界 Y 2.2–2.6m 取，并贴到 0.25m 内最近的横向板缝。</summary>
        static float WaistLv(Frame fr, float lv0, float lv1, Rng rng, List<PanelRect> rects)
        {
            float worldY = rng.Range(2.2f, 2.6f);
            float lvT = Mathf.Abs(fr.v.y) > 0.5f ? (worldY - fr.o.y) / fr.v.y : Mathf.Lerp(lv0, lv1, 0.35f);
            float best = lvT, bestD = 0.25f;
            for (int i = 0; i < rects.Count; i++)
            {
                float d = Mathf.Abs(rects[i].v0 - lvT);
                if (d < bestD) { bestD = d; best = rects[i].v0; }
                d = Mathf.Abs(rects[i].v1 - lvT);
                if (d < bestD) { bestD = d; best = rects[i].v1; }
            }
            return best;
        }

        /// <summary>
        /// 薄高面（h &lt; 1.2m，典型是梁侧面/压顶；另含「模数网格塞不下」的窄条面）：不做板场，改做结构包板——
        /// 两条通长压条（0.06×0.05，45° 倒角）+ 中间 1–2 条 0.05 宽 / 凹 0.02 的通长槽（只有横放的面做槽：
        /// 槽在背板上挖开，槽壁由相邻背板段的侧面天然形成）+ 沿压条每 0.6m 一颗螺栓。
        /// 窄高条（W &lt; H，例如 0.3×39m 的竖鳍）把压条改竖着走：通长方向跟着长轴，读作竖梁/竖鳍。
        /// </summary>
        static void BuildThinClad(MeshBuilder mb, Frame fr, Rng rng, float lu0, float lu1, float lv0, float lv1,
            float W, float H, float ribW, BuildStats stats, ref long boltLeft)
        {
            // 预算额度也管压条螺栓：额度 4 颗/板 → 0.6m；额度 2 → 1.2m（同步拉稀）
            float allow = stats == null ? 4f : Mathf.Clamp((float)stats.BoltAllowance, 0.2f, 4f);
            float spacing = 0.60f * 4f / allow;
            bool vertical = H > W;
            float insetU = Mathf.Max(0.03f, 0.002f + ribW);                 // 从转角肋内侧起，避免共面
            if (vertical)
            {
                float insetV = Mathf.Max(0.03f, 0.002f + ribW);
                float barW = Mathf.Min(0.06f, 0.30f * W);
                float b0 = lu0 + insetU, b1 = lu1 - insetU;
                if (b1 - b0 >= barW * 2f + 0.02f)
                {
                    AddChamferBox(mb, fr, SubTrim, b0, b0 + barW, lv0 + insetV, lv1 - insetV, Surf - 0.01f, Surf + 0.04f, 0.012f);
                    AddChamferBox(mb, fr, SubTrim, b1 - barW, b1, lv0 + insetV, lv1 - insetV, Surf - 0.01f, Surf + 0.04f, 0.012f);
                    AddBoltLine(mb, fr, lv0 + insetV + 0.02f, lv1 - insetV - 0.02f, b0 + barW * 0.5f, false, Surf + 0.04f, spacing, rng, ref boltLeft);
                    AddBoltLine(mb, fr, lv0 + insetV + 0.02f, lv1 - insetV - 0.02f, b1 - barW * 0.5f, false, Surf + 0.04f, spacing, rng, ref boltLeft);
                }
                return;
            }
            float barH = Mathf.Min(0.06f, 0.30f * H);
            // 上下通长压条：截面 0.06（高）× 0.05（深），嵌入背板 10mm
            AddChamferBox(mb, fr, SubTrim, lu0 + insetU, lu1 - insetU, lv0, lv0 + barH, Surf - 0.01f, Surf + 0.04f, 0.012f);
            AddChamferBox(mb, fr, SubTrim, lu0 + insetU, lu1 - insetU, lv1 - barH, lv1, Surf - 0.01f, Surf + 0.04f, 0.012f);
            AddBoltLine(mb, fr, lu0 + insetU + 0.02f, lu1 - insetU - 0.02f, lv0 + barH * 0.5f, true, Surf + 0.04f, spacing, rng, ref boltLeft);
            AddBoltLine(mb, fr, lu0 + insetU + 0.02f, lu1 - insetU - 0.02f, lv1 - barH * 0.5f, true, Surf + 0.04f, spacing, rng, ref boltLeft);
        }

        // ------------------------------------------------------------------ v2 模数网格 + 合并

        /// <summary>
        /// 选模数：在 [panelmin, panelmax] 里找一组 (cols, rows)，使两个方向的格边长都落在这个区间、
        /// 且都最接近目标值 —— 格数取整、余量均摊到各格（等价于把余量均摊到边格）。
        /// 找不到（典型是高度只有 1m 出头的矮带面：行高必然低于模数下限）就退回「按目标模数取整」，
        /// 让矮面也铺成一条条板，而不是整面一块。
        /// </summary>
        static void Grid(float W, float H, float target, out int cols, out int rows, out float mu, out float mv)
        {
            cols = 0; rows = 0; mu = W; mv = H;
            float best = float.MaxValue;
            for (float m = P_PanelMin; m <= P_PanelMax + 1e-4f; m += 0.02f)
            {
                int c = Mathf.Max(1, Mathf.RoundToInt(W / m));
                int r = Mathf.Max(1, Mathf.RoundToInt(H / m));
                float cu = W / c, cv = H / r;
                if (cu < P_PanelMin - 1e-3f || cu > P_PanelMax + 1e-3f) continue;
                if (cv < P_PanelMin - 1e-3f || cv > P_PanelMax + 1e-3f) continue;
                float s = Mathf.Abs(cu - target) + Mathf.Abs(cv - target);
                if (s < best) { best = s; cols = c; rows = r; mu = cu; mv = cv; }
            }
            if (cols == 0)
            {
                cols = Mathf.Max(1, Mathf.RoundToInt(W / target));
                rows = Mathf.Max(1, Mathf.RoundToInt(H / target));
                mu = W / cols;
                mv = H / rows;
            }
        }

        /// <summary>
        /// 铺满格 → 合并成大板：
        ///   第 1 遍「随机合并」：逐格按 seed 决定要不要并成 2×1 / 1×2 / 2×2 / 3×1 …（面积上限 9㎡），
        ///     既留 1m 级小板也出 2–4m 级大板 —— 绝大多数面走这条，构图有随机变化；
        ///   第 2 遍（仅当板数 &gt; 60）：「按行铺条 + 隔行错半块」的砖缝排布，把板数解到 60 以内
        ///     （条长一格一格加、不够再加行高，最后兜底整面一块），保证大面也守得住上限。
        /// </summary>
        static List<PanelRect> MergeCells(int cols, int rows, float mu, float mv, float fu0, float fv0, float seam, Rng rng)
        {
            float cell = mu * mv;
            int softMax = Mathf.Max(1, Mathf.FloorToInt(PanelSoftAreaCap / cell + 1e-4f));
            var rects = MergePass(cols, rows, mu, mv, fu0, fv0, seam, rng, softMax);
            if (rects.Count > PanelCapPerFace)
                rects = StripTiling(cols, rows, mu, mv, fu0, fv0, seam, rng);
            return rects;
        }

        static List<PanelRect> MergePass(int cols, int rows, float mu, float mv, float fu0, float fv0, float seam,
            Rng rng, int maxCells)
        {
            var shapes = new List<int[]>();                       // {dc, dr, cells}，按格数从小到大
            for (int dc = 1; dc <= cols && dc <= 12; dc++)
                for (int dr = 1; dr <= rows && dr <= 12; dr++)
                    if (dc * dr <= maxCells) shapes.Add(new[] { dc, dr, dc * dr });
            shapes.Sort((a, b) => a[2] != b[2] ? a[2].CompareTo(b[2]) : a[0].CompareTo(b[0]));

            var order = new List<int>();                          // 洗牌格序（确定性）
            for (int i = 0; i < cols * rows; i++) order.Add(i);
            for (int i = order.Count - 1; i > 0; i--)
            {
                int j = rng.NextInt(i + 1);
                int t = order[i]; order[i] = order[j]; order[j] = t;
            }

            var used = new bool[rows, cols];
            var avail = new List<int[]>();
            var outRects = new List<PanelRect>();
            float half = seam * 0.5f;
            for (int oi = 0; oi < order.Count; oi++)
            {
                int r = order[oi] / cols, c = order[oi] % cols;
                if (used[r, c]) continue;

                avail.Clear();
                for (int si = 0; si < shapes.Count; si++)
                {
                    int dc = shapes[si][0], dr = shapes[si][1];
                    if (dc == 1 && dr == 1) continue;
                    if (r + dr > rows || c + dc > cols) continue;
                    bool free = true;
                    for (int y = 0; y < dr && free; y++)
                        for (int x = 0; x < dc; x++)
                            if (used[r + y, c + x]) { free = false; break; }
                    if (free) avail.Add(shapes[si]);
                }

                int pick = -1;
                if (avail.Count > 0 && rng.Next() < PanelMergeChance)
                    pick = rng.NextInt(Mathf.Min(avail.Count, 4));     // 偏向小块，保证小板也有

                int dc2 = 1, dr2 = 1;
                if (pick >= 0)
                {
                    dc2 = avail[pick][0]; dr2 = avail[pick][1];
                    for (int y = 0; y < dr2; y++)
                        for (int x = 0; x < dc2; x++) used[r + y, c + x] = true;
                }
                else used[r, c] = true;

                outRects.Add(new PanelRect
                {
                    u0 = fu0 + c * mu + half, u1 = fu0 + (c + dc2) * mu - half,
                    v0 = fv0 + r * mv + half, v1 = fv0 + (r + dr2) * mv - half,
                    row = r, col = c, cells = dc2 * dr2,
                });
            }
            return outRects;
        }

        /// <summary>
        /// 按行铺条 + 隔行错半块的砖缝排布：二维搜出一块最小面积的「条」（tileW 格宽 × tileH 格高，
        /// 先横后竖），使总块数 ≤ 单面上限；找不到就退回整面一块。隔行错位让竖缝错开，
        /// 读起来是工业包板而不是网格纸（条宽已经铺满整面时不位移，免得把一行切成两块）。
        /// </summary>
        static List<PanelRect> StripTiling(int cols, int rows, float mu, float mv, float fu0, float fv0, float seam, Rng rng)
        {
            int bestW = cols, bestH = rows;
            long bestCells = long.MaxValue;
            bool canOffsetAny = true;
            for (int h = 1; h <= rows; h++)
            {
                for (int w = 1; w <= cols; w++)
                {
                    bool off = (w * mu) < cols * mu * 0.75f;               // 条宽铺满整面时不位移
                    long perRow = Mathf.CeilToInt(cols / (float)w) + (off ? 1 : 0);   // 错位行会多出半块
                    long cnt = (long)Mathf.CeilToInt(rows / (float)h) * perRow;
                    if (cnt > PanelCapPerFace) continue;
                    long cells = (long)w * h;
                    // 先比面积；面积相同取更「板条」的比例（宽 ≥ 高）
                    bool better = cells < bestCells || (cells == bestCells && w >= h && bestW < bestH);
                    if (better) { bestCells = cells; bestW = w; bestH = h; canOffsetAny = off; }
                }
            }
            int tileW = Mathf.Max(1, bestW), tileH = Mathf.Max(1, bestH);
            var outRects = new List<PanelRect>();
            float half = seam * 0.5f;
            float fieldW = cols * mu;
            float strip = Mathf.Min(tileW, cols) * mu;                 // 条宽（米）
            bool canOffset = canOffsetAny && strip < fieldW * 0.75f;
            int rowIdx = 0;
            for (int r0 = 0; r0 < rows; r0 += tileH, rowIdx++)
            {
                float v0 = fv0 + r0 * mv, v1 = fv0 + Mathf.Min(rows, r0 + tileH) * mv;
                if (v1 - v0 < 0.05f) continue;
                float shift = (canOffset && (rowIdx & 1) == 1) ? 0.5f * strip : 0f;
                float u = fu0 - shift;
                int col = 0;
                while (u < fu0 + fieldW - 0.02f)
                {
                    float uA = Mathf.Max(fu0, u), uB = Mathf.Min(fu0 + fieldW, u + strip);
                    u += strip;
                    if (uB - uA < 0.05f) { col++; continue; }
                    outRects.Add(new PanelRect
                    {
                        u0 = uA + half, u1 = uB - half,
                        v0 = v0 + half, v1 = v1 - half,
                        row = r0, col = col,
                        cells = Mathf.Max(1, Mathf.RoundToInt((uB - uA) / mu) * Mathf.Min(tileH, rows - r0)),
                    });
                    col++;
                }
            }
            if (outRects.Count == 0)                                        // 兜底：整面一块
                outRects.Add(new PanelRect
                {
                    u0 = fu0 + half, u1 = fu0 + fieldW - half,
                    v0 = fv0 + half, v1 = fv0 + rows * mv - half,
                    row = 0, col = 0, cells = cols * rows,
                });
            return outRects;
        }

        /// <summary>hero 槽道：让开槽带范围内的板（槽底由背板挖出来的凹面充当）。</summary>
        static void DropBandPanels(List<PanelRect> rects, float bandLv0, float bandLv1)
        {
            rects.RemoveAll(rc => rc.v0 < bandLv1 && rc.v1 > bandLv0);
        }

        /// <summary>内缩 60mm、0.02m 宽、凸 8mm 的浅压边线：4 条从板面斜升到 8mm 的梯形带（8 三角）。</summary>
        static void AddEdgeLine(MeshBuilder mb, Frame fr, PanelRect rc, float front)
        {
            float o = EdgeLineInset, w = EdgeLineWidth;
            float a0 = rc.u0 + o, a1 = rc.u1 - o, b0 = rc.v0 + o, b1 = rc.v1 - o;
            if (a1 - a0 < 0.10f || b1 - b0 < 0.10f) return;
            float c0 = a0 + w, c1 = a1 - w, d0 = b0 + w, d1 = b1 - w;
            if (c1 - c0 < 0.02f || d1 - d0 < 0.02f) return;
            float lo = front, hi = front + EdgeLineRise;
            AddQuadP(mb, SubTrim, fr, a0, b0, lo, a1, b0, lo, c1, d0, hi, c0, d0, hi, fr.n);   // 下带
            AddQuadP(mb, SubTrim, fr, a0, b1, lo, c0, d1, hi, c1, d1, hi, a1, b1, lo, fr.n);   // 上带
            AddQuadP(mb, SubTrim, fr, a0, b0, lo, a0, b1, lo, c0, d1, hi, c0, d0, hi, fr.n);   // 左带
            AddQuadP(mb, SubTrim, fr, a1, b0, lo, c1, d0, hi, c1, d1, hi, a1, b1, lo, fr.n);   // 右带
        }

        /// <summary>每块板四角各一颗螺栓（角内缩 60mm）；预算抽稀时按「对角 → 单颗」递减，整面 ≤120 颗。</summary>
        static void AddPanelBolts(MeshBuilder mb, Frame fr, PanelRect rc, float ln, int per, Rng rng, ref long left)
        {
            if (left <= 0) return;
            per = Mathf.Clamp(per, 0, 4);
            if (per <= 0) return;
            if (per >= 2 && rc.W > 2f * BoltInset + 0.05f && rc.H > 2f * BoltInset + 0.05f)
            {
                float u0 = rc.u0 + BoltInset, u1 = rc.u1 - BoltInset;
                float v0 = rc.v0 + BoltInset, v1 = rc.v1 - BoltInset;
                Put(mb, fr, u0, v0, ln, rng, ref left);                 // 先给对角，再补四角
                if (per >= 4) Put(mb, fr, u0, v1, ln, rng, ref left);
                if (per >= 2) Put(mb, fr, u1, v1, ln, rng, ref left);
                if (per >= 3) Put(mb, fr, u1, v0, ln, rng, ref left);
            }
            else
            {
                // 单颗（额度抽稀到底）或板太小：给板心一颗
                Put(mb, fr, (rc.u0 + rc.u1) * 0.5f, (rc.v0 + rc.v1) * 0.5f, ln, rng, ref left);
            }
        }

        static void Put(MeshBuilder mb, Frame fr, float lu, float lv, float ln, Rng rng, ref long left)
        {
            if (left <= 0) return;
            AddBolt(mb, fr, lu, lv, ln, rng);
            left--;
        }

        /// <summary>岛式收边条：贴着某块大板四边的一圈 0.04 宽压条（凸出板面 12mm）。</summary>
        static void AddIslandTrim(MeshBuilder mb, Frame fr, PanelRect rc, float panelFront)
        {
            const float w = 0.02f;   // 半宽 → 通宽 0.04
            float t = panelFront + 0.012f;
            AddChamferBox(mb, fr, SubTrim, rc.u0 + w, rc.u1 - w, rc.v0 - w, rc.v0 + w, Surf - 0.005f, t, 0.010f);
            AddChamferBox(mb, fr, SubTrim, rc.u0 + w, rc.u1 - w, rc.v1 - w, rc.v1 + w, Surf - 0.005f, t, 0.010f);
            AddChamferBox(mb, fr, SubTrim, rc.u0 - w, rc.u0 + w, rc.v0 - w, rc.v1 + w, Surf - 0.005f, t, 0.010f);
            AddChamferBox(mb, fr, SubTrim, rc.u1 - w, rc.u1 + w, rc.v0 - w, rc.v1 + w, Surf - 0.005f, t, 0.010f);
        }

        /// <summary>框套框：板场内套一圈 0.05×0.05 压条（坐在板面上，凸 0.05）。</summary>
        static void AddRing(MeshBuilder mb, Frame fr, float fu0, float fu1, float fv0, float fv1, float panelFront)
        {
            const float hw = 0.025f;
            float iu0 = fu0 + 0.05f, iu1 = fu1 - 0.05f, iv0 = fv0 + 0.05f, iv1 = fv1 - 0.05f;
            if (iu1 - iu0 < 0.30f || iv1 - iv0 < 0.30f) return;
            float back = panelFront, t = back + 0.05f;
            AddChamferBox(mb, fr, SubTrim, iu0 + hw, iu1 - hw, iv0 - hw, iv0 + hw, back, t, 0.010f);
            AddChamferBox(mb, fr, SubTrim, iu0 + hw, iu1 - hw, iv1 - hw, iv1 + hw, back, t, 0.010f);
            AddChamferBox(mb, fr, SubTrim, iu0 - hw, iu0 + hw, iv0 - hw, iv1 + hw, back, t, 0.010f);
            AddChamferBox(mb, fr, SubTrim, iu1 - hw, iu1 + hw, iv0 - hw, iv1 + hw, back, t, 0.010f);
        }

        /// <summary>穿孔带：板场靠上一条 0.08 高压条，沿线每 0.12m 一颗螺栓（预算抽稀时同步拉稀）。</summary>
        static void AddPerfBand(MeshBuilder mb, Frame fr, Rng rng, float fu0, float fu1, float fv0, float fv1,
            float panelFront, BuildStats stats, ref long boltLeft)
        {
            float lv = Mathf.Lerp(fv0, fv1, 0.72f);
            lv = Mathf.Clamp(lv, fv0 + 0.05f, fv1 - 0.13f);
            AddChamferBox(mb, fr, SubTrim, fu0 + 0.05f, fu1 - 0.05f, lv, lv + 0.08f, panelFront, panelFront + 0.05f, 0.010f);
            float allow = stats == null ? 4f : Mathf.Clamp((float)stats.BoltAllowance, 0.2f, 4f);
            float sp = 0.12f * 4f / allow;
            AddBoltLine(mb, fr, fu0 + 0.08f, fu1 - 0.08f, lv + 0.04f, true, panelFront + 0.05f, sp, rng, ref boltLeft);
        }

        // ------------------------------------------------------------------ 天花

        /// <summary>天花：背板 + 井格（格内凹 0.07，格间肋 0.12 宽凸 0.05，四周圈梁 0.12×0.10）。</summary>
        static void BuildCeil(MeshBuilder mb, Work wk)
        {
            var fr = wk.fr;
            float lu0 = wk.lu0, lu1 = wk.lu1, lv0 = wk.lv0, lv1 = wk.lv1;
            float W = wk.w, H = wk.h;
            int n = Mathf.Max(W, H) > 6f ? 3 : 2;
            float pu = W / n, pv = H / n;
            float cellFront = Surf - 0.07f;
            float back = cellFront - 0.008f;
            // 肋/圈梁从格板内部起（不是从可见面起）：否则肋与格板之间有 0.07 深的空缝，
            // 掠射角能看进去（读成破洞）。
            float ribBack = cellFront - 0.006f;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    AddPlainBox(mb, fr, SubOfTone(1, ToneBaseLevel),
                        lu0 + i * pu, lu0 + (i + 1) * pu, lv0 + j * pv, lv0 + (j + 1) * pv, back, cellFront);
            // 内部肋
            for (int i = 1; i < n; i++)
                AddChamferBox(mb, fr, SubTrim, lu0 + i * pu - 0.06f, lu0 + i * pu + 0.06f, lv0, lv1, ribBack, Surf + 0.05f, 0.010f);
            for (int j = 1; j < n; j++)
                AddChamferBox(mb, fr, SubTrim, lu0, lu1, lv0 + j * pv - 0.06f, lv0 + j * pv + 0.06f, ribBack, Surf + 0.05f, 0.010f);
            // 四周圈梁
            float bw = Mathf.Min(0.12f, 0.25f * Mathf.Min(W, H));
            AddChamferBox(mb, fr, SubTrim, lu0, lu1, lv0, lv0 + bw, ribBack, Surf + 0.10f, 0.010f);
            AddChamferBox(mb, fr, SubTrim, lu0, lu1, lv1 - bw, lv1, ribBack, Surf + 0.10f, 0.010f);
            AddChamferBox(mb, fr, SubTrim, lu0, lu0 + bw, lv0 + bw, lv1 - bw, ribBack, Surf + 0.10f, 0.010f);
            AddChamferBox(mb, fr, SubTrim, lu1 - bw, lu1, lv0 + bw, lv1 - bw, ribBack, Surf + 0.10f, 0.010f);
        }

        // ------------------------------------------------------------------ 地面（-floors 1）

        /// <summary>
        /// 地面（-floors 1）：0.012 厚铺板（顶面 = 原面 +0.004），按**面的原始轮廓**分板 ——
        /// 行深 1.5–4.0m、行内板宽 1.5–4.0m 各自独立（seed 定），板缝不贯通、允许 T 形接头，
        /// 读起来是白模那种一块块铺板，而不是等模数格网。缝宽 12mm；板严格贴住轮廓
        /// （四边各内缩半个缝 = 6mm，绝不外扩）；周边 0.10m 包边凸 0.03m；
        /// 底衬用 Base —— 缝里露石墨。绝不改碰撞。
        /// </summary>
        static void BuildFloor(MeshBuilder mb, Work wk, BuildStats stats)
        {
            var fr = wk.fr;
            var rng = new Rng(SeedOf(wk.f.seed, "floor"));
            float lu0 = wk.lu0, lu1 = wk.lu1, lv0 = wk.lv0, lv1 = wk.lv1;
            float W = wk.w, H = wk.h;
            int boardSub = SubOfTone(2, ToneBaseLevel);
            if (stats != null) stats.NoteFloorFace();

            AddPlainBox(mb, fr, SubBase, lu0, lu1, lv0, lv1, -0.014f, -0.008f);   // 底衬：缝里露的就是它

            bool alongU = W >= H;                       // 板长方向跟长轴走
            float mainLen = alongU ? W : H;             // 行内分板方向
            float crossLen = alongU ? H : W;            // 行的进深方向
            float half = FloorSeam * 0.5f;
            var rows = RandomCuts(crossLen, FloorPanelMin, FloorPanelMax, rng);
            for (int r = 0; r + 1 < rows.Count; r++)
            {
                float c0 = (alongU ? lv0 : lu0) + rows[r];
                float c1 = (alongU ? lv0 : lu0) + rows[r + 1];
                if (c1 - c0 < 0.10f) continue;
                // 每一行独立分板 → 竖缝不贯通，天然出现 T 形接头
                var cuts = RandomCuts(mainLen, FloorPanelMin, FloorPanelMax, rng);
                for (int i = 0; i + 1 < cuts.Count; i++)
                {
                    float a0 = (alongU ? lu0 : lv0) + cuts[i] + half;
                    float a1 = (alongU ? lu0 : lv0) + cuts[i + 1] - half;
                    float b0 = c0 + half, b1 = c1 - half;
                    if (a1 - a0 < 0.06f || b1 - b0 < 0.06f) continue;
                    if (alongU) AddPlainBox(mb, fr, boardSub, a0, a1, b0, b1, -0.008f, 0.004f);
                    else AddPlainBox(mb, fr, boardSub, b0, b1, a0, a1, -0.008f, 0.004f);
                    if (stats != null) stats.NoteFloorPanel(a1 - a0);   // 板宽 = 行内那块板的宽度
                }
            }

            float bw = Mathf.Min(0.10f, 0.25f * Mathf.Min(W, H));
            AddChamferBox(mb, fr, SubTrim, lu0, lu1, lv0, lv0 + bw, -0.008f, 0.030f, 0.010f);
            AddChamferBox(mb, fr, SubTrim, lu0, lu1, lv1 - bw, lv1, -0.008f, 0.030f, 0.010f);
            AddChamferBox(mb, fr, SubTrim, lu0, lu0 + bw, lv0 + bw, lv1 - bw, -0.008f, 0.030f, 0.010f);
            AddChamferBox(mb, fr, SubTrim, lu1 - bw, lu1, lv0 + bw, lv1 - bw, -0.008f, 0.030f, 0.010f);
        }

        /// <summary>
        /// 把 [0,total] 按 [minW,maxW] 随机切段（地面分板用）：末段兜底保证不剩碎条，
        /// 整段不够一格时直接一块。切点全部落在轮廓内 → 铺板永远不越界。
        /// </summary>
        static List<float> RandomCuts(float total, float minW, float maxW, Rng rng)
        {
            var cuts = new List<float> { 0f };
            float pos = 0f;
            for (int guard = 0; guard < 512 && total - pos > maxW; guard++)
            {
                float w = rng.Range(minW, maxW);
                float remain = total - pos - w;
                if (remain < minW) w = total - pos - minW;      // 给最后一块留够 minW
                if (w < minW * 0.5f) break;                     // 太碎就并入最后一块
                pos += w;
                cuts.Add(pos);
            }
            cuts.Add(total);
            return cuts;
        }

        // ------------------------------------------------------------------ 背板 / 槽带

        /// <summary>背板：按槽带把 lv 切成若干段（槽带处没有背板材料），槽底另补一块凹下去的板。
        /// 槽壁由相邻背板段的侧面天然形成，不必再补墙。</summary>
        static void AddBacking(MeshBuilder mb, Frame fr, float lu0, float lu1, float lv0, float lv1,
            float back, float front, List<Band> bands, int sub)
        {
            var sorted = bands.OrderBy(b => b.lv0).ToList();
            float cur = lv0;
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].lv0 - cur > 0.002f) AddPlainBox(mb, fr, sub, lu0, lu1, cur, sorted[i].lv0, back, front);
                cur = sorted[i].lv1;
            }
            if (lv1 - cur > 0.002f) AddPlainBox(mb, fr, sub, lu0, lu1, cur, lv1, back, front);
            for (int i = 0; i < sorted.Count; i++)
            {
                var b = sorted[i];
                if (b.fieldOnly)
                {
                    if (b.lu0 - lu0 > 0.002f) AddPlainBox(mb, fr, sub, lu0, b.lu0, b.lv0, b.lv1, back, front);
                    if (lu1 - b.lu1 > 0.002f) AddPlainBox(mb, fr, sub, b.lu1, lu1, b.lv0, b.lv1, back, front);
                }
                AddFaceQuad(mb, fr, b.sub, b.lu0, b.lu1, b.lv0, b.lv1, front - b.depth, fr.n);
            }
        }

        // ------------------------------------------------------------------ 螺栓

        /// <summary>一条边上的螺栓：实际间距锁在 [0.45, 0.70]（len 已扣掉两端内缩），用给薄高面压条。</summary>
        static void AddBoltLine(MeshBuilder mb, Frame fr, float a0, float a1, float b, bool alongU,
            float ln, float spacing, Rng rng, ref long left)
        {
            if (left <= 0) return;
            float len = a1 - a0;
            if (len <= 0.05f) return;
            int n = Mathf.Max(1, Mathf.FloorToInt(len / Mathf.Clamp(spacing, 0.05f, 5f)));
            while (n > 1 && len / (n - 1) > BoltMaxGap) n++;
            while (n > 1 && len / (n - 1) < BoltMinGap) n--;
            if (n > left) n = (int)left;
            for (int i = 0; i < n; i++)
            {
                float t = n == 1 ? 0.5f : (float)i / (n - 1);
                float a = a0 + len * t;
                if (alongU) AddBolt(mb, fr, a, b, ln, rng);
                else AddBolt(mb, fr, b, a, ln, rng);
            }
            left -= n;
        }

        static readonly Vector3[] BoltBase = new Vector3[6];
        static readonly Vector3[] BoltTop = new Vector3[6];

        /// <summary>六棱柱螺栓：6 个侧面（12 三角）+ 六角顶盖（4 三角）= 16 三角，硬边 flat 法线。
        /// UV 给 0–1 岛（螺栓是独立小件，逐颗一副 UV，不参与板面的 2m 周期）。</summary>
        static void AddBolt(MeshBuilder mb, Frame fr, float lu, float lv, float ln, Rng rng)
        {
            const float uvc = 0.5f, uvr = 0.4f;
            float rot = rng.Range(0f, Mathf.PI / 3f);
            float rTop = BoltR * 0.9f;                    // 头面微收，读作螺栓头的倒角
            Vector3 c0 = fr.P(lu, lv, ln);
            var ub = new Vector2[6];
            var ut = new Vector2[6];
            for (int k = 0; k < 6; k++)
            {
                float a = rot + k * (Mathf.PI / 3f);
                float cu = Mathf.Cos(a), cv = Mathf.Sin(a);
                BoltBase[k] = fr.P(lu + cu * BoltR, lv + cv * BoltR, ln);
                BoltTop[k] = fr.P(lu + cu * rTop, lv + cv * rTop, ln + BoltH);
                ub[k] = new Vector2(uvc + cu * uvr, uvc + cv * uvr);
                ut[k] = new Vector2(uvc + cu * uvr * 0.9f, uvc + cv * uvr * 0.9f);
            }
            for (int k = 0; k < 6; k++)
            {
                int k2 = (k + 1) % 6;
                Vector3 want = (BoltBase[k] + BoltBase[k2]) * 0.5f - c0;
                want = want.sqrMagnitude > 1e-10f ? want.normalized : fr.u;
                mb.AddQuadUv(SubTrim, BoltBase[k], BoltBase[k2], BoltTop[k2], BoltTop[k], want, ub[k], ub[k2], ut[k2], ut[k]);
            }
            Vector3 cTop = fr.P(lu, lv, ln + BoltH);
            var uc = new Vector2(uvc, uvc);
            for (int k = 0; k < 6; k++)
                mb.AddTriUv(SubTrim, cTop, BoltTop[k], BoltTop[(k + 1) % 6], fr.n, uc, ut[k], ut[(k + 1) % 6]);
            mb.BoltTris += BoltTris;
        }

        // ------------------------------------------------------------------ 几何基元

        /// <summary>面的世界平面框架：(lu, lv) 在面内，(ln) 沿法线（n 指向房间内侧，故 +ln = 外凸）。</summary>
        struct Frame
        {
            public Vector3 o, u, v, n;
            public Vector3 P(float lu, float lv, float ln) => o + u * lu + v * lv + n * ln;
        }

        struct Band
        {
            public float lu0, lu1, lv0, lv1, depth;
            public bool fieldOnly;   // true = 只在板场宽度上挖（两侧留背板当端板）
            public int sub;          // 槽底用哪个子网格
        }

        /// <summary>一块大板（格子合并后）：u0..u1 / v0..v1 已扣板缝；cells = 占了几格。</summary>
        struct PanelRect
        {
            public float u0, u1, v0, v1;
            public int row, col, cells;
            public float W => u1 - u0;
            public float H => v1 - v0;
            public float Area => W * H;
        }

        /// <summary>
        /// 顶点累加器。Store=false 时只计数（预算估算用同一套几何代码，不存在估算漂移）；
        /// 所有顶点按面拆开、法线用调用方给的朝向 → 硬边 flat，绝不平滑。
        /// </summary>
        class MeshBuilder
        {
            readonly bool store;
            readonly List<Vector3> pos = new List<Vector3>(4096);   // 顶点流是**全组共用**的，子网格只分索引
            readonly List<Vector3> nor = new List<Vector3>(4096);
            readonly List<Vector2> uv = new List<Vector2>(4096);
            readonly List<int>[] tri;
            readonly int[] subTris = new int[SubCount];
            Frame frame;

            public int BoltTris;

            public MeshBuilder(bool store)
            {
                this.store = store;
                tri = new List<int>[SubCount];
                for (int i = 0; i < SubCount; i++) tri[i] = new List<int>(2048);
            }

            public void SetFrame(Frame f) => frame = f;

            public int TotalTris
            {
                get
                {
                    int n = 0;
                    for (int i = 0; i < SubCount; i++) n += subTris[i];
                    return n;
                }
            }

            public void AddTri(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 want)
            {
                if (!store) { subTris[sub] += 1; return; }
                AddTriUv(sub, a, b, c, want, UvOf(a), UvOf(b), UvOf(c));
            }

            /// <summary>显式 UV 版本（螺栓一类要 0–1 岛的独立小件用）。</summary>
            public void AddTriUv(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 want, Vector2 ua, Vector2 ub, Vector2 uc)
            {
                if (!store) { subTris[sub] += 1; return; }
                var nv = want.sqrMagnitude > 1e-10f ? want.normalized : Vector3.up;
                int i = pos.Count;
                pos.Add(a); pos.Add(b); pos.Add(c);
                nor.Add(nv); nor.Add(nv); nor.Add(nv);
                uv.Add(ua); uv.Add(ub); uv.Add(uc);
                tri[sub].Add(i); tri[sub].Add(i + 1); tri[sub].Add(i + 2);
                subTris[sub] += 1;
            }

            /// <summary>四点按环序给出，绕向自动按 want 矫正（右手叉积对齐 want 朝外）。</summary>
            public void AddQuad(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 want)
            {
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), want) < 0f)
                {
                    var t = b; b = d; d = t;
                }
                AddTri(sub, a, b, c, want);
                AddTri(sub, a, c, d, want);
            }

            /// <summary>显式 UV 版本：绕向翻转时 UV 一起跟着换位，避免贴图被镜像。</summary>
            public void AddQuadUv(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 want,
                Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
            {
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), want) < 0f)
                {
                    var t = b; b = d; d = t;
                    var tu = ub; ub = ud; ud = tu;
                }
                AddTriUv(sub, a, b, c, want, ua, ub, uc);
                AddTriUv(sub, a, c, d, want, ua, uc, ud);
            }

            /// <summary>UV0 按米制：面内坐标 / UvBase（默认 2m 周期），所以板与板之间贴图是连续的。</summary>
            Vector2 UvOf(Vector3 p)
            {
                Vector3 d = p - frame.o;
                return new Vector2(Vector3.Dot(d, frame.u), Vector3.Dot(d, frame.v)) / UvBase;
            }

            public Mesh ToMesh(string name)
            {
                var mesh = new Mesh { name = name };
                if (pos.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(pos);
                mesh.SetNormals(nor);
                mesh.SetUVs(0, uv);
                mesh.subMeshCount = SubCount;
                for (int i = 0; i < SubCount; i++) mesh.SetTriangles(tri[i], i, false);
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        /// <summary>面内矩形方块（lu0..lu1 × lv0..lv1 × ln0..ln1）；bevel &gt; 0 时正面 4 棱切 45°，20 三角，否则 12 三角。</summary>
        static void AddChamferBox(MeshBuilder mb, Frame fr, int sub,
            float lu0, float lu1, float lv0, float lv1, float ln0, float ln1, float bevel)
        {
            if (lu1 - lu0 < 0.002f || lv1 - lv0 < 0.002f || ln1 - ln0 < 0.001f) return;
            float bw = Mathf.Min(bevel, Mathf.Min(lu1 - lu0, lv1 - lv0) * 0.49f);
            bw = Mathf.Min(bw, (ln1 - ln0) * 0.49f);
            if (bw < 0.0015f)
            {
                AddPlainBox(mb, fr, sub, lu0, lu1, lv0, lv1, ln0, ln1);
                return;
            }
            float i0 = lu0 + bw, i1 = lu1 - bw, j0 = lv0 + bw, j1 = lv1 - bw, bf = ln1 - bw;

            AddQuadP(mb, sub, fr, lu0, lv0, ln0, lu1, lv0, ln0, lu1, lv1, ln0, lu0, lv1, ln0, -fr.n);   // 背面
            AddQuadP(mb, sub, fr, i0, j0, ln1, i1, j0, ln1, i1, j1, ln1, i0, j1, ln1, fr.n);            // 正面（内缩）
            AddQuadP(mb, sub, fr, lu0, lv0, ln0, lu0, lv1, ln0, lu0, lv1, bf, lu0, lv0, bf, -fr.u);      // 侧面 -u
            AddQuadP(mb, sub, fr, lu1, lv0, ln0, lu1, lv0, bf, lu1, lv1, bf, lu1, lv1, ln0, fr.u);       // 侧面 +u
            AddQuadP(mb, sub, fr, lu0, lv0, ln0, lu0, lv0, bf, lu1, lv0, bf, lu1, lv0, ln0, -fr.v);      // 侧面 -v
            AddQuadP(mb, sub, fr, lu0, lv1, ln0, lu1, lv1, ln0, lu1, lv1, bf, lu0, lv1, bf, fr.v);       // 侧面 +v
            AddQuadP(mb, sub, fr, lu0, lv0, bf, lu0, lv1, bf, i0, j1, ln1, i0, j0, ln1, (-fr.u + fr.n).normalized);
            AddQuadP(mb, sub, fr, lu1, lv0, bf, i1, j0, ln1, i1, j1, ln1, lu1, lv1, bf, (fr.u + fr.n).normalized);
            AddQuadP(mb, sub, fr, lu0, lv0, bf, i0, j0, ln1, i1, j0, ln1, lu1, lv0, bf, (-fr.v + fr.n).normalized);
            AddQuadP(mb, sub, fr, lu0, lv1, bf, lu1, lv1, bf, i1, j1, ln1, i0, j1, ln1, (fr.v + fr.n).normalized);
        }

        /// <summary>面内长方体（6 面 12 三角）。</summary>
        static void AddPlainBox(MeshBuilder mb, Frame fr, int sub,
            float lu0, float lu1, float lv0, float lv1, float ln0, float ln1)
        {
            if (lu1 - lu0 < 0.002f || lv1 - lv0 < 0.002f || ln1 - ln0 < 0.001f) return;
            AddQuadP(mb, sub, fr, lu0, lv0, ln0, lu1, lv0, ln0, lu1, lv1, ln0, lu0, lv1, ln0, -fr.n);
            AddQuadP(mb, sub, fr, lu0, lv0, ln1, lu1, lv0, ln1, lu1, lv1, ln1, lu0, lv1, ln1, fr.n);
            AddQuadP(mb, sub, fr, lu0, lv0, ln0, lu0, lv1, ln0, lu0, lv1, ln1, lu0, lv0, ln1, -fr.u);
            AddQuadP(mb, sub, fr, lu1, lv0, ln0, lu1, lv0, ln1, lu1, lv1, ln1, lu1, lv1, ln0, fr.u);
            AddQuadP(mb, sub, fr, lu0, lv0, ln0, lu0, lv0, ln1, lu1, lv0, ln1, lu1, lv0, ln0, -fr.v);
            AddQuadP(mb, sub, fr, lu0, lv1, ln0, lu1, lv1, ln0, lu1, lv1, ln1, lu0, lv1, ln1, fr.v);
        }

        /// <summary>面内单面四边形（朝 +n 或 -n，用于槽底等薄片）。</summary>
        static void AddFaceQuad(MeshBuilder mb, Frame fr, int sub, float lu0, float lu1, float lv0, float lv1, float ln, Vector3 want)
        {
            AddQuadP(mb, sub, fr, lu0, lv0, ln, lu1, lv0, ln, lu1, lv1, ln, lu0, lv1, ln, want);
        }

        /// <summary>4 个角点显式给出（环序）的四边形，绕向按 want 自动矫正。</summary>
        static void AddQuadP(MeshBuilder mb, int sub, Frame fr,
            float a0, float a1, float a2, float b0, float b1, float b2,
            float c0, float c1, float c2, float d0, float d1, float d2, Vector3 want)
        {
            mb.AddQuad(sub, fr.P(a0, a1, a2), fr.P(b0, b1, b2), fr.P(c0, c1, c2), fr.P(d0, d1, d2), want);
        }

        // ------------------------------------------------------------------ 材质

        /// <summary>7 个立面材质：已存在则复用（幂等），否则新建。不用 Material Variant。</summary>
        static Material[] EnsureMaterials(Shader shader)
        {
            EnsureFolder(MatDir);                        // 目录不存在时 CreateAsset 会直接抛 UnityException
            var mats = new Material[SubCount];
            var fallback = Shader.Find("Universal Render Pipeline/Lit");
            for (int i = 0; i < SubCount; i++)
            {
                string path = $"{MatDir}/{MatNames[i]}.mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null)
                {
                    m = new Material(shader != null ? shader : fallback);
                    AssetDatabase.CreateAsset(m, path);
                }
                if (shader != null && m.shader != shader) m.shader = shader;
                SetIf(m, "_BaseColor", MatColors[i]);
                SetFloatIf(m, "_Metallic", MatMetallic[i]);
                SetFloatIf(m, "_Smoothness", MatSmooth[i]);
                SetIf(m, "_ShadowColor", ShadowColor);
                SetFloatIf(m, "_ToonSoftness", ToonSoftness);
                SetFloatIf(m, "_ToonThreshold", ToonThreshold);
                if (i == SubEmissive)
                {
                    // 冷白发光：NPRBase 用 _EmissionEnabled 开关 + _EMISSION 关键字
                    m.EnableKeyword("_EMISSION");
                    SetIf(m, "_EmissionColor", MatColors[i] * EmissiveStrength);
                    SetFloatIf(m, "_EmissionEnabled", 1f);
                    SetFloatIf(m, "_EmissionUseMap", 0f);
                    SetFloatIf(m, "_EmissionIntensity", EmissiveStrength);
                }
                EditorUtility.SetDirty(m);
                mats[i] = m;
            }
            return mats;
        }

        /// <summary>shader 有该属性才写；没有就跳过（只在第一次遇到时说明一次）。</summary>
        static void SetIf(Material m, string prop, Color v)
        {
            if (m.HasProperty(prop)) m.SetColor(prop, v);
            else if (!MissingLogged.Contains(prop)) { MissingLogged.Add(prop); Debug.Log($"[立面重建] shader 无属性 {prop}，已跳过（其余属性照设）"); }
        }

        static void SetFloatIf(Material m, string prop, float v)
        {
            if (m.HasProperty(prop)) m.SetFloat(prop, v);
            else if (!MissingLogged.Contains(prop)) { MissingLogged.Add(prop); Debug.Log($"[立面重建] shader 无属性 {prop}，已跳过（其余属性照设）"); }
        }

        static readonly HashSet<string> MissingLogged = new HashSet<string>();

        // ------------------------------------------------------------------ 场景

        static bool EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return true;
            if (cur.IsValid() && cur.isDirty)
            {
                if (string.IsNullOrEmpty(cur.path)) { /* batchmode 起的未命名空场景：无需保存 */ }
                else EditorSceneManager.SaveScene(cur);
            }
            var opened = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!opened.IsValid())
            {
                Debug.LogError($"[立面重建] 打开场景失败：{ScenePath}");
                return false;
            }
            Debug.Log($"[立面重建] 已打开场景 {ScenePath}");
            return true;
        }

        /// <summary>删掉本工具的根（连同它下面的分组对象）。返回删掉的根数。</summary>
        static int RemoveRoots()
        {
            var doomed = new List<GameObject>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                if (t == null) continue;
                if (!t.name.StartsWith(RootName, StringComparison.Ordinal)) continue;
                if (t.parent != null) continue;    // 只认场景根，防误删
                doomed.Add(t.gameObject);
            }
            foreach (var go in doomed) UnityEngine.Object.DestroyImmediate(go);
            return doomed.Count;
        }

        /// <summary>逐 renderer 裁决隐藏：全部面都处理了，或未处理残面 &lt; 15% 且都是 -minarea 以下的碎面。
        /// 只有真的关掉了才记进 hiddenList（Clear 按它恢复）。</summary>
        static int HideRenderers(Plan plan, out List<HiddenRenderer> hiddenList)
        {
            hiddenList = new List<HiddenRenderer>();
            int n = 0;
            foreach (var agg in plan.renderers.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Value))
            {
                if (agg.total <= 0.0001f) continue;
                bool allDone = agg.processed >= agg.total - 0.0001f;
                bool tinyOnly = agg.leftoverOther <= 0.0001f && agg.leftoverMinArea < 0.15f * agg.total;
                if (!allDone && !tinyOnly) continue;

                var go = FindByPath(agg.objectPath);
                if (go == null) continue;                     // 对象不在场景里（面表过期）：不动
                var rs = go.GetComponents<Renderer>();        // 与 FaceSkin 导出时同口径：本 GameObject 上的序号
                if (agg.index < 0 || agg.index >= rs.Length)
                {
                    Debug.LogWarning($"[立面重建] {agg.objectPath} 没有第 {agg.index} 个 Renderer，跳过隐藏");
                    continue;
                }
                var r = rs[agg.index];
                if (r == null) continue;
                hiddenList.Add(new HiddenRenderer { objectPath = agg.objectPath, rendererIndex = agg.index });
                if (!r.enabled) continue;
                r.enabled = false;
                n++;
            }
            return n;
        }

        /// <summary>按 manifest 记录把上一轮隐藏的 renderer 恢复可见。</summary>
        static int RestoreHidden(List<HiddenRenderer> list)
        {
            if (list == null || list.Count == 0) return 0;
            int n = 0;
            foreach (var h in list)
            {
                if (h == null) continue;
                var go = FindByPath(h.objectPath);
                if (go == null) continue;
                var rs = go.GetComponents<Renderer>();
                if (h.rendererIndex < 0 || h.rendererIndex >= rs.Length) continue;
                var r = rs[h.rendererIndex];
                if (r == null || r.enabled) continue;
                r.enabled = true;
                n++;
            }
            return n;
        }

        static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var go = GameObject.Find(path);
            if (go != null) return go;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                if (t == null) continue;
                if (ObjectPath(t) == path) return t.gameObject;
            }
            return null;
        }

        static string ObjectPath(Transform t)
        {
            var parts = new List<string>();
            while (t != null) { parts.Add(t.name); t = t.parent; }
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ------------------------------------------------------------------ manifest

        static ManifestDoc BuildManifest(FaceDoc doc, Plan plan, Dictionary<string, ObjResult> res,
            List<HiddenRenderer> hiddenList, BuildStats stats, long tris, long structural, int hidden, int restored)
        {
            var m = new ManifestDoc
            {
                generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                scene = ScenePath,
                facesFile = FacesPath,
                root = RootName,
                facesTotal = doc.faces.Count,
                facesProcessed = plan.works.Count,
                wallsProcessed = plan.nWall,
                ceilsProcessed = plan.nCeil,
                floorsProcessed = plan.nFloor,
                heroFaces = stats.heroFaces,
                pillarFaces = plan.nPillar,
                usedTris = tris,
                budget = P_Budget,
                boltPerPanel = BoltPerPanel,
                boltCount = (int)stats.bolts,
                thinFaces = stats.thinFaces,
                panelFaces = stats.panelFaces,
                panelCount = stats.panels,
                maxPanelsPerFace = stats.maxPanelsPerFace,
                maxPanelArea = stats.maxPanelArea,
                meanPanelArea = stats.MeanPanelArea,
                hazardFaces = stats.hazard,
                emissiveFaces = stats.emissiveDrawn,
                accentPanels = stats.accentPanels,
                panelToneMode = P_ToneMode,
                floorFaces = stats.floorFaces,
                floorPanels = stats.floorPanels,
                floorWidthMin = stats.floorWidths.Count > 0 ? stats.floorWidths.Min() : 0f,
                floorWidthP50 = Percentile(stats.floorWidths, 0.5f),
                floorWidthMax = stats.floorWidths.Count > 0 ? stats.floorWidths.Max() : 0f,
                toneSpreadP50 = Percentile(stats.spreads, 0.50f),
                toneSpreadP95 = Percentile(stats.spreads, 0.95f),
                toneSpreadMax = Percentile(stats.spreads, 1f),
                toneBaseSpreadP95 = Percentile(stats.baseSpreads, 0.95f),
                toneBaseSpreadMax = Percentile(stats.baseSpreads, 1f),
                hiddenRenderers = hidden,
                param = new ParamSnap
                {
                    scene = ScenePath,
                    faces = FacesPath,
                    outPath = OutPath,
                    minArea = P_MinArea,
                    budget = P_Budget,
                    hero = P_Hero,
                    hide = P_Hide,
                    floors = P_Floors,
                    only = P_Only ?? "",
                    limit = P_Limit,
                    skip = string.Join(",", P_Skip),
                    panelMin = P_PanelMin,
                    panelMax = P_PanelMax,
                    boltPerPanel = BoltPerPanel,
                },
            };
            string[] famNames = { "paint", "metal", "mixed" };
            for (int i = 0; i < 3; i++)
                m.families.Add(new FamilyStat { family = famNames[i], faces = stats.family[i] });
            foreach (var g in plan.groups)
            {
                var r = res.TryGetValue(g.key, out var o) ? o : null;
                m.groups.Add(new GroupStat
                {
                    group = g.key, block = g.block, klass = g.klassName,
                    faces = g.works.Count,
                    tris = r != null ? r.tris : g.tris,
                    panels = stats.GroupPanels(g.key),
                    maxPanelArea = stats.GroupMaxArea(g.key),
                    floorPanels = GroupFloorCount(stats, g.key),
                    boardWidthMin = GroupFloorWidth(stats, g.key, 0f),
                    boardWidthP50 = GroupFloorWidth(stats, g.key, 0.5f),
                    boardWidthMax = GroupFloorWidth(stats, g.key, 1f),
                    mesh = r != null ? r.path : $"{MeshDir}/{g.key}.asset",
                });
            }
            foreach (var kv in plan.skip.OrderBy(k => k.Key, StringComparer.Ordinal))
                m.skips.Add(new SkipStat { reason = kv.Key, faces = kv.Value[0], area = kv.Value[1] / 1000f });
            foreach (var h in hiddenList) m.hidden.Add(h);
            WriteManifest(m);
            return m;
        }

        static void WriteManifest(ManifestDoc m)
        {
            try
            {
                string dir = Path.GetDirectoryName(OutPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(OutPath, JsonUtility.ToJson(m, true), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogError($"[立面重建] manifest 写盘失败：{e.Message}");
            }
        }

        static int GroupFloorCount(BuildStats s, string key)
        {
            var w = s != null ? s.GroupFloorWidths(key) : null;
            return w != null ? w.Count : 0;
        }

        static float GroupFloorWidth(BuildStats s, string key, float p)
        {
            var w = s != null ? s.GroupFloorWidths(key) : null;
            if (w == null || w.Count == 0) return 0f;
            return p >= 1f ? w.Max() : (p <= 0f ? w.Min() : Percentile(w, p));
        }

        static ManifestDoc ReadManifest()
        {
            try
            {
                if (!File.Exists(OutPath)) return null;
                return JsonUtility.FromJson<ManifestDoc>(File.ReadAllText(OutPath));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[立面重建] 读 manifest 失败（将按无记录处理）：{e.Message}");
                return null;
            }
        }

        /// <summary>删本工具生成的 mesh 资产（按 manifest 清单；没有清单就扫整个输出目录）。</summary>
        static int DeleteMeshes(ManifestDoc man)
        {
            int n = 0;
            if (man != null && man.groups != null && man.groups.Count > 0)
            {
                foreach (var g in man.groups)
                {
                    if (g == null || string.IsNullOrEmpty(g.mesh)) continue;
                    if (AssetDatabase.LoadAssetAtPath<Mesh>(g.mesh) == null) continue;
                    AssetDatabase.DeleteAsset(g.mesh);
                    n++;
                }
                return n;
            }
            if (!AssetDatabase.IsValidFolder(MeshDir)) return 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Mesh", new[] { MeshDir }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(p)) continue;
                AssetDatabase.DeleteAsset(p);
                n++;
            }
            return n;
        }

        // ------------------------------------------------------------------ 杂项

        static void AppendSkipReport(StringBuilder rep, Plan plan)
        {
            rep.AppendLine($"  跳过 {plan.skip.Sum(kv => kv.Value[0])} 面：");
            foreach (var kv in plan.skip.OrderBy(k => k.Key, StringComparer.Ordinal))
                rep.AppendLine($"    {kv.Key,-16} {kv.Value[0],5} 面  {kv.Value[1] / 1000f,10:N1} ㎡");
        }

        /// <summary>预算说明：螺栓密度是否是抽稀下来的。</summary>
        static string BudgetNote() =>
            BoltPerPanel >= 4 ? "（预算内，螺栓四角全给）" : $"（超预算：螺栓额度降到每板约 {BoltPerPanel} 颗）";

        /// <summary>v2/v3 统计：墙族 / 板数 / 最大板面积 / 螺栓 / 警示 / 发光 / 同面明度差。</summary>
        static string StatsLine(BuildStats s)
        {
            if (s == null) return "";
            return $"  墙族 paint/metal/mixed = {s.family[0]}/{s.family[1]}/{s.family[2]}；" +
                   $"板场 {s.panelFaces} 面 / 板 {s.panels:N0} 块（最多 {s.maxPanelsPerFace} 块/面，平均 {s.MeanPanelArea:F2}㎡，最大 {s.maxPanelArea:F2}㎡）；" +
                   $"薄高面 {s.thinFaces}\n" +
                   $"  螺栓 {s.bolts:N0} 颗（每板 {BoltPerPanel} 颗）；警示踢脚 {s.hazard} 面；发光腰线 {s.emissiveDrawn}/{s.emissive} 面\n" +
                   (P_ToneMode == 1
                       ? $"  同面明度差（含强调板）p50 {Percentile(s.spreads, 0.50f) * 100f:F1}% / p95 {Percentile(s.spreads, 0.95f) * 100f:F1}% / max {Percentile(s.spreads, 1f) * 100f:F1}%；" +
                         $"明度微变部分（不含强调板）p95 {Percentile(s.baseSpreads, 0.95f) * 100f:F1}% / max {Percentile(s.baseSpreads, 1f) * 100f:F1}%；" +
                         $"强调板 {s.accentPanels} 块"
                       : $"  同面明度差（-paneltone 0 逐块三色轮换）p50 {Percentile(s.spreads, 0.50f) * 100f:F1}% / p95 {Percentile(s.spreads, 0.95f) * 100f:F1}% / max {Percentile(s.spreads, 1f) * 100f:F1}%");
        }

        /// <summary>地面分组统计：面数 / 板数 / 板宽（短边）min-p50-max，逐组一行。</summary>
        static string FloorLine(Plan plan, BuildStats s)
        {
            if (plan.nFloor == 0) return "  地面：未处理（-floors 0）";
            var rep = new StringBuilder();
            rep.AppendLine($"  地面分组（-floors {P_Floors}）：");
            int shown = 0;
            foreach (var g in plan.groups)
            {
                if (g.klassName != "Floor") continue;
                var widths = s != null ? s.GroupFloorWidths(g.key) : null;
                if (widths == null || widths.Count == 0)
                    rep.AppendLine($"    {g.key,-14} 面 {g.works.Count,5} / 板 0");
                else
                    rep.AppendLine($"    {g.key,-14} 面 {g.works.Count,5} / 板 {widths.Count,5} / " +
                                   $"板宽 min {widths.Min():F2} / p50 {Percentile(widths, 0.5f):F2} / max {widths.Max():F2} m");
                shown++;
            }
            if (shown == 0) rep.AppendLine("    （没有地面面入选）");
            return rep.ToString().TrimEnd();
        }

        /// <summary>模数网格自检：三个极端尺寸现场算一遍板数/单板面积，干跑时一并打印（写进输出的核对）。</summary>
        static string ModulusCheck()
        {
            var rep = new StringBuilder();
            rep.AppendLine($"  模数核对（panelmin/max = {P_PanelMin:F2}/{P_PanelMax:F2}，面板数上限 {PanelCapPerFace}，合并上限 {PanelSoftAreaCap:F1}㎡）：");
            CheckOne(rep, 12.729f, 0.7236f, "12.7×0.72 窄条");
            CheckOne(rep, 20f, 17.15f, "343㎡ 大面");
            CheckOne(rep, 2f, 2f, "2×2 小面");
            return rep.ToString().TrimEnd('\n');
        }

        static void CheckOne(StringBuilder rep, float W, float H, string tag)
        {
            float mn = Mathf.Min(W, H);
            float margin = mn < 3f ? Mathf.Min(0.10f, 0.12f * mn) : 0.20f;
            float fw = W - 2f * margin, fh = H - 2f * margin;
            if (H < 1.2f || fw < 0.6f || fh < 0.6f)
            {
                rep.AppendLine($"    {tag,-14} → 薄高面/窄条分支：结构包板（不做板场）");
                return;
            }
            var rng = new Rng(SeedOf("123456789", "wall"));
            float target = rng.Range(P_PanelMin * 1.08f, P_PanelMax * 0.83f);
            Grid(fw, fh, target, out int cols, out int rows, out float mu, out float mv);
            var rects = MergeCells(cols, rows, mu, mv, 0f, 0f, SeamMin, rng);
            float minA = float.MaxValue, maxA = 0f;
            for (int i = 0; i < rects.Count; i++)
            {
                float a = rects[i].Area;
                if (a < minA) minA = a;
                if (a > maxA) maxA = a;
            }
            rep.AppendLine($"    {tag,-14} → 场 {fw:F2}×{fh:F2}m，模数 {mu:F2}m（{cols}×{rows}={cols * rows} 格）→ {rects.Count} 块板，" +
                           $"单板 {minA:F2}–{maxA:F2}㎡（最大 {maxA:F2}）");
        }

        static string SkipSummary(Plan plan) =>
            string.Join("；", plan.skip.OrderBy(k => k.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key} {kv.Value[0]} 面"));

        static FaceDoc LoadDoc()
        {
            if (!File.Exists(FacesPath))
            {
                Debug.LogError($"[立面重建] 找不到面表 {FacesPath}（先用 FaceSkin 导出）");
                return null;
            }
            try
            {
                var doc = JsonUtility.FromJson<FaceDoc>(File.ReadAllText(FacesPath));
                if (doc == null || doc.faces == null || doc.faces.Count == 0)
                {
                    Debug.LogError($"[立面重建] 面表解析为空：{FacesPath}");
                    return null;
                }
                return doc;
            }
            catch (Exception e)
            {
                Debug.LogError($"[立面重建] 面表解析失败：{e.Message}");
                return null;
            }
        }

        /// <summary>确定性 RNG（xorshift32）：同一 seed 每次跑出同一套构件。</summary>
        class Rng
        {
            uint s;
            public Rng(uint seed) { s = seed == 0u ? 0x9E3779B9u : seed; }
            public float Next()
            {
                s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                return (s & 0xFFFFFFu) / 16777216f;
            }
            public float Range(float a, float b) => a + (b - a) * Next();
            public int NextInt(int exclusiveMax) => exclusiveMax <= 1 ? 0 : Mathf.Min(exclusiveMax - 1, (int)(Next() * exclusiveMax));
        }

        /// <summary>面表 seed 是字符串（可能超 int），按 long 解析后再做 FNV 派生各域子种子。</summary>
        static uint SeedOf(string seed, string tag)
        {
            long v = 0L;
            long.TryParse(seed, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
            return Fnv1a(v.ToString(CultureInfo.InvariantCulture) + "|" + tag);
        }

        static uint Fnv1a(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
                return h;
            }
        }

        /// <summary>4 个宏的下标做一次 Fisher–Yates 洗牌（seed 稳定）。</summary>
        static int[] Shuffle4(Rng rng)
        {
            var a = new[] { 0, 1, 2, 3 };
            for (int i = 3; i > 0; i--)
            {
                int j = rng.NextInt(i + 1);
                int t = a[i]; a[i] = a[j]; a[j] = t;
            }
            return a;
        }

        static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
            int i = path.LastIndexOf('/');
            string parent = i > 0 ? path.Substring(0, i) : "Assets";
            string name = i >= 0 ? path.Substring(i + 1) : path;
            EnsureFolder(parent);
            if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(parent, name);
        }

        static void ParseArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
            {
                switch (a[i])
                {
                    case "-scene": ScenePath = a[i + 1]; break;
                    case "-faces": FacesPath = a[i + 1]; break;
                    case "-out": OutPath = a[i + 1]; break;
                    case "-minarea": P_MinArea = Fl(a[i + 1], P_MinArea); break;
                    case "-budget": P_Budget = InL(a[i + 1], P_Budget); break;
                    case "-hero": P_Hero = Fl(a[i + 1], P_Hero); break;
                    case "-hide": P_Hide = In(a[i + 1], P_Hide); break;
                    case "-floors": P_Floors = In(a[i + 1], P_Floors); break;
                    case "-only": P_Only = a[i + 1]; break;
                    case "-limit": P_Limit = In(a[i + 1], P_Limit); break;
                    case "-panelmin": P_PanelMin = Fl(a[i + 1], P_PanelMin); break;
                    case "-panelmax": P_PanelMax = Fl(a[i + 1], P_PanelMax); break;
                    case "-paneltone": P_ToneMode = In(a[i + 1], P_ToneMode); break;
                    case "-skip": P_Skip = (a[i + 1] ?? "").Split(',').Where(s => s.Trim().Length > 0).Select(s => s.Trim()).ToArray(); break;
                }
            }
            Sanitize();
        }

        static void Sanitize()
        {
            if (string.IsNullOrEmpty(ScenePath)) ScenePath = DefaultScene;
            if (string.IsNullOrEmpty(FacesPath)) FacesPath = DefaultFaces;
            if (string.IsNullOrEmpty(OutPath)) OutPath = DefaultOut;
            P_MinArea = Mathf.Clamp(P_MinArea, 0.01f, 10000f);
            P_Budget = Math.Max(1000L, Math.Min(P_Budget, 20000000L));
            P_Hero = Mathf.Clamp(P_Hero, 1f, 100000f);
            P_Hide = P_Hide == 0 ? 0 : 1;
            P_Floors = P_Floors == 1 ? 1 : 0;
            P_Limit = Mathf.Clamp(P_Limit, 0, 1000000);
            P_PanelMin = Mathf.Clamp(P_PanelMin, 0.6f, 3.0f);
            P_PanelMax = Mathf.Clamp(P_PanelMax, P_PanelMin, 6.0f);
            P_ToneMode = P_ToneMode == 1 ? 1 : 0;
            if (P_Only != null && P_Only.Length == 0) P_Only = null;
        }

        static float Fl(string s, float d) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;

        static int In(string s, int d) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;

        static long InL(string s, long d) =>
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;

        class ObjResult
        {
            public Mesh mesh;
            public long tris;
            public string path;
        }
    }
}
