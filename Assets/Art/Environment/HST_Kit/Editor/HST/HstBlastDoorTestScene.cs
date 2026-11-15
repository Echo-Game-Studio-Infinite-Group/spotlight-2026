#if UNITY_EDITOR
// 爆门试验场 —— 在 project-0 里**从零复刻** Blender 出图用的那套试验场景, 再装上团队现成的玩家
// 与跟随相机, 让用户能亲自跑起来、走近门看它自动开。
//
// 为什么不用走廊(HstCorridorBlastDoor 那套): 走廊内宽 8m / 净高 4.4m, **装不下**这台爆门 ——
//   门规格(door_spec): 洞口 6.50(W)×6.00(H), 框外 10.60(W)×7.85(H)+下沉基础槽(y 至 -1.56)。
//   所以试验场自带墙与地台, 门按规格**原尺寸**进场(不缩放)。
//
// 场景 = Blender GIF 场景(story_wall.py)的 Unity 版:
//   · 暗色地台(80×80m, 带碰撞)      —— 走上去不会掉
//   · 纯白墙 18(W)×10(H)×1.6(D), 洞口 = 门框外轮廓里侧(完全被门框脸盘遮住, 不露缝)
//   · 爆门(SM_TechBlastDoor_v1.fbx) 正面朝 Unity +Z, 原地站在地台上
//   · 玩家(Assets/Prefabs/Player.prefab) 在 +Z 侧 16m 处面朝门; 跟随相机用团队正式一套
//     (Main Camera + Virtual Camera, 由 HstCameraRig.FixScene 接线)
//   · 打光: Flat 环境光 + 一盏主方向光 + 三盏 Realtime 点光(零 Volume 后处理, 与团队口径一致)
//
// 可重跑: 每次 NewScene 重建 —— 不留残件, 换 FBX/贴图后重跑即重装。
//
// 用法(无头): bash tools/unity-batch.sh HstBlastDoorTestScene.BuildOnce
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using GameJam.Environment;

namespace HST
{
    public static class HstBlastDoorTestScene
    {
        public const string ScenePath = "Assets/Art/Environment/HST_Kit/Scenes/SC_BlastDoor_Test.unity";
        const string FbxPath = "Assets/Art/Environment/HST_Kit/TechBlastDoor/SM_TechBlastDoor_v1.fbx";
        const string ManPath = "Assets/Art/Environment/HST_Kit/TechBlastDoor/SM_TechBlastDoor_v1.manifest.json";
        const string MatDir = "Assets/Art/Environment/HST_Kit/TechBlastDoor/Materials";
        const string TexDir = "Assets/Art/Environment/HST_Kit/TechBlastDoor/Textures";

        // 门规格(door_spec): 洞口 6.50×6.00 / 框外 10.60×7.85 / 四角 45° 切 0.60
        const float FRAME_HX = 5.30f, FRAME_Z1 = 7.85f, FRAME_CH = 0.60f;
        const float WALL_XOUT = 9.00f, WALL_TOP = 10.00f;
        const float WALL_ZC = -0.14f, WALL_ZT = 1.60f;     // 墙厚 1.6, 中心 z=-0.14(门框偏前)
        // 墙洞口 = 门框"直边"之内(x ±(HX-CH)=±4.70, y 0..(Z1-CH)=7.25):
        // 于是洞口四周都被门框脸盘盖住 —— 门框四个 45° 切角处也是墙(不是缝)。
        const float HOLE_HX = FRAME_HX - FRAME_CH;         // 4.70
        const float HOLE_Z1 = FRAME_Z1 - FRAME_CH;         // 7.25

        // ★ 用户令: 比 GIF(1.0 m/s)快 50%
        const float DoorSpeed = 1.50f;
        // 背景近黑 —— 与 Blender 出图的 world 同值(0.010,0.013,0.018 线性); 天空盒只当反射源, 不露在画面里
        static readonly Color BackdropDark = new Color(0.010f, 0.013f, 0.018f, 1f);
        // 走近 6m 才触发(触发区 10m/侧; 出生点 z=16)
        const float TriggerDepth = 10f;

        static readonly Vector3 PlayerSpawn = new Vector3(0f, 0.10f, 16f);
        static readonly Quaternion PlayerSpawnRot = Quaternion.Euler(0f, 180f, 0f);   // 面朝 -Z(门)

        [MenuItem("HST/爆门试验场 全流程(重建场景+装门+玩家相机)")]
        public static void BuildOnce()
        {
            Build();
            HstCorridorExtend.Log("爆门试验场 " + ScenePath + " 完成");
        }

        [MenuItem("HST/爆门试验场 只重建(几何+材质)")]
        public static void BuildMenu() { Build(); }

        // ============================================================ 主流程
        static void Build()
        {
            EnsureFolders();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SetupRender();
            BuildLights();
            BuildReflection();
            BuildFloor();
            BuildWall();
            GameObject door = BuildDoor();
            if (door == null) { Debug.LogError("[BDT] 门没装上 —— 中止"); return; }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            HstCorridorExtend.Log("场景存盘 " + ScenePath + " (门/地台/墙/灯)");

            // 玩家 + 现成相机 rig —— 复用走廊那套已验证接线(不另写第二份)
            AssetDatabase.Refresh();
            HstCameraRig.FixScene(ScenePath, PlayerSpawn, PlayerSpawnRot);
            HstCorridorExtend.Log("玩家 + 跟随相机已接线(出生 z=" + PlayerSpawn.z + " 面朝门)");

            // 相机清屏改纯色暗底 —— 天空盒只当反射源, 不露在画面里(观感与 GIF 的暗背板一致)
            var sc = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var camGo = GameObject.Find("Main Camera");
            var cam = camGo != null ? camGo.GetComponent<Camera>() : null;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = BackdropDark;
                // ★ 关键(2026-10-15 实测): project-0 的 URP-Renderer 带 TA 的**高度蓝雾**(fogColor 浅蓝,
                //   maxFogOpacity 0.376), 它把整幅画面朝浅蓝抬亮 —— 同机位实测 mean 亮度 0.508 vs Blender 0.275
                //   (整幅 +0.23)。这是"Unity 观感与 Blender 不一样"的**头号原因**, 比 NPR/描边/Bloom 大一个量级
                //   (只关雾 mean 就回到 0.283, 全关 0.276)。TA 自带 FogCameraOverride 就是给单台相机调雾的 ——
                //   试验场要让门读起来跟 Blender 一致, 所以**本相机**把雾压掉(只动本相机, 不碰共享的
                //   URP-Renderer 资产; 想看"带雾的成品"把这个组件删掉即可)。
                var fog = camGo.GetComponent<FogCameraOverride>();
                if (fog == null) fog = camGo.AddComponent<FogCameraOverride>();
                fog.density = 0f;
                fog.maxOpacity = 0f;
                EditorSceneManager.MarkSceneDirty(sc);
                EditorSceneManager.SaveScene(sc);
                HstCorridorExtend.Log("Main Camera: 暗底清屏 + FogCameraOverride(0/0) —— 压掉 TA 蓝雾");
            }
        }

