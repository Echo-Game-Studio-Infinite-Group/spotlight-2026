#if UNITY_EDITOR
// 走廊扩建器 v3 (2026-10-10, 用户令: 拓宽+延长+角色接入+门外造景)
// 架构口径: Unity 场景是唯一基线, 本工具直接改现场景 —— 不回 Blender 源头、不走 manifest 重建。
// (最终轮 FinalRoundCore 会 Build("corridor") 从旧 manifest 重建场景 = 会抹掉本工具的一切改动,
//  扩建后的收尾必须走本文件的 RunAll() 序列, 它复用管线公开函数但跳过 Build。)
//
// v3 要点: 全程**不用 Object.Instantiate** —— 2022.3 批处理模式下原生克隆
// (Internal_CloneSingleWithParent) 实测 SIGSEGV, 改为手工重建
// (新建 GO + 拷网格/材质/静态标记, Light/Camera 走 EditorUtility.CopySerialized)。
//
// 尺寸: 内宽 4.4→8m(墙 ±2.2→±4.0, ΔW=1.8); 全长 18.9→约 60m(+x 端从 0.2 延到 40.8)。
// 门外造景 = 观景台(平台+矮墙+发光压顶) + 城市天际线(拷贝 SC_City_Rooftops 的 CITY_ROOT)
//            + 程序夜空(六面渐变+星点, 替换"走廊内壁当天空"的旧天空盒)。
// 幂等性: 一次性变换 —— 检测到 HST_EXT_ROOT / HST_CORR_EXT_DONE 即中止(重跑请先 git 还原场景)。
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HST
{
    public static class HstCorridorExtend
    {
        // ---------------- 参数区(顶部集中, 固定 seed) ----------------
        const string SceneDir = "Assets/Scenes";
        const string SceneName = "SC_Corridor_Pragmata";
        const float DeltaW = 1.8f;      // 单侧外移量: 内壁 2.2 → 4.0
        const float NewEndX = 40.8f;    // 新端墙位置(原 0.2)
        const float PanelPitch = 1.8f;  // 墙板间距(与 Blender 模块一致, 运行时会用实测值校验)
        // 门区灯定稿基值(b837130 场景实测 = 公式×0.33): BakeScenes 每次烤前会 ×0.33,
        // 不走 Build 就必须先重置回基值, 否则每次重烤门区都暗 33%。
        const float DoorTopI = 0.0396f;     // 100W×kArea0.0036/3颗 ×0.33
        const float FillI = 0.01584f;       // 80W ×kArea/6颗 ×0.33
        const float EndWashI = 0.02376f;    // 60W ×kArea/3颗 ×0.33
        // 城市/观景台(最近沿定位: 楼群最近沿门外 60m、底面 -25m、z 居中、原尺寸)
        // 注: 量包围盒必须**排除地面板** —— CITY_Ground 有 1080m 跨度, 会把楼群(16~80m)量没
        const float CityNearX = -60f, CityBaseY = -25f, CityZ = 0f, CityScale = 1.0f, CityYaw = 90f;
        const int SkySeed = 20261010;

        [MenuItem("HST/走廊扩建(拓宽+延长+门外造景)")]
        public static void Extend() { ExtendCore(); }

        // 门外造景微调+只重出图(不动光照, 免重烤): 城市重心对齐大门视轴 + 挪开出生点机位
        [MenuItem("HST/走廊门外造景微调+重出图")]
        public static void RetuneOutside()
        {
            EditorSceneManager.OpenScene(SceneDir + "/" + SceneName + ".unity", OpenSceneMode.Single);
            var city = GameObject.Find("HST_EXT_CITY");
            if (city != null) RetuneCityPlacement(city);
            var cam6 = GameObject.Find("CAM_06_Spawn");
            if (cam6 != null) cam6.transform.position = new Vector3(35.0f, 1.62f, 0f);   // 退到角色身后, 免后脑勺挡画面
            // 门外辉光板(上午给封闭大门假造"门后有光"的自发光白板, 带碰撞体还堵着出门路)——
            // 现在门外有真城市/夜空, 必须拆掉, 否则门洞永远是刺眼白块
            var beyond = GameObject.Find("LUMEN_BeyondGlow");
            if (beyond != null) { Object.DestroyImmediate(beyond); Log("已拆除门外辉光板 LUMEN_BeyondGlow"); }
            // 玩法机位放开远裁剪: 走廊机位 far=60 是走廊尺度(城市在 60m 处会被整段裁掉)
            foreach (var nm in new[] { "CAM_06_Spawn", "CAM_07_City", "CAM_08_Facade" })
            {
                var g = GameObject.Find(nm);
                var c = g != null ? g.GetComponent<Camera>() : null;
                if (c != null) { c.farClipPlane = 3000f; }
            }
            var sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            EditorSceneManager.SaveScene(sc);
            SceneBuilder.Shoot();
            SceneBuilder.DiagLinear();
            Log("门外造景微调+重出图完成");
        }

        [MenuItem("HST/走廊扩建全流程(扩建+Player+重烤+探针+出图)")]
        public static void RunAll()
        {
            ExtendCore();
            StripExtStatic();   // 城市拷贝连静态标记一起抄来的会挤爆光烤图集(2026-10-10 实测烤挂根因)
            Log("步骤2/6 补UV2(幂等)"); SceneBuilder.FixMissingLightmapUVs();
            Log("步骤3/6 主控+碰撞+跟随相机"); SceneBuilder.AddWalkPlayer();
            Log("步骤4/6 定稿配方重烤(纯Baked+GPU, 数分钟)");
            SceneBuilder._indirectOverride = 4096;
            SceneBuilder.BakeCorridorOnly();
            SceneBuilder._indirectOverride = 0;
            SceneBuilder.Polish();                  // 相机对表+发光重定标+后处理锁默认
            Log("步骤5/6 手工反射立方盒重渲染(带门外城市)+相机裁决");
            SceneBuilder.BakeProbeManual();
            Log("步骤6/6 出图+线性读数");
            SceneBuilder.Shoot();
            SceneBuilder.DiagLinear();
            Log("走廊扩建全流程完成");
        }

        // 门外景观子树静态标记清零: 城市是从城市场景整树拷的(连 ContributeGI 一起抄),
        // 不清掉会被烤进光贴图 —— 80m 城市×24纹素/m 直接把图集/路径追踪器撑死
        // (症状: Bake 秒回 + PathTracer "Failed to render lightmap 0rpt" + 进程空转)。
        // 幂等: 对已存盘的 HST_EXT_ROOT 子树同样生效, 重跑即修复。
        public static void StripExtStatic()
        {
            var root = GameObject.Find("HST_EXT_ROOT");
            if (root == null) return;
            int n = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, (StaticEditorFlags)0);   // 此版本枚举无 Nothing/None, 0=全清
                n++;
            }
            EditorSceneManager.MarkSceneDirty(root.scene);
            EditorSceneManager.SaveScene(root.scene);
            Log("门外景观静态标记清零: " + n + " 节点(防挤爆光烤图集)");
        }

        internal static void Log(string s) => Debug.Log("[HST-EXT] " + s);
        // 崩溃追踪: 同步落盘(批处理 SEGV 时 Debug.Log 缓冲会丢, 文件不会)
        internal static void Tr(string s) => System.IO.File.AppendAllText("F:/VSCode/工业级渲染/tmp/ext_trace.txt", s + "\n");

        static void ExtendCore()
        {
            var scene = EditorSceneManager.OpenScene(SceneDir + "/" + SceneName + ".unity", OpenSceneMode.Single);
            // 幂等守卫必须在 OpenScene 之后: 批处理起手是空场景, 提前 Find 恒扑空
            // (2026-10-10 实测事故: 守卫放前面 → 扩建连跑两遍, 墙被二次外移成废品, 只能 git 还原)
            if (GameObject.Find("HST_EXT_ROOT") != null || GameObject.Find("HST_CORR_EXT_DONE") != null)
            { Debug.LogError("[HST-EXT] 已扩建过(检测到标记)。重跑请先 git 还原场景文件。"); return; }

            // ---------- 1. 侧墙外移: 墙板/墙基/踢脚/装饰条(墙面件, z 整体 ±ΔW) ----------
            // 注意: ARCH 网格是"顶点烤死世界坐标"(GO 变换全在原点), 侧向必须取自包围盒中心 ——
            // 取 transform.position.z 恒为 0, Sign(0)=1 会让两面墙挪向同侧。
            int moved = 0;
            foreach (var mr in AllMRs())
            {
                var n = mr.name;
                bool wallSkin = n.StartsWith("ARCH_Panel_") || n.StartsWith("ARCH_WallBase_") ||
                                n.StartsWith("ARCH_Skirt_") || n.StartsWith("ARCH_Trim_");
                if (!wallSkin) continue;
                mr.transform.position += new Vector3(0, 0, Mathf.Sign(mr.bounds.center.z) * DeltaW);
                moved++;
            }
            Log("1/9 侧墙件外移 " + moved + " 件");

            // ---------- 2. 墙板续铺(+x 端瓷砖式) + 端墙/门墙填充 ----------
            var panelTplN = GameObject.Find("ARCH_Panel_-10");   // +z 侧模板
            var panelTplP = GameObject.Find("ARCH_Panel_10");    // -z 侧模板
            // 节距只能用常量: 变换全在原点测不出间距(2026-10-10 实测坑: 测得 0 → 步长 0 死循环 → 内存爆)
            int clones = 0;
            var panelParent = panelTplN.transform.parent;
            foreach (var tpl in new[] { panelTplN, panelTplP })
            {
                if (tpl == null) continue;
                var b0 = tpl.GetComponent<MeshRenderer>().bounds;
                for (float cx = b0.center.x + PanelPitch; cx < NewEndX - 0.4f; cx += PanelPitch)
                {
                    var c = CloneMesh(tpl, panelParent, tpl.name + "_X");
                    AlignBoundsCenter(c, new Vector3(cx, b0.center.y, b0.center.z));
                    clones++;
                }
            }
            Tr("面板续铺完成 " + clones);
            Tr("开始端墙段");
            // 端墙: 删原 ARCH_EndWall, 用旋转 90° 的墙板铺满(纹理 1:1)
            var endWall = GameObject.Find("ARCH_EndWall");
            var endParent = endWall != null ? endWall.transform.parent : panelParent;
            if (endWall != null) Object.DestroyImmediate(endWall);
            for (int k = 0; k < 3; k++)
                foreach (float side in new[] { 1f, -1f })
                {
                    var c = CloneMesh(panelTplN, endParent, "ARCH_EndWall_X_" + side + "_" + k);
                    c.transform.rotation = Quaternion.Euler(0, 90, 0);   // 长轴 x→z
                    // 旋转后网格自带烘焙偏移, 对位必须按包围盒(变换算术会双计偏移)
                    AlignBoundsCenter(c, new Vector3(NewEndX - 0.025f, 1.7f, side * (0.895f + k * PanelPitch)));
                }
            // 门墙加宽: 删 DoorWall_Col/Head(会与填充板同平面 z-fight), 门洞两侧各铺 2 块旋转板
            Tr("端墙克隆完成, 开始门墙段");
            foreach (var kill in new[] { "ARCH_DoorWall_Col_nY", "ARCH_DoorWall_Col_pY", "ARCH_DoorWall_Head" })
            { var g = GameObject.Find(kill); if (g != null) Object.DestroyImmediate(g); }
            foreach (float side in new[] { 1f, -1f })
                for (int k = 0; k < 2; k++)
                {
                    var c = CloneMesh(panelTplN, endParent, "ARCH_DoorWallFill_X_" + side + "_" + k);
                    c.transform.rotation = Quaternion.Euler(0, 90, 0);
                    AlignBoundsCenter(c, new Vector3(-17.98f, 1.7f, side * (1.75f + k * 1.79f)));
                }
            Tr("门墙段完成");
            Log("2/9 墙板续铺 " + clones + " + 端墙/门墙填充");

            // ---------- 3. 长条件延长(按各自实测长度为节距, 首尾相接无缝; 尾部越界由外壳遮蔽) ----------
            string[] stayZ = { "ARCH_LightPane_", "ARCH_CoveTop_", "ARCH_CoveWall_" };   // 中央灯槽: 位置不动
            int tiled = 0;
            foreach (var mr in AllMRs())
            {
                var n = mr.name;
                var go = mr.gameObject;
                float len = mr.bounds.size.x;
                if (len < 2f) continue;   // 端帽/门框等小件单独处理

                if (StartsWithAny(n, stayZ))
                { Tr("Tile " + n); tiled += TileAlongX(go, len); }
                else if (StartsWithAny(n, new[] { "ARCH_Skirt_", "ARCH_Trim_", "ARCH_WallBase_" }))
                { Tr("Tile " + n); tiled += TileAlongX(go, len); }   // 已在步骤1外移, 这里只补长度
                else if (n == "ARCH_Floor")
                {
                    Tr("Floor 双拼");
                    // 补宽: 每档 x 两块 z=±2.4(各 4.8m 宽, 中缝 z=0, 外沿藏进外壳), 最后删原块
                    var parent = go.transform.parent;
                    var b0 = mr.bounds;
                    for (int k = 0; b0.max.x + k * len < NewEndX + len; k++)
                        foreach (float side in new[] { 1f, -1f })
                        {
                            var c = CloneMesh(go, parent, "ARCH_Floor_X" + (side > 0 ? "P" : "N") + "_" + k);
                            AlignBoundsCenter(c, new Vector3(b0.center.x + k * len, b0.center.y, side * 2.4f));
                            tiled++;
                        }
                    Object.DestroyImmediate(go);
                }
                else if (n == "ARCH_Ceil_Mid")
                { Tr("Ceil_Mid"); tiled += TileAlongX(go, len); }   // 中央天花 ±0.88 不变
                else if (n == "ARCH_Ceil_nY" || n == "ARCH_Ceil_pY")
                {
                    Tr("Ceil 链 " + n);
                    // 补宽: 原板留原地(1.22..2.20), 按 z 向节距链式外延到越过新墙内沿(越界进外壳)
                    float w = mr.bounds.size.z;
                    float zs = Mathf.Sign(mr.bounds.center.z);
                    for (int k = 1; k * w < DeltaW + w; k++)
                    {
                        var c = CloneMesh(go, go.transform.parent, n + "_X" + (k * w).ToString("0.##"));
                        AlignBoundsCenter(c, mr.bounds.center + new Vector3(0, 0, zs * k * w));
                        tiled += TileAlongX(c, len);
                    }
                    tiled += TileAlongX(go, len);
                }
            }
            Log("3/9 长条件延长+地板/天花补宽, 新增 " + tiled + " 件");

            // ---------- 4. 灯槽端帽: 删中段帽(_a 原端头), 门端帽(_b)保留, 新远端补帽 ----------
            var capB = GameObject.Find("ARCH_CoveCap_nY_b");
            if (capB != null)
            {
                var capParent = capB.transform.parent;
                foreach (var nm in new[] { "ARCH_CoveCap_nY_a", "ARCH_CoveCap_pY_a" })
                { var g = GameObject.Find(nm); if (g != null) Object.DestroyImmediate(g); }
                foreach (var src in new[] { "ARCH_CoveCap_nY_b", "ARCH_CoveCap_pY_b" })
                {
                    var tpl = GameObject.Find(src);
                    if (tpl == null) continue;
                    var bb = tpl.GetComponent<MeshRenderer>().bounds;
                    var c = CloneMesh(tpl, capParent, src.Replace("_b", "_far"));
                    AlignBoundsCenter(c, new Vector3(NewEndX - 0.6f, bb.center.y, bb.center.z));
                }
            }
            Log("4/9 灯槽端帽调整");

            // ---------- 5. 灯: 门区基值重置 + 灯带灯串续排(强度/颜色/间距全部继承实测) ----------
            int reset = 0;
            foreach (var li in Object.FindObjectsOfType<Light>(true))
            {
                if (li.name.StartsWith("L_LUMEN_DoorTop")) { li.intensity = DoorTopI; reset++; }
                else if (li.name.StartsWith("L_LUMEN_Fill")) { li.intensity = FillI; reset++; }
                else if (li.name.StartsWith("L_LUMEN_EndWash")) { li.intensity = EndWashI; reset++; }
            }
            int addedLights = 0;
            foreach (var chan in new[] { "ChannelL", "ChannelR" })
            {
                var lights = new List<Light>();
                foreach (var li in Object.FindObjectsOfType<Light>(true))
                    if (li.name.StartsWith("L_LUMEN_" + chan + "_")) lights.Add(li);
                if (lights.Count < 2) continue;
                lights.Sort((a, b) => a.transform.position.x.CompareTo(b.transform.position.x));
                var last = lights[lights.Count - 1];
                var first = lights[0];
                float sp = (last.transform.position.x - first.transform.position.x) / (lights.Count - 1);
                float nextX = last.transform.position.x + sp;
                int idx = lights.Count;
                while (nextX <= NewEndX - 0.6f)
                {
                    var c = new GameObject("L_LUMEN_" + chan + "_" + idx++);
                    c.transform.SetParent(last.transform.parent, false);
                    var li = c.AddComponent<Light>();
                    EditorUtility.CopySerialized(last, li);
                    var p = last.transform.position;
                    c.transform.position = new Vector3(nextX, p.y, p.z);
                    nextX += sp;
                    addedLights++;
                }
            }
            Tr("灯段完成 reset=" + reset + " added=" + addedLights);
            Log("5/9 门区灯基值重置 " + reset + " 颗 + 灯带续排 " + addedLights + " 颗(与定稿同密度)");

            // ---------- 6. 开门: 门叶是双叶一体网格, 直接移除(保留发光门框) ----------
            foreach (var nm in new[] { "ARCH_DoorLeaf", "ARCH_DoorSeam" })
            { var g = GameObject.Find(nm); if (g != null) Object.DestroyImmediate(g); }
            Log("6/9 开门(移除门叶, 保留发光门框)");

            // ---------- 7. 夜空(六面渐变+星点; 反射通道模式不动) ----------
            MakeNightSky();
            Log("7/9 夜空天空盒已挂载(M_SkyNight)");

            // ---------- 8. 门外造景: 观景台 + 外壳 + 城市 ----------
            var extRoot = new GameObject("HST_EXT_ROOT");
            BuildPlatform(extRoot.transform);
            BuildShell(extRoot.transform);
            PlaceCity(extRoot.transform);
            Log("8/9 观景台+外壳+城市背景已建");

            // ---------- 9. 玩法相机: 远端跑道 / 平台看城 / 回望建筑 ----------
            var cam1 = GameObject.Find("CAM_01_Hero");
            if (cam1 != null)
            {
                AddPlayCam(cam1, "CAM_06_Spawn", new Vector3(38.5f, 1.62f, 0), cam1.transform.rotation);
                AddPlayCam(cam1, "CAM_07_City", new Vector3(-23.8f, 1.65f, 0), cam1.transform.rotation);
                var back = new GameObject("CAM_08_Facade");
                back.transform.SetParent(cam1.transform.parent, false);
                var bcam = back.AddComponent<Camera>();
                EditorUtility.CopySerialized(cam1.GetComponent<Camera>(), bcam);
                bcam.depth = 0;
                back.transform.position = new Vector3(-25.5f, 1.65f, 0);
                back.transform.rotation = Quaternion.Euler(0, 90, 0);   // 回望 +x 建筑正面
            }
            Log("9/9 玩法相机 CAM_06/07/08 已加");

            var marker = new GameObject("HST_CORR_EXT_DONE");
            marker.transform.SetSiblingIndex(0);
            EditorSceneManager.SaveScene(scene);
            Log("扩建完成并已存盘");
        }

        // ---------------- 手工克隆(绕开 2022.3 批处理 Instantiate SIGSEGV) ----------------
        // 网格件: 新建 GO + 拷 TRS/网格/材质/静态标记(不带碰撞体, AddWalkPlayer 稍后统一补)
        internal static GameObject CloneMesh(GameObject src, Transform parent, string name)
        {
            var g = new GameObject(name);
            g.transform.SetParent(parent, false);
            g.transform.localPosition = src.transform.localPosition;
            g.transform.localRotation = src.transform.localRotation;
            g.transform.localScale = src.transform.localScale;
            var smf = src.GetComponent<MeshFilter>();
            var smr = src.GetComponent<MeshRenderer>();
            if (smf != null) g.AddComponent<MeshFilter>().sharedMesh = smf.sharedMesh;
            if (smr != null)
            {
                var nr = g.AddComponent<MeshRenderer>();
                nr.sharedMaterials = smr.sharedMaterials;
                nr.shadowCastingMode = smr.shadowCastingMode;
                nr.receiveShadows = smr.receiveShadows;
                nr.lightProbeUsage = smr.lightProbeUsage;
                nr.reflectionProbeUsage = smr.reflectionProbeUsage;
            }
            GameObjectUtility.SetStaticEditorFlags(g, GameObjectUtility.GetStaticEditorFlags(src));
            return g;
        }

        // 整树网格拷贝(城市背景用: 只拷 TRS+MeshFilter/MeshRenderer, 不带灯/相机/脚本)
        static GameObject CopyTree(GameObject src, Transform parent)
        {
            var g = CloneMesh(src, parent, src.name);
            foreach (Transform c in src.transform) CopyTree(c.gameObject, g.transform);
            return g;
        }

        static void AddPlayCam(GameObject tpl, string name, Vector3 pos, Quaternion rot)
        {
            var old = GameObject.Find(name);
            if (old != null) Object.DestroyImmediate(old);
            var g = new GameObject(name);
            g.transform.SetParent(tpl.transform.parent, false);
            var cam = g.AddComponent<Camera>();
            EditorUtility.CopySerialized(tpl.GetComponent<Camera>(), cam);
            cam.depth = 0;
            g.transform.position = pos;
            g.transform.rotation = rot;
        }

        // ---------------- 工具函数 ----------------
        internal static IEnumerable<MeshRenderer> AllMRs()
        { foreach (var r in Object.FindObjectsOfType<MeshRenderer>(true)) yield return r; }

        internal static bool StartsWithAny(string s, string[] prefixes)
        { foreach (var p in prefixes) if (s.StartsWith(p)) return true; return false; }

        // 以"件长=节距"沿 +x 复制到盖过新端墙(尾部越界由外壳遮蔽); 返回新增数
        static int TileAlongX(GameObject go, float len)
        {
            int made = 0;
            var b0 = go.GetComponent<MeshRenderer>().bounds;
            var parent = go.transform.parent;
            for (int k = 1; b0.min.x + k * len < NewEndX; k++)
            {
                var c = CloneMesh(go, parent, go.name + "_X" + k);
                AlignBoundsCenter(c, b0.center + new Vector3(k * len, 0, 0));
                made++;
            }
            return made;
        }

        // 按渲染包围盒对位: ARCH 网格"顶点烤死世界坐标"(GO 变换全在原点),
        // 克隆/旋转后的定位只能用包围盒校准, 变换算术会双计烘焙偏移
        internal static void AlignBoundsCenter(GameObject g, Vector3 wantCenter)
        {
            var b = g.GetComponent<MeshRenderer>().bounds;
            g.transform.position += wantCenter - b.center;
        }

        // 夜空: 六面 PNG(1024) —— 侧面上暗下微亮地平线, 顶面径向, 底面近黑; 星点固定 seed
        static void MakeNightSky()
        {
            const string dir = "Assets/Art/Environment/HST_Kit/Skybox";
            if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder("Assets/Art", "Skybox");
            var rnd = new System.Random(SkySeed);
            Color Zenith = Hex(0x08, 0x0A, 0x16), Horizon = Hex(0x1A, 0x26, 0x34), Below = Hex(0x04, 0x05, 0x08);

            Texture2D Side()
            {
                var t = new Texture2D(1024, 1024, TextureFormat.RGBA32, false);
                var px = new Color[1024 * 1024];
                for (int y = 0; y < 1024; y++)
                {
                    float v = y / 1023f;
                    Color c = v > 0.5f ? Color.Lerp(Horizon, Zenith, (v - 0.5f) * 2f)
                                       : Color.Lerp(Below, Horizon, v * 2f);
                    for (int x = 0; x < 1024; x++) px[y * 1024 + x] = c;
                }
                for (int i = 0; i < 220; i++)   // 星点只撒在上半天
                {
                    int x = rnd.Next(1024), y = 560 + rnd.Next(464);
                    float b = 0.15f + (float)rnd.NextDouble() * 0.65f;
                    px[y * 1024 + x] = new Color(b, b, b * 1.08f, 1);
                }
                t.SetPixels(px); t.Apply(); return t;
            }
            Texture2D Top()
            {
                var t = new Texture2D(1024, 1024, TextureFormat.RGBA32, false);
                var px = new Color[1024 * 1024];
                for (int y = 0; y < 1024; y++)
                    for (int x = 0; x < 1024; x++)
                    {
                        float r = Vector2.Distance(new Vector2(x, y), new Vector2(512, 512)) / 724f;
                        px[y * 1024 + x] = Color.Lerp(Zenith, Horizon, Mathf.Clamp01(r));
                    }
                for (int i = 0; i < 480; i++)
                {
                    int x = rnd.Next(1024), y = rnd.Next(1024);
                    float b = 0.12f + (float)rnd.NextDouble() * 0.6f;
                    px[y * 1024 + x] = new Color(b, b, b * 1.08f, 1);
                }
                t.SetPixels(px); t.Apply(); return t;
            }
            Texture2D Bottom()
            {
                var t = new Texture2D(1024, 1024, TextureFormat.RGBA32, false);
                var px = new Color[1024 * 1024];
                for (int i = 0; i < px.Length; i++) px[i] = Below;
                t.SetPixels(px); t.Apply(); return t;
            }
            void SaveFace(Texture2D t, string file)
            {
                File.WriteAllBytes(dir + "/" + file, t.EncodeToPNG());
                Object.DestroyImmediate(t);
            }
            SaveFace(Side(), "T_SkyNight_F.png");          // 四向同一张侧脸(一致渐变, 省生成)
            File.Copy(dir + "/T_SkyNight_F.png", dir + "/T_SkyNight_B.png", true);
            File.Copy(dir + "/T_SkyNight_F.png", dir + "/T_SkyNight_L.png", true);
            File.Copy(dir + "/T_SkyNight_F.png", dir + "/T_SkyNight_R.png", true);
            SaveFace(Top(), "T_SkyNight_U.png");
            SaveFace(Bottom(), "T_SkyNight_D.png");
            AssetDatabase.Refresh();
            foreach (var f in new[] { "F", "B", "L", "R", "U", "D" })
            {
                var imp = (TextureImporter)AssetImporter.GetAtPath(dir + "/T_SkyNight_" + f + ".png");
                if (imp != null) { imp.wrapMode = TextureWrapMode.Clamp; imp.SaveAndReimport(); }
            }
            var sky = AssetDatabase.LoadAssetAtPath<Material>(dir + "/M_SkyNight.mat");
            if (sky == null)
            {
                sky = new Material(Shader.Find("Skybox/6 Sided")) { name = "M_SkyNight" };
                AssetDatabase.CreateAsset(sky, dir + "/M_SkyNight.mat");
            }
            sky.SetTexture("_FrontTex", Load(dir + "/T_SkyNight_F.png"));
            sky.SetTexture("_BackTex", Load(dir + "/T_SkyNight_B.png"));
            sky.SetTexture("_LeftTex", Load(dir + "/T_SkyNight_L.png"));
            sky.SetTexture("_RightTex", Load(dir + "/T_SkyNight_R.png"));
            sky.SetTexture("_UpTex", Load(dir + "/T_SkyNight_U.png"));
            sky.SetTexture("_DownTex", Load(dir + "/T_SkyNight_D.png"));
            sky.SetColor("_Tint", new Color(0.9f, 0.95f, 1f, 1));
            sky.SetFloat("_Exposure", 1f);
            EditorUtility.SetDirty(sky);
            AssetDatabase.SaveAssets();
            RenderSettings.skybox = sky;   // 只换材质; defaultReflectionMode=Skybox 保持, 探针混合通道不动
        }

        static Texture2D Load(string path) => AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        static Color Hex(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f, 1);

        // 观景台: 平台 + 三面矮墙 + 发光压顶 + 支撑柱(门外 x -18.65..-24.35)
        static void BuildPlatform(Transform root)
        {
            var plat = new GameObject("HST_EXT_PLATFORM"); plat.transform.SetParent(root);
            var shell = MakeMat("M_HST_Shell", new Color(0.085f, 0.09f, 0.11f), 0.55f, 0.45f);
            var glow = MakeMat("M_HST_EdgeGlow", new Color(0.05f, 0.05f, 0.06f), 0f, 0.6f);
            glow.EnableKeyword("_EMISSION");
            glow.SetColor("_EmissionColor", new Color(0.9f, 1.25f, 1.5f) * 1.6f);

            Cube(plat.transform, "Slab", shell, new Vector3(-21.55f, -0.15f, 0), new Vector3(5.8f, 0.3f, 7.6f));
            Cube(plat.transform, "ParapetFar", shell, new Vector3(-24.29f, 0.5f, 0), new Vector3(0.12f, 1.0f, 7.6f));
            Cube(plat.transform, "ParapetL", shell, new Vector3(-21.55f, 0.5f, 3.74f), new Vector3(5.8f, 1.0f, 0.12f));
            Cube(plat.transform, "ParapetR", shell, new Vector3(-21.55f, 0.5f, -3.74f), new Vector3(5.8f, 1.0f, 0.12f));
            Cube(plat.transform, "GlowFar", glow, new Vector3(-24.29f, 1.02f, 0), new Vector3(0.16f, 0.05f, 7.6f));
            Cube(plat.transform, "GlowL", glow, new Vector3(-21.55f, 1.02f, 3.74f), new Vector3(5.8f, 0.05f, 0.16f));
            Cube(plat.transform, "GlowR", glow, new Vector3(-21.55f, 1.02f, -3.74f), new Vector3(5.8f, 0.05f, 0.16f));
            Cube(plat.transform, "Column", shell, new Vector3(-22f, -15.4f, 0), new Vector3(4f, 30f, 4f));
        }

        // 外壳: 把走廊包成黑色方碑(遮蔽长条件越界尾段), 正面留门洞
        static void BuildShell(Transform root)
        {
            var shellGo = new GameObject("HST_EXT_SHELL"); shellGo.transform.SetParent(root);
            var m = MakeMat("M_HST_Shell", new Color(0.085f, 0.09f, 0.11f), 0.55f, 0.45f);
            // 走廊体: x -18.9..55.5(尾部越界段全包), y -0.55..3.85, z ±4.9..5.2
            Cube(shellGo.transform, "SideL", m, new Vector3(18.3f, 1.65f, 5.05f), new Vector3(74.4f, 4.4f, 0.3f));
            Cube(shellGo.transform, "SideR", m, new Vector3(18.3f, 1.65f, -5.05f), new Vector3(74.4f, 4.4f, 0.3f));
            Cube(shellGo.transform, "Roof", m, new Vector3(18.3f, 4.0f, 0), new Vector3(74.4f, 0.3f, 10.7f));
            Cube(shellGo.transform, "Bottom", m, new Vector3(18.3f, -0.7f, 0), new Vector3(74.4f, 0.3f, 10.7f));
            Cube(shellGo.transform, "Back", m, new Vector3(55.65f, 1.65f, 0), new Vector3(0.3f, 4.4f, 10.7f));
            // 正面(门洞 z±0.95 / y 0..2.6): 两侧 + 门楣
            Cube(shellGo.transform, "FrontL", m, new Vector3(-19.05f, 1.65f, 3.0f), new Vector3(0.3f, 4.4f, 4.1f));
            Cube(shellGo.transform, "FrontR", m, new Vector3(-19.05f, 1.65f, -3.0f), new Vector3(0.3f, 4.4f, 4.1f));
            Cube(shellGo.transform, "FrontTop", m, new Vector3(-19.05f, 3.22f, 0), new Vector3(0.3f, 1.25f, 10.1f));
        }

        // 城市: 加载城市场景, 整树手工拷贝 CITY_ROOT(只拷几何, 不带 L_Sun/相机), 摆到门外
        static void PlaceCity(Transform root)
        {
            var cityScene = EditorSceneManager.OpenScene(SceneDir + "/SC_City_Rooftops.unity", OpenSceneMode.Additive);
            var cityRoot = GameObject.Find("CITY_ROOT");
            if (cityRoot == null)
            {
                Debug.LogError("[HST-EXT] SC_City_Rooftops 里没有 CITY_ROOT, 城市背景未挂");
                EditorSceneManager.CloseScene(cityScene, true);
                return;
            }
            Tr("城市整树拷贝开始");
            var copy = CopyTree(cityRoot, root);
            Tr("城市整树拷贝完成");
            copy.name = "HST_EXT_CITY";
            copy.transform.rotation = Quaternion.Euler(0, CityYaw, 0);
            EditorSceneManager.CloseScene(cityScene, true);
            RetuneCityPlacement(copy);
            Log("城市背景已挂(最近沿 x=" + CityNearX + ", 底面 y=" + CityBaseY + ", ×" + CityScale + ")");
        }

        // 最近沿定位法: 城市整树跨度 ~1080m(带 100× FBX 缩放), 按"最近沿钉在门外 CityNearX /
        // 底面压到 CityBaseY / z 居中"摆放, 缩放后再量包围盒 —— 不管内部布局怎么变都稳。
        static void RetuneCityPlacement(GameObject city)
        {
            city.transform.localScale = Vector3.one * CityScale;
            // 只按楼群量(排除地面/远壳): 地面板 1080m 跨度会把包围盒量成"地", 楼群就沉没了
            var rs = new List<MeshRenderer>();
            foreach (var r in city.GetComponentsInChildren<MeshRenderer>(true))
            {
                bool isGround = r.name.ToLower().Contains("ground") || HasAncestorNamed(r.transform, "Ground");
                if (!isGround) rs.Add(r);
            }
            if (rs.Count == 0) { Log("城市里没量到楼群, 跳过定位"); return; }
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            city.transform.position += new Vector3(CityNearX - b.max.x, CityBaseY - b.min.y, CityZ - b.center.z);
            Log("城市定位: 楼群跨度 " + b.size.ToString("0") + " → 最近沿 x=" + CityNearX + " 底面 y=" + CityBaseY);
        }

        static bool HasAncestorNamed(Transform t, string name)
        {
            while (t != null) { if (t.name.Contains(name)) return true; t = t.parent; }
            return false;
        }

        internal static GameObject Cube(Transform parent, string name, Material m, Vector3 pos, Vector3 scale)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            g.name = name;
            g.transform.SetParent(parent);
            g.transform.position = pos;
            g.transform.localScale = scale;
            g.GetComponent<MeshRenderer>().sharedMaterial = m;
            return g;
        }

        static Material MakeMat(string name, Color baseColor, float metallic, float smoothness)
        {
            var path = "Assets/Art/Environment/HST_Kit/Materials/HST_Prags/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", baseColor);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(m);
            AssetDatabase.SaveAssets();
            return m;
        }
    }
}
#endif
