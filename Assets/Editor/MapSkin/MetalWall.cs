// MetalWall.cs — 金属墙板族换装工具（Editor-only，只换材质贴图）
//
// 目标
//   · 场景里黑族墙（Material.005* / Material_005_*）→ 可攀爬金属板；
//     白墙（Material.014* / Material_014_*）→ 同一套板语言的白色版本。
//   · 贴图由 scripts/gen_metalwall.py 按「一块板 = 2.4m 宽 × 墙面高」生成。
//   · 只换贴图，不动网格、不写 prefab、不 new Material（不克隆材质）。
//     实现用 MaterialPropertyBlock 把 BC/N/MS/AO/E/R + _BaseMap_ST 写到每个
//     MeshRenderer 上；NPR 母材质本身只做 keyword 开关（manifest 记原状态）。
//
// 尺寸档（与生成器约定）
//   · 墙宽 >= 1.8m：N = round(墙宽 / 2.4)，一块板宽 = 墙宽 / N，
//     SizeKey = 2p4 x 墙高（0.1m 四舍五入，小数点写 p）。
//     _BaseMap_ST.x = N → 一面墙正好 N 块板；宽度误差 < 2% 时是拉伸，
//     误差 > 25% 时退回实际板宽做 SizeKey。
//   · 墙宽 < 1.8m：单块板，SizeKey = 墙宽 x 墙高。
//   · 高度带（0.4–3.5m 抛光 + 上下 300mm 渐变）在贴图里按「板底 = 平台面」
//     烘焙；如果某面墙底部不在平台上，需要生成器补一张带 --floor-offset 的图。
//   · 同 SizeKey 的墙复用同一组贴图；缺图时 DryRun/Run 会打印需要补图的
//     生成器命令。
//
// 目录约定
//   生成器默认输出：F:/VSCode/工业级渲染/exports/metalwall/<climb|white>/<SizeKey>/<setName>/
//   导入到工程：    Assets/Art/Images/MetalWall/<setName>/
//   manifest：      Assets/Art/Images/MetalWall/_MetalWall_manifest.json
//
// 用法
//   菜单 MapSkin/MetalWall/① 干跑（扫描+匹配，不写盘）
//        MapSkin/MetalWall/② 应用金属墙板
//        MapSkin/MetalWall/③ 清除还原
//   批处理：
//     -executeMethod MapSkin.MetalWall.DryRun
//     -executeMethod MapSkin.MetalWall.Run
//     -executeMethod MapSkin.MetalWall.Clear
//
// 注意
//   · Run 会 SaveScene（MaterialPropertyBlock 是场景内序列化数据）。
//   · Clear 会清掉目标墙上的 MaterialPropertyBlock（包括别处可能写入的块），
//     并把 manifest 里记录的原材质引用、keyword 原状态写回。
//   · 贴图导入设置：BC/E sRGB；N NormalMap；MS 线性（R=Metallic/A=Smoothness）；
//     AO/R 线性；全部 Repeat + mipmap + max 4096。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MapSkin
{
    public static class MetalWall
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string ExportRoot = "F:/VSCode/工业级渲染/exports/metalwall";
        const string ImportRoot = "Assets/Art/Images/MetalWall";
        const string ManifestAssetPath = ImportRoot + "/_MetalWall_manifest.json";

        const float ModuleM = 2.4f;
        const float MinSingleModuleM = 1.8f;
        const float MaxModuleStretch = 0.25f;
        const float MinWallHeightM = 1.2f;
        const float LongWallWarnM = 30f;

        static readonly string[] Channels = { "BC", "N", "MS", "AO", "E", "R" };
        static readonly Dictionary<string, Texture2D> TexCache = new Dictionary<string, Texture2D>();

        // ------------------------------------------------------------------ 菜单 / 批处理入口
        [MenuItem("MapSkin/MetalWall/① 干跑（扫描+匹配，不写盘）")]
        public static void DryRunMenu() => DryRun();

        [MenuItem("MapSkin/MetalWall/② 应用金属墙板")]
        public static void RunMenu() => Run();

        [MenuItem("MapSkin/MetalWall/③ 清除还原")]
        public static void ClearMenu() => Clear();

        public static void DryRun() => RunCore(true);
        public static void Run() => RunCore(false);

        // ------------------------------------------------------------------ 数据模型

        [Serializable]
        public class Entry
        {
            public string globalObjectId;
            public string hierarchy;
            public string objectName;
            public string variant;
            public string sizeKey;
            public int modules;
            public float moduleWidth;
            public float wallWidth;
            public float wallHeight;
            public string setName;
            public string exportDir;
            public List<string> originalMaterials = new List<string>();
            public List<string> materialNames = new List<string>();
            public List<bool> emissionKeyword = new List<bool>();
            public List<bool> normalKeyword = new List<bool>();
            public List<bool> metallicSpecKeyword = new List<bool>();
            public List<bool> occlusionKeyword = new List<bool>();
            public List<bool> roughnessKeyword = new List<bool>();
        }

        [Serializable]
        public class Manifest
        {
            public string scene;
            public string generatedAt;
            public string clearedAt;
            public string exportRoot;
            public List<Entry> entries = new List<Entry>();
        }

        class Candidate
        {
            public Renderer renderer;
            public string variant;
            public float width;
            public float height;
        }

        class Tier
        {
            public string variant;
            public string key;
            public float keyWidth;
            public float wallHeight;
            public float moduleWidth;
            public int modules;
            public int count;
            public string exampleName;
        }

        // ------------------------------------------------------------------ 主流程

        static void RunCore(bool dry)
        {
            EnsureScene();
            TexCache.Clear();
            var scene = EditorSceneManager.GetActiveScene();
            var oldManifest = LoadManifest();

            var candidates = ScanWalls(out var scanRep);
            var rep = new StringBuilder();
            rep.AppendLine($"[MetalWall] {(dry ? "干跑（零写盘）" : "应用")} — 扫描 {candidates.Count} 面目标墙");
            rep.AppendLine(scanRep.ToString().TrimEnd());

            var tiers = new Dictionary<string, Tier>(StringComparer.Ordinal);
            var manifest = new Manifest
            {
                scene = ScenePath,
                generatedAt = DateTime.Now.ToString("o"),
                clearedAt = null,
                exportRoot = ExportRoot,
                entries = new List<Entry>(),
            };
            int applied = 0, skipped = 0;

            foreach (var c in candidates)
            {
                SizeKeyOf(c.width, c.height, out int modules, out float moduleW, out float keyW);
                string key = "2p4x2p4";   // 方形模数面板：宽高都按 2.4m 平铺，避免高柱被纵向拉伸
                string tierId = c.variant + "|" + key;
                if (!tiers.TryGetValue(tierId, out var tier))
                {
                    tier = new Tier
                    {
                        variant = c.variant, key = key, keyWidth = keyW,
                        wallHeight = c.height, moduleWidth = moduleW,
                        modules = modules, count = 0, exampleName = Hierarchy(c.renderer.transform),
                    };
                    tiers[tierId] = tier;
                }
                tier.count++;

                bool hasSet = FindSet(c.variant, key, out string setName, out string exportDir);
                rep.AppendLine($"  {(hasSet ? "√" : "×")} {c.variant,-5} {c.width,7:F2}×{c.height,6:F2}m  " +
                               $"-> {key}  ({modules}×{moduleW:F3}m)  {Hierarchy(c.renderer.transform)}");
                if (!hasSet)
                {
                    rep.AppendLine($"      缺贴图：{c.variant}/{key}（{setName ?? "无目录"}）");
                    skipped++;
                    continue;
                }
                if (dry) { applied++; continue; }

                var entry = ApplyTo(c, modules, key, setName, exportDir, oldManifest, rep);
                if (entry != null) { manifest.entries.Add(entry); applied++; }
                else skipped++;
            }

            // 尺寸档汇总
            rep.AppendLine();
            rep.AppendLine($"[尺寸档] 共 {tiers.Count} 档（同档复用同一组贴图）");
            foreach (var t in tiers.Values.OrderBy(x => x.variant).ThenBy(x => x.key))
            {
                bool has = FindSet(t.variant, t.key, out _, out _);
                rep.AppendLine($"  {t.variant,-5} {t.key,-12} {t.count,3} 面墙  " +
                               $"{(has ? "贴图就绪" : "需要补图")}  " +
                               $"例：{t.exampleName}");
                if (!has)
                {
                    rep.AppendLine($"      生成器：python scripts/gen_metalwall.py " +
                                   $"--w {t.keyWidth.ToString("0.0", CultureInfo.InvariantCulture)} " +
                                   $"--h {t.wallHeight.ToString("0.0", CultureInfo.InvariantCulture)} " +
                                   $"--variant {t.variant} --out {ExportRoot.Replace('\\', '/')}");
                }
                if (t.wallHeight > LongWallWarnM)
                    rep.AppendLine($"      [注意] 墙高 {t.wallHeight:F1}m 超过 30m，4096 上限下 texel " +
                                   $"约 {t.wallHeight / 4096f * 1000f:F1}mm/px；建议单独分段或补高密度图。");
            }

            if (dry)
            {
                Debug.Log(rep.ToString());
                return;
            }

            // 保留上一轮已应用、这一轮因缺图/跳过而未重写的记录（否则 Clear 会漏还原）
            if (oldManifest?.entries != null)
            {
                foreach (var oe in oldManifest.entries)
                {
                    if (!manifest.entries.Any(ne =>
                            string.Equals(ne.globalObjectId, oe.globalObjectId, StringComparison.Ordinal) ||
                            string.Equals(ne.hierarchy, oe.hierarchy, StringComparison.Ordinal)))
                        manifest.entries.Add(oe);
                }
            }

            WriteManifest(manifest);
            foreach (var p in manifest.entries
                         .SelectMany(e => e.originalMaterials)
                         .Where(p => !string.IsNullOrEmpty(p))
                         .Distinct())
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(p);
                if (mat != null) EditorUtility.SetDirty(mat);
            }
            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(scene);
            Debug.Log(rep + $"\n[MetalWall] 已应用 {applied} 面，跳过 {skipped} 面；" +
                           $"manifest -> {ManifestAssetPath}");
        }

        // ------------------------------------------------------------------ 扫描

        static List<Candidate> ScanWalls(out StringBuilder rep)
        {
            rep = new StringBuilder();
            var list = new List<Candidate>();
            int skippedFloor = 0, skippedMixed = 0, skippedOther = 0;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (r == null) continue;
                var b = r.bounds;
                float w = Mathf.Max(b.size.x, b.size.z);
                float h = b.size.y;
                float t = Mathf.Min(b.size.x, b.size.z);
                if (h < MinWallHeightM || w < 0.25f || h < t)
                {
                    skippedFloor++;
                    continue;
                }

                string variant = Classify(r, out bool mixed);
                if (mixed) { skippedMixed++; continue; }
                if (variant == null) { skippedOther++; continue; }

                list.Add(new Candidate { renderer = r, variant = variant, width = w, height = h });
            }
            list.Sort((a, b) => string.CompareOrdinal(Hierarchy(a.renderer.transform),
                                                     Hierarchy(b.renderer.transform)));
            rep.AppendLine($"  [扫描] 命中 {list.Count} 面；按几何排除 {skippedFloor} 个非墙件，" +
                           $"黑白冲突 {skippedMixed} 个，非目标材质 {skippedOther} 个");
            return list;
        }

        /// <summary>按材质名前缀归类。同时出现黑族与白族 -> mixed=true，跳过。</summary>
        static string Classify(Renderer r, out bool mixed)
        {
            mixed = false;
            bool climb = false, white = false;
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) continue;
                string n = m.name;
                bool c = n.StartsWith("Material.005", StringComparison.Ordinal) ||
                         n.StartsWith("Material_005", StringComparison.Ordinal);
                bool w = n.StartsWith("Material.014", StringComparison.Ordinal) ||
                         n.StartsWith("Material_014", StringComparison.Ordinal);
                if (c) climb = true;
                if (w) white = true;
            }
            if (climb && white) { mixed = true; return null; }
            if (climb) return "climb";
            if (white) return "white";
            return null;
        }

        // ------------------------------------------------------------------ 尺寸档 / 贴图集

        static void SizeKeyOf(float wallWidth, float wallHeight,
                              out int modules, out float moduleWidth, out float keyWidth)
        {
            if (wallWidth < MinSingleModuleM)
            {
                modules = 1;
                moduleWidth = wallWidth;
                keyWidth = wallWidth;
            }
            else
            {
                modules = Mathf.Max(1, Mathf.RoundToInt(wallWidth / ModuleM));
                moduleWidth = wallWidth / modules;
                keyWidth = ModuleM;
                if (Mathf.Abs(moduleWidth - ModuleM) / ModuleM > MaxModuleStretch)
                    keyWidth = moduleWidth;
            }
        }

        static string KeyOf(float w, float h) =>
            $"{Round1(w)}x{Round1(h)}".Replace('.', 'p');

        static string Round1(float v) =>
            (Mathf.Round(v * 10f) / 10f).ToString("0.0", CultureInfo.InvariantCulture);


        static float ParseHeight(string key)
        {
            if (string.IsNullOrEmpty(key)) return 0f;
            int i = key.IndexOf('x');
            if (i < 0 || i + 1 >= key.Length) return 0f;
            var hs = key.Substring(i + 1).Replace('p', '.');
            return float.TryParse(hs, System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
        }

        static bool FindSet(string variant, string key, out string setName, out string exportDir)
        {
            setName = null;
            exportDir = null;
            string root = Path.Combine(ExportRoot, variant, key);
            if (!Directory.Exists(root))
            {
                // 精确尺寸档不存在时，退到同变体里高度最接近的一档（贴图纵向会由 ST 拉伸到墙高）。
                root = null;
                float target = ParseHeight(key);
                float best = float.MaxValue;
                var rootVariant = Path.Combine(ExportRoot, variant);
                if (Directory.Exists(rootVariant))
                {
                    foreach (var d in Directory.GetDirectories(rootVariant))
                    {
                        float h = ParseHeight(Path.GetFileName(d));
                        float diff = Mathf.Abs(h - target);
                        if (diff < best) { best = diff; root = d; }
                    }
                }
                if (root == null) return false;
            }
            var dirs = Directory.GetDirectories(root, "T_CUSTOM_MetalWall_*")
                                .OrderBy(p => p, StringComparer.Ordinal)
                                .ToArray();
            if (dirs.Length == 0) return false;
            uint hash = Fnv1a(variant + "|" + key);
            exportDir = dirs[(int)(hash % (uint)dirs.Length)];
            setName = Path.GetFileName(exportDir);
            return true;
        }

        static uint Fnv1a(string s)
        {
            uint h = 2166136261u;
            foreach (char c in s) { h ^= c; h *= 16777619u; }
            return h;
        }

        // ------------------------------------------------------------------ 应用

        static Entry ApplyTo(Candidate c, int modules, string key, string setName,
                             string exportDir, Manifest oldManifest, StringBuilder rep)
        {
            var r = c.renderer;
            var tex = new Dictionary<string, Texture2D>();
            foreach (var ch in Channels)
            {
                var t = EnsureTexture(setName, exportDir, ch, rep);
                if (t != null) tex[ch] = t;
            }
            if (!tex.ContainsKey("BC") || !tex.ContainsKey("N") || !tex.ContainsKey("MS"))
            {
                rep.AppendLine($"    [跳过] {Hierarchy(r.transform)}：BC/N/MS 缺一张以上");
                return null;
            }

            // 已有未清除的 manifest -> 沿用第一次记录的原材质 / 原 keyword 状态
            string gid = GlobalId(r);
            string hier = Hierarchy(r.transform);
            var old = oldManifest?.entries?.FirstOrDefault(e =>
                string.Equals(e.globalObjectId, gid, StringComparison.Ordinal) ||
                string.Equals(e.hierarchy, hier, StringComparison.Ordinal));
            var entry = new Entry
            {
                globalObjectId = gid,
                hierarchy = hier,
                objectName = r.gameObject.name,
                variant = c.variant,
                sizeKey = key,
                modules = modules,
                moduleWidth = c.width / modules,
                wallWidth = c.width,
                wallHeight = c.height,
                setName = setName,
                exportDir = exportDir,
            };

            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                string path = AssetDatabase.GetAssetPath(m);
                entry.originalMaterials.Add(path);
                entry.materialNames.Add(m.name);
                bool e0 = m.IsKeywordEnabled("_EMISSION");
                bool n0 = m.IsKeywordEnabled("_NORMALMAP");
                bool ms0 = m.IsKeywordEnabled("_METALLICSPECGLOSSMAP");
                bool ao0 = m.IsKeywordEnabled("_OCCLUSIONMAP");
                bool r0 = m.IsKeywordEnabled("_ROUGHNESSMAP");
                if (old != null && i < (old.emissionKeyword?.Count ?? 0))
                {
                    e0 = old.emissionKeyword[i]; n0 = old.normalKeyword[i];
                    ms0 = old.metallicSpecKeyword[i]; ao0 = old.occlusionKeyword[i];
                    r0 = old.roughnessKeyword[i];
                }
                entry.emissionKeyword.Add(e0);
                entry.normalKeyword.Add(n0);
                entry.metallicSpecKeyword.Add(ms0);
                entry.occlusionKeyword.Add(ao0);
                entry.roughnessKeyword.Add(r0);

                // keyword 是材质级开关；MPB 只改数值/贴图。原状态已记入 manifest。
                m.EnableKeyword("_NORMALMAP");
                m.EnableKeyword("_METALLICSPECGLOSSMAP");
                m.EnableKeyword("_OCCLUSIONMAP");
                m.EnableKeyword("_EMISSION");
                // 不改 _ROUGHNESSMAP：只有原本开着的材质才用 R 图覆盖 MS 的 smoothness
                EditorUtility.SetDirty(m);
                if (m.shader == null || !m.shader.name.Contains("NPR"))
                    rep.AppendLine($"    [注意] {m.name} 的 shader 不是 NPR（{m.shader?.name}）；" +
                                   "先跑 MapSkin/② 语义材质换装，或用 URP Lit 同名属性验证");
            }

            // MaterialPropertyBlock 不随场景序列化（存盘即丢）→ 每面墙 CopyAsset 一份真材质。
            const string matRoot = "Assets/Art/Materials/MapSkin/MetalWall";
            if (!AssetDatabase.IsValidFolder("Assets/Art/Materials/MapSkin"))
                AssetDatabase.CreateFolder("Assets/Art/Materials", "MapSkin");
            if (!AssetDatabase.IsValidFolder(matRoot))
                AssetDatabase.CreateFolder("Assets/Art/Materials/MapSkin", "MetalWall");
            string matKey = $"{c.variant}_{setName}_{modules}".Replace('/', '_');
            string matPath = $"{matRoot}/{matKey}.mat";
            var srcMat = r.sharedMaterials != null && r.sharedMaterials.Length > 0 ? r.sharedMaterials[0] : null;
            if (AssetDatabase.LoadAssetAtPath<Material>(matPath) == null && srcMat != null)
            {
                var srcPath = AssetDatabase.GetAssetPath(srcMat);
                if (!string.IsNullOrEmpty(srcPath)) AssetDatabase.CopyAsset(srcPath, matPath);
            }
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                rep.AppendLine($"    [跳过] {Hierarchy(r.transform)}：材质资产创建失败");
                return null;
            }
            mat.SetTexture("_BaseMap", tex["BC"]);
            mat.SetTexture("_BumpMap", tex["N"]);
            mat.SetTexture("_MetallicGlossMap", tex["MS"]);
            if (tex.TryGetValue("AO", out var ao)) mat.SetTexture("_OcclusionMap", ao);
            if (tex.TryGetValue("E", out var em)) mat.SetTexture("_EmissionMap", em);
            if (tex.TryGetValue("R", out var rough)) mat.SetTexture("_RoughnessMap", rough);
            mat.SetVector("_BaseMap_ST", new Vector4(Mathf.Max(1f, c.width / 2.4f), Mathf.Max(1f, c.height / 2.4f), 0f, 0f));
            mat.SetColor("_BaseColor", Color.white);
            mat.SetColor("_Color", Color.white);
            mat.SetFloat("_BumpScale", 1f);
            mat.SetFloat("_Metallic", 1f);
            mat.SetFloat("_Smoothness", 1f);
            mat.SetFloat("_SmoothnessTextureChannel", 0f);
            if (tex.ContainsKey("AO")) mat.SetFloat("_OcclusionStrength", 1f);
            mat.SetFloat("_EmissionEnabled", 1f);
            mat.SetFloat("_EmissionUseMap", 1f);
            mat.SetFloat("_EmissionIntensity", 1f);
            mat.SetColor("_EmissionColor", Color.white);
            EditorUtility.SetDirty(mat);
            r.sharedMaterials = new[] { mat };
            AssetDatabase.SaveAssets();

            rep.AppendLine($"      应用 {setName}  ×{modules} 板" +
                           $"（原材质 {string.Join(", ", entry.materialNames)}）");
            return entry;
        }

        // ------------------------------------------------------------------ 贴图导入

        static Texture2D EnsureTexture(string setName, string exportDir, string ch, StringBuilder rep)
        {
            string relDir = ImportRoot + "/" + setName;
            EnsureFolder(relDir);
            string file = setName + "_" + ch + ".png";
            string rel = relDir + "/" + file;
            string full = Path.Combine(Directory.GetCurrentDirectory(), rel);
            string src = Path.Combine(exportDir, file);
            bool needCopy = !File.Exists(full);
            if (!needCopy && File.Exists(src) &&
                File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(full))
                needCopy = true;                    // 生成器重跑过 -> 覆盖旧贴图
            if (needCopy)
            {
                if (!File.Exists(src))
                {
                    rep.AppendLine($"      [缺通道] {ch}: {src}");
                    return null;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
                File.Copy(src, full, true);
                AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceUpdate);
            }
            ConfigureImporter(rel, ch);
            if (TexCache.TryGetValue(rel, out var cached) && cached != null) return cached;
            var t = AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
            if (t != null) TexCache[rel] = t;
            return t;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parts = path.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }

        static void ConfigureImporter(string rel, string ch)
        {
            var ti = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (ti == null) return;
            bool dirty = false;
            if (ti.wrapMode != TextureWrapMode.Repeat) { ti.wrapMode = TextureWrapMode.Repeat; dirty = true; }
            if (ti.maxTextureSize < 4096) { ti.maxTextureSize = 4096; dirty = true; }
            if (!ti.mipmapEnabled) { ti.mipmapEnabled = true; dirty = true; }
            if (ti.anisoLevel < 8) { ti.anisoLevel = 8; dirty = true; }
            if (ch == "N")
            {
                if (ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; dirty = true; }
                if (ti.sRGBTexture) { ti.sRGBTexture = false; dirty = true; }
            }
            else
            {
                if (ti.textureType != TextureImporterType.Default) { ti.textureType = TextureImporterType.Default; dirty = true; }
                bool wantSRGB = ch == "BC" || ch == "E";
                if (ti.sRGBTexture != wantSRGB) { ti.sRGBTexture = wantSRGB; dirty = true; }
                if (ti.alphaIsTransparency) { ti.alphaIsTransparency = false; dirty = true; }
                if (ch == "MS" && ti.alphaSource != TextureImporterAlphaSource.FromInput)
                { ti.alphaSource = TextureImporterAlphaSource.FromInput; dirty = true; }
            }
            if (dirty) ti.SaveAndReimport();
        }

        // ------------------------------------------------------------------ 清除

        public static void Clear()
        {
            EnsureScene();
            TexCache.Clear();
            var m = LoadManifest();
            if (m == null || m.entries == null || m.entries.Count == 0)
            {
                Debug.Log("[MetalWall] 没有 manifest，无可清除的换装记录");
                return;
            }
            var rep = new StringBuilder("[MetalWall] 清除还原\n");
            int restored = 0, missing = 0;
            foreach (var e in m.entries)
            {
                var r = FindRenderer(e);
                if (r == null)
                {
                    rep.AppendLine($"  [缺] {e.hierarchy}（找不到 Renderer）");
                    missing++;
                    continue;
                }
                r.SetPropertyBlock(null);

                // 原材质引用写回（正常路径下 Run 没改 sharedMaterials；这里做保险）
                var mats = new Material[e.originalMaterials.Count];
                bool any = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (string.IsNullOrEmpty(e.originalMaterials[i])) continue;
                    mats[i] = AssetDatabase.LoadAssetAtPath<Material>(e.originalMaterials[i]);
                    if (mats[i] != null) any = true;
                }
                if (any) r.sharedMaterials = mats;

                // keyword 原状态
                for (int i = 0; i < e.originalMaterials.Count; i++)
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(e.originalMaterials[i]);
                    if (mat == null) continue;
                    SetKeyword(mat, "_EMISSION", At(e.emissionKeyword, i));
                    SetKeyword(mat, "_NORMALMAP", At(e.normalKeyword, i));
                    SetKeyword(mat, "_METALLICSPECGLOSSMAP", At(e.metallicSpecKeyword, i));
                    SetKeyword(mat, "_OCCLUSIONMAP", At(e.occlusionKeyword, i));
                    SetKeyword(mat, "_ROUGHNESSMAP", At(e.roughnessKeyword, i));
                    EditorUtility.SetDirty(mat);
                }
                EditorUtility.SetDirty(r);
                rep.AppendLine($"  还原 {e.hierarchy}  ({e.variant}/{e.sizeKey}/{e.setName})");
                restored++;
            }
            m.clearedAt = DateTime.Now.ToString("o");
            WriteManifest(m);
            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            rep.AppendLine($"[MetalWall] 还原 {restored} 面，找不到 {missing} 面；manifest 已标记 clearedAt");
            Debug.Log(rep.ToString());
        }

        static bool At(List<bool> a, int i) => a != null && i >= 0 && i < a.Count && a[i];

        static void SetKeyword(Material m, string kw, bool on)
        {
            if (m == null) return;
            if (on) m.EnableKeyword(kw); else m.DisableKeyword(kw);
        }

        static Renderer FindRenderer(Entry e)
        {
            if (!string.IsNullOrEmpty(e.globalObjectId) &&
                GlobalObjectId.TryParse(e.globalObjectId, out var gid))
            {
                var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                if (obj is Renderer r) return r;
            }
            var t = FindByPath(e.hierarchy);
            return t != null ? t.GetComponent<Renderer>() : null;
        }

        static Transform FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.Split('/');
            Transform cur = null;
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == parts[0]) { cur = root.transform; break; }
            }
            for (int i = 1; cur != null && i < parts.Length; i++)
                cur = cur.Find(parts[i]);
            return cur;
        }

        // ------------------------------------------------------------------ manifest / 场景

        static string GlobalId(Renderer r)
        {
            try { return GlobalObjectId.GetGlobalObjectIdSlow(r).ToString(); }
            catch { return ""; }
        }

        static string Hierarchy(Transform t)
        {
            var sb = new StringBuilder(t.name);
            var p = t.parent;
            while (p != null) { sb.Insert(0, p.name + "/"); p = p.parent; }
            return sb.ToString();
        }

        static Manifest LoadManifest()
        {
            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), ManifestAssetPath)))
                return null;
            try
            {
                var txt = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), ManifestAssetPath));
                return JsonUtility.FromJson<Manifest>(txt);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MetalWall] manifest 读取失败：{ex.Message}");
                return null;
            }
        }

        static void WriteManifest(Manifest m)
        {
            EnsureFolder(ImportRoot);
            string full = Path.Combine(Directory.GetCurrentDirectory(), ManifestAssetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
            File.WriteAllText(full, JsonUtility.ToJson(m, true), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(ManifestAssetPath, ImportAssetOptions.ForceUpdate);
        }

        static void EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return;
            if (cur.IsValid() && cur.isDirty && !string.IsNullOrEmpty(cur.path))
                EditorSceneManager.SaveScene(cur);
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log($"[MetalWall] 已打开场景 {ScenePath}");
        }
    }
}