        // ============================================================ 目录
        static void EnsureFolders()
        {
            const string root = "Assets/Art/Environment/HST_Kit/TechBlastDoor";
            if (!AssetDatabase.IsValidFolder(MatDir)) AssetDatabase.CreateFolder(root, "Materials");
            if (!AssetDatabase.IsValidFolder(TexDir)) AssetDatabase.CreateFolder(root, "Textures");
        }

        // ============================================================ 渲染口径: 零后处理 + 复刻 Blender 英雄机位
        // ★ 用户令"1:1 复刻 Blender 观感"。Blender 出图(scripts/door/shots.py)的环境是:
        //   world 近黑(0.012,0.015,0.021) + 6 盏面光(冷白主光/冷蓝轮廓/侧补/地面反光 + 2 盏小实体灯出金属高光)。
        //   URP 没有实时面光 —— 大面光用**宽角聚光灯**近似(近距大面积 -> 软), 小实体灯用**点光**(锐高光)。
        //   环境光从走廊口径的 0.75 中性白**改回近黑**: 0.75 会把暗底抬成灰白, 正是用户看到的"黑底变白"。
        static float LightGain = 0.50f;      // 小实体灯(点光)的功率标定旋钮
        static float DirGain = 0.085f;        // 大面光(方向光)的照度标定旋钮 —— 见下表 dir 说明
        const float SpotAngle = 70f;         // (备用)聚光锥角

