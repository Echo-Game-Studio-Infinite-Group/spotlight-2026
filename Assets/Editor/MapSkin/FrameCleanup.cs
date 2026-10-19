// 七个区块外围白色立方体框架的审计 / 删除 / 恢复工具。
//
// 背景：BOX_S_01..S_07 下各有一个 `Beam` 容器，里面是白族材质（Material.014/061 等）
// 的细长 prefab 梁，视觉上是一圈白盒定界脚手架。用户判断可以删，但必须先确认：
//   · 梁上有没有 Collider（很多 L1_Beam 自带 MeshCollider/BoxCollider，可能是可站立面）；
//   · 有没有子物体 / 脚本 / 外部脚本引用；
//   · 删掉后区块是否还有别的 Collider 能当边界。
//
// 本工具只做五件事：
//   1) Scan      只读扫描，写 _session_audit/FRAME_Candidates.json（含 renderer.enabled 现状）；
//   2) Hide      默认干跑，-run 1 才执行；只把框架件的 MeshRenderer.enabled 关掉，
//                 不删 GameObject、不动 Collider，写 _session_audit/FRAME_Hidden.json；
//   3) Unhide    按 FRAME_Hidden.json 把对应 MeshRenderer.enabled 恢复为 true；
//   4) Remove    默认干跑，-run 1 才删；只删"框架边 + 无 Collider + 无子物体 + 无脚本引用"的件；
//                 带 Collider 的件单独列出，绝不自动删（本轮不使用，视觉问题交给 Hide）；
//   5) Restore   按 _session_audit/FRAME_Removed.json 用原 mesh/材质重建；
//   6) AuditPlay 只读预测每关 Collider 总量变化，标出删完后会失去最后一道 Collider 边界的区块。
//
// 硬约束：
//   · 不写 prefab、不 new Material、不启动 Unity；
//   · Hide 只处理 BOX_S_0x 根下、名字以 L1_Beam_ 开头、长细比 >= 6、截面 <= 2.5m 的件；
//     绝不关闭 S_0x_* 原始件、MAPSKIN_*、DEC_*；
//   · Hide 绝不修改 Collider（用户口径：视觉可删，碰撞大致不变）；
//   · Remove 只删无 Collider 件；有 Collider 的候选不删，交主控判断。
//
// 额外发现（不在本工具处理范围）：Assets/Art/EnvSkins/BEVEL_Backup.asset 里存有
// Beam 件的 objectPath 备份记录；删掉场景件后这些记录会失效，但它是编辑器备份资产，
// 不影响运行时。需要清理时另开工具，本工具不修改该资产。
//
// 框架判定口径：
//   · 细长比 = 最长边 / 最短边 >= 6，截面 = 中间边 <= 2.5m；
//   · 在每个区块的细长候选里按"世界长度"分桶，取包围盒体积最大、且至少有 4 条边
//     落在该包围盒棱上的那一组作为框架笼；
//   · 判断某件是否为"框架边"：长轴的两条垂直轴都至少有一端面贴着框架笼包围盒
//     （面距 <= 1.5m）。用端面而不是中心，是因为 2m 厚的梁中心天然比笼边界内缩 1m。
//   · `closedFrame` 严格按需求统计 X/Y/Z 三向各 >= 4 条；本场景的框架多为上下两圈
//     + 墙板承重，Y 向长梁缺失，因此 closedFrame 常为 false，但这不代表它不是框架。
//     报告里同时给出 axisX/Y/Z，供主控判断。
//
// 菜单：MapSkin/框架清理/*
// 批处理：
//   -executeMethod MapSkin.FrameCleanup.Scan
//   -executeMethod MapSkin.FrameCleanup.Hide         默认干跑；-run 1 执行；-include-manual 0 排除带 Collider 件
//   -executeMethod MapSkin.FrameCleanup.Unhide
//   -executeMethod MapSkin.FrameCleanup.Remove       默认干跑；-run 1 执行
//   -executeMethod MapSkin.FrameCleanup.Restore
//   -executeMethod MapSkin.FrameCleanup.AuditPlay
//   通用参数：-scene/-out/-tol/-slender/-cross/-include-manual/-run/-save
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UObject = UnityEngine.Object;

namespace MapSkin
{
    public static class FrameCleanup
    {
        // ------------------------------------------------------------------ 常量

        const string DefaultScene = "Assets/Scenes/Level1.unity";
        const string CandidatesName = "FRAME_Candidates.json";
        const string RemovedName = "FRAME_Removed.json";
        const string HiddenName = "FRAME_Hidden.json";
        const string AuditPlayName = "FRAME_AuditPlay.txt";

        static readonly Regex BlockRe = new Regex(@"^BOX_S_?0([1-7])$", RegexOptions.Compiled);

        // 判定阈值（可用 -tol/-slender/-cross 覆盖；改代码前先想清楚为什么）
        static float P_Tol = 1.5f;          // 面距容差
        static float P_MinSlender = 6f;     // 长细比下限
        static float P_MaxCross = 2.5f;     // 截面（中间边）上限
        static int P_MinEdges = 4;          // 框架组至少需要的边数

        // 运行控制
        static bool P_Run = false;          // -run 1 真正执行
        static bool P_Save = true;          // -save 0 不存场景
        static bool P_IncludeManual = true; // -include-manual 0 排除带 Collider 的框架件（默认包含，用户已确认要隐藏）
        static string P_Scene = DefaultScene;
        static string P_OutDir = null;

        // ------------------------------------------------------------------ 菜单

        [MenuItem("MapSkin/框架清理/① 扫描候选（只读）", false, 300)]
        static void ScanMenu() => Scan();

        [MenuItem("MapSkin/框架清理/② 删除安全框架件（干跑）", false, 301)]
        static void RemoveMenu() => Remove();

        [MenuItem("MapSkin/框架清理/③ 按 manifest 恢复", false, 302)]
        static void RestoreMenu() => Restore();

        [MenuItem("MapSkin/框架清理/④ 玩法碰撞预测（只读）", false, 303)]
        static void AuditPlayMenu() => AuditPlay();

        [MenuItem("MapSkin/框架清理/⑤ 隐藏框架件（只关渲染）", false, 304)]
        static void HideMenu() => Hide();

        [MenuItem("MapSkin/框架清理/⑥ 按 manifest 恢复可见", false, 305)]
        static void UnhideMenu() => Unhide();

        // ------------------------------------------------------------------ 批处理入口

        /// <summary>只读扫描：识别七个区块的白色框架，写 FRAME_Candidates.json。</summary>
        public static void Scan()
        {
            ParseArgs();
            EnsureScene();
            var result = BuildReport();
            WriteReport(result.report);
            LogSummary(result.report);
        }

