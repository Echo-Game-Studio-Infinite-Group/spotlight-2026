// =====================================================================================
// 出生点套件 / 终点套件 —— Level1 专用（S_01 起始区、S_07 末端）
//
// 用户口径：
//   · 出生点 = "入口/起飞台"的仪式感：门架 + 垂直光环 + 八角出生盘 + 三道前进导向标；
//   · 终点   = 收束感：中央核心柱 + 两道水平能量环 + 16 边形终点平台 + FINISH 控制台；
//   · 两套件同设计语言但不同构，只在 S_01 / S_07 各用一次；
//   · 位置从区块包围盒推断，失败就跳过并在 manifest 的 skipped 里写原因；
//   · 只生成场景对象 + mesh 资产；不写 prefab、不 new Material（用 CopyAsset 复制 NPR 母材质）；
//   · 套件是纯视觉件，不加 Collider —— 碰撞仍由原地编几何承担。
//
// 资产来源（由 Blender 生成器产出，路径写在 manifest 里）：
//   F:/VSCode/工业级渲染/tmp/unity_export/spawn_goal/spawn/SPAWN_KIT_*.fbx
//   F:/VSCode/工业级渲染/tmp/unity_export/spawn_goal/goal/GOAL_KIT_*.fbx
//   F:/VSCode/工业级渲染/exports/spawn_goal/{spawn,goal}/<asset>/<asset>_{BC,N,...}.png
//
// 菜单：
//   MapSkin/出生终点套件/① 干跑：推断位置（零写盘）
//   MapSkin/出生终点套件/② Run：S_01 出生点 + S_07 终点
//   MapSkin/出生终点套件/③ Clear：删除 DEC_Shared/SpawnGoal
// 批处理：
//   -executeMethod MapSkin.SpawnGoalKit.DryRun
//   -executeMethod MapSkin.SpawnGoalKit.Run
//   -executeMethod MapSkin.SpawnGoalKit.Clear
//
// 输出：<workspace>/_session_audit/SPAWN_GOAL_Manifest.json
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
    /// <summary>
    /// 出生点 / 终点套件：唯一放置入口是 PlaceOne；Run 只负责找区块、推位置、调 PlaceOne。
    /// </summary>
    public static class SpawnGoalKit
    {
        // ------------------------------------------------------------------ 常量

        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string SharedRootName = "DEC_Shared";
        const string BucketName = "SpawnGoal";
        const string ManifestFile = "SPAWN_GOAL_Manifest.json";
        const string ManifestDryFile = "SPAWN_GOAL_Manifest.dryrun.json";
        const string NprMother = "Assets/Art/Materials/NPRBase.mat";

        const string SpawnBlock = "S_01";
        const string GoalBlock = "S_07";
        const string SpawnDefaultAsset = "SPAWN_KIT_W10_H7.4_D6.5";
        const string GoalDefaultAsset = "GOAL_KIT_W11_H8.5_D7.5";
        const string SpawnSourceRoot = "F:/VSCode/工业级渲染/tmp/unity_export/spawn_goal/spawn";
        const string GoalSourceRoot = "F:/VSCode/工业级渲染/tmp/unity_export/spawn_goal/goal";
        const string SpawnTexRoot = "F:/VSCode/工业级渲染/exports/spawn_goal/spawn";
        const string GoalTexRoot = "F:/VSCode/工业级渲染/exports/spawn_goal/goal";

        static readonly string[] Channels = { "BC", "N", "R", "M", "AO", "H", "MS", "ORM", "E" };

        // ------------------------------------------------------------------ 数据契约

        [Serializable]
        public class KitDoc
        {
            public int schema;
            public string scene;
            public string generatedUtc;
            public string status;
            public KitRec[] kits;
            public SkipRec[] skipped;
        }

        [Serializable]
        public class KitRec
        {
            public string type;              // Spawn / Goal
            public string block;             // S_01 / S_07
            public string status;            // assets-ready / placed / skipped
            public string container;         // 场景层级路径
            public string asset;             // SPAWN_KIT_...
            public float[] size;             // [W, H, D]
            public float[] pos;              // [x, y, z]
            public float yaw;
            public SurfaceRec surface;
            public string orientationBasis;  // GATE_Manifest / bbox-long-axis
            public MeshRec mesh;
            public TexRec texture;
            public string material;
            public string layoutFile;
            public string note;
        }

        [Serializable]
        public class SurfaceRec
        {
            public string source;
            public float topY;
            public float area;
        }

        [Serializable]
        public class MeshRec
        {
            public string sourceFbx;
            public string assetPath;
            public int vertices;
            public int triangles;
            public int nonManifoldEdges;
            public int boundaryEdges;
            public int faces;
        }

        [Serializable]
        public class TexRec
        {
            public string sourceDir;
            public string assetDir;
            public string[] channels;
            public float mmPerPx;
            public int[] resolution;
            public float[] sheet;
            public string canvas;
        }

        [Serializable]
        public class SkipRec
        {
            public string block;
            public string reason;
        }

        [Serializable]
        public sealed class GateDoc
        {
            public GateRec[] gates;
        }

        [Serializable]
        public sealed class GateRec
        {
            public string block;
            public string type;
            public float[] pos;
        }

        // ------------------------------------------------------------------ 菜单 / 批处理

        [MenuItem("MapSkin/出生终点套件/① 干跑：推断 S_01 / S_07 位置（零写盘）")]
        public static void DryRunMenu() => DryRun();

        [MenuItem("MapSkin/出生终点套件/② Run：放置出生点 + 终点")]
        public static void RunMenu() => Run();

        [MenuItem("MapSkin/出生终点套件/③ Clear：删除 DEC_Shared/SpawnGoal")]
        public static void ClearMenu() => Clear();

        /// <summary>只推断位置并写 dryrun 报告，不复制资产、不改场景。</summary>
        public static void DryRun() => RunCore(dry: true);

        /// <summary>放置两套件并保存场景，manifest 写回实际位置/朝向。</summary>
        public static void Run() => RunCore(dry: false);

        /// <summary>删除 DEC_Shared/SpawnGoal（含两个区块容器）；导入资产保留复用。</summary>
        public static void Clear()
        {
            if (!EnsureScene()) return;

            var shared = GameObject.Find(SharedRootName);
            if (shared != null)
            {
                var bucket = shared.transform.Find(BucketName);
                if (bucket != null)
                {
                    int n = bucket.childCount;
                    UnityEngine.Object.DestroyImmediate(bucket.gameObject);
                    Debug.Log($"[SpawnGoal·清除] 删除 {SharedRootName}/{BucketName}（{n} 个区块容器）。");
                }
                if (shared.transform.childCount == 0)
                    UnityEngine.Object.DestroyImmediate(shared.gameObject);
            }
            else
            {
                Debug.Log("[SpawnGoal·清除] 场景里没有 DEC_Shared，无需清除。");
            }

            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        // ------------------------------------------------------------------ 主流程

        static void RunCore(bool dry)
        {
            if (!EnsureScene()) return;

            var doc = LoadManifest();
            if (doc == null)
            {
                doc = new KitDoc
                {
                    schema = 1,
                    scene = ScenePath,
                    kits = new KitRec[0],
                    skipped = new SkipRec[0],
                };
            }

            var kits = new List<KitRec>(doc.kits ?? new KitRec[0]);
            var skips = new List<SkipRec>();
            var rep = new StringBuilder();
            rep.AppendLine($"[SpawnGoal·{(dry ? "干跑" : "生成")}] scene={ScenePath}");

            int placed = 0;
            placed += PlaceOne(kits, skips, rep, "Spawn", SpawnBlock, true, dry,
                               SpawnSourceRoot, SpawnTexRoot, SpawnDefaultAsset);
            placed += PlaceOne(kits, skips, rep, "Goal", GoalBlock, false, dry,
                               GoalSourceRoot, GoalTexRoot, GoalDefaultAsset);

            doc.kits = kits.ToArray();
            doc.skipped = skips.ToArray();
            doc.status = dry ? "dry-run" : (placed == 2 ? "placed" : (placed > 0 ? "partial" : "failed"));
            doc.generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            if (dry)
            {
                WriteManifest(doc, ManifestDryFile);
            }
            else
            {
                AssetDatabase.SaveAssets();
                var scene = EditorSceneManager.GetActiveScene();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                WriteManifest(doc, ManifestFile);
            }

            rep.AppendLine($"[SpawnGoal] 放置 {placed} 套，跳过 {skips.Count} 套。");
            foreach (var s in skips) rep.AppendLine($"    [跳过] {s.block}: {s.reason}");
            Debug.Log(rep.ToString());
        }

        static int PlaceOne(List<KitRec> kits, List<SkipRec> skips, StringBuilder rep,
                            string type, string block, bool spawn, bool dry,
                            string sourceRoot, string texRoot, string defaultAsset)
        {
            var rec = kits.FirstOrDefault(k => k != null && k.type == type);
            if (rec == null)
            {
                rec = new KitRec
                {
                    type = type,
                    block = block,
                    status = "assets-ready",
                    container = $"{SharedRootName}/{BucketName}/{block}/{type.ToUpperInvariant()}_{block}",
                    asset = defaultAsset,
                    size = new[] { 0f, 0f, 0f },
                    pos = new[] { 0f, 0f, 0f },
                    surface = new SurfaceRec(),
                    mesh = new MeshRec(),
                    texture = new TexRec(),
                    note = spawn ? "入口/起飞台" : "终点核心",
                };
                kits.Add(rec);
            }
            rec.block = block;
            rec.status = "assets-ready";

            // 1) 资产定位：manifest 优先，否则扫描目录取最新
            string asset = string.IsNullOrEmpty(rec.asset) ? defaultAsset : rec.asset;
            string srcFbx = rec.mesh != null ? rec.mesh.sourceFbx : null;
            string srcTex = rec.texture != null ? rec.texture.sourceDir : null;
            string fbxRoot = Directory.Exists(sourceRoot) ? sourceRoot : null;
            string texRootUse = Directory.Exists(texRoot) ? texRoot : null;

            if (string.IsNullOrEmpty(srcFbx) || !File.Exists(srcFbx))
            {
                srcFbx = fbxRoot != null ? Path.Combine(fbxRoot, asset + ".fbx") : null;
            }
            if (srcFbx == null || !File.Exists(srcFbx))
            {
                srcFbx = FindNewestKit(fbxRoot, spawn ? "SPAWN_KIT_" : "GOAL_KIT_");
                if (srcFbx != null) asset = Path.GetFileNameWithoutExtension(srcFbx);
            }
            if (srcFbx == null || !File.Exists(srcFbx))
            {
                Skip(skips, rep, block, $"找不到 FBX（{asset}.fbx）");
                rec.status = "skipped";
                return 0;
            }
            rec.asset = asset;

            if (string.IsNullOrEmpty(srcTex) || !Directory.Exists(srcTex))
                srcTex = texRootUse != null ? Path.Combine(texRootUse, asset) : null;
            if (srcTex == null || !Directory.Exists(srcTex))
            {
                Skip(skips, rep, block, $"找不到贴图目录（{asset}）");
                rec.status = "skipped";
                return 0;
            }

            // 2) 场景定位：区块根 -> 最大可行走面
            var box = FindBlockRoot(block);
            if (box == null)
            {
                Skip(skips, rep, block, "找不到 BOX_S_0x 根节点");
                rec.status = "skipped";
                return 0;
            }
            if (!TryInferSurface(box, out var surface, out var blockBounds, out var why))
            {
                Skip(skips, rep, block, why);
                rec.status = "skipped";
                return 0;
            }

            // 3) 导入 mesh（干跑只按 manifest / 资产名取尺寸，不复制到 Assets）
            Mesh mesh = null;
            string meshAssetPath = rec.mesh != null ? rec.mesh.assetPath : null;
            Vector3 kitSize;
            if (dry)
            {
                kitSize = FallbackSize(asset, rec);
            }
            else
            {
                if (string.IsNullOrEmpty(meshAssetPath))
                    meshAssetPath = spawn
                        ? $"Assets/Art/Environment/SpawnGoal/Spawn/{asset}.fbx"
                        : $"Assets/Art/Environment/SpawnGoal/Goal/{asset}.fbx";
                mesh = ImportMesh(srcFbx, meshAssetPath);
                if (mesh == null)
                {
                    Skip(skips, rep, block, "FBX 导入后没有 Mesh");
                    rec.status = "skipped";
                    return 0;
                }
                kitSize = mesh.bounds.size;
            }

            var pl = ComputePlacement(surface, blockBounds, block, spawn, kitSize);
            if (pl == null)
            {
                Skip(skips, rep, block, "包围盒推断失败（长轴/尺寸不可用）");
                rec.status = "skipped";
                return 0;
            }

            rec.size = new[] { kitSize.x, kitSize.y, kitSize.z };
            rec.pos = new[] { pl.pos.x, pl.pos.y, pl.pos.z };
            rec.yaw = pl.yaw;
            rec.surface = new SurfaceRec { source = surface.source, topY = surface.topY, area = surface.area };
            rec.orientationBasis = pl.basis;
            rec.container = $"{SharedRootName}/{BucketName}/{block}/{type.ToUpperInvariant()}_{block}";

            rep.AppendLine($"{block} {type}: 面 {surface.source} topY={surface.topY:F2} area={surface.area:F1}m² " +
                           $"-> pos={V(pl.pos)} yaw={pl.yaw:F0} size={kitSize.x:F2}x{kitSize.y:F2}x{kitSize.z:F2} " +
                           $"dir={pl.basis}");

            if (dry) return 1;

            // 4) 贴图 + 材质 + 场景对象
            string stem = asset;
            string texAssetDir = spawn
                ? $"Assets/Art/Images/SpawnGoal/Spawn/{stem}"
                : $"Assets/Art/Images/SpawnGoal/Goal/{stem}";
            string matAssetPath = spawn
                ? $"Assets/Art/Materials/MapSkin/SpawnGoal/Spawn/{stem}.mat"
                : $"Assets/Art/Materials/MapSkin/SpawnGoal/Goal/{stem}.mat";

            if (!ImportTextures(srcTex, texAssetDir, stem))
            {
                Skip(skips, rep, block, "贴图复制/导入失败");
                rec.status = "skipped";
                return 0;
            }
            var mat = EnsureMaterial(texAssetDir, matAssetPath, stem);
            if (mat == null)
            {
                Skip(skips, rep, block, "材质 CopyAsset / 参数设置失败");
                rec.status = "skipped";
                return 0;
            }

            var bucket = EnsureBucket();
            var blockRoot = EnsureBlockContainer(bucket, block);
            string objName = $"{type.ToUpperInvariant()}_{block}";
            var old = blockRoot.Find(objName);
            if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);

            var go = new GameObject(objName);
            go.transform.SetParent(blockRoot, false);
            go.transform.position = pl.pos;
            go.transform.rotation = Quaternion.Euler(0f, pl.yaw, 0f);
            go.transform.localScale = Vector3.one;

            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = mesh;
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.On;
            mr.receiveShadows = true;
            // 纯视觉件：不加 Collider

            rec.status = "placed";
            rec.mesh = new MeshRec
            {
                sourceFbx = Normalize(srcFbx),
                assetPath = meshAssetPath,
                vertices = mesh.vertexCount,
                triangles = mesh.triangles.Length / 3,
                nonManifoldEdges = rec.mesh != null ? rec.mesh.nonManifoldEdges : 0,
                boundaryEdges = rec.mesh != null ? rec.mesh.boundaryEdges : 0,
                faces = rec.mesh != null ? rec.mesh.faces : 0,
            };
            rec.texture = new TexRec
            {
                sourceDir = Normalize(srcTex),
                assetDir = texAssetDir,
                channels = Channels,
                mmPerPx = rec.texture != null ? rec.texture.mmPerPx : 5f,
                resolution = rec.texture != null ? rec.texture.resolution : new int[0],
                sheet = rec.texture != null ? rec.texture.sheet : new float[0],
                canvas = rec.texture != null ? rec.texture.canvas : "top=front elevation, bottom=plan",
            };
            rec.material = matAssetPath;
            rep.AppendLine($"    -> {rec.container} verts={rec.mesh.vertices} tris={rec.mesh.triangles}");
            return 1;
        }

        static void Skip(List<SkipRec> skips, StringBuilder rep, string block, string why)
        {
            skips.Add(new SkipRec { block = block, reason = why });
            rep.AppendLine($"{block}: 跳过——{why}");
            Debug.LogWarning($"[SpawnGoal] {block}: 跳过——{why}");
        }

        // ------------------------------------------------------------------ 场景 / 区块定位

        static bool EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return true;

            if (cur.IsValid() && cur.isDirty && !string.IsNullOrEmpty(cur.path))
            {
                Debug.LogWarning("[SpawnGoal] 当前场景有未保存改动，不替你切换场景；请先保存后重跑。");
                return false;
            }

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log($"[SpawnGoal] 已打开 {ScenePath}");
            return true;
        }

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
                    if (IsUnderShared(t)) continue;
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

        // ------------------------------------------------------------------ 可行走面推断（与 GateKit 同口径）

        sealed class SurfaceInfo
        {
            public Bounds bounds;
            public float topY;
            public float area;
            public string source;
        }

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

                if (b.size.y < 0.05f) continue;
                float topArea = b.size.x * b.size.z;
                float shortSide = Mathf.Min(b.size.x, b.size.z);
                if (topArea < 1.5f || shortSide < 0.6f) continue;
                float aspect = Mathf.Max(b.size.x, b.size.z) / Mathf.Max(0.01f, shortSide);
                if (aspect > 12f) continue;

                cands.Add(new SurfaceInfo { bounds = b, topY = b.max.y, area = topArea, source = r.name });
            }

            if (!hasBlock) { why = "没有可用于推断边界的地编原始几何"; return false; }
            if (cands.Count == 0) { why = "没有满足面积/长宽比条件的水平面"; return false; }

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

        // ------------------------------------------------------------------ 位置 / 朝向

        sealed class Placement
        {
            public Vector3 pos;
            public float yaw;
            public string basis;
        }

        /// <summary>
        /// 长轴负端 = 上游、正端 = 下游。出生点放在上游端内缩，终点放在下游端内缩。
        /// 前进方向优先取 GATE_Manifest 的 Entrance->Exit；缺失时退回 +长轴。
        /// 套件本地 +Z = 前进方向；Goal 的正面（-Z）因此朝向跑来的玩家。
        /// </summary>
        static Placement ComputePlacement(SurfaceInfo surface, Bounds block, string blockName,
                                          bool spawn, Vector3 kitSize)
        {
            var b = surface.bounds;
            if (b.size.x <= 0.1f && b.size.z <= 0.1f) return null;

            bool alongX = b.size.x >= b.size.z;
            float longSize = alongX ? b.size.x : b.size.z;
            float shortSize = alongX ? b.size.z : b.size.x;
            float kitLong = kitSize.z;            // 本地 Z = 进深 D
            float kitWide = kitSize.x;

            string basis;
            int sign = RouteSign(blockName, alongX, out basis);

            float inset = Mathf.Clamp(kitLong * 0.85f, 0.5f, Mathf.Max(0.5f, longSize * 0.40f));
            float upstream = alongX
                ? (sign > 0 ? b.min.x : b.max.x)
                : (sign > 0 ? b.min.z : b.max.z);
            float downstream = alongX
                ? (sign > 0 ? b.max.x : b.min.x)
                : (sign > 0 ? b.max.z : b.min.z);

            float coord = spawn ? upstream + sign * inset : downstream - sign * inset;
            var center = b.center;
            center.y = surface.topY + 0.01f;
            if (alongX) center.x = coord; else center.z = coord;
            center.y = RefineY(center, surface.topY);

            if (shortSize < kitWide + 0.5f)
            {
                Debug.LogWarning($"[SpawnGoal] {blockName}: 可行走面短边 {shortSize:F2}m < 套件宽 {kitWide:F2}m，可能压边。");
            }
            if (longSize < kitLong * 1.7f)
            {
                Debug.LogWarning($"[SpawnGoal] {blockName}: 可行走面长边 {longSize:F2}m 偏短（套件进深 {kitLong:F2}m）。");
            }

            float yaw = alongX ? (sign > 0 ? 90f : -90f) : (sign > 0 ? 0f : 180f);
            return new Placement { pos = center, yaw = yaw, basis = basis };
        }

        static int RouteSign(string block, bool alongX, out string basis)
        {
            basis = "bbox-long-axis";
            var doc = LoadGates();
            if (doc != null && doc.gates != null)
            {
                GateRec ent = null, exit = null;
                foreach (var g in doc.gates)
                {
                    if (g == null || g.block != block || g.pos == null || g.pos.Length < 3) continue;
                    if (g.type == "Entrance") ent = g;
                    else if (g.type == "Exit") exit = g;
                }
                if (ent != null && exit != null)
                {
                    float d = alongX ? exit.pos[0] - ent.pos[0] : exit.pos[2] - ent.pos[2];
                    if (Mathf.Abs(d) > 0.5f)
                    {
                        basis = "GATE_Manifest";
                        return d > 0f ? 1 : -1;
                    }
                }
            }
            return 1;
        }

        static float RefineY(Vector3 pos, float fallbackTopY)
        {
            var origin = new Vector3(pos.x, fallbackTopY + 0.60f, pos.z);
            if (Physics.Raycast(origin, Vector3.down, out var hit, 1.6f, ~0, QueryTriggerInteraction.Ignore))
                return hit.point.y + 0.005f;
            return fallbackTopY + 0.005f;
        }

        // ------------------------------------------------------------------ 资产导入

        static Mesh ImportMesh(string srcFbx, string assetPath)
        {
            EnsureFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/'));
            if (!File.Exists(assetPath) || File.GetLastWriteTimeUtc(srcFbx) > File.GetLastWriteTimeUtc(assetPath))
                File.Copy(srcFbx, assetPath, true);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            return LoadMesh(assetPath);
        }

        static Mesh LoadMesh(string assetPath)
        {
            return AssetDatabase.LoadAllAssetsAtPath(assetPath)
                .OfType<Mesh>()
                .OrderByDescending(m => m.vertexCount)
                .FirstOrDefault();
        }

        /// <summary>干跑用的尺寸来源：manifest 记录 > 资产名里的 W/H/D > 本套件默认值。</summary>
        static Vector3 FallbackSize(string asset, KitRec rec)
        {
            if (rec != null && rec.size != null && rec.size.Length >= 3 && rec.size[0] > 0.1f)
                return new Vector3(rec.size[0], rec.size[1], rec.size[2]);

            var m = Regex.Match(asset ?? string.Empty, @"W([0-9.]+)_H([0-9.]+)_D([0-9.]+)");
            if (m.Success)
            {
                float w = ParseF(m.Groups[1].Value);
                float h = ParseF(m.Groups[2].Value);
                float d = ParseF(m.Groups[3].Value);
                if (w > 0.1f && h > 0.1f && d > 0.1f) return new Vector3(w, h, d);
            }
            return asset != null && asset.StartsWith("GOAL_", StringComparison.Ordinal)
                ? new Vector3(11f, 8.5f, 7.5f)
                : new Vector3(10f, 7.4f, 6.5f);
        }

        static float ParseF(string s) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

        static bool ImportTextures(string srcDir, string assetDir, string stem)
        {
            if (!Directory.Exists(srcDir)) return false;
            EnsureFolder(assetDir);
            bool any = false;
            foreach (var ch in Channels)
            {
                string src = Path.Combine(srcDir, $"{stem}_{ch}.png");
                string dst = $"{assetDir}/{stem}_{ch}.png";
                if (!File.Exists(src)) continue;
                if (!File.Exists(dst) || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dst))
                    File.Copy(src, dst, true);
                any = true;
            }
            if (!any) return false;
            AssetDatabase.ImportAsset(assetDir, ImportAssetOptions.ImportRecursive);

            SetImp($"{assetDir}/{stem}_BC.png", TextureImporterType.Default, false);
            SetImp($"{assetDir}/{stem}_N.png", TextureImporterType.NormalMap, true);
            foreach (var suf in new[] { "R", "M", "AO", "H", "MS", "ORM" })
                SetImp($"{assetDir}/{stem}_{suf}.png", TextureImporterType.Default, true);
            SetImp($"{assetDir}/{stem}_E.png", TextureImporterType.Default, false);
            // BC 是材质的必要条件；缺它就当导入失败，避免放一个无贴图的白件
            return File.Exists($"{assetDir}/{stem}_BC.png");
        }

        static void SetImp(string path, TextureImporterType type, bool linear)
        {
            if (!File.Exists(path)) return;
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return;
            ti.textureType = type;
            ti.sRGBTexture = !linear;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.maxTextureSize = 4096;
            ti.mipmapEnabled = true;
            ti.SaveAndReimport();
        }

        static Material EnsureMaterial(string texAssetDir, string matAssetPath, string stem)
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(matAssetPath) == null)
            {
                EnsureFolder(Path.GetDirectoryName(matAssetPath).Replace('\\', '/'));
                if (!AssetDatabase.CopyAsset(NprMother, matAssetPath))
                {
                    Debug.LogError($"[SpawnGoal] CopyAsset 失败: {NprMother} -> {matAssetPath}");
                    return null;
                }
                AssetDatabase.ImportAsset(matAssetPath);
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(matAssetPath);
            if (mat == null) return null;

            var bc = AssetDatabase.LoadAssetAtPath<Texture2D>($"{texAssetDir}/{stem}_BC.png");
            var nm = AssetDatabase.LoadAssetAtPath<Texture2D>($"{texAssetDir}/{stem}_N.png");
            var ms = AssetDatabase.LoadAssetAtPath<Texture2D>($"{texAssetDir}/{stem}_MS.png");
            var ao = AssetDatabase.LoadAssetAtPath<Texture2D>($"{texAssetDir}/{stem}_AO.png");
            var em = AssetDatabase.LoadAssetAtPath<Texture2D>($"{texAssetDir}/{stem}_E.png");

            mat.SetTexture("_BaseMap", bc);
            mat.SetTexture("_BumpMap", nm);
            mat.SetFloat("_BumpScale", 1f);
            mat.SetTexture("_MetallicGlossMap", ms);
            mat.SetTexture("_OcclusionMap", ao);
            mat.SetFloat("_OcclusionStrength", 1f);
            mat.SetTexture("_EmissionMap", em);
            mat.SetFloat("_EmissionEnabled", 1f);
            mat.SetFloat("_EmissionUseMap", 1f);
            mat.SetColor("_EmissionColor", Color.white);
            mat.SetFloat("_EmissionIntensity", 1.2f);

            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 1f);
            mat.SetFloat("_SmoothnessTextureChannel", 0f);
            mat.SetFloat("_Surface", 0f);
            mat.SetFloat("_AlphaClip", 0f);
            mat.SetColor("_ShadowColor", new Color(0.30f, 0.48f, 0.53f));
            mat.SetFloat("_ToonSoftness", 0.15f);
            mat.SetFloat("_ToonThreshold", 1.0f);

            mat.SetTextureScale("_BaseMap", new Vector2(1f, 1f));
            mat.SetTextureOffset("_BaseMap", Vector2.zero);
            mat.SetTextureScale("_BumpMap", new Vector2(1f, 1f));
            mat.SetTextureOffset("_BumpMap", Vector2.zero);
            mat.SetTextureScale("_MetallicGlossMap", new Vector2(1f, 1f));
            mat.SetTextureOffset("_MetallicGlossMap", Vector2.zero);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static string FindNewestKit(string root, string prefix)
        {
            if (root == null || !Directory.Exists(root)) return null;
            return Directory.GetFiles(root, prefix + "*.fbx")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        // ------------------------------------------------------------------ 场景容器

        static Transform EnsureBucket()
        {
            var shared = GameObject.Find(SharedRootName);
            if (shared == null)
            {
                shared = new GameObject(SharedRootName);
                Debug.Log($"[SpawnGoal] 新建公共容器 {SharedRootName}");
            }
            var bucket = shared.transform.Find(BucketName);
            if (bucket == null)
            {
                var go = new GameObject(BucketName);
                go.transform.SetParent(shared.transform, false);
                bucket = go.transform;
            }
            return bucket;
        }

        static Transform EnsureBlockContainer(Transform bucket, string block)
        {
            var t = bucket.Find(block);
            if (t == null)
            {
                var go = new GameObject(block);
                go.transform.SetParent(bucket, false);
                t = go.transform;
            }
            return t;
        }

        // ------------------------------------------------------------------ manifest

        static string AuditDir()
        {
            string best = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "_session_audit"));
            if (Directory.Exists(best)) return best;
            string alt = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "_session_audit"));
            if (Directory.Exists(alt)) return alt;
            Directory.CreateDirectory(best);
            return best;
        }

        static KitDoc LoadManifest()
        {
            string path = Path.Combine(AuditDir(), ManifestFile);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[SpawnGoal] 找不到 {path}，将按默认资产名与目录扫描放置。");
                return null;
            }
            try
            {
                var doc = JsonUtility.FromJson<KitDoc>(File.ReadAllText(path));
                if (doc != null) return doc;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SpawnGoal] 解析 manifest 失败: {e.Message}");
            }
            return null;
        }

        static GateDoc LoadGates()
        {
            string path = Path.Combine(AuditDir(), "GATE_Manifest.json");
            if (!File.Exists(path)) return null;
            try { return JsonUtility.FromJson<GateDoc>(File.ReadAllText(path)); }
            catch { return null; }
        }

        static void WriteManifest(KitDoc doc, string file)
        {
            string path = Path.Combine(AuditDir(), file);
            try
            {
                File.WriteAllText(path, JsonUtility.ToJson(doc, true), new UTF8Encoding(false));
                Debug.Log($"[SpawnGoal] manifest -> {path}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SpawnGoal] manifest 写盘失败: {e.Message}");
            }
        }

        // ------------------------------------------------------------------ 工具

        static string V(Vector3 v) =>
            string.Format(CultureInfo.InvariantCulture, "({0:F2},{1:F2},{2:F2})", v.x, v.y, v.z);

        static string Normalize(string p) => string.IsNullOrEmpty(p) ? p : p.Replace('\\', '/');

        static void EnsureFolder(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            assetPath = assetPath.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(assetPath)) return;
            var parts = assetPath.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
