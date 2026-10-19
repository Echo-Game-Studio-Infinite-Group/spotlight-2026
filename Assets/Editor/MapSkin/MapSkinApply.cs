using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// Level1 灰盒 → 正式材质（语义色 → 材质族）批量就地换装。
    ///
    /// 语义表权威：`docs/技术/地图灰盒语义色.md`（颜色→语义）+ `docs/技术/NPR管线打光与制图规范.md`
    /// （不透明件用 NPR 母材质 Lit tag 版；就地改 shader 保住 GUID 与原有引用，禁止 throwaway 克隆 §2.4）。
    ///
    /// 干的事（只碰材质资产，不动场景对象结构/名字/碰撞）：
    ///   1) 就地改 .mat：shader = `OurFunction/NPR/Deferred Toon`（母材质 NPRBase.shader，第①步已打 Lit tag）；
    ///      toon 三件套 _ShadowColor(0.16,0.29,0.43) / _ToonSoftness 0.15 / _ToonThreshold 1.0（规范 §2.1 标准参数）；
    ///      _BaseMap(BC,sRGB) / _BumpMap(N,法线) / _MetallicGlossMap(MS,线性) 指向工程内贴图 + 对应 keyword；
    ///      贴图 tiling = UV 基准尺度 / 贴图 tile_m（UV 由 MapSkinUvBake 烘成"世界坐标/基准尺度"）。
    ///   2) 贴图缺失的族在日志里点名，等纹理线补（本工程当前只有白墙板 / 白地台 / 黑强调三套）。
    ///   3) 打印对账日志：改了哪些 .mat、各覆盖多少材质槽。
    ///
    /// Material Variant（`Assets/Art/Materials/Variants/Opaque/**`）单独处理：它们的 shader 由母链继承、
    /// Unity 不允许改（会报错），所以只改属性；其 shader 继承的正是 NPRBase.shader，第①步的 Lit tag 自动跟随。
    ///
    /// 幂等：属性按表写死，重复跑结果一致。
    /// 菜单：MapSkin/② Level1 语义材质换装(就地改 .mat)
    /// 批处理：-executeMethod MapSkin.MapSkinApply.Run
    /// </summary>
    public static class MapSkinApply
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string NprShaderName = "OurFunction/NPR/Deferred Toon";
        const string NprShaderPath = "Assets/Shaders/NPR/NPRBase.shader";
        const string BaseMatDir = "Assets/Art/Models/MikuGandam/materials";
        const string VariantDir = "Assets/Art/Materials/Variants/Opaque/MikuGandam";
        const string PrefabDir = "Assets/Prefabs/Maps/Level1";
        const string ImgRoot = "Assets/Art/Images/HST_Prags";
        /// <summary>贴图集来源（纹理线导出目录，工程外）。目录不存在时日志点名，不阻塞其他族。</summary>
        const string ExportRoot = "F:/VSCode/工业级渲染/exports/textures";

        // 带色（对白模太暗的旧值 0.16,0.29,0.43 已弃用）——2026-10-18 对齐 TA 示例场景 TATestScene：
        // TA 的材质影带色是 (0.30,0.48,0.53)，那套是给亮堂室内用的；旧值那套是给深色两厅用的。
        static readonly Color ShadowColor = new Color(0.30f, 0.48f, 0.53f);
        const float ToonSoftness = 0.15f;
        const float ToonThreshold = 1.0f;

        /// <summary>NPR 母材质的三个贴图槽（属性名以 NPRBase.shader 的 Properties 块实名声明为准）。</summary>
        static readonly string[] TexSlots = { "_BaseMap", "_BumpMap", "_MetallicGlossMap" };

        // ---------------------------------------------------------------- 语义 → 材质族表

        class Family
        {
            public string key;            // 日志用族名
            public string semantics;      // 语义（权威：地图灰盒语义色.md）
            public string[] mats;         // 基底材质名（Assets/Art/Models/MikuGandam/materials/<名>.mat）
            public string texName;        // 贴图集名（Assets/Art/Images/HST_Prags/<texName>/）；null = 缺贴图
            public float uvBase;          // 该族贴图对应的 UV 基准尺度（墙/柱 3.6、地 4.8）
            public float[] tileFallback;  // params.json 缺失时的 tile_m 兜底
            public Color baseColor;       // 写死的 _BaseColor（≤1.0）
            public bool tintTexture;      // true = _BaseColor 当色相在贴图上（青地板：保留语义色，不乘没贴图）
            public float smoothness = -1f; // ≥0 = 写死 _Smoothness（-1 不动）；地坪族要 0.6–0.75 给反射
            public float metallic = -1f;   // ≥0 = 写死 _Metallic（-1 不动）；地坪族 ≤0.05
        }

        static readonly Family[] Families =
        {
            new Family {
                key = "白·墙板族", semantics = "地板 / 不可攀爬柱（未标注体块，中性灰底）",
                mats = new[] { "Material.014", "Material.060", "Material.051" },
                texName = "T_HS_PanelWall_White", uvBase = 3.6f, tileFallback = new[] { 1.792f, 3.4f },
                baseColor = new Color(0.80f, 0.80f, 0.80f), tintTexture = false,
            },
            new Family {
                key = "黑·可攀爬族", semantics = "可攀爬墙壁（43-48 等六面墙本体）",
                mats = new[] { "Material.005", "Material.006", "Material.013", "Material.016", "Material.017", "Material.041" },
                texName = "T_HS_DarkAccent_Black", uvBase = 3.6f, tileFallback = new[] { 1.2f, 1.2f },
                // 原值是纯黑/近黑 0.038：贴图乘 0 等于没有贴图。抬到 0.20 让深色族的板缝/焊缝刻线可读，
                // 仍是全场最暗一族（比白族 0.80 暗 4 倍），"黑 = 可攀爬"的辨识度不丢。
                baseColor = new Color(0.20f, 0.21f, 0.23f), tintTexture = false,
            },
            new Family {
                key = "青·标注地板族", semantics = "被额外标注的地板（特殊地板）",
                mats = new[] { "Material.015", "Material.026", "Material.039" },
                texName = "T_HS_FloorPlate_White", uvBase = 4.8f, tileFallback = new[] { 4.8f, 4.8f },
                // 用白地台贴图 + 青色调作 _BaseColor，保住"青 = 被标注地板"的语义（纯色会丢板缝细节）
                baseColor = new Color(0.18f, 0.56f, 0.58f), tintTexture = true,
                // 地坪族给反射（2026-10-18 派单 B.4）：_Smoothness 0.70（0.6–0.75 区间）、_Metallic 0.05（上限内）
                smoothness = 0.70f, metallic = 0.05f,
            },
            new Family {
                key = "橙·进入点", semantics = "关卡进入点",
                mats = new[] { "Material.038" }, texName = null, uvBase = 3.6f,
                tileFallback = new[] { 1f, 1f }, baseColor = new Color(0.87f, 0.27f, 0.04f), tintTexture = false,
            },
            new Family {
                key = "黄·离开点", semantics = "关卡离开点",
                mats = new[] { "Material.061" }, texName = null, uvBase = 3.6f,
                tileFallback = new[] { 1f, 1f }, baseColor = new Color(0.80f, 0.79f, 0.08f), tintTexture = false,
            },
            new Family {
                key = "洋红·滑铲标志", semantics = "向下滑铲标志",
                mats = new[] { "Material.063" }, texName = null, uvBase = 3.6f,
                tileFallback = new[] { 1f, 1f }, baseColor = new Color(0.89f, 0.23f, 0.46f), tintTexture = false,
            },
        };

        // ---------------------------------------------------------------- 彩蛋入口

        /// <summary>语义表里全部基底材质名（去重）。UV 重投影按它挑对象——只有吃到新贴图的件才需要重投影。</summary>
        public static string[] SkinMatBaseNames
        {
            get
            {
                var l = new List<string>();
                foreach (var f in Families) l.AddRange(f.mats);
                l.Sort(System.StringComparer.Ordinal);
                return l.Distinct().ToArray();
            }
        }

        [MenuItem("MapSkin/② Level1 语义材质换装(就地改 .mat)")]
        public static void RunMenu() => Run();

        /// <summary>一键链：① Lit tag → ② 材质换装 → ④ 打光写盘（批处理只能给一个 -executeMethod，故需此入口）。
        /// -executeMethod MapSkin.MapSkinApply.PipelineAll</summary>
        [MenuItem("MapSkin/⑥ 一键：①Lit tag + ②换装 + ④打光(写盘)")]
        public static void PipelineAll()
        {
            NprLitTagFix.Run();
            Run();
            MapSkinLighting.Run();
        }

        /// <summary>材质槽对账要在场景上跑：Level1 没开就打开（有未保存改动时不强开，只警告）。</summary>
        static void EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return;
            if (cur.IsValid() && cur.isDirty)
            {
                Debug.LogWarning("[MapSkin] 当前场景有未保存改动，不替你切换场景；本轮的槽位对账会漏场景统计（材质资产照改）");
                return;
            }
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        public static void Run()
        {
            EnsureScene();
            var shader = Shader.Find(NprShaderName);
            if (shader == null)
            {
                Debug.LogError($"[MapSkin] 找不到 shader {NprShaderName}（{NprShaderPath} 是否编译通过？）");
                return;
            }
            var shaderGuid = AssetDatabase.AssetPathToGUID(NprShaderPath);
            if (shaderGuid == null || shaderGuid.Length == 0)
                Debug.LogWarning("[MapSkin] 取不到 NPRBase.shader 的 GUID，variant 的 shader 继承校验会跳过");

            EnsureFolders();

            var rep = new StringBuilder();
            var touched = new List<string>();          // 改过的 .mat 路径
            var missingTex = new List<string>();       // 缺贴图的族

            // 1) 先把该有的贴图拉进工程并配好导入设置
            var texByFamily = new Dictionary<string, Dictionary<string, Texture2D>>();
            foreach (var f in Families)
            {
                if (f.texName == null) continue;
                var map = EnsureTextureSet(f.texName, rep);
                if (map == null) { missingTex.Add($"{f.key} → {f.texName}（导出目录 {ExportRoot}/{f.texName} 缺失）"); continue; }
                texByFamily[f.texName] = map;
            }

            // 2) 逐个材质族就地改
            var familyOf = new Dictionary<string, Family>();  // 材质资产路径 → 族
            foreach (var f in Families)
            {
                var rows = new List<string>();
                int slots = 0, mats = 0;

                // 2a) 基底材质：shader + 属性一起改
                foreach (var n in f.mats)
                {
                    var p = $"{BaseMatDir}/{n}.mat";
                    var m = AssetDatabase.LoadAssetAtPath<Material>(p);
                    if (m == null) { rep.AppendLine($"  [缺] {p} 不存在"); continue; }
                    ApplyToMaterial(m, f, shader, texByFamily, rep, isVariant: false);
                    familyOf[p] = f; mats++; touched.Add(p);
                    rows.Add("    " + p);
                }

                // 2b) 同名 Material Variant（Assets/Art/Materials/Variants/Opaque 下 <基底名>_<hash>.mat）
                foreach (var p in FindVariantsOf(f.mats))
                {
                    var m = AssetDatabase.LoadAssetAtPath<Material>(p);
                    if (m == null) continue;
                    ApplyToMaterial(m, f, shader, texByFamily, rep, isVariant: true);
                    familyOf[p] = f; mats++; touched.Add(p);
                    rows.Add("    " + p + "  (variant)");
                }

                rep.AppendLine($"[族] {f.key} — {f.semantics}");
                if (f.texName == null)
                    rep.AppendLine($"    贴图：无（族内暂用纯色 _BaseColor 呈现，等纹理线补贴图集）");
                else if (!texByFamily.ContainsKey(f.texName))
                    rep.AppendLine($"    贴图：{f.texName} 未取到（见下方缺失清单）");
                else
                    rep.AppendLine($"    贴图：{f.texName}，UV 基准 {f.uvBase}m，tiling = {f.uvBase}/tile_m");
                rep.AppendLine($"    改动材质 {mats} 个：");
                rep.AppendLine(string.Join("\n", rows));
            }

            AssetDatabase.SaveAssets();
            EditorUtility.SetDirty(AssetDatabase.LoadAssetAtPath<Shader>(NprShaderPath));

            // 3) 对账：各材质覆盖多少材质槽（场景内全渲染器，含预制体实例；另有 Level1 预制体库统计）
            rep.AppendLine();
            rep.AppendLine(CountSlots(familyOf, out var prefabRep));
            rep.AppendLine(prefabRep);

            if (missingTex.Count > 0)
            {
                rep.AppendLine("[缺贴图·等纹理线补]");
                foreach (var s in missingTex) rep.AppendLine("    " + s);
            }

            Debug.Log($"[MapSkin] 语义材质换装完成：动过 {touched.Distinct().Count()} 个 .mat 资产\n{rep}");
        }

        // ---------------------------------------------------------------- 单个材质落地

        static void ApplyToMaterial(Material m, Family f, Shader shader,
            Dictionary<string, Dictionary<string, Texture2D>> texByFamily, StringBuilder rep, bool isVariant)
        {
            // --- shader：就地改（GUID 不变，场景/prefab 引用全保住）---
            if (!isVariant)
            {
                if (m.shader != shader) m.shader = shader;
            }
            else
            {
                // Material Variant 的 shader 由母链继承（Unity 不允许改）：只校验，别硬改
                if (m.shader != shader)
                    rep.AppendLine($"    [注意] variant {m.name} 的 shader 是 {m.shader.name}，非 {NprShaderName}；" +
                                   $"variant 不可改 shader，已跳过 shader（属性照改）");
            }

            // --- toon 三件套（规范 §2.1 标准参数）---
            m.SetColor("_ShadowColor", ShadowColor);
            m.SetFloat("_ToonSoftness", ToonSoftness);
            m.SetFloat("_ToonThreshold", ToonThreshold);

            // --- 基色（≤1.0 硬约束）---
            var c = f.baseColor;
            m.SetColor("_BaseColor", new Color(Mathf.Min(c.r, 1f), Mathf.Min(c.g, 1f), Mathf.Min(c.b, 1f), 1f));

            // --- 反射相关的粗糙/金属度（地坪族：给反射；其余族不动）---
            if (f.smoothness >= 0f) m.SetFloat("_Smoothness", Mathf.Clamp01(f.smoothness));
            if (f.metallic >= 0f) m.SetFloat("_Metallic", Mathf.Clamp01(f.metallic));

            // --- 贴图三槽 + tiling ---
            if (f.texName != null && texByFamily.TryGetValue(f.texName, out var map))
            {
                var tile = ReadTileM(f.texName, f.tileFallback);
                // uv 已烘成"世界坐标 / uvBase"，贴图想按 tile_m 米一循环 → tiling = uvBase / tile_m
                var scale = new Vector2(f.uvBase / Mathf.Max(tile[0], 1e-4f), f.uvBase / Mathf.Max(tile[1], 1e-4f));

                m.SetTexture("_BaseMap", map["BC"]);
                m.SetTextureScale("_BaseMap", scale);
                m.SetTextureOffset("_BaseMap", Vector2.zero);

                m.SetTexture("_BumpMap", map["N"]);
                m.SetTextureScale("_BumpMap", scale);
                m.SetTextureOffset("_BumpMap", Vector2.zero);
                m.SetFloat("_BumpScale", 1f);
                m.EnableKeyword("_NORMALMAP");

                m.SetTexture("_MetallicGlossMap", map["MS"]);
                m.SetTextureScale("_MetallicGlossMap", scale);
                m.SetTextureOffset("_MetallicGlossMap", Vector2.zero);
                m.SetFloat("_Metallic", 0f);
                m.SetFloat("_SmoothnessTextureChannel", 0f);  // 0 = Metallic Alpha
                m.EnableKeyword("_METALLICSPECGLOSSMAP");

                m.SetFloat("_Surface", 0f);                    // 不透明（规范 §2.2 透明件另走 Transparent Toon）
                m.SetFloat("_AlphaClip", 0f);
                m.DisableKeyword("_ALPHATEST_ON");
            }
            else
            {
                // 缺贴图族：把槽位显式清空（避免残留上轮误挂的贴图），保留纯色语义
                foreach (var s in TexSlots)
                {
                    if (m.GetTexture(s) != null) m.SetTexture(s, null);
                    m.SetTextureScale(s, Vector2.one);
                    m.SetTextureOffset(s, Vector2.zero);
                }
                if (m.IsKeywordEnabled("_NORMALMAP")) m.DisableKeyword("_NORMALMAP");
                if (m.IsKeywordEnabled("_METALLICSPECGLOSSMAP")) m.DisableKeyword("_METALLICSPECGLOSSMAP");
            }

            EditorUtility.SetDirty(m);
        }

        // ---------------------------------------------------------------- variant 查找

        static List<string> FindVariantsOf(string[] baseNames)
        {
            var list = new List<string>();
            if (!AssetDatabase.IsValidFolder(VariantDir)) return list;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { VariantDir }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var file = Path.GetFileNameWithoutExtension(p);
                // 命名口径：<基底名>_<hash>；也兼容变体直接取了基底名的情形
                foreach (var b in baseNames)
                    if (file == b || file.StartsWith(b + "_")) { list.Add(p); break; }
            }
            list.Sort(System.StringComparer.Ordinal);
            return list;
        }

        // ---------------------------------------------------------------- 贴图入库

        static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Art/Images"))
                AssetDatabase.CreateFolder("Assets/Art", "Images");
            if (!AssetDatabase.IsValidFolder(ImgRoot))
                AssetDatabase.CreateFolder("Assets/Art/Images", "HST_Prags");
        }

        /// <summary>确保工程内有该贴图集（缺则从导出目录拷入），并配好 BC/N/MS 三张的导入设置。
        /// 同时被 MapSkinLineDispatch 复用（刻线族 25 套）。</summary>
        public static Dictionary<string, Texture2D> EnsureTextureSet(string texName, StringBuilder rep)
        {
            int ignored = 0;
            return EnsureTextureSet(texName, rep, ref ignored);
        }

        public static Dictionary<string, Texture2D> EnsureTextureSet(string texName, StringBuilder rep, ref int copied)
        {
            var dstDir = $"{ImgRoot}/{texName}";
            var srcDir = Path.Combine(ExportRoot, texName);
            if (!AssetDatabase.IsValidFolder(dstDir))
            {
                if (!Directory.Exists(srcDir))
                {
                    rep.AppendLine($"  [贴图] {texName}: 导出目录不存在 {srcDir}");
                    return null;
                }
                AssetDatabase.CreateFolder(ImgRoot, texName);
            }
            Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), dstDir));

            var map = new Dictionary<string, Texture2D>();
            foreach (var ch in new[] { "BC", "N", "MS" })
            {
                var rel = $"{dstDir}/{texName}_{ch}.png";
                var full = Path.Combine(Directory.GetCurrentDirectory(), rel);
                if (!File.Exists(full))
                {
                    var src = Path.Combine(srcDir, $"{texName}_{ch}.png");
                    if (!File.Exists(src)) { rep.AppendLine($"  [贴图] {texName}: 缺 {ch} 通道（{src}）"); continue; }
                    File.Copy(src, full, true);
                    copied++;
                }
                AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceUpdate);
                ConfigureImporter(rel, ch);
                var t = AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
                if (t == null) { rep.AppendLine($"  [贴图] {texName}: {ch} 导入失败 {rel}"); continue; }
                map[ch] = t;
            }
            if (map.Count >= 3) rep.AppendLine($"  [贴图] {texName}: BC/N/MS 就位（{dstDir}）");
            return map.Count == 0 ? null : map;
        }

        static void ConfigureImporter(string path, string channel)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return;
            bool dirty = false;
            if (ti.wrapMode != TextureWrapMode.Repeat) { ti.wrapMode = TextureWrapMode.Repeat; dirty = true; }
            if (ti.anisoLevel < 4) { ti.anisoLevel = 4; dirty = true; }
            if (ti.maxTextureSize < 2048) { ti.maxTextureSize = 2048; dirty = true; }
            if (ti.mipmapEnabled == false) { ti.mipmapEnabled = true; dirty = true; }
            if (channel == "N")
            {
                if (ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; dirty = true; }
                if (ti.sRGBTexture) { ti.sRGBTexture = false; dirty = true; }
            }
            else
            {
                bool wantSRGB = channel == "BC";
                if (ti.textureType != TextureImporterType.Default) { ti.textureType = TextureImporterType.Default; dirty = true; }
                if (ti.sRGBTexture != wantSRGB) { ti.sRGBTexture = wantSRGB; dirty = true; }
                if (ti.alphaIsTransparency) { ti.alphaIsTransparency = false; dirty = true; }
            }
            if (dirty) ti.SaveAndReimport();
        }

        /// <summary>读贴图集 params.json 的 tile_m（米/循环）；读不到用族兜底值。
        /// public：MapSkinLineDispatch（刻线族 25 套）也要用同一口径。</summary>
        public static float[] ReadTileM(string texName, float[] fallback)
        {
            foreach (var dir in new[] { Path.Combine(ExportRoot, texName),
                                        Path.Combine(Directory.GetCurrentDirectory(), ImgRoot, texName) })
            {
                var p = Path.Combine(dir, texName + "_params.json");
                if (!File.Exists(p)) continue;
                var txt = File.ReadAllText(p);
                var m = Regex.Match(txt, "\"tile_m\"\\s*:\\s*\\[\\s*([0-9.]+)\\s*,\\s*([0-9.]+)\\s*\\]");
                if (m.Success)
                    return new[] { float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                                   float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) };
            }
            return fallback;
        }

        // ---------------------------------------------------------------- 材质槽对账

        static string CountSlots(Dictionary<string, Family> familyOf, out string prefabRep)
        {
            var rep = new StringBuilder();
            var scene = EditorSceneManager.GetActiveScene();
            bool haveScene = scene.IsValid() && scene.path == ScenePath;
            if (!haveScene)
            {
                foreach (var s in EditorSceneManager.GetSceneManagerSetup())
                    if (s.path == ScenePath) { haveScene = true; break; }
            }

            var slot = new Dictionary<string, int>();   // 族 key → 槽数
            var slotPrefab = new Dictionary<string, int>();
            var unknown = new Dictionary<string, int>();

            void Tally(Renderer r, bool inPrefab)
            {
                foreach (var mm in r.sharedMaterials)
                {
                    if (mm == null) continue;
                    var p = AssetDatabase.GetAssetPath(mm);
                    string key;
                    if (p != null && p.Length > 0 && familyOf.TryGetValue(p, out var fam)) key = fam.key;
                    else
                    {
                        if (!inPrefab) unknown[mm.name] = unknown.TryGetValue(mm.name, out var u) ? u + 1 : 1;
                        continue;
                    }
                    var d = inPrefab ? slotPrefab : slot;
                    d[key] = d.TryGetValue(key, out var c) ? c + 1 : 1;
                }
            }

            if (haveScene)
            {
                foreach (var r in Object.FindObjectsOfType<Renderer>()) Tally(r, false);
                rep.AppendLine("[槽位对账·场景内（含预制体实例）] " + (slot.Count == 0 ? "无命中" : "") );
                foreach (var kv in slot.OrderByDescending(k => k.Value)) rep.AppendLine($"    {kv.Key}: {kv.Value} 槽");
            }
            else
            {
                rep.AppendLine("[槽位对账·场景内] Level1 未打开，跳过场景统计（材质资产已改完，打开即生效）");
            }

            // Level1 预制体库（引擎不打开 prefab 也能读，直接扫文本里的材质 GUID 引用）
            var byGuid = new Dictionary<string, int>();
            var guid2fam = new Dictionary<string, string>();
            foreach (var kv in familyOf)
            {
                var g = AssetDatabase.AssetPathToGUID(kv.Key);
                if (g != null && g.Length > 0) guid2fam[g] = kv.Value.key;
            }
            int files = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { PrefabDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                files++;
                var txt = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), path));
                foreach (Match m in Regex.Matches(txt, @"guid: ([0-9a-f]{32}), type: 2\}"))
                    if (guid2fam.TryGetValue(m.Groups[1].Value, out var k))
                        byGuid[k] = byGuid.TryGetValue(k, out var c) ? c + 1 : 1;
            }
            prefabRep = $"[槽位对账·{PrefabDir} 预制体库（{files} 个 prefab 的材质引用计数，含同一槽被多实例引用）]";
            foreach (var kv in byGuid.OrderByDescending(k => k.Value)) prefabRep += $"\n    {kv.Key}: {kv.Value} 处";

            if (unknown.Count > 0)
            {
                rep.AppendLine("[场景内未纳入语义表、保持原样的材质 Top8]");
                foreach (var kv in unknown.OrderByDescending(k => k.Value).Take(8))
                    rep.AppendLine($"    {kv.Key}: {kv.Value} 槽");
            }
            return rep.ToString();
        }
    }
}