        /// <summary>删除安全框架件。默认干跑；-run 1 才执行。带 Collider 的件只列出。</summary>
        public static void Remove()
        {
            ParseArgs();
            EnsureScene();
            var result = BuildReport();
            WriteReport(result.report);
            LogSummary(result.report);

            var safe = result.pieces.Where(p => p.rec.safeToDelete).ToList();
            var manual = result.pieces.Where(p => p.rec.frameRelated && p.rec.colliderCount > 0)
                                      .OrderBy(p => p.rec.block).ThenBy(p => p.rec.name).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"[框架·删除] 安全删除计划 {safe.Count} 件；需人工确认（有 Collider）{manual.Count} 件，不删。");
            foreach (var p in safe)
                sb.AppendLine($"    [可删] {p.rec.block} {p.rec.name}  碰撞=0");
            foreach (var p in manual)
                sb.AppendLine($"    [人工] {p.rec.block} {p.rec.name}  Collider={p.rec.colliderCount}  " +
                              $"位置=({p.rec.position[0]:F1},{p.rec.position[1]:F1},{p.rec.position[2]:F1})  " +
                              $"材质={p.rec.material}  离可站立面={(p.rec.gapToStandable >= 0 ? p.rec.gapToStandable.ToString("F2") + "m" : "无")}");
            Debug.Log(sb.ToString());

            if (!P_Run)
            {
                Debug.Log("[框架·删除] DryRun：未删除。确认后加 -run 1 执行；有 Collider 的件不在自动删除范围内。");
                return;
            }
            if (safe.Count == 0)
            {
                Debug.Log("[框架·删除] 没有可安全删除的框架件（全部带 Collider 或有子物体/脚本）。");
                return;
            }

            WriteRemovedManifest(result, safe);
            int removed = 0;
            foreach (var p in safe)
            {
                if (p.go == null) continue;
                UObject.DestroyImmediate(p.go);
                removed++;
            }
            if (P_Save) SaveScene();
            Debug.Log($"[框架·删除] 已删除 {removed} 件；manifest: {Path.Combine(OutDir, RemovedName)}");
        }

        /// <summary>只关闭框架件的 MeshRenderer，保留 GameObject 与 Collider。默认干跑；-run 1 才执行。
        /// 默认包含带 Collider 的 manual 件（用户已确认这些粗梁就是要隐藏的框架）；
        /// 用 -include-manual 0 可只处理无 Collider 件。</summary>
        public static void Hide()
        {
            ParseArgs();
            EnsureScene();
            var result = BuildReport();
            WriteReport(result.report);
            LogSummary(result.report);

            var targets = SelectHideTargets(result.pieces);
            var sb = new StringBuilder();
            sb.AppendLine($"[框架·隐藏] 计划隐藏 {targets.Count} 件（只关 MeshRenderer；不删对象、不动 Collider）；" +
                          $"include-manual={P_IncludeManual}。");
            foreach (var p in targets)
                sb.AppendLine($"    [隐藏] {p.rec.block} {p.rec.name,-44} Collider={p.rec.colliderCount} " +
                              $"renderer.enabled={p.mr.enabled}  {p.rec.path}");
            Debug.Log(sb.ToString());

            if (!P_Run)
            {
                Debug.Log("[框架·隐藏] DryRun：未修改。确认后加 -run 1 执行；恢复用 Unhide。");
                return;
            }
            if (targets.Count == 0)
            {
                Debug.Log("[框架·隐藏] 没有符合安全条件的框架件可隐藏。");
                return;
            }

            WriteHiddenManifest(targets);
            int hidden = 0, alreadyHidden = 0;
            foreach (var p in targets)
            {
                if (p.mr == null) continue;
                if (!p.mr.enabled) { alreadyHidden++; continue; }
                p.mr.enabled = false;
                EditorUtility.SetDirty(p.mr);
                hidden++;
            }
            if (hidden > 0)
            {
                var scene = EditorSceneManager.GetActiveScene();
                if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
                if (P_Save) SaveScene();
            }
            Debug.Log($"[框架·隐藏] 已隐藏 {hidden} 件，原本已隐藏 {alreadyHidden} 件；" +
                      $"Collider 未改动；manifest: {Path.Combine(OutDir, HiddenName)}");

            var remainSb = new StringBuilder("[框架·隐藏] 每关剩余可见框架件：");
            foreach (var b in result.report.blocks)
            {
                int remain = result.pieces.Count(p => p.rec.block == b.block && p.rec.frameRelated &&
                                                      p.mr != null && p.mr.enabled);
                remainSb.Append($" {b.block}={remain}");
            }
            Debug.Log(remainSb.ToString());
        }

