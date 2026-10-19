// 逐面定制皮肤（FaceSkin）
//
// 目标：把 BOX_S_01..BOX_S_07 七个灰盒区块里每个可见平面拆成独立“面”，
//       导出面尺寸/轮廓/朝向给外部 Python 生成唯一贴图，再按 manifest 回贴成
//       “一面一 submesh、一面一材质”。
//
// 三个入口：
//   · Export()：扫描场景 → 写 F:/VSCode/taptap-game-jem/_session_audit/face_spec.json
//   · Apply() ：读 F:/VSCode/taptap-game-jem/_session_audit/face_skin_manifest.json → 回贴
//   · Clear() ：读 face_skin_restore.json → 还原原 mesh / 原材质
//
// 菜单：MapSkin/⑧ 逐面皮肤·导出 spec
//       MapSkin/⑧ 逐面皮肤·应用 manifest
//       MapSkin/⑧ 逐面皮肤·还原
// 批处理：-executeMethod MapSkin.FaceSkin.Export / MapSkin.FaceSkin.Apply / MapSkin.FaceSkin.Clear
//
// 关键口径（与需求一致）：
//   · 只扫场景根对象名以 BOX_S_ 开头的层级；跳过 ART_ / HST_ / DEC_Shared 开头的对象；
//   · 跳过材质名含 DeckKit / Gate / Spawn / Goal 的对象；
//   · 三角形先按“同 submesh + 共享顶点索引”并查集连通，再用代表法线（点积 > 0.999）
//     和平面距离（< 5mm）做共面切分——这样既不会把分离面片误并，也不会把不同平面并掉；
//   · 每个面独立计算 u/v 轴：v 尽量朝世界上方（水平面用世界 +Z），u = v × n，
//     保证 (u,v,n) 右手系；轮廓用凸包，矩形输出 4 点；
//   · 面积 < 0.5 m² 的面不导出；
//   · seed 对 objectPath + worldCenter 做 FNV-1a，保证同一面稳定。
//
// 重要实现说明：
//   · Apply 不是按 manifest 里的三角形清单回贴，而是用与 Export 完全相同的分析算法
//     对“原 mesh”重新分组，再用面 id 里的 #fNN 下标对齐。因此 Export / Apply 之间
//     不要改 Transform 或原 mesh，否则面 id 可能漂移。
//   · 不同 renderer 共用 mesh 时各自 Object.Instantiate 克隆，绝不修改源 mesh。
//   · 新 mesh 顶点数超过 65535 时 warning 并跳过该 renderer（不做任何修改）。
//   · 只处理 MeshFilter + Renderer；SkinnedMeshRenderer 等跳过并报告。
//   · manifest 里没有覆盖到的面（例如面积过小或生成器漏面）保留原材质，不强行拆材质，
//     避免把未定制的小面变成白模。
//
// 本文件只使用 UnityEditor / UnityEngine API，不依赖项目内其他脚本。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MapSkin
{
    public static class FaceSkin
    {
        // ---------------------------------------------------------------- 常量

        const string ScenePath = "Assets/Scenes/Level1.unity";

        // 外部 Python 生成器与主控约定的审计目录（与工程同级）
        static string AuditDir => Path.GetFullPath(Path.Combine(ProjectRoot, "..", "_session_audit")).Replace('\\', '/');
        static string SpecPath => AuditDir + "/face_spec.json";
        static string ManifestPath => AuditDir + "/face_skin_manifest.json";
        static string RestorePath => AuditDir + "/face_skin_restore.json";

        const string MeshDir = "Assets/Art/Environment/FaceSkin/Meshes";
        const string TexDir = "Assets/Art/Environment/FaceSkin/Textures";
        const string MatDir = "Assets/Art/Materials/MapSkin/FaceSkin";
        const string TemplateMat = "Assets/Art/Materials/MapSkin/Dec/S_04/F_A_0.mat";

        const string GeneratedMeshPrefix = "FACESKIN_";

        /// <summary>同面判定：世界法线点积阈值。</summary>
        const float SameNormalDot = 0.999f;
        /// <summary>同面判定：到代表平面的最大距离（米）。</summary>
        const float PlaneDistTol = 0.005f;
        /// <summary>导出最小面积（平方米）。</summary>
        const float MinFaceArea = 0.5f;
        /// <summary>
        /// 大面切板阈值：PRAGMATA 实测是一张 1024 贴图铺 9–15m 墙（≈10mm/px），
        /// 逐面自持、不切板。这里改为不切板：一个共面面 = 一张画布（上限 4096，
        /// 40m 墙约 10mm/px，16m 以下约 4mm/px），既符合参考也把贴图数量压到可交付。
        /// </summary>
        const float PanelThreshold = 100000f;
        /// <summary>切板模数：7.2m = 6×1.2 = 4×1.8 = 3×2.4，落在 6–8m 目标内。</summary>
        const float PanelStep = 7.2f;
        /// <summary>不允许出现小于该宽度的碎条（米）。</summary>
        const float MinPanelStrip = 1.2f;
        /// <summary>小面阈值：面积小于该值的面走 8 变体贴图，不单独出大图。</summary>
        const float SmallFaceArea = 6f;
        /// <summary>可行走面：法线朝上且面积大于该值。</summary>
        const float WalkableMinArea = 4f;
        /// <summary>小面变体数量。</summary>
        const int VariantCount = 8;
        /// <summary>粗略贴图预算（字节），只用于报告预警。</summary>
        const double TextureBudgetBytes = 3d * 1024d * 1024d * 1024d;
        /// <summary>预估分辨率假设：5mm/px（manifest 的默认 mmPx）。</summary>
        const double AssumedMmPerPixel = 5d;
        /// <summary>退化三角形判定（叉积平方，约合面积 5e-9 m²）。</summary>
        const float MinTriCrossSqr = 1e-16f;
        /// <summary>凸包去除共线点的叉积阈值。</summary>
        const float HullCollinearEps = 1e-4f;
        /// <summary>投影点去重距离平方（约 1e-5 m）。</summary>
        const float HullDedupeSqr = 1e-10f;
        /// <summary>Unity 16 位索引的保守顶点上限。</summary>
        const int MaxVertices16 = 65535;

        static readonly string[] SkipNamePrefixes = { "ART_", "HST_", "DEC_Shared" };
        static readonly string[] SkipMaterialKeywords = { "DeckKit", "Gate", "Spawn", "Goal" };
        // 六边形墙皮是浮雕网格，逐 hex 面会把 spec 撑爆；由独立材质方案处理
        static readonly string[] HexMaterialPrefixes = { "M_panel_q", "M_dark_q" };
        const string HexMeshPrefix = "SM_WallSkin_";

        // ---------------------------------------------------------------- 菜单 / 入口

        [MenuItem("MapSkin/⑧ 逐面皮肤·导出 spec")]
        public static void ExportMenu() => Export();

        [MenuItem("MapSkin/⑧ 逐面皮肤·应用 manifest")]
        public static void ApplyMenu()
        {
            if (!File.Exists(ManifestPath))
            {
                Debug.LogError($"[FaceSkin] 找不到 manifest：{ManifestPath}");
                return;
            }
            if (!EditorUtility.DisplayDialog("逐面皮肤·应用",
                    $"将读取：\n{ManifestPath}\n\n" +
                    "对每个受影响 renderer：克隆 mesh、按面拆 submesh、每面一份 URP Lit 材质。\n" +
                    "不会改动 Collider / Transform。继续？",
                    "应用", "取消"))
            {
                Debug.Log("[FaceSkin] 用户取消");
                return;
            }
            Apply();
        }

        [MenuItem("MapSkin/⑧ 逐面皮肤·还原")]
        public static void ClearMenu() => Clear();

        /// <summary>扫描 BOX_S_* 区块并写 face_spec.json。不修改场景。</summary>
        public static void Export()
        {
            if (!EnsureScene()) return;
            EnsureAuditDir();

            var records = new List<FaceRecord>();
            var styleHist = new Dictionary<string, int>(StringComparer.Ordinal);
            var skipHist = new Dictionary<string, int>(StringComparer.Ordinal);

            int blockCount = 0, rendererCount = 0, tinyFaces = 0, unreadable = 0;
            int panelizedFaces = 0, smallCount = 0;

            var scene = EditorSceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                if (!root.name.StartsWith("BOX_S_", StringComparison.Ordinal)) continue;
                blockCount++;

                var blockRecords = new List<FaceRecord>();
                float groundY = float.NaN;
                float bestFloorArea = 0f;

                var renderers = root.GetComponentsInChildren<Renderer>(true);
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    if (!r.enabled || !r.gameObject.activeInHierarchy)
                    {
                        Count(skipHist, "未启用 / 非激活");
                        continue;
                    }
                    if (StartsWithAny(r.gameObject.name, SkipNamePrefixes))
                    {
                        Count(skipHist, "名字前缀跳过");
                        continue;
                    }

                    var shared = r.sharedMaterials;
                    if (HasAnyMaterialName(shared, SkipMaterialKeywords))
                    {
                        Count(skipHist, "已有专用皮肤材质");
                        continue;
                    }
                    // 六边形墙皮：材质前缀或 mesh 前缀命中都跳过
                    if (HasAnyMaterialNamePrefix(shared, HexMaterialPrefixes))
                    {
                        Count(skipHist, "六边形墙皮材质");
                        continue;
                    }

                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null)
                    {
                        Count(skipHist, "非 MeshFilter");
                        continue;
                    }
                    var mesh = mf.sharedMesh;
                    if (mesh == null)
                    {
                        Count(skipHist, "mesh 为空");
                        continue;
                    }
                    if (!string.IsNullOrEmpty(mesh.name) &&
                        mesh.name.StartsWith(HexMeshPrefix, StringComparison.Ordinal))
                    {
                        Count(skipHist, "六边形墙皮网格");
                        continue;
                    }

                    // 已经应用过本工具的 renderer 不重复导出（否则原材质信息会丢）
                    string existingMeshPath = AssetDatabase.GetAssetPath(mesh);
                    if (!string.IsNullOrEmpty(existingMeshPath) &&
                        existingMeshPath.StartsWith(MeshDir + "/", StringComparison.Ordinal))
                    {
                        Count(skipHist, "已是 FaceSkin 生成 mesh");
                        continue;
                    }
                    if (HasAnyMaterialPath(shared, MatDir + "/"))
                    {
                        Count(skipHist, "已是 FaceSkin 生成材质");
                        continue;
                    }

                    if (!mesh.isReadable)
                    {
                        unreadable++;
                        Count(skipHist, "mesh 不可读(Read/Write 关闭)");
                        continue;
                    }

                    MeshAnalysis analysis;
                    try
                    {
                        analysis = AnalyzeMesh(mesh, r.transform.localToWorldMatrix, r.transform.worldToLocalMatrix);
                    }
                    catch (Exception e)
                    {
                        unreadable++;
                        Count(skipHist, "mesh 读取异常");
                        Debug.LogWarning($"[FaceSkin·导出] 读取 mesh 失败：{ObjectPath(r.transform)} / {mesh.name}：{e.Message}");
                        continue;
                    }

                    rendererCount++;

                    string objectPath = ObjectPath(r.transform);
                    var allRenderers = r.gameObject.GetComponents<Renderer>();
                    int rendererIndex = Array.IndexOf(allRenderers, r);
                    if (rendererIndex < 0) rendererIndex = 0;
                    bool multiRenderer = allRenderers.Length > 1;

                    for (int gi = 0; gi < analysis.Groups.Count; gi++)
                    {
                        var g = analysis.Groups[gi];
                        if (g.Area < MinFaceArea)
                        {
                            tinyFaces++;
                            continue;
                        }

                        // 区块地面候选：面积最大的朝上楼层（用于 wallHeightM）
                        if (g.Normal.y > 0.9f && g.Area > WalkableMinArea && g.Area > bestFloorArea)
                        {
                            bestFloorArea = g.Area;
                            groundY = g.Center.y;
                        }

                        var mat = GetMaterial(shared, g.Submesh);
                        string matName = mat != null ? mat.name : "";
                        Color baseColor = GetBaseColor(mat);
                        string style = ClassifyStyle(g.Normal, g.Area, matName);

                        string idBase = objectPath +
                                        (multiRenderer
                                            ? "#r" + rendererIndex.ToString(CultureInfo.InvariantCulture)
                                            : "");

                        var panels = BuildPanels(analysis, gi);
                        if (panels.Count > 1) panelizedFaces++;

                        foreach (var p in panels)
                        {
                            string panelSuffix = panels.Count > 1
                                ? "p" + p.PanelIndex.ToString("D2", CultureInfo.InvariantCulture)
                                : "";
                            string id = idBase + "#f" + gi.ToString("D2", CultureInfo.InvariantCulture) + panelSuffix;

                            string seedKey = objectPath + "|" +
                                             Fmt4(p.Center.x) + "," + Fmt4(p.Center.y) + "," + Fmt4(p.Center.z) +
                                             (panels.Count > 1
                                                 ? "|p" + p.PanelIndex.ToString(CultureInfo.InvariantCulture)
                                                 : "");
                            uint seed = Fnv1a(seedKey);
                            int variantIndex = p.Small
                                ? (int)((Fnv1a(style) + seed) % (uint)VariantCount)
                                : -1;
                            if (p.Small) smallCount++;

                            blockRecords.Add(new FaceRecord
                            {
                                Id = id,
                                ObjectPath = objectPath,
                                RendererIndex = rendererIndex,
                                Submesh = g.Submesh,
                                Style = style,
                                Shape = p.Shape,
                                W = p.MaxU - p.MinU,
                                H = p.MaxV - p.MinV,
                                Origin = p.Center,
                                U = p.U,
                                V = p.V,
                                N = p.Normal,
                                Outline = p.Outline,
                                WorldCenter = p.Center,
                                SourceMaterial = matName,
                                BaseColor = baseColor,
                                Seed = seed,
                                PanelIndex = p.PanelIndex,
                                PanelCount = panels.Count,
                                Small = p.Small,
                                VariantIndex = variantIndex,
                                IsWalkable = g.Normal.y > 0.9f && p.Area > WalkableMinArea,
                                AdjacentStyle = "",
                                Area = p.Area,
                            });

                            Count(styleHist, style);
                        }
                    }
                }

                // 本区块地面顶面：面积最大的朝上楼层；没有楼层时退回区块根节点高度
                if (float.IsNaN(groundY)) groundY = root.transform.position.y;
                foreach (var rec in blockRecords)
                    rec.WallHeightM = rec.WorldCenter.y - groundY;
                records.AddRange(blockRecords);
            }

            WriteSpec(records);

            int smallStyleCount = records.Where(x => x.Small)
                                         .Select(x => x.Style)
                                         .Distinct()
                                         .Count();
            double estBytes = EstimateTextureBytes(records, smallStyleCount);

            var rep = new StringBuilder();
            rep.AppendLine($"[FaceSkin·导出] 区块 {blockCount} 个；扫描 renderer {rendererCount} 个；写出面/面板 {records.Count} 个");
            rep.AppendLine($"  面积 < {MinFaceArea} m² 跳过 {tinyFaces} 个；不可读 mesh {unreadable} 个");
            rep.AppendLine($"  切板面 {panelizedFaces} 个；小面(<{SmallFaceArea} m²) {smallCount} 个，" +
                           $"变体组 {smallStyleCount} style × {VariantCount} 张");
            rep.AppendLine($"  贴图粗略估算（{AssumedMmPerPixel}mm/px，5 图 RGBA8 + mip）≈ " +
                           $"{estBytes / (1024d * 1024d * 1024d):F2} GB / 目标 3 GB");
            if (estBytes > TextureBudgetBytes)
            {
                rep.AppendLine($"  [预算警告] 预估超过 3GB：优先把 <1m² 的件并入 8 变体（本工具已把 <{SmallFaceArea}m² 全部标记为 small）");
            }
            if (styleHist.Count > 0)
            {
                rep.AppendLine("  style 分布：" + string.Join("    ",
                    styleHist.OrderByDescending(kv => kv.Value)
                             .Select(kv => $"{kv.Key}={kv.Value}")));
            }
            if (skipHist.Count > 0)
            {
                rep.AppendLine("  跳过 renderer：");
                foreach (var kv in skipHist.OrderByDescending(kv => kv.Value))
                    rep.AppendLine($"    [{kv.Value,4}] {kv.Key}");
            }
            rep.AppendLine($"  spec → {SpecPath}");
            Debug.Log(rep.ToString());
        }

        /// <summary>粗略贴图体积估算，只用于 3GB 预算预警；不代表生成器最终分辨率/压缩。</summary>
        static double EstimateTextureBytes(List<FaceRecord> records, int smallStyleCount)
        {
            const double pxPerMeter = 1000d / AssumedMmPerPixel;
            const double bytesPerPixel = 4d * 5d * 1.3333d; // 5 张 RGBA8 + mip 链

            double bytes = 0d;
            foreach (var r in records)
            {
                if (r.Small) continue;
                // 单张面贴图受导入 maxTextureSize=4096 限制
                double w = Math.Min(4096d, Math.Max(1d, Math.Ceiling(r.W * pxPerMeter)));
                double h = Math.Min(4096d, Math.Max(1d, Math.Ceiling(r.H * pxPerMeter)));
                bytes += w * h * bytesPerPixel;
            }

            // 每个 style 8 张变体，按 1m×1m 参考尺寸估
            double variantPx = pxPerMeter;
            bytes += smallStyleCount * VariantCount * variantPx * variantPx * bytesPerPixel;
            return bytes;
        }

        /// <summary>读取 face_skin_manifest.json，按面拆 submesh 并回贴材质。</summary>
        public static void Apply()
        {
            if (!EnsureScene()) return;
            if (!File.Exists(ManifestPath))
            {
                Debug.LogError($"[FaceSkin] 找不到 manifest：{ManifestPath}");
                return;
            }

            FaceSkinManifest manifest = null;
            try
            {
                manifest = JsonUtility.FromJson<FaceSkinManifest>(File.ReadAllText(ManifestPath));
            }
            catch (Exception e)
            {
                Debug.LogError($"[FaceSkin] manifest 解析失败：{e.Message}");
                return;
            }
            if (manifest == null || manifest.faces == null || manifest.faces.Count == 0)
            {
                Debug.LogWarning($"[FaceSkin] manifest 没有 faces：{ManifestPath}");
                return;
            }

            var restore = LoadRestore() ?? new FaceSkinRestore();
            if (restore.renderers == null) restore.renderers = new List<RestoreEntry>();

            var specRendererMap = LoadSpecRendererMap();
            var stats = new ApplyStats();
            stats.ManifestFaces = manifest.faces.Count;

            // 按 objectPath 分组（排序保证批处理输出稳定）
            var byPath = new Dictionary<string, List<ManifestFace>>(StringComparer.Ordinal);
            foreach (var f in manifest.faces)
            {
                if (f == null || string.IsNullOrEmpty(f.objectPath)) continue;
                if (!byPath.TryGetValue(f.objectPath, out var list))
                {
                    list = new List<ManifestFace>();
                    byPath[f.objectPath] = list;
                }
                list.Add(f);
            }

            var report = new StringBuilder();
            foreach (string objectPath in byPath.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var faces = byPath[objectPath];
                var go = FindByPath(objectPath);
                if (go == null)
                {
                    stats.MissingObjects++;
                    report.AppendLine($"    [找不到对象] {objectPath}（{faces.Count} 个面）");
                    continue;
                }

                var renderers = go.GetComponents<Renderer>();
                if (renderers.Length == 0)
                {
                    stats.MissingObjects++;
                    report.AppendLine($"    [对象无 Renderer] {objectPath}");
                    continue;
                }

                var byRenderer = new Dictionary<int, List<ManifestFace>>();
                foreach (var f in faces)
                {
                    int ri = 0;
                    if (specRendererMap != null && specRendererMap.TryGetValue(f.id, out int specRi))
                        ri = specRi;
                    else
                        ri = ParseRendererIndex(f.id);

                    if (ri < 0 || ri >= renderers.Length)
                    {
                        // 没有 spec 时默认 0；id 里带 #rN 但对象结构变了则退回 0
                        ri = 0;
                    }

                    if (!byRenderer.TryGetValue(ri, out var list))
                    {
                        list = new List<ManifestFace>();
                        byRenderer[ri] = list;
                    }
                    list.Add(f);
                }

                foreach (int ri in byRenderer.Keys.OrderBy(x => x))
                {
                    var r = renderers[ri];
                    if (r == null)
                    {
                        stats.SkippedRenderers++;
                        report.AppendLine($"    [Renderer 为空] {objectPath} #r{ri}");
                        continue;
                    }
                    ProcessRenderer(r, objectPath, ri, byRenderer[ri], restore, stats, report);
                }
            }

            if (restore.renderers.Count > 0) SaveRestore(restore);

            AssetDatabase.SaveAssets();
            SaveScene();

            var summary = new StringBuilder();
            summary.AppendLine($"[FaceSkin·应用] manifest 面 {stats.ManifestFaces} 个；匹配并拆分面/面板 {stats.MatchedFaces} 个；" +
                               $"改动 renderer {stats.ChangedRenderers} 个 / 对象 {stats.ChangedObjects} 个");
            summary.AppendLine($"  缺失贴图 {stats.MissingTextures} 张；缺失面 {stats.MissingFaces} 个；" +
                               $"找不到对象 {stats.MissingObjects} 个；跳过 renderer {stats.SkippedRenderers} 个" +
                               $"（其中顶点超限 {stats.VertexLimitSkips} 个）");
            if (stats.MaterialFailures > 0)
                summary.AppendLine($"  材质创建失败 {stats.MaterialFailures} 个（对应面继续用原材质）");
            summary.AppendLine($"  restore → {RestorePath}");
            if (report.Length > 0) summary.Append(report);
            Debug.Log(summary.ToString());
        }

        /// <summary>按 face_skin_restore.json 还原原 mesh / 原材质，并删除本工具生成的资产。</summary>
        public static void Clear()
        {
            if (!EnsureScene()) return;
            if (!File.Exists(RestorePath))
            {
                Debug.LogWarning($"[FaceSkin·还原] 找不到还原表：{RestorePath}");
                return;
            }

            var restore = LoadRestore();
            if (restore == null || restore.renderers == null || restore.renderers.Count == 0)
            {
                Debug.LogWarning($"[FaceSkin·还原] 还原表为空：{RestorePath}");
                return;
            }

            int restored = 0, failed = 0, deleted = 0;
            var report = new StringBuilder();
            var generatedMeshes = new List<string>();
            var generatedMats = new List<string>();

            foreach (var e in restore.renderers)
            {
                if (e == null) continue;

                if (!string.IsNullOrEmpty(e.generatedMeshAssetPath))
                    generatedMeshes.Add(e.generatedMeshAssetPath);
                if (e.generatedMaterialPaths != null)
                    generatedMats.AddRange(e.generatedMaterialPaths);

                var go = FindByPath(e.objectPath);
                if (go == null)
                {
                    failed++;
                    report.AppendLine($"    [找不到对象] {e.objectPath}");
                    continue;
                }

                var renderers = go.GetComponents<Renderer>();
                if (e.rendererIndex < 0 || e.rendererIndex >= renderers.Length)
                {
                    failed++;
                    report.AppendLine($"    [Renderer 下标失效] {e.objectPath} #r{e.rendererIndex}");
                    continue;
                }

                var r = renderers[e.rendererIndex];
                var mf = r != null ? r.GetComponent<MeshFilter>() : null;
                if (mf == null)
                {
                    failed++;
                    report.AppendLine($"    [没有 MeshFilter] {e.objectPath} #r{e.rendererIndex}");
                    continue;
                }

                var mesh = LoadSourceMesh(e.originalMeshAssetPath, e.originalMeshName);
                if (mesh == null)
                {
                    failed++;
                    report.AppendLine($"    [找不到原 mesh] {e.objectPath} ← {e.originalMeshAssetPath} / {e.originalMeshName}");
                }
                else
                {
                    mf.sharedMesh = mesh;
                    restored++;
                }

                var mats = LoadOriginalMaterials(e);
                if (mats != null && mats.Length > 0)
                    r.sharedMaterials = mats;
                else
                {
                    failed++;
                    report.AppendLine($"    [找不到原材质表] {e.objectPath} #r{e.rendererIndex}");
                }
            }

            // 先还原再统计引用，避免删掉仍被别的 renderer 使用的资产
            var usedMeshes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var p = AssetDatabase.GetAssetPath(mf.sharedMesh);
                if (!string.IsNullOrEmpty(p)) usedMeshes.Add(p);
            }

            var usedMats = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    var p = AssetDatabase.GetAssetPath(m);
                    if (!string.IsNullOrEmpty(p)) usedMats.Add(p);
                }
            }

            foreach (var p in generatedMeshes.Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (!p.StartsWith(MeshDir + "/", StringComparison.Ordinal)) continue;
                if (usedMeshes.Contains(p)) continue;
                if (AssetDatabase.DeleteAsset(p)) deleted++;
            }

            foreach (var p in generatedMats.Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (!p.StartsWith(MatDir + "/", StringComparison.Ordinal)) continue;
                if (usedMats.Contains(p)) continue;
                if (AssetDatabase.DeleteAsset(p)) deleted++;
            }

            AssetDatabase.SaveAssets();
            SaveScene();

            if (failed == 0)
            {
                try { File.Delete(RestorePath); }
                catch (Exception e) { Debug.LogWarning($"[FaceSkin·还原] 删除还原表失败：{e.Message}"); }
            }
            else
            {
                Debug.LogWarning($"[FaceSkin·还原] 仍有 {failed} 件未还原，保留还原表：{RestorePath}");
            }

            var rep = new StringBuilder();
            rep.AppendLine($"[FaceSkin·还原] 恢复 renderer {restored} 个；失败 {failed} 个；删除生成资产 {deleted} 个");
            if (report.Length > 0) rep.Append(report);
            Debug.Log(rep.ToString());
        }

        // ---------------------------------------------------------------- 导出数据

        class FaceRecord
        {
            public string Id;
            public string ObjectPath;
            public int RendererIndex;
            public int Submesh;
            public string Style;
            public string Shape;
            public float W;
            public float H;
            public Vector3 Origin;
            public Vector3 U;
            public Vector3 V;
            public Vector3 N;
            public List<Vector2> Outline;
            public Vector3 WorldCenter;
            public string SourceMaterial;
            public Color BaseColor;
            public uint Seed;

            // ---- 面板 / 小面 / 生成辅助字段
            public int PanelIndex;
            public int PanelCount;
            public bool Small;
            public int VariantIndex;
            public float WallHeightM;
            public bool IsWalkable;
            public string AdjacentStyle;
            public float Area;
        }

        static void WriteSpec(List<FaceRecord> faces)
        {
            EnsureAuditDir();
            var sb = new StringBuilder(1 << 20);
            sb.Append("{\"version\":1,\"faces\":[");
            for (int i = 0; i < faces.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var f = faces[i];
                sb.Append("\n{\"id\":\"").Append(Escape(f.Id)).Append("\",");
                sb.Append("\"objectPath\":\"").Append(Escape(f.ObjectPath)).Append("\",");
                sb.Append("\"rendererIndex\":").Append(f.RendererIndex.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"submesh\":").Append(f.Submesh.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"style\":\"").Append(Escape(f.Style)).Append("\",");
                sb.Append("\"shape\":\"").Append(Escape(f.Shape)).Append("\",");
                sb.Append("\"size\":[").Append(F(f.W)).Append(',').Append(F(f.H)).Append("],");
                sb.Append("\"plane\":{\"origin\":").Append(V(f.Origin))
                  .Append(",\"u\":").Append(V(f.U))
                  .Append(",\"v\":").Append(V(f.V))
                  .Append(",\"n\":").Append(V(f.N)).Append("},");
                sb.Append("\"outline\":[");
                for (int k = 0; k < f.Outline.Count; k++)
                {
                    if (k > 0) sb.Append(',');
                    sb.Append('[').Append(F(f.Outline[k].x)).Append(',').Append(F(f.Outline[k].y)).Append(']');
                }
                sb.Append("],");
                sb.Append("\"worldCenter\":").Append(V(f.WorldCenter)).Append(',');
                sb.Append("\"sourceMaterial\":\"").Append(Escape(f.SourceMaterial)).Append("\",");
                sb.Append("\"baseColor\":[").Append(F(f.BaseColor.r)).Append(',')
                  .Append(F(f.BaseColor.g)).Append(',')
                  .Append(F(f.BaseColor.b)).Append("],");
                sb.Append("\"seed\":").Append(f.Seed.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"panelIndex\":").Append(f.PanelIndex.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"panelCount\":").Append(f.PanelCount.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"small\":").Append(f.Small ? "true" : "false").Append(',');
                sb.Append("\"variantIndex\":").Append(f.VariantIndex.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append("\"wallHeightM\":").Append(F(f.WallHeightM)).Append(',');
                sb.Append("\"isWalkable\":").Append(f.IsWalkable ? "true" : "false").Append(',');
                sb.Append("\"adjacentStyle\":\"").Append(Escape(f.AdjacentStyle)).Append("\"");
                sb.Append('}');
            }
            sb.Append("\n]}\n");
            File.WriteAllText(SpecPath, sb.ToString(), new UTF8Encoding(false));
        }

        static string ClassifyStyle(Vector3 n, float area, string materialName)
        {
            string m = materialName ?? "";
            bool wall = Mathf.Abs(n.y) < 0.3f;

            // 地面：法线朝上或材质名带 Floor（优先级最高，避免被 014/White 抢走）
            if (n.y > 0.9f || Contains(m, "Floor")) return "floor";
            // 黑铁 / 机架
            if (Contains(m, "005") || Contains(m, "Black") || Contains(m, "Rack")) return "metal_dark";
            // 白漆竖直墙
            if (wall && (Contains(m, "014") || Contains(m, "White"))) return "painted_white";
            // 青色信息带
            if (Contains(m, "015") || Contains(m, "Cyan")) return "info_band";
            // 橙 / 黄：可行走金属，警示条交给贴图
            if (Contains(m, "038") || Contains(m, "061")) return "walk_metal";
            // 攀爬面
            if (Contains(m, "climb_")) return "climb_metal";
            // 其余梁 / 柱
            return "trim";
        }

        static Color GetBaseColor(Material m)
        {
            if (m == null) return Color.white;
            if (m.HasProperty("_BaseColor")) return m.GetColor("_BaseColor");
            if (m.HasProperty("_Color")) return m.GetColor("_Color");
            return Color.white;
        }

        // ---------------------------------------------------------------- mesh 分析

        class TriGroup
        {
            public List<int> Triangles;
            public int Submesh;
            public Vector3 Normal;
            public Vector3 Center;
            public float Area;
            public List<int> Vertices;
            public Vector3 U;
            public Vector3 V;
            public float MinU, MaxU, MinV, MaxV;
            public List<Vector2> Outline;
            public string Shape;
        }

        class MeshAnalysis
        {
            public Vector3[] LocalVerts;
            public Vector3[] WorldVerts;
            public Vector3[] Normals;
            public Vector2[] Uv0;
            public Vector4[] Tangents;
            public int[] FlatTris;
            public int[] TriSub;
            public int[] TriGroup;
            public Vector3[] TriNormals;
            public Vector3[] TriCenters;
            public float[] TriAreas;
            public bool[] TriDegenerate;
            public List<TriGroup> Groups;
            public int SubmeshCount;
            public int TriangleCount;
            public Matrix4x4 WorldToLocal;
            public bool HasNormals;
            public bool HasUv0;
            public bool HasTangents;
        }

        /// <summary>
        /// 把 mesh 三角形按“同 submesh + 共享顶点”连通，再按代表平面切分：
        /// 法线点积 > 0.999 且三顶点到代表平面距离都 < 5mm 才归入同一面。
        /// 分组顺序：连通块按最小三角形下标排序；块内每次取面积最大的三角形做种子。
        /// 该顺序在 Export 与 Apply 中完全一致，面 id 的 #fNN 才能对齐。
        /// </summary>
        static MeshAnalysis AnalyzeMesh(Mesh mesh, Matrix4x4 localToWorld, Matrix4x4 worldToLocal)
        {
            var result = new MeshAnalysis
            {
                Groups = new List<TriGroup>(),
                WorldToLocal = worldToLocal,
                SubmeshCount = mesh.subMeshCount,
            };

            var local = mesh.vertices;
            if (local == null || local.Length != mesh.vertexCount)
                throw new InvalidOperationException("mesh 顶点不可读（Read/Write 关闭？）");

            result.LocalVerts = local;
            var world = new Vector3[local.Length];
            for (int i = 0; i < local.Length; i++)
                world[i] = localToWorld.MultiplyPoint3x4(local[i]);
            result.WorldVerts = world;

            var normals = mesh.normals;
            result.HasNormals = normals != null && normals.Length == local.Length && local.Length > 0;
            result.Normals = result.HasNormals ? normals : null;

            var uv0 = mesh.uv;
            result.HasUv0 = uv0 != null && uv0.Length == local.Length && local.Length > 0;
            result.Uv0 = result.HasUv0 ? uv0 : null;

            var tangents = mesh.tangents;
            result.HasTangents = tangents != null && tangents.Length == local.Length && local.Length > 0;
            result.Tangents = result.HasTangents ? tangents : null;

            int subCount = mesh.subMeshCount;
            var triList = new List<int>();
            var triSubList = new List<int>();
            for (int s = 0; s < subCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                if (tris == null) continue;
                for (int k = 0; k + 2 < tris.Length; k += 3)
                {
                    triList.Add(tris[k]);
                    triList.Add(tris[k + 1]);
                    triList.Add(tris[k + 2]);
                    triSubList.Add(s);
                }
            }

            int triCount = triList.Count / 3;
            result.TriangleCount = triCount;
            result.FlatTris = triList.ToArray();
            result.TriSub = triSubList.ToArray();
            result.TriGroup = new int[triCount];
            for (int t = 0; t < triCount; t++) result.TriGroup[t] = -1;

            var triN = new Vector3[triCount];
            var triC = new Vector3[triCount];
            var triA = new float[triCount];
            var triDeg = new bool[triCount];

            var flat = result.FlatTris;
            for (int t = 0; t < triCount; t++)
            {
                int i0 = flat[t * 3], i1 = flat[t * 3 + 1], i2 = flat[t * 3 + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 ||
                    i0 >= world.Length || i1 >= world.Length || i2 >= world.Length)
                {
                    triDeg[t] = true;
                    continue;
                }

                Vector3 a = world[i0], b = world[i1], c = world[i2];
                Vector3 cross = Vector3.Cross(b - a, c - a);
                if (cross.sqrMagnitude < MinTriCrossSqr)
                {
                    triDeg[t] = true;
                    continue;
                }

                triN[t] = cross.normalized;
                triC[t] = (a + b + c) / 3f;
                triA[t] = 0.5f * cross.magnitude;
            }

            result.TriNormals = triN;
            result.TriCenters = triC;
            result.TriAreas = triA;
            result.TriDegenerate = triDeg;

            if (triCount == 0) return result;

            // ---- 并查集：同 submesh 且共享顶点索引 -> 连通
            var parent = new int[triCount];
            var rank = new byte[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;

            var firstByVertex = new Dictionary<long, int>(triCount * 2);
            for (int t = 0; t < triCount; t++)
            {
                if (triDeg[t]) continue;
                int sub = result.TriSub[t];
                for (int k = 0; k < 3; k++)
                {
                    int vi = flat[t * 3 + k];
                    long key = ((long)sub << 32) | (uint)vi;
                    if (firstByVertex.TryGetValue(key, out int other))
                        Union(parent, rank, t, other);
                    else
                        firstByVertex[key] = t;
                }
            }

            var components = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                if (triDeg[t]) continue;
                int root = Find(parent, t);
                if (!components.TryGetValue(root, out var list))
                {
                    list = new List<int>();
                    components[root] = list;
                }
                list.Add(t);
            }

            var compList = components.Values.ToList();
            compList.Sort((a, b) => a[0].CompareTo(b[0]));

            // ---- 连通块内按代表平面切分
            foreach (var component in compList)
            {
                var remaining = component;
                while (remaining.Count > 0)
                {
                    int seed = SelectSeedTriangle(remaining, triA);
                    Vector3 seedN = triN[seed];
                    Vector3 seedP = triC[seed];

                    var inGroup = new List<int>();
                    var rest = new List<int>(remaining.Count);

                    foreach (int t in remaining)
                    {
                        if (Vector3.Dot(triN[t], seedN) > SameNormalDot &&
                            TriangleOnPlane(t, seedN, seedP, flat, world))
                        {
                            inGroup.Add(t);
                        }
                        else
                        {
                            rest.Add(t);
                        }
                    }

                    int groupIndex = result.Groups.Count;
                    foreach (int t in inGroup) result.TriGroup[t] = groupIndex;
                    result.Groups.Add(BuildGroup(inGroup, result));
                    remaining = rest;
                }
            }

            return result;
        }

        static int SelectSeedTriangle(List<int> tris, float[] areas)
        {
            int seed = tris[0];
            float best = areas[seed];
            for (int i = 1; i < tris.Count; i++)
            {
                int t = tris[i];
                float a = areas[t];
                if (a > best || (Mathf.Approximately(a, best) && t < seed))
                {
                    seed = t;
                    best = a;
                }
            }
            return seed;
        }

        static bool TriangleOnPlane(int t, Vector3 n, Vector3 p, int[] flat, Vector3[] world)
        {
            for (int k = 0; k < 3; k++)
            {
                Vector3 v = world[flat[t * 3 + k]];
                if (Mathf.Abs(Vector3.Dot(n, v - p)) > PlaneDistTol) return false;
            }
            return true;
        }

        static TriGroup BuildGroup(List<int> tris, MeshAnalysis a)
        {
            var g = new TriGroup
            {
                Triangles = tris,
                Submesh = a.TriSub[tris[0]],
            };

            float area = 0f;
            Vector3 normalSum = Vector3.zero;
            Vector3 centerSum = Vector3.zero;
            var vertexSet = new HashSet<int>();

            foreach (int t in tris)
            {
                float ta = a.TriAreas[t];
                area += ta;
                normalSum += a.TriNormals[t] * ta;
                centerSum += a.TriCenters[t] * ta;
                for (int k = 0; k < 3; k++)
                    vertexSet.Add(a.FlatTris[t * 3 + k]);
            }

            g.Area = area;
            g.Normal = normalSum.sqrMagnitude > 1e-12f
                ? normalSum.normalized
                : a.TriNormals[tris[0]];
            g.Center = area > 1e-12f
                ? centerSum / area
                : a.TriCenters[tris[0]];

            g.Vertices = new List<int>(vertexSet);
            g.Vertices.Sort();

            // ---- u/v 轴：v 尽量朝世界上方；水平面 v = 世界 +Z；u = v × n 保证右手系
            Vector3 n = g.Normal;
            Vector3 v = Vector3.up - n * Vector3.Dot(Vector3.up, n);
            if (v.sqrMagnitude < 1e-8f)
                v = Vector3.forward - n * Vector3.Dot(Vector3.forward, n);
            if (v.sqrMagnitude < 1e-8f)
                v = Vector3.right - n * Vector3.Dot(Vector3.right, n);
            v.Normalize();

            Vector3 u = Vector3.Cross(v, n);
            if (u.sqrMagnitude < 1e-8f)
                u = Vector3.right;
            u.Normalize();

            g.U = u;
            g.V = v;

            float minU = float.MaxValue, maxU = float.MinValue;
            float minV = float.MaxValue, maxV = float.MinValue;
            var projected = new List<Vector2>(g.Vertices.Count);

            foreach (int vi in g.Vertices)
            {
                Vector3 p = a.WorldVerts[vi] - g.Center;
                float pu = Vector3.Dot(p, u);
                float pv = Vector3.Dot(p, v);
                if (pu < minU) minU = pu;
                if (pu > maxU) maxU = pu;
                if (pv < minV) minV = pv;
                if (pv > maxV) maxV = pv;
                projected.Add(new Vector2(pu, pv));
            }

            if (g.Vertices.Count == 0)
            {
                minU = maxU = minV = maxV = 0f;
            }

            g.MinU = minU;
            g.MaxU = maxU;
            g.MinV = minV;
            g.MaxV = maxV;
            g.Outline = ConvexHull(projected);
            g.Shape = IsRectangle(g.Outline) ? "rect" : "poly";

            return g;
        }

        /// <summary>Andrew monotone chain；顺带去掉共线点，矩形面只留 4 个角。</summary>
        static List<Vector2> ConvexHull(List<Vector2> input)
        {
            var hull = new List<Vector2>();
            if (input == null || input.Count == 0) return hull;

            var pts = new List<Vector2>(input);
            pts.Sort((x, y) =>
            {
                int c = x.x.CompareTo(y.x);
                return c != 0 ? c : x.y.CompareTo(y.y);
            });

            var uniq = new List<Vector2>(pts.Count);
            foreach (var p in pts)
            {
                if (uniq.Count == 0 ||
                    (p - uniq[uniq.Count - 1]).sqrMagnitude > HullDedupeSqr)
                {
                    uniq.Add(p);
                }
            }

            if (uniq.Count <= 2) return uniq;

            var lower = new List<Vector2>();
            foreach (var p in uniq)
            {
                while (lower.Count >= 2 &&
                       Cross(lower[lower.Count - 1] - lower[lower.Count - 2],
                             p - lower[lower.Count - 1]) <= HullCollinearEps)
                {
                    lower.RemoveAt(lower.Count - 1);
                }
                lower.Add(p);
            }

            var upper = new List<Vector2>();
            for (int i = uniq.Count - 1; i >= 0; i--)
            {
                var p = uniq[i];
                while (upper.Count >= 2 &&
                       Cross(upper[upper.Count - 1] - upper[upper.Count - 2],
                             p - upper[upper.Count - 1]) <= HullCollinearEps)
                {
                    upper.RemoveAt(upper.Count - 1);
                }
                upper.Add(p);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        static bool IsRectangle(List<Vector2> hull)
        {
            if (hull == null || hull.Count != 4) return false;
            for (int i = 0; i < 4; i++)
            {
                Vector2 p0 = hull[i];
                Vector2 p1 = hull[(i + 1) % 4];
                Vector2 p2 = hull[(i + 2) % 4];
                Vector2 e1 = p1 - p0;
                Vector2 e2 = p2 - p1;
                if (e1.sqrMagnitude < 1e-10f || e2.sqrMagnitude < 1e-10f) return false;
                e1.Normalize();
                e2.Normalize();
                if (Mathf.Abs(Vector2.Dot(e1, e2)) > 0.02f) return false;
            }
            return true;
        }

        static void Union(int[] parent, byte[] rank, int a, int b)
        {
            a = Find(parent, a);
            b = Find(parent, b);
            if (a == b) return;
            if (rank[a] < rank[b]) parent[a] = b;
            else if (rank[a] > rank[b]) parent[b] = a;
            else { parent[b] = a; rank[a]++; }
        }

        static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        // ---------------------------------------------------------------- 大面切板

        /// <summary>切板用顶点：同时保留局部/世界位置，裁切时按边线性插值。</summary>
        struct ClipVert
        {
            public Vector3 Local;
            public Vector3 World;
            public Vector3 Normal;
            public Vector2 Uv0;
            public Vector4 Tangent;
            public bool HasNormal;
            public bool HasUv0;
            public bool HasTangent;
        }

        struct ClipTri
        {
            public ClipVert A, B, C;
        }

        /// <summary>一块面板：几何 + 自己的 u/v 包围盒 / 轮廓 / 中心。</summary>
        class PanelGeometry
        {
            public int GroupIndex;
            public int PanelIndex;
            public int PanelCount;
            public int Submesh;
            public List<ClipTri> Tris;
            public float Area;
            public Vector3 Normal;
            public Vector3 Center;
            public Vector3 U, V;
            public float MinU, MaxU, MinV, MaxV;
            public List<Vector2> Outline;
            public string Shape;
            public bool Small;
        }

        /// <summary>面板的最终 mesh 数据（每个面板一个 submesh）。</summary>
        class PanelBuild
        {
            public PanelGeometry Panel;
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<Vector4> Tangents = new List<Vector4>();
            public readonly List<int> Indices = new List<int>();
        }

        /// <summary>面板内顶点去重键：位置 0.1mm + 法线 1e-3 量化。</summary>
        readonly struct VertKey : IEquatable<VertKey>
        {
            readonly long X, Y, Z, NX, NY, NZ;

            public VertKey(Vector3 p, Vector3 n)
            {
                X = (long)Math.Round(p.x / 0.0001);
                Y = (long)Math.Round(p.y / 0.0001);
                Z = (long)Math.Round(p.z / 0.0001);
                NX = (long)Math.Round(n.x / 0.001);
                NY = (long)Math.Round(n.y / 0.001);
                NZ = (long)Math.Round(n.z / 0.001);
            }

            public bool Equals(VertKey o) =>
                X == o.X && Y == o.Y && Z == o.Z &&
                NX == o.NX && NY == o.NY && NZ == o.NZ;

            public override bool Equals(object obj) => obj is VertKey o && Equals(o);

            public override int GetHashCode()
            {
                unchecked
                {
                    long h = X * 73856093L ^ Y * 19349663L ^ Z * 83492791L ^
                             NX * 51392867L ^ NY * 23456789L ^ NZ * 34567891L;
                    return (int)(h ^ (h >> 32));
                }
            }
        }

        /// <summary>
        /// 把一个大面切成规则面板：
        ///   · 只有 W>10m 或 H>10m 才切；
        ///   · 切分线固定在 7.2m（1.2/1.8/2.4 的公倍数）的全局网格上，
        ///     s/t 用世界坐标点乘面内 u/v，保证相邻同向面的分缝对齐；
        ///   · 面边缘不足 1.2m 的碎条并入相邻面板（删掉该条切分线），
        ///     因此实际面板约为 7.2m，边缘可能到 7.2–8.4m；
        ///   · 每个面板独立计算中心、尺寸、轮廓、seed。
        /// </summary>
        static List<PanelGeometry> BuildPanels(MeshAnalysis a, int groupIndex)
        {
            var g = a.Groups[groupIndex];
            var panels = new List<PanelGeometry>();

            float minS = float.MaxValue, maxS = float.MinValue;
            float minT = float.MaxValue, maxT = float.MinValue;
            foreach (int vi in g.Vertices)
            {
                Vector3 wp = a.WorldVerts[vi];
                float s = Vector3.Dot(wp, g.U);
                float t = Vector3.Dot(wp, g.V);
                if (s < minS) minS = s;
                if (s > maxS) maxS = s;
                if (t < minT) minT = t;
                if (t > maxT) maxT = t;
            }
            if (g.Vertices.Count == 0) minS = maxS = minT = maxT = 0f;

            int triCount = g.Triangles.Count;
            var triVerts = new ClipTri[triCount];
            var triMinS = new float[triCount];
            var triMaxS = new float[triCount];
            var triMinT = new float[triCount];
            var triMaxT = new float[triCount];

            for (int i = 0; i < triCount; i++)
            {
                int t = g.Triangles[i];
                var cv0 = MakeClipVert(a, a.FlatTris[t * 3]);
                var cv1 = MakeClipVert(a, a.FlatTris[t * 3 + 1]);
                var cv2 = MakeClipVert(a, a.FlatTris[t * 3 + 2]);
                triVerts[i] = new ClipTri { A = cv0, B = cv1, C = cv2 };

                float s0 = Vector3.Dot(cv0.World, g.U);
                float s1 = Vector3.Dot(cv1.World, g.U);
                float s2 = Vector3.Dot(cv2.World, g.U);
                float t0 = Vector3.Dot(cv0.World, g.V);
                float t1 = Vector3.Dot(cv1.World, g.V);
                float t2 = Vector3.Dot(cv2.World, g.V);
                triMinS[i] = Mathf.Min(s0, Mathf.Min(s1, s2));
                triMaxS[i] = Mathf.Max(s0, Mathf.Max(s1, s2));
                triMinT[i] = Mathf.Min(t0, Mathf.Min(t1, t2));
                triMaxT[i] = Mathf.Max(t0, Mathf.Max(t1, t2));
            }

            bool needSplit = (maxS - minS) > PanelThreshold + 1e-4f ||
                             (maxT - minT) > PanelThreshold + 1e-4f;

            if (!needSplit)
            {
                var all = new List<ClipTri>(triCount);
                for (int i = 0; i < triCount; i++) all.Add(triVerts[i]);
                panels.Add(BuildPanelGeometry(all, groupIndex, 0, 1, g));
                return panels;
            }

            var cutsS = GridCuts(minS, maxS);
            var cutsT = GridCuts(minT, maxT);

            var edgesS = new List<float> { minS };
            edgesS.AddRange(cutsS);
            edgesS.Add(maxS);

            var edgesT = new List<float> { minT };
            edgesT.AddRange(cutsT);
            edgesT.Add(maxT);

            int panelIndex = 0;
            for (int i = 0; i < edgesS.Count - 1; i++)
            {
                float s0 = edgesS[i];
                float s1 = edgesS[i + 1];
                if (s1 - s0 < 1e-4f) continue;

                for (int j = 0; j < edgesT.Count - 1; j++)
                {
                    float t0 = edgesT[j];
                    float t1 = edgesT[j + 1];
                    if (t1 - t0 < 1e-4f) continue;

                    var clipped = new List<ClipTri>();
                    for (int k = 0; k < triCount; k++)
                    {
                        // 2D AABB 快速剔除
                        if (triMaxS[k] < s0 - 1e-5f || triMinS[k] > s1 + 1e-5f ||
                            triMaxT[k] < t0 - 1e-5f || triMinT[k] > t1 + 1e-5f)
                            continue;

                        var poly = ClipTriangle(triVerts[k], g.U, g.V, s0, s1, t0, t1);
                        if (poly == null || poly.Count < 3) continue;

                        for (int q = 1; q < poly.Count - 1; q++)
                        {
                            // 1e-6 m² 以下的裁切碎屑直接丢弃，避免边界重复面片
                            if (TriangleArea(poly[0].World, poly[q].World, poly[q + 1].World) < 1e-6f)
                                continue;
                            clipped.Add(new ClipTri { A = poly[0], B = poly[q], C = poly[q + 1] });
                        }
                    }

                    if (clipped.Count == 0) continue;
                    panels.Add(BuildPanelGeometry(clipped, groupIndex, panelIndex, 0, g));
                    panelIndex++;
                }
            }

            // 极端情况下裁切全部为空：退回整面一块，避免丢几何
            if (panels.Count == 0)
            {
                var all = new List<ClipTri>(triCount);
                for (int i = 0; i < triCount; i++) all.Add(triVerts[i]);
                panels.Add(BuildPanelGeometry(all, groupIndex, 0, 1, g));
                return panels;
            }

            foreach (var p in panels) p.PanelCount = panels.Count;
            return panels;
        }

        /// <summary>生成某一轴上的切分线（全局 7.2m 网格；剔除会造成 &lt;1.2m 碎条的边缘线）。</summary>
        static List<float> GridCuts(float min, float max)
        {
            var cuts = new List<float>();
            if (max - min <= PanelThreshold + 1e-4f) return cuts;

            int k0 = Mathf.FloorToInt(min / PanelStep) + 1;
            int k1 = Mathf.CeilToInt(max / PanelStep) - 1;
            for (int k = k0; k <= k1; k++)
            {
                float c = k * PanelStep;
                if (c > min + 1e-4f && c < max - 1e-4f) cuts.Add(c);
            }

            if (cuts.Count > 0 && cuts[0] - min < MinPanelStrip - 1e-4f)
                cuts.RemoveAt(0);
            if (cuts.Count > 0 && max - cuts[cuts.Count - 1] < MinPanelStrip - 1e-4f)
                cuts.RemoveAt(cuts.Count - 1);

            return cuts;
        }

        static PanelGeometry BuildPanelGeometry(List<ClipTri> tris, int groupIndex,
                                                int panelIndex, int panelCount, TriGroup g)
        {
            var p = new PanelGeometry
            {
                GroupIndex = groupIndex,
                PanelIndex = panelIndex,
                PanelCount = panelCount,
                Submesh = g.Submesh,
                Tris = tris,
                Normal = g.Normal,
                U = g.U,
                V = g.V,
            };

            float area = 0f;
            Vector3 centerSum = Vector3.zero;
            var worldPts = new List<Vector3>(tris.Count * 3);

            foreach (var tri in tris)
            {
                float ta = TriangleArea(tri.A.World, tri.B.World, tri.C.World);
                if (ta <= 0f) continue;
                area += ta;
                centerSum += (tri.A.World + tri.B.World + tri.C.World) / 3f * ta;
                worldPts.Add(tri.A.World);
                worldPts.Add(tri.B.World);
                worldPts.Add(tri.C.World);
            }

            p.Area = area;
            p.Small = area < SmallFaceArea;
            p.Center = area > 1e-12f ? centerSum / area : g.Center;

            float minU = float.MaxValue, maxU = float.MinValue;
            float minV = float.MaxValue, maxV = float.MinValue;
            var projected = new List<Vector2>(worldPts.Count);

            foreach (var wp in worldPts)
            {
                Vector3 rel = wp - p.Center;
                float pu = Vector3.Dot(rel, p.U);
                float pv = Vector3.Dot(rel, p.V);
                if (pu < minU) minU = pu;
                if (pu > maxU) maxU = pu;
                if (pv < minV) minV = pv;
                if (pv > maxV) maxV = pv;
                projected.Add(new Vector2(pu, pv));
            }

            if (worldPts.Count == 0) minU = maxU = minV = maxV = 0f;

            p.MinU = minU;
            p.MaxU = maxU;
            p.MinV = minV;
            p.MaxV = maxV;
            p.Outline = ConvexHull(projected);
            p.Shape = IsRectangle(p.Outline) ? "rect" : "poly";
            return p;
        }

        static ClipVert MakeClipVert(MeshAnalysis a, int vi)
        {
            return new ClipVert
            {
                Local = a.LocalVerts[vi],
                World = a.WorldVerts[vi],
                HasNormal = a.HasNormals,
                Normal = a.HasNormals ? a.Normals[vi] : Vector3.zero,
                HasUv0 = a.HasUv0,
                Uv0 = a.HasUv0 ? a.Uv0[vi] : Vector2.zero,
                HasTangent = a.HasTangents,
                Tangent = a.HasTangents ? a.Tangents[vi] : Vector4.zero,
            };
        }

        static float TriangleArea(Vector3 a, Vector3 b, Vector3 c)
        {
            return 0.5f * Vector3.Cross(b - a, c - a).magnitude;
        }

        /// <summary>Sutherland–Hodgman：把三角形裁到面板矩形 [s0,s1]×[t0,t1]。</summary>
        static List<ClipVert> ClipTriangle(ClipTri tri, Vector3 u, Vector3 v,
                                           float s0, float s1, float t0, float t1)
        {
            var poly = new List<ClipVert>(8) { tri.A, tri.B, tri.C };
            poly = ClipHalfPlane(poly, u, v, false, s0, true);
            poly = ClipHalfPlane(poly, u, v, false, s1, false);
            poly = ClipHalfPlane(poly, u, v, true, t0, true);
            poly = ClipHalfPlane(poly, u, v, true, t1, false);
            return DedupePoly(poly);
        }

        static List<ClipVert> ClipHalfPlane(List<ClipVert> poly, Vector3 u, Vector3 v,
                                            bool useT, float value, bool keepGreater)
        {
            if (poly == null || poly.Count == 0) return poly;

            const float eps = 1e-5f;
            Vector3 axis = useT ? v : u;
            var output = new List<ClipVert>(poly.Count + 2);

            for (int i = 0; i < poly.Count; i++)
            {
                var cur = poly[i];
                var next = poly[(i + 1) % poly.Count];
                float dc = DistanceToPlane(cur, axis, value, keepGreater);
                float dn = DistanceToPlane(next, axis, value, keepGreater);
                bool inCur = dc >= -eps;
                bool inNext = dn >= -eps;

                if (inCur) output.Add(cur);
                if (inCur != inNext)
                {
                    float denom = dc - dn;
                    float t = Mathf.Abs(denom) < 1e-9f ? 0f : Mathf.Clamp01(dc / denom);
                    output.Add(LerpClip(cur, next, t));
                }
            }
            return output;
        }

        static float DistanceToPlane(ClipVert p, Vector3 axis, float value, bool keepGreater)
        {
            float coord = Vector3.Dot(p.World, axis);
            return keepGreater ? coord - value : value - coord;
        }

        static ClipVert LerpClip(ClipVert a, ClipVert b, float t)
        {
            var r = new ClipVert
            {
                Local = Vector3.LerpUnclamped(a.Local, b.Local, t),
                World = Vector3.LerpUnclamped(a.World, b.World, t),
                HasNormal = a.HasNormal && b.HasNormal,
                HasUv0 = a.HasUv0 && b.HasUv0,
                HasTangent = a.HasTangent && b.HasTangent,
            };

            if (r.HasNormal)
            {
                r.Normal = Vector3.LerpUnclamped(a.Normal, b.Normal, t);
                if (r.Normal.sqrMagnitude > 1e-10f) r.Normal.Normalize();
            }

            if (r.HasUv0)
                r.Uv0 = Vector2.LerpUnclamped(a.Uv0, b.Uv0, t);

            if (r.HasTangent)
            {
                Vector4 tt = Vector4.LerpUnclamped(a.Tangent, b.Tangent, t);
                Vector3 xyz = new Vector3(tt.x, tt.y, tt.z);
                if (xyz.sqrMagnitude > 1e-10f) xyz.Normalize();
                r.Tangent = new Vector4(xyz.x, xyz.y, xyz.z, t < 0.5f ? a.Tangent.w : b.Tangent.w);
            }

            return r;
        }

        static List<ClipVert> DedupePoly(List<ClipVert> poly)
        {
            var result = new List<ClipVert>(poly.Count);
            foreach (var p in poly)
            {
                if (result.Count == 0 ||
                    (p.Local - result[result.Count - 1].Local).sqrMagnitude > 1e-12f)
                    result.Add(p);
            }
            if (result.Count > 1 &&
                (result[0].Local - result[result.Count - 1].Local).sqrMagnitude <= 1e-12f)
                result.RemoveAt(result.Count - 1);

            if (result.Count < 3) result.Clear();
            return result;
        }

        static PanelBuild BuildPanelMesh(MeshAnalysis a, PanelGeometry p, bool normalizedUv)
        {
            var b = new PanelBuild { Panel = p };
            var map = new Dictionary<VertKey, int>(Mathf.Max(3, p.Tris.Count));
            float w = p.MaxU - p.MinU;
            float h = p.MaxV - p.MinV;
            bool useNormals = a.HasNormals;
            bool useTangents = a.HasTangents && a.HasNormals;

            foreach (var tri in p.Tris)
            {
                AppendPanelVertex(a, p, b, map, tri.A, normalizedUv, w, h, useNormals, useTangents);
                AppendPanelVertex(a, p, b, map, tri.B, normalizedUv, w, h, useNormals, useTangents);
                AppendPanelVertex(a, p, b, map, tri.C, normalizedUv, w, h, useNormals, useTangents);
            }
            return b;
        }

        static void AppendPanelVertex(MeshAnalysis a, PanelGeometry p, PanelBuild b,
                                      Dictionary<VertKey, int> map, ClipVert cv,
                                      bool normalizedUv, float w, float h,
                                      bool useNormals, bool useTangents)
        {
            Vector3 normalForKey = cv.HasNormal ? cv.Normal : Vector3.zero;
            var key = new VertKey(cv.Local, normalForKey);
            if (map.TryGetValue(key, out int ni))
            {
                b.Indices.Add(ni);
                return;
            }

            ni = b.Vertices.Count;
            map[key] = ni;
            b.Vertices.Add(cv.Local);

            Vector3 localNormal = cv.HasNormal ? cv.Normal : Vector3.zero;
            if (useNormals)
            {
                if (localNormal.sqrMagnitude < 1e-10f)
                    localNormal = a.WorldToLocal.MultiplyVector(p.Normal);
                if (localNormal.sqrMagnitude < 1e-10f) localNormal = Vector3.up;
                localNormal.Normalize();
                b.Normals.Add(localNormal);
            }

            if (normalizedUv)
            {
                Vector3 rel = cv.World - p.Center;
                float pu = Vector3.Dot(rel, p.U);
                float pv = Vector3.Dot(rel, p.V);
                b.Uvs.Add(new Vector2(
                    w > 1e-6f ? Mathf.Clamp01((pu - p.MinU) / w) : 0.5f,
                    h > 1e-6f ? Mathf.Clamp01((pv - p.MinV) / h) : 0.5f));
            }
            else
            {
                b.Uvs.Add(cv.HasUv0 ? cv.Uv0 : Vector2.zero);
            }

            if (useTangents)
                b.Tangents.Add(CalcFaceTangent(a.WorldToLocal, localNormal, p.U, p.V, p.Normal));

            b.Indices.Add(ni);
        }

        // ---------------------------------------------------------------- Apply 组装

        class BuildResult
        {
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector2> Uvs = new List<Vector2>();
            public readonly List<Vector4> Tangents = new List<Vector4>();
            public readonly List<List<int>> Submeshes = new List<List<int>>();
            public readonly List<Material> Materials = new List<Material>();
            public readonly List<string> MaterialPaths = new List<string>();
            /// <summary>与 submesh 对齐的面板键；&lt;0 表示原材质 / 未匹配面板或残余几何。</summary>
            public readonly List<long> SubmeshPanelKeys = new List<long>();
            public bool NormalsValid = true;
            public bool TangentsValid = true;
        }

        class ParsedFace
        {
            public int GroupIndex;
            public int PanelIndex;
            public ManifestFace Face;
        }

        class ApplyStats
        {
            public int ManifestFaces;
            public int MatchedFaces;
            public int ChangedRenderers;
            public int ChangedObjects;
            public int MissingTextures;
            public int MissingFaces;
            public int MissingObjects;
            public int SkippedRenderers;
            public int VertexLimitSkips;
            public int MaterialFailures;
            public readonly HashSet<string> ChangedObjectPaths =
                new HashSet<string>(StringComparer.Ordinal);
            /// <summary>同一轮 Apply 内已处理过的贴图（小面变体大量复用同一张图，避免重复 reimport）。</summary>
            public readonly HashSet<string> ProcessedTextureKeys =
                new HashSet<string>(StringComparer.Ordinal);
        }

        static long PanelKey(int groupIndex, int panelIndex) =>
            ((long)groupIndex << 32) | (uint)panelIndex;

        static void ProcessRenderer(Renderer r, string objectPath, int rendererIndex,
                                    List<ManifestFace> faces, FaceSkinRestore restore,
                                    ApplyStats stats, StringBuilder report)
        {
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null)
            {
                stats.SkippedRenderers++;
                report.AppendLine($"    [跳过·非 MeshFilter] {objectPath} #r{rendererIndex}");
                return;
            }

            var current = mf.sharedMesh;
            if (current == null)
            {
                stats.SkippedRenderers++;
                report.AppendLine($"    [跳过·mesh 为空] {objectPath} #r{rendererIndex}");
                return;
            }

            RestoreEntry entry = FindRestoreEntry(restore, objectPath, rendererIndex);
            bool isNewEntry = entry == null;

            Mesh baseMesh;
            Material[] originalMats;

            if (!isNewEntry)
            {
                baseMesh = LoadSourceMesh(entry.originalMeshAssetPath, entry.originalMeshName);
                if (baseMesh == null)
                {
                    stats.SkippedRenderers++;
                    report.AppendLine($"    [跳过·找不到原 mesh] {objectPath} #r{rendererIndex} ← " +
                                      $"{entry.originalMeshAssetPath} / {entry.originalMeshName}");
                    return;
                }
                originalMats = LoadOriginalMaterials(entry);
                if (originalMats == null)
                {
                    originalMats = r.sharedMaterials;
                    report.AppendLine($"    [警告·原材质表缺失，暂用当前材质] {objectPath} #r{rendererIndex}");
                }
            }
            else
            {
                string currentPath = AssetDatabase.GetAssetPath(current);
                if (!string.IsNullOrEmpty(currentPath) &&
                    currentPath.StartsWith(MeshDir + "/", StringComparison.Ordinal))
                {
                    stats.SkippedRenderers++;
                    report.AppendLine($"    [跳过·生成件但无还原表] {objectPath} #r{rendererIndex}");
                    return;
                }
                if (HasAnyMaterialPath(r.sharedMaterials, MatDir + "/"))
                {
                    stats.SkippedRenderers++;
                    report.AppendLine($"    [跳过·生成材质但无还原表] {objectPath} #r{rendererIndex}");
                    return;
                }

                baseMesh = current;
                originalMats = r.sharedMaterials;

                entry = new RestoreEntry
                {
                    objectPath = objectPath,
                    rendererIndex = rendererIndex,
                    originalMeshAssetPath = AssetDatabase.GetAssetPath(baseMesh),
                    originalMeshName = baseMesh.name,
                    originalMaterialPaths = new List<string>(),
                    originalMaterialNames = new List<string>(),
                    generatedMaterialPaths = new List<string>(),
                };
                if (originalMats != null)
                {
                    foreach (var m in originalMats)
                    {
                        entry.originalMaterialPaths.Add(m != null ? AssetDatabase.GetAssetPath(m) : "");
                        entry.originalMaterialNames.Add(m != null ? m.name : "");
                    }
                }
            }

            if (baseMesh == null || !baseMesh.isReadable)
            {
                stats.SkippedRenderers++;
                report.AppendLine($"    [跳过·原 mesh 不可读] {objectPath} #r{rendererIndex} / {baseMesh?.name}");
                return;
            }

            MeshAnalysis analysis;
            try
            {
                analysis = AnalyzeMesh(baseMesh, r.transform.localToWorldMatrix, r.transform.worldToLocalMatrix);
            }
            catch (Exception e)
            {
                stats.SkippedRenderers++;
                report.AppendLine($"    [跳过·分析异常] {objectPath} #r{rendererIndex}：{e.Message}");
                return;
            }

            if (analysis == null || analysis.Groups.Count == 0)
            {
                stats.SkippedRenderers++;
                report.AppendLine($"    [跳过·无面组] {objectPath} #r{rendererIndex}");
                return;
            }

            // ---- manifest 面 -> (面组下标, 面板下标)；id 里的 #fNN / pNN 与 Export 完全一致
            var parsed = new List<ParsedFace>();
            foreach (var f in faces)
            {
                if (f == null) continue;
                if (!ParseFaceLocator(f.id, out int gi, out int pi))
                {
                    stats.MissingFaces++;
                    report.AppendLine($"    [缺失面·id 无法解析] {objectPath} #r{rendererIndex} / {f.id}");
                    continue;
                }
                if (gi < 0 || gi >= analysis.Groups.Count)
                {
                    stats.MissingFaces++;
                    report.AppendLine($"    [缺失面·面组越界] {objectPath} #r{rendererIndex} / {f.id}");
                    continue;
                }

                parsed.Add(new ParsedFace { GroupIndex = gi, PanelIndex = pi, Face = f });

                int expectedSub = analysis.Groups[gi].Submesh;
                if (f.submesh != expectedSub)
                {
                    report.AppendLine($"    [警告·submesh 不一致] {f.id} manifest={f.submesh} 实际={expectedSub}");
                }
            }

            if (parsed.Count == 0)
            {
                stats.SkippedRenderers++;
                report.AppendLine($"    [跳过·manifest 面全部无法对齐] {objectPath} #r{rendererIndex}");
                return;
            }

            // ---- 只对受影响的面组重算切板
            var groupsToPanel = new HashSet<int>();
            foreach (var pf in parsed) groupsToPanel.Add(pf.GroupIndex);

            var panelsByGroup = new Dictionary<int, List<PanelGeometry>>();
            foreach (int gi in groupsToPanel.OrderBy(x => x))
                panelsByGroup[gi] = BuildPanels(analysis, gi);

            var matchedPanels = new Dictionary<long, ManifestFace>();
            var matchedGroups = new HashSet<int>();
            foreach (var pf in parsed)
            {
                var panels = panelsByGroup[pf.GroupIndex];
                if (pf.PanelIndex < 0 || pf.PanelIndex >= panels.Count)
                {
                    stats.MissingFaces++;
                    report.AppendLine($"    [缺失面] {pf.Face.id}（面板 {pf.PanelIndex}/{panels.Count} 不存在）");
                    continue;
                }

                long key = PanelKey(pf.GroupIndex, pf.PanelIndex);
                if (matchedPanels.ContainsKey(key))
                    report.AppendLine($"    [重复面 id，后者覆盖] {pf.Face.id}");

                matchedPanels[key] = pf.Face;
                matchedGroups.Add(pf.GroupIndex);
            }

            if (matchedGroups.Count == 0)
            {
                stats.SkippedRenderers++;
                report.AppendLine($"    [跳过·manifest 面全部无法对齐] {objectPath} #r{rendererIndex}");
                return;
            }

            // ---- 组装（先原材质占位；顶点超限时还没创建任何材质资产）
            BuildResult build = BuildSplitMesh(analysis, panelsByGroup, matchedGroups, matchedPanels, originalMats);
            if (build == null || build.Submeshes.Count == 0 ||
                build.Vertices.Count != build.Uvs.Count)
            {
                stats.SkippedRenderers++;
                report.AppendLine($"    [跳过·组装失败] {objectPath} #r{rendererIndex}");
                return;
            }

            if (build.Vertices.Count > MaxVertices16)
            {
                stats.VertexLimitSkips++;
                stats.SkippedRenderers++;
                report.AppendLine($"    [跳过·顶点超限] {objectPath} #r{rendererIndex}：拆分后 " +
                                  $"{build.Vertices.Count} 顶点 > {MaxVertices16}");
                return;
            }

            // ---- 每块面板一份材质；未匹配面板 / 残余几何保留原材质
            var generatedMatPaths = new List<string>();
            int matchedFaceCount = 0;
            for (int i = 0; i < build.Submeshes.Count; i++)
            {
                long key = build.SubmeshPanelKeys[i];
                if (key < 0) continue;
                if (!matchedPanels.TryGetValue(key, out var face)) continue;

                matchedFaceCount++;
                var mat = EnsureFaceMaterial(face, stats);
                if (mat == null)
                {
                    stats.MaterialFailures++;
                    report.AppendLine($"    [材质失败·沿用原材质] {face.id}");
                    continue;
                }

                build.Materials[i] = mat;
                string mp = AssetDatabase.GetAssetPath(mat);
                build.MaterialPaths[i] = mp;
                if (!string.IsNullOrEmpty(mp)) generatedMatPaths.Add(mp);
            }

            // ---- mesh 资产（重复 Apply 复用同一路径）
            EnsureFolder(MeshDir);
            string stem = GeneratedMeshPrefix + SafeName(objectPath, 80) + "_r" +
                          rendererIndex.ToString(CultureInfo.InvariantCulture) + "_" +
                          Hash8(objectPath + "#r" + rendererIndex.ToString(CultureInfo.InvariantCulture));
            string meshPath = MeshDir + "/" + stem + ".asset";

            Mesh outMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (outMesh == null)
            {
                outMesh = UnityEngine.Object.Instantiate(baseMesh);
                outMesh.name = stem;
                AssetDatabase.CreateAsset(outMesh, meshPath);
            }

            ApplyBuildData(outMesh, build);
            // 压缩顶点/索引；submesh 顺序仍由上面的稳定构建顺序决定
            try { MeshUtility.Optimize(outMesh); }
            catch (Exception e) { report.AppendLine($"    [警告·MeshUtility.Optimize 失败] {e.Message}"); }
            EditorUtility.SetDirty(outMesh);

            // ---- 只改 mesh 与材质；Collider / Transform 一律不动
            mf.sharedMesh = outMesh;
            r.sharedMaterials = build.Materials.ToArray();

            if (isNewEntry) restore.renderers.Add(entry);
            entry.generatedMeshAssetPath = meshPath;
            if (entry.generatedMaterialPaths == null)
                entry.generatedMaterialPaths = new List<string>();
            foreach (var p in generatedMatPaths)
            {
                if (!string.IsNullOrEmpty(p) && !entry.generatedMaterialPaths.Contains(p))
                    entry.generatedMaterialPaths.Add(p);
            }

            stats.MatchedFaces += matchedFaceCount;
            stats.ChangedRenderers++;
            if (stats.ChangedObjectPaths.Add(objectPath)) stats.ChangedObjects++;
            report.AppendLine($"    {objectPath} #r{rendererIndex}: 面/面板 {matchedFaceCount} / " +
                              $"顶点 {build.Vertices.Count} / submesh {build.Submeshes.Count} → {meshPath}");
        }

        static BuildResult BuildSplitMesh(MeshAnalysis a,
                                          Dictionary<int, List<PanelGeometry>> panelsByGroup,
                                          HashSet<int> matchedGroups,
                                          Dictionary<long, ManifestFace> matchedPanels,
                                          Material[] originalMats)
        {
            var b = new BuildResult();
            bool useNormals = a.HasNormals;
            bool useTangents = a.HasTangents && a.HasNormals;

            // ---- 受影响的面组：所有面板（matched 用 0–1 UV，未 matched 用原 UV0）
            foreach (int gi in matchedGroups.OrderBy(x => x))
            {
                var g = a.Groups[gi];
                var originMat = GetMaterial(originalMats, g.Submesh);

                foreach (var p in panelsByGroup[gi])
                {
                    long key = PanelKey(gi, p.PanelIndex);
                    bool matched = matchedPanels.ContainsKey(key);
                    var pb = BuildPanelMesh(a, p, matched);
                    AppendSubmesh(b, pb.Vertices, pb.Normals, pb.Uvs, pb.Tangents, pb.Indices,
                                  originMat, matched ? key : -1L, useNormals, useTangents);
                }
            }

            // ---- 未受影响的面组：按原 submesh 原样保留（小面 / 退化面 / 其他 submesh）
            for (int s = 0; s < a.SubmeshCount; s++)
            {
                var verts = new List<Vector3>();
                var norms = new List<Vector3>();
                var uvs = new List<Vector2>();
                var tans = new List<Vector4>();
                var tris = new List<int>();
                var map = new Dictionary<int, int>();

                for (int t = 0; t < a.TriangleCount; t++)
                {
                    if (a.TriSub[t] != s) continue;
                    int gi = a.TriGroup[t];
                    if (gi >= 0 && matchedGroups.Contains(gi)) continue;
                    if (a.TriDegenerate[t]) continue;

                    for (int k = 0; k < 3; k++)
                    {
                        int vi = a.FlatTris[t * 3 + k];
                        if (!map.TryGetValue(vi, out int ni))
                        {
                            ni = verts.Count;
                            map[vi] = ni;

                            verts.Add(a.LocalVerts[vi]);
                            if (useNormals) norms.Add(a.Normals[vi]);
                            uvs.Add(a.HasUv0 ? a.Uv0[vi] : Vector2.zero);
                            if (useTangents) tans.Add(a.Tangents[vi]);
                        }
                        tris.Add(ni);
                    }
                }

                if (tris.Count == 0) continue;

                AppendSubmesh(b, verts, norms, uvs, tans, tris,
                              GetMaterial(originalMats, s), -1L, useNormals, useTangents);
            }

            return b;
        }

        /// <summary>把一个 submesh 追加进最终缓冲（索引整体偏移）。</summary>
        static void AppendSubmesh(BuildResult dst, List<Vector3> verts, List<Vector3> norms,
                                  List<Vector2> uvs, List<Vector4> tans, List<int> tris,
                                  Material material, long panelKey,
                                  bool useNormals, bool useTangents)
        {
            int offset = dst.Vertices.Count;
            dst.Vertices.AddRange(verts);

            if (useNormals && dst.NormalsValid && norms.Count == verts.Count)
            {
                dst.Normals.AddRange(norms);
            }
            else if (useNormals)
            {
                dst.NormalsValid = false;
                dst.Normals.Clear(); // 计数异常 -> 交给 RecalculateNormals
            }

            dst.Uvs.AddRange(uvs);

            if (useTangents && dst.TangentsValid && tans.Count == verts.Count)
            {
                dst.Tangents.AddRange(tans);
            }
            else if (useTangents)
            {
                dst.TangentsValid = false;
                dst.Tangents.Clear(); // 计数异常 -> 交给 RecalculateTangents
            }

            var idx = new List<int>(tris.Count);
            for (int i = 0; i < tris.Count; i++) idx.Add(tris[i] + offset);

            dst.Submeshes.Add(idx);
            dst.Materials.Add(material);
            dst.MaterialPaths.Add(material != null ? AssetDatabase.GetAssetPath(material) : "");
            dst.SubmeshPanelKeys.Add(panelKey);
        }

        /// <summary>
        /// 面内切线：把面的世界 u 轴变换到局部空间，正交化到顶点法线；
        /// w 取使 bitangent ≈ v 的符号。这样 normal map 的 +X 与贴图 u 方向一致。
        /// </summary>
        static Vector4 CalcFaceTangent(Matrix4x4 worldToLocal, Vector3 localNormal,
                                       Vector3 worldU, Vector3 worldV, Vector3 fallbackWorldNormal)
        {
            Vector3 n = localNormal;
            if (n.sqrMagnitude < 1e-10f)
                n = worldToLocal.MultiplyVector(fallbackWorldNormal);
            if (n.sqrMagnitude < 1e-10f) n = Vector3.up;
            n.Normalize();

            Vector3 lu = worldToLocal.MultiplyVector(worldU);
            Vector3 lv = worldToLocal.MultiplyVector(worldV);

            Vector3 t = lu - n * Vector3.Dot(lu, n);
            if (t.sqrMagnitude < 1e-10f)
                t = Vector3.Cross(n, lv);
            if (t.sqrMagnitude < 1e-10f)
                t = Vector3.right;
            t.Normalize();

            float sign = Vector3.Dot(Vector3.Cross(n, t), lv) >= 0f ? 1f : -1f;
            return new Vector4(t.x, t.y, t.z, sign);
        }

        static void ApplyBuildData(Mesh mesh, BuildResult b)
        {
            mesh.Clear();
            mesh.indexFormat = IndexFormat.UInt16;

            mesh.SetVertices(b.Vertices);
            bool hasNormals = b.NormalsValid && b.Normals.Count == b.Vertices.Count;
            if (hasNormals) mesh.SetNormals(b.Normals);

            mesh.SetUVs(0, b.Uvs);

            bool hasTangents = b.TangentsValid && b.Tangents.Count == b.Vertices.Count;
            if (hasTangents) mesh.SetTangents(b.Tangents);

            mesh.subMeshCount = b.Submeshes.Count;
            for (int i = 0; i < b.Submeshes.Count; i++)
                mesh.SetTriangles(b.Submeshes[i], i, false);

            if (!hasNormals) mesh.RecalculateNormals();
            if (!hasTangents) mesh.RecalculateTangents();
            mesh.RecalculateBounds();
        }

        // ---------------------------------------------------------------- 材质 / 贴图

        static Material EnsureFaceMaterial(ManifestFace face, ApplyStats stats)
        {
            if (face == null || string.IsNullOrEmpty(face.id)) return null;

            EnsureFolder(MatDir);
            string safeId = SafeFaceId(face.id);
            string matPath = MatDir + "/" + safeId + ".mat";

            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                if (AssetDatabase.LoadAssetAtPath<Material>(TemplateMat) == null)
                {
                    Debug.LogError($"[FaceSkin] 模板材质不存在：{TemplateMat}");
                    return null;
                }
                if (!AssetDatabase.CopyAsset(TemplateMat, matPath))
                {
                    Debug.LogError($"[FaceSkin] CopyAsset 失败：{TemplateMat} -> {matPath}");
                    return null;
                }
                AssetDatabase.ImportAsset(matPath);
                mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null) return null;
            }

            var tex = face.textures ?? new ManifestTextures();

            // BC / E 走 sRGB；N 是法线贴图；MS / AO 线性
            var bc = LoadFaceTexture(tex.BC, safeId, "BC", false, false, stats);
            var nm = LoadFaceTexture(tex.N, safeId, "N", true, true, stats);
            var ms = LoadFaceTexture(tex.MS, safeId, "MS", false, true, stats);
            var ao = LoadFaceTexture(tex.AO, safeId, "AO", false, true, stats);
            var em = LoadFaceTexture(tex.E, safeId, "E", false, false, stats);

            // 先清空模板自带贴图，避免复用材质时残留上一轮内容
            mat.SetTexture("_BaseMap", bc);
            mat.SetTexture("_BumpMap", nm);
            mat.SetTexture("_MetallicGlossMap", ms);
            mat.SetTexture("_OcclusionMap", ao);
            mat.SetTexture("_EmissionMap", em);

            mat.SetFloat("_BumpScale", 1f);
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Metallic", 1f);
            mat.SetFloat("_Smoothness", 1f);
            if (mat.HasProperty("_SmoothnessTextureChannel"))
                mat.SetFloat("_SmoothnessTextureChannel", 0f); // 0 = Metallic Alpha

            // 缺 E 图时不能让整面白色自发光，故只在有 E 图时启用发光
            if (em != null)
            {
                mat.SetColor("_EmissionColor", Color.white);
                mat.EnableKeyword("_EMISSION");
            }
            else
            {
                mat.SetColor("_EmissionColor", Color.black);
                mat.DisableKeyword("_EMISSION");
            }

            mat.EnableKeyword("_NORMALMAP");
            mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            mat.EnableKeyword("_OCCLUSIONMAP");

            SetTextureTiling(mat, "_BaseMap");
            SetTextureTiling(mat, "_BumpMap");
            SetTextureTiling(mat, "_MetallicGlossMap");
            SetTextureTiling(mat, "_OcclusionMap");
            SetTextureTiling(mat, "_EmissionMap");

            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void SetTextureTiling(Material mat, string property)
        {
            mat.SetTextureScale(property, new Vector2(1f, 1f));
            mat.SetTextureOffset(property, Vector2.zero);
        }

        static Texture2D LoadFaceTexture(string path, string faceSafe, string slot,
                                         bool normalMap, bool linear, ApplyStats stats)
        {
            string abs = ResolveTexturePath(path);
            if (string.IsNullOrEmpty(abs))
            {
                stats.MissingTextures++;
                return null;
            }

            string rel = ToAssetsRelative(abs);
            bool copied = false;
            if (string.IsNullOrEmpty(rel))
            {
                // 生成器写在 Assets 外：复制进工程后再导入
                EnsureFolder(TexDir);
                string faceDir = TexDir + "/" + faceSafe;
                EnsureFolder(faceDir);

                string ext = Path.GetExtension(abs);
                if (string.IsNullOrEmpty(ext)) ext = ".png";
                string dstAsset = faceDir + "/" + slot + ext;
                string dstAbs = Path.GetFullPath(Path.Combine(ProjectRoot, dstAsset));

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dstAbs));
                    File.Copy(abs, dstAbs, true);
                    AssetDatabase.ImportAsset(dstAsset, ImportAssetOptions.ForceUpdate);
                    rel = dstAsset;
                    copied = true;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[FaceSkin] 复制贴图失败：{abs} -> {dstAsset}：{e.Message}");
                    stats.MissingTextures++;
                    return null;
                }
            }

            // 小面 8 变体会反复引用同一张贴图：同一轮只导入/配置一次
            string cacheKey = rel + "|" + (normalMap ? "N" : "D") + (linear ? "L" : "S");
            if (stats.ProcessedTextureKeys.Contains(cacheKey))
                return AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
            stats.ProcessedTextureKeys.Add(cacheKey);

            if (!copied)
                AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceUpdate);

            ConfigureTextureImporter(rel, normalMap, linear, copied);

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
            if (tex == null)
            {
                stats.MissingTextures++;
                return null;
            }
            return tex;
        }

        static void ConfigureTextureImporter(string rel, bool normalMap, bool linear, bool forceImport)
        {
            var ti = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (ti == null && forceImport)
            {
                AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceUpdate);
                ti = AssetImporter.GetAtPath(rel) as TextureImporter;
            }
            if (ti == null) return;

            var wantedType = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
            bool sRGB = !linear;

            bool changed =
                ti.textureType != wantedType ||
                ti.sRGBTexture != sRGB ||
                ti.wrapMode != TextureWrapMode.Clamp ||
                ti.maxTextureSize != 4096 ||
                !ti.mipmapEnabled;

            ti.textureType = wantedType;
            ti.sRGBTexture = sRGB;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.maxTextureSize = 4096;
            ti.mipmapEnabled = true;

            if (changed || forceImport) ti.SaveAndReimport();
        }

        static string ResolveTexturePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path.Replace('\\', '/');

            try
            {
                if (File.Exists(p)) return Path.GetFullPath(p);
            }
            catch { /* 非法路径继续尝试其他解析 */ }

            if (!Path.IsPathRooted(p))
            {
                string c1 = Path.GetFullPath(Path.Combine(ProjectRoot, p));
                if (File.Exists(c1)) return c1;

                string c2 = Path.GetFullPath(Path.Combine(AuditDir, p));
                if (File.Exists(c2)) return c2;

                string c3 = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), p));
                if (File.Exists(c3)) return c3;
            }

            return null;
        }

        static string ToAssetsRelative(string abs)
        {
            if (string.IsNullOrEmpty(abs)) return null;
            string p = abs.Replace('\\', '/');

            if (p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return p;

            string root = ProjectRoot + "/Assets/";
            if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return p.Substring(ProjectRoot.Length + 1);

            return null;
        }

        // ---------------------------------------------------------------- 还原表 / spec 读取

        [Serializable]
        public class FaceSpec
        {
            public int version;
            public List<FaceSpecFace> faces = new List<FaceSpecFace>();
        }

        [Serializable]
        public class FaceSpecFace
        {
            public string id;
            public string objectPath;
            public int rendererIndex;
            public int submesh;
        }

        [Serializable]
        public class FaceSkinManifest
        {
            public List<ManifestFace> faces = new List<ManifestFace>();
        }

        [Serializable]
        public class ManifestFace
        {
            public string id;
            public string objectPath;
            public int submesh;
            public ManifestTextures textures;
            public float mmPx;
        }

        [Serializable]
        public class ManifestTextures
        {
            public string BC;
            public string N;
            public string MS;
            public string AO;
            public string E;
        }

        [Serializable]
        public class FaceSkinRestore
        {
            public int version = 1;
            public List<RestoreEntry> renderers = new List<RestoreEntry>();
        }

        [Serializable]
        public class RestoreEntry
        {
            public string objectPath;
            public int rendererIndex;
            public string originalMeshAssetPath;
            public string originalMeshName;
            public List<string> originalMaterialPaths = new List<string>();
            public List<string> originalMaterialNames = new List<string>();
            public string generatedMeshAssetPath;
            public List<string> generatedMaterialPaths = new List<string>();
        }

        static FaceSkinRestore LoadRestore()
        {
            if (!File.Exists(RestorePath)) return null;
            try
            {
                var r = JsonUtility.FromJson<FaceSkinRestore>(File.ReadAllText(RestorePath));
                if (r != null && r.renderers == null) r.renderers = new List<RestoreEntry>();
                return r;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FaceSkin] 还原表解析失败：{e.Message}");
                return null;
            }
        }

        static void SaveRestore(FaceSkinRestore restore)
        {
            if (restore == null) return;
            EnsureAuditDir();
            File.WriteAllText(RestorePath, JsonUtility.ToJson(restore, true), new UTF8Encoding(false));
        }

        static RestoreEntry FindRestoreEntry(FaceSkinRestore restore, string objectPath, int rendererIndex)
        {
            if (restore == null || restore.renderers == null) return null;
            foreach (var e in restore.renderers)
            {
                if (e == null) continue;
                if (e.rendererIndex == rendererIndex &&
                    string.Equals(e.objectPath, objectPath, StringComparison.Ordinal))
                    return e;
            }
            return null;
        }

        static Dictionary<string, int> LoadSpecRendererMap()
        {
            if (!File.Exists(SpecPath)) return null;
            try
            {
                var spec = JsonUtility.FromJson<FaceSpec>(File.ReadAllText(SpecPath));
                if (spec == null || spec.faces == null) return null;
                var map = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var f in spec.faces)
                {
                    if (f == null || string.IsNullOrEmpty(f.id)) continue;
                    map[f.id] = f.rendererIndex;
                }
                return map;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FaceSkin] spec 解析失败（将退回按 id 解析 rendererIndex）：{e.Message}");
                return null;
            }
        }

        /// <summary>解析 id：#fNN 或 #fNNpMM；p 后缀只在切板面出现。</summary>
        static bool ParseFaceLocator(string id, out int groupIndex, out int panelIndex)
        {
            groupIndex = -1;
            panelIndex = 0;
            if (string.IsNullOrEmpty(id)) return false;

            int fPos = id.LastIndexOf("#f", StringComparison.Ordinal);
            if (fPos < 0) return false;

            int i = fPos + 2;
            int start = i;
            while (i < id.Length && char.IsDigit(id[i])) i++;
            if (i == start) return false;
            if (!int.TryParse(id.Substring(start, i - start), NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out groupIndex))
                return false;

            if (i < id.Length && id[i] == 'p')
            {
                int ps = i + 1;
                int pe = ps;
                while (pe < id.Length && char.IsDigit(id[pe])) pe++;
                if (pe > ps &&
                    !int.TryParse(id.Substring(ps, pe - ps), NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out panelIndex))
                    panelIndex = 0;
            }
            return true;
        }

        static int ParseRendererIndex(string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            int fPos = id.LastIndexOf("#f", StringComparison.Ordinal);
            if (fPos < 0) return 0;

            int rPos = id.LastIndexOf("#r", Mathf.Max(0, fPos - 1), StringComparison.Ordinal);
            if (rPos < 0) return 0;

            int end = fPos;
            string tail = id.Substring(rPos + 2, end - rPos - 2);
            return int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ri)
                ? ri
                : 0;
        }

        // ---------------------------------------------------------------- 资产装载

        static Mesh LoadSourceMesh(string assetPath, string meshName)
        {
            if (!string.IsNullOrEmpty(assetPath))
            {
                var all = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                if (all != null)
                {
                    foreach (var o in all)
                    {
                        if (o is Mesh m && (string.IsNullOrEmpty(meshName) || m.name == meshName))
                            return m;
                    }
                }
                var direct = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                if (direct != null) return direct;
            }

            if (!string.IsNullOrEmpty(meshName))
            {
                // Unity 内置图元（无资产路径）
                try
                {
                    var builtin = Resources.GetBuiltinResource<Mesh>(meshName + ".fbx");
                    if (builtin != null) return builtin;
                }
                catch { /* 不是内置名，继续按名字找资产 */ }

                foreach (var guid in AssetDatabase.FindAssets(meshName + " t:Mesh"))
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    var all = AssetDatabase.LoadAllAssetsAtPath(p);
                    if (all == null) continue;
                    foreach (var o in all)
                    {
                        if (o is Mesh m && m.name == meshName) return m;
                    }
                }
            }

            return null;
        }

        static Material[] LoadOriginalMaterials(RestoreEntry e)
        {
            if (e == null) return null;

            int n = 0;
            if (e.originalMaterialPaths != null) n = Mathf.Max(n, e.originalMaterialPaths.Count);
            if (e.originalMaterialNames != null) n = Mathf.Max(n, e.originalMaterialNames.Count);
            if (n == 0) return null;

            var mats = new Material[n];
            for (int i = 0; i < n; i++)
            {
                string path = e.originalMaterialPaths != null && i < e.originalMaterialPaths.Count
                    ? e.originalMaterialPaths[i] : "";
                string name = e.originalMaterialNames != null && i < e.originalMaterialNames.Count
                    ? e.originalMaterialNames[i] : "";

                Material m = null;
                if (!string.IsNullOrEmpty(path))
                    m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null && !string.IsNullOrEmpty(name))
                    m = LoadMaterialByName(name);
                mats[i] = m;
            }
            return mats;
        }

        static Material LoadMaterialByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            if (name == "Default-Material")
                return AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat");

            foreach (var guid in AssetDatabase.FindAssets(name + " t:Material"))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var m = AssetDatabase.LoadAssetAtPath<Material>(p);
                if (m != null && m.name == name) return m;
            }
            return null;
        }

        // ---------------------------------------------------------------- 场景 / 路径

        static bool EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return true;

            string abs = Path.Combine(ProjectRoot, ScenePath);
            if (!File.Exists(abs))
            {
                Debug.LogError($"[FaceSkin] 场景不存在：{abs}");
                return false;
            }

            if (cur.IsValid() && cur.isDirty && !string.IsNullOrEmpty(cur.path))
                EditorSceneManager.SaveScene(cur);

            var opened = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log($"[FaceSkin] 已打开场景 {ScenePath}");
            return opened.IsValid();
        }

        static void SaveScene()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid()) return;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.Split('/');
            if (parts.Length == 0) return null;

            GameObject current = null;
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root != null && root.name == parts[0])
                {
                    current = root;
                    break;
                }
            }
            if (current == null) return null;

            for (int i = 1; i < parts.Length; i++)
            {
                var t = current.transform.Find(parts[i]);
                if (t == null) return null;
                current = t.gameObject;
            }
            return current;
        }

        static string ObjectPath(Transform t)
        {
            var parts = new List<string>();
            while (t != null)
            {
                parts.Add(t.name);
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ---------------------------------------------------------------- 杂项

        static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');

        static void EnsureAuditDir()
        {
            Directory.CreateDirectory(AuditDir);
        }

        static void EnsureFolder(string assetFolder)
        {
            if (string.IsNullOrEmpty(assetFolder)) return;
            assetFolder = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(assetFolder)) return;

            int idx = assetFolder.LastIndexOf('/');
            string parent = idx > 0 ? assetFolder.Substring(0, idx) : "Assets";
            string name = idx >= 0 ? assetFolder.Substring(idx + 1) : assetFolder;

            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            if (!AssetDatabase.IsValidFolder(assetFolder))
                AssetDatabase.CreateFolder(parent, name);
        }

        static Material GetMaterial(Material[] mats, int index)
        {
            if (mats == null || index < 0 || index >= mats.Length) return null;
            return mats[index];
        }

        static bool StartsWithAny(string s, string[] prefixes)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var p in prefixes)
                if (s.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        static bool HasAnyMaterialName(Material[] mats, string[] keywords)
        {
            if (mats == null) return false;
            foreach (var m in mats)
            {
                if (m == null) continue;
                foreach (var k in keywords)
                {
                    if (m.name != null && m.name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            return false;
        }

        static bool HasAnyMaterialNamePrefix(Material[] mats, string[] prefixes)
        {
            if (mats == null) return false;
            foreach (var m in mats)
            {
                if (m == null || string.IsNullOrEmpty(m.name)) continue;
                foreach (var p in prefixes)
                {
                    if (m.name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        static bool HasAnyMaterialPath(Material[] mats, string prefix)
        {
            if (mats == null) return false;
            foreach (var m in mats)
            {
                if (m == null) continue;
                string p = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(p) && p.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        static bool Contains(string s, string token) =>
            !string.IsNullOrEmpty(s) && s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        static void Count(Dictionary<string, int> dict, string key)
        {
            dict.TryGetValue(key, out int c);
            dict[key] = c + 1;
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "0";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }

        static string Fmt4(float v) => v.ToString("F4", CultureInfo.InvariantCulture);

        static string V(Vector3 v) =>
            "[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]";

        static string SafeFaceId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "face";
            var sb = new StringBuilder(id.Length + 4);
            foreach (char c in id)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                    sb.Append(c);
                else if (c == '/')
                    sb.Append("__");
                else
                    sb.Append('_');
            }
            string s = sb.ToString();
            if (s.Length > 120) s = s.Substring(0, 120) + "_" + Hash8(id);
            return string.IsNullOrEmpty(s) ? "face" : s;
        }

        static string SafeName(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            if (sb.Length > maxLen) sb.Length = maxLen;
            return sb.Length == 0 ? "unnamed" : sb.ToString();
        }

        static string Hash8(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (char c in s ?? "")
                {
                    h ^= c;
                    h *= 16777619u;
                }
                return h.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        static uint Fnv1a(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (char c in s ?? "")
                {
                    h ^= c;
                    h *= 16777619u;
                }
                return h;
            }
        }
    }
}
