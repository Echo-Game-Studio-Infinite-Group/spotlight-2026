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
    /// 刻线族分派（用户 2026-10-18 派单 A）：把 25 套程序化刻线贴图接进语义材质，并**按对象**分派，
    /// 让整图有花纹变化（不是所有墙一个样）。
    ///
    /// 贴图源：`F:/VSCode/工业级渲染/exports/textures/T_HS_Line_{A_Ortho,B_Step,C_Island,D_Jog,E_Bulk}_{White,White_Floor,Black,Black_Floor,Cyan}`
    /// （每套 BC/N/MS + params.json；墙板版 3.6×4.8m@1536×2048，地坪版 4.8×4.8m@2048×2048）
    ///
    /// 落地方式：
    ///   ① 25 套贴图全部拷进 `Assets/Art/Images/HST_Prags/` 并按通道配导入设置（BC=sRGB、N=法线、MS=线性）。
    ///   ② 建 **Material Variant**（母材质 = 语义族基底材质 .014/.005/.015）——不覆盖基底、不改 shader/GUID，
    ///      toon 三件套与 NPR 参数全部继承母材质（②步已写死），本步只覆盖贴图三槽 + tiling + 底色。
    ///      5 种用途 × 5 族 = 25 个变体：WhiteWall / WhiteFloor / BlackWall / BlackFloor / Cyan。
    ///      tiling = UV 基准尺度 / 该套 tile_m（UV 已烘成"世界坐标 / 基准尺度"）。
    ///   ③ 逐对象分派：稳定 hash（对象名 + 世界坐标取整）在 A/B/C/D/E 五族里选一套；**同一对象每次跑结果一致**。
    ///      墙/地判定 = 该对象网格的世界三角形面积按主法线分类，朝上面（±Y）占比 ≥ 阈值判地坪 →
    ///      用 `*_White_Floor` / `*_Black_Floor`。这是**按对象**判的，不是按材质槽 —— 同一 .mat 被墙和地
    ///      共用这件事在这里不构成障碍（槽位是逐对象的），代价是同一个件的侧面也会吃到地坪贴图（只差贴图花样，不影响尺度）。
    ///   ④ 橙/黄/洋红三族（.038/.061/.063）工程外无贴图 → 按规范 §2.3 就地改成 **URP Unlit 发光强调件**，
    ///      基色 = 语义色且 ≤1.0（不产生 Bloom 光晕），底纹用**中性白**刻线 BC（青 BC 的红通道≈0，
    ///      乘在橙/黄/洋红上会把色相打没，故不用青）。
    ///
    /// 幂等：变体按路径复用、分派结果由 hash 决定、发光件属性写死。
    /// 菜单：MapSkin/⑦ 刻线族分派(按对象 hash + 墙/地判定)
    /// 批处理：-executeMethod MapSkin.MapSkinLineDispatch.Run
    /// </summary>
    public static class MapSkinLineDispatch
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string LineMatDir = "Assets/Art/Materials/MapSkin/Line";
        const string AccentMatDir = "Assets/Art/Materials/MapSkin/Accent";
        const string ImgRoot = "Assets/Art/Images/HST_Prags";
        const string ExportRoot = "F:/VSCode/工业级渲染/exports/textures";
        const string UrpUnlit = "Universal Render Pipeline/Unlit";
        const string NprShaderName = "OurFunction/NPR/Deferred Toon";

        /// <summary>朝上面积占比 ≥ 此值 → 判为地坪件（用 *_Floor 贴图）。</summary>
        const float FloorRatio = 0.55f;

        static readonly string[] Letters = { "A_Ortho", "B_Step", "C_Island", "D_Jog", "E_Bulk" };

        // 语义族 → （基底材质名, 该族语义色/底色）
        static readonly string[] WhiteMats = { "Material.014", "Material.060", "Material.051" };
        static readonly string[] BlackMats = { "Material.005", "Material.006", "Material.013", "Material.016", "Material.017", "Material.041" };
        static readonly string[] CyanMats = { "Material.015", "Material.026", "Material.039" };
        const string WhiteRootMat = "Material.014";     // 白族变体挂它（.014/.060/.051 参数逐位同款）
        const string BlackRootMat = "Material.005";
        const string CyanRootMat = "Material.015";

        /// <summary>发光强调件 = 语义色 ≤1.0（规范 §2.3）+ 底纹贴图。</summary>
        static readonly (string mat, string key, Color col, string semantics)[] Accents =
        {
            ("Material.038", "Accent_Enter", new Color(0.87f, 0.27f, 0.04f), "关卡进入点"),
            ("Material.061", "Accent_Exit",  new Color(0.80f, 0.79f, 0.08f), "关卡离开点"),
            ("Material.063", "Accent_Slide", new Color(0.89f, 0.23f, 0.46f), "向下滑铲标志"),
        };
        /// <summary>发光件底纹（中性白刻线 BC；乘色相不变）。</summary>
        const string AccentPatternTex = "T_HS_Line_A_Ortho_White";

        class Variant
        {
            public string path, texName;
            public float uvBase;
            public Material mat;
        }

        static readonly Dictionary<string, Variant> _variants = new Dictionary<string, Variant>();
        static readonly Dictionary<int, int> _planeClassCache = new Dictionary<int, int>();  // mesh hashcode → 0 墙 / 1 地

        // ---------------------------------------------------------------- 入口

        [MenuItem("MapSkin/⑦ 刻线族分派(按对象 hash + 墙/地判定)")]
        public static void RunMenu() => Run();

        public static void Run()
        {
            EnsureScene();
            var rep = new StringBuilder();

            // ---------- ① 贴图入库 ----------
            var texOK = new Dictionary<string, int>();
            int copied = 0, missing = 0;
            foreach (var tex in AllTexNames())
            {
                var map = MapSkinApply.EnsureTextureSet(tex, rep, ref copied);
                if (map == null) { missing++; continue; }
                texOK[tex] = map.Count;
            }
            rep.AppendLine($"[贴图] 25 套刻线集：就位 {texOK.Count} 套、缺 {missing} 套、新拷入文件 {copied} 个 → {ImgRoot}");

            // ---------- ② 建 25 个 Material Variant ----------
            EnsureFolder("Assets/Art/Materials", "MapSkin");
            EnsureFolder("Assets/Art/Materials/MapSkin", "Line");
            int made = 0, reused = 0;
            foreach (var kv in BuildVariantPlan())
            {
                var v = EnsureVariant(kv.Key, kv.Value.baseMat, kv.Value.texName, kv.Value.uvBase, texOK, ref made, ref reused, rep);
                if (v != null) _variants[kv.Key] = v;
            }
            AssetDatabase.SaveAssets();
            rep.AppendLine($"[变体] Material Variant：新建 {made} 个、复用 {reused} 个 → {LineMatDir}");
            rep.AppendLine("        母材质 = 族基底 .014/.005/.015；toon 三件套与 NPR 参数**继承母材质**，变体只覆盖贴图槽+tiling+底色");

            // ---------- ③ 逐对象分派 ----------
            var counts = new SortedDictionary<string, int>();
            int assigned = 0, slots = 0, planeFloor = 0, planeWall = 0;
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled) continue;
                var mats = r.sharedMaterials;
                bool dirty = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var fam = FamilyOf(mats[i]);
                    if (fam == null) continue;                       // 非语义族（换皮/UI 等）不动
                    if (fam == "Accent") continue;                   // 发光件在 ④ 就地改资产，不分派
                    bool isFloor = fam == "Cyan" || ClassifyPlane(r) == 1;
                    if (isFloor) planeFloor++; else planeWall++;
                    int idx = StableIndex(r, i);
                    string key = $"{fam}|{(isFloor ? "Floor" : "Wall")}|{Letters[idx]}";
                    if (fam == "Cyan") key = $"Cyan|Wall|{Letters[idx]}";   // 青族无地坪版贴图（见日志）
                    if (!_variants.TryGetValue(key, out var v) || v.mat == null) continue;
                    mats[i] = v.mat;
                    dirty = true;
                    assigned++;
                    var ck = $"{fam} {(isFloor ? "地坪" : "墙板")} {Letters[idx]}";
                    counts[ck] = counts.TryGetValue(ck, out var c) ? c + 1 : 1;
                }
                if (dirty) { r.sharedMaterials = mats; EditorUtility.SetDirty(r); slots++; }
            }
            AssetDatabase.SaveAssets();
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid()) { EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); }
            rep.AppendLine($"[分派] 命中渲染器 {slots} 个、材质槽 {assigned} 个（墙板判定 {planeWall}、地坪判定 {planeFloor}）");
            rep.AppendLine($"        墙/地判定口径：网格世界三角形面积按主法线分类，朝上面占比 ≥ {FloorRatio:P0} 判地坪" +
                           $"（按**对象**判；同一件侧面也会吃到地坪贴图，只差花样不影响尺度）");
            foreach (var kv in counts) rep.AppendLine($"        {kv.Key}: {kv.Value} 槽");
            rep.AppendLine($"        场景已存盘：{ScenePath}");

            // ---------- ④ 发光强调件（就地改 .038/.061/.063 及其变体）----------
            var unlit = Shader.Find(UrpUnlit);
            if (unlit == null) { rep.AppendLine("[发光] 找不到 URP Unlit，跳过"); }
            else
            {
                var pattern = texOK.ContainsKey(AccentPatternTex)
                    ? AssetDatabase.LoadAssetAtPath<Texture2D>($"{ImgRoot}/{AccentPatternTex}/{AccentPatternTex}_BC.png") : null;
                foreach (var a in Accents)
                {
                    foreach (var p in AccentTargets(a.mat))
                    {
                        var m = AssetDatabase.LoadAssetAtPath<Material>(p);
                        if (m == null) continue;
                        bool isVariant = m.parent != null;
                        if (!isVariant) m.shader = unlit;               // variant 的 shader 继承母链，改不了
                        m.SetColor("_BaseColor", new Color(Mathf.Min(a.col.r, 1f), Mathf.Min(a.col.g, 1f), Mathf.Min(a.col.b, 1f), 1f));
                        if (pattern != null) m.SetTexture("_BaseMap", pattern);
                        m.SetTextureScale("_BaseMap", Vector2.one);
                        m.SetTextureOffset("_BaseMap", Vector2.zero);
                        EditorUtility.SetDirty(m);
                        rep.AppendLine($"[发光] {p}: → {(isVariant ? "（variant）" : "")}URP Unlit，" +
                                       $"基色 ({a.col.r:F2},{a.col.g:F2},{a.col.b:F2}) ≤1.0（无 Bloom 光晕）、" +
                                       $"底纹 {(pattern != null ? AccentPatternTex + "_BC" : "无")} —— {a.key}/{a.semantics}");
                    }
                }
                rep.AppendLine("[发光] 选 URP Unlit 而非 NPR+自发光：Unlit 不受 GTAO/环境光影响，");
                rep.AppendLine("        语义标记在任意亮度下都稳定可辨（当前画面偏暗的前提下这条最关键），");
                rep.AppendLine("        且规范 §2.3 就是这条口径；同族 shader 在本工程参考预制体里已在延迟管线下正常出画。");
                rep.AppendLine("[发光] 底纹用中性白刻线 BC 而不是青 BC：青 BC 红通道≈0，乘在橙/黄/洋红上会把色相打没。");
            }

            Debug.Log($"[MapSkin·刻线分派] 完成\n{rep}");
        }

        // ---------------------------------------------------------------- 计划表

        static IEnumerable<string> AllTexNames()
        {
            foreach (var l in Letters)
                foreach (var kind in new[] { "White", "White_Floor", "Black", "Black_Floor", "Cyan" })
                    yield return $"T_HS_Line_{l}_{kind}";
        }

        struct Plan { public string baseMat, texName; public float uvBase; }

        static Dictionary<string, Plan> BuildVariantPlan()
        {
            var d = new Dictionary<string, Plan>();
            foreach (var l in Letters)
            {
                // 白：墙板版基准 3.6，地坪版基准 4.8
                d[$"White|Wall|{l}"] = new Plan { baseMat = WhiteRootMat, texName = $"T_HS_Line_{l}_White", uvBase = 3.6f };
                d[$"White|Floor|{l}"] = new Plan { baseMat = WhiteRootMat, texName = $"T_HS_Line_{l}_White_Floor", uvBase = 4.8f };
                d[$"Black|Wall|{l}"] = new Plan { baseMat = BlackRootMat, texName = $"T_HS_Line_{l}_Black", uvBase = 3.6f };
                d[$"Black|Floor|{l}"] = new Plan { baseMat = BlackRootMat, texName = $"T_HS_Line_{l}_Black_Floor", uvBase = 4.8f };
                // 青只有墙板版（3.6×4.8）——用在朝上面的件上，按 4.8 基准换算 tiling
                d[$"Cyan|Wall|{l}"] = new Plan { baseMat = CyanRootMat, texName = $"T_HS_Line_{l}_Cyan", uvBase = 4.8f };
            }
            return d;
        }

        static Variant EnsureVariant(string key, string baseMatName, string texName, float uvBase,
            Dictionary<string, int> texOK, ref int made, ref int reused, StringBuilder rep)
        {
            var parent = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Art/Models/MikuGandam/materials/{baseMatName}.mat");
            if (parent == null) { rep.AppendLine($"[变体] 母材质缺失 {baseMatName}，跳过 {key}"); return null; }
            var name = $"{baseMatName}_Line{key.Replace("|", "_")}";
            var path = $"{LineMatDir}/{name}.mat";
            var v = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (v == null)
            {
                v = new Material(parent);
                v.parent = parent;                     // 关键：成为 Material Variant（母链 = 语义基底材质）
                v.name = name;
                AssetDatabase.CreateAsset(v, path);
                made++;
            }
            else reused++;

            // 覆盖：贴图三槽 + tiling（toon 三件套与其余 NPR 参数一律继承母材质）
            if (texOK.ContainsKey(texName))
            {
                var bc = AssetDatabase.LoadAssetAtPath<Texture2D>($"{ImgRoot}/{texName}/{texName}_BC.png");
                var nn = AssetDatabase.LoadAssetAtPath<Texture2D>($"{ImgRoot}/{texName}/{texName}_N.png");
                var ms = AssetDatabase.LoadAssetAtPath<Texture2D>($"{ImgRoot}/{texName}/{texName}_MS.png");
                var tile = MapSkinApply.ReadTileM(texName, new[] { 3.6f, 4.8f });
                var scale = new Vector2(uvBase / Mathf.Max(tile[0], 1e-4f), uvBase / Mathf.Max(tile[1], 1e-4f));
                if (bc != null) { v.SetTexture("_BaseMap", bc); v.SetTextureScale("_BaseMap", scale); v.SetTextureOffset("_BaseMap", Vector2.zero); }
                if (nn != null) { v.SetTexture("_BumpMap", nn); v.SetTextureScale("_BumpMap", scale); v.SetFloat("_BumpScale", 1f); v.EnableKeyword("_NORMALMAP"); }
                if (ms != null)
                {
                    v.SetTexture("_MetallicGlossMap", ms);
                    v.SetTextureScale("_MetallicGlossMap", scale);
                    v.SetFloat("_Metallic", 0f);
                    v.SetFloat("_SmoothnessTextureChannel", 0f);   // 0 = 取 Metallic Alpha
                    v.EnableKeyword("_METALLICSPECGLOSSMAP");
                }
                // 底色：贴图自带颜色（白 BC≈0.71 灰、黑 BC≈0.044、青 BC=青），所以一律 1.0 让它直通
                v.SetColor("_BaseColor", Color.white);

                // 地坪件给反射（派单 B.4）：MS 图的 Smoothness(A) 实测 ≈0.72 → _Smoothness=1 让它直通，落在 0.6–0.75 区间
                bool isFloor = key.Contains("|Floor|") || key.StartsWith("Cyan|");
                v.SetFloat("_Smoothness", isFloor ? 1f : 0.5f);
                v.SetFloat("_Metallic", isFloor ? 0.05f : 0f);
                v.SetFloat("_SmoothnessTextureChannel", 0f);
            }
            EditorUtility.SetDirty(v);
            return new Variant { path = path, texName = texName, uvBase = uvBase, mat = v };
        }

        // ---------------------------------------------------------------- 族判定

        /// <summary>材质 → 语义族（沿母链找基底材质名；Material Variant 也认）。</summary>
        static string FamilyOf(Material m)
        {
            for (var cur = m; cur != null; cur = cur.parent)
            {
                var n = cur.name;
                if (WhiteMats.Contains(n)) return "White";
                if (BlackMats.Contains(n)) return "Black";
                if (CyanMats.Contains(n)) return "Cyan";
                if (Accents.Any(a => a.mat == n)) return "Accent";
            }
            return null;
        }

        /// <summary>稳定分派序号：FNV1a(对象名 + 世界坐标取整) % 5。同对象每次跑必得同一档。</summary>
        static int StableIndex(Renderer r, int slot)
        {
            var p = r.bounds.center;
            var key = $"{r.gameObject.name}|{slot}|{Mathf.RoundToInt(p.x)}_{Mathf.RoundToInt(p.y)}_{Mathf.RoundToInt(p.z)}";
            unchecked
            {
                uint h = 2166136261;
                foreach (var c in key) { h = (h ^ c) * 16777619; }
                return (int)(h % (uint)Letters.Length);
            }
        }

        /// <summary>0 = 墙板件 / 1 = 地坪件：世界三角形面积按主法线分类，朝上面占比 ≥ FloorRatio 判地坪。</summary>
        static int ClassifyPlane(Renderer r)
        {
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return 0;
            var mesh = mf.sharedMesh;
            int id = mesh.GetInstanceID();
            if (_planeClassCache.TryGetValue(id, out var cached)) return cached;

            var vs = mesh.vertices;
            var tris = mesh.triangles;
            var M = r.transform.localToWorldMatrix;
            float upA = 0f, otherA = 0f;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                var a = M.MultiplyPoint3x4(vs[tris[i]]);
                var b = M.MultiplyPoint3x4(vs[tris[i + 1]]);
                var c = M.MultiplyPoint3x4(vs[tris[i + 2]]);
                var n = Vector3.Cross(b - a, c - a);
                float area = n.magnitude * 0.5f;
                if (area < 1e-9f) continue;
                n /= (area * 2f);
                if (Mathf.Abs(n.y) >= Mathf.Max(Mathf.Abs(n.x), Mathf.Abs(n.z))) upA += area; else otherA += area;
            }
            float total = upA + otherA;
            int res = total > 1e-6f && upA / total >= FloorRatio ? 1 : 0;
            _planeClassCache[id] = res;
            return res;
        }

        // ---------------------------------------------------------------- 杂项

        static List<string> AccentTargets(string baseName)
        {
            var list = new List<string> { $"Assets/Art/Models/MikuGandam/materials/{baseName}.mat" };
            const string vdir = "Assets/Art/Materials/Variants/Opaque";
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { vdir }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var file = Path.GetFileNameWithoutExtension(p);
                if (file == baseName || file.StartsWith(baseName + "_")) list.Add(p);
            }
            return list;
        }

        static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{child}")) AssetDatabase.CreateFolder(parent, child);
        }

        static void EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return;
            if (cur.IsValid() && cur.isDirty)
            {
                Debug.LogWarning("[刻线分派] 当前场景有未保存改动，不替你切换场景；请在 Level1 打开时跑");
                return;
            }
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }
    }
}