        /// <summary>按 FRAME_Hidden.json 把对应 MeshRenderer.enabled 恢复为 true。</summary>
        public static void Unhide()
        {
            ParseArgs();
            EnsureScene();
            string path = Path.Combine(OutDir, HiddenName);
            if (!File.Exists(path))
            {
                Debug.LogError($"[框架·恢复可见] 找不到 manifest：{path}");
                return;
            }
            HiddenManifest man;
            try { man = JsonUtility.FromJson<HiddenManifest>(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception e) { Debug.LogError($"[框架·恢复可见] manifest 解析失败：{e.Message}"); return; }
            if (man == null || man.entries == null || man.entries.Count == 0)
            {
                Debug.LogWarning("[框架·恢复可见] manifest 为空。");
                return;
            }

            int shown = 0, alreadyVisible = 0, missing = 0;
            foreach (var e in man.entries)
            {
                if (e == null) continue;
                var t = FindByPath(e.path);
                if (t == null)
                {
                    Debug.LogWarning($"[框架·恢复可见] 找不到对象 '{e.path}'，跳过 {e.name}");
                    missing++;
                    continue;
                }
                var mrs = t.GetComponents<MeshRenderer>();
                MeshRenderer mr = null;
                if (e.rendererIndex >= 0 && e.rendererIndex < mrs.Length) mr = mrs[e.rendererIndex];
                else if (mrs.Length > 0) mr = mrs[0];
                if (mr == null)
                {
                    Debug.LogWarning($"[框架·恢复可见] '{e.path}' 上没有 MeshRenderer，跳过 {e.name}");
                    missing++;
                    continue;
                }
                if (mr.enabled) { alreadyVisible++; continue; }
                mr.enabled = true;
                EditorUtility.SetDirty(mr);
                shown++;
            }

            if (shown > 0)
            {
                var scene = EditorSceneManager.GetActiveScene();
                if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
                if (P_Save) SaveScene();
            }
            Debug.Log($"[框架·恢复可见] 已恢复 {shown} 件，原本可见 {alreadyVisible} 件，跳过 {missing} 件；manifest: {path}");
        }

        /// <summary>按 FRAME_Removed.json 用原 mesh / 材质 / 变换重建。不新建材质、不写 prefab。</summary>
        public static void Restore()
        {
            ParseArgs();
            EnsureScene();
            string path = Path.Combine(OutDir, RemovedName);
            if (!File.Exists(path))
            {
                Debug.LogError($"[框架·恢复] 找不到 manifest：{path}");
                return;
            }
            RemovedManifest man;
            try { man = JsonUtility.FromJson<RemovedManifest>(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception e) { Debug.LogError($"[框架·恢复] manifest 解析失败：{e.Message}"); return; }
            if (man == null || man.entries == null || man.entries.Count == 0)
            {
                Debug.LogWarning("[框架·恢复] manifest 为空。");
                return;
            }

            int restored = 0, skipped = 0;
            foreach (var e in man.entries)
            {
                if (e == null) continue;
                Transform parent = FindByPath(e.parentPath);
                if (parent == null)
                {
                    // 父容器被改名/挪走时退回到区块根，至少不把对象丢在场景根
                    parent = FindBlockRoot(e.block);
                    if (parent == null)
                    {
                        Debug.LogError($"[框架·恢复] 找不到父容器 '{e.parentPath}' 与区块 {e.block}，跳过 {e.name}");
                        skipped++;
                        continue;
                    }
                    Debug.LogWarning($"[框架·恢复] 父容器 '{e.parentPath}' 不存在，{e.name} 回挂到 {parent.name}。");
                }
                if (parent.Find(e.name) != null)
                {
                    Debug.LogWarning($"[框架·恢复] {parent.name} 下已存在同名对象，跳过 {e.name}");
                    skipped++;
                    continue;
                }

                var go = new GameObject(e.name);
                go.layer = e.layer;
                if (!string.IsNullOrEmpty(e.tag))
                {
                    try { go.tag = e.tag; }
                    catch { Debug.LogWarning($"[框架·恢复] 标签 '{e.tag}' 不存在，{e.name} 保持 Untagged"); }
                }
                go.SetActive(e.activeSelf);
                go.transform.SetParent(parent, false);
                go.transform.localPosition = V3(e.localPosition);
                var q = new Quaternion(Val(e.localRotation, 0), Val(e.localRotation, 1),
                                        Val(e.localRotation, 2), Val(e.localRotation, 3, 1f));
                if (q.x == 0f && q.y == 0f && q.z == 0f && q.w == 0f) q = Quaternion.identity;
                go.transform.localRotation = Quaternion.Normalize(q);
                go.transform.localScale = V3(e.localScale, Vector3.one);
                if (e.siblingIndex >= 0)
                    go.transform.SetSiblingIndex(Mathf.Clamp(e.siblingIndex, 0, parent.childCount - 1));

                var mf = go.AddComponent<MeshFilter>();
                Mesh mesh = null;
                if (!string.IsNullOrEmpty(e.meshAssetPath))
                    mesh = AssetDatabase.LoadAssetAtPath<Mesh>(e.meshAssetPath);
                if (mesh == null && !string.IsNullOrEmpty(e.meshName))
                    mesh = LoadBuiltinMesh(e.meshName);
                if (mesh != null) mf.sharedMesh = mesh;
                else Debug.LogWarning($"[框架·恢复] {e.name} 的 mesh 找不到（path='{e.meshAssetPath}', name='{e.meshName}'）");

                var mr = go.AddComponent<MeshRenderer>();
                var mats = new List<Material>();
                if (e.materialPaths != null)
                {
                    foreach (var mp in e.materialPaths)
                    {
                        if (string.IsNullOrEmpty(mp)) continue;
                        var m = AssetDatabase.LoadAssetAtPath<Material>(mp);
                        if (m != null) mats.Add(m);
                        else Debug.LogWarning($"[框架·恢复] {e.name} 的材质找不到：{mp}");
                    }
                }
                if (mats.Count > 0) mr.sharedMaterials = mats.ToArray();

                mr.enabled = e.rendererEnabled;
                mr.shadowCastingMode = (ShadowCastingMode)e.shadowCastingMode;
                mr.receiveShadows = e.receiveShadows;
                mr.lightProbeUsage = (LightProbeUsage)e.lightProbeUsage;
                mr.reflectionProbeUsage = (ReflectionProbeUsage)e.reflectionProbeUsage;
                mr.renderingLayerMask = (uint)e.renderingLayerMask;
                mr.sortingOrder = e.sortingOrder;

                if (e.isStatic)
                    GameObjectUtility.SetStaticEditorFlags(go, (StaticEditorFlags)e.staticEditorFlags);

                // 安全件按定义没有 Collider，所以恢复时不加碰撞体。
                restored++;
            }

            if (P_Save) SaveScene();
            Debug.Log($"[框架·恢复] 已恢复 {restored} 件，跳过 {skipped} 件；manifest: {path}");
        }

        /// <summary>只读预测：每关 Collider 总量在"安全删除"与"人工全删框架"两种口径下的变化。</summary>
        public static void AuditPlay()
        {
            ParseArgs();
            EnsureScene();
            var result = BuildReport();

            var sb = new StringBuilder();
            sb.AppendLine("[框架·玩法碰撞预测] 只读；安全删除只动无 Collider 件，人工全删指主控确认后连 Collider 件一起删。");
            sb.AppendLine("区块    Collider前  安全删后  全删后  框架Collider  非框架Collider  三轴封闭  失去最后边界");
            foreach (var b in result.report.blocks)
            {
                int safeColliders = result.pieces
                    .Where(p => p.rec.block == b.block && p.rec.safeToDelete)
                    .Sum(p => p.rec.colliderCount);
                int afterSafe = b.collidersBefore - safeColliders;
                sb.AppendLine($"{(b.losesLastBoundary ? "[!] " : "    ")}{b.block,-6} " +
                              $"{b.collidersBefore,9} {afterSafe,10} {b.collidersAfterFull,7} " +
                              $"{b.frameColliders,13} {b.collidersBefore - b.frameColliders,14} " +
                              $"{b.closedFrame,8} {b.losesLastBoundary,13}");
                sb.AppendLine($"      边界：{b.boundaryNote}");
            }

            var doomed = result.report.blocks.Where(b => b.losesLastBoundary).ToList();
            if (doomed.Count > 0)
                sb.AppendLine("[!] 删除后会失去最后一道 Collider 边界的区块：" +
                              string.Join("、", doomed.Select(b => b.block)));
            else
                sb.AppendLine("结论：七个区块在人工全删框架后都仍有非框架 Collider（柱子/墙/地面），不会失去最后一道边界。");

            Directory.CreateDirectory(OutDir);
            string path = Path.Combine(OutDir, AuditPlayName);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Debug.Log(sb.ToString());
            Debug.Log($"[框架·玩法碰撞预测] 已写入 {path}");
        }

        // ------------------------------------------------------------------ 扫描核心

        class PieceRuntime
        {
            public GameObject go;
            public MeshFilter mf;
            public MeshRenderer mr;
            public Bounds bounds;
            public int longAxis;
            public float longSize;
            public PieceRec rec;
        }

        class FrameGroup
        {
            public readonly List<PieceRuntime> pieces = new List<PieceRuntime>();
            public Bounds bounds;
            public bool hasBounds;
            public int edges;
            public float volume;
        }

        class BlockRuntime
        {
            public string block;
            public Transform root;
            public Transform beam;
            public Bounds bounds;
            public bool hasBounds;
            public Bounds frameBounds;
            public bool hasFrame;
            public readonly List<PieceRuntime> pieces = new List<PieceRuntime>();
            public BlockRec rec;
        }

        class ScanResult
        {
            public FrameReport report;
            public List<PieceRuntime> pieces = new List<PieceRuntime>();
        }

        static ScanResult BuildReport()
        {
            var result = new ScanResult();
            var blocks = new List<BlockRuntime>();

            foreach (var t in UObject.FindObjectsOfType<Transform>(true))
            {
                if (t == null || !BlockRe.IsMatch(t.name)) continue;
                // 只认顶层区块根，防止嵌套容器重复统计
                bool nested = false;
                for (var p = t.parent; p != null; p = p.parent)
                    if (BlockRe.IsMatch(p.name)) { nested = true; break; }
                if (nested) continue;

                var br = AnalyzeBlock(t);
                if (br != null && br.pieces.Count > 0) blocks.Add(br);
            }
            blocks.Sort((a, b) => string.CompareOrdinal(a.block, b.block));

            var allPieces = new List<PieceRec>();
            var blocksRec = new List<BlockRec>();
            foreach (var b in blocks)
            {
                allPieces.AddRange(b.pieces.Select(p => p.rec));
                blocksRec.Add(b.rec);
            }

            result.report = new FrameReport
            {
                generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                scene = P_Scene,
                note = "框架判定：细长比>=6、截面<=2.5m；按世界长度分桶取体积最大的长梁组作为框架笼，" +
                       "长轴两条垂直轴上至少一端面贴笼边界（<=1.5m）的件记为框架边。" +
                       "closedFrame 严格统计 X/Y/Z 各>=4，本场景 Y 向长梁常缺失，故常为 false。" +
                       "安全删除只动框架笼内、无 Collider、无子物体、无脚本引用、无外部脚本引用的件。",
                boundaryTol = P_Tol,
                minSlender = P_MinSlender,
                maxCross = P_MaxCross,
                blocks = blocksRec,
                pieces = allPieces.OrderBy(p => p.block).ThenBy(p => p.name).ToList(),
                manual = allPieces.Where(p => p.frameRelated && p.colliderCount > 0)
                                  .OrderBy(p => p.block).ThenBy(p => p.name).ToList(),
            };
            result.pieces = blocks.SelectMany(b => b.pieces).ToList();
            return result;
        }

        static BlockRuntime AnalyzeBlock(Transform blockRoot)
        {
            var br = new BlockRuntime
            {
                block = "S_0" + BlockRe.Match(blockRoot.name).Groups[1].Value,
                root = blockRoot,
            };
            br.beam = FindDirectChild(blockRoot, "Beam");
            if (br.beam == null)
            {
                // 少数关卡可能把 Beam 容器再套一层；退一步按名字找后代
                foreach (var t in blockRoot.GetComponentsInChildren<Transform>(true))
                    if (t != null && string.Equals(t.name, "Beam", StringComparison.OrdinalIgnoreCase))
                    {
                        br.beam = t;
                        break;
                    }
            }
            if (br.beam == null)
            {
                Debug.LogWarning($"[框架] {br.block} 下找不到 Beam 容器，跳过。");
                return null;
            }

            // 区块包围盒：Beam 容器之外的所有渲染件（原始 S_0x_* / 柱子 / 墙 / 地面）。
            foreach (var r in blockRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || IsUnder(r.transform, br.beam)) continue;
                if (!br.hasBounds) { br.bounds = r.bounds; br.hasBounds = true; }
                else br.bounds.Encapsulate(r.bounds);
            }

            // 收集 Beam 容器里的渲染件
            foreach (var mr in br.beam.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (mr == null) continue;
                var go = mr.gameObject;
                if (go == null) continue;
                var mf = go.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var b = mr.bounds;
                if (b.size.sqrMagnitude < 1e-8f) continue;

                var size = b.size;
                int longAxis = MajorAxis(size);
                float longSize = AxisMax(size, longAxis);
                float minSize = AxisMin(size, longAxis);
                float cross = size.x + size.y + size.z - longSize - minSize;
                float slender = longSize / Mathf.Max(minSize, 1e-4f);

                var mats = mr.sharedMaterials;
                var matNames = new List<string>();
                var matPaths = new List<string>();
                if (mats != null)
                {
                    foreach (var m in mats)
                    {
                        if (m == null) continue;
                        matNames.Add(m.name);
                        string mp = AssetDatabase.GetAssetPath(m);
                        if (!string.IsNullOrEmpty(mp)) matPaths.Add(mp);
                    }
                }

                var p = new PieceRuntime
                {
                    go = go,
                    mf = mf,
                    mr = mr,
                    bounds = b,
                    longAxis = longAxis,
                    longSize = longSize,
                    rec = new PieceRec
                    {
                        name = go.name,
                        block = br.block,
                        path = PathOf(go.transform),
                        parentPath = PathOf(go.transform.parent),
                        localPosition = new[] { go.transform.localPosition.x, go.transform.localPosition.y, go.transform.localPosition.z },
                        localRotation = new[] { go.transform.localRotation.x, go.transform.localRotation.y, go.transform.localRotation.z, go.transform.localRotation.w },
                        localScale = new[] { go.transform.localScale.x, go.transform.localScale.y, go.transform.localScale.z },
                        material = string.Join(", ", matNames),
                        materialPaths = matPaths.ToArray(),
                        meshAssetPath = AssetDatabase.GetAssetPath(mf.sharedMesh),
                        meshName = mf.sharedMesh != null ? mf.sharedMesh.name : "",
                        position = new[] { b.center.x, b.center.y, b.center.z },
                        size = new[] { size.x, size.y, size.z },
                        boundsMin = new[] { b.min.x, b.min.y, b.min.z },
                        boundsMax = new[] { b.max.x, b.max.y, b.max.z },
                        longAxis = "XYZ"[longAxis].ToString(),
                        longSize = longSize,
                        slender = slender,
                        cross = cross,
                        tris = TriCount(mf.sharedMesh),
                        hasCollider = false,
                        hasChild = go.transform.childCount > 0,
                        hasScript = go.GetComponentsInChildren<MonoBehaviour>(true).Length > 0,
                        colliderCount = 0,
                        gapToStandable = -1f,
                        nearBlockBoundary = br.hasBounds && NearBoundary(b, br.bounds, P_Tol),
                        geometryCandidate = slender >= P_MinSlender && cross <= P_MaxCross,
                        rendererEnabled = mr.enabled,
                    },
                };
                var cols = go.GetComponentsInChildren<Collider>(true);
                p.rec.colliderCount = cols.Length;
                p.rec.hasCollider = cols.Length > 0;
                br.pieces.Add(p);
            }

            if (br.pieces.Count == 0) return br;

            if (!br.hasBounds)
            {
                // 没有非 Beam 几何时退回用 Beam 自身包围盒
                bool first = true;
                foreach (var p in br.pieces)
                {
                    if (first) { br.bounds = p.bounds; br.hasBounds = true; first = false; }
                    else br.bounds.Encapsulate(p.bounds);
                }
            }

            BuildFrame(br);
            ComputeGap(br);
            BuildBlockRec(br);
            return br;
        }

        static void BuildFrame(BlockRuntime br)
        {
            var cands = br.pieces.Where(p => p.rec.geometryCandidate).ToList();
            var groups = new Dictionary<int, FrameGroup>();
            foreach (var p in cands)
            {
                int bucket = Mathf.RoundToInt(p.longSize * 4f);   // 0.25m 桶，同一 prefab 实例长度一致
                FrameGroup g;
                if (!groups.TryGetValue(bucket, out g))
                {
                    g = new FrameGroup();
                    groups[bucket] = g;
                }
                g.pieces.Add(p);
                if (!g.hasBounds) { g.bounds = p.bounds; g.hasBounds = true; }
                else g.bounds.Encapsulate(p.bounds);
            }

            FrameGroup best = null;
            foreach (var g in groups.Values)
            {
                if (g.pieces.Count < P_MinEdges) continue;
                int edges = 0;
                foreach (var p in g.pieces)
                    if (EdgeLike(p.bounds, g.bounds, p.longAxis, P_Tol)) edges++;
                g.edges = edges;
                if (edges < P_MinEdges) continue;
                g.volume = g.bounds.size.x * g.bounds.size.y * g.bounds.size.z;
                if (best == null ||
                    g.volume > best.volume + 0.01f ||
                    (Mathf.Abs(g.volume - best.volume) <= 0.01f &&
                     (g.edges > best.edges || (g.edges == best.edges && g.pieces.Count > best.pieces.Count))))
                    best = g;
            }

            if (best == null)
            {
                foreach (var p in cands)
                {
                    p.rec.edgeLike = false;
                    p.rec.frame = false;
                    p.rec.frameRelated = false;
                }
                br.hasFrame = false;
                br.frameBounds = br.bounds;
                return;
            }

            br.hasFrame = true;
            br.frameBounds = best.bounds;
            int[] axisCounts = new int[3];
            foreach (var p in cands)
            {
                p.rec.edgeLike = EdgeLike(p.bounds, best.bounds, p.longAxis, P_Tol);
                p.rec.frame = p.rec.edgeLike;
                p.rec.frameRelated = p.rec.edgeLike || Inside(p.bounds, best.bounds, P_Tol);
                if (p.rec.edgeLike) axisCounts[p.longAxis]++;
            }

            br.rec = new BlockRec();
            br.rec.axisX = axisCounts[0];
            br.rec.axisY = axisCounts[1];
            br.rec.axisZ = axisCounts[2];
            br.rec.closedFrame = axisCounts[0] >= P_MinEdges &&
                                 axisCounts[1] >= P_MinEdges &&
                                 axisCounts[2] >= P_MinEdges;
        }

        static void ComputeGap(BlockRuntime br)
        {
            var colliders = UObject.FindObjectsOfType<Collider>(true);
            foreach (var p in br.pieces)
            {
                if (!p.rec.geometryCandidate) continue;
                float best = -1f;
                var b = p.bounds;
                foreach (var c in colliders)
                {
                    if (c == null || c.isTrigger) continue;
                    if (c.transform == p.go.transform || c.transform.IsChildOf(p.go.transform)) continue;
                    var s = c.bounds;
                    if (s.size.sqrMagnitude < 1e-8f) continue;
                    float ox = Mathf.Min(b.max.x, s.max.x) - Mathf.Max(b.min.x, s.min.x);
                    float oz = Mathf.Min(b.max.z, s.max.z) - Mathf.Max(b.min.z, s.min.z);
                    if (ox <= 0.05f || oz <= 0.05f) continue;
                    if (s.max.y > b.min.y + 0.05f) continue;
                    float gap = b.min.y - s.max.y;
                    if (best < 0f || gap < best) best = gap;
                }
                p.rec.gapToStandable = best;
            }
        }

        static void BuildBlockRec(BlockRuntime br)
        {
            var related = br.pieces.Where(p => p.rec.frameRelated).ToList();
            var frames = br.pieces.Where(p => p.rec.frame).ToList();

            // 外部脚本引用只在"其它安全检查都通过"的件上扫描，省时间。
            // 删除口径用 frameRelated：框架笼内的横梁/联系梁也算框架件，
            // 只要它没有 Collider/子物体/脚本引用就可以安全删；框架边单独统计。
            foreach (var p in br.pieces)
            {
                if (!p.rec.frameRelated) continue;
                if (p.rec.hasCollider || p.rec.hasChild || p.rec.hasScript) continue;
                if (!DeletableKind(p.go)) continue;
                p.rec.externalRef = HasExternalReference(p.go);
                p.rec.safeToDelete = !p.rec.externalRef;
            }

            int collidersBefore = br.root.GetComponentsInChildren<Collider>(true).Length;
            int frameColliders = related.Sum(p => p.rec.colliderCount);
            int safeColliders = br.pieces.Where(p => p.rec.safeToDelete).Sum(p => p.rec.colliderCount);
            int afterSafe = collidersBefore - safeColliders;
            int afterFull = Mathf.Max(0, collidersBefore - frameColliders);
            bool lose = afterFull <= 0;

            if (br.rec == null) br.rec = new BlockRec();
            br.rec.block = br.block;
            br.rec.boundsMin = new[] { br.bounds.min.x, br.bounds.min.y, br.bounds.min.z };
            br.rec.boundsMax = new[] { br.bounds.max.x, br.bounds.max.y, br.bounds.max.z };
            br.rec.frameBoundsMin = new[] { br.frameBounds.min.x, br.frameBounds.min.y, br.frameBounds.min.z };
            br.rec.frameBoundsMax = new[] { br.frameBounds.max.x, br.frameBounds.max.y, br.frameBounds.max.z };
            br.rec.beamPieces = br.pieces.Count;
            br.rec.candidates = br.pieces.Count(p => p.rec.geometryCandidate);
            br.rec.nearBoundaryCandidates = br.pieces.Count(p => p.rec.geometryCandidate && p.rec.nearBlockBoundary);
            br.rec.frameEdges = frames.Count;
            br.rec.frameRelated = related.Count;
            br.rec.safeToDelete = br.pieces.Count(p => p.rec.safeToDelete);
            br.rec.needsManual = related.Count(p => p.rec.colliderCount > 0);
            br.rec.unconfirmed = br.rec.candidates - related.Count;
            br.rec.collidersBefore = collidersBefore;
            br.rec.collidersAfterSafe = afterSafe;
            br.rec.collidersAfterFull = afterFull;
            br.rec.frameColliders = frameColliders;
            br.rec.losesLastBoundary = lose;
            br.rec.boundaryNote = lose
                ? "完整删除框架后将没有 Collider 边界（区块只靠框架件支撑）"
                : $"完整删除框架后仍余 {afterFull} 个非框架 Collider（柱子/墙/地面）；安全删除不改变碰撞";
            br.rec.boundaryNote += br.rec.closedFrame
                ? "；框架三轴封闭"
                : "；框架非三轴封闭（缺 Y 向长梁，呈上下圈/桁架形态）";
        }

        // ------------------------------------------------------------------ 判定工具

        static bool EdgeLike(Bounds b, Bounds cage, int longAxis, float tol)
        {
            for (int a = 0; a < 3; a++)
            {
                if (a == longAxis) continue;
                if (Mathf.Abs(AxisMin(b, a) - AxisMin(cage, a)) > tol &&
                    Mathf.Abs(AxisMax(b, a) - AxisMax(cage, a)) > tol) return false;
            }
            return true;
        }

        static bool Inside(Bounds b, Bounds cage, float tol)
        {
            return b.min.x >= cage.min.x - tol && b.max.x <= cage.max.x + tol &&
                   b.min.y >= cage.min.y - tol && b.max.y <= cage.max.y + tol &&
                   b.min.z >= cage.min.z - tol && b.max.z <= cage.max.z + tol;
        }

        static bool NearBoundary(Bounds b, Bounds block, float tol)
        {
            for (int a = 0; a < 3; a++)
                if (Mathf.Abs(AxisMin(b, a) - AxisMin(block, a)) <= tol ||
                    Mathf.Abs(AxisMax(b, a) - AxisMax(block, a)) <= tol) return true;
            return false;
        }

        static int MajorAxis(Vector3 s)
        {
            if (s.x >= s.y && s.x >= s.z) return 0;
            if (s.y >= s.z) return 1;
            return 2;
        }

        static float AxisMin(Vector3 v, int axis) => axis == 0 ? v.x : axis == 1 ? v.y : v.z;
        static float AxisMax(Vector3 v, int axis) => axis == 0 ? v.x : axis == 1 ? v.y : v.z;
        static float AxisMin(Bounds b, int axis) => axis == 0 ? b.min.x : axis == 1 ? b.min.y : b.min.z;
        static float AxisMax(Bounds b, int axis) => axis == 0 ? b.max.x : axis == 1 ? b.max.y : b.max.z;

        static Transform FindDirectChild(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                if (string.Equals(c.name, name, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }

        static bool IsUnder(Transform t, Transform root)
        {
            for (var x = t; x != null; x = x.parent)
                if (x == root) return true;
            return false;
        }

        /// <summary>Hide 的候选筛选：Scan 判定的框架相关件中，只保留满足安全边界的件。</summary>
        static List<PieceRuntime> SelectHideTargets(List<PieceRuntime> pieces)
        {
            var list = new List<PieceRuntime>();
            foreach (var p in pieces)
            {
                if (p == null || p.go == null || p.mr == null) continue;
                if (!p.rec.frameRelated) continue;
                if (!IsHideSafe(p)) continue;
                list.Add(p);
            }
            list.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.rec.block, b.rec.block);
                return c != 0 ? c : string.CompareOrdinal(a.rec.name, b.rec.name);
            });
            return list;
        }

