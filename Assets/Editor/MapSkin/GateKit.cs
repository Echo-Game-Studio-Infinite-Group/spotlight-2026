// =====================================================================================
// 七图统一出入口套件（公共系统，不按关卡拆）
//
// 用户口径：
//   · 全部地图的出入口美术风格必须一模一样：门框、双扇门板、门头图案、配色、编号牌、状态灯统一。
//   · Entrance = 暖橙 + 向上箭头 + "IN"；Exit = 青 + 向下箭头 + "OUT"。
//     两套几何与图案完全一致，只换颜色 / 箭头方向 / 字。
//   · 不许让人把出口入口弄反：色（橙 vs 青）、箭头（上 vs 下）、字（IN vs OUT）三重区分。
//   · 位置从每个 BOX 的最大可行走面边界自动推断；推断失败就跳过并在报告里列出来。
//
// 做法：
//   · 一套函数、七关调用；生成物只挂到 `DEC_Shared/Gate/<block>`（公共容器，不隶属分关）。
//   · 图案全部用几何：三条斜杠组成的箭头、圆环、短横线、七段数码管数字、笔画字母。
//     不依赖字体贴图；同一图案在 7 张图里引用同一份 mesh 资产（逐字节相同）。
//   · 门套件只用 MapSkinPalette 的共享材质（MS_{Semantic}_{Variant}），不再按关卡建材质。
//   · 不写 prefab：只生成场景对象 + mesh 资产；不调用 SaveAsPrefabAsset。
//
// API：
//   GateKit.Place(string block, Vector3 pos, float yaw, GateType type, float width, float height)
//
// 菜单：MapSkin/门套件/① 干跑  ② 生成 7 关各一对  ③ 清除
// 批处理：
//   -executeMethod MapSkin.GateKit.DryRun
//   -executeMethod MapSkin.GateKit.Run
//   -executeMethod MapSkin.GateKit.Clear
//
// 输出：`<workspace>/_session_audit/GATE_Manifest.json`（每关门位置、朝向、类型、尺寸、图案哈希）
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
using UnityEngine.Rendering;

namespace MapSkin
{
    public enum GateType
    {
        Entrance = 0,
        Exit = 1,
    }

    /// <summary>
    /// 七图公共出入口套件。Place 是唯一造型入口；Run 只负责在七个 BOX 上找位置并调用 Place。
    /// </summary>
    public static class GateKit
    {
        // ------------------------------------------------------------------ 常量

        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string SharedRootName = "DEC_Shared";
        const string GateBucketName = "Gate";
        const string MeshDir = "Assets/Art/EnvSkins/Shared/Gate";
        const string ManifestFile = "GATE_Manifest.json";
        const string ManifestDryFile = "GATE_Manifest.dryrun.json";

        const float DefaultWidth = 4.0f;
        const float DefaultHeight = 3.2f;
        const int KitVersion = 1;

        static readonly string[] Blocks = { "S_01", "S_02", "S_03", "S_04", "S_05", "S_06", "S_07" };

        // 同一图案在七图里共用同一份 mesh：key = 形状名（不含关卡）
        static readonly Dictionary<string, Mesh> MeshCache = new Dictionary<string, Mesh>(StringComparer.Ordinal);

        // ------------------------------------------------------------------ 菜单 / 批处理

        [MenuItem("MapSkin/门套件/① 干跑：推断 7 关出入口位置（零写盘）")]
        public static void DryRunMenu() => DryRun();

        [MenuItem("MapSkin/门套件/② 生成：7 关各一对 Entrance/Exit")]
        public static void RunMenu() => Run();

        [MenuItem("MapSkin/门套件/③ 清除：DEC_Shared/Gate/*")]
        public static void ClearMenu() => Clear();

        /// <summary>只推断位置与图案哈希，不建对象、不写 mesh / 场景。批处理：-executeMethod MapSkin.GateKit.DryRun</summary>
        public static void DryRun()
        {
            RunCore(dry: true);
        }

        /// <summary>七关各放一对门并保存场景。批处理：-executeMethod MapSkin.GateKit.Run</summary>
        public static void Run()
        {
            RunCore(dry: false);
        }

        /// <summary>删除 DEC_Shared/Gate 下的全部门与区块容器；共享 mesh 资产保留复用。</summary>
        public static void Clear()
        {
            if (!EnsureScene()) return;
            MeshCache.Clear();
            var scene = EditorSceneManager.GetActiveScene();

            var shared = FindRoot(SharedRootName);
            if (shared != null)
            {
                var gate = shared.transform.Find(GateBucketName);
                if (gate != null)
                {
                    int n = gate.childCount;
                    UnityEngine.Object.DestroyImmediate(gate.gameObject);
                    Debug.Log($"[GateKit·清除] 删除 {SharedRootName}/{GateBucketName}（{n} 个区块容器）。");
                }
                if (shared.transform.childCount == 0) UnityEngine.Object.DestroyImmediate(shared.gameObject);
            }
            else
            {
                Debug.Log("[GateKit·清除] 场景里没有 DEC_Shared，无需清除。");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        // ------------------------------------------------------------------ 主流程

        static void RunCore(bool dry)
        {
            if (!EnsureScene()) return;

            MapSkinPalette.ReloadPalette();
            MeshCache.Clear();

            var scene = EditorSceneManager.GetActiveScene();
            var man = new GateManifest
            {
                kitVersion = KitVersion,
                scene = ScenePath,
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                dryRun = dry,
                gates = new List<GateRec>(),
                skipped = new List<SkipRec>(),
                patternHashes = new List<PatternHash>(),
            };

            var rep = new StringBuilder();
            rep.AppendLine($"[GateKit·{(dry ? "干跑" : "生成")}] scene={ScenePath} 版本={KitVersion}");
            var dimSeen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var block in Blocks)
            {
                var box = FindBlockRoot(block);
                if (box == null)
                {
                    Skip(man, rep, block, "找不到 BOX_S_0x 根节点");
                    continue;
                }

                if (!TryInferSurface(box, out var surface, out var blockBounds, out var why))
                {
                    Skip(man, rep, block, why);
                    continue;
                }

                var pl = ComputePlacement(surface, blockBounds, block);
                string dimKey = string.Format(CultureInfo.InvariantCulture, "{0:F2}x{1:F2}", pl.width, pl.height);

                if (dry)
                {
                    man.gates.Add(Record(block, GateType.Entrance, pl.entrancePos, pl.yaw, pl.width, pl.height, surface));
                    man.gates.Add(Record(block, GateType.Exit, pl.exitPos, pl.yaw, pl.width, pl.height, surface));
                    rep.AppendLine($"{block}: 面 {surface.source} topY={surface.topY:F2} area={surface.area:F1}m² " +
                                   $"-> Entrance {V(pl.entrancePos)} / Exit {V(pl.exitPos)} yaw={pl.yaw:F0} " +
                                   $"size={pl.width:F2}x{pl.height:F2}");
                }
                else
                {
                    var e = Place(block, pl.entrancePos, pl.yaw, GateType.Entrance, pl.width, pl.height);
                    var x = Place(block, pl.exitPos, pl.yaw, GateType.Exit, pl.width, pl.height);
                    if (e != null) man.gates.Add(Record(block, GateType.Entrance, e.transform.position, pl.yaw, pl.width, pl.height, surface));
                    else Skip(man, rep, block, "Entrance 放置失败（容器/材质/场景不可用）");
                    if (x != null) man.gates.Add(Record(block, GateType.Exit, x.transform.position, pl.yaw, pl.width, pl.height, surface));
                    else Skip(man, rep, block, "Exit 放置失败（容器/材质/场景不可用）");
                    rep.AppendLine($"{block}: 面 {surface.source} topY={surface.topY:F2} area={surface.area:F1}m² " +
                                   $"-> Entrance {V(pl.entrancePos)} / Exit {V(pl.exitPos)} yaw={pl.yaw:F0} " +
                                   $"size={pl.width:F2}x{pl.height:F2}");
                }

                if (dimSeen.Add(dimKey))
                {
                    float leafW, leafH;
                    LeafSize(pl.width, pl.height, out leafW, out leafH);
                    AddHash(man.patternHashes, $"Frame_{dimKey}",
                        GetMesh($"Frame_{dimKey}", () => BuildFrameMesh(pl.width, pl.height), save: !dry));
                    AddHash(man.patternHashes, $"Leaf_{dimKey}",
                        GetMesh($"Leaf_{dimKey}", () => BuildLeafMesh(leafW, leafH), save: !dry));
                }
            }

            // 固定图案：箭头、圆环、字母、数字、状态灯。七关共用同一份 mesh → 哈希相同。
            AddHash(man.patternHashes, "Pattern_Chevron",
                GetMesh("Pattern_Chevron", BuildChevronMesh, save: !dry));
            AddHash(man.patternHashes, "Pattern_FloorArrow",
                GetMesh("Pattern_FloorArrow", BuildFloorArrowMesh, save: !dry));
            AddHash(man.patternHashes, "Pattern_Ring",
                GetMesh("Pattern_Ring", BuildRingMesh, save: !dry));
            AddHash(man.patternHashes, "Glyph_IN",
                GetMesh("Glyph_IN", () => BuildGlyphMesh("IN"), save: !dry));
            AddHash(man.patternHashes, "Glyph_OUT",
                GetMesh("Glyph_OUT", () => BuildGlyphMesh("OUT"), save: !dry));
            AddHash(man.patternHashes, "UnitBox",
                GetMesh("UnitBox", BuildUnitBoxMesh, save: !dry));
            AddHash(man.patternHashes, "UnitSphere",
                GetMesh("UnitSphere", BuildUnitSphereMesh, save: !dry));
            for (int i = 1; i <= 7; i++)
            {
                int idx = i;
                AddHash(man.patternHashes, $"Digits_{idx:00}",
                    GetMesh($"Digits_{idx:00}", () => BuildDigitsMesh(idx), save: !dry));
            }

            if (!dry)
            {
                AssetDatabase.SaveAssets();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            rep.AppendLine();
            rep.AppendLine($"[GateKit] 门 {man.gates.Count} 个，跳过 {man.skipped.Count} 个；图案 {man.patternHashes.Count} 项。");
            foreach (var s in man.skipped) rep.AppendLine($"    [跳过] {s.block}: {s.reason}");
            WriteManifest(man, dry ? ManifestDryFile : ManifestFile);
            Debug.Log(rep.ToString());
        }

        static void Skip(GateManifest man, StringBuilder rep, string block, string why)
        {
            man.skipped.Add(new SkipRec { block = block, reason = why });
            rep.AppendLine($"{block}: 跳过——{why}");
        }

        // ------------------------------------------------------------------ 场景 / 区块定位

        static bool EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return true;

            if (cur.IsValid() && cur.isDirty && !string.IsNullOrEmpty(cur.path))
            {
                Debug.LogWarning("[GateKit] 当前场景有未保存改动，不替你切换场景；请先保存后重跑。");
                return false;
            }

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log($"[GateKit] 已打开 {ScenePath}");
            return true;
        }

        static GameObject FindRoot(string name)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid()) return null;
            foreach (var root in scene.GetRootGameObjects())
                if (root != null && root.name == name) return root;
            return null;
        }

