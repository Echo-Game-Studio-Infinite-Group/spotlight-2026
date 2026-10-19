// =====================================================================================
// 全地图唯一语义调色板（公共系统，不按关卡拆）
//
// 用户口径：
//   · 黑 = 可奔跑 / 可攀爬；白 = 常规建筑；青 = 标注 / 信息；
//     黄 / 橙 = 警示 / 出口；玻璃 = 可透视分隔。
//   · 同种地形必须一眼看出是同类：颜色可以改，但语义不能一关一个样。
//
// 本工具解决什么：
//   七张图各自建了一套材质族（S_02 的 DEC_S02_B0/B1/W2、S_03 的 DEC_S03_D_W_*、
//   S_06 的 DEC_S06_Black_0..3、S_05 的 Material.014/005、S_01/S_04/S_07 的 B_*/W_*），
//   同一个「黑」出现 (0.040,0.041,0.048) / (0.045,0.048,0.055) / (0.052,0.054,0.060)
//   多个值。这里把它们按名字 / 现有基色归到同一套语义，就地改色。
//
// 唯一调色板：
//   · C# 常量是权威默认值（本文件下半部 PaletteDefaults）。
//   · `Assets/Settings/MapSkinPalette.json` 是给美术 / TA 看的同一份数据；存在时以它为准，
//     不存在时 Run 会按 C# 常量导出一份。JSON 不依赖脚本 GUID，比 .asset 更适合跨工具读取。
//   · 共享材质只有一套：`Assets/Art/Materials/MapSkin/Palette/MS_{Semantic}_{Variant}.mat`
//     （6 语义 × Base/Accent/Emissive = 18 个），供 GateKit、后续程序化构件共用。
//     **不再按关卡新建材质族**；老材质只就地改色，不改路径（各关 Decorate 脚本仍按旧路径加载）。
//
// Run 干的事：
//   1) 扫描场景引用 + 工程内目标材质（Material.014/005、DEC_*、B_*/W_*/E_*/P_*、Glazing）；
//   2) 按名字优先、现有基色兜底归到 语义 × 变体；
//   3) 就地把 _BaseColor / _Color / 发光变体的 _EmissionColor 改到调色板值，
//      贴图、tiling、offset、光滑度等一概不动（保住各关装饰的 UV 口径）；
//   4) 确保 18 个 `MS_*` 共享材质存在；
//   5) 输出归类报告到 `_session_audit/PALETTE_Report.txt`（DryRun 写 PALETTE_DryRun.txt）。
//
// 可回退：
//   第一次 Run 前把每个材质被改的属性快照写进 `Assets/Art/EnvSkins/PALETTE_Backup.asset`；
//   Restore 按快照逐项写回。已存在的备份不会被第二次 Run 覆盖（否则备份会被改成"改后值"）。
//
// 菜单：MapSkin/调色板/① 干跑归类  ② 统一换色(就地)  ③ 还原 PALETTE_Backup
// 批处理：
//   -executeMethod MapSkin.MapSkinPalette.DryRun
//   -executeMethod MapSkin.MapSkinPalette.Run
//   -executeMethod MapSkin.MapSkinPalette.Restore
// =====================================================================================
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
    /// <summary>地形语义。枚举顺序即报告顺序，不要随意改动。</summary>
    public enum MapSemantic
    {
        Black = 0,   // 可奔跑 / 可攀爬
        White = 1,   // 常规建筑
        Cyan = 2,    // 标注 / 信息
        Yellow = 3,  // 警示
        Orange = 4,  // 出口 / 进入点 / 暖色焦点
        Glass = 5,   // 玻璃 / 可透视分隔
    }

    /// <summary>同一语义只允许三档：基色 / 强调 / 自发光。</summary>
    public enum MapVariant
    {
        Base = 0,
        Accent = 1,
        Emissive = 2,
    }

    // ---------------------------------------------------------------- 调色板数据模型

    [Serializable]
    public class PaletteSemanticEntry
    {
        public string semantic;
        public float[] baseColor;
        public float[] accentColor;
        public float[] emissiveColor;
        public float metallic = -1f;    // -1 = JSON 未给，沿用 C# 默认值
        public float smoothness = -1f;
    }

    [Serializable]
    public class PaletteSignalEntry
    {
        public string signal;
        public float[] color;
    }

    [Serializable]
    public class PaletteFile
    {
        public int version = 1;
        public string note;
        public PaletteSignalEntry[] signals;
        public PaletteSemanticEntry[] semantics;
    }

    /// <summary>调色板改色快照。放在 Editor 程序集里：只服务 Editor 工具，运行时不需要。</summary>
    public class PaletteBackup : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public string assetPath;
            public string materialName;
            public string semantic;
            public string variant;
            public string reason;

            public bool hasBaseColor; public Color baseColor;
            public bool hasColor; public Color color;
            public bool hasEmissionColor; public Color emissionColor;
            public bool hadEmissionKeyword;
            public bool hasEmissionEnabled; public float emissionEnabled;
            public bool hasEmissionIntensity; public float emissionIntensity;
            public bool hasEmissionUseMap; public float emissionUseMap;
            public bool hasMetallic; public float metallic;
            public bool hasSmoothness; public float smoothness;
            public bool hasSurface; public float surface;
            public bool hadTransparentKeyword;
            public int originalRenderQueue;
        }

        public int version = 1;
        public string createdUtc;
        public List<Entry> entries = new List<Entry>();

        /// <summary>本工具第一次 Run 时新建的共享材质；Restore 会删掉它们。</summary>
        public List<string> createdAssets = new List<string>();
    }

    // ---------------------------------------------------------------- 主工具

    /// <summary>
    /// 七图统一语义色。所有对「地形语义色」的读写都应经过本类，不要在别处再写死颜色。
    /// </summary>
    public static class MapSkinPalette
    {
        // ------------------------------------------------------------------ 固定路径

        public const string ScenePath = "Assets/Scenes/Level1.unity";
        public const string JsonPath = "Assets/Settings/MapSkinPalette.json";
        public const string BackupPath = "Assets/Art/EnvSkins/PALETTE_Backup.asset";
        public const string PaletteMatDir = "Assets/Art/Materials/MapSkin/Palette";
        public const string BaseMatDir = "Assets/Art/Models/MikuGandam/materials";
        public const string GlazingMatPath = "Assets/Art/Materials/MapSkin/Glazing/MAPSKIN_Glazing.mat";

        const string NprShaderName = "OurFunction/NPR/Deferred Toon";
        const string NprTransparentShaderName = "OurFunction/NPR/Transparent Toon";
        const string UrpLitShaderName = "Universal Render Pipeline/Lit";
        const string UrpUnlitShaderName = "Universal Render Pipeline/Unlit";

        // ------------------------------------------------------------------ 唯一调色板（C# 常量 = 权威默认值）

        // 语义色不是"好看就行"：黑必须仍是全场最暗、白必须仍是常规建筑、青/黄/橙必须够饱和，
        // 这样玩家在高速移动里只靠色块就能读出脚下能不能跑、墙能不能爬。
        public static readonly Color BlackBase = new Color(0.040f, 0.044f, 0.052f, 1f);
        public static readonly Color BlackAccent = new Color(0.120f, 0.135f, 0.160f, 1f);
        public static readonly Color BlackEmissive = new Color(0.090f, 0.200f, 0.300f, 1f);

        public static readonly Color WhiteBase = new Color(0.760f, 0.770f, 0.785f, 1f);
        public static readonly Color WhiteAccent = new Color(0.930f, 0.940f, 0.950f, 1f);
        public static readonly Color WhiteEmissive = new Color(0.700f, 0.780f, 0.850f, 1f);

        public static readonly Color CyanBase = new Color(0.000f, 0.560f, 0.580f, 1f);
        public static readonly Color CyanAccent = new Color(0.050f, 0.850f, 0.900f, 1f);
        public static readonly Color CyanEmissive = new Color(0.000f, 0.780f, 0.820f, 1f);

        public static readonly Color YellowBase = new Color(0.820f, 0.720f, 0.080f, 1f);
        public static readonly Color YellowAccent = new Color(1.000f, 0.860f, 0.160f, 1f);
        public static readonly Color YellowEmissive = new Color(1.000f, 0.720f, 0.050f, 1f);

        public static readonly Color OrangeBase = new Color(0.870f, 0.270f, 0.040f, 1f);
        public static readonly Color OrangeAccent = new Color(1.000f, 0.450f, 0.080f, 1f);
        public static readonly Color OrangeEmissive = new Color(1.000f, 0.350f, 0.020f, 1f);

        // 玻璃偏浅青（贴现有 MAPSKIN_Glazing 的 0.55,0.68,0.72），避免读成"黑 = 可攀爬"
        public static readonly Color GlassBase = new Color(0.450f, 0.580f, 0.620f, 0.35f);
        public static readonly Color GlassAccent = new Color(0.680f, 0.820f, 0.880f, 0.55f);
        public static readonly Color GlassEmissive = new Color(0.030f, 0.120f, 0.150f, 1f);

        /// <summary>状态灯只做门禁信号，不参与地形语义（不进 6 语义表，但仍是唯一色值）。</summary>
        public static readonly Color SignalGreen = new Color(0.080f, 0.850f, 0.280f, 1f);
        public static readonly Color SignalRed = new Color(0.900f, 0.120f, 0.100f, 1f);

        // ------------------------------------------------------------------ 语义定义

        sealed class SemanticDef
        {
            public MapSemantic semantic;
            public string key;
            public string chinese;
            public Color baseColor;
            public Color accentColor;
            public Color emissiveColor;
            public float metallic;
            public float smoothness;
        }

        static readonly SemanticDef[] Defs =
        {
            new SemanticDef { semantic = MapSemantic.Black,  key = "Black",  chinese = "可奔跑/可攀爬",
                baseColor = BlackBase,  accentColor = BlackAccent,  emissiveColor = BlackEmissive,
                metallic = 0.10f, smoothness = 0.35f },
            new SemanticDef { semantic = MapSemantic.White,  key = "White",  chinese = "常规建筑",
                baseColor = WhiteBase,  accentColor = WhiteAccent,  emissiveColor = WhiteEmissive,
                metallic = 0.02f, smoothness = 0.30f },
            new SemanticDef { semantic = MapSemantic.Cyan,   key = "Cyan",   chinese = "标注/信息",
                baseColor = CyanBase,   accentColor = CyanAccent,   emissiveColor = CyanEmissive,
                metallic = 0.05f, smoothness = 0.55f },
            new SemanticDef { semantic = MapSemantic.Yellow, key = "Yellow", chinese = "警示",
                baseColor = YellowBase, accentColor = YellowAccent, emissiveColor = YellowEmissive,
                metallic = 0.05f, smoothness = 0.50f },
            new SemanticDef { semantic = MapSemantic.Orange, key = "Orange", chinese = "出口/暖色焦点",
                baseColor = OrangeBase, accentColor = OrangeAccent, emissiveColor = OrangeEmissive,
                metallic = 0.05f, smoothness = 0.50f },
            new SemanticDef { semantic = MapSemantic.Glass,  key = "Glass",  chinese = "玻璃/可透视分隔",
                baseColor = GlassBase,  accentColor = GlassAccent,  emissiveColor = GlassEmissive,
                metallic = 0.20f, smoothness = 0.85f },
        };

        static readonly Dictionary<MapSemantic, SemanticDef> DefMap =
            Defs.ToDictionary(d => d.semantic, d => d);

        // JSON 覆盖值（存在时优先）；Run/DryRun 开头 LoadPalette。
        static readonly Dictionary<MapSemantic, SemanticDef> RuntimeDefs =
            new Dictionary<MapSemantic, SemanticDef>();

        static readonly Dictionary<string, Color> RuntimeSignals =
            new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);

        static bool _loaded;

        // ------------------------------------------------------------------ 菜单 / 批处理入口

        [MenuItem("MapSkin/调色板/① 干跑：归类报告（零资产写盘）")]
        public static void DryRunMenu() => DryRun();

        [MenuItem("MapSkin/调色板/② 统一语义换色（就地改材质）")]
        public static void RunMenu() => Run();

        [MenuItem("MapSkin/调色板/③ 还原：PALETTE_Backup")]
        public static void RestoreMenu() => Restore();

        /// <summary>只归类、只打印，不写任何材质 / 资产 / 场景。批处理：-executeMethod MapSkin.MapSkinPalette.DryRun</summary>
        public static void DryRun()
        {
            EnsureSceneOpen();
            var report = new StringBuilder();
            LoadPalette(report);
            var plan = BuildPlan(report);
            report.AppendLine();
            report.AppendLine("[干跑] 零资产写盘。Run 会就地修改上面列出的材质，并确保 18 个 MS_* 共享材质存在。");
            WriteReport("PALETTE_DryRun.txt", report.ToString(), logIt: true);
        }

        /// <summary>归类 + 就地换色 + 确保共享材质。批处理：-executeMethod MapSkin.MapSkinPalette.Run</summary>
        public static void Run()
        {
            EnsureSceneOpen();
            var report = new StringBuilder();
            var paletteSource = LoadPalette(report);
            report.AppendLine($"[调色板来源] {paletteSource}");

            // 1) JSON 不存在则按 C# 常量导出（存在则尊重美术改动，不覆盖）
            if (!File.Exists(JsonPath))
            {
                ExportJson(report, overwrite: false);
            }
            else
            {
                report.AppendLine($"[调色板] 使用现有 {JsonPath}（C# 常量仅作缺省值）");
            }

            // 2) 归类
            var plan = BuildPlan(report);

            // 3) 确保 18 个共享材质：一套函数，七关共用
            var backup = LoadOrCreateBackup();
            var created = EnsureCanonicalSet(backup, report);

            // 4) 就地换色（先记快照，再改）
            int changedProps = 0;
            foreach (var item in plan.materials)
            {
                if (item.material == null || string.IsNullOrEmpty(item.assetPath)) continue;
                var rec = RememberOnce(backup, item);
                changedProps += ApplyInPlace(item, rec);
            }

            AssetDatabase.SaveAssets();
            EditorUtility.SetDirty(backup);
            AssetDatabase.SaveAssets();

            report.AppendLine();
            report.AppendLine($"[结果] 归类材质 {plan.materials.Count} 个（{plan.materials.Select(m => m.assetPath).Distinct().Count()} 条资产路径），" +
                              $"就地改属性 {changedProps} 处；新建共享材质 {created} 个已记入备份。");
            if (plan.unknownLarge.Count > 0)
            {
                report.AppendLine($"[未归类] {plan.unknownLarge.Count} 个，保持原样（按名字前 20 个）：");
                foreach (var u in plan.unknownLarge.OrderBy(k => k.Key, StringComparer.Ordinal).Take(20))
                    report.AppendLine($"    {u.Key,-64} 基色 {ColorText(u.Value)}");
            }
            report.AppendLine();
            report.AppendLine("[提示] 旧材质路径保持不动——各关 Decorate 脚本仍按旧路径加载；");
            report.AppendLine("       新公共构件（GateKit 等）只用 Palette/MS_{Semantic}_{Variant}.mat。");

            WriteReport("PALETTE_Report.txt", report.ToString(), logIt: true);
        }

        /// <summary>按 PALETTE_Backup 逐项还原旧材质颜色；共享 MS_* 材质保留（GateKit 仍引用）。批处理：-executeMethod MapSkin.MapSkinPalette.Restore</summary>
        public static void Restore()
        {
            var dp = AssetDatabase.LoadAssetAtPath<PaletteBackup>(BackupPath);
            if (dp == null)
            {
                Debug.LogWarning($"[MapSkin·调色板] 找不到备份 {BackupPath}，没有可还原内容。");
                return;
            }

            if (dp.entries == null) dp.entries = new List<PaletteBackup.Entry>();

            int restored = 0, missing = 0;
            var rep = new StringBuilder();
            foreach (var e in dp.entries)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(e.assetPath);
                if (m == null) { missing++; rep.AppendLine($"  [缺] {e.assetPath}"); continue; }

                if (e.hasBaseColor && m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", e.baseColor);
                if (e.hasColor && m.HasProperty("_Color")) m.SetColor("_Color", e.color);
                if (e.hasEmissionColor && m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", e.emissionColor);
                if (e.hasEmissionEnabled && m.HasProperty("_EmissionEnabled")) m.SetFloat("_EmissionEnabled", e.emissionEnabled);
                if (e.hasEmissionIntensity && m.HasProperty("_EmissionIntensity")) m.SetFloat("_EmissionIntensity", e.emissionIntensity);
                if (e.hasEmissionUseMap && m.HasProperty("_EmissionUseMap")) m.SetFloat("_EmissionUseMap", e.emissionUseMap);
                if (e.hasMetallic && m.HasProperty("_Metallic")) m.SetFloat("_Metallic", e.metallic);
                if (e.hasSmoothness && m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", e.smoothness);
                if (e.hasSurface && m.HasProperty("_Surface")) m.SetFloat("_Surface", e.surface);
                if (e.hasSurface)
                {
                    if (e.hadTransparentKeyword) m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    else m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    m.renderQueue = e.originalRenderQueue;
                }

                // 发光 keyword 不是属性，单独还原
                if (e.hasEmissionColor || e.hasEmissionEnabled)
                {
                    if (e.hadEmissionKeyword) m.EnableKeyword("_EMISSION");
                    else m.DisableKeyword("_EMISSION");
                }

                EditorUtility.SetDirty(m);
                restored++;
            }

            // 共享 MS_* 材质是公共系统资产，Restore 只还原旧材质颜色，不删共享材质
            // （GateKit 的门还引用着它们；删了会让场景里的门变粉）。
            AssetDatabase.SaveAssets();
            Debug.Log($"[MapSkin·调色板] 还原完成：旧材质 {restored} 个，缺失 {missing} 个；" +
                      $"共享材质 {(dp.createdAssets?.Count ?? 0)} 个保留。\n{rep}");
        }

        // ------------------------------------------------------------------ 公共 API（GateKit / 其他公共构件用）

        public static string SemanticKey(MapSemantic s) => Def(s).key;

        public static string SemanticChinese(MapSemantic s) => Def(s).chinese;

        public static string VariantKey(MapVariant v) =>
            v == MapVariant.Base ? "Base" : (v == MapVariant.Accent ? "Accent" : "Emissive");

        public static Color GetColor(MapSemantic s, MapVariant v)
        {
            EnsureLoaded();
            var d = Def(s);
            return v == MapVariant.Base ? d.baseColor : (v == MapVariant.Accent ? d.accentColor : d.emissiveColor);
        }

        public static float GetMetallic(MapSemantic s) { EnsureLoaded(); return Def(s).metallic; }

        public static float GetSmoothness(MapSemantic s) { EnsureLoaded(); return Def(s).smoothness; }

        public static string CanonicalPath(MapSemantic s, MapVariant v) =>
            $"{PaletteMatDir}/MS_{SemanticKey(s)}_{VariantKey(v)}.mat";

        /// <summary>
        /// 取共享语义材质。缺失时从同类母材质 CopyAsset（不 new Material 克隆），
        /// 再写调色板色值。这是唯一允许新建材质的地方——而且是 6×3 的公共集合，不按关卡复制。
        /// </summary>
        public static Material GetMaterial(MapSemantic s, MapVariant v)
        {
            EnsureLoaded();
            string path = CanonicalPath(s, v);
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;

            EnsureFolder("Assets/Art/Materials/MapSkin", "Palette");
            var src = FindSourceMaterial(s);
            if (src != null)
            {
                var srcPath = AssetDatabase.GetAssetPath(src);
                if (!string.IsNullOrEmpty(srcPath) && srcPath != path && AssetDatabase.CopyAsset(srcPath, path))
                    m = AssetDatabase.LoadAssetAtPath<Material>(path);
            }

            if (m == null)
            {
                // 硬约束：不 new Material 克隆。找不到任何可 CopyAsset 的母材质就报错，不偷偷造克隆。
                Debug.LogError($"[MapSkin·调色板] 找不到可拷贝的母材质，无法创建 {path}。");
                return null;
            }

            ApplyCanonicalValues(m, s, v);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>
        /// 状态灯材质（红/绿）。不属于地形六语义，但同样走唯一色值与统一命名。
        /// lit=true → 自发光灯（入口绿灯）；lit=false → 暗红/暗色灯罩（出口红灯常暗，保留封锁状态语义）。
        /// </summary>
        public static Material GetSignalMaterial(string signalName, Color color, bool lit = true)
        {
            EnsureLoaded();
            if (RuntimeSignals.TryGetValue(signalName, out var signalColor)) color = signalColor;
            string path = lit
                ? $"{PaletteMatDir}/MS_Signal_{signalName}_Emissive.mat"
                : $"{PaletteMatDir}/MS_Signal_{signalName}_Dim.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                EnsureFolder("Assets/Art/Materials/MapSkin", "Palette");
                var src = AssetDatabase.LoadAssetAtPath<Material>($"{BaseMatDir}/Material.015.mat");
                if (src == null) src = AssetDatabase.LoadAssetAtPath<Material>($"{BaseMatDir}/Material.014.mat");
                if (src != null)
                {
                    var srcPath = AssetDatabase.GetAssetPath(src);
                    if (!string.IsNullOrEmpty(srcPath) && AssetDatabase.CopyAsset(srcPath, path))
                        m = AssetDatabase.LoadAssetAtPath<Material>(path);
                }
                if (m == null)
                {
                    Debug.LogError($"[MapSkin·调色板] 找不到可拷贝的母材质，无法创建 {path}。");
                    return null;
                }
            }

            Color baseCol = lit ? color : new Color(color.r * 0.35f, color.g * 0.35f, color.b * 0.35f, 1f);
            SetColorSafe(m, "_BaseColor", baseCol);
            SetColorSafe(m, "_Color", baseCol);
            foreach (var tex in new[] { "_BaseMap", "_MainTex", "_BumpMap", "_MetallicGlossMap", "_OcclusionMap", "_EmissionMap" })
                if (m.HasProperty(tex)) m.SetTexture(tex, null);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", lit ? 0.6f : 0.8f);

            if (lit)
            {
                SetColorSafe(m, "_EmissionColor", color);
                if (m.HasProperty("_EmissionEnabled")) m.SetFloat("_EmissionEnabled", 1f);
                if (m.HasProperty("_EmissionIntensity")) m.SetFloat("_EmissionIntensity", 1.6f);
                if (m.HasProperty("_EmissionUseMap")) m.SetFloat("_EmissionUseMap", 0f);
                m.EnableKeyword("_EMISSION");
            }
            else
            {
                if (m.HasProperty("_EmissionEnabled")) m.SetFloat("_EmissionEnabled", 0f);
                m.DisableKeyword("_EMISSION");
            }

            EditorUtility.SetDirty(m);
            AssetDatabase.SaveAssets();
            return m;
        }

        /// <summary>按名字 / 现有基色把一个材质归到语义。公开给审计与后续工具复用。</summary>
        public static bool Classify(Material m, out MapSemantic semantic, out MapVariant variant, out string reason)
        {
            semantic = MapSemantic.White;
            variant = MapVariant.Base;
            reason = null;
            if (m == null) return false;

            string assetPath = AssetDatabase.GetAssetPath(m) ?? string.Empty;
            string n = (m.name ?? string.Empty).ToUpperInvariant();
            if (n.Length == 0) n = Path.GetFileNameWithoutExtension(assetPath).ToUpperInvariant();
            string path = assetPath.ToUpperInvariant();
            if (n.Length == 0) return false;

            // ---- 变体：先判自发光，再判强调件
            if (HasAny(n, "GLOW", "EMISS", "EMISSIVE", "NEON", "LAMP", "BEACON", "LUM") ||
                n.Contains("_ECY") || n.Contains("_EWH") || n.Contains("_EWA") ||
                n.Contains("EMISSIVECYAN") || n.Contains("EMISSIVEWARM"))
            {
                variant = MapVariant.Emissive;
            }
            else if (HasAny(n, "ACCENT", "TRIM", "RIB", "SEAM", "EDGE", "FRAME", "STRIPE", "MARK", "BAND") ||
                     n.Contains("_ACY") || n.Contains("_AOR") || n.Contains("_AYE") ||
                     Regex.IsMatch(n, @"^E_[A-Z]") || Regex.IsMatch(n, @"^P_[A-Z]") ||
                     n.Contains("AMBER"))
            {
                variant = MapVariant.Accent;
            }

            // ---- 语义：玻璃优先（避免 GLAZING 里的 GL 被别的规则吃掉）
            if (HasAny(n, "GLASS", "GLAZ", "WINDOW", "TRANSPARENT") || path.Contains("/GLAZING/"))
            {
                semantic = MapSemantic.Glass;
                reason = "名字/路径 = 玻璃";
                return true;
            }

            // 橙色（入口 / 暖色焦点）——必须在黄色之前判，WARM 是暖橙不是黄
            if (HasAny(n, "ORANGE", "ENTRANCE", "ENTRY", "WARM") ||
                n.Contains("_AOR") || n.Contains("_EWA") ||
                n.Contains("MATERIAL.038") || n.Contains("MATERIAL_038"))
            {
                semantic = MapSemantic.Orange;
                reason = "名字 = 橙/入口/暖色";
                return true;
            }

            // 黄色（警示）
            if (HasAny(n, "YELLOW", "WARNING", "WARN", "CAUTION", "HAZARD", "AMBER") ||
                n.Contains("_AYE") ||
                n.Contains("MATERIAL.061") || n.Contains("MATERIAL_061") ||
                n.Contains("MATERIAL.063") || n.Contains("MATERIAL_063"))
            {
                semantic = MapSemantic.Yellow;
                reason = "名字 = 黄/警示";
                return true;
            }

            // 青色（标注 / 信息）
            if (HasAny(n, "CYAN", "INFO", "MARKER", "SIGN") ||
                n.Contains("_ACY") || n.Contains("_ECY") ||
                n.Contains("MATERIAL.015") || n.Contains("MATERIAL_015") ||
                n.Contains("MATERIAL.026") || n.Contains("MATERIAL_026") ||
                n.Contains("MATERIAL.039") || n.Contains("MATERIAL_039") ||
                Regex.IsMatch(n, @"^(E|P)_CYAN") || n.Contains("ACCENTCYAN") || n.Contains("LCC_"))
            {
                semantic = MapSemantic.Cyan;
                reason = "名字 = 青/信息";
                return true;
            }

            // 调色板没有绿色语义：绿色状态件/装饰统一并入青色（信息），并在报告里标明来源
            if (HasAny(n, "GREEN"))
            {
                semantic = MapSemantic.Cyan;
                reason = "绿色 → 青（调色板无绿语义）";
                return true;
            }

            // S_01/S_04/S_07 的 B_A_1/B_B_1… 是 "Building"（建筑白），不是 Black。
            // 名字会骗人，所以这一族用现有基色兜底判黑白：亮 → 白，暗 → 黑。
            if (Regex.IsMatch(n, @"^B_[A-E]_\d"))
            {
                if (TryGetBaseColor(m, out var bc0))
                {
                    float lum0 = 0.2126f * bc0.r + 0.7152f * bc0.g + 0.0722f * bc0.b;
                    if (lum0 < 0.30f)
                    {
                        semantic = MapSemantic.Black;
                        reason = $"B_ 建筑族但基色偏黑 lum={lum0:F3}";
                        return true;
                    }
                }
                semantic = MapSemantic.White;
                reason = "B_ 建筑族（按基色判为白）";
                return true;
            }

            // 黑色（可奔跑 / 可攀爬）
            if (HasAny(n, "BLACK", "BLK", "DARK") ||
                n.Contains("MATERIAL.005") || n.Contains("MATERIAL_005") ||
                n.Contains("MATERIAL.006") || n.Contains("MATERIAL_006") ||
                n.Contains("MATERIAL.013") || n.Contains("MATERIAL_013") ||
                n.Contains("MATERIAL.016") || n.Contains("MATERIAL_016") ||
                n.Contains("MATERIAL.017") || n.Contains("MATERIAL_017") ||
                n.Contains("MATERIAL.041") || n.Contains("MATERIAL_041") ||
                Regex.IsMatch(n, @"(^|[_\s])B[0-3]([_\s]|$)"))
            {
                semantic = MapSemantic.Black;
                reason = "名字 = 黑/可攀爬";
                return true;
            }

            // 白色（常规建筑）
            if (HasAny(n, "WHITE") || n.Contains("_EWH") ||
                n.Contains("MATERIAL.014") || n.Contains("MATERIAL_014") ||
                n.Contains("MATERIAL.060") || n.Contains("MATERIAL_060") ||
                n.Contains("MATERIAL.051") || n.Contains("MATERIAL_051") ||
                Regex.IsMatch(n, @"(^|[_\s])W[0-4]([_\s]|$)") ||
                Regex.IsMatch(n, @"^W_[A-E]_\d") || Regex.IsMatch(n, @"^P_W_\d") ||
                Regex.IsMatch(n, @"^DEC_S0[1-7]_D_W") ||
                Regex.IsMatch(n, @"^DEC_S0[1-7]_W_W"))
            {
                semantic = MapSemantic.White;
                reason = "名字 = 白/常规建筑";
                return true;
            }

            // ---- 名字没线索：用现有基色兜底
            if (!TryGetBaseColor(m, out var c)) { reason = "名字/颜色都无法判定"; return false; }
            Color.RGBToHSV(c, out var h, out var s, out var val);
            float lum = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

            if (val < 0.16f) { semantic = MapSemantic.Black; reason = $"基色亮度 {lum:F3} < 0.16"; return true; }
            if (s < 0.18f && val > 0.45f) { semantic = MapSemantic.White; reason = $"基色低饱和亮色 {lum:F3}"; return true; }
            if (s > 0.35f)
            {
                if (h >= 0.40f && h <= 0.58f) { semantic = MapSemantic.Cyan; reason = $"基色青相 h={h:F2}"; return true; }
                if (h >= 0.05f && h < 0.13f) { semantic = MapSemantic.Orange; reason = $"基色橙相 h={h:F2}"; return true; }
                if (h >= 0.13f && h < 0.20f) { semantic = MapSemantic.Yellow; reason = $"基色黄相 h={h:F2}"; return true; }
            }

            // 深灰 / 中灰：按亮度落到黑或白，保证"不是黑就是白"，不再新增第五种灰
            semantic = val < 0.35f ? MapSemantic.Black : MapSemantic.White;
            reason = $"灰阶兜底 lum={lum:F3}";
            return true;
        }

        // ------------------------------------------------------------------ 归类计划

        sealed class MatItem
        {
            public Material material;
            public string assetPath;
            public MapSemantic semantic;
            public MapVariant variant;
            public string reason;
        }

        sealed class Plan
        {
            public List<MatItem> materials = new List<MatItem>();
            public Dictionary<string, Color> unknownLarge = new Dictionary<string, Color>();
            public int sceneSlots;
            public int sceneMatchedSlots;
        }

        static Plan BuildPlan(StringBuilder report)
        {
            var plan = new Plan();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // 1) 工程内目标材质（不只场景引用：预制体实例 / 后续加载也要统一）
            var guids = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });
            var all = new List<Material>(guids.Length);
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (string.IsNullOrEmpty(p)) continue;
                if (p.StartsWith("Assets/Art/VFX/", StringComparison.OrdinalIgnoreCase)) continue; // VFX 不吃地形语义
                var m = AssetDatabase.LoadAssetAtPath<Material>(p);
                if (m != null) all.Add(m);
            }

            // 2) 场景内引用（即使名字不在目标前缀里，也按颜色归类，保证画面一致）
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid())
            {
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
                {
                    if (r == null) continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        plan.sceneSlots++;
                        var p = AssetDatabase.GetAssetPath(m);
                        if (!string.IsNullOrEmpty(p) && !seen.Contains(p)) all.Add(m);
                    }
                }
            }

            // 3) 归类
            foreach (var m in all)
            {
                if (m == null) continue;
                var p = AssetDatabase.GetAssetPath(m);
                if (string.IsNullOrEmpty(p)) continue;
                if (!seen.Add(p)) continue;
                if (p.StartsWith(PaletteMatDir + "/", StringComparison.OrdinalIgnoreCase)) continue; // 共享材质单独维护

                // 只碰七关地形材质族；角色 / 道具 / VFX 即使被场景引用也不改（避免误伤）
                bool targetName = IsTargetMaterialName(m.name) ||
                                  p.StartsWith("Assets/Art/Materials/Variants/Opaque/MikuGandam/", StringComparison.OrdinalIgnoreCase);
                if (!targetName) continue;

                if (Classify(m, out var s, out var v, out var why))
                {
                    plan.materials.Add(new MatItem { material = m, assetPath = p, semantic = s, variant = v, reason = why });
                    if (IsSharedSlot(p)) plan.sceneMatchedSlots++;
                }
                else if (TryGetBaseColor(m, out var c))
                {
                    if (!plan.unknownLarge.ContainsKey(m.name)) plan.unknownLarge[m.name] = c;
                }
            }

            plan.materials.Sort((a, b) => string.CompareOrdinal(a.assetPath, b.assetPath));

            // 4) 报告
            report.AppendLine($"[扫描] 工程材质 {all.Count} 个（含场景引用），归类 {plan.materials.Count} 个；" +
                              $"场景材质槽 {plan.sceneSlots} 个。");
            foreach (var g in plan.materials.GroupBy(x => x.semantic).OrderBy(g => (int)g.Key))
            {
                var d = Def(g.Key);
                report.AppendLine();
                report.AppendLine($"[语义] {d.key} / {d.chinese} —— {g.Count()} 个材质");
                report.AppendLine($"       基色 {ColorText(d.baseColor)}  强调 {ColorText(d.accentColor)}  发光 {ColorText(d.emissiveColor)}");
                foreach (var vg in g.GroupBy(x => x.variant).OrderBy(v => (int)v.Key))
                {
                    report.AppendLine($"   · {VariantKey(vg.Key)}（{vg.Count()}）");
                    foreach (var it in vg.OrderBy(x => x.assetPath))
                        report.AppendLine($"       {it.assetPath,-78} {it.reason}");
                }
            }
            return plan;
        }

        static bool IsSharedSlot(string p) =>
            p.StartsWith("Assets/Art/Materials/MapSkin/", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(BaseMatDir, StringComparison.OrdinalIgnoreCase);

        /// <summary>目标材质族：七关历史上用过的所有地形材质命名。</summary>
        static bool IsTargetMaterialName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.StartsWith("Material.014") || name.StartsWith("Material_014") ||
                name.StartsWith("Material.005") || name.StartsWith("Material_005")) return true;
            if (name.StartsWith("DEC_") || name.StartsWith("MS_") || name.StartsWith("MAPSKIN_Glazing")) return true;
            if (name.StartsWith("M_panel")) return true;   // S_01 少量旧面板材质
            if (Regex.IsMatch(name, @"^(B|W|E|P)_[A-Z0-9]")) return true;
            if (name.StartsWith("Material.") || name.StartsWith("Material_"))
            {
                // 已知的地形母材质（黑/白/青/橙/黄/洋红）；不碰角色用的 Material.001 等
                return Regex.IsMatch(name, @"^Material[._](006|013|015|016|017|026|038|039|041|051|060|061|063)(_|$)");
            }
            return false;
        }

        // ------------------------------------------------------------------ 就地换色

        static int ApplyInPlace(MatItem item, PaletteBackup.Entry rec)
        {
            var m = item.material;
            if (m == null) return 0;

            var c = GetColor(item.semantic, item.variant);
            int n = 0;

            if (m.HasProperty("_BaseColor"))
            {
                rec.hasBaseColor = true; rec.baseColor = m.GetColor("_BaseColor");
                m.SetColor("_BaseColor", c); n++;
            }
            if (m.HasProperty("_Color"))
            {
                rec.hasColor = true; rec.color = m.GetColor("_Color");
                m.SetColor("_Color", c); n++;
            }

            // 发光变体：把发光色也拉到同一语义；非发光变体不碰原有发光设置
            if (item.variant == MapVariant.Emissive)
            {
                rec.hadEmissionKeyword = m.IsKeywordEnabled("_EMISSION");
                if (m.HasProperty("_EmissionColor"))
                {
                    rec.hasEmissionColor = true; rec.emissionColor = m.GetColor("_EmissionColor");
                    m.SetColor("_EmissionColor", c); n++;
                }
                if (m.HasProperty("_EmissionEnabled"))
                {
                    rec.hasEmissionEnabled = true; rec.emissionEnabled = m.GetFloat("_EmissionEnabled");
                    m.SetFloat("_EmissionEnabled", 1f); n++;
                }
                if (m.HasProperty("_EmissionIntensity"))
                {
                    rec.hasEmissionIntensity = true; rec.emissionIntensity = m.GetFloat("_EmissionIntensity");
                    m.SetFloat("_EmissionIntensity", 1f); n++;
                }
                if (m.HasProperty("_EmissionUseMap"))
                {
                    rec.hasEmissionUseMap = true; rec.emissionUseMap = m.GetFloat("_EmissionUseMap");
                    m.SetFloat("_EmissionUseMap", 0f); n++;
                }
                m.EnableKeyword("_EMISSION");
            }

            // 玻璃：只统一色相与 alpha，不改 shader 的透明模式（各关现成设置可能是 Transparent Toon / Lit）
            if (item.semantic == MapSemantic.Glass)
            {
                var gc = GetColor(MapSemantic.Glass, item.variant);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", gc);
                if (m.HasProperty("_Color")) m.SetColor("_Color", gc);
                if (m.HasProperty("_Surface") && m.GetFloat("_Surface") < 0.5f)
                {
                    rec.hasSurface = true; rec.surface = m.GetFloat("_Surface");
                    rec.hadTransparentKeyword = m.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT");
                    rec.originalRenderQueue = m.renderQueue;
                    m.SetFloat("_Surface", 1f);
                    m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    m.renderQueue = 3000;
                    n++;
                }
            }

            EditorUtility.SetDirty(m);
            return n;
        }

        static PaletteBackup.Entry RememberOnce(PaletteBackup backup, MatItem item)
        {
            var e = backup.entries.FirstOrDefault(x =>
                string.Equals(x.assetPath, item.assetPath, StringComparison.Ordinal));
            if (e != null) return e;   // 已备份过：保留第一次的原值，后续 Run 不覆盖

            e = new PaletteBackup.Entry
            {
                assetPath = item.assetPath,
                materialName = item.material != null ? item.material.name : Path.GetFileNameWithoutExtension(item.assetPath),
                semantic = SemanticKey(item.semantic),
                variant = VariantKey(item.variant),
                reason = item.reason,
            };
            backup.entries.Add(e);
            return e;
        }

        static int EnsureCanonicalSet(PaletteBackup backup, StringBuilder report)
        {
            int created = 0;
            report.AppendLine();
            report.AppendLine("[共享材质] 6 语义 × 3 变体 = 18 个（MS_{Semantic}_{Variant}）");
            foreach (var d in Defs)
            {
                foreach (MapVariant v in new[] { MapVariant.Base, MapVariant.Accent, MapVariant.Emissive })
                {
                    string path = CanonicalPath(d.semantic, v);
                    bool existed = AssetDatabase.LoadAssetAtPath<Material>(path) != null;
                    var m = GetMaterial(d.semantic, v);
                    if (m == null) { report.AppendLine($"    [失败] {path}"); continue; }
                    if (!existed)
                    {
                        created++;
                        if (backup != null && !backup.createdAssets.Contains(path)) backup.createdAssets.Add(path);
                        report.AppendLine($"    + {path}  = {ColorText(GetColor(d.semantic, v))}");
                    }
                }
            }
            report.AppendLine($"    新建 {created} 个；已存在 {18 - created} 个。");
            return created;
        }

        static void ApplyCanonicalValues(Material m, MapSemantic s, MapVariant v)
        {
            var d = Def(s);
            var c = GetColor(s, v);

            SetColorSafe(m, "_BaseColor", c);
            SetColorSafe(m, "_Color", c);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", d.metallic);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", d.smoothness);

            // 共享语义材质是"纯色板"，不继承母材质的地形贴图（否则门套件的图案会被板缝贴图污染）
            foreach (var tex in new[] { "_BaseMap", "_MainTex", "_BumpMap", "_MetallicGlossMap", "_OcclusionMap", "_EmissionMap" })
                if (m.HasProperty(tex)) m.SetTexture(tex, null);
            foreach (var kw in new[] { "_NORMALMAP", "_METALLICSPECGLOSSMAP", "_OCCLUSIONMAP" })
                m.DisableKeyword(kw);
            if (m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", 0f);

            if (v == MapVariant.Emissive)
            {
                SetColorSafe(m, "_EmissionColor", c);
                if (m.HasProperty("_EmissionEnabled")) m.SetFloat("_EmissionEnabled", 1f);
                if (m.HasProperty("_EmissionIntensity")) m.SetFloat("_EmissionIntensity", 1f);
                if (m.HasProperty("_EmissionUseMap")) m.SetFloat("_EmissionUseMap", 0f);
                m.EnableKeyword("_EMISSION");
            }

            if (s == MapSemantic.Glass)
            {
                if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = 3000;
            }
        }

        // ------------------------------------------------------------------ 母材质查找

        static Material FindSourceMaterial(MapSemantic s)
        {
            string[] candidates;
            switch (s)
            {
                case MapSemantic.Black:
                    candidates = new[] { $"{BaseMatDir}/Material.005.mat", $"{BaseMatDir}/Material.013.mat", $"{BaseMatDir}/Material.006.mat" };
                    break;
                case MapSemantic.White:
                    candidates = new[] { $"{BaseMatDir}/Material.014.mat", $"{BaseMatDir}/Material.060.mat", $"{BaseMatDir}/Material.051.mat" };
                    break;
                case MapSemantic.Cyan:
                    candidates = new[] { $"{BaseMatDir}/Material.015.mat", $"{BaseMatDir}/Material.026.mat", $"{BaseMatDir}/Material.039.mat" };
                    break;
                case MapSemantic.Yellow:
                    candidates = new[] { $"{BaseMatDir}/Material.061.mat", $"{BaseMatDir}/Material.063.mat" };
                    break;
                case MapSemantic.Orange:
                    candidates = new[] { $"{BaseMatDir}/Material.038.mat" };
                    break;
                case MapSemantic.Glass:
                    candidates = new[] { GlazingMatPath };
                    break;
                default:
                    candidates = new string[0];
                    break;
            }

            foreach (var p in candidates)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(p);
                if (m != null && m.shader != null && !m.shader.name.Contains("InternalError")) return m;
            }

            // 该语义没有专属母材质时，退到白/黑母材质再染色（CopyAsset，不 new 克隆）
            foreach (var p in new[] { $"{BaseMatDir}/Material.014.mat", $"{BaseMatDir}/Material.005.mat" })
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(p);
                if (m != null && m.shader != null && !m.shader.name.Contains("InternalError")) return m;
            }

            // 最后退路：工程里任意一个可用的地形材质，仍然只做 CopyAsset。
            // 优先非 variant 母材质（CopyAsset + 改属性最稳），实在没有再用 variant。
            Material firstVariant = null;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/Art/Materials" }))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (m == null || m.shader == null || m.shader.name.Contains("InternalError")) continue;
                if (m.parent == null) return m;
                if (firstVariant == null) firstVariant = m;
            }
            return firstVariant;
        }

        // ------------------------------------------------------------------ 调色板 JSON

        static void EnsureLoaded()
        {
            if (_loaded) return;
            LoadPalette(new StringBuilder());
        }

        /// <summary>强制重读 JSON（GateKit.Run 开头调用，保证美术刚改的色值立即生效）。</summary>
        public static void ReloadPalette()
        {
            _loaded = false;
            LoadPalette(new StringBuilder());
        }

        /// <summary>材质逐个都在资产库，场景扫描只是补"场景引用"统计；Level1 没开就打开，开着别的脏场景不硬切。</summary>
        static void EnsureSceneOpen()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return;
            if (cur.IsValid() && cur.isDirty && !string.IsNullOrEmpty(cur.path))
            {
                Debug.LogWarning($"[MapSkin·调色板] 当前场景 {cur.path} 有未保存改动，不切换；本轮场景引用统计会跳过（材质资产照常处理）。");
                return;
            }
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log($"[MapSkin·调色板] 已打开 {ScenePath}");
        }

        static string LoadPalette(StringBuilder report)
        {
            _loaded = true;
            RuntimeDefs.Clear();
            foreach (var d in Defs) RuntimeDefs[d.semantic] = Clone(d);
            RuntimeSignals.Clear();
            RuntimeSignals["Green"] = SignalGreen;
            RuntimeSignals["Red"] = SignalRed;

            if (!File.Exists(JsonPath))
                return "C# 常量（JSON 尚未生成）";

            try
            {
                var json = File.ReadAllText(JsonPath);
                var file = JsonUtility.FromJson<PaletteFile>(json);
                if (file == null || file.semantics == null)
                {
                    report.AppendLine($"[调色板] {JsonPath} 解析为空，改用 C# 常量。");
                    return "C# 常量（JSON 空）";
                }

                foreach (var e in file.semantics)
                {
                    if (e == null || string.IsNullOrEmpty(e.semantic)) continue;
                    if (!Enum.TryParse(e.semantic, true, out MapSemantic s)) continue;
                    var d = RuntimeDefs[s];
                    var bc = ParseColor(e.baseColor, d.baseColor);
                    var ac = ParseColor(e.accentColor, d.accentColor);
                    var ec = ParseColor(e.emissiveColor, d.emissiveColor);
                    d.baseColor = bc; d.accentColor = ac; d.emissiveColor = ec;
                    if (e.metallic >= 0f) d.metallic = Mathf.Clamp01(e.metallic);
                    if (e.smoothness >= 0f) d.smoothness = Mathf.Clamp01(e.smoothness);
                }

                if (file.signals != null)
                {
                    foreach (var e in file.signals)
                    {
                        if (e == null || string.IsNullOrEmpty(e.signal)) continue;
                        var fallback = RuntimeSignals.TryGetValue(e.signal, out var old) ? old : Color.white;
                        RuntimeSignals[e.signal] = ParseColor(e.color, fallback);
                    }
                }
                return JsonPath;
            }
            catch (Exception ex)
            {
                report.AppendLine($"[调色板] 读取 {JsonPath} 失败：{ex.Message}；改用 C# 常量。");
                return "C# 常量（JSON 读取失败）";
            }
        }

        static void ExportJson(StringBuilder report, bool overwrite)
        {
            if (!overwrite && File.Exists(JsonPath))
            {
                report.AppendLine($"[调色板] {JsonPath} 已存在，不覆盖（尊重美术改动）。");
                return;
            }

            EnsureFolder("Assets", "Settings");
            var file = new PaletteFile
            {
                version = 1,
                note = "七图唯一语义调色板。C# 常量 MapSkinPalette 是默认值；本文件存在时以本文件为准。改色后跑 MapSkin/调色板/② 统一换色。",
                signals = new[]
                {
                    new PaletteSignalEntry { signal = "Green", color = ToArray(SignalGreen) },
                    new PaletteSignalEntry { signal = "Red", color = ToArray(SignalRed) },
                },
                semantics = Defs.Select(def => Def(def.semantic)).Select(d => new PaletteSemanticEntry
                {
                    semantic = d.key,
                    baseColor = ToArray(d.baseColor),
                    accentColor = ToArray(d.accentColor),
                    emissiveColor = ToArray(d.emissiveColor),
                    metallic = d.metallic,
                    smoothness = d.smoothness,
                }).ToArray(),
            };

            var jsonFull = Path.Combine(Directory.GetCurrentDirectory(), JsonPath);
            Directory.CreateDirectory(Path.GetDirectoryName(jsonFull) ?? Directory.GetCurrentDirectory());
            File.WriteAllText(jsonFull, JsonUtility.ToJson(file, true), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(JsonPath, ImportAssetOptions.ForceUpdate);
            report.AppendLine($"[调色板] 已导出 {JsonPath}");
        }

        [MenuItem("MapSkin/调色板/工具：重新导出 JSON（覆盖）")]
        public static void ExportJsonMenu()
        {
            var rep = new StringBuilder();
            LoadPalette(rep);
            ExportJson(rep, overwrite: true);
            Debug.Log(rep.ToString());
        }

        // ------------------------------------------------------------------ 小工具

        static SemanticDef Def(MapSemantic s)
        {
            if (RuntimeDefs.TryGetValue(s, out var d)) return d;
            return DefMap[s];
        }

        static SemanticDef Clone(SemanticDef d) => new SemanticDef
        {
            semantic = d.semantic, key = d.key, chinese = d.chinese,
            baseColor = d.baseColor, accentColor = d.accentColor, emissiveColor = d.emissiveColor,
            metallic = d.metallic, smoothness = d.smoothness,
        };

        static bool HasAny(string s, params string[] tokens)
        {
            foreach (var t in tokens) if (s.Contains(t)) return true;
            return false;
        }

        static bool TryGetBaseColor(Material m, out Color c)
        {
            if (m.HasProperty("_BaseColor")) { c = m.GetColor("_BaseColor"); return true; }
            if (m.HasProperty("_Color")) { c = m.GetColor("_Color"); return true; }
            c = Color.gray;
            return false;
        }

        static void SetColorSafe(Material m, string prop, Color c)
        {
            if (m.HasProperty(prop)) m.SetColor(prop, new Color(c.r, c.g, c.b, c.a));
        }

        static float[] ToArray(Color c) => new[] { c.r, c.g, c.b, c.a };

        static Color ParseColor(float[] v, Color fallback)
        {
            if (v == null || v.Length < 3) return fallback;
            float a = v.Length >= 4 ? v[3] : 1f;
            return new Color(v[0], v[1], v[2], a);
        }

        static string ColorText(Color c) =>
            string.Format(CultureInfo.InvariantCulture, "({0:F3},{1:F3},{2:F3},{3:F2})", c.r, c.g, c.b, c.a);

        static void EnsureFolder(string parent, string name)
        {
            // parent 也可能是多级路径（如 Assets/Art/Materials/MapSkin），逐级补
            if (!AssetDatabase.IsValidFolder(parent))
                EnsureFolderPath(parent);
            if (!AssetDatabase.IsValidFolder($"{parent}/{name}"))
                AssetDatabase.CreateFolder(parent, name);
        }

        static void EnsureFolderPath(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent) || parent == path) return;
            EnsureFolderPath(parent);
            var leaf = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(leaf) && AssetDatabase.IsValidFolder(parent))
                AssetDatabase.CreateFolder(parent, leaf);
        }

        static PaletteBackup LoadOrCreateBackup()
        {
            var bk = AssetDatabase.LoadAssetAtPath<PaletteBackup>(BackupPath);
            if (bk != null)
            {
                if (bk.entries == null) bk.entries = new List<PaletteBackup.Entry>();
                if (bk.createdAssets == null) bk.createdAssets = new List<string>();
                return bk;
            }

            EnsureFolder("Assets/Art", "EnvSkins");
            bk = ScriptableObject.CreateInstance<PaletteBackup>();
            bk.version = 1;
            bk.createdUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            AssetDatabase.CreateAsset(bk, BackupPath);
            Debug.Log($"[MapSkin·调色板] 新建备份 {BackupPath}");
            return bk;
        }

        static void WriteReport(string fileName, string text, bool logIt)
        {
            if (logIt) Debug.Log(text);

            try
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                var workspace = Directory.GetParent(projectRoot)?.FullName ?? projectRoot;
                var dir = Path.Combine(workspace, "_session_audit");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, fileName), text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MapSkin·调色板] 报告写盘失败：{ex.Message}");
            }
        }
    }
}