        /// <summary>Hide 的安全边界：BOX_S_0x 根下、L1_Beam_ 前缀、长细比/截面达标；
        /// 明确排除 S_0x_* 原始件与 MAPSKIN_*/DEC_*；带 Collider 件由 P_IncludeManual 控制。</summary>
        static bool IsHideSafe(PieceRuntime p)
        {
            var go = p.go;
            if (go == null) return false;
            string n = go.name;

            if (!n.StartsWith("L1_Beam_", StringComparison.Ordinal)) return false;
            if (n.StartsWith("S_0", StringComparison.Ordinal)) return false;
            if (n.StartsWith("MAPSKIN_", StringComparison.Ordinal)) return false;
            if (n.StartsWith("DEC_", StringComparison.Ordinal)) return false;

            // root 是场景顶层对象；必须正好是 BOX_S_0x，防止误伤场景其它物体。
            var root = go.transform.root;
            if (root == null || !BlockRe.IsMatch(root.name)) return false;

            if (p.rec.slender < P_MinSlender) return false;
            if (p.rec.cross > P_MaxCross) return false;

            if (p.rec.colliderCount > 0 && !P_IncludeManual) return false;
            return true;
        }

        static bool DeletableKind(GameObject go)
        {
            if (go == null) return false;
            string n = go.name;
            if (n.StartsWith("S_0", StringComparison.Ordinal)) return false;   // 原始件一律不碰
            if (n.StartsWith("MAPSKIN_", StringComparison.Ordinal) ||
                n.StartsWith("DEC_", StringComparison.Ordinal)) return true;
            return PrefabUtility.IsPartOfPrefabInstance(go);
        }