        /// <summary>找区块根：BOX_S_01 / BOX_S01 / S_01 / S_1 都认；优先子渲染器最多的那个。</summary>
        static GameObject FindBlockRoot(string block)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid()) return null;

            var m = Regex.Match(block ?? string.Empty, @"([1-7])");
            if (!m.Success) return null;
            int idx = m.Groups[1].Value[0] - '0';
            var re = new Regex($@"^(BOX[_\s-]?)?S[_\s-]?0?{idx}$", RegexOptions.IgnoreCase);

            GameObject best = null;
            int bestCount = -1;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null) continue;
                    if (!re.IsMatch(t.name)) continue;
                    if (IsUnderShared(t)) continue;                 // 排除 DEC_Shared/Gate/S_01 这类自建容器
                    int count = t.GetComponentsInChildren<Renderer>(true).Length;
                    if (count > bestCount) { bestCount = count; best = t.gameObject; }
                }
            }
            return best;
        }

        static bool IsUnderShared(Transform t)
        {
            for (var cur = t; cur != null; cur = cur.parent)
                if (cur.name == SharedRootName) return true;
            return false;
        }

        // ------------------------------------------------------------------ 可行走面推断

        sealed class SurfaceInfo
        {
            public Bounds bounds;
            public float topY;
            public float area;
            public string source;
        }

        /// <summary>
        /// 从 BOX 子树里找「最大可行走面」：
        ///   1) 只看地编原始几何，排除 DEC_/MAPSKIN_/GATE_/ENEMY_/HST_ 生成件与灯光；
        ///   2) 候选 = 水平尺寸够大、长宽比不夸张的盒状件顶面；
        ///   3) 同高度 + XZ 相邻的拼板合并成一块，避免拿单块地砖当整层地板；
        ///   4) 在区块高度下 65% 内选总面积最大的一块，同面积取更低的一层（多楼层关卡的主入口多在底层）。
        /// </summary>
        static bool TryInferSurface(GameObject box, out SurfaceInfo info, out Bounds blockBounds, out string why)
        {
            info = null;
            blockBounds = default;
            why = null;

            var rs = box.GetComponentsInChildren<Renderer>(true);
            if (rs == null || rs.Length == 0) { why = "BOX 下没有 Renderer"; return false; }

            bool hasBlock = false;
            var cands = new List<SurfaceInfo>();

            foreach (var r in rs)
            {
                if (r == null) continue;
                if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer) continue;
                if (!IsOriginalGeometry(r.transform, box.transform)) continue;

                var b = r.bounds;
                if (b.size.x <= 0.01f || b.size.z <= 0.01f) continue;

                if (!hasBlock) { blockBounds = b; hasBlock = true; }
                else blockBounds.Encapsulate(b);

                if (b.size.y < 0.05f) continue;                       // 贴片 / 地面标记
                float topArea = b.size.x * b.size.z;
                float shortSide = Mathf.Min(b.size.x, b.size.z);
                if (topArea < 1.5f || shortSide < 0.6f) continue;     // 小件 / 细梁
                float aspect = Mathf.Max(b.size.x, b.size.z) / Mathf.Max(0.01f, shortSide);
                if (aspect > 12f) continue;                           // 墙 / 梁 / 长条

                cands.Add(new SurfaceInfo
                {
                    bounds = b,
                    topY = b.max.y,
                    area = topArea,
                    source = r.name,
                });
            }

            if (!hasBlock) { why = "没有可用于推断边界的地编原始几何"; return false; }
            if (cands.Count == 0) { why = "没有满足面积/长宽比条件的水平面"; return false; }

            // ---- 先按顶面高度聚簇（容差 15cm）
            cands.Sort((a, b) => a.topY.CompareTo(b.topY));
            var byHeight = new List<List<SurfaceInfo>>();
            foreach (var c in cands)
            {
                bool added = false;
                foreach (var g in byHeight)
                {
                    if (Mathf.Abs(g[0].topY - c.topY) <= 0.15f) { g.Add(c); added = true; break; }
                }
                if (!added) byHeight.Add(new List<SurfaceInfo> { c });
            }

            // ---- 同高度内再按 XZ 相邻合并（拼板地板）
            var scored = new List<SurfaceInfo>();
            foreach (var g in byHeight)
            {
                var clusters = new List<List<SurfaceInfo>>();
                foreach (var c in g)
                {
                    var hits = clusters.Where(cl => cl.Any(x => XZNear(x.bounds, c.bounds, 0.60f))).ToList();
                    if (hits.Count == 0)
                    {
                        clusters.Add(new List<SurfaceInfo> { c });
                    }
                    else
                    {
                        var merged = new List<SurfaceInfo> { c };
                        foreach (var h in hits) { merged.AddRange(h); clusters.Remove(h); }
                        clusters.Add(merged);
                    }
                }

                foreach (var cl in clusters)
                {
                    var u = cl[0].bounds;
                    float totalArea = 0f;
                    float top = float.MinValue;
                    foreach (var x in cl)
                    {
                        u.Encapsulate(x.bounds);
                        totalArea += x.area;
                        if (x.topY > top) top = x.topY;
                    }
                    if (u.size.x * u.size.z < 2.0f) continue;
                    scored.Add(new SurfaceInfo
                    {
                        bounds = u,
                        topY = top,
                        area = totalArea,
                        source = cl.Count > 1 ? $"{cl.Count} 块拼板" : cl[0].source,
                    });
                }
            }

            if (scored.Count == 0) { why = "聚簇后没有足够大的水平面"; return false; }

            float maxArea = scored.Max(s => s.area);
            float lowLimit = blockBounds.min.y + blockBounds.size.y * 0.65f;

            var viable = scored.Where(s => s.area >= maxArea * 0.30f && s.topY <= lowLimit).ToList();
            if (viable.Count == 0) viable = scored.Where(s => s.area >= maxArea * 0.30f).ToList();
            if (viable.Count == 0) viable = scored;

            info = viable.OrderBy(s => s.topY).ThenByDescending(s => s.area).First();
            return true;
        }

        static bool IsOriginalGeometry(Transform t, Transform box)
        {
            for (var cur = t; cur != null; cur = cur.parent)
            {
                string n = cur.name ?? string.Empty;
                if (n.StartsWith("DEC_", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("MAPSKIN_", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("GATE_", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("ENEMY_", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("HST_", StringComparison.OrdinalIgnoreCase) ||
                    n.StartsWith("Box Volume", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (cur == box) break;
            }
            return true;
        }

        static bool XZNear(Bounds a, Bounds b, float gap)
        {
            return a.min.x <= b.max.x + gap && a.max.x >= b.min.x - gap &&
                   a.min.z <= b.max.z + gap && a.max.z >= b.min.z - gap;
        }

        sealed class Placement
        {
            public Vector3 entrancePos;
            public Vector3 exitPos;
            public float yaw;
            public float width;
            public float height;
        }

        static Placement ComputePlacement(SurfaceInfo surface, Bounds block, string blockName)
        {
            var b = surface.bounds;
            bool alongX = b.size.x >= b.size.z;
            float longSize = alongX ? b.size.x : b.size.z;
            float shortSize = alongX ? b.size.z : b.size.x;

            float width = Mathf.Clamp(Mathf.Min(DefaultWidth, shortSize - 0.6f), 1.8f, DefaultWidth);
            float height = DefaultHeight;
            if (block.size.y > 0.1f)
            {
                // 门头标识在门楣上方约 1.1m，按区块净高给门体留出余量
                height = Mathf.Clamp(DefaultHeight, 2.0f, Mathf.Max(2.0f, block.size.y - 1.4f));
            }

            float inset = Mathf.Min(2.0f, longSize * 0.15f) + 0.5f;
            inset = Mathf.Min(inset, longSize * 0.45f);

            // 两扇门都朝「来人的方向」：入口在长轴负端、出口在正端，正面法线统一指向 -长轴
            float yaw = alongX ? 270f : 180f;

            Vector3 center = b.center;
            center.y = surface.topY + 0.01f;
            Vector3 a = center, d = center;
            if (alongX) { a.x = b.min.x + inset; d.x = b.max.x - inset; }
            else { a.z = b.min.z + inset; d.z = b.max.z - inset; }

            a.y = RefineY(a, surface.topY);
            d.y = RefineY(d, surface.topY);

            return new Placement
            {
                entrancePos = a,
                exitPos = d,
                yaw = yaw,
                width = width,
                height = height,
            };
        }

        /// <summary>在推断面上向下 1.6m 打一条射线，贴合真实地面高度；打不到就用 AABB 顶面。</summary>
        static float RefineY(Vector3 pos, float fallbackTopY)
        {
            var origin = new Vector3(pos.x, fallbackTopY + 0.60f, pos.z);
            if (Physics.Raycast(origin, Vector3.down, out var hit, 1.6f, ~0, QueryTriggerInteraction.Ignore))
                return hit.point.y + 0.01f;
            return fallbackTopY + 0.01f;
        }

        // ------------------------------------------------------------------ 放置 API

        /// <summary>
        /// 唯一的门套件造型入口。生成到 `DEC_Shared/Gate/<block>/GATE_<block>_<Entrance|Exit>`。
        /// 参数 pos 是门口地面中心；yaw 是绕 Y 的朝向（门正面 = +Z）。
        /// 返回值是门根对象；失败返回 null。
        /// </summary>
        public static GameObject Place(string block, Vector3 pos, float yaw, GateType type, float width, float height)
        {
            block = NormalizeBlock(block);
            width = Mathf.Clamp(width, 1.8f, 12f);
            height = Mathf.Clamp(height, 2.0f, 8f);

            var parent = EnsureGateContainer(block);
            if (parent == null) return null;

            DestroyExisting(parent, type);

            var root = new GameObject($"GATE_{block}_{type}");
            root.transform.SetParent(parent, false);
            root.transform.position = pos;
            root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            root.transform.localScale = Vector3.one;

            BuildGate(root.transform, block, type, width, height);
            if (root.transform.childCount == 0)
            {
                UnityEngine.Object.DestroyImmediate(root);
                return null;
            }
            return root;
        }

        static string NormalizeBlock(string block)
        {
            var m = Regex.Match(block ?? string.Empty, @"([1-7])");
            return m.Success ? "S_0" + m.Groups[1].Value : "S_00";
        }

        static Transform EnsureGateContainer(string block)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                Debug.LogError("[GateKit] 没有可用场景，无法放置门。");
                return null;
            }

            var shared = FindRoot(SharedRootName);
            if (shared == null)
            {
                shared = new GameObject(SharedRootName);
                Debug.Log($"[GateKit] 新建公共容器 {SharedRootName}");
            }

            Transform gate = shared.transform.Find(GateBucketName);
            if (gate == null)
            {
                var go = new GameObject(GateBucketName);
                go.transform.SetParent(shared.transform, false);
                gate = go.transform;
            }

            Transform blockRoot = gate.Find(block);
            if (blockRoot == null)
            {
                var go = new GameObject(block);
                go.transform.SetParent(gate, false);
                blockRoot = go.transform;
            }
            return blockRoot;
        }

        static void DestroyExisting(Transform blockRoot, GateType type)
        {
            string suffix = "_" + type;
            for (int i = blockRoot.childCount - 1; i >= 0; i--)
            {
                var c = blockRoot.GetChild(i);
                if (c == null) continue;
                if (c.name.StartsWith("GATE_", StringComparison.Ordinal) &&
                    c.name.EndsWith(suffix, StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(c.gameObject);
            }
        }

        // ------------------------------------------------------------------ 造型

        static void BuildGate(Transform root, string block, GateType type, float width, float height)
        {
            bool entrance = type == GateType.Entrance;
            MapSemantic semantic = entrance ? MapSemantic.Orange : MapSemantic.Cyan;

            var body = MapSkinPalette.GetMaterial(semantic, MapVariant.Base);
            var accent = MapSkinPalette.GetMaterial(semantic, MapVariant.Accent);
            var glow = MapSkinPalette.GetMaterial(semantic, MapVariant.Emissive);
            var dark = MapSkinPalette.GetMaterial(MapSemantic.Black, MapVariant.Base);
            var darkAccent = MapSkinPalette.GetMaterial(MapSemantic.Black, MapVariant.Accent);
            var white = MapSkinPalette.GetMaterial(MapSemantic.White, MapVariant.Base);
            var warning = MapSkinPalette.GetMaterial(MapSemantic.Yellow, MapVariant.Accent);
            var green = MapSkinPalette.GetSignalMaterial("Green", MapSkinPalette.SignalGreen, lit: true);
            var redLens = MapSkinPalette.GetSignalMaterial("Red", MapSkinPalette.SignalRed, lit: false);

            if (body == null || accent == null || glow == null || dark == null)
            {
                Debug.LogError("[GateKit] 共享语义材质创建失败，门套件未生成。");
                return;
            }

            float t = FrameThickness(width);
            float depth = FrameDepth(width);
            float lintelH = LintelHeight(height);
            float openingW = width - 2f * t;
            float openingH = height - lintelH;
            float leafW, leafH;
            LeafSize(width, height, out leafW, out leafH);

            // ---- 1) 门框：倒角盒（左右门柱 + 门楣 + 门槛）
            var frameMesh = GetMesh($"Frame_{width:F2}x{height:F2}", () => BuildFrameMesh(width, height), save: true);
            Part(root, "Frame", frameMesh, body, Vector3.zero, Quaternion.identity, Vector3.one);

            // 门洞内圈强调条（正面）
            var trimMesh = GetMesh($"Trim_{width:F2}x{height:F2}", () => BuildTrimMesh(width, height), save: true);
            Part(root, "OpeningTrim", trimMesh, accent, Vector3.zero, Quaternion.identity, Vector3.one);

            // ---- 2) 双扇门板（左右各一扇；右扇旋转 180° 复用同一 mesh）
            var leafMesh = GetMesh($"Leaf_{leafW:F2}x{leafH:F2}", () => BuildLeafMesh(leafW, leafH), save: true);
            Vector3 leafL = new Vector3(-(leafW * 0.5f + 0.012f), leafH * 0.5f + 0.03f, 0f);
            Vector3 leafR = new Vector3(+(leafW * 0.5f + 0.012f), leafH * 0.5f + 0.03f, 0f);
            // 门板用语义强调色（入口暖橙 / 出口青）：整扇门一眼分色，不靠小字去猜
            Part(root, "DoorLeaf_L", leafMesh, accent, leafL, Quaternion.identity, Vector3.one);
            Part(root, "DoorLeaf_R", leafMesh, accent, leafR, Quaternion.Euler(0f, 180f, 0f), Vector3.one);

            // 中缝 + 横向发光线（一条函数，两种门只换颜色）
            Part(root, "DoorSeam", UnitBox(), darkAccent, new Vector3(0f, leafH * 0.5f + 0.03f, 0.075f),
                Quaternion.identity, new Vector3(0.045f, leafH, 0.16f));
            Part(root, "DoorStripe", UnitBox(), glow, new Vector3(0f, leafH * 0.62f + 0.03f, 0.085f),
                Quaternion.identity, new Vector3(Mathf.Max(0.3f, openingW - 0.14f), 0.075f, 0.03f), castShadow: false);

            // ---- 3) 门槛警示条：黄/黑交替（警示语义）
            int stripes = 6;
            float sw = width / stripes;
            for (int i = 0; i < stripes; i++)
            {
                float x = -width * 0.5f + sw * (i + 0.5f);
                Part(root, "Threshold_" + i, UnitBox(), (i % 2 == 0) ? warning : dark,
                    new Vector3(x, 0.012f, depth * 0.5f + 0.14f),
                    Quaternion.identity, new Vector3(sw * 0.92f, 0.018f, 0.28f));
            }

            // ---- 4) 门头标识：底板 + 边框 + 圆环 + 三斜杠箭头 + IN/OUT 几何字
            float signW = Mathf.Clamp(width * 0.65f, 1.9f, 2.8f);
            float signH = 0.90f;
            float signD = 0.08f;
            float signY = height + signH * 0.5f + 0.20f;
            float signZ = depth * 0.5f + 0.06f;

            Part(root, "SignPlate", UnitBox(), dark, new Vector3(0f, signY, signZ),
                Quaternion.identity, new Vector3(signW, signH, signD));

            // 边框四条短横线
            Part(root, "SignBorder_T", UnitBox(), accent, new Vector3(0f, signY + signH * 0.5f + 0.018f, signZ + signD * 0.5f + 0.012f),
                Quaternion.identity, new Vector3(signW + 0.06f, 0.045f, 0.025f));
            Part(root, "SignBorder_B", UnitBox(), accent, new Vector3(0f, signY - signH * 0.5f - 0.018f, signZ + signD * 0.5f + 0.012f),
                Quaternion.identity, new Vector3(signW + 0.06f, 0.045f, 0.025f));
            Part(root, "SignBorder_L", UnitBox(), accent, new Vector3(-signW * 0.5f - 0.018f, signY, signZ + signD * 0.5f + 0.012f),
                Quaternion.identity, new Vector3(0.045f, signH + 0.06f, 0.025f));
            Part(root, "SignBorder_R", UnitBox(), accent, new Vector3(+signW * 0.5f + 0.018f, signY, signZ + signD * 0.5f + 0.012f),
                Quaternion.identity, new Vector3(0.045f, signH + 0.06f, 0.025f));

            // 门头与门楣之间的两根支撑
            Part(root, "SignBracket_L", UnitBox(), darkAccent, new Vector3(-signW * 0.32f, height + 0.10f, signZ),
                Quaternion.identity, new Vector3(0.08f, 0.20f, 0.08f));
            Part(root, "SignBracket_R", UnitBox(), darkAccent, new Vector3(+signW * 0.32f, height + 0.10f, signZ),
                Quaternion.identity, new Vector3(0.08f, 0.20f, 0.08f));

            float ringX = -signW * 0.30f;
            Part(root, "SignRing", GetMesh("Pattern_Ring", BuildRingMesh, save: true), accent,
                new Vector3(ringX, signY, signZ + signD * 0.5f + 0.020f),
                Quaternion.identity, Vector3.one);

            // 同一个箭头 mesh：Entrance 朝上，Exit 绕 Z 转 180° 朝下
            Part(root, "SignArrow", GetMesh("Pattern_Chevron", BuildChevronMesh, save: true), glow,
                new Vector3(ringX, signY, signZ + signD * 0.5f + 0.042f),
                Quaternion.Euler(0f, 0f, entrance ? 0f : 180f), Vector3.one, castShadow: false);

            string glyphKey = entrance ? "Glyph_IN" : "Glyph_OUT";
            Part(root, "SignText", GetMesh(glyphKey, () => BuildGlyphMesh(entrance ? "IN" : "OUT"), save: true), glow,
                new Vector3(signW * 0.20f, signY, signZ + signD * 0.5f + 0.038f),
                Quaternion.identity, Vector3.one, castShadow: false);

            // ---- 5) 地面导向条 + 三斜杠地箭头 + 短横线
            float stripLen = 2.20f;
            float stripW = width * 0.62f;
            float stripZ = depth * 0.5f + stripLen * 0.5f;

            Part(root, "FloorGuideStrip", UnitBox(), accent, new Vector3(0f, 0.010f, stripZ),
                Quaternion.identity, new Vector3(stripW, 0.020f, stripLen));

            for (int i = -1; i <= 1; i++)
                Part(root, "FloorGuideBar_" + (i + 1), UnitBox(), dark, new Vector3(0f, 0.022f, stripZ + i * 0.70f),
                    Quaternion.identity, new Vector3(stripW * 0.82f, 0.014f, 0.055f));

            // 两块门都朝「来人的方向」；地面箭头一律指向穿过门洞的 +长轴方向（局部 -Z）。
            // 入口：从门外指向门内；出口：从门内指向门外。同一 mesh，同一旋转。
            Part(root, "FloorArrow", GetMesh("Pattern_FloorArrow", BuildFloorArrowMesh, save: true), glow,
                new Vector3(0f, 0.030f, stripZ + 0.10f),
                Quaternion.Euler(0f, 180f, 0f), new Vector3(1.80f, 1f, 1.80f), castShadow: false);

            // ---- 6) 侧柱编号牌：装在右侧门柱外侧面，朝 +X；底板 + 七段数字 + 上下短横线
            float plateW = 0.42f, plateH = 0.34f, plateD = 0.045f;
            var plateRoot = new GameObject("NumberPlateRoot");
            plateRoot.transform.SetParent(root, false);
            plateRoot.transform.localPosition = new Vector3(width * 0.5f + 0.005f, height * 0.55f, 0f);
            plateRoot.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);   // 局部 +Z → 世界 +X

            Part(plateRoot.transform, "NumberPlate", UnitBox(), dark, Vector3.zero,
                Quaternion.identity, new Vector3(plateW, plateH, plateD));
            Part(plateRoot.transform, "NumberPlateBar_T", UnitBox(), accent, new Vector3(0f, plateH * 0.5f + 0.03f, plateD * 0.5f + 0.012f),
                Quaternion.identity, new Vector3(plateW * 0.72f, 0.02f, 0.012f));
            Part(plateRoot.transform, "NumberPlateBar_B", UnitBox(), accent, new Vector3(0f, -plateH * 0.5f - 0.03f, plateD * 0.5f + 0.012f),
                Quaternion.identity, new Vector3(plateW * 0.72f, 0.02f, 0.012f));

            int bi = 1;
            var bm = Regex.Match(block ?? "", @"([1-7])");
            if (bm.Success) bi = bm.Groups[1].Value[0] - '0';
            Part(plateRoot.transform, "NumberDigits", GetMesh($"Digits_{bi:00}", () => BuildDigitsMesh(bi), save: true), white,
                new Vector3(0f, 0f, plateD * 0.5f + 0.015f), Quaternion.identity, Vector3.one);

            // ---- 7) 状态灯：绿灯常亮；红灯是预留的封锁状态（当前熄灭，形态仍在）
            float lampY = height + 0.10f;
            float lampZ = depth * 0.5f;
            float lampX = width * 0.5f - t - 0.30f;

            Part(root, "LampBar", UnitBox(), darkAccent, new Vector3(lampX, lampY - 0.05f, lampZ),
                Quaternion.identity, new Vector3(0.58f, 0.06f, 0.09f));
            Part(root, "Lamp_Green", GetMesh("UnitSphere", BuildUnitSphereMesh, save: true), green,
                new Vector3(lampX - 0.15f, lampY + 0.04f, lampZ), Quaternion.identity, new Vector3(0.16f, 0.16f, 0.16f), castShadow: false);
            Part(root, "Lamp_Red", GetMesh("UnitSphere", BuildUnitSphereMesh, save: true), redLens,
                new Vector3(lampX + 0.15f, lampY + 0.04f, lampZ), Quaternion.identity, new Vector3(0.16f, 0.16f, 0.16f), castShadow: false);

            // 门套件对象统一标记，方便清除与审计
            root.gameObject.tag = "Untagged";
        }

        static float FrameThickness(float width) => Mathf.Clamp(width * 0.06f, 0.14f, 0.32f);
        static float FrameDepth(float width) => Mathf.Clamp(width * 0.10f, 0.35f, 0.60f);
        static float LintelHeight(float height) => Mathf.Clamp(height * 0.12f, 0.28f, 0.50f);

        static void LeafSize(float width, float height, out float leafW, out float leafH)
        {
            float t = FrameThickness(width);
            float lintelH = LintelHeight(height);
            float openingW = width - 2f * t;
            float openingH = height - lintelH;
            leafW = openingW * 0.5f - 0.02f;
            leafH = openingH - 0.06f;
        }

        static GameObject Part(Transform parent, string name, Mesh mesh, Material mat,
            Vector3 localPos, Quaternion localRot, Vector3 localScale, bool castShadow = true)
        {
            if (mesh == null || mat == null) return null;

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.transform.localScale = localScale;

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = castShadow ? ShadowCastingMode.On : ShadowCastingMode.Off;
            mr.receiveShadows = true;
            return go;
        }

        // ------------------------------------------------------------------ mesh 资产

        static void EnsureMeshFolder()
        {
            EnsureFolderPath("Assets/Art/EnvSkins/Shared/Gate");
        }

        static void EnsureFolderPath(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent) || parent == path) return;
            EnsureFolderPath(parent);
            string leaf = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(leaf) && AssetDatabase.IsValidFolder(parent))
                AssetDatabase.CreateFolder(parent, leaf);
        }

        /// <summary>
        /// 取共享 mesh。save=true 时落盘到 `Assets/Art/EnvSkins/Shared/Gate/GATE_<key>.asset`；
        /// 同一 key 七图共用一份 → 图案逐字节相同。
        /// </summary>
        static Mesh GetMesh(string key, Func<Mesh> build, bool save)
        {
            if (MeshCache.TryGetValue(key, out var cached) && cached != null)
            {
                if (!save || AssetDatabase.Contains(cached)) return cached;
                MeshCache.Remove(key);
            }

            string path = $"{MeshDir}/GATE_v{KitVersion}_{key}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                MeshCache[key] = existing;
                return existing;
            }

            var m = build();
            if (m == null) return null;
            m.name = "GATE_" + key;
            if (save)
            {
                EnsureMeshFolder();
                AssetDatabase.CreateAsset(m, path);
            }
            MeshCache[key] = m;
            return m;
        }

        // ------------------------------------------------------------------ 几何：门框 / 门板 / 图案

        static Mesh BuildFrameMesh(float width, float height)
        {
            var mb = new MeshBuilder();
            float t = FrameThickness(width);
            float depth = FrameDepth(width);
            float lintelH = LintelHeight(height);
            float c = Mathf.Min(0.035f, t * 0.18f);

            mb.AddChamferedBox(new Vector3(-width * 0.5f + t * 0.5f, height * 0.5f, 0f), new Vector3(t, height, depth), c);
            mb.AddChamferedBox(new Vector3(+width * 0.5f - t * 0.5f, height * 0.5f, 0f), new Vector3(t, height, depth), c);
            mb.AddChamferedBox(new Vector3(0f, height - lintelH * 0.5f, 0f), new Vector3(width, lintelH, depth), c);
            mb.AddChamferedBox(new Vector3(0f, 0.035f, 0f), new Vector3(width, t * 0.85f, depth * 1.06f), c * 0.5f);
            return mb.Build("GATE_Frame");
        }

        static Mesh BuildTrimMesh(float width, float height)
        {
            var mb = new MeshBuilder();
            float t = FrameThickness(width);
            float depth = FrameDepth(width);
            float lintelH = LintelHeight(height);
            float openingW = width - 2f * t;
            float openingH = height - lintelH;
            float z = depth * 0.5f + 0.020f;
            float c = 0.008f;

            mb.AddChamferedBox(new Vector3(-openingW * 0.5f - 0.03f, openingH * 0.5f, z), new Vector3(0.06f, openingH, 0.03f), c);
            mb.AddChamferedBox(new Vector3(+openingW * 0.5f + 0.03f, openingH * 0.5f, z), new Vector3(0.06f, openingH, 0.03f), c);
            mb.AddChamferedBox(new Vector3(0f, openingH + 0.03f, z), new Vector3(openingW + 0.12f, 0.06f, 0.03f), c);
            return mb.Build("GATE_Trim");
        }

        static Mesh BuildLeafMesh(float leafW, float leafH)
        {
            var mb = new MeshBuilder();
            float depth = 0.12f;
            mb.AddChamferedBox(Vector3.zero, new Vector3(leafW, leafH, depth), 0.018f);
            // 门板正面做一层内凹面板（同一材质，靠倒角阴影读层次）
            mb.AddChamferedBox(new Vector3(0f, 0f, depth * 0.5f + 0.010f),
                new Vector3(leafW * 0.80f, leafH * 0.84f, 0.020f), 0.008f);
            return mb.Build("GATE_Leaf");
        }

        /// <summary>三根斜杠叠成的向上箭头（XY 平面，正面 +Z）。Exit 只旋转 180°，mesh 不换。</summary>
        static Mesh BuildChevronMesh()
        {
            var mb = new MeshBuilder();
            float barLen = 0.20f, barThick = 0.045f, barDepth = 0.030f;
            float half = barLen * 0.70710678f * 0.5f;   // 45° 斜杠的半投影
            for (int i = 0; i < 3; i++)
            {
                float y0 = -0.16f + i * 0.11f;
                mb.AddBox(new Vector3(-half, y0 + half, 0f),
                    new Vector3(barLen, barThick, barDepth), Quaternion.Euler(0f, 0f, 45f));
                mb.AddBox(new Vector3(+half, y0 + half, 0f),
                    new Vector3(barLen, barThick, barDepth), Quaternion.Euler(0f, 0f, -45f));
            }
            return mb.Build("GATE_Pattern_Chevron");
        }

        /// <summary>地面三斜杠箭头（XZ 平面，指向 +Z）。Exit 绕 Y 转 180° 指向 -Z，mesh 不换。</summary>
        static Mesh BuildFloorArrowMesh()
        {
            var mb = new MeshBuilder();
            float barLen = 0.30f, barThick = 0.060f, barH = 0.024f;
            for (int i = 0; i < 3; i++)
            {
                float z0 = -0.24f + i * 0.16f;
                mb.AddBox(new Vector3(-0.106f, 0f, z0 + 0.106f),
                    new Vector3(barThick, barH, barLen), Quaternion.Euler(0f, 45f, 0f));
                mb.AddBox(new Vector3(+0.106f, 0f, z0 + 0.106f),
                    new Vector3(barThick, barH, barLen), Quaternion.Euler(0f, -45f, 0f));
            }
            return mb.Build("GATE_Pattern_FloorArrow");
        }

        /// <summary>圆环（XY 平面，正面 +Z）。半径固定，七图共用。</summary>
        static Mesh BuildRingMesh()
        {
            var mb = new MeshBuilder();
            mb.AddRing(0.30f, 0.245f, 0.040f, 32);
            return mb.Build("GATE_Pattern_Ring");
        }

        /// <summary>笔画字母：I/N/O/U/T。不依赖字体贴图。</summary>
        static Mesh BuildGlyphMesh(string text)
        {
            var mb = new MeshBuilder();
            float h = 0.36f, t = 0.055f, d = 0.030f, gap = 0.070f;

            float total = 0f;
            foreach (char ch in text) total += GlyphAdvance(ch, h) + gap;
            total -= gap;

            float x = -total * 0.5f;
            foreach (char ch in text)
            {
                float w = GlyphAdvance(ch, h);
                AddGlyph(mb, ch, x + w * 0.5f, 0f, h, t, d);
                x += w + gap;
            }
            return mb.Build("GATE_Glyph");
        }

        static float GlyphAdvance(char ch, float h)
        {
            switch (ch)
            {
                case 'I': return h * 0.34f;
                default: return h * 0.70f;
            }
        }

        static void AddGlyph(MeshBuilder mb, char ch, float cx, float cy, float h, float t, float d)
        {
            float halfH = h * 0.5f;
            float halfW = h * 0.28f;

            switch (ch)
            {
                case 'I':
                    mb.AddBox(new Vector3(cx, cy, 0f), new Vector3(t, h, d));
                    mb.AddBox(new Vector3(cx, cy + halfH, 0f), new Vector3(h * 0.30f, t, d));
                    mb.AddBox(new Vector3(cx, cy - halfH, 0f), new Vector3(h * 0.30f, t, d));
                    break;

                case 'N':
                    mb.AddBox(new Vector3(cx - halfW, cy, 0f), new Vector3(t, h, d));
                    mb.AddBox(new Vector3(cx + halfW, cy, 0f), new Vector3(t, h, d));
                    {
                        float len = Mathf.Sqrt((halfW * 2f) * (halfW * 2f) + h * h);
                        float angle = Mathf.Atan2(h, halfW * 2f) * Mathf.Rad2Deg;
                        mb.AddBox(new Vector3(cx, cy, 0f), new Vector3(len, t, d), Quaternion.Euler(0f, 0f, angle));
                    }
                    break;

                case 'O':
                    mb.AddBox(new Vector3(cx - halfW, cy, 0f), new Vector3(t, h, d));
                    mb.AddBox(new Vector3(cx + halfW, cy, 0f), new Vector3(t, h, d));
                    mb.AddBox(new Vector3(cx, cy + halfH, 0f), new Vector3(halfW * 2f + t, t, d));
                    mb.AddBox(new Vector3(cx, cy - halfH, 0f), new Vector3(halfW * 2f + t, t, d));
                    break;

                case 'U':
                    mb.AddBox(new Vector3(cx - halfW, cy, 0f), new Vector3(t, h, d));
                    mb.AddBox(new Vector3(cx + halfW, cy, 0f), new Vector3(t, h, d));
                    mb.AddBox(new Vector3(cx, cy - halfH, 0f), new Vector3(halfW * 2f + t, t, d));
                    break;

                case 'T':
                    mb.AddBox(new Vector3(cx, cy + halfH, 0f), new Vector3(halfW * 2f + t, t, d));
                    mb.AddBox(new Vector3(cx, cy, 0f), new Vector3(t, h, d));
                    break;

                default:
                    // 未知字符用短横线占位，保持几何可读
                    mb.AddBox(new Vector3(cx, cy, 0f), new Vector3(halfW * 1.4f, t, d));
                    break;
            }
        }

        /// <summary>编号牌数字：七段数码管几何，01–07。</summary>
        static Mesh BuildDigitsMesh(int index)
        {
            var mb = new MeshBuilder();
            string text = index.ToString("00", CultureInfo.InvariantCulture);
            float w = 0.15f, h = 0.26f, t = 0.035f, d = 0.030f, gap = 0.060f;
            float total = w * text.Length + gap * (text.Length - 1);
            float x = -total * 0.5f + w * 0.5f;

            foreach (char ch in text)
            {
                if (ch >= '0' && ch <= '9') AddSevenSeg(mb, ch - '0', x, 0f, w, h, t, d);
                x += w + gap;
            }
            return mb.Build("GATE_Digits");
        }

        static void AddSevenSeg(MeshBuilder mb, int digit, float cx, float cy, float w, float h, float t, float d)
        {
            //       a
            //     f   b
            //       g
            //     e   c
            //       d
            bool[] seg =
            {
                (digit != 1 && digit != 4),                          // a
                (digit != 5 && digit != 6),                          // b
                (digit != 2),                                        // c
                (digit != 1 && digit != 4 && digit != 7),            // d
                (digit == 0 || digit == 2 || digit == 6 || digit == 8), // e
                (digit != 1 && digit != 2 && digit != 3 && digit != 7), // f
                (digit != 0 && digit != 1 && digit != 7),            // g
            };

            float hx = w * 0.5f, hy = h * 0.5f;
            if (seg[0]) mb.AddBox(new Vector3(cx, cy + hy, 0f), new Vector3(w, t, d));
            if (seg[1]) mb.AddBox(new Vector3(cx + hx, cy + hy * 0.5f, 0f), new Vector3(t, hy, d));
            if (seg[2]) mb.AddBox(new Vector3(cx + hx, cy - hy * 0.5f, 0f), new Vector3(t, hy, d));
            if (seg[3]) mb.AddBox(new Vector3(cx, cy - hy, 0f), new Vector3(w, t, d));
            if (seg[4]) mb.AddBox(new Vector3(cx - hx, cy - hy * 0.5f, 0f), new Vector3(t, hy, d));
            if (seg[5]) mb.AddBox(new Vector3(cx - hx, cy + hy * 0.5f, 0f), new Vector3(t, hy, d));
            if (seg[6]) mb.AddBox(new Vector3(cx, cy, 0f), new Vector3(w, t, d));
        }

        /// <summary>1m 立方体共享 mesh：门板条、铭牌、灯座等纯盒件复用，避免为每件生成网格。</summary>
        static Mesh UnitBox() => GetMesh("UnitBox", BuildUnitBoxMesh, save: true);

        static Mesh BuildUnitBoxMesh()
        {
            var mb = new MeshBuilder();
            mb.AddBox(Vector3.zero, Vector3.one);
            return mb.Build("GATE_UnitBox");
        }

        static Mesh BuildUnitSphereMesh()
        {
            var mb = new MeshBuilder();
            mb.AddSphere(Vector3.zero, 0.5f, 20, 10);
            return mb.Build("GATE_UnitSphere");
        }

        // ------------------------------------------------------------------ Manifest / 哈希

        [Serializable]
        public class GateManifest
        {
            public int kitVersion;
            public string scene;
            public string generatedUtc;
            public bool dryRun;
            public List<GateRec> gates;
            public List<SkipRec> skipped;
            public List<PatternHash> patternHashes;
        }

        [Serializable]
        public class GateRec
        {
            public string block;
            public string type;
            public string container;
            public float[] pos;
            public float yaw;
            public float width;
            public float height;
            public float surfaceTopY;
            public float surfaceArea;
            public string surfaceSource;
        }

        [Serializable]
        public class SkipRec
        {
            public string block;
            public string reason;
        }

        [Serializable]
        public class PatternHash
        {
            public string pattern;
            public string hash;
            public int vertices;
            public int triangles;
        }

        static GateRec Record(string block, GateType type, Vector3 pos, float yaw, float width, float height, SurfaceInfo surface)
        {
            return new GateRec
            {
                block = block,
                type = type.ToString(),
                container = $"{SharedRootName}/{GateBucketName}/{block}",
                pos = new[] { pos.x, pos.y, pos.z },
                yaw = yaw,
                width = width,
                height = height,
                surfaceTopY = surface.topY,
                surfaceArea = surface.area,
                surfaceSource = surface.source,
            };
        }

        static void AddHash(List<PatternHash> list, string key, Mesh m)
        {
            if (m == null) return;
            list.Add(new PatternHash
            {
                pattern = key,
                hash = HashMesh(m),
                vertices = m.vertexCount,
                triangles = m.triangles.Length / 3,
            });
        }

        /// <summary>FNV-1a 64：对顶点、法线、三角形索引做顺序哈希，用来证明七图图案逐字节相同。</summary>
        static string HashMesh(Mesh m)
        {
            ulong h = 14695981039346656037UL;
            var vs = m.vertices;
            var ns = m.normals;
            var ts = m.triangles;
            foreach (var v in vs) { MixFloat(ref h, v.x); MixFloat(ref h, v.y); MixFloat(ref h, v.z); }
            foreach (var v in ns) { MixFloat(ref h, v.x); MixFloat(ref h, v.y); MixFloat(ref h, v.z); }
            foreach (var i in ts) MixInt(ref h, i);
            return h.ToString("x16", CultureInfo.InvariantCulture);
        }

        static void MixFloat(ref ulong h, float f)
        {
            int bits = BitConverter.ToInt32(BitConverter.GetBytes(f), 0);
            MixInt(ref h, bits);
        }

        static void MixInt(ref ulong h, int v)
        {
            for (int i = 0; i < 4; i++)
            {
                h ^= (byte)(v >> (i * 8));
                h *= 1099511628211UL;
            }
        }

        static void WriteManifest(GateManifest man, string fileName)
        {
            string json = JsonUtility.ToJson(man, true);
            try
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                var workspace = Directory.GetParent(projectRoot)?.FullName ?? projectRoot;
                var dir = Path.Combine(workspace, "_session_audit");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, fileName);
                File.WriteAllText(path, json, new UTF8Encoding(false));
                Debug.Log($"[GateKit] Manifest -> {path}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GateKit] Manifest 写盘失败：{ex.Message}\n{json}");
            }
        }

        static string V(Vector3 v) =>
            string.Format(CultureInfo.InvariantCulture, "({0:F2},{1:F2},{2:F2})", v.x, v.y, v.z);

        // =================================================================================
        // 程序化网格 builder（倒角盒 / 球 / 圆环；全部 flat 法线、确定性顶点顺序）
        // =================================================================================

        sealed class MeshBuilder
        {
            readonly List<Vector3> verts = new List<Vector3>();
            readonly List<Vector3> norms = new List<Vector3>();
            readonly List<Vector2> uvs = new List<Vector2>();
            readonly List<int> tris = new List<int>();

            const float UvScale = 3.6f;

            public Mesh Build(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(verts);
                m.SetNormals(norms);
                m.SetUVs(0, uvs);
                m.SetTriangles(tris, 0);
                m.RecalculateBounds();
                return m;
            }

            // ---------------------------------------------------------- 盒体

            public void AddBox(Vector3 center, Vector3 size)
            {
                AddBox(center, size, Quaternion.identity);
            }

            public void AddBox(Vector3 center, Vector3 size, Quaternion rot)
            {
                float hx = size.x * 0.5f, hy = size.y * 0.5f, hz = size.z * 0.5f;
                Vector3 p000 = new Vector3(-hx, -hy, -hz);
                Vector3 p001 = new Vector3(-hx, -hy, +hz);
                Vector3 p010 = new Vector3(-hx, +hy, -hz);
                Vector3 p011 = new Vector3(-hx, +hy, +hz);
                Vector3 p100 = new Vector3(+hx, -hy, -hz);
                Vector3 p101 = new Vector3(+hx, -hy, +hz);
                Vector3 p110 = new Vector3(+hx, +hy, -hz);
                Vector3 p111 = new Vector3(+hx, +hy, +hz);

                AddQuad(p100, p110, p111, p101, Vector3.right, rot, center);
                AddQuad(p000, p001, p011, p010, Vector3.left, rot, center);
                AddQuad(p010, p011, p111, p110, Vector3.up, rot, center);
                AddQuad(p000, p100, p101, p001, Vector3.down, rot, center);
                AddQuad(p001, p101, p111, p011, Vector3.forward, rot, center);
                AddQuad(p000, p010, p110, p100, Vector3.back, rot, center);
            }

            public void AddChamferedBox(Vector3 center, Vector3 size, float chamfer)
            {
                AddChamferedBox(center, size, chamfer, Quaternion.identity);
            }

            /// <summary>12 条棱一条 45° 切面 + 8 个角三角；法线 flat，和场景灰盒的硬表面语言一致。</summary>
            public void AddChamferedBox(Vector3 center, Vector3 size, float chamfer, Quaternion rot)
            {
                float c = Mathf.Clamp(chamfer, 0f, Mathf.Min(size.x, Mathf.Min(size.y, size.z)) * 0.45f);
                if (c <= 1e-4f) { AddBox(center, size, rot); return; }

                float hx = size.x * 0.5f, hy = size.y * 0.5f, hz = size.z * 0.5f;
                float ix = hx - c, iy = hy - c, iz = hz - c;

                // 6 个主面
                AddQuad(new Vector3(hx, -iy, -iz), new Vector3(hx, iy, -iz), new Vector3(hx, iy, iz), new Vector3(hx, -iy, iz), Vector3.right, rot, center);
                AddQuad(new Vector3(-hx, -iy, -iz), new Vector3(-hx, -iy, iz), new Vector3(-hx, iy, iz), new Vector3(-hx, iy, -iz), Vector3.left, rot, center);
                AddQuad(new Vector3(-ix, hy, -iz), new Vector3(-ix, hy, iz), new Vector3(ix, hy, iz), new Vector3(ix, hy, -iz), Vector3.up, rot, center);
                AddQuad(new Vector3(-ix, -hy, -iz), new Vector3(ix, -hy, -iz), new Vector3(ix, -hy, iz), new Vector3(-ix, -hy, iz), Vector3.down, rot, center);
                AddQuad(new Vector3(-ix, -iy, hz), new Vector3(ix, -iy, hz), new Vector3(ix, iy, hz), new Vector3(-ix, iy, hz), Vector3.forward, rot, center);
                AddQuad(new Vector3(-ix, -iy, -hz), new Vector3(-ix, iy, -hz), new Vector3(ix, iy, -hz), new Vector3(ix, -iy, -hz), Vector3.back, rot, center);

                // 12 条棱切面：沿 X / Y / Z 各 4 条
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        AddQuad(
                            new Vector3(-ix, sy * hy, sz * iz), new Vector3(ix, sy * hy, sz * iz),
                            new Vector3(ix, sy * iy, sz * hz), new Vector3(-ix, sy * iy, sz * hz),
                            new Vector3(0f, sy, sz).normalized, rot, center);
                    }
                }
                for (int sx = -1; sx <= 1; sx += 2)
                {
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        AddQuad(
                            new Vector3(sx * hx, -iy, sz * iz), new Vector3(sx * hx, iy, sz * iz),
                            new Vector3(sx * ix, iy, sz * hz), new Vector3(sx * ix, -iy, sz * hz),
                            new Vector3(sx, 0f, sz).normalized, rot, center);
                    }
                }
                for (int sx = -1; sx <= 1; sx += 2)
                {
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        AddQuad(
                            new Vector3(sx * hx, sy * iy, -iz), new Vector3(sx * hx, sy * iy, iz),
                            new Vector3(sx * ix, sy * hy, iz), new Vector3(sx * ix, sy * hy, -iz),
                            new Vector3(sx, sy, 0f).normalized, rot, center);
                    }
                }

                // 8 个角
                for (int sx = -1; sx <= 1; sx += 2)
                {
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            AddTri(
                                new Vector3(sx * hx, sy * iy, sz * iz),
                                new Vector3(sx * ix, sy * hy, sz * iz),
                                new Vector3(sx * ix, sy * iy, sz * hz),
                                new Vector3(sx, sy, sz).normalized, rot, center);
                        }
                    }
                }
            }

            // ---------------------------------------------------------- 球 / 圆环

            public void AddSphere(Vector3 center, float radius, int segments, int rings)
            {
                segments = Mathf.Max(3, segments);
                rings = Mathf.Max(2, rings);

                for (int r = 0; r < rings; r++)
                {
                    float phi0 = Mathf.PI * r / rings;
                    float phi1 = Mathf.PI * (r + 1) / rings;
                    for (int s = 0; s < segments; s++)
                    {
                        float th0 = Mathf.PI * 2f * s / segments;
                        float th1 = Mathf.PI * 2f * (s + 1) / segments;

                        Vector3 p00 = SpherePoint(phi0, th0, radius);
                        Vector3 p01 = SpherePoint(phi0, th1, radius);
                        Vector3 p10 = SpherePoint(phi1, th0, radius);
                        Vector3 p11 = SpherePoint(phi1, th1, radius);

                        Vector3 n = (p00 + p01 + p10 + p11).normalized;
                        AddQuad(p00 + center, p10 + center, p11 + center, p01 + center, n, Quaternion.identity, Vector3.zero);
                    }
                }
            }

            static Vector3 SpherePoint(float phi, float theta, float radius)
            {
                float sp = Mathf.Sin(phi);
                return new Vector3(radius * sp * Mathf.Cos(theta), radius * Mathf.Cos(phi), radius * sp * Mathf.Sin(theta));
            }

            public void AddRing(float outer, float inner, float depth, int segments)
            {
                outer = Mathf.Max(outer, inner + 1e-3f);
                segments = Mathf.Max(6, segments);
                float hz = depth * 0.5f;

                for (int i = 0; i < segments; i++)
                {
                    float a0 = Mathf.PI * 2f * i / segments;
                    float a1 = Mathf.PI * 2f * (i + 1) / segments;
                    Vector2 d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0));
                    Vector2 d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
                    Vector2 dm = (d0 + d1).normalized;

                    // 正面 / 背面
                    AddQuad(
                        new Vector3(d0.x * inner, d0.y * inner, hz),
                        new Vector3(d0.x * outer, d0.y * outer, hz),
                        new Vector3(d1.x * outer, d1.y * outer, hz),
                        new Vector3(d1.x * inner, d1.y * inner, hz),
                        Vector3.forward, Quaternion.identity, Vector3.zero);
                    AddQuad(
                        new Vector3(d0.x * inner, d0.y * inner, -hz),
                        new Vector3(d1.x * inner, d1.y * inner, -hz),
                        new Vector3(d1.x * outer, d1.y * outer, -hz),
                        new Vector3(d0.x * outer, d0.y * outer, -hz),
                        Vector3.back, Quaternion.identity, Vector3.zero);

                    // 外壁 / 内壁
                    AddQuad(
                        new Vector3(d0.x * outer, d0.y * outer, -hz),
                        new Vector3(d0.x * outer, d0.y * outer, hz),
                        new Vector3(d1.x * outer, d1.y * outer, hz),
                        new Vector3(d1.x * outer, d1.y * outer, -hz),
                        new Vector3(dm.x, dm.y, 0f), Quaternion.identity, Vector3.zero);
                    AddQuad(
                        new Vector3(d0.x * inner, d0.y * inner, -hz),
                        new Vector3(d1.x * inner, d1.y * inner, -hz),
                        new Vector3(d1.x * inner, d1.y * inner, hz),
                        new Vector3(d0.x * inner, d0.y * inner, hz),
                        new Vector3(-dm.x, -dm.y, 0f), Quaternion.identity, Vector3.zero);
                }
            }

            // ---------------------------------------------------------- 基础三角面

            void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward, Quaternion rot, Vector3 offset)
            {
                a = rot * a + offset;
                b = rot * b + offset;
                c = rot * c + offset;
                d = rot * d + offset;
                outward = (rot * outward).normalized;

                // 保证从外侧看是 CCW；顺序不对就反转
                Vector3 cross = Vector3.Cross(b - a, c - a);
                if (Vector3.Dot(cross, outward) < 0f)
                {
                    Vector3 tmp = b; b = d; d = tmp;
                }

                int i0 = AddVert(a, outward);
                int i1 = AddVert(b, outward);
                int i2 = AddVert(c, outward);
                int i3 = AddVert(d, outward);
                tris.Add(i0); tris.Add(i1); tris.Add(i2);
                tris.Add(i0); tris.Add(i2); tris.Add(i3);
            }

            void AddTri(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, Quaternion rot, Vector3 offset)
            {
                a = rot * a + offset;
                b = rot * b + offset;
                c = rot * c + offset;
                outward = (rot * outward).normalized;

                Vector3 cross = Vector3.Cross(b - a, c - a);
                if (Vector3.Dot(cross, outward) < 0f)
                {
                    Vector3 tmp = b; b = c; c = tmp;
                }

                int i0 = AddVert(a, outward);
                int i1 = AddVert(b, outward);
                int i2 = AddVert(c, outward);
                tris.Add(i0); tris.Add(i1); tris.Add(i2);
            }

            int AddVert(Vector3 p, Vector3 n)
            {
                verts.Add(p);
                norms.Add(n);
                uvs.Add(new Vector2((p.x + p.z) / UvScale, p.y / UvScale));
                return verts.Count - 1;
            }
        }
    }
}
