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
    /// <summary>
    /// 面组 UV 归一化：把每个“连通、共面、同法线主轴”的面组单独铺满 0–1。
    ///
    /// 为什么需要：旧的世界三平面 UV（MapSkinUvBake）得到的是“世界坐标 / 基准尺度”的平铺 UV，
    /// 任何小于贴图格（3.6m / 4.8m）的面都会采样到贴图的随机子矩形 —— 线条半途被截断、墙角随机接缝、
    /// 白色梁上出现半截圆弧。用户已经明确否定这种做法。
    /// 本工具改口径为：**一个面 = 一张完整设计**。每个连通面组按自身 2D 包围盒归一化到 0–1，
    /// 与贴图格大小无关；配合按尺寸出图的定制贴图（T_CUSTOM_*）即可得到“边缘对齐”的整面设计。
    ///
    /// 面组定义（以下同时满足才算一组）：
    ///   · 法线主轴相同（±X / ±Y / ±Z）；
    ///   · 法线符号相同（+X 与 -X 不混组，避免正反面共用包围盒）；
    ///   · 共面（平面偏移按 1mm 量化）；
    ///   · 三角形之间通过**共享边**连通（顶点位置按 0.2mm 焊接，兼容硬边拆点/重复顶点）。
    /// 只共享一个角点的两个面不会并组（避免 L 形包围盒把两组拉伸到一起）。
    ///
    /// 朝向一致性：UV 轴按“从物体外侧看”的右手系固定映射（见 UvAxes）：
    ///   +X: u=-Z, v=+Y    -X: u=+Z, v=+Y
    ///   +Y: u=+X, v=-Z    -Y: u=+X, v=+Z
    ///   +Z: u=+X, v=+Y    -Z: u=-X, v=+Y
    /// 同一物体的同向面永远用同一套轴，不会因为三角形顶点顺序/包围盒取最小最大而随机镜像；
    /// 正反面各用镜像轴，从外侧看文字不翻。
    ///
    /// 顶点必须按面组拆分：立方体一个角点同时属于 3 个面，而一个顶点只能有一份 UV0。
    /// 本工具重建顶点缓冲（position/normal/tangent/全部 UV 通道/顶点色逐位复制），三角形索引跟着重映射。
    ///
    /// 原 UV 备份（与 MapSkinUvBake 同口径）：
    ///   · 优先备份进 UV1（mesh.uv2）；被占用则退到 UV2（mesh.uv3）；两个都被占用 → 跳过并记原因；
    ///   · 源 mesh 没有 UV0 时不需要备份，通道记 B0；
    ///   · 生成 mesh 名形如 FACEUV_B1_<源名>_<路径哈希>_p<x>_<y>_<z>，
    ///     B1/B2/B0 是备份通道标记（Clear / Restore 都靠它判断，不猜）。
    ///
    /// 幂等：mesh 名以 FACEUV_ 开头 → 跳过；`-rebuild 1` 可在原产物上重算（沿用原备份通道）。
    ///
    /// 菜单：MapSkin/⑦ 面组 UV 归一化·干跑(只报数)
    ///       MapSkin/⑦ 面组 UV 归一化·执行(写盘)
    ///       MapSkin/⑦ 面组 UV 归一化·清除(恢复原 mesh、删副本)
    /// 批处理：-executeMethod MapSkin.MapSkinFaceUv.DryRun / Run / Clear
    /// 参数：-scene <场景路径>  -only <对象名前缀>  -margin <0..0.45>  -max <三角形上限，0=不限>
    ///       -rebuild 1
    /// </summary>
    public static class MapSkinFaceUv
    {
        const string DefaultScene = "Assets/Scenes/Level1.unity";
        const string OutDir = "Assets/Art/EnvSkins/FaceUV";
        const string BakedPrefix = "FACEUV_";
        const string ManifestPath = OutDir + "/faceuv_backup.json";

        /// <summary>共面判定容差（米）。灰盒件是硬边体块，1mm 足够把同一面并组，又不会把 1mm 薄板两面并掉。</summary>
        const float PlaneTol = 0.001f;
        /// <summary>顶点位置焊接容差（米）。硬边拆点、FBX 浮点误差都在这以内。</summary>
        const float WeldTol = 0.0002f;

        /// <summary>UV 面内包围盒判定：小于这个尺寸视为退化面，该轴取 0.5。</summary>
        const float MinFaceSize = 1e-6f;

        static string ScenePath = DefaultScene;
        static string OnlyPrefix = null;      // null = 全部
        static float Margin = 0f;             // UV 内缩量（0..0.45）
        static int MaxTris = 0;               // 0 = 不限
        static bool Rebuild = false;          // -rebuild 1：在 FACEUV_ 产物上重算

        // ---------------------------------------------------------------- 数据结构

        /// <summary>量化后的世界位置键（0.2mm 焊接）。</summary>
        readonly struct PosKey : IEquatable<PosKey>, IComparable<PosKey>
        {
            public readonly long X, Y, Z;

            public PosKey(Vector3 p)
            {
                X = (long)Math.Round(p.x / WeldTol);
                Y = (long)Math.Round(p.y / WeldTol);
                Z = (long)Math.Round(p.z / WeldTol);
            }

            public bool Equals(PosKey o) => X == o.X && Y == o.Y && Z == o.Z;

            public override bool Equals(object obj) => obj is PosKey o && Equals(o);

            public override int GetHashCode()
            {
                unchecked
                {
                    long h = X * 73856093L ^ Y * 19349663L ^ Z * 83492791L;
                    return (int)(h ^ (h >> 32));
                }
            }

            public int CompareTo(PosKey o)
            {
                int c = X.CompareTo(o.X);
                if (c != 0) return c;
                c = Y.CompareTo(o.Y);
                return c != 0 ? c : Z.CompareTo(o.Z);
            }
        }

        /// <summary>面平面键：法线主轴 + 符号 + 平面偏移（1mm 量化）。</summary>
        readonly struct PlaneKey : IEquatable<PlaneKey>
        {
            public readonly int Axis;     // 0=X 1=Y 2=Z
            public readonly int Sign;     // +1 / -1
            public readonly long Offset;  // dot(normal, point) / PlaneTol

            public PlaneKey(int axis, int sign, float offset)
            {
                Axis = axis;
                Sign = sign;
                Offset = (long)Math.Round(offset / PlaneTol);
            }

            public bool Equals(PlaneKey o) => Axis == o.Axis && Sign == o.Sign && Offset == o.Offset;

            public override bool Equals(object obj) => obj is PlaneKey o && Equals(o);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = Axis * 397;
                    h = h * 397 + Sign;
                    h = h * 397 + Offset.GetHashCode();
                    return h;
                }
            }
        }

        /// <summary>无向边键（两个焊接位置，按字典序排序）。只有同一 PlaneKey 下的边才参与并组。</summary>
        readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            public readonly PosKey A, B;

            public EdgeKey(PosKey a, PosKey b)
            {
                if (a.CompareTo(b) <= 0) { A = a; B = b; }
                else { A = b; B = a; }
            }

            public bool Equals(EdgeKey o) => A.Equals(o.A) && B.Equals(o.B);

            public override bool Equals(object obj) => obj is EdgeKey o && Equals(o);

            public override int GetHashCode()
            {
                unchecked { return (A.GetHashCode() * 397) ^ B.GetHashCode(); }
            }
        }

        readonly struct PlaneEdgeKey : IEquatable<PlaneEdgeKey>
        {
            public readonly PlaneKey Plane;
            public readonly EdgeKey Edge;

            public PlaneEdgeKey(PlaneKey plane, EdgeKey edge) { Plane = plane; Edge = edge; }

            public bool Equals(PlaneEdgeKey o) => Plane.Equals(o.Plane) && Edge.Equals(o.Edge);

            public override bool Equals(object obj) => obj is PlaneEdgeKey o && Equals(o);

            public override int GetHashCode()
            {
                unchecked { return (Plane.GetHashCode() * 397) ^ Edge.GetHashCode(); }
            }
        }

        class GroupInfo
        {
            public int Axis;
            public int Sign;
            public float MinA, MaxA, MinB, MaxB;
            public bool Tilted;         // 法线与主轴夹角 > ~10°（斜切/倒角面）
            public int Triangles;       // 本组三角形数
        }

        /// <summary>一个对象的面组分析结果（DryRun 只留统计，Run 还要用它建网格）。</summary>
        class FaceGroups
        {
            public int[] TriGroup;              // 每个平铺三角形 → 面组下标；-1 = 退化三角形
            public List<GroupInfo> Groups;
            public Vector3[] World;             // 每个原顶点的世界位置
            public int DegenerateTris;
            public int TiltedGroups;
            public int ZeroSizeGroups;
            public int Triangles;
        }

        class Item
        {
            public MeshFilter Mf;
            public Mesh Src;
            public string Name;
            public string ObjectPath;
            public string GlobalId;
            public int Verts;
            public int Tris;
            public int BackupChannel;           // 1=UV1, 2=UV2, 0=无原 UV（无需备份）
            public bool RebuildItem;            // 源 mesh 已是 FACEUV_ 产物
            public Vector2[] BackupSource;      // rebuild 时沿用原备份；新跑时为 null（用 Src.uv）
            public string Skip;                 // 非空 = 跳过原因
        }

        class Stats
        {
            public int Scanned;
            public int Processed;
            public long Tris, Vertices, NewVertices;
            public long Groups;
            public int MaxGroups;
            public string MaxGroupsName = "";
            public int Backup1, Backup2, Backup0;
            public long DegenerateTris;
            public int TiltedGroups;
            public int ZeroSizeGroups;
            public readonly Dictionary<string, int> SkipHist = new Dictionary<string, int>();
            public readonly Dictionary<string, List<string>> SkipExamples = new Dictionary<string, List<string>>();
            public readonly Dictionary<string, int> GroupHist = new Dictionary<string, int>();
            public readonly List<string> TopGroups = new List<string>();

            public void Skip(string reason, string name)
            {
                SkipHist.TryGetValue(reason, out int c);
                SkipHist[reason] = c + 1;
                if (!SkipExamples.TryGetValue(reason, out var list))
                {
                    list = new List<string>();
                    SkipExamples[reason] = list;
                }
                if (list.Count < 3) list.Add(name);
            }

            public void AddGroups(int n)
            {
                Groups += n;
                string bin = n == 1 ? "1" : n <= 3 ? "2-3" : n <= 6 ? "4-6"
                           : n <= 12 ? "7-12" : n <= 24 ? "13-24" : "25+";
                GroupHist.TryGetValue(bin, out int c);
                GroupHist[bin] = c + 1;
                if (n > MaxGroups) { MaxGroups = n; MaxGroupsName = ""; }
            }
        }

        [Serializable] public class ManifestEntry
        {
            public string globalId;
            public string objectPath;
            public string objectName;
            public Vector3 worldPos;
            public string generatedAssetPath;
            public string sourceAssetPath;   // 空 = Unity 内置 mesh
            public string sourceMeshName;
            public int backupChannel;        // 1/2/0
            public int vertices;
            public int triangles;
            public int faceGroups;
        }

        [Serializable] public class Manifest
        {
            public string tool;
            public string scene;
            public List<ManifestEntry> entries = new List<ManifestEntry>();
        }

        // ---------------------------------------------------------------- 菜单/入口

        [MenuItem("MapSkin/⑦ 面组 UV 归一化·干跑(只报数)")]
        public static void DryRunMenu() => DryRun();

        [MenuItem("MapSkin/⑦ 面组 UV 归一化·执行(写盘)")]
        public static void RunMenu()
        {
            ParseArgs();
            var items = Plan();
            int n = items.Count(i => i.Skip == null);
            long tris = items.Where(i => i.Skip == null).Sum(i => (long)i.Tris);
            if (!EditorUtility.DisplayDialog("面组 UV 归一化",
                    $"将处理 {n} 个 MeshFilter（共 {tris:N0} 三角形），每个对象生成一份 mesh 副本写入 {OutDir}/\n" +
                    $"口径：每个连通共面面组独立铺满 UV0 0–1，原 UV 备份进 UV1/UV2\n\n继续？",
                    "执行", "取消"))
            {
                Debug.Log("[FaceUV] 用户取消");
                return;
            }
            Run();
        }

        /// <summary>干跑：只统计面组与跳过原因，不写任何资产。 -executeMethod MapSkin.MapSkinFaceUv.DryRun</summary>
        public static void DryRun()
        {
            ParseArgs();
            Process(execute: false);
        }

        /// <summary>执行：生成 mesh 副本并赋回。 -executeMethod MapSkin.MapSkinFaceUv.Run</summary>
        public static void Run()
        {
            ParseArgs();
            Process(execute: true);
        }

        /// <summary>清除：把 MeshFilter 指回原 mesh，并删除本工具生成的 FACEUV_* 副本。
        /// -executeMethod MapSkin.MapSkinFaceUv.Clear</summary>
        public static void Clear()
        {
            ParseArgs();
            EnsureScene();

            var manifest = LoadManifest();
            var byGlobal = new Dictionary<string, ManifestEntry>();
            var byPath = new Dictionary<string, ManifestEntry>();
            if (manifest != null)
            {
                foreach (var e in manifest.entries)
                {
                    if (!string.IsNullOrEmpty(e.globalId)) byGlobal[e.globalId] = e;
                    if (!string.IsNullOrEmpty(e.objectPath)) byPath[e.objectPath] = e;
                }
            }

            var doomed = new HashSet<string>();     // 待删的生成 mesh 资产路径
            var stillUsed = new HashSet<string>();  // 恢复后仍被引用的资产路径
            int restored = 0, orphan = 0, noSource = 0;
            var rep = new StringBuilder();

            var filters = UnityEngine.Object.FindObjectsOfType<MeshFilter>(true);
            foreach (var mf in filters)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var mesh = mf.sharedMesh;
                if (!mesh.name.StartsWith(BakedPrefix))
                {
                    var p0 = AssetDatabase.GetAssetPath(mesh);
                    if (!string.IsNullOrEmpty(p0)) stillUsed.Add(p0);
                    continue;
                }

                string gid = GlobalId(mf.gameObject);
                string path = ObjectPath(mf.transform);
                ManifestEntry entry = null;
                if (!string.IsNullOrEmpty(gid)) byGlobal.TryGetValue(gid, out entry);
                if (entry == null) byPath.TryGetValue(path, out entry);

                if (entry != null)
                {
                    var src = LoadSourceMesh(entry);
                    if (src != null)
                    {
                        mf.sharedMesh = src;
                        restored++;
                    }
                    else
                    {
                        noSource++;
                        rep.AppendLine($"    [找不到原 mesh] {mf.gameObject.name} ← {entry.sourceAssetPath} / {entry.sourceMeshName}");
                    }
                    if (!string.IsNullOrEmpty(entry.generatedAssetPath)) doomed.Add(entry.generatedAssetPath);
                }
                else
                {
                    orphan++;
                    var p = AssetDatabase.GetAssetPath(mesh);
                    if (!string.IsNullOrEmpty(p)) doomed.Add(p);
                }
            }

            // 恢复后重新收集仍在用的资产路径，避免误删被别人引用的副本
            stillUsed.Clear();
            foreach (var mf in filters)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var p = AssetDatabase.GetAssetPath(mf.sharedMesh);
                if (!string.IsNullOrEmpty(p)) stillUsed.Add(p);
            }

            int deleted = 0;
            foreach (var p in doomed)
            {
                if (string.IsNullOrEmpty(p) || stillUsed.Contains(p)) continue;
                if (!p.StartsWith(OutDir, StringComparison.Ordinal)) continue; // 只删本工具目录
                if (AssetDatabase.DeleteAsset(p)) deleted++;
            }

            // 兜底：清掉目录里已无引用的 FACEUV_* 副本（例如中途失败留下的）
            if (AssetDatabase.IsValidFolder(OutDir))
            {
                foreach (var guid in AssetDatabase.FindAssets("t:Mesh", new[] { OutDir }))
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    if (stillUsed.Contains(p)) continue;
                    var name = Path.GetFileNameWithoutExtension(p);
                    if (name.StartsWith(BakedPrefix) && AssetDatabase.DeleteAsset(p)) deleted++;
                }
            }

            AssetDatabase.SaveAssets();
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.IsValid() && scene.isDirty) EditorSceneManager.SaveScene(scene);
            // 有恢复不了的条目就保留备份表，方便修完原 mesh 再重试；全部恢复才删表
            if (manifest != null)
            {
                if (noSource == 0 && orphan == 0) DeleteManifest();
                else Debug.LogWarning($"[FaceUV·清除] 仍有 {noSource + orphan} 件未恢复，保留备份表 {ManifestPath}");
            }

            rep.AppendLine($"[FaceUV·清除] 恢复 {restored} 件；找不到原 mesh {noSource} 件；" +
                           $"无备份表可对账 {orphan} 件；删除副本 {deleted} 个资产");
            Debug.Log(rep.ToString());
        }

        // ---------------------------------------------------------------- 计划

        static void EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return;
            if (cur.IsValid() && cur.isDirty && !string.IsNullOrEmpty(cur.path))
            {
                Debug.LogWarning($"[FaceUV] 当前场景 {cur.path} 有未保存改动，不替你切换场景；请在目标场景打开时跑本工具");
                return;
            }
            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), ScenePath)))
            {
                Debug.LogError($"[FaceUV] 场景不存在：{ScenePath}");
                return;
            }
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log($"[FaceUV] 已打开场景 {ScenePath}");
        }

        /// <summary>只做便宜的跳过判定；面组分析放到 Process 里逐对象做，避免一次性把全场顶点读进内存。</summary>
        static List<Item> Plan()
        {
            EnsureScene();
            var list = new List<Item>();
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
            {
                if (mf == null) continue;
                var src = mf.sharedMesh;
                var it = new Item
                {
                    Mf = mf,
                    Src = src,
                    Name = mf.gameObject.name,
                    ObjectPath = ObjectPath(mf.transform),
                    GlobalId = GlobalId(mf.gameObject),
                };
                list.Add(it);

                if (src == null) { it.Skip = "sharedMesh 为空"; continue; }
                it.Verts = src.vertexCount;

                if (OnlyPrefix != null && !it.Name.StartsWith(OnlyPrefix, StringComparison.Ordinal))
                { it.Skip = $"-only 前缀不匹配（{OnlyPrefix}）"; continue; }

                bool isBaked = src.name.StartsWith(BakedPrefix);
                if (isBaked && !Rebuild) { it.Skip = "已处理（mesh 名 FACEUV_ 前缀，幂等跳过）"; continue; }
                if (isBaked) it.RebuildItem = true;

                if (it.Verts < 3) { it.Skip = "顶点不足 3"; continue; }
                // 三角形计数要遍历子网格；放在便宜的跳过判定之后，避免为 -only/幂等对象白算
                it.Tris = SafeTriangleCount(src);
                if (it.Tris == 0) { it.Skip = "无三角形"; continue; }
                if (MaxTris > 0 && it.Tris > MaxTris)
                { it.Skip = $"三角形 {it.Tris:N0} > 上限 {MaxTris:N0}"; continue; }

                var uv0 = src.uv;
                bool hasUv0 = uv0 != null && uv0.Length == it.Verts;
                bool uv1Taken = HasUvChannel(src, 1);
                bool uv2Taken = HasUvChannel(src, 2);

                if (isBaked)
                {
                    // 旧版本/手改过的 FACEUV_ 网格可能没有 B0/B1/B2 标记；没有标记就不知道原 UV 在哪，
                    // 重建会静默丢掉备份，直接跳过让人工处理。
                    if (!src.name.StartsWith(BakedPrefix + "B0_") &&
                        !src.name.StartsWith(BakedPrefix + "B1_") &&
                        !src.name.StartsWith(BakedPrefix + "B2_"))
                    {
                        it.Skip = "重建时 mesh 名缺 B0/B1/B2 备份通道标记";
                        continue;
                    }
                    // 重建：备份通道由 mesh 名标记写死，不再二次搬家
                    it.BackupChannel = ParseBackupChannel(src.name);
                    it.BackupSource = it.BackupChannel == 1 ? src.uv2
                                     : it.BackupChannel == 2 ? src.uv3 : null;
                    if (it.BackupChannel > 0 &&
                        (it.BackupSource == null || it.BackupSource.Length != it.Verts))
                        it.Skip = "重建时找不到原 UV 备份（mesh 名标记与 UV 通道不一致）";
                    continue;
                }

                if (!hasUv0)
                {
                    it.BackupChannel = 0;   // 没有原 UV，无需备份
                }
                else if (!uv1Taken)
                {
                    it.BackupChannel = 1;
                }
                else if (!uv2Taken)
                {
                    it.BackupChannel = 2;
                }
                else
                {
                    it.Skip = "UV1 与 UV2 都已被占用（无处备份原 UV）";
                }
            }
            list.Sort((a, b) => b.Tris.CompareTo(a.Tris));
            return list;
        }

        static int SafeTriangleCount(Mesh m)
        {
            try
            {
                int n = 0;
                for (int s = 0; s < m.subMeshCount; s++) n += m.GetTriangles(s).Length / 3;
                return n;
            }
            catch { return 0; }
        }

        static bool HasUvChannel(Mesh m, int channel)
        {
            try
            {
                var a = channel == 1 ? m.uv2 : m.uv3;
                return a != null && a.Length == m.vertexCount && a.Length > 0;
            }
            catch { return false; }
        }

        static int ParseBackupChannel(string meshName)
        {
            if (meshName.StartsWith(BakedPrefix + "B1_")) return 1;
            if (meshName.StartsWith(BakedPrefix + "B2_")) return 2;
            if (meshName.StartsWith(BakedPrefix + "B0_")) return 0;
            return 0;
        }

        // ---------------------------------------------------------------- 执行/干跑

        static void Process(bool execute)
        {
            var items = Plan();
            var stats = new Stats { Scanned = items.Count };
            var manifest = execute ? (LoadManifest() ?? new Manifest { tool = "MapSkinFaceUv", scene = ScenePath })
                                   : null;
            var manifestByGlobal = new Dictionary<string, ManifestEntry>();
            var manifestByPath = new Dictionary<string, ManifestEntry>();
            if (manifest != null)
            {
                foreach (var e in manifest.entries)
                {
                    if (!string.IsNullOrEmpty(e.globalId)) manifestByGlobal[e.globalId] = e;
                    if (!string.IsNullOrEmpty(e.objectPath)) manifestByPath[e.objectPath] = e;
                }
            }

            if (execute) EnsureOutDir();

            int written = 0;
            var rep = new StringBuilder();
            foreach (var it in items)
            {
                if (it.Skip != null)
                {
                    stats.Skip(it.Skip, it.Name);
                    continue;
                }

                // 重建时必须在备份表里找到“最初的原 mesh”，否则 Clear 只能还原到上一轮的 FACEUV_ 副本
                ManifestEntry entry = null;
                if (execute)
                {
                    if (!string.IsNullOrEmpty(it.GlobalId)) manifestByGlobal.TryGetValue(it.GlobalId, out entry);
                    if (entry == null) manifestByPath.TryGetValue(it.ObjectPath, out entry);
                    if (it.RebuildItem && entry == null)
                    {
                        stats.Skip("重建时备份表无此对象（无法安全保留最初的原 mesh）", it.Name);
                        continue;
                    }
                }

                FaceGroups fg;
                try
                {
                    fg = Analyze(it.Src, it.Mf.transform.localToWorldMatrix);
                }
                catch (Exception e)
                {
                    string reason = "分析异常: " + e.GetType().Name;
                    stats.Skip(reason, it.Name);
                    rep.AppendLine($"    [异常] {it.Name}: {e.Message}");
                    continue;
                }

                stats.Processed++;
                stats.Tris += it.Tris;
                stats.Vertices += it.Verts;
                stats.Groups += fg.Groups.Count;
                stats.DegenerateTris += fg.DegenerateTris;
                stats.TiltedGroups += fg.TiltedGroups;
                stats.ZeroSizeGroups += fg.ZeroSizeGroups;
                stats.AddGroups(fg.Groups.Count);
                if (fg.Groups.Count >= stats.MaxGroups) stats.MaxGroupsName = it.Name;
                if (it.BackupChannel == 1) stats.Backup1++;
                else if (it.BackupChannel == 2) stats.Backup2++;
                else stats.Backup0++;
                if (stats.TopGroups.Count < 10)
                    stats.TopGroups.Add($"{it.Name}: {fg.Groups.Count} 组 / {it.Tris:N0} 三角形 / {it.Verts:N0} 顶点");

                if (!execute) continue;

                try
                {
                    Mesh dst = BuildMesh(it, fg);
                    if (dst == null) { stats.Skip("建网格失败", it.Name); continue; }
                    stats.NewVertices += dst.vertexCount;

                    var p = it.Mf.transform.position;
                    string stem = $"{BakedPrefix}B{it.BackupChannel}_{Sanitize(it.Name, 56)}_" +
                                  $"{Hash8(it.ObjectPath)}_p{Mathf.RoundToInt(p.x)}_{Mathf.RoundToInt(p.y)}_{Mathf.RoundToInt(p.z)}";
                    string assetPath = OutDir + "/" + stem + ".asset";
                    dst.name = stem;

                    var old = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                    if (old != null) AssetDatabase.DeleteAsset(assetPath);
                    AssetDatabase.CreateAsset(dst, assetPath);
                    it.Mf.sharedMesh = dst;
                    written++;

                    // 备份表：重建时保留最初的 source（否则 Clear 会还原到上一轮的 FACEUV_ 副本）
                    bool keepSource = it.RebuildItem && entry != null;
                    if (entry == null)
                    {
                        entry = new ManifestEntry();
                        manifest.entries.Add(entry);
                    }
                    entry.globalId = it.GlobalId;
                    entry.objectPath = it.ObjectPath;
                    entry.objectName = it.Name;
                    entry.worldPos = p;
                    entry.generatedAssetPath = assetPath;
                    entry.backupChannel = it.BackupChannel;
                    entry.vertices = dst.vertexCount;
                    entry.triangles = it.Tris;
                    entry.faceGroups = fg.Groups.Count;
                    if (!keepSource)
                    {
                        entry.sourceAssetPath = AssetDatabase.GetAssetPath(it.Src);
                        entry.sourceMeshName = it.Src.name;
                    }
                    if (!string.IsNullOrEmpty(it.GlobalId)) manifestByGlobal[it.GlobalId] = entry;
                    manifestByPath[it.ObjectPath] = entry;

                    rep.AppendLine($"    {it.Name}: {fg.Groups.Count} 组 / {it.Verts}v→{dst.vertexCount}v / " +
                                   $"备份→UV{it.BackupChannel} → {assetPath}");
                }
                catch (Exception e)
                {
                    stats.Skip("写盘异常: " + e.GetType().Name, it.Name);
                    rep.AppendLine($"    [写盘异常] {it.Name}: {e.Message}");
                }
            }

            if (execute)
            {
                AssetDatabase.SaveAssets();
                SaveManifest(manifest);
                var scene = EditorSceneManager.GetActiveScene();
                if (scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    if (scene.path == ScenePath) EditorSceneManager.SaveScene(scene);
                }
                rep.Insert(0, $"[FaceUV] 已写 {written} 个 mesh 副本 → {OutDir}/\n");
            }

            Debug.Log(rep + Report(stats, execute));
        }

        // ---------------------------------------------------------------- 面组分析

        static FaceGroups Analyze(Mesh src, Matrix4x4 m)
        {
            var vs = src.vertices;
            if (vs == null || vs.Length != src.vertexCount)
                throw new InvalidOperationException("mesh 顶点不可读（Read/Write 关闭？）");

            var ns = src.normals;
            bool hasN = ns != null && ns.Length == vs.Length;

            // 所有子网格拉平成一张三角形表；triGroup 下标与它对齐
            var subs = new List<int[]>();
            int triCount = 0;
            for (int s = 0; s < src.subMeshCount; s++)
            {
                var t = src.GetTriangles(s);
                subs.Add(t);
                triCount += t.Length / 3;
            }
            var tris = new int[triCount * 3];
            int w = 0;
            foreach (var t in subs)
            {
                Array.Copy(t, 0, tris, w, t.Length);
                w += t.Length;
            }

            var world = new Vector3[vs.Length];
            var vk = new PosKey[vs.Length];
            for (int i = 0; i < vs.Length; i++)
            {
                world[i] = m.MultiplyPoint3x4(vs[i]);
                vk[i] = new PosKey(world[i]);
            }

            var parent = new int[triCount];
            var rank = new byte[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }
                return x;
            }

            void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a == b) return;
                if (rank[a] < rank[b]) { parent[a] = b; }
                else if (rank[a] > rank[b]) { parent[b] = a; }
                else { parent[b] = a; rank[a]++; }
            }

            var triAxis = new sbyte[triCount];
            var triSign = new sbyte[triCount];   // 0/0 = 退化三角形
            var edgeOwner = new Dictionary<PlaneEdgeKey, int>();
            int degenerate = 0;

            for (int t = 0; t < triCount; t++)
            {
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                Vector3 a = world[i0], b = world[i1], c = world[i2];
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-16f)
                {
                    degenerate++;
                    continue;
                }
                n.Normalize();
                int axis = DominantAxis(n);
                int sign = n[axis] >= 0f ? 1 : -1;
                var pk = new PlaneKey(axis, sign, Vector3.Dot(n, a));
                triAxis[t] = (sbyte)axis;
                triSign[t] = (sbyte)sign;

                AddEdge(t, i0, i1, pk);
                AddEdge(t, i1, i2, pk);
                AddEdge(t, i2, i0, pk);
            }

            // 局部函数：同一 PlaneKey 下共享一条焊接边 -> 并组
            void AddEdge(int t, int ia, int ib, PlaneKey pk)
            {
                var ek = new PlaneEdgeKey(pk, new EdgeKey(vk[ia], vk[ib]));
                if (edgeOwner.TryGetValue(ek, out int other)) Union(t, other);
                else edgeOwner[ek] = t;
            }

            var rootToGroup = new Dictionary<int, int>();
            var groups = new List<GroupInfo>();
            var triGroup = new int[triCount];
            for (int t = 0; t < triCount; t++)
            {
                if (triAxis[t] == 0 && triSign[t] == 0) { triGroup[t] = -1; continue; }
                int root = Find(t);
                if (!rootToGroup.TryGetValue(root, out int g))
                {
                    g = groups.Count;
                    rootToGroup[root] = g;
                    groups.Add(new GroupInfo
                    {
                        Axis = triAxis[t],
                        Sign = triSign[t],
                        MinA = float.MaxValue,
                        MinB = float.MaxValue,
                        MaxA = float.MinValue,
                        MaxB = float.MinValue,
                    });
                }
                triGroup[t] = g;
                groups[g].Triangles++;
            }

            // 逐组算面内包围盒 + 倾斜审计
            for (int t = 0; t < triCount; t++)
            {
                int g = triGroup[t];
                if (g < 0) continue;
                var gi = groups[g];
                UvAxes(gi.Axis, gi.Sign, out int aa, out int sa, out int ab, out int sb);
                for (int k = 0; k < 3; k++)
                {
                    var p = world[tris[t * 3 + k]];
                    float ca = p[aa] * sa;
                    float cb = p[ab] * sb;
                    if (ca < gi.MinA) gi.MinA = ca;
                    if (ca > gi.MaxA) gi.MaxA = ca;
                    if (cb < gi.MinB) gi.MinB = cb;
                    if (cb > gi.MaxB) gi.MaxB = cb;
                }
            }

            int tilted = 0, zero = 0;
            foreach (var gi in groups)
                if (gi.MaxA - gi.MinA < MinFaceSize || gi.MaxB - gi.MinB < MinFaceSize) zero++;

            // 倾斜审计：逐三角形求 |n[axis]|，组内任一三角形的主轴分量 < 0.985（约 10°）即记为斜切面。
            // 斜切面仍然铺满 0–1（不随机截断），但会因世界轴投影而轻微拉伸 —— 报告里给出数量供判断。
            var tiltedFlag = new bool[groups.Count];
            for (int t = 0; t < triCount; t++)
            {
                int g = triGroup[t];
                if (g < 0) continue;
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                Vector3 n = Vector3.Cross(world[i1] - world[i0], world[i2] - world[i0]);
                if (n.sqrMagnitude < 1e-16f) continue;
                n.Normalize();
                if (Mathf.Abs(n[groups[g].Axis]) < 0.985f) tiltedFlag[g] = true;
            }
            for (int g = 0; g < groups.Count; g++)
                if (tiltedFlag[g]) { groups[g].Tilted = true; tilted++; }

            return new FaceGroups
            {
                TriGroup = triGroup,
                Groups = groups,
                World = world,
                DegenerateTris = degenerate,
                TiltedGroups = tilted,
                ZeroSizeGroups = zero,
                Triangles = triCount,
            };
        }

        static int DominantAxis(Vector3 n)
        {
            float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
            if (ay >= ax && ay >= az) return 1;
            return ax >= az ? 0 : 2;
        }

        /// <summary>从物体外侧看为右手系的 UV 轴映射；同一 (axis, sign) 永远同一套轴，保证同向面不镜像。</summary>
        static void UvAxes(int axis, int sign, out int aAxis, out int aSign, out int bAxis, out int bSign)
        {
            switch (axis)
            {
                case 0: // X
                    aAxis = 2;
                    aSign = sign > 0 ? -1 : 1;
                    bAxis = 1;
                    bSign = 1;
                    break;
                case 1: // Y
                    aAxis = 0;
                    aSign = 1;
                    bAxis = 2;
                    bSign = sign > 0 ? -1 : 1;
                    break;
                default: // Z
                    aAxis = 0;
                    aSign = sign > 0 ? 1 : -1;
                    bAxis = 1;
                    bSign = 1;
                    break;
            }
        }

        static Vector2 FaceUv(GroupInfo g, Vector3 world, float margin)
        {
            UvAxes(g.Axis, g.Sign, out int aa, out int sa, out int ab, out int sb);
            float ca = world[aa] * sa;
            float cb = world[ab] * sb;
            float sizeA = g.MaxA - g.MinA;
            float sizeB = g.MaxB - g.MinB;
            float u = sizeA < MinFaceSize ? 0.5f : (ca - g.MinA) / sizeA;
            float v = sizeB < MinFaceSize ? 0.5f : (cb - g.MinB) / sizeB;
            float m = Mathf.Clamp(margin, 0f, 0.45f);
            float span = 1f - 2f * m;
            return new Vector2(Mathf.Clamp01(m + u * span), Mathf.Clamp01(m + v * span));
        }

        // ---------------------------------------------------------------- 建网格

        static Mesh BuildMesh(Item it, FaceGroups fg)
        {
            var src = it.Src;
            int vc = src.vertexCount;
            var vs = src.vertices;
            if (vs == null || vs.Length != vc) return null;

            var ns = src.normals;
            bool hasN = ns != null && ns.Length == vc;
            var ts = src.tangents;
            bool hasT = ts != null && ts.Length == vc;

            // 原 UV 通道：全部保留（重建顶点后逐顶点复制）
            var srcUv = new List<Vector2>[8];
            for (int ch = 0; ch < 8; ch++)
            {
                var list = new List<Vector2>();
                src.GetUVs(ch, list);
                if (list.Count == vc) srcUv[ch] = list;
            }
            var colors = src.colors;
            bool hasColor = colors != null && colors.Length == vc;
            var colors32 = src.colors32;
            bool hasColor32 = colors32 != null && colors32.Length == vc;

            // 输出通道集合：UV0 恒有；源有数据的通道保留；备份通道补一个
            var outCh = new bool[8];
            outCh[0] = true;
            for (int ch = 1; ch < 8; ch++) outCh[ch] = srcUv[ch] != null;
            if (it.BackupChannel > 0) outCh[it.BackupChannel] = true;
            var newUv = new List<Vector2>[8];
            for (int ch = 0; ch < 8; ch++) if (outCh[ch]) newUv[ch] = new List<Vector2>(vc);

            var nv = new List<Vector3>(vc);
            var nn = hasN ? new List<Vector3>(vc) : null;
            var nt = hasT ? new List<Vector4>(vc) : null;
            var nc = hasColor ? new List<Color>(vc) : null;
            var nc32 = hasColor32 ? new List<Color32>(vc) : null;

            var map = new Dictionary<long, int>();   // (group<<32 | vertexIndex) → 新顶点
            var newTrisBySub = new List<int>[src.subMeshCount];
            for (int s = 0; s < src.subMeshCount; s++) newTrisBySub[s] = new List<int>();

            for (int s = 0; s < src.subMeshCount; s++)
            {
                var st = src.GetTriangles(s);
                for (int k = 0; k + 2 < st.Length; k += 3)
                {
                    int flatTri = SubTriOffset(src, s) + k / 3;
                    int g = flatTri < fg.TriGroup.Length ? fg.TriGroup[flatTri] : -1;
                    for (int j = 0; j < 3; j++)
                    {
                        int vi = st[k + j];
                        long key = ((long)(g + 1) << 32) | (uint)vi;
                        if (!map.TryGetValue(key, out int nvi))
                        {
                            nvi = nv.Count;
                            map[key] = nvi;
                            nv.Add(vs[vi]);
                            if (nn != null) nn.Add(ns[vi]);
                            if (nt != null) nt.Add(ts[vi]);
                            if (nc != null) nc.Add(colors[vi]);
                            if (nc32 != null) nc32.Add(colors32[vi]);

                            Vector2 uv0 = g >= 0 && g < fg.Groups.Count
                                ? FaceUv(fg.Groups[g], fg.World[vi], Margin)
                                : new Vector2(0.5f, 0.5f);
                            for (int ch = 0; ch < 8; ch++)
                            {
                                if (!outCh[ch]) continue;
                                Vector2 val;
                                if (ch == 0) val = uv0;
                                else if (ch == it.BackupChannel)
                                    val = it.BackupSource != null && it.BackupSource.Length == vc
                                        ? it.BackupSource[vi]
                                        : (srcUv[0] != null ? srcUv[0][vi] : Vector2.zero);
                                else val = srcUv[ch] != null ? srcUv[ch][vi] : Vector2.zero;
                                newUv[ch].Add(val);
                            }
                        }
                        newTrisBySub[s].Add(nvi);
                    }
                }
            }

            var dst = new Mesh
            {
                name = src.name,
                indexFormat = nv.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            dst.SetVertices(nv);
            if (nn != null) dst.SetNormals(nn);
            if (nt != null) dst.SetTangents(nt);
            for (int ch = 0; ch < 8; ch++)
                if (outCh[ch] && newUv[ch] != null && newUv[ch].Count == nv.Count)
                    dst.SetUVs(ch, newUv[ch]);
            if (nc != null) dst.colors = nc.ToArray();
            if (nc32 != null) dst.colors32 = nc32.ToArray();

            dst.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++)
                dst.SetTriangles(newTrisBySub[s], s, false);
            if (!hasN) dst.RecalculateNormals();
            dst.RecalculateBounds();
            return dst;
        }

        static int[] _subOffsets;
        static Mesh _subOffsetsMesh;

        /// <summary>子网格 → 全局三角形下标偏移（BuildMesh 里逐三角形查，缓存一份）。</summary>
        static int SubTriOffset(Mesh m, int sub)
        {
            if (_subOffsetsMesh != m || _subOffsets == null || _subOffsets.Length != m.subMeshCount + 1)
            {
                _subOffsetsMesh = m;
                _subOffsets = new int[m.subMeshCount + 1];
                for (int s = 0; s < m.subMeshCount; s++)
                    _subOffsets[s + 1] = _subOffsets[s] + m.GetTriangles(s).Length / 3;
            }
            return _subOffsets[sub];
        }

        // ---------------------------------------------------------------- 备份表

        static Manifest LoadManifest()
        {
            string abs = Path.Combine(Directory.GetCurrentDirectory(), ManifestPath);
            if (!File.Exists(abs)) return null;
            try { return JsonUtility.FromJson<Manifest>(File.ReadAllText(abs)); }
            catch (Exception e)
            {
                Debug.LogWarning($"[FaceUV] 备份表解析失败（{ManifestPath}）：{e.Message}");
                return null;
            }
        }

        static void SaveManifest(Manifest m)
        {
            if (m == null) return;
            EnsureOutDir();
            string abs = Path.Combine(Directory.GetCurrentDirectory(), ManifestPath);
            File.WriteAllText(abs, JsonUtility.ToJson(m, true), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(ManifestPath, ImportAssetOptions.ForceUpdate);
        }

        static void DeleteManifest()
        {
            string abs = Path.Combine(Directory.GetCurrentDirectory(), ManifestPath);
            if (!File.Exists(abs)) return;
            // AssetDatabase 只认识已导入的资产；未导入时用文件系统兜底
            if (!AssetDatabase.DeleteAsset(ManifestPath))
            {
                File.Delete(abs);
                string meta = abs + ".meta";
                if (File.Exists(meta)) File.Delete(meta);
                AssetDatabase.Refresh();
            }
        }

        static Mesh LoadSourceMesh(ManifestEntry e)
        {
            if (e == null) return null;
            if (!string.IsNullOrEmpty(e.sourceAssetPath))
            {
                var all = AssetDatabase.LoadAllAssetsAtPath(e.sourceAssetPath);
                if (all != null)
                {
                    foreach (var o in all)
                        if (o is Mesh m && (string.IsNullOrEmpty(e.sourceMeshName) || m.name == e.sourceMeshName))
                            return m;
                }
                var direct = AssetDatabase.LoadAssetAtPath<Mesh>(e.sourceAssetPath);
                if (direct != null) return direct;
            }
            if (!string.IsNullOrEmpty(e.sourceMeshName))
            {
                // Unity 内置图元（无资产路径）：Cube/Sphere/Capsule/Cylinder/Plane/Quad
                try
                {
                    var builtin = Resources.GetBuiltinResource<Mesh>(e.sourceMeshName + ".fbx");
                    if (builtin != null) return builtin;
                }
                catch { /* 不是内置名，继续按名字找资产 */ }

                foreach (var guid in AssetDatabase.FindAssets(e.sourceMeshName + " t:Mesh"))
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    var all = AssetDatabase.LoadAllAssetsAtPath(p);
                    if (all == null) continue;
                    foreach (var o in all)
                        if (o is Mesh m && m.name == e.sourceMeshName) return m;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- 报告

        static string Report(Stats s, bool executed)
        {
            var rep = new StringBuilder();
            rep.AppendLine($"[FaceUV·{(executed ? "执行" : "干跑")}] 扫描 MeshFilter {s.Scanned} 个；" +
                           $"可处理 {s.Processed} 个；跳过 {s.SkipHist.Values.Sum()} 个");
            rep.AppendLine($"  面组：总计 {s.Groups:N0} 组，平均 {(s.Processed > 0 ? (double)s.Groups / s.Processed : 0):F1} 组/对象，" +
                           $"最多 {s.MaxGroups} 组（{s.MaxGroupsName}）");
            rep.AppendLine($"  三角形 {s.Tris:N0}；顶点 {s.Vertices:N0}" +
                           (executed ? $" → 新顶点 {s.NewVertices:N0}" : ""));
            rep.AppendLine($"  原 UV 备份：UV1 {s.Backup1} 件 / UV2 {s.Backup2} 件 / 无需备份(B0) {s.Backup0} 件");
            rep.AppendLine($"  退化三角形 {s.DegenerateTris:N0}；零尺寸面组 {s.ZeroSizeGroups}；" +
                           $"倾斜面组（法线与主轴夹角 >10°）{s.TiltedGroups}");

            if (s.GroupHist.Count > 0)
            {
                var order = new[] { "1", "2-3", "4-6", "7-12", "13-24", "25+" };
                rep.AppendLine("  面组数直方图：" + string.Join("    ",
                    order.Where(k => s.GroupHist.ContainsKey(k))
                         .Select(k => $"{k} 组: {s.GroupHist[k]} 个对象")));
            }
            if (s.TopGroups.Count > 0)
            {
                rep.AppendLine("  面组最多 Top10：");
                foreach (var t in s.TopGroups) rep.AppendLine("    " + t);
            }
            if (s.SkipHist.Count > 0)
            {
                rep.AppendLine("  跳过原因直方图：");
                foreach (var kv in s.SkipHist.OrderByDescending(k => k.Value))
                {
                    string ex = s.SkipExamples.TryGetValue(kv.Key, out var list)
                        ? string.Join(", ", list) : "";
                    rep.AppendLine($"    [{kv.Value,5} 个] {kv.Key}    例: {ex}");
                }
            }
            return rep.ToString();
        }

        // ---------------------------------------------------------------- 杂项

        static string ObjectPath(Transform t)
        {
            var parts = new List<string>();
            while (t != null)
            {
                parts.Add(t.name + "#" + t.GetSiblingIndex());
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        static string GlobalId(GameObject go)
        {
            try { return GlobalObjectId.GetGlobalObjectIdSlow(go).ToString(); }
            catch { return ""; }
        }

        static string Sanitize(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            if (sb.Length > maxLen) sb.Length = maxLen;
            return sb.ToString();
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
                return h.ToString("x8");
            }
        }

        static void EnsureOutDir()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Art/EnvSkins"))
                AssetDatabase.CreateFolder("Assets/Art", "EnvSkins");
            if (!AssetDatabase.IsValidFolder(OutDir))
                AssetDatabase.CreateFolder("Assets/Art/EnvSkins", "FaceUV");
        }

        static void ParseArgs()
        {
            ScenePath = DefaultScene;
            OnlyPrefix = null;
            Margin = 0f;
            MaxTris = 0;
            Rebuild = false;

            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[i + 1] : null;
                if (a == "-scene") { var v = Next(); if (!string.IsNullOrEmpty(v)) { ScenePath = v; i++; } }
                else if (a == "-only" || a == "-faceuvonly") { var v = Next(); if (!string.IsNullOrEmpty(v)) { OnlyPrefix = v; i++; } }
                else if (a == "-margin" || a == "-faceuvmargin")
                {
                    if (float.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    { Margin = Mathf.Clamp(v, 0f, 0.45f); i++; }
                }
                else if (a == "-max" || a == "-faceuvmax")
                {
                    if (int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                    { MaxTris = Mathf.Max(0, v); i++; }
                }
                else if (a == "-rebuild")
                {
                    var v = Next();
                    Rebuild = v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
                    if (v != null) i++;
                }
            }
        }
    }
}