        /// <summary>扫全场景 MonoBehaviour 的序列化对象引用，判断是否有脚本指向这件。
        /// 光看自身有没有 MonoBehaviour 不够：别的脚本可能用字段挂着这个挂点。</summary>
        static bool HasExternalReference(GameObject go)
        {
            var targets = new HashSet<UObject> { go, go.transform };
            foreach (var c in go.GetComponents<Component>())
                if (c != null) targets.Add(c);

            foreach (var mb in UObject.FindObjectsOfType<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                if (mb.gameObject == go || mb.transform.IsChildOf(go.transform)) continue;
                try
                {
                    var so = new SerializedObject(mb);
                    var it = so.GetIterator();
                    while (it.NextVisible(true))
                    {
                        if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var obj = it.objectReferenceValue;
                        if (obj != null && targets.Contains(obj)) return true;
                    }
                }
                catch
                {
                    // 丢失脚本 / 半序列化对象：跳过，不阻塞扫描
                }
            }
            return false;
        }

        static int TriCount(Mesh mesh)
        {
            if (mesh == null) return 0;
            int n = 0;
            try
            {
                for (int s = 0; s < mesh.subMeshCount; s++)
                    n += (int)(mesh.GetIndexCount(s) / 3);
            }
            catch
            {
                n = mesh.vertexCount / 3;
            }
            return n;
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static Transform FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var parts = path.Split('/');
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid()) return null;
            GameObject current = null;
            foreach (var root in scene.GetRootGameObjects())
                if (root != null && root.name == parts[0]) { current = root; break; }
            if (current == null) return null;
            for (int i = 1; i < parts.Length; i++)
            {
                Transform next = null;
                for (int c = 0; c < current.transform.childCount; c++)
                {
                    var child = current.transform.GetChild(c);
                    if (child.name == parts[i]) { next = child; break; }
                }
                if (next == null) return null;
                current = next.gameObject;
            }
            return current.transform;
        }

        static Transform FindBlockRoot(string block)
        {
            if (string.IsNullOrEmpty(block)) return null;
            string box = "BOX_" + block;   // S_01 -> BOX_S_01
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid()) return null;
            foreach (var root in scene.GetRootGameObjects())
                if (root != null && root.name == box) return root.transform;
            return null;
        }

        static Mesh LoadBuiltinMesh(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string n = name;
            if (n.StartsWith("Cube")) n = "Cube";
            else if (n.StartsWith("Sphere")) n = "Sphere";
            else if (n.StartsWith("Capsule")) n = "Capsule";
            else if (n.StartsWith("Cylinder")) n = "Cylinder";
            else if (n.StartsWith("Plane")) n = "Plane";
            else if (n.StartsWith("Quad")) n = "Quad";
            else return null;
            try { return Resources.GetBuiltinResource<Mesh>(n + ".fbx"); }
            catch { return null; }
        }

        static Vector3 V3(float[] a) => V3(a, Vector3.zero);

        static Vector3 V3(float[] a, Vector3 fallback)
        {
            if (a == null || a.Length < 3) return fallback;
            return new Vector3(a[0], a[1], a[2]);
        }

        static float Val(float[] a, int i, float fallback = 0f)
        {
            return a != null && i < a.Length ? a[i] : fallback;
        }

        // ------------------------------------------------------------------ 报告 / manifest

        static void WriteReport(FrameReport report)
        {
            Directory.CreateDirectory(OutDir);
            string path = Path.Combine(OutDir, CandidatesName);
            File.WriteAllText(path, JsonUtility.ToJson(report, true), new UTF8Encoding(false));
            Debug.Log($"[框架] 候选报告已写入 {path}");
        }

        /// <summary>写 FRAME_Hidden.json：记录路径、mesh/材质、Renderer 定位信息与 Collider 数量。
        /// JsonUtility 不能序列化对象引用，因此用 path + rendererIndex 记录"Renderer 引用"。</summary>
        static void WriteHiddenManifest(List<PieceRuntime> targets)
        {
            var man = new HiddenManifest
            {
                operation = "FrameCleanup.Hide",
                scene = P_Scene,
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                note = "只关闭 MeshRenderer.enabled；GameObject 与 Collider 均保留。恢复用 path + rendererIndex 定位渲染器。",
                count = targets.Count,
            };
            foreach (var p in targets)
            {
                var go = p.go;
                var mr = p.mr;
                var mrs = go.GetComponents<MeshRenderer>();
                int rendererIndex = -1;
                for (int i = 0; i < mrs.Length; i++)
                    if (mrs[i] == mr) { rendererIndex = i; break; }

                var cols = go.GetComponentsInChildren<Collider>(true);
                man.entries.Add(new HiddenEntry
                {
                    name = go.name,
                    block = p.rec.block,
                    path = p.rec.path,
                    parentPath = p.rec.parentPath,
                    rendererPath = PathOf(mr.transform),
                    rendererType = mr.GetType().FullName,
                    rendererIndex = rendererIndex,
                    rendererEnabledBefore = mr.enabled,
                    meshAssetPath = p.rec.meshAssetPath,
                    meshName = p.rec.meshName,
                    material = p.rec.material,
                    materialPaths = p.rec.materialPaths,
                    position = p.rec.position,
                    size = p.rec.size,
                    colliderCount = cols.Length,
                    colliderTypes = cols.Select(c => c.GetType().Name).ToArray(),
                });
            }
            Directory.CreateDirectory(OutDir);
            string path = Path.Combine(OutDir, HiddenName);
            File.WriteAllText(path, JsonUtility.ToJson(man, true), new UTF8Encoding(false));
            Debug.Log($"[框架·隐藏] 隐藏清单已写入 {path}（{man.count} 件）");
        }

        static void WriteRemovedManifest(ScanResult result, List<PieceRuntime> safe)
        {
            var man = new RemovedManifest
            {
                operation = "FrameCleanup.Remove",
                scene = P_Scene,
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                note = "恢复用：原 mesh 路径 / 材质路径 / 局部变换 / renderer 设置。安全件按定义没有 Collider。",
                count = safe.Count,
                tris = safe.Sum(p => p.rec.tris),
            };
            foreach (var p in safe)
            {
                var go = p.go;
                var mr = p.mr;
                var mats = mr.sharedMaterials;
                var matPaths = new List<string>();
                var matNames = new List<string>();
                if (mats != null)
                {
                    foreach (var m in mats)
                    {
                        if (m == null) continue;
                        matNames.Add(m.name);
                        string mp = AssetDatabase.GetAssetPath(m);
                        if (!string.IsNullOrEmpty(mp)) matPaths.Add(mp);
                    }
                }
                var lp = go.transform.localPosition;
                var lr = go.transform.localRotation;
                var ls = go.transform.localScale;
                man.entries.Add(new RemovedEntry
                {
                    name = go.name,
                    block = p.rec.block,
                    parentPath = PathOf(go.transform.parent),
                    position = new[] { p.rec.position[0], p.rec.position[1], p.rec.position[2] },
                    size = new[] { p.rec.size[0], p.rec.size[1], p.rec.size[2] },
                    localPosition = new[] { lp.x, lp.y, lp.z },
                    localRotation = new[] { lr.x, lr.y, lr.z, lr.w },
                    localScale = new[] { ls.x, ls.y, ls.z },
                    meshAssetPath = AssetDatabase.GetAssetPath(p.mf.sharedMesh),
                    meshName = p.mf.sharedMesh != null ? p.mf.sharedMesh.name : "",
                    material = string.Join(", ", matNames),
                    materialPaths = matPaths.ToArray(),
                    tris = p.rec.tris,
                    layer = go.layer,
                    tag = go.tag,
                    activeSelf = go.activeSelf,
                    siblingIndex = go.transform.GetSiblingIndex(),
                    isStatic = go.isStatic,
                    staticEditorFlags = (int)GameObjectUtility.GetStaticEditorFlags(go),
                    rendererEnabled = mr.enabled,
                    shadowCastingMode = (int)mr.shadowCastingMode,
                    receiveShadows = mr.receiveShadows,
                    lightProbeUsage = (int)mr.lightProbeUsage,
                    reflectionProbeUsage = (int)mr.reflectionProbeUsage,
                    renderingLayerMask = (int)mr.renderingLayerMask,
                    sortingOrder = mr.sortingOrder,
                });
            }
            Directory.CreateDirectory(OutDir);
            string path = Path.Combine(OutDir, RemovedName);
            File.WriteAllText(path, JsonUtility.ToJson(man, true), new UTF8Encoding(false));
            Debug.Log($"[框架·删除] 删除清单已写入 {path}（{man.count} 件，三角形 -{man.tris}）");
        }

        static void LogSummary(FrameReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[框架扫描] 区块   Beam件  细长候选  贴块边界  框架边  相关件  可安全删  需人工确认  未确认  " +
                          "Collider(前/安全删后/全删后)  三轴封闭");
            foreach (var b in report.blocks)
            {
                sb.AppendLine($"  {b.block,-6} {b.beamPieces,6} {b.candidates,8} {b.nearBoundaryCandidates,9} " +
                              $"{b.frameEdges,7} {b.frameRelated,6} {b.safeToDelete,9} {b.needsManual,11} {b.unconfirmed,6}  " +
                              $"{b.collidersBefore}/{b.collidersAfterSafe}/{b.collidersAfterFull,-6} {b.closedFrame}");
                sb.AppendLine($"      轴计数 X={b.axisX} Y={b.axisY} Z={b.axisZ}；{b.boundaryNote}");
                int candCount = report.pieces.Count(p => p.block == b.block && p.geometryCandidate);
                int candOff = report.pieces.Count(p => p.block == b.block && p.geometryCandidate && !p.rendererEnabled);
                sb.AppendLine($"      候选件 renderer.enabled=false：{candOff}/{candCount}");
            }
            sb.AppendLine($"  合计：Beam 件 {report.blocks.Sum(b => b.beamPieces)}，细长候选 {report.blocks.Sum(b => b.candidates)}，" +
                          $"框架边 {report.blocks.Sum(b => b.frameEdges)}，相关 {report.blocks.Sum(b => b.frameRelated)}，" +
                          $"可安全删 {report.blocks.Sum(b => b.safeToDelete)}，需人工确认 {report.blocks.Sum(b => b.needsManual)}。");
            if (report.manual != null && report.manual.Count > 0)
            {
                sb.AppendLine("  需人工确认（有 Collider，不自动删）示例：");
                foreach (var p in report.manual.Take(24))
                    sb.AppendLine($"    {p.block} {p.name}  Collider={p.colliderCount}  " +
                                  $"位置=({p.position[0]:F1},{p.position[1]:F1},{p.position[2]:F1})  材质={p.material}");
                if (report.manual.Count > 24)
                    sb.AppendLine($"    ... 其余 {report.manual.Count - 24} 件见 FRAME_Candidates.json 的 manual 数组");
            }

            // 逐件打印 renderer.enabled 现状，便于确认 Hide 幂等与恢复效果。
            sb.AppendLine("  候选件 renderer.enabled 现状（逐件）：");
            foreach (var p in report.pieces.Where(p => p.geometryCandidate)
                                           .OrderBy(p => p.block).ThenBy(p => p.name))
                sb.AppendLine($"    {p.block} {p.name,-44} slender={p.slender,7:F1} cross={p.cross,5:F2} " +
                              $"Collider={p.colliderCount} frameRelated={p.frameRelated} " +
                              $"renderer.enabled={p.rendererEnabled}");
            Debug.Log(sb.ToString());
        }

        // ------------------------------------------------------------------ 参数 / 场景

        static string OutDir
        {
            get
            {
                if (!string.IsNullOrEmpty(P_OutDir)) return P_OutDir;
                // Assets 的上两级 = 外层仓库根（F:/VSCode/taptap-game-jem），与既有审计工具口径一致
                var root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
                return Path.Combine(root, "_session_audit");
            }
        }

        static void EnsureScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == P_Scene) return;
            if (cur.IsValid() && cur.isDirty && !string.IsNullOrEmpty(cur.path))
                EditorSceneManager.SaveScene(cur);       // 未命名的空场景保存会抛异常，故要求 path 非空
            EditorSceneManager.OpenScene(P_Scene, OpenSceneMode.Single);
            Debug.Log($"[框架] 已打开场景 {P_Scene}");
        }

        static void SaveScene()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid()) return;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[框架] 场景已存盘：{scene.path}");
        }

        static void ParseArgs()
        {
            P_Tol = 1.5f;
            P_MinSlender = 6f;
            P_MaxCross = 2.5f;
            P_MinEdges = 4;
            P_Run = false;
            P_Save = true;
            P_IncludeManual = true;
            P_Scene = DefaultScene;
            P_OutDir = null;

            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string k = a[i].ToLowerInvariant();
                string v = i + 1 < a.Length ? a[i + 1] : null;
                switch (k)
                {
                    case "-run": P_Run = Flag(v); i++; break;
                    case "-save": P_Save = Flag(v); i++; break;
                    case "-include-manual": P_IncludeManual = Flag(v); i++; break;
                    case "-scene": if (v != null) P_Scene = v; i++; break;
                    case "-out": if (v != null) P_OutDir = v; i++; break;
                    case "-tol": P_Tol = Fl(v, P_Tol); i++; break;
                    case "-slender": P_MinSlender = Fl(v, P_MinSlender); i++; break;
                    case "-cross": P_MaxCross = Fl(v, P_MaxCross); i++; break;
                }
            }
            P_Tol = Mathf.Clamp(P_Tol, 0.1f, 10f);
            P_MinSlender = Mathf.Max(1.5f, P_MinSlender);
            P_MaxCross = Mathf.Max(0.05f, P_MaxCross);
        }

        static bool Flag(string v)
        {
            if (string.IsNullOrEmpty(v)) return true;
            return v != "0" && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
        }

        static float Fl(string v, float fallback)
        {
            float f;
            return v != null && float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out f) ? f : fallback;
        }

        // ------------------------------------------------------------------ 序列化结构

        [Serializable]
        public class PieceRec
        {
            public string name, block, path, parentPath, material, longAxis, meshAssetPath, meshName;
            public string[] materialPaths;
            public float[] position, size, boundsMin, boundsMax;
            public float[] localPosition, localRotation, localScale;
            public float longSize, slender, cross, gapToStandable;
            public int tris, colliderCount;
            public bool hasCollider, hasChild, hasScript, externalRef;
            public bool nearBlockBoundary, geometryCandidate, edgeLike, frame, frameRelated, safeToDelete;
            public bool rendererEnabled;
        }

        [Serializable]
        public class BlockRec
        {
            public string block, boundaryNote;
            public float[] boundsMin, boundsMax, frameBoundsMin, frameBoundsMax;
            public int beamPieces, candidates, nearBoundaryCandidates, frameEdges, frameRelated;
            public int safeToDelete, needsManual, unconfirmed;
            public int axisX, axisY, axisZ;
            public bool closedFrame, losesLastBoundary;
            public int collidersBefore, collidersAfterSafe, collidersAfterFull, frameColliders;
        }

        [Serializable]
        public class FrameReport
        {
            public string generatedAt, scene, note;
            public float boundaryTol, minSlender, maxCross;
            public List<BlockRec> blocks = new List<BlockRec>();
            public List<PieceRec> pieces = new List<PieceRec>();
            public List<PieceRec> manual = new List<PieceRec>();
        }

        [Serializable]
        public class HiddenEntry
        {
            public string name, block, path, parentPath, rendererPath, rendererType;
            public string material, meshAssetPath, meshName;
            public string[] materialPaths, colliderTypes;
            public float[] position, size;
            public int rendererIndex, colliderCount;
            public bool rendererEnabledBefore;
        }

        [Serializable]
        public class HiddenManifest
        {
            public string operation, scene, timestamp, note;
            public int count;
            public List<HiddenEntry> entries = new List<HiddenEntry>();
        }

        [Serializable]
        public class RemovedEntry
        {
            public string name, block, parentPath, material, meshAssetPath, meshName, tag;
            public string[] materialPaths;
            public float[] position, size, localPosition, localRotation, localScale;
            public int tris, layer, siblingIndex, staticEditorFlags;
            public int shadowCastingMode, lightProbeUsage, reflectionProbeUsage, renderingLayerMask, sortingOrder;
            public bool activeSelf, isStatic, rendererEnabled, receiveShadows;
        }

        [Serializable]
        public class RemovedManifest
        {
            public string operation, scene, timestamp, note;
            public int count, tris;
            public List<RemovedEntry> entries = new List<RemovedEntry>();
        }
    }
}
