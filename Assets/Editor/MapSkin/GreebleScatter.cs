// GreebleScatter.cs — 功能副形（GREEBLE KIT v1）散布装配：把 exports/greeble 的工业功能件
//   按 SCATTER_PLAN.json 的散布点装配进场景，让墙面 / 平台边缘长满"真实功能件"。
//
// 一句话：plan 的每个点 = 一件功能件；按 (block, ID) 分组 → 同组合并成一个 mesh
//   （Mesh.CombineMeshes，矩阵 = TRS(pos, LookRotation(forward), scale)）→ 一个 MeshRenderer 引一份材质。
//   3970 个点最终只有几十个对象、几十份材质资产，而不是 3970 个 GameObject。
//
// 三件事：
//   ① 素材入库（幂等）：FBX → Assets/Art/Environment/Greeble/<ID>/；
//      5 张贴图（BC/N/MS/AO/E）→ Assets/Art/Images/Greeble/<ID>/，并配好导入设置
//      （_BC sRGB、_N NormalMap、_MS/_AO/_E 线性、512、CompressedHQ、mipmap）；
//      批量导入与批量 SaveAndReimport 都用 StartAssetEditing/StopAssetEditing 包住，避免逐张重导入卡死。
//   ② 材质：每件一份 M_Greeble_<ID>.mat（shader = OurFunction/NPR/Deferred Toon；toon 三件套与
//      MapSkinApply.cs 同值；BC/N/MS/AO/E 各挂一槽 + 对应 keyword）。已存在则就地复用。
//   ③ 装配：根 MAPSKIN_Greeble（identity）→ <block>/<ID> 两层，合并 mesh 写
//      Assets/Art/EnvSkins/Greeble/<block>_<ID>.asset。生成物不带 Collider、不写 prefab。
//
// 栏杆特殊（性能）：RAILING_2m 默认 -railmode procedural 程序化生成低模栏杆（顶横杆 + 一根中横杆 +
//   两根立柱 = 4 个 45° 倒角盒 = 176 tri/件，沿 up×forward 方向按 length_u_m 长，法线 flat；
//   底板并入立柱底部，省 43% 三角与顶点）；-railmode sparse 在程序化基础上**每 2 件放 1 件**（远景区块）；
//   -railmode fbx 用原 FBX（1372 tri/件）；-railmode skip 不生成栏杆。
//   三角预算默认 300 万（v2 计划全量 ≈ 2.74M tri；-budget 可改，压回 2500000 会截断计划尾部），
//   触顶即停并打印实际用量与未放置件数。
//
// v2 追加（评审三条 + 压体积）：
//   ① 接头套环：run 类（CABLE_TRAY_/CONDUIT_/PIPE_）每件两端各加一圈薄套环（0.06 宽、比本体外廓大
//      10mm、45° 倒角，4 根倒角盒成圈 = 176 tri/端），用 Trim 材质单独合并成 <block>_<ID>_Trim；
//      同时把离所在面 u 边界 < 0.6m 的 run 件端头吸附到边界（贴齐 u=0 / u=1），让转角两侧的件对上。
//   ② 高度节奏：墙面件按确定性哈希给高度抖动（kick ±0.15m / waist ±0.25m，同面同档共用一个抖动值），
//      并把约 20% 的 (面, 档) 整组换到第三条高度 floor+1.2m（tier = mid，±0.12m）。
//   ③ V2 计划直读：天花/地面件的 forward = [0,±1,0]（竖直）走专门姿态（局部 +Y 朝 forward 保持自然
//      "上"、水平朝向取 run_dir/wall_dir，绝不翻滚）；新增 tier（mid / corner / ceiling / floor）不跳过。
//   ④ 栏杆 4 盒（见上）+ -railmode sparse。
//
// v3 追加（出图评审）：
//   ⑤ 栏杆换深钢：4 个倒角盒换成 顶杆 0.05×0.05 / 中杆 0.03×0.03 / 立柱 0.06×1.08×0.06，顶面 1.02m，
//      材质单独一份 Assets/Art/Materials/MapSkin/Greeble/M_Greeble_Railing.mat
//      （_BaseColor 0.16/0.17/0.19、_Metallic 0.9、_Smoothness 0.35），不再用亮白件材质。
//   ⑥ -trayflip 0|1：天花桥架局部 +Z 取反开关（0 = 默认：背板贴顶、箱体下挂；1 = 字面 +Z 朝上），
//      只为 A/B 一跑就知道，不用改代码。
//   ⑦ run 件中段托架：桥架每件 1 颗、线槽每件 2 颗 SUPPORT_BRACKET 实例（件中心沿 mount_normal 贴墙，
//      线槽两颗在 ±长/6），几何并进该件所在组的 mesh，不新建对象；立管自带墙卡箍不加。
//
// 菜单：MapSkin/功能件/① 干跑  ② 执行  ③ 清除
// 批处理（均为 public static void、无参，配合 -executeMethod）：
//   -executeMethod MapSkin.GreebleScatter.DryRun   只读体检：清单 / 三角预算 / 素材在位情况，零写盘
//   -executeMethod MapSkin.GreebleScatter.Run      素材入库 + 建材质 + 合并装配 + 存场景 + 落盘清单
//   -executeMethod MapSkin.GreebleScatter.Clear    删 MAPSKIN_Greeble 根与生成的 mesh 资产
// 参数：-scene / -plan / -manifest / -budget / -railmode / -only / -block / -limit / -out / -save
//       / -faces / -usnap / -rhythm
//
// 不碰什么（硬约束）：任何 Collider、场景原有对象、原有材质与贴图资产、其它工具的生成物；
//   不写 prefab（SaveAsPrefabAsset 一次都不调）；Clear 只删自己名下的根与 mesh 资产。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    public static class GreebleScatter
    {
        // ==================================================================== 常量

        /// <summary>本工具的生成物根名（幂等重跑与 Clear 的唯一抓手）。</summary>
        const string RootName = "MAPSKIN_Greeble";

        const string DefaultScenePath = "Assets/Scenes/Level1_Skin.unity";
        const string DefaultSrcRoot = "F:/VSCode/工业级渲染/exports/greeble";
        const string DefaultPlanPath = DefaultSrcRoot + "/SCATTER_PLAN.json";
        const string DefaultManifestPath = DefaultSrcRoot + "/GREEBLE_MANIFEST.json";
        const string DefaultOutPath = "F:/VSCode/taptap-game-jem/_session_audit/greeble_scatter.json";

        /// <summary>工程内落点：FBX / 贴图 / 材质 / 合并 mesh。</summary>
        const string FbxDir = "Assets/Art/Environment/Greeble";
        const string TexDir = "Assets/Art/Images/Greeble";
        const string MatDir = "Assets/Art/Materials/MapSkin/Greeble";
        const string MeshDir = "Assets/Art/EnvSkins/Greeble";

        /// <summary>NPR 母 shader（与 MapSkinApply.cs 同口径）。</summary>
        const string NprShaderName = "OurFunction/NPR/Deferred Toon";
        const string NprShaderPath = "Assets/Shaders/NPR/NPRBase.shader";

        /// <summary>toon 三件套（规范 §2.1 标准参数，数值照抄 MapSkinApply.cs）。</summary>
        static readonly Color ToonShadowColor = new Color(0.30f, 0.48f, 0.53f);
        const float ToonSoftness = 0.15f;
        const float ToonThreshold = 1.0f;

        /// <summary>贴图导入设置（功能件是小件，512 够用且省显存）。</summary>
        const int TexMaxSize = 512;

        /// <summary>5 个贴图通道：BC 基色 / N 法线 / MS 金属-粗糙 / AO 遮蔽 / E 自发光。</summary>
        static readonly string[] Channels = { "BC", "N", "MS", "AO", "E" };

        /// <summary>程序化栏杆：4 个 45° 倒角盒（顶杆 + 一根中杆 + 两根立柱）× 44 三角 = 176 三角/件。</summary>
        const int RailBoxes = 4;
        const int RailTrisPerPiece = RailBoxes * ChamferTris;

        /// <summary>栏杆尺寸（v3：顶面 1.02m 贴玩家视高、构件细一号、深钢材质）。</summary>
        const float RailTopY = 1.02f;        // 栏杆顶面高（顶杆上表面）
        const float RailTopSize = 0.05f;     // 顶杆截面 0.05×0.05
        const float RailMidSize = 0.03f;     // 中杆截面 0.03×0.03
        const float RailMidY = 0.55f;        // 中杆中心高
        const float RailPostSize = 0.06f;    // 立柱截面 0.06×0.06
        const float RailPostH = 1.08f;       // 立柱高（顶端与顶杆齐平 1.02，底端埋地 0.06）

        /// <summary>栏杆深钢材质（v3：不再是亮白桁架）。</summary>
        static readonly Color RailBaseColor = new Color(0.16f, 0.17f, 0.19f);
        const float RailMetallic = 0.9f;
        const float RailSmoothness = 0.35f;
        const string RailMatPath = MatDir + "/M_Greeble_Railing.mat";

        // ---- v3：run 件中段托架（SUPPORT_BRACKET 实例，并进该件所在组的 mesh，不新建对象）----
        const string BracketId = "SUPPORT_BRACKET";
        const float BracketWallEps = 0.012f; // 沿 forward 的小偏移：躲开件背板共面 z-fighting

        /// <summary>倒角盒固定三角数：6 面 12 + 12 棱 24 + 8 角 8 = 44。</summary>
        const int ChamferTris = 44;

        /// <summary>RAILING 类 ID 的识别口径（plan 里就是 RAILING_2m）。</summary>
        const string RailIdKey = "RAILING";

        /// <summary>run 类（沿走向成段的管 / 桥 / 线槽）ID 关键词：接头套环与边界吸附只对它们做。</summary>
        static readonly string[] RunKeys = { "CABLE_TRAY", "CONDUIT", "PIPE" };

        // ---- 接头套环（每件两端各一圈；4 根 45° 倒角盒成方环）----
        const float CollarWidth = 0.06f;      // 沿走向宽（端面与件端齐平）
        const float CollarGap = 0.001f;       // 环内壁离本体 1mm（防 z-fighting）
        const float CollarBar = 0.010f;       // 环壁厚 10mm → 外廓比本体大 11mm（要求 8–12mm）
        const int CollarTrisPerPiece = 8 * ChamferTris;   // 两端 × 4 根 = 352
        const float CollarBevel = 0.0025f;    // 薄环的 45° 倒角宽

        // ---- 高度节奏（只动墙面 kick / waist / mid）----
        const float TierKickH = 0.35f;        // 踢脚线相对楼面高
        const float TierMidH = 1.2f;          // v2 新增第三档
        const float TierWaistH = 2.4f;        // 腰线
        const float JitKick = 0.15f;          // kick ±0.15m
        const float JitMid = 0.12f;           // mid  ±0.12m
        const float JitWaist = 0.25f;         // waist ±0.25m
        const float MidShare = 0.20f;         // 约 20% 的 (面, 档) 整组改走 mid

        /// <summary>竖直 forward 判定：|dot(forward, up)| 超过此值（夹角 &lt; 5°）即按天花/地面件处理。</summary>
        const float VerticalCos = 0.9962f;

        /// <summary>Trim 件（接头套环）材质：参数照抄工程 Trim 口径
        /// Assets/Art/Materials/MapSkin/Facade/M_Facade_Trim.mat（_BaseColor 0.40/0.405/0.42、
        /// _Smoothness 0.55、_Metallic 0.85）+ NPR toon 三件套。</summary>
        static readonly Color TrimBaseColor = new Color(0.400f, 0.405f, 0.420f);
        const float TrimSmoothness = 0.55f;
        const float TrimMetallic = 0.85f;
        const string TrimMatPath = MatDir + "/M_Greeble_Trim.mat";

        /// <summary>面规格（含 outline）默认位置：u 边界吸附要用它把 u 换算成米。</summary>
        const string DefaultFaceFlatPath = "F:/VSCode/taptap-game-jem/_session_audit/face_flat.json";

        // ==================================================================== 参数（命令行 -xxx 可覆盖）

        static string P_Scene = DefaultScenePath;
        static string P_Plan = DefaultPlanPath;
        static string P_Manifest = DefaultManifestPath;
        static long P_Budget = 3000000;              // -budget       三角预算上限（v2 全量约 2.74M，故默认抬到 3.0M；压回 2500000 会截断计划尾部）
        static string P_RailMode = "procedural";     // -railmode     procedural | sparse | fbx | skip
        static readonly List<string> P_Only = new List<string>();   // -only         只装某类（可逗号分隔/多次）
        static string P_Block = null;                // -block        只装某区块
        static int P_Limit = 0;                      // -limit        只装前 N 件（0 = 全部）
        static string P_Out = DefaultOutPath;        // -out          运行清单落盘位置
        static int P_Save = 1;                       // -save 0       不存场景（只改内存，调试用）
        static string P_Faces = DefaultFaceFlatPath; // -faces        face_flat.json（u 边界吸附用；缺文件则跳过吸附）
        static float P_USnap = 0.6f;                 // -usnap        run 件离面 u 边界小于此值（米）→ 端头贴边界；0 = 关
        static int P_Rhythm = 1;                     // -rhythm 0     关掉高度抖动 / mid 迁移（A/B 对照用）
        static int P_TrayFlip = 0;                   // -trayflip 1   天花桥架局部 +Z 取反（A/B 验证"背板贴顶"读法）

        // ==================================================================== 菜单

        [MenuItem("MapSkin/功能件/① 干跑")]
        public static void DryRunMenu()
        {
            DryRun();
        }

        [MenuItem("MapSkin/功能件/② 执行")]
        public static void RunMenu()
        {
            ParseArgs();
            if (!Application.isBatchMode && !EditorUtility.DisplayDialog("功能件装配",
                    $"按 {P_Plan} 把功能副形装配进 {P_Scene}：\n" +
                    $"· 素材入库（FBX → {FbxDir}/、贴图 → {TexDir}/）\n" +
                    $"· 材质 {MatDir}/M_Greeble_<ID>.mat\n" +
                    $"· 按 (区块, 类别) 合并成 mesh → {MeshDir}/<区块>_<ID>.asset\n" +
                    $"· 栏杆模式 {P_RailMode}，三角预算 {P_Budget:N0}\n\n继续？", "执行", "取消"))
                return;
            Run();
        }

        [MenuItem("MapSkin/功能件/③ 清除")]
        public static void ClearMenu()
        {
            if (!Application.isBatchMode && !EditorUtility.DisplayDialog("清除功能件",
                    $"删除场景里的 {RootName} 根对象与 {MeshDir}/ 下本工具生成的 mesh 资产。\n" +
                    "入库的 FBX / 贴图 / 材质保留（下次装配直接复用）。\n\n继续？", "清除", "取消"))
                return;
            Clear();
        }

        // ==================================================================== 批处理入口

        /// <summary>干跑：清点 plan / manifest、算三角预算、体检素材是否在位。零写盘、零改场景。
        /// 批处理：-executeMethod MapSkin.GreebleScatter.DryRun</summary>
        public static void DryRun()
        {
            ParseArgs();
            if (!EnsureScene(false)) return;

            var ctx = new Ctx();
            if (!LoadSources(ctx)) return;                      // 读盘 + 过滤（无副作用）
            ApplyHeightRhythm(ctx);                             // v2②：确定性高度抖动 + 20% 迁入 mid
            ApplyEdgeSnap(ctx);                                 // v2①：run 件贴齐面 u 边界
            ctx.Assets = CheckAssets(ctx, dryRun: true);        // 只看文件在不在，不复制不导入
            ApplyBudget(ctx);

            var sb = new StringBuilder();
            sb.AppendLine($"[功能件] 干跑（零写盘）：scene={P_Scene}");
            AppendPlanSummary(sb, ctx);
            AppendIdTable(sb, ctx);
            AppendAssetCheck(sb, ctx);
            AppendBudgetLine(sb, ctx);
            AppendSceneState(sb);
            Debug.Log(sb.ToString());
        }

        /// <summary>执行：素材入库 + 建材质 + 合并装配 + 存场景 + 落盘清单。
        /// 批处理：-executeMethod MapSkin.GreebleScatter.Run</summary>
        public static void Run()
        {
            var t0 = DateTime.Now;
            ParseArgs();
            if (!EnsureScene(true)) return;

            var ctx = new Ctx();
            if (!LoadSources(ctx)) return;
            ApplyHeightRhythm(ctx);                             // v2②：确定性高度抖动 + 20% 迁入 mid
            ApplyEdgeSnap(ctx);                                 // v2①：run 件贴齐面 u 边界

            int removed = RemoveRoot(false);                    // 幂等：重跑先删同名根
            if (removed > 0) Debug.Log($"[功能件] 幂等：先删旧根 {RootName} ×{removed}");
            int oldMeshes = ClearMeshAssets();                  // 幂等：旧合并 mesh 一并清掉重来
            if (oldMeshes > 0) Debug.Log($"[功能件] 幂等：先删旧 mesh 资产 ×{oldMeshes}");

            var assets = CheckAssets(ctx, dryRun: false);       // 入库 + 导入设置 + 材质
            ctx.Assets = assets;
            ApplyBudget(ctx);

            if (ctx.Items.Count == 0)
            {
                Debug.LogWarning("[功能件] 没有可装点（预算 0 / 全被过滤 / 素材缺失），未生成任何对象。");
                if (P_Save == 1 && removed > 0) SaveScene();
                WriteOut(ctx, "run", assets);
                return;
            }

            BuildGroups(ctx);
            var built = BuildMeshes(ctx, assets);
            CreateObjects(ctx, built, assets);

            if (P_Save == 1) SaveScene();
            WriteOut(ctx, "run", assets);

            var sb = new StringBuilder();
            sb.AppendLine($"[功能件] 装配完成，用时 {(DateTime.Now - t0).TotalSeconds:F1}s");
            AppendPlanSummary(sb, ctx);
            AppendIdTable(sb, ctx);
            AppendBudgetLine(sb, ctx);
            sb.AppendLine($"[落点] {MeshDir}/ 下 mesh 资产 {ctx.MeshAssets} 份；场景对象 {ctx.Objects} 个（根 {RootName}）");
            sb.AppendLine($"[Trim] 接头套环 {ctx.Collars} 圈；托架 {ctx.Brackets} 颗（已并进各自组 mesh）。" +
                          $"Trim 材质 {(ctx.TrimMat != null ? TrimMatPath : "缺失（shader 没找到，用默认材质）")}；" +
                          $"栏杆材质 {(ctx.RailMat != null ? RailMatPath : "缺失")}");
            sb.AppendLine($"[清单] {P_Out}");
            AppendSkipReasons(sb, ctx);
            Debug.Log(sb.ToString());
        }

        /// <summary>清除：删场景里的 MAPSKIN_Greeble 根与生成的 mesh 资产（入库的 FBX/贴图/材质保留）。
        /// 批处理：-executeMethod MapSkin.GreebleScatter.Clear</summary>
        public static void Clear()
        {
            ParseArgs();
            if (!EnsureScene(false)) return;

            int objs = RemoveRoot(false);
            int meshes = ClearMeshAssets();
            if (P_Save == 1 && (objs > 0 || meshes > 0)) SaveScene();

            Debug.Log($"[功能件] 已清除：根对象 {objs} 个（{RootName}）、mesh 资产 {meshes} 份（{MeshDir}/）。" +
                      "入库素材与材质未动。");
        }

        // ==================================================================== 场景

        /// <summary>确保打开的是 -scene 指定的场景。踩过的坑：batchmode 下 Unity 默认开的是空场景，
        /// 不先打开目标场景就会"扫到 0 个对象 / 建错场景"。</summary>
        static bool EnsureScene(bool forWrite)
        {
            if (!File.Exists(AssetPathToAbs(P_Scene)))
            {
                Debug.LogError($"[功能件] 场景不存在：{P_Scene}");
                return false;
            }

            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == P_Scene) return true;

            if (cur.IsValid() && cur.isDirty)
            {
                if (string.IsNullOrEmpty(cur.path))
                {
                    // batchmode 启动时的未命名空场景：直接切目标场景即可，没有需要保存的内容
                }
                else if (forWrite)
                {
                    EditorSceneManager.SaveScene(cur);
                }
                else
                {
                    Debug.LogError($"[功能件] 当前场景 {cur.path} 有未保存改动；为保证零写盘/不丢改动已中止。" +
                                   "请先保存并打开目标场景。");
                    return false;
                }
            }

            EditorSceneManager.OpenScene(P_Scene, OpenSceneMode.Single);
            Debug.Log($"[功能件] 已打开场景 {P_Scene}");
            return true;
        }

        static void SaveScene()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid()) return;
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[功能件] 已保存场景 {scene.path}");
        }

        /// <summary>删掉场景里名叫 MAPSKIN_Greeble 的对象（只认这个名字，绝不碰别的）。
        /// 用全场景 Transform 扫描是为了兼容被误拖进层级的旧实例。</summary>
        static int RemoveRoot(bool save)
        {
            var scene = EditorSceneManager.GetActiveScene();
            var doomed = new List<GameObject>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                if (t == null) continue;
                if (!t.gameObject.scene.IsValid() || t.gameObject.scene != scene) continue;
                if (t.name != RootName) continue;
                doomed.Add(t.gameObject);
            }
            foreach (var go in doomed) UnityEngine.Object.DestroyImmediate(go);
            if (save && doomed.Count > 0) SaveScene();
            return doomed.Count;
        }

        /// <summary>删 MeshDir 下本工具生成的 mesh 资产（只删该目录直属文件，不递归、不动别处）。</summary>
        static int ClearMeshAssets()
        {
            if (!AssetDatabase.IsValidFolder(MeshDir)) return 0;
            var paths = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Mesh", new[] { MeshDir }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(p)) continue;
                if (!p.StartsWith(MeshDir + "/", StringComparison.Ordinal)) continue;
                string rest = p.Substring(MeshDir.Length + 1);
                if (rest.Contains("/")) continue;                       // 子目录里的不是本工具的
                paths.Add(p);
            }
            int n = 0;
            foreach (var p in paths)
                if (AssetDatabase.DeleteAsset(p)) n++;
            return n;
        }

        // ==================================================================== 阶段 1：清点（plan + manifest + 过滤）

        /// <summary>读 plan/manifest，把点过滤成候选集（-only/-block/合法性），并记下过滤原因。零副作用。</summary>
        static bool LoadSources(Ctx ctx)
        {
            if (!File.Exists(P_Plan))
            {
                Debug.LogError($"[功能件] 找不到散布计划：{P_Plan}");
                return false;
            }
            if (!File.Exists(P_Manifest))
            {
                Debug.LogError($"[功能件] 找不到素材清单：{P_Manifest}");
                return false;
            }

            PlanDoc plan = null;
            ManifestDoc man = null;
            try
            {
                plan = JsonUtility.FromJson<PlanDoc>(File.ReadAllText(P_Plan));
                man = JsonUtility.FromJson<ManifestDoc>(File.ReadAllText(P_Manifest));
            }
            catch (Exception e)
            {
                Debug.LogError($"[功能件] JSON 解析失败：{e.Message}");
                return false;
            }
            if (plan == null || plan.items == null || plan.items.Count == 0)
            {
                Debug.LogError($"[功能件] plan 里没有 items（结构不符：JsonUtility 只吃与字段名一致的扁平结构）：{P_Plan}");
                return false;
            }
            if (man == null || man.items == null || man.items.Count == 0)
            {
                Debug.LogError($"[功能件] manifest 里没有 items：{P_Manifest}");
                return false;
            }

            ctx.SrcRoot = SrcRoot();
            foreach (var mi in man.items)
                if (!string.IsNullOrEmpty(mi.id)) ctx.Man[mi.id] = mi;

            ctx.PlanItems = plan.items.Count;
            int filtered = 0;
            int railIdx = 0;                                   // -railmode sparse 的取样计数（plan 顺序）
            bool railSparse = P_RailMode == "sparse";
            foreach (var it in plan.items)
            {
                if (it == null) { filtered++; continue; }
                if (string.IsNullOrEmpty(it.greeble)) { filtered++; continue; }
                if (P_Only.Count > 0 && !P_Only.Contains(it.greeble)) { filtered++; continue; }
                if (!string.IsNullOrEmpty(P_Block) &&
                    !string.Equals(it.block, P_Block, StringComparison.Ordinal)) { filtered++; continue; }
                if (!ctx.Man.ContainsKey(it.greeble))
                {
                    filtered++;
                    ctx.Skip($"清单里没有该类（{it.greeble}）");
                    continue;
                }
                if (IsRail(it.greeble) && P_RailMode == "skip")
                {
                    filtered++;
                    ctx.Skip("railmode=skip（栏杆不生成）");
                    continue;
                }
                if (IsRail(it.greeble) && railSparse && (railIdx++ % 2) != 0)
                {
                    filtered++;
                    ctx.Skip("railmode=sparse（每 2 件放 1 件）");
                    continue;
                }
                if (!ValidItem(it, out string bad))
                {
                    filtered++;
                    ctx.Skip(bad);
                    continue;
                }
                ctx.Candidates.Add(it);
            }
            ctx.FilteredOut = filtered;

            // -limit：按 plan 顺序取前 N 件
            if (P_Limit > 0 && ctx.Candidates.Count > P_Limit)
            {
                ctx.Skip($"-limit {P_Limit} 截断", ctx.Candidates.Count - P_Limit);
                ctx.Candidates.RemoveRange(P_Limit, ctx.Candidates.Count - P_Limit);
            }
            return true;
        }

        /// <summary>点的合法性：坐标/朝向必须是有限的 3 元组，forward 非零（否则姿态无法确定）。
        /// forward 竖直（天花/地面件）本身合法——姿态由 Pose() 换 up 兜底；但程序化栏杆的走向
        /// = up×forward，forward 竖直时走向退化，这类点单独剔除。</summary>
        static bool ValidItem(PlanItem it, out string bad)
        {
            bad = null;
            if (!Finite3(it.pos)) { bad = "pos 非法（缺字段/NaN）"; return false; }
            if (!Finite3(it.forward)) { bad = "forward 非法（缺字段/NaN）"; return false; }
            var f = new Vector3(it.forward[0], it.forward[1], it.forward[2]);
            if (f.sqrMagnitude < 1e-8f) { bad = "forward ≈ 0，姿态无法确定"; return false; }
            if (IsRail(it.greeble) && RailIsProcedural &&
                Mathf.Abs(Vector3.Dot(f.normalized, Vector3.up)) > VerticalCos)
            { bad = "栏杆 forward 竖直，程序化走向无法确定"; return false; }
            return true;
        }

        static bool Finite3(float[] a)
        {
            if (a == null || a.Length < 3) return false;
            for (int i = 0; i < 3; i++)
                if (float.IsNaN(a[i]) || float.IsInfinity(a[i])) return false;
            return true;
        }

        /// <summary>源素材根目录：以 -manifest 所在目录为准（自带默认值兜底）。</summary>
        static string SrcRoot()
        {
            string dir = null;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(P_Manifest)); } catch { /* 非法路径 → 用默认 */ }
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir.Replace('\\', '/');
            return DefaultSrcRoot;
        }

        static bool IsRail(string id) =>
            !string.IsNullOrEmpty(id) && id.IndexOf(RailIdKey, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>run 类（沿走向成段的管 / 桥 / 线槽）：接头套环 + u 边界吸附的范围。
        /// 注意 VENT_GRILLE / ACCESS_HATCH 虽然 role 也是 "run"，但它们是面板件，不套环。</summary>
        static bool IsRunClass(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            foreach (var k in RunKeys)
                if (id.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>程序化栏杆口径（procedural = 低模全量；sparse = 低模 + 每 2 件放 1 件）。</summary>
        static bool RailIsProcedural => P_RailMode == "procedural" || P_RailMode == "sparse";

        // ==================================================================== 阶段 2：素材入库

        /// <summary>
        /// 素材入库 + 导入设置 + 建材质。dryRun=true 时只做"在位体检"，不复制、不导入、不建资产。
        /// 返回每个 ID 的可用性（FBX 首 mesh + 材质 + 失败原因）。
        /// </summary>
        static Dictionary<string, IdAssets> CheckAssets(Ctx ctx, bool dryRun)
        {
            var res = new Dictionary<string, IdAssets>(StringComparer.Ordinal);
            var ids = new List<string>();
            foreach (var it in ctx.Candidates)
                if (!ids.Contains(it.greeble)) ids.Add(it.greeble);
            ids.Sort(StringComparer.Ordinal);
            bool needBracket = false;
            foreach (var id in ids)
                if (BracketCount(id) > 0) { needBracket = true; break; }

            if (dryRun)
            {
                foreach (var id in ids)
                {
                    var a = new IdAssets { Id = id };
                    var mi = ctx.Man[id];
                    bool needFbx = !(IsRail(id) && RailIsProcedural);
                    if (needFbx && !File.Exists(Path.Combine(ctx.SrcRoot, FbxRel(mi))))
                        a.Fail = $"缺 FBX（{FbxRel(mi)}）";
                    int texHave = 0;
                    for (int i = 0; i < Channels.Length; i++)
                        if (File.Exists(Path.Combine(ctx.SrcRoot, TexRel(mi, Channels[i])))) texHave++;
                    a.TexHave = texHave;
                    if (texHave == 0 && a.Fail == null) a.Fail = "源里一张贴图都没有";
                    res[id] = a;
                }
                return res;
            }

            // ---- ① 批量复制 + 导入（File.Copy 不碰 AssetDatabase，全部塞进一个批）----
            var texSrcDst = new List<string[]>();          // {src, dstAsset}
            var fbxSrcDst = new List<string[]>();
            foreach (var id in ids)
            {
                var mi = ctx.Man[id];
                for (int i = 0; i < Channels.Length; i++)
                {
                    string src = Path.Combine(ctx.SrcRoot, TexRel(mi, Channels[i]));
                    string dst = $"{TexDir}/{id}/{Path.GetFileName(src)}";
                    texSrcDst.Add(new[] { src, dst });
                }
                if (IsRail(id) && RailIsProcedural) continue;      // 程序化栏杆（含 sparse）不吃 FBX
                string fsrc = Path.Combine(ctx.SrcRoot, FbxRel(mi));
                fbxSrcDst.Add(new[] { fsrc, $"{FbxDir}/{id}/{Path.GetFileName(fsrc)}" });
            }

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var e in texSrcDst) CopyIfNewer(e[0], e[1]);
                foreach (var e in fbxSrcDst) CopyIfNewer(e[0], e[1]);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            // ---- ② 兜底补登记：磁盘上有、资产库还没登记的文件（上一轮中断留下的）补一次导入 ----
            var notInDb = new List<string>();
            foreach (var e in texSrcDst)
                if (File.Exists(AssetPathToAbs(e[1])) && AssetImporter.GetAtPath(e[1]) == null) notInDb.Add(e[1]);
            foreach (var e in fbxSrcDst)
                if (File.Exists(AssetPathToAbs(e[1])) && AssetImporter.GetAtPath(e[1]) == null) notInDb.Add(e[1]);
            if (notInDb.Count > 0)
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    foreach (var p in notInDb) AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }
            }

            // ---- ③ 贴图导入设置（批量 SaveAndReimport；在批里排队，不会逐张重导入卡死）----
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var e in texSrcDst)
                    if (File.Exists(AssetPathToAbs(e[1])))
                        ConfigureTextureImporter(e[1], ChannelOf(e[1]));
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            // ---- ④ FBX 导入设置（isReadable 是 CombineMeshes 的前提；材质不导入，防生成 .fbm 垃圾）----
            foreach (var e in fbxSrcDst)
                ConfigureModelImporter(e[1]);

            // ---- ④b 托架件（SUPPORT_BRACKET）FBX：不建材质，几何直接并进 run 件所在组 ----
            var bmi = ctx.Man.ContainsKey(BracketId) ? ctx.Man[BracketId] : null;
            if (needBracket)
            {
                if (bmi == null)
                {
                    Debug.LogWarning($"[功能件] 清单里没有 {BracketId}，本轮不给 run 件加托架。");
                }
                else
                {
                    string bsrc = Path.Combine(ctx.SrcRoot, FbxRel(bmi));
                    string bdst = $"{FbxDir}/{BracketId}/{Path.GetFileName(bsrc)}";
                    if (!File.Exists(bsrc))
                    {
                        Debug.LogWarning($"[功能件] 找不到托架 FBX：{bsrc}（本轮不加托架）。");
                    }
                    else
                    {
                        CopyIfNewer(bsrc, bdst);
                        ConfigureModelImporter(bdst);
                        var bgo = AssetDatabase.LoadAssetAtPath<GameObject>(bdst);
                        var bmf = bgo != null ? bgo.GetComponent<MeshFilter>() : null;
                        if (bmf == null && bgo != null) bmf = bgo.GetComponentInChildren<MeshFilter>(true);
                        ctx.BracketMesh = bmf != null ? bmf.sharedMesh : null;
                        if (ctx.BracketMesh == null || !ctx.BracketMesh.isReadable)
                        {
                            Debug.LogWarning($"[功能件] 托架 FBX 不可用（{bdst}），本轮不加托架。");
                            ctx.BracketMesh = null;
                        }
                    }
                }
            }

            // ---- ⑤ 材质 + FBX 首 mesh ----
            var shader = FindNprShader();
            bool needTrim = false, needRail = false;
            foreach (var id in ids)
            {
                if (IsRunClass(id)) needTrim = true;
                if (IsRail(id) && RailIsProcedural) needRail = true;
            }
            if (shader != null && needTrim) ctx.TrimMat = EnsureTrimMaterial(ctx, shader);
            if (shader != null && needRail) ctx.RailMat = EnsureRailMaterial(ctx, shader);

            foreach (var id in ids)
            {
                var a = new IdAssets { Id = id };
                var mi = ctx.Man[id];

                // 贴图（缺哪张挂哪张，缺 BC 只告警不阻断）
                var tex = new Texture2D[Channels.Length];
                int have = 0;
                for (int i = 0; i < Channels.Length; i++)
                {
                    string dst = $"{TexDir}/{id}/{Path.GetFileName(Path.Combine(ctx.SrcRoot, TexRel(mi, Channels[i])))}";
                    tex[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(dst);
                    if (tex[i] != null) have++;
                }
                a.TexHave = have;
                if (have == 0)
                {
                    a.Fail = "贴图一张都没导入成功";
                    res[id] = a;
                    continue;
                }

                if (shader == null)
                {
                    a.Fail = $"找不到 shader {NprShaderName}";
                    res[id] = a;
                    continue;
                }
                a.Mat = EnsureMaterial(ctx, id, shader, mi, tex);

                if (!(IsRail(id) && RailIsProcedural))
                {
                    string fbxAsset = $"{FbxDir}/{id}/{Path.GetFileName(Path.Combine(ctx.SrcRoot, FbxRel(mi)))}";
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(fbxAsset);
                    var mf = go != null ? go.GetComponent<MeshFilter>() : null;
                    if (mf == null && go != null) mf = go.GetComponentInChildren<MeshFilter>(true);
                    a.Src = mf != null ? mf.sharedMesh : null;
                    if (a.Src == null) a.Fail = $"FBX 里没有可用 mesh（{fbxAsset}）";
                    else if (!a.Src.isReadable) a.Fail = $"mesh 不可读（isReadable=false，无法合并）";
                }

                res[id] = a;
            }

            foreach (var id in ids)
            {
                var a = res[id];
                if (a.Fail != null) Debug.LogWarning($"[功能件] {id} 不可用：{a.Fail}");
            }
            return res;
        }

        /// <summary>FBX 导入设置：isReadable（CombineMeshes 的前提）、不导入材质（防 .fbm 垃圾）、
        /// 不算切线与法线之外的东西、不加 Collider。</summary>
        static void ConfigureModelImporter(string assetPath)
        {
            if (!File.Exists(AssetPathToAbs(assetPath))) return;
            var imp = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (imp == null) return;
            imp.isReadable = true;
            imp.materialImportMode = ModelImporterMaterialImportMode.None;
            imp.meshCompression = ModelImporterMeshCompression.Off;
            imp.importAnimation = false;
            imp.importCameras = false;
            imp.importLights = false;
            imp.importNormals = ModelImporterNormals.Import;
            imp.importTangents = ModelImporterTangents.CalculateMikk;
            imp.addCollider = false;
            imp.SaveAndReimport();
        }

        /// <summary>幂等复制：目标缺失或源更新时才拷（同一个 ID 的多轮迭代不重复写盘）。</summary>
        static void CopyIfNewer(string src, string dstAsset)
        {
            if (!File.Exists(src)) return;
            string dstAbs = AssetPathToAbs(dstAsset);
            try
            {
                if (File.Exists(dstAbs) && File.GetLastWriteTimeUtc(src) <= File.GetLastWriteTimeUtc(dstAbs))
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(dstAbs));
                File.Copy(src, dstAbs, true);
                AssetDatabase.ImportAsset(dstAsset, ImportAssetOptions.ForceUpdate);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[功能件] 复制失败：{src} → {dstAsset}：{e.Message}");
            }
        }

        /// <summary>贴图导入设置：BC sRGB / N NormalMap / MS·AO·E 线性；512、CompressedHQ、mipmap、Clamp。</summary>
        static void ConfigureTextureImporter(string rel, string channel)
        {
            var ti = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (ti == null) return;

            bool normal = channel == "N";
            bool srgb = channel == "BC";
            var wantType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;

            bool changed =
                ti.textureType != wantType ||
                ti.sRGBTexture != srgb ||
                ti.wrapMode != TextureWrapMode.Clamp ||
                ti.maxTextureSize != TexMaxSize ||
                ti.textureCompression != TextureImporterCompression.CompressedHQ ||
                !ti.mipmapEnabled;

            ti.textureType = wantType;
            ti.sRGBTexture = srgb;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.maxTextureSize = TexMaxSize;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.mipmapEnabled = true;
            ti.alphaIsTransparency = false;
            ti.isReadable = false;

            if (changed) ti.SaveAndReimport();
        }

        /// <summary>文件名里第 5 段即通道（T_Greeble_&lt;ID&gt;_BC.png）；认不出按 MS（线性）处理。</summary>
        static string ChannelOf(string assetPath)
        {
            string n = Path.GetFileNameWithoutExtension(assetPath);
            foreach (var ch in Channels)
                if (n.EndsWith("_" + ch, StringComparison.OrdinalIgnoreCase)) return ch;
            return "MS";
        }

        /// <summary>每件一份 M_Greeble_&lt;ID&gt;.mat：NPR shader + toon 三件套 + BC/N/MS/AO/E 五槽与 keyword。
        /// 已存在则就地复用（只改自己这一份）。</summary>
        static Material EnsureMaterial(Ctx ctx, string id, Shader shader, ManifestItem mi, Texture2D[] tex)
        {
            string path = $"{MatDir}/M_Greeble_{id}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool created = false;
            if (m == null)
            {
                EnsureFolder(MatDir);
                m = new Material(shader);
                m.name = $"M_Greeble_{id}";
                AssetDatabase.CreateAsset(m, path);
                created = true;
            }
            else if (m.shader != shader)
            {
                m.shader = shader;
            }

            m.SetColor("_ShadowColor", ToonShadowColor);
            m.SetFloat("_ToonSoftness", ToonSoftness);
            m.SetFloat("_ToonThreshold", ToonThreshold);

            m.SetColor("_BaseColor", Color.white);            // 颜色都烤进 BC，母色保持白
            m.SetFloat("_Surface", 0f);
            m.SetFloat("_AlphaClip", 0f);
            m.DisableKeyword("_ALPHATEST_ON");

            SetTex(m, "_BaseMap", tex[0]);
            SetTex(m, "_BumpMap", tex[1]);
            m.SetFloat("_BumpScale", 1f);
            if (tex[1] != null) m.EnableKeyword("_NORMALMAP"); else m.DisableKeyword("_NORMALMAP");

            SetTex(m, "_MetallicGlossMap", tex[2]);
            m.SetFloat("_Metallic", 0f);                      // 有 MS 贴图时金属度取自贴图 R
            m.SetFloat("_Smoothness", 1f);                    // 1 = 让 MS 的 A 通道原样通过
            m.SetFloat("_SmoothnessTextureChannel", 0f);      // 0 = Metallic Alpha
            if (tex[2] != null) m.EnableKeyword("_METALLICSPECGLOSSMAP");
            else m.DisableKeyword("_METALLICSPECGLOSSMAP");

            SetTex(m, "_OcclusionMap", tex[3]);
            m.SetFloat("_OcclusionStrength", 1f);
            if (tex[3] != null) m.EnableKeyword("_OCCLUSIONMAP"); else m.DisableKeyword("_OCCLUSIONMAP");

            // 自发光只给清单里真有发光区的件开（JUNCTION_BOX 的状态灯 / LIGHT_FIXTURE 的漫射板）
            SetTex(m, "_EmissionMap", tex[4]);
            bool emit = mi != null && mi.emissive != null && mi.emissive.Length > 0;
            m.SetFloat("_EmissionEnabled", emit ? 1f : 0f);
            m.SetFloat("_EmissionUseMap", emit ? 1f : 0f);
            m.SetColor("_EmissionColor", emit ? Color.white : Color.black);
            if (emit) m.EnableKeyword("_EMISSION"); else m.DisableKeyword("_EMISSION");

            EditorUtility.SetDirty(m);
            if (created) ctx.MaterialsCreated++; else ctx.MaterialsReused++;
            return m;
        }

        static void SetTex(Material m, string prop, Texture2D t)
        {
            m.SetTexture(prop, t);
            m.SetTextureScale(prop, Vector2.one);
            m.SetTextureOffset(prop, Vector2.zero);
        }

        static Shader FindNprShader()
        {
            var s = Shader.Find(NprShaderName);
            if (s == null) s = AssetDatabase.LoadAssetAtPath<Shader>(NprShaderPath);
            if (s == null) Debug.LogError($"[功能件] 找不到 shader {NprShaderName}（{NprShaderPath} 是否编译通过？）");
            return s;
        }

        // ==================================================================== v2② 高度节奏

        /// <summary>
        /// 高度节奏（只动墙面件的 kick / waist）：
        ///   · 同面 + 同档共用一个确定性抖动值（kick ±0.15m、waist ±0.25m、mid ±0.12m）——
        ///     同一面墙同一条线上的件仍排成一条线（栏杆/桥架的对齐不会被抖散）；
        ///   · 约 20% 的 (面, 档) 整组改走第三条高度 floor+1.2m（tier = mid），让墙面上出现三条高度带，
        ///     而不是 kick / waist 两条机械横线；
        ///   · 竖直件（立管落地）、天花 / 地面件、栏杆、转角件一律不碰；plan 自己给了别的标高也不碰
        ///     （|pos.y - floor_y - 本档高| > 0.4m 视为 plan 特意摆的）。
        /// 哈希是 FNV-1a（进程无关），重跑结果逐位一致。
        /// </summary>
        static void ApplyHeightRhythm(Ctx ctx)
        {
            if (P_Rhythm == 0) return;
            var toMid = new Dictionary<string, bool>(StringComparer.Ordinal);
            var jit = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var it in ctx.Candidates)
            {
                if (!RhythmTier(ctx, it, out string tier)) continue;

                string faceKey = (it.face_id ?? "?") + "|" + tier;
                if (!toMid.TryGetValue(faceKey, out bool mid))
                {
                    mid = tier != "mid" && Hash01(faceKey + "|tomid") < MidShare;
                    toMid[faceKey] = mid;
                }
                string eff = mid ? "mid" : tier;
                float cur = it.pos[1] - it.floor_y;
                if (Mathf.Abs(cur - TierHeight(tier)) > 0.4f) continue;   // 不在本档线上（plan 特意摆的）→ 不动
                float baseH = TierHeight(eff);

                string jkey = (it.face_id ?? "?") + "|" + eff;
                if (!jit.TryGetValue(jkey, out float j))
                {
                    j = (Hash01(jkey + "|jit") * 2f - 1f) * TierJitter(eff);
                    jit[jkey] = j;
                }
                it.pos[1] = it.floor_y + baseH + j;
                ctx.RhythmJittered++;
                if (eff != tier) { it.tier = eff; ctx.MidMigrated++; }
            }
        }

        /// <summary>可做高度节奏的墙件：清单标 wall、档位 kick / waist / mid、floor_y 合理且当前标高在档位附近。</summary>
        static bool RhythmTier(Ctx ctx, PlanItem it, out string tier)
        {
            tier = it.tier ?? "";
            if (!ctx.Man.TryGetValue(it.greeble, out var mi)) return false;
            if (!string.Equals(mi.mount, "wall", StringComparison.OrdinalIgnoreCase)) return false;
            if (tier != "kick" && tier != "waist" && tier != "mid") return false;   // 新档 ceiling/corner/floor/vertical 自动跳过
            if (!Finite3(it.pos) || float.IsNaN(it.floor_y) || float.IsInfinity(it.floor_y)) return false;
            if (Mathf.Abs(it.pos[1] - it.floor_y) > 6f) return false;               // floor_y 明显对不上 → 不碰
            return true;
        }

        static float TierHeight(string tier) =>
            tier == "mid" ? TierMidH : tier == "waist" ? TierWaistH : TierKickH;

        static float TierJitter(string tier) =>
            tier == "mid" ? JitMid : tier == "waist" ? JitWaist : JitKick;

        /// <summary>确定性 [0,1) 哈希（FNV-1a 32 位）。不要用 string.GetHashCode：Unity/Mono 会做进程随机化，重跑会漂。</summary>
        static float Hash01(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
                return (h & 0xFFFFFFu) / (float)0x1000000;
            }
        }

        // ==================================================================== v2① u 边界吸附

        /// <summary>
        /// run 件（CABLE_TRAY / CONDUIT / PIPE）离所在面 u 边界 &lt; -usnap 米时，把**端头贴齐边界**：
        /// 件心移到 边界 ± 半长（不是把件心压在边界上 —— 那样会有一半挑空 / 插墙），
        /// 这样两面墙在同一个转角上的同类件正好端对端接上。
        /// u 是米（= (pos - face.origin)·face.u），面规格取自 face_flat.json（-faces 可换；文件不在则整步跳过）。
        /// 竖直 forward 的件（天花 / 地面）不在 u 吸附范围：它们的走向由 run_dir 决定，不是面的 u。
        /// </summary>
        static void ApplyEdgeSnap(Ctx ctx)
        {
            if (P_USnap <= 0f) return;
            if (!LoadFaces(ctx)) return;

            foreach (var it in ctx.Candidates)
            {
                if (!IsRunClass(it.greeble)) continue;
                var f = Vec3(it.forward);
                if (f.sqrMagnitude < 1e-8f) continue;
                f.Normalize();
                if (Mathf.Abs(Vector3.Dot(f, Vector3.up)) > VerticalCos) continue;
                if (string.IsNullOrEmpty(it.face_id)) continue;
                if (!ctx.Faces.TryGetValue(it.face_id, out var fr)) continue;
                if (!Finite3(fr.origin) || !Finite3(fr.u) || fr.outline == null || fr.outline.Length < 6) continue;

                var origin = Vec3(fr.origin);
                var axisU = Vec3(fr.u).normalized;
                var axisV = Vec3(fr.v).normalized;
                float vPos = Vector3.Dot(Vec3(it.pos) - origin, axisV);
                if (!USlice(fr, vPos, out float uMin, out float uMax)) continue;

                float scale = it.scale > 0.01f ? it.scale : 1f;
                var mi = ctx.Man[it.greeble];
                // 件沿面 u 轴的实际半宽：盒体支撑函数 Σ |局部轴·u| × 该轴尺寸 / 2
                // （横段件局部 X 就是 u 向 → dims[0]/2；转角立管斜 45° → 自动折算出更宽的值）
                var rot = Pose(it, mi);
                float half = 0f;
                for (int ax = 0; ax < 3; ax++)
                    half += Mathf.Abs(Vector3.Dot(rot * Unit(ax), axisU)) * SizeOfLocal(mi, ax) * scale * 0.5f;
                half = Mathf.Max(0.02f, half);
                if (uMax - uMin < half * 2.05f) continue;                                     // 面比件还短 → 别硬塞

                float u = it.u;
                float target;
                if (u - uMin < P_USnap) target = uMin + half;
                else if (uMax - u < P_USnap) target = uMax - half;
                else continue;

                float d = target - u;
                if (Mathf.Abs(d) < 0.002f) continue;
                it.pos[0] += axisU.x * d;
                it.pos[1] += axisU.y * d;
                it.pos[2] += axisU.z * d;
                it.u = target;
                ctx.Snapped++;
            }
            if (ctx.Snapped > 0)
                Debug.Log($"[功能件] 边界吸附：{ctx.Snapped} 件 run 类端头贴齐面 u 边界（阈值 {P_USnap:F2}m）");
        }

        /// <summary>面轮廓在给定 v 处的 u 跨度（多边形水平切片）。轮廓是 face_flat 的 outline（局部 uv，米，成对）。</summary>
        static bool USlice(FaceRec fr, float v, out float uMin, out float uMax)
        {
            uMin = float.MaxValue;
            uMax = float.MinValue;
            int n = fr.outline.Length / 2;
            if (n < 3) return false;
            for (int i = 0; i < n; i++)
            {
                float u1 = fr.outline[i * 2], v1 = fr.outline[i * 2 + 1];
                int j = (i + 1) % n;
                float u2 = fr.outline[j * 2], v2 = fr.outline[j * 2 + 1];
                if ((v1 <= v && v2 > v) || (v2 <= v && v1 > v))
                {
                    float t = (v - v1) / (v2 - v1);
                    float uu = u1 + t * (u2 - u1);
                    if (uu < uMin) uMin = uu;
                    if (uu > uMax) uMax = uu;
                }
            }
            if (uMin > uMax)                                  // 切片退化（v 落在轮廓外）→ 退回整体 u 跨度
            {
                for (int i = 0; i < n; i++)
                {
                    float uu = fr.outline[i * 2];
                    if (uu < uMin) uMin = uu;
                    if (uu > uMax) uMax = uu;
                }
            }
            return uMin <= uMax;
        }

        /// <summary>读 face_flat.json（幂等缓存）。缺文件 / 解析失败 → 报一次警告并跳过吸附（不失败整轮）。</summary>
        static bool LoadFaces(Ctx ctx)
        {
            if (ctx.FacesLoaded) return ctx.Faces != null && ctx.Faces.Count > 0;
            ctx.FacesLoaded = true;
            if (!File.Exists(P_Faces))
            {
                Debug.LogWarning($"[功能件] 找不到面规格 {P_Faces}，本轮跳过 u 边界吸附（-faces 可指路，-usnap 0 可显式关掉）。");
                return false;
            }
            try
            {
                var doc = JsonUtility.FromJson<FaceDoc>(File.ReadAllText(P_Faces));
                if (doc == null || doc.faces == null || doc.faces.Count == 0)
                {
                    Debug.LogWarning($"[功能件] 面规格里没有 faces：{P_Faces}");
                    return false;
                }
                foreach (var fr in doc.faces)
                    if (fr != null && !string.IsNullOrEmpty(fr.id)) ctx.Faces[fr.id] = fr;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[功能件] 面规格解析失败：{P_Faces}：{e.Message}");
                return false;
            }
            return ctx.Faces.Count > 0;
        }

        // ==================================================================== v2① 接头套环几何

        /// <summary>清单 dims_m = (局部 X, 局部 Z 出墙深, 局部 Y 高) 三元组（见 GREEBLE_MANIFEST
        /// fbx_axis：Blender +Y→Unity +Z、+Z→Unity +Y）。取某档尺寸，缺失返回 0。</summary>
        static float Dims(ManifestItem mi, int i) =>
            mi != null && mi.dims_m != null && mi.dims_m.Length >= 3 ? Mathf.Max(0f, mi.dims_m[i]) : 0f;

        /// <summary>件的走向轴：0 = 局部 X（横段：桥架 / 线槽），1 = 局部 Y（立管）。按 dims 里
        /// 局部 X 与局部 Y（dims[0] 与 dims[2]）谁长判定。</summary>
        static int RunAxis(ManifestItem mi) => Dims(mi, 2) > Dims(mi, 0) ? 1 : 0;

        /// <summary>局部轴 → 该轴上的件尺寸（0=X→dims[0]，1=Y→dims[2]，2=Z→dims[1]）。</summary>
        static float SizeOfLocal(ManifestItem mi, int localAxis) =>
            localAxis == 0 ? Dims(mi, 0) : localAxis == 1 ? Dims(mi, 2) : Dims(mi, 1);

        static Vector3 MakeVec(int a, float va, int b, float vb, int c, float vc)
        {
            float[] v = new float[3];
            v[a] = va;
            v[b] = vb;
            v[c] = vc;
            return new Vector3(v[0], v[1], v[2]);
        }

        /// <summary>
        /// 接头套环模板（件局部系，跟随件的 TRS 一起摆）：在件两端各 4 根 45° 倒角盒拼成一圈方环，
        /// 沿走向宽 0.06m、比本体外廓大 10mm、壁厚 28mm，环的端面与件端齐平（读作"标准接头"，
        /// 让两件之间的缝变成一段正常的接头而不是断头）。走向轴 = 局部 X（横段）或局部 Y（立管）。
        /// 8 根 × 44 = 352 三角/件。
        /// </summary>
        static Mesh BuildCollarTemplate(string name, ManifestItem mi)
        {
            int run = RunAxis(mi);                       // 走向轴
            int a1 = (run + 1) % 3;                      // 截面两轴
            int a2 = (run + 2) % 3;
            float len = Mathf.Max(0.2f, SizeOfLocal(mi, run));
            float s1 = Mathf.Max(0.04f, SizeOfLocal(mi, a1));
            float s2 = Mathf.Max(0.04f, SizeOfLocal(mi, a2));

            var verts = new List<Vector3>(8 * 96);
            var norms = new List<Vector3>(8 * 96);
            var uvs = new List<Vector2>(8 * 96);
            var tans = new List<Vector4>(8 * 96);
            var tris = new List<int>(8 * ChamferTris * 3);
            const float bevel = CollarBevel;

            float posRun = len * 0.5f - CollarWidth * 0.5f;                 // 端面与件端齐平
            float off1 = s1 * 0.5f + CollarGap + CollarBar * 0.5f;          // ±a1 两根（满跨 a2）
            float off2 = s2 * 0.5f + CollarGap + CollarBar * 0.5f;          // ±a2 两根
            float span2 = s2 + 2f * CollarGap + 2f * CollarBar;
            float span1 = s1 + 2f * CollarGap;

            for (int end = 0; end < 2; end++)
            {
                float r = end == 0 ? posRun : -posRun;
                // a1 方向的两根：盖住 a2 的整个外廓（角上不留缝）
                AppendChamferBox(verts, norms, uvs, tans, tris,
                    MakeVec(run, r, a1, off1, a2, 0f),
                    MakeVec(run, CollarWidth, a1, CollarBar, a2, span2), bevel);
                AppendChamferBox(verts, norms, uvs, tans, tris,
                    MakeVec(run, r, a1, -off1, a2, 0f),
                    MakeVec(run, CollarWidth, a1, CollarBar, a2, span2), bevel);
                // a2 方向的两根：夹在两片之间（外廓内收，正好扣住本体）
                AppendChamferBox(verts, norms, uvs, tans, tris,
                    MakeVec(run, r, a1, 0f, a2, off2),
                    MakeVec(run, CollarWidth, a1, span1, a2, CollarBar), bevel);
                AppendChamferBox(verts, norms, uvs, tans, tris,
                    MakeVec(run, r, a1, 0f, a2, -off2),
                    MakeVec(run, CollarWidth, a1, span1, a2, CollarBar), bevel);
            }

            var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(verts);
            m.SetNormals(norms);
            m.SetUVs(0, uvs);
            m.SetTangents(tans);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Trim 材质（接头套环用）：NPR shader + toon 三件套 + 工程 Trim 口径参数；已存在则复用。</summary>
        static Material EnsureTrimMaterial(Ctx ctx, Shader shader)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(TrimMatPath);
            if (m != null) return m;
            EnsureFolder(MatDir);
            m = new Material(shader);
            m.name = "M_Greeble_Trim";
            m.SetColor("_ShadowColor", ToonShadowColor);
            m.SetFloat("_ToonSoftness", ToonSoftness);
            m.SetFloat("_ToonThreshold", ToonThreshold);
            m.SetColor("_BaseColor", TrimBaseColor);
            m.SetFloat("_Metallic", TrimMetallic);
            m.SetFloat("_Smoothness", TrimSmoothness);
            m.SetFloat("_Surface", 0f);
            m.SetFloat("_AlphaClip", 0f);
            m.DisableKeyword("_NORMALMAP");
            m.DisableKeyword("_METALLICSPECGLOSSMAP");
            m.DisableKeyword("_OCCLUSIONMAP");
            m.DisableKeyword("_EMISSION");
            AssetDatabase.CreateAsset(m, TrimMatPath);
            EditorUtility.SetDirty(m);
            ctx.MaterialsCreated++;
            return m;
        }

        /// <summary>栏杆深钢材质（v3）：NPR shader + toon 三件套 + 深钢参数，纯色不加贴图；已存在则复用。
        /// 与 Trim 材质分开（Trim 是套环/接头的浅金属，栏杆是深钢）。</summary>
        static Material EnsureRailMaterial(Ctx ctx, Shader shader)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(RailMatPath);
            if (m != null) return m;
            EnsureFolder(MatDir);
            m = new Material(shader);
            m.name = "M_Greeble_Railing";
            m.SetColor("_ShadowColor", ToonShadowColor);
            m.SetFloat("_ToonSoftness", ToonSoftness);
            m.SetFloat("_ToonThreshold", ToonThreshold);
            m.SetColor("_BaseColor", RailBaseColor);
            m.SetFloat("_Metallic", RailMetallic);
            m.SetFloat("_Smoothness", RailSmoothness);
            m.SetFloat("_Surface", 0f);
            m.SetFloat("_AlphaClip", 0f);
            m.DisableKeyword("_NORMALMAP");
            m.DisableKeyword("_METALLICSPECGLOSSMAP");
            m.DisableKeyword("_OCCLUSIONMAP");
            m.DisableKeyword("_EMISSION");
            AssetDatabase.CreateAsset(m, RailMatPath);
            EditorUtility.SetDirty(m);
            ctx.MaterialsCreated++;
            return m;
        }

        // ==================================================================== v3：run 件中段托架

        /// <summary>run 件中段托架个数：桥架（2m）1 个正中，线槽（3m）2 个三等分点；立管自带墙卡箍，不加。</summary>
        static int BracketCount(string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            if (id.IndexOf("CONDUIT", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            if (id.IndexOf("CABLE_TRAY", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return 0;
        }

        /// <summary>托架沿走向的偏移（米，件局部 X 方向）：1 个 → 正中；2 个 → ±长/6（三等分点）。</summary>
        static void BracketOffsets(string id, float len, List<float> outp)
        {
            outp.Clear();
            int n = BracketCount(id);
            if (n == 1) outp.Add(0f);
            else if (n == 2) { outp.Add(-len / 6f); outp.Add(len / 6f); }
        }

        /// <summary>单颗托架的三角数（清单 SUPPORT_BRACKET 为准，兜底 428）。</summary>
        static long BracketTris(Ctx ctx)
        {
            var mi = ctx.Man.ContainsKey(BracketId) ? ctx.Man[BracketId] : null;
            return mi != null && mi.triangles > 0 ? mi.triangles : 428;
        }

        // ==================================================================== 阶段 3：预算与分组

        /// <summary>逐件在预算内配额（plan 顺序，确定性）；素材不可用的类整类剔除；触顶即停。</summary>
        static void ApplyBudget(Ctx ctx)
        {
            long used = 0;
            bool hit = false;
            int idx = 0;
            while (idx < ctx.Candidates.Count)
            {
                var it = ctx.Candidates[idx];
                var a = ctx.Assets[it.greeble];
                if (a != null && a.Fail != null)
                {
                    ctx.Skip($"{it.greeble} 素材不可用：{a.Fail}");
                    idx++;
                    continue;
                }
                if (hit)
                {
                    ctx.Skip("超出三角预算（-budget）");
                    idx++;
                    continue;
                }
                long tris = ItemTris(ctx, it.greeble);
                if (used + tris > P_Budget)
                {
                    hit = true;
                    ctx.BudgetHit = true;
                    ctx.Skip("超出三角预算（-budget）");
                    idx++;
                    continue;
                }
                used += tris;
                ctx.Items.Add(it);
                ctx.PlannedTris += tris;
                idx++;
            }
            ctx.Unplaced = ctx.Candidates.Count - ctx.Items.Count;
        }

        static long ItemTris(Ctx ctx, string id)
        {
            long t;
            if (IsRail(id) && RailIsProcedural) t = RailTrisPerPiece;
            else
            {
                var mi = ctx.Man.ContainsKey(id) ? ctx.Man[id] : null;
                t = mi != null && mi.triangles > 0 ? mi.triangles : 0;   // 清单没给三角数：按 0 计
            }
            if (IsRunClass(id))
            {
                t += CollarTrisPerPiece;                                  // v2①：接头套环
                t += BracketCount(id) * BracketTris(ctx);                 // v3：中段托架（并进同组 mesh）
            }
            return t;
        }

        /// <summary>按 (block, ID) 分组：同组合并成一个 mesh / 一个 MeshRenderer。
        /// run 类额外分出一组 Trim（接头套环），用 Trim 材质单独合并。</summary>
        static void BuildGroups(Ctx ctx)
        {
            foreach (var it in ctx.Items)
            {
                string key = it.block + "|" + it.greeble;
                if (!ctx.GroupMap.TryGetValue(key, out var g))
                {
                    g = new Group { Block = it.block, Id = it.greeble, IsRail = IsRail(it.greeble), Trim = false };
                    ctx.GroupMap[key] = g;
                    ctx.Groups.Add(g);
                }
                g.Items.Add(it);

                if (!IsRunClass(it.greeble)) continue;
                string tkey = key + "|Trim";
                if (!ctx.GroupMap.TryGetValue(tkey, out var tg))
                {
                    tg = new Group { Block = it.block, Id = it.greeble, Trim = true };
                    ctx.GroupMap[tkey] = tg;
                    ctx.Groups.Add(tg);
                }
                tg.Items.Add(it);
            }
            // 层级顺序按 (区块, 类别, Trim 在后) 定死，重跑与上一次完全一致
            ctx.Groups.Sort((x, y) =>
            {
                int c = string.CompareOrdinal(x.Block ?? "", y.Block ?? "");
                if (c != 0) return c;
                c = string.CompareOrdinal(x.Id ?? "", y.Id ?? "");
                if (c != 0) return c;
                return (x.Trim ? 1 : 0) - (y.Trim ? 1 : 0);
            });
        }

        // ==================================================================== 阶段 4：合并成 mesh

        /// <summary>把每组的所有实例合并成一个 mesh（世界坐标已烘进顶点），写进 MeshDir 并返回成功的组。</summary>
        static List<Group> BuildMeshes(Ctx ctx, Dictionary<string, IdAssets> assets)
        {
            var done = new List<Group>();
            var railTemplates = new Dictionary<int, Mesh>();     // 长度 ×10 取整 → 模板
            var collarTemplates = new Dictionary<string, Mesh>();// ID → 接头套环模板
            EnsureFolder(MeshDir);                               // 目录建在批外（批内不调 CreateFolder）

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var g in ctx.Groups)
                {
                    Mesh src = null;
                    if (g.Trim)
                    {
                        if (!collarTemplates.TryGetValue(g.Id, out src))
                        {
                            var cmi = ctx.Man.ContainsKey(g.Id) ? ctx.Man[g.Id] : null;
                            if (cmi != null)
                            {
                                src = BuildCollarTemplate($"GREEBLE_Collar_{g.Id}", cmi);
                                collarTemplates[g.Id] = src;
                            }
                        }
                        if (src == null) { ctx.Skip($"接头套环模板缺失（{g.Id}）", g.Items.Count); continue; }
                    }
                    else if (g.IsRail && RailIsProcedural)
                    {
                        int key = Mathf.Max(5, Mathf.RoundToInt(RailLength(g) * 10f));
                        if (!railTemplates.TryGetValue(key, out src))
                        {
                            src = BuildRailTemplate($"GREEBLE_Rail_{key / 10f:0.0}m", key * 0.1f);
                            railTemplates[key] = src;
                        }
                    }
                    else if (assets.TryGetValue(g.Id, out var a))
                    {
                        src = a.Src;
                    }
                    if (src == null) { ctx.Skip($"源 mesh 缺失（{g.Id}）", g.Items.Count); continue; }

                    Mesh merged;
                    try
                    {
                        merged = CombineGroup(ctx, g, src);
                    }
                    catch (Exception e)
                    {
                        ctx.Skip($"{g.Id} 合并失败：{e.Message}", g.Items.Count);
                        continue;
                    }

                    string path = $"{MeshDir}/{GroupAssetName(g)}.asset";
                    AssetDatabase.CreateAsset(merged, path);
                    g.Merged = merged;
                    g.MeshPath = path;
                    g.Tris = merged.triangles.Length;            // 合并后实际三角数
                    done.Add(g);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            foreach (var kv in railTemplates) UnityEngine.Object.DestroyImmediate(kv.Value);
            foreach (var kv in collarTemplates) UnityEngine.Object.DestroyImmediate(kv.Value);

            ctx.MeshAssets = done.Count;
            ctx.Tris = 0;
            foreach (var g in done) ctx.Tris += g.Tris;
            ctx.Placed = 0;
            ctx.Collars = 0;
            ctx.Brackets = 0;
            foreach (var g in done)
            {
                if (g.Trim) ctx.Collars += g.Items.Count * 2;      // 每件两端各一圈
                else ctx.Placed += g.Items.Count;
                if (!g.Trim && !g.IsRail && ctx.BracketMesh != null)
                    ctx.Brackets += BracketCount(g.Id) * g.Items.Count;
            }
            ctx.Unplaced += (ctx.Items.Count - ctx.Placed);
            ctx.ByGroups = true;          // 之后一律以组为准，防止两份计数漂移（Items 只读，不改内容）
            return done;
        }

        /// <summary>合并 mesh 资产的命名：普通件 &lt;block&gt;_&lt;ID&gt;，接头套环 &lt;block&gt;_&lt;ID&gt;_Trim。</summary>
        static string GroupAssetName(Group g) =>
            AssetName(g.Block, g.Id) + (g.Trim ? "_Trim" : "");

        /// <summary>同组一次性合并：transform = TRS(pos, Pose(it), scale)，mergeSubMeshes=true。
        /// run 件（桥架 / 线槽）在件中心沿走向补 1–2 颗 SUPPORT_BRACKET 实例，同组同材质、不新建对象。</summary>
        static Mesh CombineGroup(Ctx ctx, Group g, Mesh src)
        {
            var combines = new List<CombineInstance>(g.Items.Count);
            ctx.Man.TryGetValue(g.Id, out var mi);
            var bracketMesh = (g.Trim || g.IsRail) ? null : ctx.BracketMesh;
            var offsets = new List<float>(2);
            float runLen = bracketMesh != null ? SizeOfLocal(mi, RunAxis(mi)) : 0f;
            if (bracketMesh != null && runLen <= 0.01f) bracketMesh = null;      // 清单缺尺寸 → 不加托架
            foreach (var it in g.Items)
            {
                float s = it.scale > 0.01f ? it.scale : 1f;
                var rot = Pose(it, mi);
                var pos = Vec3(it.pos);
                combines.Add(new CombineInstance
                {
                    mesh = src,
                    subMeshIndex = 0,
                    transform = Matrix4x4.TRS(pos, rot, Vector3.one * s),
                });
                if (bracketMesh == null) continue;

                // 局部 +X = 走向（三条姿态规则都成立）；局部 +Z = 背板贴墙方向
                Vector3 runDir = rot * Vector3.right;
                Vector3 back = rot * Vector3.forward;
                BracketOffsets(g.Id, runLen, offsets);
                foreach (var off in offsets)
                {
                    combines.Add(new CombineInstance
                    {
                        mesh = bracketMesh,
                        subMeshIndex = 0,
                        transform = Matrix4x4.TRS(pos + runDir * (off * s) + back * BracketWallEps, rot, Vector3.one * s),
                    });
                }
            }
            var mesh = new Mesh
            {
                name = g.Trim ? AssetName(g.Block, g.Id) + "_Trim" : AssetName(g.Block, g.Id),
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            };
            mesh.CombineMeshes(combines.ToArray(), true, true);
            return mesh;
        }

        /// <summary>
        /// 姿态。plan 的 forward 口径分三档（V2 生成器 docstring "姿态约定" 与之一致）：
        ///   ① forward 水平（墙件 / 栏杆 / 转角立管）：局部 +Z 朝房间 = LookRotation(forward, up)；
        ///   ② forward 竖直 + 有水平 run_dir（天花桥架）：局部 +X = 走向（run_dir），
        ///      局部 +Z = -forward —— 背板贴安装面、箱体垂进房间（= 生成器 "--tray-sign below" 的几何意图）；
        ///   ③ forward 竖直、无 run_dir（天花灯具 / 地面格栅）：局部 +Y = forward，物体保持自然"上"
        ///      （灯体下垂、格栅底面贴地），水平朝向取 wall_dir（格栅贴墙走向），否则世界 +X。
        /// 三档都用右手正交基显式构造，LookRotation 的 forward/up 永不共线 → 不会退化、不翻滚；
        /// 竖直 forward 时不读 yaw_deg（V2 生成器明确：竖直 forward 的 yaw_deg 无意义，一律 0.0）。
        /// </summary>
        static Quaternion Pose(PlanItem it, ManifestItem mi)
        {
            var f = Vec3(it.forward);
            if (f.sqrMagnitude < 1e-8f) f = Vector3.forward;
            f.Normalize();
            if (Mathf.Abs(Vector3.Dot(f, Vector3.up)) <= VerticalCos)
                return Quaternion.LookRotation(f, Vector3.up);            // ① 水平 forward

            Vector3 x, y, z;
            var run = Vec3(it.run_dir);
            if (run.sqrMagnitude > 1e-6f)
            {
                // ② 天花桥架：局部 +Z 朝背板方向。默认 -f（背板贴安装面、箱体垂进房间）；
                //    -trayflip 1 取反（局部 +Z = forward，供 A/B 验证生成器 docstring 的字面读法）。
                z = P_TrayFlip == 1 ? f : -f;
                x = run - Vector3.Dot(run, z) * z;
                if (x.sqrMagnitude < 1e-6f) x = Vector3.right;
                x.Normalize();
                y = Vector3.Cross(z, x).normalized;
            }
            else
            {
                y = f;                                                    // ③ 灯具 / 格栅：自然"上"
                var hint = Vec3(it.wall_dir);
                if (hint.sqrMagnitude < 1e-6f) hint = Vector3.right;
                x = hint - Vector3.Dot(hint, y) * y;
                if (x.sqrMagnitude < 1e-6f) x = Vector3.right - Vector3.Dot(Vector3.right, y) * y;
                if (x.sqrMagnitude < 1e-6f) x = Vector3.forward;
                x.Normalize();
                z = Vector3.Cross(x, y).normalized;
            }
            return Quaternion.LookRotation(z, y);
        }

        /// <summary>float[3] → Vector3（缺字段返回 zero）。</summary>
        static Vector3 Vec3(float[] a) =>
            a != null && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;

        static float RailLength(Group g)
        {
            float L = 0f;
            foreach (var it in g.Items)
                if (it.length_u_m > L) L = it.length_u_m;
            return L > 0.2f ? L : 2f;
        }

        static string AssetName(string block, string id) =>
            $"{(string.IsNullOrEmpty(block) ? "NOBLOCK" : block)}_{id}";

        // ==================================================================== 阶段 5：建对象

        /// <summary>根 MAPSKIN_Greeble（identity）→ &lt;block&gt;/&lt;ID&gt;；合并 mesh 已是世界坐标，
        /// 所以每一层都保持 identity。不加 Collider（生成物不参与碰撞）。</summary>
        static void CreateObjects(Ctx ctx, List<Group> groups, Dictionary<string, IdAssets> assets)
        {
            if (groups.Count == 0) return;
            var root = new GameObject(RootName);
            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;
            root.transform.localScale = Vector3.one;

            var blocks = new Dictionary<string, Transform>(StringComparer.Ordinal);
            int objs = 1;
            foreach (var g in groups)
            {
                string blockName = string.IsNullOrEmpty(g.Block) ? "NOBLOCK" : g.Block;
                if (!blocks.TryGetValue(blockName, out var bt))
                {
                    var bgo = new GameObject(blockName);
                    bgo.transform.SetParent(root.transform, false);
                    bt = bgo.transform;
                    blocks[blockName] = bt;
                    objs++;
                }
                var go = new GameObject(g.Trim ? g.Id + "_Trim" : g.Id);
                go.transform.SetParent(bt, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;

                go.AddComponent<MeshFilter>().sharedMesh = g.Merged;
                var mr = go.AddComponent<MeshRenderer>();
                Material mat = null;
                if (g.Trim) mat = ctx.TrimMat;
                else if (g.IsRail && RailIsProcedural && ctx.RailMat != null) mat = ctx.RailMat;
                if (mat == null && assets.TryGetValue(g.Id, out var a)) mat = a.Mat;   // 兜底（材质缺失时不空着）
                if (mat != null) mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                mr.receiveShadows = true;
                objs++;
            }
            ctx.Objects = objs;
        }

        // ==================================================================== 程序化栏杆（低模倒角盒）

        /// <summary>
        /// 程序化低模栏杆模板（局部空间：+X = 沿栏杆走向、+Y = 上、+Z = 外法线；原点 = 底面中心）。
        /// v3：4 个 45° 倒角盒 = 176 三角 —— 顶杆 0.05×0.05（顶面 1.02m）、一根中杆 0.03×0.03（y 0.55）、
        /// 两根立柱 0.06×1.08×0.06（顶端与顶杆齐平、埋地 0.06；底板并入立柱底部）。
        /// 法线 flat、UV 盒投影。与 LookRotation(forward, up) 的局部 +X = up×forward 一致，直接用 TRS 摆位。
        /// </summary>
        static Mesh BuildRailTemplate(string name, float length)
        {
            float L = Mathf.Clamp(length, 0.5f, 8f);
            var verts = new List<Vector3>(RailBoxes * 96);
            var norms = new List<Vector3>(RailBoxes * 96);
            var uvs = new List<Vector2>(RailBoxes * 96);
            var tans = new List<Vector4>(RailBoxes * 96);
            var tris = new List<int>(RailBoxes * ChamferTris * 3);
            const float bevel = 0.006f;

            float topCy = RailTopY - RailTopSize * 0.5f;                    // 顶杆中心：上表面 = 1.02
            float postCy = RailTopY - RailPostH * 0.5f;                     // 立柱中心：顶端 1.02、底端 -0.06
            AppendChamferBox(verts, norms, uvs, tans, tris,
                new Vector3(0f, topCy, 0f), new Vector3(L, RailTopSize, RailTopSize), bevel);
            AppendChamferBox(verts, norms, uvs, tans, tris,
                new Vector3(0f, RailMidY, 0f), new Vector3(L, RailMidSize, RailMidSize), bevel);
            float px = Mathf.Max(0.05f, L * 0.5f - RailPostSize * 0.5f);
            AppendChamferBox(verts, norms, uvs, tans, tris,
                new Vector3(px, postCy, 0f), new Vector3(RailPostSize, RailPostH, RailPostSize), bevel);
            AppendChamferBox(verts, norms, uvs, tans, tris,
                new Vector3(-px, postCy, 0f), new Vector3(RailPostSize, RailPostH, RailPostSize), bevel);

            var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(verts);
            m.SetNormals(norms);
            m.SetUVs(0, uvs);
            m.SetTangents(tans);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// 45° 倒角盒 = 6 个内缩面 + 12 条棱面 + 8 个角三角 = 44 三角。
        /// 做法：24 个点（每个面 4 个内缩角点）的凸包，逐个面写出；顶点按面复制 → 天然硬边（flat）。
        /// </summary>
        static void AppendChamferBox(List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs,
                                     List<Vector4> tans, List<int> tris,
                                     Vector3 center, Vector3 size, float bevel)
        {
            float[] h = { size.x * 0.5f, size.y * 0.5f, size.z * 0.5f };
            float minH = Mathf.Min(Mathf.Min(h[0], h[1]), h[2]);
            float b = Mathf.Clamp(bevel, 0f, Mathf.Max(0f, minH * 0.45f));

            var P = new Vector3[24];
            for (int axis = 0; axis < 3; axis++)
            {
                int o1 = (axis + 1) % 3, o2 = (axis + 2) % 3;
                for (int sign = 0; sign < 2; sign++)
                    for (int s1 = 0; s1 < 2; s1++)
                        for (int s2 = 0; s2 < 2; s2++)
                        {
                            float[] c = new float[3];
                            c[axis] = (sign == 0 ? -1f : 1f) * h[axis];
                            c[o1] = (s1 == 0 ? -1f : 1f) * (h[o1] - b);
                            c[o2] = (s2 == 0 ? -1f : 1f) * (h[o2] - b);
                            P[Idx(axis, sign, s1, s2)] = center + new Vector3(c[0], c[1], c[2]);
                        }
            }

            // 6 个内缩面
            for (int axis = 0; axis < 3; axis++)
                for (int sign = 0; sign < 2; sign++)
                {
                    var pts = new[]
                    {
                        P[Idx(axis, sign, 0, 0)], P[Idx(axis, sign, 0, 1)],
                        P[Idx(axis, sign, 1, 1)], P[Idx(axis, sign, 1, 0)],
                    };
                    AddPoly(verts, norms, uvs, tans, tris, pts, Unit(axis) * (sign == 0 ? -1f : 1f));
                }

            // 12 条棱面（每对轴 × 4 个象限）
            for (int a = 0; a < 3; a++)
                for (int bb = a + 1; bb < 3; bb++)
                    for (int sa = 0; sa < 2; sa++)
                        for (int sb = 0; sb < 2; sb++)
                        {
                            int a0, a1, b0, b1;
                            if (bb == (a + 1) % 3) { a0 = Idx(a, sa, sb, 0); a1 = Idx(a, sa, sb, 1); }
                            else { a0 = Idx(a, sa, 0, sb); a1 = Idx(a, sa, 1, sb); }
                            if (a == (bb + 1) % 3) { b0 = Idx(bb, sb, sa, 0); b1 = Idx(bb, sb, sa, 1); }
                            else { b0 = Idx(bb, sb, 0, sa); b1 = Idx(bb, sb, 1, sa); }
                            var pts = new[] { P[a0], P[a1], P[b1], P[b0] };
                            AddPoly(verts, norms, uvs, tans, tris, pts,
                                    Unit(a) * (sa == 0 ? -1f : 1f) + Unit(bb) * (sb == 0 ? -1f : 1f));
                        }

            // 8 个角三角
            for (int sx = 0; sx < 2; sx++)
                for (int sy = 0; sy < 2; sy++)
                    for (int sz = 0; sz < 2; sz++)
                    {
                        var pts = new[]
                        {
                            P[Idx(0, sx, sy, sz)],
                            P[Idx(1, sy, sz, sx)],
                            P[Idx(2, sz, sx, sy)],
                        };
                        AddPoly(verts, norms, uvs, tans, tris, pts,
                                new Vector3(sx == 0 ? -1f : 1f, sy == 0 ? -1f : 1f, sz == 0 ? -1f : 1f));
                    }
        }

        /// <summary>24 点索引：axis 轴、sign 侧、另两轴（o1、o2）的符号。</summary>
        static int Idx(int axis, int sign, int s1, int s2) =>
            ((axis * 2 + sign) * 2 + s1) * 2 + s2;

        /// <summary>写一个（凸）多边形面：法线用期望面法线、绕序按点积矫正、UV 按法线主轴盒投影（2 m 一循环）、
        /// 切线取面内 u 方向并带手性符号。</summary>
        static void AddPoly(List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs,
                            List<Vector4> tans, List<int> tris, Vector3[] ptsIn, Vector3 expected)
        {
            var pts = (Vector3[])ptsIn.Clone();
            Vector3 n = Vector3.Normalize(expected);
            if (Vector3.Dot(Vector3.Cross(pts[1] - pts[0], pts[2] - pts[0]), n) < 0f)
                Array.Reverse(pts);

            int major = DominantAxis(n);
            int u0 = (major + 1) % 3, u1 = (major + 2) % 3;
            Vector3 tangent = Unit(u0), bitangent = Unit(u1);
            float w = Vector3.Dot(Vector3.Cross(n, tangent), bitangent) < 0f ? -1f : 1f;

            int b = verts.Count;
            for (int i = 0; i < pts.Length; i++)
            {
                verts.Add(pts[i]);
                norms.Add(n);
                uvs.Add(new Vector2(pts[i][u0] * 0.5f + 0.5f, pts[i][u1] * 0.5f + 0.5f));
                tans.Add(new Vector4(tangent.x, tangent.y, tangent.z, w));
            }
            for (int i = 1; i < pts.Length - 1; i++)
            {
                tris.Add(b);
                tris.Add(b + i);
                tris.Add(b + i + 1);
            }
        }

        static int DominantAxis(Vector3 n)
        {
            float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
            if (ax >= ay && ax >= az) return 0;
            return ay >= az ? 1 : 2;
        }

        static Vector3 Unit(int axis) =>
            axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;

        // ==================================================================== 报告

        static void AppendPlanSummary(StringBuilder sb, Ctx ctx)
        {
            sb.AppendLine($"[计划] {P_Plan}");
            sb.AppendLine($"       共 {ctx.PlanItems} 件；过滤后候选 {ctx.Candidates.Count} 件" +
                          $"（-only [{string.Join(",", P_Only)}] -block {P_Block ?? "*"} -limit {P_Limit} 剔除 {ctx.FilteredOut} 件）");
            sb.AppendLine($"[栏杆] railmode={P_RailMode}" +
                          (P_RailMode == "procedural" ? $"（程序化低模 {RailTrisPerPiece} tri/件，全量，深钢 {RailMatPath}）" :
                           P_RailMode == "sparse" ? $"（程序化低模 {RailTrisPerPiece} tri/件，每 2 件放 1 件，深钢）" :
                           P_RailMode == "fbx" ? "（用原 FBX + 该件材质）" : "（不生成）") +
                          $"；顶面 {RailTopY:F2}m");
            sb.AppendLine($"[v2] 接头套环：run 类每件两端各 1 圈（{CollarTrisPerPiece} tri/件，Trim 材质）；" +
                          $"u 边界吸附阈值 {P_USnap:F2}m（本次吸附 {ctx.Snapped} 件）；" +
                          $"高度节奏 {(P_Rhythm == 0 ? "关" : "开")}" +
                          $"（抖动了 {ctx.RhythmJittered} 件，迁入 mid {ctx.MidMigrated} 件）");
            sb.AppendLine($"[v3] 托架：桥架 1 颗 / 线槽 2 颗 SUPPORT_BRACKET 并进同组 mesh" +
                          $"（清单三角 {BracketTris(ctx)}/颗，本组统计 {ctx.Brackets} 颗）；" +
                          $"天花桥架 trayflip={P_TrayFlip}" +
                          (P_TrayFlip == 1 ? "（局部 +Z = forward）" : "（局部 +Z = -forward：背板贴顶、箱体下挂）"));
        }

        static void AppendIdTable(StringBuilder sb, Ctx ctx)
        {
            // 按候选集统计（干跑）或按实际放置统计（执行），同一张表两种口径
            var order = new List<string>();
            var stat = new Dictionary<string, long[]>(StringComparer.Ordinal);   // {件数, 三角, 组数}
            if (ctx.ByGroups)
            {
                foreach (var g in ctx.Groups)
                {
                    if (!stat.TryGetValue(g.Id, out var v)) { v = new long[3]; stat[g.Id] = v; order.Add(g.Id); }
                    if (!g.Trim) v[0] += g.Items.Count;      // Trim 组与本体同一批件：件数不重复计
                    v[1] += g.Tris;                          // 三角如实计入该 ID
                    v[2] += 1;
                }
            }
            else
            {
                // 干跑：按预算配额后的放置集统计（一件都没配到时退回候选集，便于看清清单本身）
                var src = ctx.Items.Count > 0 ? ctx.Items : ctx.Candidates;
                foreach (var it in src)
                {
                    if (!stat.TryGetValue(it.greeble, out var v)) { v = new long[3]; stat[it.greeble] = v; order.Add(it.greeble); }
                    v[0] += 1;
                    v[1] += ItemTris(ctx, it.greeble);
                }
            }
            order.Sort(StringComparer.Ordinal);

            sb.AppendLine("[分类] ID | mount | 件数 | 三角 | 合并组数");
            long total = 0, totalTris = 0, totalGroups = 0;
            foreach (var id in order)
            {
                var v = stat[id];
                string mount = ctx.Man.ContainsKey(id) ? ctx.Man[id].mount : "?";
                string flag = ctx.Assets.TryGetValue(id, out var a) && a.Fail != null ? $"  ← {a.Fail}" : "";
                sb.AppendLine($"  {id} | {mount} | {v[0]} | {v[1]:N0} | {v[2]}{flag}");
                total += v[0];
                totalTris += v[1];
                totalGroups += v[2];
            }
            sb.AppendLine($"  小计 | | {total} | {totalTris:N0} | {totalGroups}");
        }

        static void AppendAssetCheck(StringBuilder sb, Ctx ctx)
        {
            sb.AppendLine("[素材] 源根 " + ctx.SrcRoot);
            var ids = new List<string>(ctx.Assets.Keys);
            ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var a = ctx.Assets[id];
                if (a.Fail != null) { sb.AppendLine($"  ✗ {id}：{a.Fail}"); continue; }
                string fbx = IsRail(id) && RailIsProcedural ? "FBX 不需要（程序化）" : "FBX ✓";
                string extra = IsRunClass(id)
                    ? $"；接头套环每件 2 圈（{CollarTrisPerPiece} tri/件，Trim → {TrimMatPath}）" +
                      (BracketCount(id) > 0 ? $"；中段托架 {BracketCount(id)} 颗/件（SUPPORT_BRACKET，并进同组）" : "")
                    : "";
                sb.AppendLine($"  ✓ {id}：{fbx}，贴图 {a.TexHave}/5" +
                              $"；工程内材质 {(File.Exists(AssetPathToAbs($"{MatDir}/M_Greeble_{id}.mat")) ? "已存在" : "待建")}" +
                              $"；mesh {(File.Exists(AssetPathToAbs($"{MeshDir}/{AssetName(ctx.FirstBlockOf(id), id)}.asset")) ? "已存在" : "待建")}" +
                              extra);
            }
            bool needBracket = false;
            foreach (var it in ctx.Candidates)
                if (BracketCount(it.greeble) > 0) { needBracket = true; break; }
            if (needBracket)
            {
                var bmi = ctx.Man.ContainsKey(BracketId) ? ctx.Man[BracketId] : null;
                string bsrc = bmi != null ? Path.Combine(ctx.SrcRoot, FbxRel(bmi)) : null;
                string state = bmi == null ? "清单里没有该 ID" :
                               File.Exists(bsrc) ? "FBX ✓ " + FbxRel(bmi) : "缺 FBX " + FbxRel(bmi);
                sb.AppendLine($"  · 托架源 {BracketId}：{state}（清单三角 {BracketTris(ctx)}/颗）");
            }
        }

        static void AppendBudgetLine(StringBuilder sb, Ctx ctx)
        {
            sb.AppendLine($"[预算] 上限 {P_Budget:N0} tri；本次计划 {ctx.PlannedTris:N0} tri" +
                          (ctx.BudgetHit ? " ← 触顶，已停止发放配额" : "（未触顶）"));
            sb.AppendLine($"[配额] 放置 {(ctx.ByGroups ? ctx.Placed : ctx.Items.Count)} 件；未放置 {ctx.Unplaced} 件");
        }

        static void AppendSkipReasons(StringBuilder sb, Ctx ctx)
        {
            if (ctx.SkipReasons.Count == 0) return;
            sb.AppendLine("[未放置] 原因分布：");
            var keys = new List<string>(ctx.SkipReasons.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var k in keys) sb.AppendLine($"  {ctx.SkipReasons[k]} 件 ← {k}");
        }

        static void AppendSceneState(StringBuilder sb)
        {
            var scene = EditorSceneManager.GetActiveScene();
            int existing = 0;
            foreach (var go in scene.GetRootGameObjects())
                if (go != null && go.name == RootName) existing++;
            int meshAssets = 0;
            if (AssetDatabase.IsValidFolder(MeshDir))
                meshAssets = AssetDatabase.FindAssets("t:Mesh", new[] { MeshDir }).Length;
            sb.AppendLine($"[现场] 场景里已存在 {RootName} 根 {existing} 个；{MeshDir}/ 下已有 mesh 资产 {meshAssets} 份" +
                          (existing > 0 || meshAssets > 0 ? "（执行会先清掉重来）" : ""));
        }

        // ==================================================================== 运行清单落盘

        [Serializable]
        class OutDoc
        {
            public string tool = "MapSkin.GreebleScatter";
            public string mode = "run";
            public string timestamp;
            public string scene;
            public string plan;
            public string manifest;
            public string railmode;
            public string onlyFilter = "";
            public string blockFilter = "";
            public int limit;
            public int planItems;
            public int candidates;
            public int placed;
            public int unplaced;
            public long tris;
            public long budget;
            public bool budgetHit;
            public int meshAssets;
            public int objects;
            public int materialsCreated;
            public int materialsReused;
            public string trimMaterial = "";
            public string railMaterial = "";      // v3：程序化栏杆深钢材质
            public int trayflip;                  // v3：-trayflip（0 = 背板贴顶箱体下挂）
            public int brackets;                  // v3：run 件中段托架（SUPPORT_BRACKET 实例）
            public int collars;                  // v2①：接头套环（件数 = run 类件数 × 2 端）
            public int snappedToFaceEdge;        // v2①：贴齐面 u 边界的 run 件数
            public int rhythmJittered;           // v2②：做过高度抖动的墙件数
            public int midMigrated;              // v2②：迁到第三条高度 mid 的件数
            public List<OutId> perId = new List<OutId>();
            public List<OutBlock> perBlock = new List<OutBlock>();
            public List<string> skipReasons = new List<string>();
            public List<string> meshFiles = new List<string>();
        }

        [Serializable]
        class OutId
        {
            public string id;
            public string mount;
            public int placed;
            public long tris;
            public int groups;
            public string fail = "";
        }

        [Serializable]
        class OutBlock
        {
            public string block;
            public int placed;
            public long tris;
            public int objects;
        }

        /// <summary>运行清单（JsonUtility.ToJson(doc, true)）落盘到 -out（默认 _session_audit/greeble_scatter.json）。</summary>
        static void WriteOut(Ctx ctx, string mode, Dictionary<string, IdAssets> assets)
        {
            if (string.IsNullOrEmpty(P_Out)) return;
            var doc = new OutDoc
            {
                mode = mode,
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                scene = P_Scene,
                plan = P_Plan,
                manifest = P_Manifest,
                railmode = P_RailMode,
                onlyFilter = string.Join(",", P_Only),
                blockFilter = P_Block ?? "",
                limit = P_Limit,
                planItems = ctx.PlanItems,
                candidates = ctx.Candidates.Count,
                placed = ctx.ByGroups ? ctx.Placed : 0,
                unplaced = ctx.Unplaced,
                tris = ctx.Tris,
                budget = P_Budget,
                budgetHit = ctx.BudgetHit,
                meshAssets = ctx.MeshAssets,
                objects = ctx.Objects,
                materialsCreated = ctx.MaterialsCreated,
                materialsReused = ctx.MaterialsReused,
                trimMaterial = ctx.TrimMat != null ? TrimMatPath : "",
                railMaterial = ctx.RailMat != null ? RailMatPath : "",
                trayflip = P_TrayFlip,
                brackets = ctx.Brackets,
                collars = ctx.Collars,
                snappedToFaceEdge = ctx.Snapped,
                rhythmJittered = ctx.RhythmJittered,
                midMigrated = ctx.MidMigrated,
            };

            var ids = new List<string>();
            if (ctx.ByGroups)
            {
                foreach (var g in ctx.Groups) if (!ids.Contains(g.Id)) ids.Add(g.Id);
            }
            else
            {
                foreach (var it in ctx.Candidates) if (!ids.Contains(it.greeble)) ids.Add(it.greeble);
            }
            ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var rec = new OutId { id = id, mount = ctx.Man.ContainsKey(id) ? ctx.Man[id].mount : "" };
                if (ctx.ByGroups)
                {
                    foreach (var g in ctx.Groups)
                    {
                        if (g.Id != id) continue;
                        if (!g.Trim) rec.placed += g.Items.Count;   // Trim（接头套环）不重复计件
                        rec.tris += g.Tris;
                        if (!g.Trim) rec.groups++;
                        doc.meshFiles.Add(g.MeshPath);
                    }
                }
                else
                {
                    // 未进入合并阶段（一上来就没配额）：只统计真正配到额度的件，与顶层 placed 口径一致
                    foreach (var it in ctx.Items)
                        if (it.greeble == id) { rec.placed++; rec.tris += ItemTris(ctx, id); }
                }
                if (assets != null && assets.TryGetValue(id, out var a) && a.Fail != null) rec.fail = a.Fail;
                doc.perId.Add(rec);
            }

            var blockStat = new Dictionary<string, long[]>(StringComparer.Ordinal);
            foreach (var g in ctx.Groups)
            {
                string bn = string.IsNullOrEmpty(g.Block) ? "NOBLOCK" : g.Block;
                if (!blockStat.TryGetValue(bn, out var v)) { v = new long[3]; blockStat[bn] = v; }
                if (!g.Trim) v[0] += g.Items.Count;      // Trim 组不重复计件
                v[1] += g.Tris;
                v[2] += 1;
            }
            var bkeys = new List<string>(blockStat.Keys);
            bkeys.Sort(StringComparer.Ordinal);
            foreach (var k in bkeys)
                doc.perBlock.Add(new OutBlock { block = k, placed = (int)blockStat[k][0], tris = blockStat[k][1], objects = (int)blockStat[k][2] });

            var skeys = new List<string>(ctx.SkipReasons.Keys);
            skeys.Sort(StringComparer.Ordinal);
            foreach (var k in skeys) doc.skipReasons.Add($"{k} ×{ctx.SkipReasons[k]}");

            try
            {
                string dir = Path.GetDirectoryName(P_Out);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(P_Out, JsonUtility.ToJson(doc, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[功能件] 清单落盘失败：{P_Out}：{e.Message}");
            }
        }

        // ==================================================================== 运行上下文

        class Ctx
        {
            public readonly Dictionary<string, ManifestItem> Man = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
            public readonly List<PlanItem> Candidates = new List<PlanItem>();
            public readonly List<PlanItem> Items = new List<PlanItem>();
            public readonly List<Group> Groups = new List<Group>();
            public readonly Dictionary<string, Group> GroupMap = new Dictionary<string, Group>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> SkipReasons = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<string, FaceRec> Faces = new Dictionary<string, FaceRec>(StringComparer.Ordinal);

            public Dictionary<string, IdAssets> Assets;      // AppendIdTable / 报告用
            public Material TrimMat;                         // 接头套环材质
            public Material RailMat;                         // 程序化栏杆深钢材质
            public Mesh BracketMesh;                         // SUPPORT_BRACKET 的 FBX mesh（并进 run 件组）
            public string SrcRoot = DefaultSrcRoot;
            public bool FacesLoaded;
            public bool ByGroups;                            // true = 计数口径已切到 Groups（合并之后）
            public int Snapped;                              // v2①：贴齐 u 边界的件数
            public int Collars;                              // v2①：接头套环总数（件数 × 2 端）
            public int Brackets;                             // v3：中段托架总数
            public int RhythmJittered;                       // v2②：做过高度抖动的件数
            public int MidMigrated;                          // v2②：迁到第三条高度 mid 的件数
            public int PlanItems;
            public int FilteredOut;
            public int Unplaced;
            public int Placed;
            public long PlannedTris;
            public long Tris;
            public bool BudgetHit;
            public int MeshAssets;
            public int Objects;
            public int MaterialsCreated;
            public int MaterialsReused;

            public void Skip(string reason, int count = 1)
            {
                if (string.IsNullOrEmpty(reason)) reason = "未分类";
                SkipReasons.TryGetValue(reason, out int n);
                SkipReasons[reason] = n + count;
            }

            public string FirstBlockOf(string id)
            {
                foreach (var it in Candidates) if (it.greeble == id) return it.block;
                return null;
            }
        }

        /// <summary>一个 (block, ID) 组：同组合并成一个 mesh。Trim = 该件的接头套环组（另一份 mesh + 另一份材质）。</summary>
        class Group
        {
            public string Block;
            public string Id;
            public bool IsRail;
            public bool Trim;
            public readonly List<PlanItem> Items = new List<PlanItem>();
            public Mesh Merged;
            public string MeshPath;
            public long Tris;
        }

        /// <summary>一个 ID 的素材可用性。</summary>
        class IdAssets
        {
            public string Id;
            public Mesh Src;          // FBX 首 mesh；程序化栏杆为 null（用模板）
            public Material Mat;
            public int TexHave;
            public string Fail;       // 非空 = 该类不可用（整类剔除）
        }

        // ==================================================================== JSON 结构（只声明需要的字段）

        [Serializable]
        class PlanDoc
        {
            public int version;
            public List<PlanItem> items = new List<PlanItem>();
        }

        /// <summary>散布点。字段名与 JSON 键一一对应（JsonUtility 区分大小写）。
        /// run_dir / wall_dir 是 V2 新增的信息性字段：天花桥架给水平走向，地面格栅给贴墙走向。</summary>
        [Serializable]
        class PlanItem
        {
            public string greeble;
            public float[] pos;
            public float[] forward;
            public float[] mount_normal;
            public float yaw_deg;
            public float scale = 1f;
            public string face_id;
            public string face_style;
            public string block;
            public string tier;
            public float height_m;
            public float floor_y;
            public float item_height_m;
            public float length_u_m;
            public string role;
            public float u;
            public float[] run_dir;          // V2：水平走向（天花桥架）
            public float[] wall_dir;         // V2：贴的那面墙的走向（地面格栅）
        }

        [Serializable]
        class ManifestDoc
        {
            public int version;
            public List<ManifestItem> items = new List<ManifestItem>();
        }

        /// <summary>素材清单条目：只取用得到的字段（textures 是定键字典，用定字段类接）。</summary>
        [Serializable]
        class ManifestItem
        {
            public string id;
            public string asset;
            public string fbx;
            public string folder;
            public string mount;
            public string forward_unity;
            public float[] dims_m;
            public int triangles;
            public string[] emissive;
            public TexSet textures;
        }

        [Serializable]
        class TexSet
        {
            public string BC;
            public string N;
            public string MS;
            public string AO;
            public string E;
        }

        /// <summary>face_flat.json：3843 个面的扁平规格（只取吸附用得到的字段）。</summary>
        [Serializable]
        class FaceDoc
        {
            public int version;
            public List<FaceRec> faces = new List<FaceRec>();
        }

        /// <summary>面：origin/u/v 是世界系（u、v 为单位向量），outline 是局部 uv 多边形（米，成对）。</summary>
        [Serializable]
        class FaceRec
        {
            public string id;
            public float[] origin;
            public float[] u;
            public float[] v;
            public float[] n;
            public float[] outline;
            public float w;
            public float h;
            public string shape;
        }

        static string FbxRel(ManifestItem mi)
        {
            if (!string.IsNullOrEmpty(mi.fbx)) return mi.fbx;
            return $"{mi.folder}/SM_Greeble_{mi.id}.fbx";      // 清单缺字段时的约定兜底
        }

        static string TexRel(ManifestItem mi, string ch)
        {
            string rel = null;
            if (mi.textures != null)
            {
                switch (ch)
                {
                    case "BC": rel = mi.textures.BC; break;
                    case "N": rel = mi.textures.N; break;
                    case "MS": rel = mi.textures.MS; break;
                    case "AO": rel = mi.textures.AO; break;
                    case "E": rel = mi.textures.E; break;
                }
            }
            if (string.IsNullOrEmpty(rel)) rel = $"{mi.folder}/T_Greeble_{mi.id}_{ch}.png";
            return rel;
        }

        // ==================================================================== 参数解析 / 路径工具

        /// <summary>命令行参数（也作用于菜单：与其它 MapSkin 工具同规矩）。
        /// -scene / -plan / -manifest / -budget / -railmode / -only / -block / -limit / -out / -save</summary>
        static void ParseArgs()
        {
            P_Scene = DefaultScenePath;
            P_Plan = DefaultPlanPath;
            P_Manifest = DefaultManifestPath;
            P_Budget = 3000000;                // v2 计划全量 ≈ 2.74M tri（含套环），默认预算抬到 3.0M 保完整装配
            P_RailMode = "procedural";
            P_Only.Clear();
            P_Block = null;
            P_Limit = 0;
            P_Out = DefaultOutPath;
            P_Save = 1;
            P_Faces = DefaultFaceFlatPath;
            P_USnap = 0.6f;
            P_Rhythm = 1;
            P_TrayFlip = 0;

            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
            {
                switch (a[i])
                {
                    case "-scene": P_Scene = a[i + 1]; break;
                    case "-plan": P_Plan = a[i + 1]; break;
                    case "-manifest": P_Manifest = a[i + 1]; break;
                    case "-budget": P_Budget = InL(a[i + 1], P_Budget); break;
                    case "-railmode": P_RailMode = (a[i + 1] ?? "").Trim().ToLowerInvariant(); break;
                    case "-only":
                        foreach (var s in (a[i + 1] ?? "").Split(','))
                            if (s.Trim().Length > 0) P_Only.Add(s.Trim());
                        break;
                    case "-block": P_Block = string.IsNullOrEmpty(a[i + 1]) ? null : a[i + 1]; break;
                    case "-limit": P_Limit = In(a[i + 1], P_Limit); break;
                    case "-out": if (!string.IsNullOrEmpty(a[i + 1])) P_Out = a[i + 1]; break;
                    case "-save": P_Save = In(a[i + 1], P_Save) == 0 ? 0 : 1; break;
                    case "-faces": if (!string.IsNullOrEmpty(a[i + 1])) P_Faces = a[i + 1]; break;
                    case "-usnap": P_USnap = Fl(a[i + 1], P_USnap); break;
                    case "-rhythm": P_Rhythm = In(a[i + 1], P_Rhythm) == 0 ? 0 : 1; break;
                    case "-trayflip": P_TrayFlip = In(a[i + 1], P_TrayFlip) == 0 ? 0 : 1; break;
                }
            }

            switch (P_RailMode)
            {
                case "proc":
                case "procedural":
                case "lowpoly": P_RailMode = "procedural"; break;
                case "sparse": P_RailMode = "sparse"; break;
                case "fbx": P_RailMode = "fbx"; break;
                case "skip":
                case "off":
                case "none": P_RailMode = "skip"; break;
                default:
                    Debug.LogWarning($"[功能件] 未知 -railmode '{P_RailMode}'，按 procedural 处理（可选：procedural/sparse/fbx/skip）");
                    P_RailMode = "procedural";
                    break;
            }
            P_Budget = Math.Max(1000L, Math.Min(P_Budget, 50000000L));
            P_Limit = Mathf.Clamp(P_Limit, 0, 1000000);
            P_USnap = Mathf.Clamp(P_USnap, 0f, 5f);
            P_Scene = (P_Scene ?? "").Replace('\\', '/');
        }

        static float Fl(string s, float d) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;

        static int In(string s, int d) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;

        static long InL(string s, long d) =>
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;

        /// <summary>Assets 相对路径 → 磁盘绝对路径。</summary>
        static string AssetPathToAbs(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return assetPath;
            string p = assetPath.Replace('\\', '/');
            if (p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(Path.Combine(Application.dataPath, p.Substring("Assets/".Length)));
            return Path.GetFullPath(p);
        }

        /// <summary>逐级建目录（Assets 相对路径）。</summary>
        static void EnsureFolder(string assetFolder)
        {
            if (string.IsNullOrEmpty(assetFolder) || AssetDatabase.IsValidFolder(assetFolder)) return;
            int i = assetFolder.LastIndexOf('/');
            string parent = i > 0 ? assetFolder.Substring(0, i) : "Assets";
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, assetFolder.Substring(i + 1));
        }
    }
}
