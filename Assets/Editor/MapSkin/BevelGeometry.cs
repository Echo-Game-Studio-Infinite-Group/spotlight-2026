using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// Level1 全场景「盒状件倒角化」：把灰盒里所有盒状 MeshFilter 换成**程序化切角盒**网格。
    ///
    /// 为什么干这件事（用户点名「你连倒角都不干吗」）：
    ///   真直角在光照里只有像素级硬切变，读成 CG/廉价感；硬表面风格要求每条硬边有可见的
    ///   45° 平直切面（chamfer，不是圆角）。灰盒件绝大多数是「Unity Cube + 非均匀 scale」或
    ///   ProBuilder 抽出的 12/24 三角盒，直接给它们换一份切角盒网格，成本最低、收益最直接。
    ///
    /// 切角盒长什么样：
    ///   · 12 条棱全部一条 45° 切面；8 个角自然收成三角面。
    ///   · 面：6 个矩形 ×2 = 12 三角；棱切面：12 个四边形 ×2 = 24 三角；角：8 个三角 = 8 三角。
    ///     合计 **44 三角 / 96 顶点**（顶点按面拆开，法线是硬边 flat——绝不平滑成圆）。
    ///   · 倒角宽按世界尺寸分级：
    ///       - 主形（最大边 ≥ 0.3m）：件宽（最小边）的 11%，下限 8mm、上限 300mm；
    ///       - 小件（最大边 < 0.3m）：micro 0.75mm（0.5–1mm 中间值）；
    ///       - 再乘「不超过最小边 35%」的几何保险，避免切面把薄件压塌。
    ///   · 每条轴上的世界切宽相等 → 世界空间里就是 45°；非均匀 scale 下按
    ///     `bLocal[i] = bWorld / |lossyScale[i]|` 换算，保证世界口径仍是 45°。
    ///
    /// 选谁（盒状判据，与任务口径一致）：
    ///   1) 名字不含 `MAPSKIN_` / `DEC_`（工具生成件与分关装饰件不归本工具管）；
    ///   2) 当前 mesh 名不以 `BEVEL_` 开头（幂等）；
    ///   3) 三角形数 ≤ 64，且只有 1 个子网格（多子网格意味着材质按面分区，重建会丢分区）；
    ///   4) mesh.bounds 与渲染器世界 AABB 形状一致（按 localToWorld 变换 bounds 八角点后对比，
    ///      旋转件也支持——不是在拿世界轴对齐尺寸硬套局部尺寸）；
    ///   5) 可读 mesh 再做「顶点全在 bounds 八角点」校验，排除圆柱/楔形/斜切异形体；
    ///      不可读 mesh 只能按 bounds + 三角数放行并记警告。
    ///
    /// 不改什么（硬约束）：
    ///   · 不改对象名、位置/旋转/缩放、材质、任何 Collider；
    ///   · 切角只削角，不动 6 个原始面平面 → 新 mesh.bounds 与旧 bounds 完全一致，
    ///     BoxCollider 尺寸与 MeshCollider 引用的原 mesh 都不受影响。
    ///
    /// ProBuilder / Prefab 纪律（地编硬警告）：
    ///   · **绝不写 prefab 资产**：不调用 SaveAsPrefabAsset / SaveAsPrefabAssetAndConnect；
    ///     只改场景实例的 `MeshFilter.sharedMesh`（Prefab 实例会产生场景 override，这是允许的）。
    ///   · 带 `ProBuilderMesh` 组件的对象**允许照常替换**（替换 sharedMesh 不是导出预制体，
    ///     不会丢 PB 组件里的网格数据）；只在日志里单独标 `[pb]`，不跳过。
    ///   · 不引入/不依赖 ProBuilder 包：检测用字符串 `GetComponent("ProBuilderMesh")` + 类型名兜底，
    ///     包未安装时不会编译失败。
    ///
    /// 存哪：
    ///   `Assets/Art/EnvSkins/BEVEL_<对象名>_<W>x<H>x<D>.asset`，赋回 `MeshFilter.sharedMesh`。
    ///   原 mesh 引用与对象标识记在 `Assets/Art/EnvSkins/BEVEL_Backup.asset`（BevelBackup ScriptableObject），
    ///   Clear 按它恢复。
    ///
    /// 幂等：mesh 名以 `BEVEL_` 开头即跳过；名字冲突自动加 `_2`、`_3`。
    /// 护栏：总三角增量 ≤ 60 万（默认；-bv-budget 可改），按名字排序确定性处理，触顶即停并打印实际用量。
    ///
    /// 菜单：MapSkin/倒角/① 干跑  ② 执行  ③ 清除
    /// 批处理（均为 public static void、无参）：
    ///   -executeMethod MapSkin.BevelGeometry.DryRun   只打印：零写盘、零建资产、零改场景
    ///   -executeMethod MapSkin.BevelGeometry.Run      生成切角盒 + 写 mesh 资产 + 存场景
    ///   -executeMethod MapSkin.BevelGeometry.Clear    恢复原 mesh + 删除本工具 mesh 资产
    /// 参数：-bv-&lt;键&gt; &lt;值&gt;（键清单见 ParseArgs）
    /// </summary>
    public static class BevelGeometry
    {
        // ------------------------------------------------------------------ 常量

        static string ScenePath = "Assets/Scenes/Level1.unity";
        const string OutDir = "Assets/Art/EnvSkins";
        const string BackupPath = "Assets/Art/EnvSkins/BEVEL_Backup.asset";
        const string Prefix = "BEVEL_";

        /// <summary>UV 基准必须与 MapSkinUvBake / BuildingEnhance 一致，否则切角面与旧面的板缝对不齐。</summary>
        const float WallUvBase = 3.6f;
        const float FloorUvBase = 4.8f;

        /// <summary>切角盒固定三角数：6 面 12 + 12 棱 24 + 8 角 8 = 44。</summary>
        const int ChamferTris = 44;

        // ------------------------------------------------------------------ 参数（-bv-* 可覆盖）

        static float P_Ratio = 0.11f;          // -bv-ratio       主形倒角宽 / 件宽（0.08–0.15，取中值 0.11）
        static float P_MinBevel = 0.008f;      // -bv-min         主形倒角宽下限（m）
        static float P_MaxBevel = 0.300f;      // -bv-max         主形倒角宽上限（m）
        static float P_MicroBevel = 0.00075f;  // -bv-micro       小件 micro 倒角宽（m，0.5–1mm 中间值）
        static float P_SmallPiece = 0.30f;     // -bv-small       最大边小于此值 → 只做 micro（m）
        static float P_MinDim = 0.002f;        // -bv-mindim      最小边小于此值 → 跳过（太薄，切面必塌）
        static float P_CapRatio = 0.35f;       // -bv-cap         倒角宽 ≤ 最小边 × 此比例（几何保险）
        static int P_MaxSrcTris = 64;          // -bv-maxtris     原 mesh 三角数上限
        static long P_MaxTriDelta = 600000;    // -bv-budget      总三角增量上限
        static int P_ListLimit = 0;            // -bv-list        逐件清单最多打印多少行（0 = 全部）
        static int P_CleanOrphans = 1;         // -bv-cleanorphans 1 = Clear 时顺带删除无人引用的 BEVEL_*.asset

        // ------------------------------------------------------------------ 菜单

        [MenuItem("MapSkin/倒角/① 干跑：盒状清单/尺寸分布/三角增量(零写盘)")]
        public static void DryRunMenu() => DryRun();

        [MenuItem("MapSkin/倒角/② 执行：全场景切角盒(写盘)")]
        public static void RunMenu()
        {
            ParseArgs();
            if (!EnsureScene(false)) return;
            var all = Scan();
            var cands = all.Where(i => i.skip == null).ToList();
            long orig = cands.Sum(i => (long)i.srcTris);
            long neu = (long)cands.Count * ChamferTris;
            if (!EditorUtility.DisplayDialog("全场景倒角化",
                    $"将处理 {cands.Count} 个盒状 MeshFilter：\n" +
                    $"原三角形 {orig:N0} → 新三角形 {neu:N0}（增量 {neu - orig:N0}，上限 {P_MaxTriDelta:N0}）\n" +
                    $"每个对象生成一份切角盒 mesh 写入 {OutDir}/，并记入 {BackupPath}。\n" +
                    $"不改对象名/位置/材质/碰撞体。\n\n继续？", "执行", "取消"))
            {
                Debug.Log("[倒角] 用户取消");
                return;
            }
            Run();
        }

        [MenuItem("MapSkin/倒角/③ 清除：恢复原 mesh 并删除生成资产")]
        public static void ClearMenu()
        {
            if (!EditorUtility.DisplayDialog("清除倒角化",
                    "按 BEVEL_Backup.asset 把每个对象恢复成倒角前的原 mesh，并删除本工具生成的 BEVEL_*.asset。\n" +
                    "对象名/位置/材质/碰撞体不受影响。\n\n继续？", "恢复", "取消"))
                return;
            Clear();
        }

        // ------------------------------------------------------------------ 入口

        /// <summary>干跑：只扫描与计算，零写盘、零建资产、零改场景。
        /// 批处理：-executeMethod MapSkin.BevelGeometry.DryRun</summary>
        public static void DryRun()
        {
            ParseArgs();
            if (!EnsureScene(false)) return;
            var all = Scan();
            var rep = BuildReport(all, dryRun: true);
            Debug.Log(rep);
        }

        /// <summary>执行：生成切角盒、建 mesh 资产、赋回 MeshFilter、存场景。
        /// 批处理：-executeMethod MapSkin.BevelGeometry.Run</summary>
        public static void Run()
        {
            ParseArgs();
            if (!EnsureScene(true)) return;

            var all = Scan();
            var cands = all.Where(i => i.skip == null).ToList();

            // 预算按处理顺序（名字排序）确定性发放；触顶的件记 skip，不再生成。
            var accepted = new List<Item>();
            long usedDelta = 0;
            foreach (var it in cands)
            {
                long delta = ChamferTris - it.srcTris;
                if (usedDelta + delta > P_MaxTriDelta)
                {
                    it.skip = $"超出三角增量预算（{usedDelta + delta:N0} > {P_MaxTriDelta:N0}）";
                    continue;
                }
                usedDelta += delta;
                accepted.Add(it);
            }

            var rep = new StringBuilder();
            rep.AppendLine($"[倒角·执行] 场景 {ScenePath}；扫描 MeshFilter {all.Count} 个，盒状候选 {cands.Count} 个，预算内 {accepted.Count} 个");
            int unverified = cands.Count(i => i.hullUnverified);
            if (unverified > 0)
                rep.AppendLine($"[提示] {unverified} 个候选 mesh 不可读，未做盒状顶点校验（按 bounds+三角数放行）");
            foreach (var it in cands.Where(i => i.skip != null))
                rep.AppendLine($"  - 预算外跳过：{it.name}（{it.skip}）");
            var pbCands = accepted.Where(i => i.isPb).ToList();
            rep.AppendLine($"[ProBuilder] 检出 pb 对象 {all.Count(i => i.isPb)} 个；倒角清单内 {pbCands.Count} 个（允许替换 sharedMesh，仅标注，不跳过）");
            foreach (var it in pbCands.Take(20))
                rep.AppendLine($"  [pb] {it.name}  {Dims(it.worldSize)}m");

            if (accepted.Count == 0)
            {
                rep.AppendLine("[倒角·执行] 没有可处理的盒状件（可能已全部倒角化）。未写盘。");
                Debug.Log(rep.ToString());
                return;
            }

            var backup = LoadBackup(create: true);
            if (backup == null)
            {
                Debug.LogError($"[倒角·执行] 无法创建备份资产 {BackupPath}，已中止（未改任何对象）");
                return;
            }
            var byGlobalId = new Dictionary<string, BevelBackup.Entry>(StringComparer.Ordinal);
            foreach (var e in backup.entries)
                if (!string.IsNullOrEmpty(e.globalId)) byGlobalId[e.globalId] = e;

            EnsureFolder(OutDir);
            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int made = 0, failed = 0;
            long actualDelta = 0;

            for (int idx = 0; idx < accepted.Count; idx++)
            {
                var it = accepted[idx];
                if (!Application.isBatchMode)
                    EditorUtility.DisplayProgressBar("全场景倒角化", it.name, (idx + 1f) / accepted.Count);
                try
                {
                    string stem = Prefix + SafeName(it.name) + "_" + Dims(it.worldSize);
                    string path = UniquePath(stem, usedPaths);
                    string meshName = System.IO.Path.GetFileNameWithoutExtension(path);
                    var mesh = BuildChamferMesh(it, meshName);
                    AssetDatabase.CreateAsset(mesh, path);

                    it.genPath = path;
                    it.mf.sharedMesh = mesh;
                    MarkChanged(it.mf);

                    string gid = GlobalObjectId.GetGlobalObjectIdSlow(it.mf).ToString();
                    if (!byGlobalId.TryGetValue(gid, out var e))
                    {
                        e = new BevelBackup.Entry();
                        byGlobalId[gid] = e;
                        backup.entries.Add(e);
                    }
                    e.globalId = gid;
                    e.objectPath = it.path;
                    e.objectName = it.name;
                    e.originalMesh = it.src;
                    e.generatedMeshPath = path;
                    e.worldSize = it.worldSize;
                    e.originalTris = it.srcTris;

                    made++;
                    actualDelta += ChamferTris - it.srcTris;
                    rep.AppendLine($"  + {it.name}  {Dims(it.worldSize)}m  bevel={it.bevelWorld * 1000f:F1}mm  " +
                                   $"{it.srcTris}→{ChamferTris} tri  → {path}");
                }
                catch (Exception ex)
                {
                    failed++;
                    rep.AppendLine($"  ! {it.name} 生成失败：{ex.GetType().Name}: {ex.Message}");
                }
            }

            if (!Application.isBatchMode) EditorUtility.ClearProgressBar();
            EditorUtility.SetDirty(backup);
            AssetDatabase.SaveAssets();
            SaveScene();

            rep.AppendLine();
            rep.AppendLine($"[倒角·执行] 实际生成 {made} 件（失败 {failed}），总三角增量 {actualDelta:N0} / {P_MaxTriDelta:N0}；" +
                           $"备份条目 {backup.entries.Count} 条 → {BackupPath}；场景已存盘：{ScenePath}");
            if (failed > 0)
                rep.AppendLine("[倒角·执行] 有对象生成失败：场景里它们的 mesh 仍指向原 mesh（本次未存盘前已抛异常的对象不会留下半成品引用）");
            Debug.Log(rep.ToString());
        }

        /// <summary>清除：按备份恢复原 mesh，并删除本工具生成的 mesh 资产。
        /// 批处理：-executeMethod MapSkin.BevelGeometry.Clear</summary>
        public static void Clear()
        {
            ParseArgs();
            if (!EnsureScene(true)) return;

            var rep = new StringBuilder();
            var backup = AssetDatabase.LoadAssetAtPath<BevelBackup>(BackupPath);
            var scene = EditorSceneManager.GetActiveScene();

            // 先给场景里的 MeshFilter 建索引：globalId 优先，层级路径次之，名字最后。
            var byPath = new Dictionary<string, MeshFilter>(StringComparer.Ordinal);
            var byName = new Dictionary<string, List<MeshFilter>>(StringComparer.Ordinal);
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
            {
                if (mf == null || mf.gameObject.scene != scene) continue;
                var p = PathOf(mf.transform);
                if (!byPath.ContainsKey(p)) byPath[p] = mf;
                var n = mf.gameObject.name;
                if (!byName.TryGetValue(n, out var list)) byName[n] = list = new List<MeshFilter>();
                list.Add(mf);
            }

            int restored = 0, missing = 0;
            var assets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (backup != null)
            {
                foreach (var e in backup.entries)
                {
                    var mf = Resolve(e.globalId, e.objectPath, e.objectName, byPath, byName);
                    if (mf == null)
                    {
                        missing++;
                        rep.AppendLine($"  ? 找不到对象：{e.objectName}  ({e.objectPath})");
                        continue;
                    }
                    if (e.originalMesh != null)
                    {
                        mf.sharedMesh = e.originalMesh;
                        MarkChanged(mf);
                        if (!string.IsNullOrEmpty(e.generatedMeshPath)) assets.Add(e.generatedMeshPath);
                    }
                    else
                    {
                        // 备份没有原 mesh 引用：不删生成资产，避免把仍在引用的 mesh 删成 Missing
                        rep.AppendLine($"  ! 备份缺少原 mesh：{e.objectName}（{e.objectPath}），保留其生成资产");
                    }
                    restored++;
                }
            }

            int deleted = 0;
            foreach (var p in assets)
            {
                if (AssetDatabase.LoadAssetAtPath<Mesh>(p) != null)
                {
                    AssetDatabase.DeleteAsset(p);
                    deleted++;
                }
            }

            int orphans = 0;
            if (P_CleanOrphans == 1)
            {
                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
                {
                    if (mf == null || mf.sharedMesh == null) continue;
                    if (!mf.sharedMesh.name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                    var p = AssetDatabase.GetAssetPath(mf.sharedMesh);
                    if (!string.IsNullOrEmpty(p)) referenced.Add(p);
                }
                foreach (var guid in AssetDatabase.FindAssets("t:Mesh", new[] { OutDir }))
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(p)) continue;
                    if (!p.StartsWith(OutDir + "/" + Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (referenced.Contains(p) || assets.Contains(p)) continue;
                    AssetDatabase.DeleteAsset(p);
                    orphans++;
                }
            }

            // 备份表清空（资产保留，便于下一次 Run 直接复用同一路径）
            if (backup != null)
            {
                backup.entries.Clear();
                EditorUtility.SetDirty(backup);
            }
            AssetDatabase.SaveAssets();
            SaveScene();

            // 仍未恢复的 BEVEL_ 件（备份表缺记录）要明确报出来
            int unresolved = 0;
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (mf.sharedMesh.name.StartsWith(Prefix, StringComparison.Ordinal)) unresolved++;
            }

            rep.AppendLine($"[倒角·清除] 恢复 {restored} 件，找不到 {missing} 件；删除生成 mesh 资产 {deleted} 个" +
                           (P_CleanOrphans == 1 ? $"（另清理无人引用 orphan {orphans} 个）" : "") +
                           $"；场景已存盘：{ScenePath}");
            if (unresolved > 0)
                rep.AppendLine($"[倒角·清除] 警告：场景里仍有 {unresolved} 个 BEVEL_ mesh 引用没有备份记录，无法自动恢复" +
                               "（先跑 DryRun 把清单发出来，再人工处理）。");
            Debug.Log(rep.ToString());
        }

        // ------------------------------------------------------------------ 扫描 / 判定

        class Item
        {
            public MeshFilter mf;
            public Renderer r;
            public Mesh src;
            public string name;
            public string path;
            public string skip;             // 非空 = 跳过原因
            public string tier;             // "main" / "micro"
            public Bounds localBounds;
            public Vector3 worldSize;
            public float bevelWorld;
            public int srcTris;
            public string genPath;
            public bool hullUnverified;    // mesh 不可读：只能按 bounds+三角数放行
            public bool isPb;              // 同 GameObject 上带 ProBuilderMesh（允许替换，仅标注）
        }

        static bool EnsureScene(bool forWrite)
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return true;

            if (cur.IsValid() && cur.isDirty)
            {
                if (string.IsNullOrEmpty(cur.path))
                {
                    // batchmode 启动时的未命名空场景：直接切 Level1 即可，没有需要保存的内容
                }
                else if (forWrite)
                {
                    EditorSceneManager.SaveScene(cur);
                }
                else
                {
                    Debug.LogError($"[倒角] 当前场景 {cur.path} 有未保存改动；" +
                                   "为保证零写盘/不丢改动，已中止。请先保存并打开 Level1。");
                    return false;
                }
            }

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log($"[倒角] 已打开场景 {ScenePath}");
            return true;
        }

        /// <summary>扫描当前场景全部 MeshFilter（含 prefab 实例），按名字排序保证预算发放确定性。</summary>
        static List<Item> Scan()
        {
            var scene = EditorSceneManager.GetActiveScene();
            var list = new List<Item>();
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
            {
                if (mf == null) continue;
                if (!mf.gameObject.scene.IsValid() || mf.gameObject.scene != scene) continue;
                if ((mf.hideFlags & HideFlags.DontSave) != 0) continue;
                list.Add(Inspect(mf));
            }
            return list.OrderBy(i => i.name, StringComparer.Ordinal).ToList();
        }

        static Item Inspect(MeshFilter mf)
        {
            var it = new Item { mf = mf };
            var go = mf.gameObject;
            it.name = go.name;
            it.path = PathOf(go.transform);
            it.isPb = HasProBuilderMesh(go);   // 只标注，不跳过

            // 1) 名字排除：工具生成件 / 分关装饰件
            if (it.name.IndexOf("MAPSKIN_", StringComparison.OrdinalIgnoreCase) >= 0)
            { it.skip = "名字含 MAPSKIN_"; return it; }
            if (it.name.IndexOf("DEC_", StringComparison.OrdinalIgnoreCase) >= 0)
            { it.skip = "名字含 DEC_"; return it; }

            var mesh = mf.sharedMesh;
            it.src = mesh;
            if (mesh == null) { it.skip = "mesh 为空"; return it; }

            // 2) 幂等：已是本工具产物
            if (mesh.name.StartsWith(Prefix, StringComparison.Ordinal))
            { it.skip = "已是 BEVEL_ 产物"; return it; }

            var r = mf.GetComponent<Renderer>();
            it.r = r;
            if (r == null) { it.skip = "没有 Renderer"; return it; }

            // 3) 三角数 + 子网格
            long tris = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) tris += mesh.GetIndexCount(s) / 3;
            if (tris <= 0) { it.skip = "无三角形"; return it; }
            if (tris > int.MaxValue) { it.skip = "三角数异常"; return it; }
            it.srcTris = (int)tris;
            if (mesh.subMeshCount > 1)
            { it.skip = $"多子网格({mesh.subMeshCount})：重建会丢材质分区"; return it; }
            if (it.srcTris > P_MaxSrcTris)
            { it.skip = $"三角数 {it.srcTris} > {P_MaxSrcTris}"; return it; }

            // 4) 尺寸 / 变换
            var tr = mf.transform;
            var ls = tr.lossyScale;
            if (Mathf.Abs(ls.x) < 1e-6f || Mathf.Abs(ls.y) < 1e-6f || Mathf.Abs(ls.z) < 1e-6f)
            { it.skip = "缩放为 0"; return it; }

            var lb = mesh.bounds;
            it.localBounds = lb;
            if (lb.size.x < 1e-6f || lb.size.y < 1e-6f || lb.size.z < 1e-6f)
            { it.skip = "mesh.bounds 退化"; return it; }

            it.worldSize = new Vector3(
                Mathf.Abs(lb.size.x * ls.x),
                Mathf.Abs(lb.size.y * ls.y),
                Mathf.Abs(lb.size.z * ls.z));

            float minDim = Mathf.Min(it.worldSize.x, Mathf.Min(it.worldSize.y, it.worldSize.z));
            float maxDim = Mathf.Max(it.worldSize.x, Mathf.Max(it.worldSize.y, it.worldSize.z));
            if (minDim < P_MinDim)
            {
                it.skip = $"最薄边 {minDim * 1000f:F1}mm < {P_MinDim * 1000f:F1}mm";
                return it;
            }

            // 5) mesh.bounds 与渲染器世界 AABB 形状一致（按 localToWorld 变换 bounds 八角点）
            if (!BoundsMatch(lb, tr.localToWorldMatrix, r.bounds, out var whyBounds))
            { it.skip = whyBounds; return it; }

            // 6) 盒状校验（可读 mesh）：顶点必须全在 bounds 八角点上
            if (mesh.isReadable)
            {
                if (!IsBoxHull(mesh, lb, out var whyHull))
                { it.skip = "非盒状：" + whyHull; return it; }
            }
            else
            {
                it.hullUnverified = true;
            }

            // 7) 倒角宽分级
            if (maxDim < P_SmallPiece)
            {
                it.tier = "micro";
                it.bevelWorld = P_MicroBevel;
            }
            else
            {
                it.tier = "main";
                it.bevelWorld = Mathf.Clamp(minDim * P_Ratio, P_MinBevel, P_MaxBevel);
            }
            // 几何保险：切宽不能超过最小边的一半（这里留 35% 余量）
            it.bevelWorld = Mathf.Min(it.bevelWorld, P_CapRatio * minDim);
            if (it.bevelWorld < 0.0002f)
            {
                it.skip = $"倒角宽 {it.bevelWorld * 1000f:F2}mm 过小";
                return it;
            }

            return it;
        }

        /// <summary>
        /// 检测 GameObject 上是否有 ProBuilderMesh 组件。
        /// 工程当前**没有安装** ProBuilder 包，直接写 `ProBuilderMesh` 类型会编译不过；
        /// 所以先用字符串 `GetComponent("ProBuilderMesh")`（包在时装得上就命中），
        /// 再用类型名兜底（组件脚本还在、只是类型不在当前程序集时也能命中）。
        /// 命中只做标注，**不跳过**——替换 sharedMesh 不是导出预制体，不会写 prefab 资产。
        /// </summary>
        static bool HasProBuilderMesh(GameObject go)
        {
            if (go == null) return false;
            try
            {
                if (go.GetComponent("ProBuilderMesh") != null) return true;
            }
            catch
            {
                // 字符串取组件在某些 Unity 版本/缺失脚本情况下会抛：忽略，走类型名兜底
            }
            var comps = go.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                var t = c.GetType();
                if (t.Name == "ProBuilderMesh" ||
                    t.FullName == "UnityEngine.ProBuilder.ProBuilderMesh")
                    return true;
            }
            return false;
        }

        /// <summary>mesh 局部 bounds 经 localToWorld 变换后的 AABB，应与 Renderer.bounds 一致。
        /// 旋转件也成立（比较的是同一变换后的 AABB），不会把 45°/90° 旋转的梁误杀。</summary>
        static bool BoundsMatch(Bounds lb, Matrix4x4 m, Bounds world, out string why)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector3(
                    (i & 1) == 0 ? lb.min.x : lb.max.x,
                    (i & 2) == 0 ? lb.min.y : lb.max.y,
                    (i & 4) == 0 ? lb.min.z : lb.max.z);
                var wp = m.MultiplyPoint3x4(p);
                min = Vector3.Min(min, wp);
                max = Vector3.Max(max, wp);
            }
            var expected = max - min;
            var actual = world.size;
            const float rel = 1e-3f;
            const float abs = 1e-3f;
            bool ok =
                Mathf.Abs(expected.x - actual.x) <= Mathf.Max(abs, actual.x * rel) &&
                Mathf.Abs(expected.y - actual.y) <= Mathf.Max(abs, actual.y * rel) &&
                Mathf.Abs(expected.z - actual.z) <= Mathf.Max(abs, actual.z * rel);
            if (!ok)
            {
                why = $"mesh.bounds→世界 AABB ({expected.x:F2},{expected.y:F2},{expected.z:F2}) " +
                      $"≠ Renderer.bounds ({actual.x:F2},{actual.y:F2},{actual.z:F2})";
                return false;
            }
            why = null;
            return true;
        }

        /// <summary>盒状校验：所有顶点都落在 mesh.bounds 的 8 个角点上（允许 1e-4 相对误差）。
        /// 圆柱/楔形/斜切异形体会在某个轴上有中间坐标 → 被排除。</summary>
        static bool IsBoxHull(Mesh mesh, Bounds b, out string why)
        {
            Vector3[] vs;
            try { vs = mesh.vertices; }
            catch (Exception e)
            {
                why = "读取顶点失败：" + e.Message;
                return false;
            }
            if (vs == null || vs.Length == 0) { why = "无顶点"; return false; }

            var min = b.min;
            var max = b.max;
            var eps = new Vector3(
                Mathf.Max(b.size.x * 1e-4f, 1e-4f),
                Mathf.Max(b.size.y * 1e-4f, 1e-4f),
                Mathf.Max(b.size.z * 1e-4f, 1e-4f));

            for (int i = 0; i < vs.Length; i++)
            {
                var v = vs[i];
                bool cx = Mathf.Abs(v.x - min.x) <= eps.x || Mathf.Abs(v.x - max.x) <= eps.x;
                bool cy = Mathf.Abs(v.y - min.y) <= eps.y || Mathf.Abs(v.y - max.y) <= eps.y;
                bool cz = Mathf.Abs(v.z - min.z) <= eps.z || Mathf.Abs(v.z - max.z) <= eps.z;
                if (!(cx && cy && cz))
                {
                    why = $"顶点[{i}] ({v.x:F3},{v.y:F3},{v.z:F3}) 不在包围盒角点";
                    return false;
                }
            }
            why = null;
            return true;
        }

        // ------------------------------------------------------------------ 切角盒网格

        /// <summary>
        /// 生成切角盒。顶点在对象局部空间（不改变对象 transform）；UV 按 MapSkinUvBake 的
        /// 世界三平面口径写 UV0（竖面 /3.6、水平面 /4.8），保证换 mesh 后贴图尺度与旧件一致。
        /// </summary>
        static Mesh BuildChamferMesh(Item it, string meshName)
        {
            var b = it.localBounds;
            var c = b.center;
            var h = b.extents;
            var ls = it.mf.transform.lossyScale;
            var absScale = new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));

            // 世界 45° → 局部每轴切宽 = 世界切宽 / 该轴缩放
            float[] bl =
            {
                it.bevelWorld / absScale.x,
                it.bevelWorld / absScale.y,
                it.bevelWorld / absScale.z,
            };
            // 几何保险（浮点/极端比例兜底）：局部切宽必须明显小于半边长
            bl[0] = Mathf.Min(bl[0], h.x * 0.70f);
            bl[1] = Mathf.Min(bl[1], h.y * 0.70f);
            bl[2] = Mathf.Min(bl[2], h.z * 0.70f);

            var mb = new MeshBuilder(it.mf.transform);

            // ---- 6 个原始面（矩形，向 ±轴收 b）
            // +X / -X
            float xp = c.x + h.x, xm = c.x - h.x;
            float y0 = c.y - (h.y - bl[1]), y1 = c.y + (h.y - bl[1]);
            float z0 = c.z - (h.z - bl[2]), z1 = c.z + (h.z - bl[2]);
            mb.Quad(new Vector3(xp, y0, z0), new Vector3(xp, y0, z1),
                    new Vector3(xp, y1, z1), new Vector3(xp, y1, z0), Vector3.right);
            mb.Quad(new Vector3(xm, y0, z0), new Vector3(xm, y0, z1),
                    new Vector3(xm, y1, z1), new Vector3(xm, y1, z0), Vector3.left);

            // +Y / -Y
            float yp = c.y + h.y, ym = c.y - h.y;
            float x0y = c.x - (h.x - bl[0]), x1y = c.x + (h.x - bl[0]);
            float z0y = c.z - (h.z - bl[2]), z1y = c.z + (h.z - bl[2]);
            mb.Quad(new Vector3(x0y, yp, z0y), new Vector3(x0y, yp, z1y),
                    new Vector3(x1y, yp, z1y), new Vector3(x1y, yp, z0y), Vector3.up);
            mb.Quad(new Vector3(x0y, ym, z0y), new Vector3(x0y, ym, z1y),
                    new Vector3(x1y, ym, z1y), new Vector3(x1y, ym, z0y), Vector3.down);

            // +Z / -Z
            float zp = c.z + h.z, zm = c.z - h.z;
            float x0z = c.x - (h.x - bl[0]), x1z = c.x + (h.x - bl[0]);
            float y0z = c.y - (h.y - bl[1]), y1z = c.y + (h.y - bl[1]);
            mb.Quad(new Vector3(x0z, y0z, zp), new Vector3(x1z, y0z, zp),
                    new Vector3(x1z, y1z, zp), new Vector3(x0z, y1z, zp), Vector3.forward);
            mb.Quad(new Vector3(x0z, y0z, zm), new Vector3(x1z, y0z, zm),
                    new Vector3(x1z, y1z, zm), new Vector3(x0z, y1z, zm), Vector3.back);

            // ---- 12 条棱的 45° 切面
            // 轴对 (i,j)，k = 剩余轴；每条棱在 k 轴两端各生成一个四边形。
            float[] cc = { c.x, c.y, c.z };
            float[] hh = { h.x, h.y, h.z };
            int[][] pairs = { new[] { 0, 1 }, new[] { 0, 2 }, new[] { 1, 2 } };
            foreach (var pair in pairs)
            {
                int i = pair[0], j = pair[1], k = 3 - i - j;
                for (int si = -1; si <= 1; si += 2)
                for (int sj = -1; sj <= 1; sj += 2)
                {
                    float[] pi = { 0f, 0f, 0f };
                    float[] pj = { 0f, 0f, 0f };

                    // 靠 i 面的端点
                    pi[i] = cc[i] + si * hh[i];
                    pi[j] = cc[j] + sj * (hh[j] - bl[j]);
                    // 靠 j 面的端点
                    pj[i] = cc[i] + si * (hh[i] - bl[i]);
                    pj[j] = cc[j] + sj * hh[j];

                    float k0 = cc[k] - (hh[k] - bl[k]);
                    float k1 = cc[k] + (hh[k] - bl[k]);
                    var a0 = new Vector3(pi[0], pi[1], pi[2]); a0[k] = k0;
                    var b0 = new Vector3(pj[0], pj[1], pj[2]); b0[k] = k0;
                    var a1 = new Vector3(pi[0], pi[1], pi[2]); a1[k] = k1;
                    var b1 = new Vector3(pj[0], pj[1], pj[2]); b1[k] = k1;

                    // 局部法线方向 ∝ (1/bl_i, 1/bl_j) ∝ (|scale_i|, |scale_j|)，再带符号
                    var hint = Vector3.zero;
                    hint[i] = si * absScale[i];
                    hint[j] = sj * absScale[j];
                    if (hint.sqrMagnitude > 1e-12f) hint.Normalize();

                    mb.Quad(a0, b0, b1, a1, hint);
                }
            }

            // ---- 8 个角：三点截角三角面
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                var a = new Vector3(
                    c.x + sx * h.x,
                    c.y + sy * (h.y - bl[1]),
                    c.z + sz * (h.z - bl[2]));
                var bb = new Vector3(
                    c.x + sx * (h.x - bl[0]),
                    c.y + sy * h.y,
                    c.z + sz * (h.z - bl[2]));
                var cd = new Vector3(
                    c.x + sx * (h.x - bl[0]),
                    c.y + sy * (h.y - bl[1]),
                    c.z + sz * h.z);
                var hint = new Vector3(sx * absScale.x, sy * absScale.y, sz * absScale.z);
                if (hint.sqrMagnitude > 1e-12f) hint.Normalize();
                mb.Tri(a, bb, cd, hint);
            }

            var mesh = mb.Build(meshName);
            return mesh;
        }

        /// <summary>
        /// 平直硬边网格构建器：每个面独立顶点、独立法线（flat），绝不跨面平滑。
        /// UV 按世界三平面投影（与 MapSkinUvBake 同口径：竖向面 /3.6、水平面 /4.8）。
        /// </summary>
        class MeshBuilder
        {
            readonly List<Vector3> vs = new List<Vector3>();
            readonly List<Vector3> ns = new List<Vector3>();
            readonly List<Vector2> uvs = new List<Vector2>();
            readonly List<int> tris = new List<int>();
            readonly Matrix4x4 l2w;
            readonly Matrix4x4 invT;

            public MeshBuilder(Transform tr)
            {
                l2w = tr.localToWorldMatrix;
                invT = l2w.inverse.transpose;
            }

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outwardHint)
            {
                var n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-12f) return;
                n.Normalize();
                if (Vector3.Dot(n, outwardHint) < 0f)
                {
                    var tmp = b; b = d; d = tmp;
                    n = -n;
                }
                int i0 = vs.Count;
                Add(a, n); Add(b, n); Add(c, n); Add(d, n);
                tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
                tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3);
            }

            public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 outwardHint)
            {
                var n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-12f) return;
                n.Normalize();
                if (Vector3.Dot(n, outwardHint) < 0f)
                {
                    var tmp = b; b = c; c = tmp;
                    n = -n;
                }
                int i0 = vs.Count;
                Add(a, n); Add(b, n); Add(c, n);
                tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
            }

            void Add(Vector3 p, Vector3 n)
            {
                vs.Add(p);
                ns.Add(n);

                var wp = l2w.MultiplyPoint3x4(p);
                var wn = invT.MultiplyVector(n);
                if (wn.sqrMagnitude < 1e-12f) wn = Vector3.up; else wn.Normalize();

                float ax = Mathf.Abs(wn.x), ay = Mathf.Abs(wn.y), az = Mathf.Abs(wn.z);
                if (ay >= ax && ay >= az)          // 水平面 → XZ，地基准
                    uvs.Add(new Vector2(wp.x / FloorUvBase, wp.z / FloorUvBase));
                else if (ax >= az)                 // 竖面 ±X → ZY，墙基准
                    uvs.Add(new Vector2(wp.z / WallUvBase, wp.y / WallUvBase));
                else                               // 竖面 ±Z → XY，墙基准
                    uvs.Add(new Vector2(wp.x / WallUvBase, wp.y / WallUvBase));
            }

            public Mesh Build(string name)
            {
                var m = new Mesh
                {
                    name = name,
                    indexFormat = UnityEngine.Rendering.IndexFormat.UInt16
                };
                m.SetVertices(vs);
                m.SetNormals(ns);
                m.SetUVs(0, uvs);
                m.SetTriangles(tris, 0);
                m.RecalculateTangents();
                m.RecalculateBounds();
                return m;
            }
        }

        // ------------------------------------------------------------------ 备份 / 场景 / 资产

        static BevelBackup LoadBackup(bool create)
        {
            var b = AssetDatabase.LoadAssetAtPath<BevelBackup>(BackupPath);
            if (b == null && create)
            {
                EnsureFolder(OutDir);
                b = ScriptableObject.CreateInstance<BevelBackup>();
                b.entries = new List<BevelBackup.Entry>();
                AssetDatabase.CreateAsset(b, BackupPath);
                Debug.Log($"[倒角] 新建备份资产 {BackupPath}");
            }
            return b;
        }

        static MeshFilter Resolve(string globalId, string objectPath, string objectName,
            Dictionary<string, MeshFilter> byPath, Dictionary<string, List<MeshFilter>> byName)
        {
            if (!string.IsNullOrEmpty(globalId))
            {
                try
                {
                    if (GlobalObjectId.TryParse(globalId, out var gid))
                    {
                        var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid);
                        // 只认当前场景里的实例组件：绝不去改 prefab 资产里的同名组件
                        if (obj is MeshFilter mf &&
                            mf.gameObject.scene == EditorSceneManager.GetActiveScene())
                            return mf;
                    }
                }
                catch
                {
                    // 旧版本/损坏的 globalId：继续走路径/名字退路
                }
            }
            if (!string.IsNullOrEmpty(objectPath) && byPath.TryGetValue(objectPath, out var byPathMf))
                return byPathMf;
            if (!string.IsNullOrEmpty(objectName) && byName.TryGetValue(objectName, out var list) && list.Count == 1)
                return list[0];
            return null;
        }

        static void MarkChanged(MeshFilter mf)
        {
            EditorUtility.SetDirty(mf);
            if (PrefabUtility.IsPartOfPrefabInstance(mf))
                PrefabUtility.RecordPrefabInstancePropertyModifications(mf);
        }

        static void SaveScene()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) return;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            var leaf = System.IO.Path.GetFileName(path);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        static string UniquePath(string stem, HashSet<string> used)
        {
            string path = $"{OutDir}/{stem}.asset";
            int n = 2;
            while (used.Contains(path) || AssetDatabase.LoadAssetAtPath<Mesh>(path) != null)
            {
                path = $"{OutDir}/{stem}_{n}.asset";
                n++;
            }
            used.Add(path);
            return path;
        }

        static string PathOf(Transform t)
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

        static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) s = "Object";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            var r = sb.ToString();
            while (r.Contains("__")) r = r.Replace("__", "_");
            if (r.Length > 56)
            {
                int h = 17;
                foreach (var c in s) h = unchecked(h * 31 + c);
                r = r.Substring(0, 48) + "_" + (h & 0x7fffffff).ToString("x8");
            }
            return r;
        }

        static string Dims(Vector3 s) =>
            string.Format(CultureInfo.InvariantCulture, "{0:F2}x{1:F2}x{2:F2}", s.x, s.y, s.z);

        // ------------------------------------------------------------------ 报表

        static string BuildReport(List<Item> all, bool dryRun)
        {
            var rep = new StringBuilder();
            var cands = all.Where(i => i.skip == null).ToList();
            var skipped = all.Where(i => i.skip != null).ToList();

            rep.AppendLine($"[倒角·{(dryRun ? "干跑" : "扫描")}] 场景 {ScenePath}：MeshFilter {all.Count} 个，盒状候选 {cands.Count} 个，跳过 {skipped.Count} 个");
            rep.AppendLine($"[倒角·{(dryRun ? "干跑" : "扫描")}] {(dryRun ? "零写盘：未建资产、未改场景、未存盘。" : "")}");

            // 跳过原因分组
            if (skipped.Count > 0)
            {
                rep.AppendLine("[跳过原因]");
                foreach (var g in skipped.GroupBy(i => i.skip).OrderByDescending(g => g.Count()))
                {
                    rep.AppendLine($"  {g.Count(),5}  {g.Key}");
                    int shown = 0;
                    foreach (var it in g.Take(8))
                    {
                        rep.AppendLine($"         - {it.name}");
                        shown++;
                    }
                    if (g.Count() > shown) rep.AppendLine($"         … 另 {g.Count() - shown} 件");
                }
            }

            if (cands.Count == 0)
            {
                rep.AppendLine("[倒角] 没有盒状候选。");
                return rep.ToString();
            }

            // 尺寸分布（按最小世界边分档）
            rep.AppendLine("[尺寸分布（最小世界边）]");
            var buckets = new (string label, float lo, float hi)[]
            {
                ("< 0.30m", 0f, 0.30f),
                ("0.30–1.0m", 0.30f, 1.0f),
                ("1.0–3.0m", 1.0f, 3.0f),
                ("3.0–10m", 3.0f, 10f),
                ("≥ 10m", 10f, float.MaxValue),
            };
            foreach (var bk in buckets)
            {
                int n = cands.Count(i =>
                {
                    float m = Mathf.Min(i.worldSize.x, Mathf.Min(i.worldSize.y, i.worldSize.z));
                    return m >= bk.lo && m < bk.hi;
                });
                if (n > 0)
                    rep.AppendLine($"  {bk.label,-12} {n,5} 件  ({n * 100f / cands.Count:F1}%)");
            }

            // 倒角宽 / 三角
            var widths = cands.Select(i => i.bevelWorld).OrderBy(v => v).ToList();
            long orig = cands.Sum(i => (long)i.srcTris);
            long neu = (long)cands.Count * ChamferTris;
            long delta = neu - orig;
            rep.AppendLine($"[倒角档位] main {cands.Count(i => i.tier == "main")}、micro {cands.Count(i => i.tier == "micro")}；" +
                           $"切宽 min/median/max = {widths[0] * 1000f:F1} / {widths[widths.Count / 2] * 1000f:F1} / {widths[widths.Count - 1] * 1000f:F1} mm");
            rep.AppendLine($"[三角] 原 {orig:N0} → 新 {neu:N0}（{ChamferTris} 三角/件），增量 {delta:N0} / 上限 {P_MaxTriDelta:N0}" +
                           (delta > P_MaxTriDelta ? "  ⚠ 超预算，Run 会按名字顺序截断" : "  ✓ 在预算内"));
            rep.AppendLine($"[对象合计] 三角增量上限 {P_MaxTriDelta:N0}，预计实际用量 {delta:N0}");
            int unverified = cands.Count(i => i.hullUnverified);
            if (unverified > 0)
                rep.AppendLine($"[提示] {unverified} 个候选 mesh 不可读，未做盒状顶点校验（按 bounds+三角数放行）");

            // ProBuilder 纪律：允许替换 sharedMesh，只标注；本工具不因 pb 跳过任何对象。
            var pbCands = cands.Where(i => i.isPb).ToList();
            int pbBlocked = 0;   // 本工具不因 ProBuilder 跳过任何对象（替换 sharedMesh 是允许的）
            rep.AppendLine($"[ProBuilder] 检出 pb 对象 {all.Count(i => i.isPb)} 个；倒角清单内 {pbCands.Count} 个（允许替换 sharedMesh，仅标注，不跳过）");
            rep.AppendLine($"跳过 ProBuilder/Mesh 非本工具可改 的对象数 = {pbBlocked}");
            foreach (var it in pbCands.Take(20))
                rep.AppendLine($"  [pb] {it.name} | {Dims(it.worldSize)} | {it.bevelWorld * 1000f:F1}mm");
            if (pbCands.Count > 20)
                rep.AppendLine($"  … 另 {pbCands.Count - 20} 个 pb 对象");

            // 全图包围盒
            var merged = new Bounds();
            bool has = false;
            foreach (var it in cands)
            {
                var rb = it.r.bounds;
                if (!has) { merged = rb; has = true; }
                else merged.Encapsulate(rb);
            }
            if (has)
                rep.AppendLine($"[包围盒] center=({merged.center.x:F1},{merged.center.y:F1},{merged.center.z:F1}) " +
                               $"size=({merged.size.x:F1},{merged.size.y:F1},{merged.size.z:F1})");

            // 逐件清单（名字 / 世界尺寸 / AABB / 切宽 / 原三角）
            rep.AppendLine("[清单]  名字 | 世界尺寸(WxHxD) | 渲染器AABB center/size | 切宽 | 原三角");
            int limit = P_ListLimit > 0 ? Mathf.Min(P_ListLimit, cands.Count) : cands.Count;
            for (int i = 0; i < limit; i++)
            {
                var it = cands[i];
                var wb = it.r.bounds;
                rep.AppendLine($"  {it.name} | {Dims(it.worldSize)} | " +
                               $"c=({wb.center.x:F1},{wb.center.y:F1},{wb.center.z:F1}) " +
                               $"s=({wb.size.x:F1},{wb.size.y:F1},{wb.size.z:F1}) | " +
                               $"{it.bevelWorld * 1000f:F1}mm {it.tier} | {it.srcTris} tri");
            }
            if (limit < cands.Count)
                rep.AppendLine($"  … 另 {cands.Count - limit} 件（-bv-list 0 可打印全部）");

            return rep.ToString();
        }

        // ------------------------------------------------------------------ 参数解析 / 夹取兜底

        static void ParseArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
            {
                switch (a[i])
                {
                    case "-scene": ScenePath = a[i + 1]; break;
                    case "-bv-ratio": P_Ratio = Fl(a[i + 1], P_Ratio); break;
                    case "-bv-min": P_MinBevel = Fl(a[i + 1], P_MinBevel); break;
                    case "-bv-max": P_MaxBevel = Fl(a[i + 1], P_MaxBevel); break;
                    case "-bv-micro": P_MicroBevel = Fl(a[i + 1], P_MicroBevel); break;
                    case "-bv-small": P_SmallPiece = Fl(a[i + 1], P_SmallPiece); break;
                    case "-bv-mindim": P_MinDim = Fl(a[i + 1], P_MinDim); break;
                    case "-bv-cap": P_CapRatio = Fl(a[i + 1], P_CapRatio); break;
                    case "-bv-maxtris": P_MaxSrcTris = In(a[i + 1], P_MaxSrcTris); break;
                    case "-bv-budget": P_MaxTriDelta = InL(a[i + 1], P_MaxTriDelta); break;
                    case "-bv-list": P_ListLimit = In(a[i + 1], P_ListLimit); break;
                    case "-bv-cleanorphans": P_CleanOrphans = In(a[i + 1], P_CleanOrphans); break;
                }
            }
            Sanitize();
        }

        static void Sanitize()
        {
            P_Ratio = Mathf.Clamp(P_Ratio, 0.01f, 0.50f);
            P_MinBevel = Mathf.Clamp(P_MinBevel, 0f, 0.20f);
            P_MaxBevel = Mathf.Clamp(P_MaxBevel, P_MinBevel, 2f);
            P_MicroBevel = Mathf.Clamp(P_MicroBevel, 0.0001f, 0.01f);
            P_SmallPiece = Mathf.Clamp(P_SmallPiece, 0.01f, 10f);
            P_MinDim = Mathf.Clamp(P_MinDim, 0.0005f, 0.05f);
            P_CapRatio = Mathf.Clamp(P_CapRatio, 0.05f, 0.49f);
            P_MaxSrcTris = Mathf.Clamp(P_MaxSrcTris, 6, 1000);
            P_MaxTriDelta = Math.Max(1L, Math.Min(P_MaxTriDelta, 5000000L));
            P_ListLimit = Mathf.Clamp(P_ListLimit, 0, 10000);
            P_CleanOrphans = P_CleanOrphans == 0 ? 0 : 1;
        }

        static float Fl(string s, float d) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;

        static int In(string s, int d) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;

        static long InL(string s, long d) =>
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;
    }
}