        static void SetupRender()
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.012f, 0.015f, 0.021f);  // = Blender W_Door/W_Wall 的 world
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.fog = false;
            RenderSettings.reflectionIntensity = 1f;

            // 反射源: 仍留一张**很暗**的中性程序化天空盒当环境反射底(Blender 的 world 也是近黑);
            // 亮反射主要来自实时探针捕获的白墙与地台。可见背景由相机清成纯黑(见 Build 末尾 / RefShot)。
            var sh = Shader.Find("Skybox/Procedural");
            if (sh != null)
            {
                string p = MatDir + "/M_Door_TestSky.mat";
                var sky = AssetDatabase.LoadAssetAtPath<Material>(p);
                if (sky == null)
                {
                    sky = new Material(sh) { name = "M_Door_TestSky" };
                    AssetDatabase.CreateAsset(sky, p);
                }
                sky.SetFloat("_SunSize", 0.02f);
                sky.SetFloat("_SunSizeConvergence", 5f);
                sky.SetFloat("_AtmosphereThickness", 0.35f);
                sky.SetColor("_SkyTint", new Color(0.020f, 0.023f, 0.028f));
                sky.SetColor("_GroundColor", new Color(0.008f, 0.010f, 0.013f));
                sky.SetFloat("_Exposure", 1.0f);
                EditorUtility.SetDirty(sky);
                RenderSettings.skybox = sky;
                HstCorridorExtend.Log("环境光 Flat 近黑(与 Blender world 同值); 反射源 = 暗天空盒 + 实时探针");
            }
            else
            {
                RenderSettings.skybox = null;
                HstCorridorExtend.Log("[warn] 找不到 Skybox/Procedural —— 反射只靠实时探针");
            }
        }

        // Blender shots.py 的 6 盏面光, 坐标已按 unity=(-x, z, -y) 换算, 目标点 (0, 2.8, 0)。
        // ★ 关键: Blender 的 4 盏大灯是 **5m 面光** -> 大面积**均匀**照射; URP 的聚光/点光是"点", 只照出一滩光斑
        //   (实测: 面光下白墙整片亮, 聚光下白墙是渐变 -> 这就是"观感不一样"的主因)。
        //   方向光对所有表面**等照度**照射, 是"面光的远场极限" -> 用它逼真大面光的均匀感。
        //   小实体灯(0.45m)本来就是锐高光 -> 保留点光, 出金属的记忆点高光。
        //   dir=true 时: 强度 = P/1000 * DirGain(方向光照度与距离无关);  false: 强度 = P/(4π)*scale*LightGain。
        struct BLight { public string name; public Vector3 pos; public float watts; public Color col; public float size; public float scale; public bool dir; }
        static readonly BLight[] BlenderLights = {
            new BLight{ name="L_key",    pos=new Vector3(  9.5f, 11.0f, 12.0f), watts=1000f, col=new Color(0.84f,0.91f,1.00f), size=5.0f,  scale=1.00f, dir=true  },
            new BLight{ name="L_rim",    pos=new Vector3(-11.0f,  8.0f, -4.0f), watts= 620f, col=new Color(0.40f,0.70f,1.00f), size=4.0f,  scale=1.00f, dir=true  },
            new BLight{ name="L_fill",   pos=new Vector3( -9.0f,  0.4f,  8.5f), watts= 240f, col=new Color(0.56f,0.67f,0.84f), size=4.5f,  scale=1.00f, dir=true  },
            new BLight{ name="L_under",  pos=new Vector3(  0.0f, -1.2f, 10.0f), watts= 130f, col=new Color(0.34f,0.55f,0.86f), size=4.0f,  scale=1.00f, dir=true  },
            new BLight{ name="L_spot_a", pos=new Vector3(  4.0f,  6.2f, 12.0f), watts= 300f, col=new Color(0.88f,0.94f,1.00f), size=0.45f, scale=0.30f, dir=false },
            new BLight{ name="L_spot_b", pos=new Vector3( -4.6f,  1.6f, 11.0f), watts= 190f, col=new Color(0.55f,0.80f,1.00f), size=0.45f, scale=0.30f, dir=false },
        };
        static readonly Vector3 BlenderAim = new Vector3(0f, 2.8f, 0f);   // Blender 里所有面光都指向 (0,0,2.8)

        static void BuildLights()
        {
            foreach (var bl in BlenderLights)
            {
                var go = new GameObject("HST_BDT_" + bl.name);
                go.transform.position = bl.pos;
                go.transform.rotation = Quaternion.LookRotation(BlenderAim - bl.pos, Vector3.up);
                var l = go.AddComponent<Light>();
                if (bl.dir)
                {
                    l.type = LightType.Directional;
                    l.intensity = bl.watts / 1000f * DirGain;
                }
                else
                {
                    l.type = LightType.Point;
                    l.intensity = bl.watts / (4f * Mathf.PI) * bl.scale * LightGain;
                    l.range = 60f;
                }
                l.color = bl.col;
                l.shadows = LightShadows.Soft;
                l.shadowStrength = 0.75f;
            }
            HstCorridorExtend.Log(string.Format(
                "灯: 复刻 Blender 6 面光(4 方向光当大面光 + 2 点光), DirGain={0:0.00} LightGain={1:0.00}, 环境光近黑",
                DirGain, LightGain));
        }

        // ★ 环境反射: 没有天空盒/反射探针时, URP 里 metallic=1 的材质(拉丝钢/中灰金属)几乎是黑的
        //   —— Blender 里它们有大面积柔光(面光)打出的宽高光, 观感差得远。这里补一盏**实时反射探针**,
        //   把白墙+地台+灯的环境反射给金属, 让质感回到 Blender 的量级(不涉及后处理 Volume, 不违反纪律)。
        static GameObject BuildReflection()
        {
            var go = new GameObject("HST_BDT_ReflectionProbe");
            go.transform.position = new Vector3(0f, 3.4f, 0f);
            var rp = go.AddComponent<ReflectionProbe>();
            rp.mode = ReflectionProbeMode.Realtime;
            rp.refreshMode = ReflectionProbeRefreshMode.EveryFrame;
            rp.timeSlicingMode = ReflectionProbeTimeSlicingMode.AllFacesAtOnce;
            rp.resolution = 128;
            rp.center = Vector3.zero;
            rp.size = new Vector3(34f, 14f, 30f);
            rp.nearClipPlane = 0.1f;
            rp.farClipPlane = 150f;
            rp.intensity = 1f;
            rp.boxProjection = false;
            rp.RenderProbe();     // 编辑器里也先渲一次, 出图才反映真实反射
            HstCorridorExtend.Log("环境反射: 实时探针 128px, 盒 34x14x30 @(0,3.4,0)");
            return go;
        }

        // ============================================================ 地台 / 墙
        static void BuildFloor()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);   // 10x10m @scale1, 自带 MeshCollider
            floor.name = "HST_BDT_Floor";
            floor.transform.position = Vector3.zero;
            floor.transform.localScale = new Vector3(4.8f, 1f, 4.8f);      // 48 x 48 m(与 Blender shots.py 地台同尺寸)
            // 地台: ★ 不照抄 Blender 的材质数值 —— Blender 的地台几乎全靠**大面光的宽镜面**提亮(metallic 0.55),
            //   而 URP 的方向光在平面上给不出那种宽镜面(实测: 同编号材质下地台掉到近黑, 与 Blender 差 -0.09)。
            //   所以按"出图观感"标定: 抬高漫反射基色 + 去掉金属度, 让地台读数落在 Blender 的 0.08~0.15 区间。
            var fmat = SimpleMat("M_Door_TestFloor", new Color(0.160f, 0.172f, 0.196f), 0.0f, 0.60f);
            // 地台关掉直接光的镜面高光(实测: 4 盏方向光的介质高光会把整片地台抬亮, 与 Blender 面光下的暗地台不符)
            fmat.SetFloat("_SpecularHighlights", 0f);
            EditorUtility.SetDirty(fmat);
            floor.GetComponent<MeshRenderer>().sharedMaterial = fmat;
            HstCorridorExtend.Log("地台 80x80m(带碰撞)");
        }

        static void BuildWall()
        {
            var root = new GameObject("HST_BDT_Wall");
            Material mat = SimpleMat("M_Door_TestWall", new Color(0.86f, 0.87f, 0.88f), 0f, 0.62f);

            float wSide = (WALL_XOUT - HOLE_HX);                 // 每侧墙宽 4.30
            float cx = (WALL_XOUT + HOLE_HX) * 0.5f;             // 6.85
            Box(root.transform, new Vector3(-cx, WALL_TOP * 0.5f, WALL_ZC),
                new Vector3(wSide, WALL_TOP, WALL_ZT), mat);
            Box(root.transform, new Vector3(cx, WALL_TOP * 0.5f, WALL_ZC),
                new Vector3(wSide, WALL_TOP, WALL_ZT), mat);
            Box(root.transform, new Vector3(0f, (HOLE_Z1 + WALL_TOP) * 0.5f, WALL_ZC),
                new Vector3(HOLE_HX * 2f, WALL_TOP - HOLE_Z1, WALL_ZT), mat);
            HstCorridorExtend.Log(string.Format(
                "墙 18x10x1.6 纯白; 洞口 x±{0:0.00} y0..{1:0.00}(被门框脸盘全遮)", HOLE_HX, HOLE_Z1));
        }

        static void Box(Transform parent, Vector3 center, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "HST_BDT_WallBox";
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        // ============================================================ 门
        static GameObject BuildDoor()
        {
            // ★ 顺序要紧: 先建好 11 个材质资产(Unity 靠**名字**去找它们), 再切导入器让它按名认领。
            BuildDoorMaterials();
            SetupFbxImport();

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            if (prefab == null) { Debug.LogError("[BDT] 找不到爆门 FBX: " + FbxPath); return null; }

            var door = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            door.name = "HST_BlastDoor";
            door.transform.position = Vector3.zero;
            door.transform.rotation = Quaternion.identity;      // 门正面朝 Unity +Z
            door.transform.localScale = Vector3.one;

            // 不再按索引指槽(那是错的) —— 只核对导入器按 FBX 材质索引认领的结果
            VerifyDoorMaterials(door);

            // 八扇... 十六扇门扇是动态件: 关掉所有 Static 标记(防 Batching 把动件钉死)
            var leafPrefix = new[] { "GEO-OL_", "GEO-OD_", "GEO-ID_" };
            int dyn = 0;
            foreach (var t in door.GetComponentsInChildren<Transform>(true))
            {
                bool isLeaf = HstCorridorExtend.StartsWithAny(t.name, leafPrefix);
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, (StaticEditorFlags)0);
                if (isLeaf) dyn++;
            }

            var ctrl = door.AddComponent<TechBlastDoorController>();
            var px = door.AddComponent<TechBlastDoorProximity>();

            // 门速 1.5 m/s(= GIF 的 1.5 倍); 外层走到 50% 内层起步(与冻结规格一致)
            var soc = new SerializedObject(ctrl);
            soc.FindProperty("speedMetersPerSecond").floatValue = DoorSpeed;
            soc.FindProperty("bcdStartFraction").floatValue = 0.5f;
            soc.ApplyModifiedPropertiesWithoutUndo();

            var sop = new SerializedObject(px);
            sop.FindProperty("triggerDepthEachSide").floatValue = TriggerDepth;
            sop.ApplyModifiedPropertiesWithoutUndo();

            // 尺寸门禁: 合并包围盒应 ≈ 10.60 宽(x) × 7.85+ 高(y); 门扇计数应 = 16
            var bb = BoundsOf(door);
            Debug.Log(string.Format("[BDT] 爆门包围盒 size={0} center={1}", bb.size.ToString("0.000"), bb.center.ToString("0.00")));
            if (Mathf.Abs(bb.size.x - 10.92f) > 0.15f || bb.size.y < 7.80f || bb.size.y > 9.60f)
                Debug.LogError("[BDT] 爆门实测尺寸偏离规格(W 应 ≈10.92, H 应 ≈9.41)—— 查 FBX 导入缩放!");
            if (dyn != 16) Debug.LogError("[BDT] 门扇节点数=" + dyn + "(应 16)—— 查 FBX 对象名");

            HstCorridorExtend.Log(string.Format("门进场: 原点站立, 动态门扇 {0}/16, 门速 {1} m/s(比 GIF 快 50%)",
                dyn, DoorSpeed));
            return door;
        }

        static void SetupFbxImport()
        {
            var imp = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
            if (imp == null) { Debug.LogError("[BDT] FBX importer 拿不到: " + FbxPath); return; }
            bool dirty = false;
            // ★ 2026-10-15 关键修正(用户报"黑色底变白"): **不能**按 manifest 的材质列表下标指槽 ——
            //   实测 Unity 的子网格顺序 ≠ Blender 的材质槽顺序(GEO-ID_horiz_in_l 实测 order=[0,2,3,4,1,6],
            //   按三角形数对拍出来的)。按下标指槽会把"近黑凹腔"涂成中灰 —— 正是用户看到的样子。
            //   正解: 让**导入器按 FBX 自己的材质索引认领** —— 用 External+Everywhere 按名字搜到我们
            //   已建好的 M_Door_*.mat(名字与 FBX 里的材质名一致), 子网格↔材质由文件决定, 不经我们的手。
            if (imp.materialImportMode != ModelImporterMaterialImportMode.ImportStandard)
            { imp.materialImportMode = ModelImporterMaterialImportMode.ImportStandard; dirty = true; }
            if (imp.materialLocation != ModelImporterMaterialLocation.External)
            { imp.materialLocation = ModelImporterMaterialLocation.External; dirty = true; }
            if (imp.materialName != ModelImporterMaterialName.BasedOnMaterialName)
            { imp.materialName = ModelImporterMaterialName.BasedOnMaterialName; dirty = true; }
            if (imp.materialSearch != ModelImporterMaterialSearch.Everywhere)
            { imp.materialSearch = ModelImporterMaterialSearch.Everywhere; dirty = true; }
            if (imp.importCameras) { imp.importCameras = false; dirty = true; }
            if (imp.importLights) { imp.importLights = false; dirty = true; }
            if (imp.importBlendShapes) { imp.importBlendShapes = false; dirty = true; }
            if (dirty) { imp.SaveAndReimport(); HstCorridorExtend.Log("FBX 导入设置: 材质按名认领(ImportStandard+External+Everywhere), 已重导入"); }
        }

        // ---- 贴图导入: ★ 基色图是"线性字节", 必须关 sRGB, 否则整体偏暗(Color Space 踩坑) ----
        static Texture2D Tex(string file, bool normal)
        {
            string p = TexDir + "/" + file;
            var ti = AssetImporter.GetAtPath(p) as TextureImporter;
            if (ti == null) { Debug.LogError("[BDT] 缺贴图: " + p); return null; }
            bool dirty = false;
            var want = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (ti.textureType != want) { ti.textureType = want; dirty = true; }
            if (ti.sRGBTexture) { ti.sRGBTexture = false; dirty = true; }
            if (ti.wrapMode != TextureWrapMode.Repeat) { ti.wrapMode = TextureWrapMode.Repeat; dirty = true; }
            if (!ti.mipmapEnabled) { ti.mipmapEnabled = true; dirty = true; }
            if (dirty) ti.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(p);
        }

        [System.Serializable] class ManObj { public string name; public string[] materials; }
        [System.Serializable] class ManMat
        {
            public string material; public float[] base_color; public float metallic;
            public float roughness; public string bc_texture; public string normal_texture;
            public float emission_strength;
        }
        [System.Serializable] class ManFile { public List<ManObj> objects; public List<ManMat> unity_materials; }

        static ManFile LoadManifest()
        {
            string abs = Application.dataPath + ManPath.Substring("Assets".Length);
            if (!File.Exists(abs)) { Debug.LogError("[BDT] 找不到 manifest: " + abs); return null; }
            return JsonUtility.FromJson<ManFile>(File.ReadAllText(abs));
        }

        static void BuildDoorMaterials()
        {
            if (!AssetDatabase.IsValidFolder(MatDir))
                AssetDatabase.CreateFolder("Assets/Art/Environment/HST_Kit/TechBlastDoor", "Materials");

            var man = LoadManifest();
            if (man == null || man.unity_materials == null) { Debug.LogError("[BDT] manifest 里没有 unity_materials"); return; }

            Texture2D nrm = Tex("T_Door_Panel_N.png", true);
            foreach (var mm in man.unity_materials)
            {
                string path = MatDir + "/" + mm.material + ".mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = mm.material };
                    AssetDatabase.CreateAsset(mat, path);
                }
                else if (mat.shader == null || mat.shader.name != "Universal Render Pipeline/Lit")
                {
                    mat.shader = Shader.Find("Universal Render Pipeline/Lit");
                }

                Texture2D bc = string.IsNullOrEmpty(mm.bc_texture) ? null : Tex(mm.bc_texture, false);
                Color baseCol = (mm.base_color != null && mm.base_color.Length >= 3)
                    ? new Color(mm.base_color[0], mm.base_color[1], mm.base_color[2], 1f) : Color.white;

                // 有基色图时贴图里已烘了基色 -> _BaseColor 用白, 免得双重变暗
                mat.SetColor("_BaseColor", bc != null ? Color.white : baseCol);
                mat.SetFloat("_Metallic", mm.metallic);
                mat.SetFloat("_Smoothness", Mathf.Clamp01(1f - mm.roughness));

                if (bc != null) mat.SetTexture("_BaseMap", bc);
                if (nrm != null && !string.IsNullOrEmpty(mm.normal_texture))
                {
                    mat.SetTexture("_BumpMap", nrm);
                    mat.SetFloat("_BumpScale", 1f);
                    mat.EnableKeyword("_NORMALMAP");
                }
                else mat.DisableKeyword("_NORMALMAP");

                // 发光件: ★ 基色 ≤1.0, 亮度靠 **Emission Intensity**(= 乘进 _EmissionColor 的倍数)。
                //   Blender 侧 emission_strength 青 5.0 / 白 4.5(IMPORT.md §5 同值); 只填基色会让 LED 暗 5 倍。
                if (mm.emission_strength > 0.001f)
                {
                    mat.EnableKeyword("_EMISSION");
                    mat.SetColor("_EmissionColor", baseCol * Mathf.Max(1f, mm.emission_strength));
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                }
                else mat.DisableKeyword("_EMISSION");

                EditorUtility.SetDirty(mat);
            }
            AssetDatabase.SaveAssets();
            HstCorridorExtend.Log("门材质 " + man.unity_materials.Count + " 槽(基色图/法线图已挂, 基色图关 sRGB)");
        }

        // 核对导入器按 FBX 材质索引认领的结果: 每个渲染器的材质都必须是"本目录里我们建的那 11 个之一",
        // 且必须在该对象 manifest 声明的材质集合里(集合比对, **不比对顺序** —— 顺序由 FBX 决定)。
        static void VerifyDoorMaterials(GameObject door)
        {
            var man = LoadManifest();
            if (man == null || man.objects == null) { Debug.LogError("[BDT] manifest 解析失败"); return; }
            var byObj = new Dictionary<string, HashSet<string>>();
            foreach (var o in man.objects)
                byObj[o.name] = new HashSet<string>(o.materials ?? new string[0]);

            int ok = 0, bad = 0, skipped = 0;
            var msgs = new List<string>();
            foreach (var mf in door.GetComponentsInChildren<MeshFilter>(true))
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null) { skipped++; continue; }
                string nm = mf.gameObject.name;
                if (!byObj.TryGetValue(nm, out var want)) { skipped++; continue; }
                bool good = mr.sharedMaterials.Length > 0;
                foreach (var m in mr.sharedMaterials)
                {
                    if (m == null) { good = false; msgs.Add(nm + ": 有 null 槽"); continue; }
                    string p = AssetDatabase.GetAssetPath(m);
                    if (string.IsNullOrEmpty(p) || !p.StartsWith(MatDir))
                    { good = false; msgs.Add(nm + " -> " + m.name + " (不是本目录材质: " + p + ")"); }
                    else if (!want.Contains(m.name))
                    { good = false; msgs.Add(nm + " -> " + m.name + " (不在 manifest 声明里)"); }
                }
                if (good) ok++; else bad++;
            }
            for (int i = 0; i < msgs.Count && i < 12; i++) Debug.LogError("[BDTMATCHK] " + msgs[i]);
            HstCorridorExtend.Log(string.Format("门材质核对: 通过 {0} / 异常 {1} / 未比对 {2}", ok, bad, skipped));
            if (bad > 0) Debug.LogError("[BDTMATCHK] 有 " + bad + " 个渲染器材质不对");
        }

        // ============================================================ 材质自检
        // 风险模式: 有基色图时我把 _BaseColor 设成白(颜色全交给图)。**若 _BaseMap 没挂上, 该材质就会渲染成纯白** ——
        // 这正是用户说的"黑色底变白"的样子。所以这里逐材质转储 _BaseMap / _BaseColor / 槽数与子网格数。
        [MenuItem("HST/爆门试验场 材质自检(转储槽+贴图, 标出会渲染成白的材质)")]
        public static void DumpMaterials()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var door = GameObject.Find("HST_BlastDoor");
            if (door == null) { Debug.LogError("[BDTMAT] 找不到 HST_BlastDoor"); return; }

            var seen = new HashSet<string>();
            var whiteRisk = new List<string>();
            int subMismatch = 0;
            foreach (var mf in door.GetComponentsInChildren<MeshFilter>(true))
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null) continue;
                int sub = mf.sharedMesh != null ? mf.sharedMesh.subMeshCount : -1;
                var mats = mr.sharedMaterials;
                if (sub != mats.Length)
                {
                    subMismatch++;
                    if (subMismatch <= 8)
                        Debug.LogError(string.Format("[BDTMAT] 槽/子网格数不符 {0}: submesh={1} mats={2}[{3}]",
                            mf.gameObject.name, sub, mats.Length, string.Join(",", System.Array.ConvertAll(mats, m => m == null ? "<null>" : m.name))));
                }
                foreach (var m in mats)
                {
                    if (m == null) continue;
                    if (!seen.Add(m.name)) continue;
                    var bm = m.GetTexture("_BaseMap");
                    Color bc = m.GetColor("_BaseColor");
                    Debug.Log(string.Format("[BDTMAT] {0}: baseMap={1} baseColor=({2:F3},{3:F3},{4:F3}) metal={5:F2} smooth={6:F2}",
                        m.name, bm != null ? bm.name : "<NULL>", bc.r, bc.g, bc.b,
                        m.GetFloat("_Metallic"), m.GetFloat("_Smoothness")));
                    if (bm == null && bc.r > 0.9f && bc.g > 0.9f && bc.b > 0.9f)
                        whiteRisk.Add(m.name);
                }
            }
            Debug.Log("[BDTMAT] 材质总数 " + seen.Count + "; 槽/子网格数不符的对象 " + subMismatch + " 个");
            Debug.Log(whiteRisk.Count == 0
                ? "[BDTMAT] OK 没有任何材质会因缺基色图而渲染成纯白"
                : "[BDTMAT] 失败 会渲染成纯白的材质: " + string.Join(",", whiteRisk));
        }

        // 逐子网格三角形数 —— 与 FBX 侧"逐材质槽三角形数"对拍, 就唯一确定 Unity 的子网格↔材质映射
        [MenuItem("HST/爆门试验场 探测器: 子网格三角形数 vs 材质")]
        public static void ProbeSubmeshes()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var door = GameObject.Find("HST_BlastDoor");
            if (door == null) { Debug.LogError("[BDTSUB] 找不到门"); return; }
            foreach (var mf in door.GetComponentsInChildren<MeshFilter>(true))
            {
                string n = mf.gameObject.name;
                if (n != "GEO-ID_horiz_in_l" && n != "GEO-OL_in_l" && n != "GEO-ID_vert_up_in"
                    && n != "GEO-OL_in_r" && n != "GEO-OL_out_l" && n != "GEO-OL_out_r") continue;
                var me = mf.sharedMesh; var mr = mf.GetComponent<MeshRenderer>();
                Debug.Log(string.Format("[BDTSUB] {0}: submeshes={1} mats={2}", n, me.subMeshCount, mr.sharedMaterials.Length));
                for (int s = 0; s < me.subMeshCount; s++)
                    Debug.Log(string.Format("[BDTSUB]    sub{0} tris={1} mat={2}", s,
                        me.GetTriangles(s).Length / 3,
                        s < mr.sharedMaterials.Length && mr.sharedMaterials[s] != null ? mr.sharedMaterials[s].name : "<null>"));
            }
        }

        // ============================================================ 逻辑自检(编辑器内, 不进 Play)
        // ★ 实测: 无头 batchmode 里进 Play 会挂在图形初始化(日志停在 Created GICache, 不返回)——
        //   所以自检**手动驱动**控制器, 走的仍是真代码路径:
        //     SendMessage("Awake") → 真做名字绑定 + 算时间线; SetProgress(1) → 真按位移表摆门扇;
        //     再逐扇量实际位移 vs 位移表。场景**不存盘**(只在内存里摆完又复位)。
        static readonly string[] LeafArrays =
        { "outerDecorNearLeaves", "horizontalDoorLeaves", "verticalDoorLeaves", "outerDecorFarLeaves" };

        [MenuItem("HST/爆门试验场 逻辑自检(绑定+时长+位移, 不进 Play)")]
        public static void BindCheck()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var door = GameObject.Find("HST_BlastDoor");
            if (door == null) { Debug.LogError("[BDTCHK] 场景里找不到 HST_BlastDoor"); return; }
            var ctrl = door.GetComponent<TechBlastDoorController>();
            if (ctrl == null) { Debug.LogError("[BDTCHK] 门根没有 TechBlastDoorController"); return; }

            ctrl.SendMessage("Awake");     // 编辑器里 Awake 不会自动跑 —— 手动跑一次

            var so = new SerializedObject(ctrl);
            int bound = 0, total = 0;
            var miss = new List<string>();
            foreach (var an in LeafArrays)
            {
                var p = so.FindProperty(an);
                for (int i = 0; i < p.arraySize; i++)
                {
                    var e = p.GetArrayElementAtIndex(i);
                    total++;
                    if (e.FindPropertyRelative("leaf").objectReferenceValue as Transform != null) bound++;
                    else miss.Add(e.FindPropertyRelative("doorObjectName").stringValue);
                }
            }
            var p0 = new Dictionary<string, Vector3>();
            foreach (var t in door.GetComponentsInChildren<Transform>(true))
                if (!p0.ContainsKey(t.name)) p0.Add(t.name, t.position);
            Debug.Log(string.Format("[BDTCHK] 门扇绑定 {0}/{1}; 未绑定=[{2}]; OpenDuration={3:F3}s (门速 1.5)",
                bound, total, string.Join(",", miss), ctrl.OpenDuration));

            ctrl.SetProgress(1f);          // 全开
            int ok = 0;
            var bad = new List<string>();
            foreach (var an in LeafArrays)
            {
                var p = so.FindProperty(an);
                for (int i = 0; i < p.arraySize; i++)
                {
                    var e = p.GetArrayElementAtIndex(i);
                    string nm = e.FindPropertyRelative("doorObjectName").stringValue;
                    Vector3 off = e.FindPropertyRelative("openOffset").vector3Value;
                    var t = FindDeep(door.transform, nm);
                    if (t == null || !p0.ContainsKey(nm)) { bad.Add(nm + "(找不到)"); continue; }
                    float err = Vector3.Distance(t.position, p0[nm] + door.transform.TransformVector(off)) * 1000f;
                    if (err <= 1f) ok++;
                    else bad.Add(string.Format("{0}(差{1:F2}mm)", nm, err));
                }
            }
            ctrl.SetProgress(0f);          // 复位(不存盘)

            Debug.Log(string.Format("[BDTCHK] 位移核对 {0}/{1} 到位; 异常=[{2}]", ok, total, string.Join(",", bad)));
            Debug.Log(bound == 16 && ok == 16 && bad.Count == 0 ? "[BDTCHK] OK 全部通过" : "[BDTCHK] 失败");
        }

        // ============================================================ 无头 Play 自检
        // 目的: 在 GUI 里真跑一遍 Play —— 用户手点 Play 就是最直接的验收, 本菜单是自动化版。
        // ⚠ 只在**带窗口**的编辑器里用; 无头 batchmode 进 Play 会挂在图形初始化(实测), 趟数用 BindCheck。
        static bool _playInit;
        static float _t0;
        static Dictionary<string, Vector3> _p0;
        static TechBlastDoorController _ctrl;
        static GameObject _door;

        [MenuItem("HST/爆门试验场 Play 自检(仅带窗口的编辑器; 无头会挂)")]
        public static void RunPlayTest()
        {
            if (!File.Exists(ScenePath)) { Debug.LogError("[BDTPLAY] 缺场景 " + ScenePath); EditorApplication.Exit(1); return; }
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            EditorApplication.playModeStateChanged -= OnPms;
            EditorApplication.playModeStateChanged += OnPms;
            EditorApplication.EnterPlaymode();
        }

        static void OnPms(PlayModeStateChange s)
        {
            if (s != PlayModeStateChange.EnteredPlayMode) return;
            EditorApplication.playModeStateChanged -= OnPms;
            _playInit = false;
            EditorApplication.update -= PlayTick;
            EditorApplication.update += PlayTick;
            Debug.Log("[BDTPLAY] EnteredPlayMode");
        }

        static void PlayTick()
        {
            float el = Time.realtimeSinceStartup - _t0;
            if (!_playInit)
            {
                _playInit = true;
                _t0 = Time.realtimeSinceStartup;
                _door = GameObject.Find("HST_BlastDoor");
                if (_door == null) { Done(1, "Play 里找不到 HST_BlastDoor"); return; }
                _ctrl = _door.GetComponent<TechBlastDoorController>();
                if (_ctrl == null) { Done(1, "门根没有 TechBlastDoorController"); return; }

                var so = new SerializedObject(_ctrl);
                int bound = 0, total = 0;
                var miss = new List<string>();
                foreach (var an in LeafArrays)
                {
                    var p = so.FindProperty(an);
                    for (int i = 0; i < p.arraySize; i++)
                    {
                        var elm = p.GetArrayElementAtIndex(i);
                        total++;
                        if (elm.FindPropertyRelative("leaf").objectReferenceValue as Transform != null) bound++;
                        else miss.Add(elm.FindPropertyRelative("doorObjectName").stringValue);
                    }
                }
                _p0 = new Dictionary<string, Vector3>();
                foreach (var t in _door.GetComponentsInChildren<Transform>(true))
                    if (!_p0.ContainsKey(t.name)) _p0.Add(t.name, t.position);
                Debug.Log(string.Format("[BDTPLAY] 门扇绑定 {0}/{1}; 未绑定=[{2}]", bound, total, string.Join(",", miss)));
                Debug.Log(string.Format("[BDTPLAY] 开门全程时长 OpenDuration={0:F3}s (门速 1.5 m/s)", _ctrl.OpenDuration));
                _ctrl.Open();
                return;
            }

            if (el > 15f) { Done(1, "超时: 15s 内没开完, Progress=" + _ctrl.Progress.ToString("F3")); return; }
            if (_ctrl.Progress < 0.999f) return;

            // 到全开: 逐扇核对实际位移 vs 位移表
            var so2 = new SerializedObject(_ctrl);
            int ok = 0; var bad = new List<string>();
            foreach (var an in LeafArrays)
            {
                var p = so2.FindProperty(an);
                for (int i = 0; i < p.arraySize; i++)
                {
                    var elm = p.GetArrayElementAtIndex(i);
                    string nm = elm.FindPropertyRelative("doorObjectName").stringValue;
                    Vector3 off = elm.FindPropertyRelative("openOffset").vector3Value;
                    var t = FindDeep(_door.transform, nm);
                    if (t == null || !_p0.ContainsKey(nm)) { bad.Add(nm + "(找不到)"); continue; }
                    Vector3 want = _p0[nm] + _door.transform.TransformVector(off);
                    float err = Vector3.Distance(t.position, want) * 1000f;
                    if (err <= 1f) ok++;
                    else bad.Add(string.Format("{0}(差{1:F2}mm)", nm, err));
                }
            }
            Done(bad.Count == 0 && ok == 16 ? 0 : 1,
                 string.Format("全开耗时 {0:F2}s; 位移核对 {1}/16 到位; 异常=[{2}]", el, ok, string.Join(",", bad)));
        }

        static void Done(int code, string msg)
        {
            EditorApplication.update -= PlayTick;
            if (code == 0) Debug.Log("[BDTPLAY] OK " + msg);
            else Debug.LogError("[BDTPLAY] 失败 " + msg);
            EditorApplication.delayCall += () => { EditorApplication.isPlaying = false; EditorApplication.Exit(code); };
        }

        // ============================================================ 出图(闭/半开/全开)
        // 编排: 场景不存盘(只在内存里摆门扇), 出 PNG 到系统临时目录 —— 不往仓库里丢图。
        [MenuItem("HST/爆门试验场 出图(正/半开/全开 + 3/4)")]
        public static void ShotOnce()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var door = GameObject.Find("HST_BlastDoor");
            if (door == null) { Debug.LogError("[BDT] 场景里找不到 HST_BlastDoor"); return; }
            var probe = Object.FindObjectOfType<ReflectionProbe>();
            if (probe != null) probe.RenderProbe();      // 出图前先把反射探针渲一遍

            // 关掉场景自带相机, 免得 URP 每帧多渲一次
            foreach (var c in Object.FindObjectsOfType<Camera>(true)) c.gameObject.SetActive(false);

            string dir = Path.Combine(Path.GetTempPath(), "hst_blastdoor_shots");
            Directory.CreateDirectory(dir);

            var camGO = new GameObject("BDT_ShotCam");
            var cam = camGO.AddComponent<Camera>();
            cam.fieldOfView = 38f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 300f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = BackdropDark;
            camGO.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = false;
            // 与场景 Main Camera 同口径: 压掉 TA 高度蓝雾(否则出图是"成品观感", 跟用户在场景里看到的不是一回事)
            var sfog = camGO.AddComponent<FogCameraOverride>(); sfog.density = 0f; sfog.maxOpacity = 0f;
            Look(cam, new Vector3(0f, 4.0f, 28f), new Vector3(0f, 3.6f, 0f));

            var closed = CaptureClosed(door);
            SetPose(door, closed, new[] { 0f, 0f, 0f, 0f });
            Snap(cam, Path.Combine(dir, "front_closed.png"));
            SetPose(door, closed, new[] { 0.5f, 0.5f, 0.5f, 0.5f });
            Snap(cam, Path.Combine(dir, "front_half.png"));
            SetPose(door, closed, new[] { 1f, 1f, 1f, 1f });
            Snap(cam, Path.Combine(dir, "front_open.png"));

            Look(cam, new Vector3(-11f, 5.2f, 15f), new Vector3(0f, 3.2f, 0f));
            Snap(cam, Path.Combine(dir, "tq_open.png"));

            // ★ 特写(只开外层装饰门 OL/OD, 内层两道真门留在关闭位) —— 直接看"内门是不是黑的"
            cam.fieldOfView = 34f;
            Look(cam, new Vector3(0f, 3.3f, 12.5f), new Vector3(0f, 3.3f, 0f));
            SetPose(door, closed, new[] { 0f, 0f, 0f, 0f });
            Snap(cam, Path.Combine(dir, "close_front_closed.png"));
            SetPose(door, closed, new[] { 1f, 0f, 0f, 1f });
            Snap(cam, Path.Combine(dir, "close_inner.png"));

            SetPose(door, closed, new[] { 0f, 0f, 0f, 0f });
            Object.DestroyImmediate(camGO);
            HstCorridorExtend.Log("出图 4 张 → " + dir + " (场景未存盘)");
        }

        // ============================================================ 同机位参考出图(与 Blender 逐帧对拍)
        // ★ 目的: 之前两边图不能比 —— Blender 用 SHOTS 机位(50mm/1600x900), Unity 用随手摆的机位。
        //   这里把 Blender 的验收机位**换算进 Unity 坐标**(unity = (-x, z, -y)), 并用同一焦距/画幅,
        //   于是两张图可以直接叠差。出图同时打印与 Blender shots.py 同口径的亮度统计(线性亮度)。
        static Vector3 U(float x, float y, float z) => new Vector3(-x, z, -y);   // Blender 点 -> Unity 点

        // Blender: sensor 36mm(长边) + 16:9 -> 竖直 FOV = 2*atan(18/(lens*aspect))
        static float VFov(float lens, float aspect) =>
            2f * Mathf.Atan(18f / (lens * aspect)) * Mathf.Rad2Deg;

        [MenuItem("HST/爆门试验场 同机位参考出图(对齐 Blender SHOTS, 1600x900)")]
        public static void RefShot() { RefShotWithSuffix("", ""); }

        // ★ 干净渲染器: project-0 的 URP-Renderer 带 TA 的 9 个 feature(雾×2/NPR 光照/延迟描边/性能Bloom/
        //   GTAO/SSR/体积光/红移)。它们对**每台相机**生效, 于是"Unity 出图"其实是**TA 管线成品**,
        //   和 Blender 的零后处理直出没法直接比。本菜单临时把 features 全关(内存里开关, 不存盘),
        //   出 `*_clean.png`, 用来回答"差异里有多少是资产、多少是管线"。
        [MenuItem("HST/爆门试验场 同机位参考出图(干净渲染器: 临时关掉 TA 的 Renderer Features)")]
        public static void RefShotClean() { RefShotWithSuffix("_clean", "all"); }

        // 只关"雾"这两条 feature —— 用来把 TA 管线的影响拆成"雾"与"其它(NPR/描边/Bloom)"
        [MenuItem("HST/爆门试验场 同机位参考出图(只关雾 Renderer Feature)")]
        public static void RefShotNoFog() { RefShotWithSuffix("_nofog", "fog"); }

        static void RefShotWithSuffix(string suffix, string featureFilter)
        {
            var saved = new List<KeyValuePair<ScriptableRendererFeature, bool>>();
            if (!string.IsNullOrEmpty(featureFilter))
            {
                var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/URP-Renderer.asset");
                if (rd == null) { Debug.LogError("[BDTREF] 找不到 URP-Renderer.asset"); return; }
                foreach (var f in rd.rendererFeatures)
                {
                    if (f == null) continue;
                    if (featureFilter == "fog" && !f.GetType().Name.Contains("Fog")) continue;
                    saved.Add(new KeyValuePair<ScriptableRendererFeature, bool>(f, f.isActive));
                    f.SetActive(false);
                }
                HstCorridorExtend.Log("临时关闭 " + saved.Count + " 个 Renderer Feature(筛选='" + featureFilter + "', 内存开关不存盘)");
            }
            try { RefShotCore(suffix); }
            finally
            {
                foreach (var kv in saved) if (kv.Key != null) kv.Key.SetActive(kv.Value);
            }
        }

        static void RefShotCore(string suffix)
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var door = GameObject.Find("HST_BlastDoor");
            if (door == null) { Debug.LogError("[BDTREF] 场景里找不到 HST_BlastDoor"); return; }
            var probe = Object.FindObjectOfType<ReflectionProbe>();
            if (probe != null) probe.RenderProbe();

            foreach (var c in Object.FindObjectsOfType<Camera>(true)) c.gameObject.SetActive(false);

            // 玩家/相机 rig 挡在门前 -> 对拍时藏起来(与 Blender 场景一致: 只有门+地台+可选墙)
            var hidden = new List<GameObject>();
            foreach (var t in Object.FindObjectsOfType<Transform>(true))
            {
                var go = t.gameObject;
                if (go.name.StartsWith("HST_BDT_ShotCam")) continue;
                bool isPlayer = go.CompareTag("Player") || go.name.StartsWith("Player")
                    || go.name.StartsWith("Main Camera") || go.name.StartsWith("Virtual Camera");
                if (isPlayer && go.activeSelf) { go.SetActive(false); hidden.Add(go); }
            }
            var wall = GameObject.Find("HST_BDT_Wall");
            var floor = GameObject.Find("HST_BDT_Floor");

            string dir = Path.Combine(Path.GetTempPath(), "hst_blastdoor_shots");
            Directory.CreateDirectory(dir);

            var camGO = new GameObject("HST_BDT_ShotCam_Ref");
            var cam = camGO.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f; cam.farClipPlane = 300f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = BackdropDark;   // 与 Blender 出图同底
            camGO.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = false;

            RenderRefShots(cam, probe, suffix, dir);

            if (wall != null) wall.SetActive(true);
            if (floor != null) floor.SetActive(true);
            foreach (var go in hidden) if (go != null) go.SetActive(true);
            Object.DestroyImmediate(camGO);
            HstCorridorExtend.Log("同机位参考图 5 张 -> " + dir + " (1600x900, 与 Blender SHOTS 对齐, 后缀'" + suffix + "')");
        }

        // (名字, Blender 机位 from, look_at, 焦距, 是否带墙) —— 与 door_spec.SHOTS / shots.py 一一对应
        static readonly object[][] RefShots = {
            new object[] { "front",       new Vector3(0f,-26f,3.20f),   new Vector3(0f,0f,3.10f),   50f, false },
            new object[] { "front_wall",  new Vector3(0f,-26f,3.20f),   new Vector3(0f,0f,3.10f),   50f, true  },
            new object[] { "front_close", new Vector3(0f,-15f,3.00f),   new Vector3(0f,0.2f,3.00f), 50f, false },
            new object[] { "three_q",     new Vector3(-13.8f,-18f,8.6f),new Vector3(0f,0f,3.00f),   45f, false },
            new object[] { "side",        new Vector3(20f,0f,4.00f),    new Vector3(0f,0f,3.60f),   50f, false },
        };

        static void RenderRefShots(Camera cam, ReflectionProbe probe, string suffix, string dir)
        {
            var wall = GameObject.Find("HST_BDT_Wall");
            var floor = GameObject.Find("HST_BDT_Floor");
            var door = GameObject.Find("HST_BlastDoor");
            var closed = CaptureClosed(door);
            SetPose(door, closed, new[] { 0f, 0f, 0f, 0f });     // 关态(与 closed_*.png 对应)
            foreach (var s in RefShots)
            {
                string name = (string)s[0];
                if (wall != null) wall.SetActive((bool)s[4]);
                if (floor != null) floor.SetActive(true);
                // ★ 每张图重捕反射探针 —— 否则"无墙"那几张里, 光滑金属反射的仍是"含白墙"的探针内容,
                //   门上会凭空多出大块高光斑(与 Blender 无墙场景对不上)。
                if (probe != null && probe.gameObject.activeInHierarchy) probe.RenderProbe();
                cam.aspect = 1600f / 900f;
                cam.fieldOfView = VFov((float)s[3], cam.aspect);
                Look(cam, U(((Vector3)s[1]).x, ((Vector3)s[1]).y, ((Vector3)s[1]).z),
                         U(((Vector3)s[2]).x, ((Vector3)s[2]).y, ((Vector3)s[2]).z));
                Snap(cam, 1600, 900, Path.Combine(dir, "ref_" + name + suffix + ".png"));
            }
        }

        static void Look(Camera cam, Vector3 pos, Vector3 target)
        {
            cam.transform.position = pos;
            cam.transform.rotation = Quaternion.LookRotation(target - pos, Vector3.up);
        }

        /// <summary>抓十六扇门的关闭位(= FBX 实例化默认位姿), 之后所有摆位都从它推。</summary>
        static Dictionary<string, Vector3> CaptureClosed(GameObject door)
        {
            var d = new Dictionary<string, Vector3>();
            foreach (var t in door.GetComponentsInChildren<Transform>(true))
                if (!d.ContainsKey(t.name)) d.Add(t.name, t.position);
            return d;
        }

        static void SetPose(GameObject door, Dictionary<string, Vector3> closed, float[] fr)
        {
            var ctrl = door.GetComponent<TechBlastDoorController>();
            var so = new SerializedObject(ctrl);
            for (int a = 0; a < LeafArrays.Length; a++)
            {
                float frac = (fr != null && a < fr.Length) ? fr[a] : 0f;
                var prop = so.FindProperty(LeafArrays[a]);
                if (prop == null || !prop.isArray) continue;
                for (int i = 0; i < prop.arraySize; i++)
                {
                    var el = prop.GetArrayElementAtIndex(i);
                    string nm = el.FindPropertyRelative("doorObjectName").stringValue;
                    Vector3 off = el.FindPropertyRelative("openOffset").vector3Value;
                    var t = FindDeep(door.transform, nm);
                    if (t == null || !closed.ContainsKey(nm)) continue;
                    t.position = closed[nm] + door.transform.TransformVector(off * frac);
                }
            }
        }

        static Transform FindDeep(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            return null;
        }

        static void Snap(Camera cam, string file) { Snap(cam, 1000, 640, file); }

        // ★ 实测坑(2026-10-15): `new RenderTexture(W,H,24,ARGB32)` 走 RenderTextureReadWrite.Default,
        //   在 **Linear 色彩空间**工程里拿到的是**线性缓冲**; 而 `EncodeToPNG` 只写原始字节 ->
        //   出来的 PNG 是"把线性值当 sRGB 存"的图, 显示与对拍都**整体偏暗**(背景实测 0.015 而真值应 0.109)。
        //   修法: 用 linear:true 的贴图取原始线性值, 手工做 linear->sRGB, 再编码。
        static void Snap(Camera cam, int W, int H, string file)
        {
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt; cam.aspect = (float)W / H; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false, true);   // mipChain=false, linear=true
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            RenderTexture.active = null;
            var px = tex.GetPixels(0);
            for (int i = 0; i < px.Length; i++)
            {
                px[i].r = Mathf.LinearToGammaSpace(px[i].r);
                px[i].g = Mathf.LinearToGammaSpace(px[i].g);
                px[i].b = Mathf.LinearToGammaSpace(px[i].b);
            }
            tex.SetPixels(px, 0); tex.Apply();
            byte[] bytes = tex.EncodeToPNG();
            File.WriteAllBytes(file, bytes);
            cam.targetTexture = null; rt.Release(); Object.DestroyImmediate(rt); Object.DestroyImmediate(tex);
            Debug.Log("[BDT] shot " + Path.GetFileName(file) + " bytes=" + bytes.Length);
        }

        // ============================================================ 通用
        static Material SimpleMat(string name, Color c, float metal, float rough)
        {
            string path = MatDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Metallic", metal);
            m.SetFloat("_Smoothness", Mathf.Clamp01(1f - rough));
            EditorUtility.SetDirty(m);
            return m;
        }

        static Bounds BoundsOf(GameObject root)
        {
            var rends = root.GetComponentsInChildren<MeshRenderer>();
            if (rends.Length == 0) return new Bounds(root.transform.position, Vector3.zero);
            var b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            return b;
        }
    }
}
#endif
