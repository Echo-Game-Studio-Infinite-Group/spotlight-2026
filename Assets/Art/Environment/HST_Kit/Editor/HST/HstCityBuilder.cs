#if UNITY_EDITOR
// HST 城市楼房场景构建器 (2026-10-10)
// 用途: 把 Blender 侧 build_city.py 导出的 SM_City_Rooftops_v1.fbx + T_BLD/T_HS 贴图
//       装成 Urban 场景(SC_City_Rooftops): 接地布局 + 天际线 + URP Lit 材质接线 +
//       逐构件尺寸验收(对 manifest_city.json) + headless 出图。
// 用法(无头): Unity.exe -batchmode -quit -projectPath <p0> -executeMethod HST.HstCityBuilder.BuildCity
// 视觉验证: 日景 4 机位 + 暮色 2 机位(材质/光照临时换装) + NPR 变体 1 机位(非破坏)。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HST
{
    public static class HstCityBuilder
    {
        const string FBX = "Assets/Art/Environment/HST_Kit/Models/HST_Prags/SM_City_Rooftops_v1.fbx";
        const string HUMAN_FBX = "Assets/Art/Environment/HST_Kit/Models/HST_Prags/SM_Human_Scale175_v1.fbx";
        const string TXT = "Assets/Art/Environment/HST_Kit/Textures/HST_Prags";
        const string MAT = "Assets/Art/Environment/HST_Kit/Materials/HST_Prags";
        const string SCENE = "Assets/Art/Environment/HST_Kit/Scenes/SC_City_Rooftops.unity";
        const string MANIFEST = "Assets/Art/Environment/HST_Kit/Manifests/HST_Prags/manifests/manifest_city.json";
        const string SPEC_JSON = "Assets/Art/Environment/HST_Kit/Manifests/HST_Prags/manifests/spec_bld.json";
        const string OUT = @"F:/VSCode/工业级渲染/renders/city_v1";
        const float SIZE_TOL = 0.005f;   // 5mm

        // 假人标尺站在主楼街门旁(Unity 系)
        static readonly Vector3 HUMAN_POS = new Vector3(-1.8f, 0f, 8.6f);

        // ---- 轴向(实测, 别照抄"Blender -Y 正面 → Unity +Z 正面"的经验说法) ----
        // 本工程 FBX 往返的映射是 **Unity(x, y, z) = Blender(x, z, y)** —— 即 **Blender +Y → Unity +Z**。
        // 判据(实测): 假人脚尖在 Blender -Y(脚长 0.2625 里脚尖占 72%), 进 Unity 后 mesh 局部
        // z ∈ [-0.00189, +0.00123] = Blender y ∈ [-0.189, +0.123] **同号** → 是 +Y 对 +Z。
        // ⇒ kit 的临街面(Blender -Y, 按本工作区"正面朝 -Y"约定做的)在 Unity 里朝 **-Z**;
        //   要让它对着 +z 的机位群, **摆放 yaw 必须加 180°**。漏了这一条的表现是"门/雨棚/假人
        //   全都在楼背面"(实测: 街景里正面墙干净得像没做门口, 极容易误判成几何没导出去)。
        const float STREET_YAW = 180f;

        // ---- 布局(Unity 系; 含 STREET_YAW 修正) ----
        // name: (pos, rotY)
        static readonly (string name, Vector3 pos, float yaw)[] LAYOUT = {
            ("SM_BLD_TowerA_v1", new Vector3(0, 0, 0), STREET_YAW),
            ("SM_BLD_SlabB_v1", new Vector3(44, 0, 30), 90 + STREET_YAW),
            ("SM_BLD_CylC_v1", new Vector3(-36, 0, -22), STREET_YAW),
            ("SM_BLD_StepD_v1", new Vector3(36, 0, -30), STREET_YAW),
            // 商铺裙楼摆在主楼**临街侧(+z = 机位侧)** —— 橱窗与主楼雨棚同朝一面;
            // 摆在楼后(-z)的话街景机位根本看不到"首层商铺"这件事(本轮验收要它可见)
            ("SM_BLD_Podium_v1", new Vector3(-20, 0, 24), STREET_YAW),
            // 楼梯核心筒: 门朝 +z, 与裙楼同一侧街
            ("SM_Core_Stair_v1", new Vector3(24, 0, 20), STREET_YAW),
        };
        static readonly (string name, Vector3 pos, float yaw)[] SKYLINE = {
            ("SM_BLD_SkyL_v1", new Vector3(140, 0, 80), 15 + STREET_YAW),
            ("SM_BLD_SkyM_v1", new Vector3(-150, 0, -90), 40 + STREET_YAW),
            ("SM_BLD_SkyS_v1", new Vector3(90, 0, -150), 70 + STREET_YAW),
            ("SM_BLD_SkyL_v1", new Vector3(-90, 0, 170), 25 + STREET_YAW),
            ("SM_BLD_SkyM_v1", new Vector3(210, 0, -30), 55 + STREET_YAW),
            ("SM_BLD_SkyS_v1", new Vector3(-220, 0, 40), 10 + STREET_YAW),
            ("SM_BLD_SkyM_v1", new Vector3(60, 0, 230), 80 + STREET_YAW),
        };

        [MenuItem("HST/City 楼房场景(建+验+出图)")]
        public static void BuildCityMenu() { BuildCity(); }

        public static void BuildCity()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            var prevScene = EditorSceneManager.GetActiveScene();
            UnityEngine.SceneManagement.Scene city = default;
            try
            {
                ConfigureImporters();
                var mats = BuildMaterials();
                // **附加式**建场景(不打断用户当前打开的场景)。批量模式下初始场景是无标题
                // 未保存场景, 附加会报 "Cannot create a new scene additively with an untitled
                // scene unsaved"(实测) → 无路径时退回 Single 模式, 收尾也不再还原。
                bool additive = !string.IsNullOrEmpty(prevScene.path);
                city = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                        additive ? NewSceneMode.Additive : NewSceneMode.Single);
                if (additive) EditorSceneManager.SetActiveScene(city);
                addingToExisting = additive;
                var root = new GameObject("CITY_ROOT");
                BuildLighting();
                BuildGround(root);
                var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FBX);
                if (fbx == null) { Debug.LogError("[CITY] 找不到 FBX: " + FBX); Finish(prevScene, city, false); return; }
                string manifest = File.Exists(MANIFEST) ? File.ReadAllText(MANIFEST) : null;
                int placed = PlaceBuildings(root, fbx, mats, manifest);
                PlaceSkyline(root, fbx, mats, manifest);
                PlaceScaleRef(root, mats);
                Diag(root, mats, null);                 // 诊断先跑(尺寸/材质/法线/比例门一次拿全)
                VerifyAgainstManifest(root);            // 报警不中止: 数值+图一起看再决定
                var cams = BuildCameras();              // ★ 相机必须在**存盘之前**建:
                EditorSceneManager.SaveScene(city, SCENE);   // 否则打开场景看不到任何机位(实测踩过)
                AssetDatabase.SaveAssets();
                Debug.Log($"[CITY] 场景已存 {SCENE}, 主楼 {placed} 件 + 天际线 {SKYLINE.Length} 件 + {cams.Count} 台机位");
                ShootAll(cams);
                Finish(prevScene, city, true);
            }
            catch (Exception e)
            {
                Debug.LogError("[CITY] 异常: " + e);
                Finish(prevScene, city, false);
            }
        }

        static bool addingToExisting = true;

        /// <summary>收尾: 关掉附加场景, 把用户原来的活动场景还回去(不碰它, 无对话框)。</summary>
        static void Finish(UnityEngine.SceneManagement.Scene prev, UnityEngine.SceneManagement.Scene city,
                           bool ok)
        {
            if (addingToExisting && city.IsValid()) EditorSceneManager.CloseScene(city, true);
            if (addingToExisting && prev.IsValid()) EditorSceneManager.SetActiveScene(prev);
            Debug.Log("[CITY] 收尾完成, 用户场景未受影响。结果: " + (ok ? "OK" : "FAIL"));
        }

        // ==================================================== 导入设置
        static void ConfigureImporters()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { TXT }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var n = Path.GetFileName(p);
                bool linear = n.EndsWith("_N.png") || n.EndsWith("_R.png") || n.EndsWith("_M.png")
                              || n.EndsWith("_AO.png") || n.EndsWith("_MS.png") || n.EndsWith("_ORM.png");
                var ti = AssetImporter.GetAtPath(p) as TextureImporter;
                if (ti == null) continue;
                bool ch = false;
                if (ti.sRGBTexture == linear) { ti.sRGBTexture = !linear; ch = true; }
                if (ti.wrapMode != TextureWrapMode.Repeat) { ti.wrapMode = TextureWrapMode.Repeat; ch = true; }
                if (ti.maxTextureSize < 2048) { ti.maxTextureSize = 2048; ch = true; }
                if (ti.mipmapEnabled == false) { ti.mipmapEnabled = true; ch = true; }
                if (ch) ti.SaveAndReimport();
            }
            foreach (var f in new[] { FBX, HUMAN_FBX })
            {
                var fi = AssetImporter.GetAtPath(f) as ModelImporter;
                if (fi == null) continue;
                bool ch = false;
                if (fi.materialImportMode != ModelImporterMaterialImportMode.None)
                { fi.materialImportMode = ModelImporterMaterialImportMode.None; ch = true; }   // 材质全由本脚本接线
                // 轴转换: 不开会把 Blender 的 Z-up 直接当 Unity 的 Z-up → 楼躺倒(实测轴序 24/24/14)
                if (!fi.bakeAxisConversion) { fi.bakeAxisConversion = true; ch = true; }
                if (ch) fi.SaveAndReimport();
            }
            Debug.Log("[CITY] 导入设置完成(贴图 sRGB/Repeat/2048, FBX 不导材质, 含假人)");
        }

        // ==================================================== 材质
        class Mats { public Material facadeDay, facadeDusk, roof, panel, dark, ground, scaleRef;
                     public Material winWarm, winCool, winNeut, accent; }
        static readonly string[] KW_COMMON = { "_NORMALMAP", "_METALLICSPECGLOSSMAP",
                                               "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A" };

        static Mats BuildMaterials()
        {
            var m = new Mats
            {
                facadeDay = MakeLit("M_BLD_FacadeDay", TXT + "/T_BLD_Facade_White_Day", 1.6f, KW_COMMON),
                facadeDusk = MakeLit("M_BLD_FacadeDusk", TXT + "/T_BLD_Facade_White_Dusk", 3.2f, KW_COMMON),
                roof = MakeLit("M_BLD_Roof", TXT + "/T_HS_FloorPlate_White", 0f, KW_COMMON),
                panel = MakeLit("M_BLD_Panel", TXT + "/T_HS_PanelWall_White", 0f, KW_COMMON),
                dark = MakeLit("M_BLD_Dark", TXT + "/T_HS_DarkAccent_Black", 0f, KW_COMMON),
                // 亮窗/发光带: **URP Unlit + HDR _BaseColor**(项目已验证的发光口径 ——
                // 不走 _EMISSION 关键字: 该关键字在 2022.3 里内存开、落盘丢, 不可靠)
                winWarm = MakeUnlit("M_BLD_Emit_AWarm", new Color(1.00f, 0.72f, 0.42f) * 0.90f),
                winCool = MakeUnlit("M_BLD_Emit_BCool", new Color(0.55f, 0.72f, 1.00f) * 0.85f),
                winNeut = MakeUnlit("M_BLD_Emit_CNeut", new Color(0.95f, 0.93f, 0.88f) * 0.85f),
                accent = MakeUnlit("M_BLD_Emit_DAccent", new Color(0.039f, 0.557f, 0.890f) * 0.95f),
            };
            // 地面: 纯色沥青(预览/临时), 不走 PBR 套件
            var sp = MAT + "/M_BLD_Ground.mat";
            var gd = AssetDatabase.LoadAssetAtPath<Material>(sp);
            if (gd == null)
            {
                gd = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "M_BLD_Ground" };
                gd.SetColor("_BaseColor", new Color(0.155f, 0.165f, 0.175f, 1f));
                gd.SetFloat("_Smoothness", 0.35f);
                AssetDatabase.CreateAsset(gd, sp);
            }
            m.ground = gd;
            // 假人标尺: 纯色中性灰(标尺件不贴图, 一眼能和建筑区分开)
            var srp = MAT + "/M_ScaleRef_Neutral.mat";
            var sr = AssetDatabase.LoadAssetAtPath<Material>(srp);
            if (sr == null)
            {
                sr = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "M_ScaleRef_Neutral" };
                sr.SetColor("_BaseColor", new Color(0.72f, 0.72f, 0.74f, 1f));
                sr.SetFloat("_Smoothness", 0.30f);
                sr.SetFloat("_Metallic", 0.0f);
                AssetDatabase.CreateAsset(sr, srp);
            }
            m.scaleRef = sr;
            AssetDatabase.SaveAssets();
            return m;
        }

        static Material MakeUnlit(string name, Color hdr)
        {
            string mp = MAT + "/" + name + ".mat";
            var mm = AssetDatabase.LoadAssetAtPath<Material>(mp);
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (mm == null) { mm = new Material(sh) { name = name }; AssetDatabase.CreateAsset(mm, mp); }
            mm.shader = sh;
            mm.SetTexture("_BaseMap", null);
            mm.SetColor("_BaseColor", hdr);      // HDR >1 → 吃 TA Bloom
            mm.SetFloat("_Surface", 0f);         // Opaque: 不需要透明关键字(避开同一类持久化坑)
            mm.SetFloat("_ZWrite", 1f);
            mm.SetFloat("_Cull", 0f);            // 双面: 面片进出视角都可见
            EditorUtility.SetDirty(mm);
            AssetDatabase.SaveAssets();
            EditorUtility.ClearDirty(mm);
            return AssetDatabase.LoadAssetAtPath<Material>(mp);
        }

        static Material MakeLit(string name, string folder, float emis, string[] kws)
        {
            string mp = MAT + "/" + name + ".mat";
            var mm = AssetDatabase.LoadAssetAtPath<Material>(mp);
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (mm == null)
            {
                mm = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mm, mp);
            }
            mm.shader = shader;
            // 路径拼写坑: 贴图在**子目录**里且文件名带集合名前缀 ——
            // folder + "_BC.png" 是错的(少了 /<集合名>), 会静默拿不到图 → BC=False + metallic=1 = 黑楼
            string set = System.IO.Path.GetFileName(folder);
            var bc = AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/{set}_BC.png");
            var n = AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/{set}_N.png");
            var ms = AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/{set}_MS.png");
            var e = AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/{set}_E.png");
            mm.SetTexture("_BaseMap", bc);
            mm.SetTexture("_BumpMap", n);
            mm.SetTexture("_MetallicGlossMap", ms);
            if (e != null) mm.SetTexture("_EmissionMap", e);
            mm.SetFloat("_Smoothness", 1f);
            mm.SetFloat("_SmoothnessTextureChannel", 1f);   // 与走廊口径一致(BaseMap A = smoothness)
            mm.SetFloat("_Metallic", 1f);
            mm.SetFloat("_BumpScale", 1f);
            mm.SetFloat("_OcclusionStrength", 0f);          // AO 已预乘/自行处理
            if (emis > 0)
            {
                mm.SetColor("_EmissionColor", new Color(0.92f, 0.94f, 1f) * emis);
                mm.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }
            var kw = new List<string>(kws);
            if (emis > 0) kw.Add("_EMISSION");
            foreach (var k in kw) mm.EnableKeyword(k);
            EditorUtility.SetDirty(mm);
            AssetDatabase.SaveAssets();
            PatchKeywordsOnDisk(mp, kw);   // 磁盘级写 m_ValidKeywords(EnableKeyword 存不进 .mat)
            var m2 = AssetDatabase.LoadAssetAtPath<Material>(mp);
            // 复查 + ClearDirty: 不 Clear 的话编辑器退出时会把"未带关键字的内存状态"重新刷盘,
            // 把刚打的补丁覆盖掉(实测: 文件一度有 _EMISSION, 运行结束后又没了)
            EditorUtility.ClearDirty(m2);
            Debug.Log($"[CITY-KW] {name} 复查: _EMISSION={(m2.IsKeywordEnabled("_EMISSION") ? "开" : "关")} " +
                      $"kw=[{string.Join(",", m2.shaderKeywords)}]");
            return m2;
        }

        /// <summary>.mat 是 YAML 文本; 关键字写进 m_ValidKeywords 并自 m_InvalidKeywords 移除。
        /// 注意路径: `Assets/...` 是**相对**路径, 批处理下 CWD 不是工程根 → File.Exists 恒 false
        /// 而静默跳过(实测: 补丁从未执行、日志里一条 CITY-KW 都没有)。必须转绝对路径。</summary>
        static void PatchKeywordsOnDisk(string path, List<string> kws)
        {
            string abs = Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
            if (!File.Exists(abs)) { Debug.LogWarning("[CITY-KW] 文件不存在: " + abs); return; }
            var txt = File.ReadAllText(abs);
            Debug.Log("[CITY-KW] 进入 " + Path.GetFileName(path));
            var rxV = new System.Text.RegularExpressions.Regex(
                @"m_ValidKeywords:(\s*\[\])?(?<v>(\r?\n  - [^\r\n]+)*)");
            var rxI = new System.Text.RegularExpressions.Regex(
                @"m_InvalidKeywords:(\s*\[\])?(?<v>(\r?\n  - [^\r\n]+)*)");
            var mv = rxV.Match(txt);
            var mi = rxI.Match(txt);
            if (!mv.Success) { Debug.LogWarning("[CITY-KW] 无 m_ValidKeywords: " + path); return; }
            string v = mv.Groups["v"].Value;
            string iv = mi.Success ? mi.Groups["v"].Value : "";
            bool ch = false;
            foreach (var k in kws)
            {
                if (v.Contains("\n  - " + k)) continue;
                v = "\n  - " + k + v;
                iv = iv.Replace("\n  - " + k, "");
                ch = true;
            }
            if (!ch) { Debug.Log("[CITY-KW] " + Path.GetFileName(path) + " 无需补(已含 " + string.Join(",", kws) + ")"); return; }
            txt = txt.Remove(mv.Index, mv.Length).Insert(mv.Index, "m_ValidKeywords:" + v);
            var mi2 = rxI.Match(txt);
            if (mi2.Success)
                txt = txt.Remove(mi2.Index, mi2.Length).Insert(mi2.Index, "m_InvalidKeywords:" + iv);
            File.WriteAllText(abs, txt);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Debug.Log("[CITY-KW] " + Path.GetFileName(path) + " ← " + string.Join(",", kws));
        }

        // ==================================================== 场景
        static void BuildLighting()
        {
            var sunGo = new GameObject("L_Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1.0f, 0.975f, 0.93f);
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft;
            // 太阳从**相机同侧**上方来(相机群在 +x+z 象限俯看): 光行进方向 (-x,-y,-z),
            // 照亮面向相机的 +x/+z 立面。首轮方向写反(0.579,-0.574,0.579) → 可见面全在背光,
            // 白天街景里楼发黑(实测)。
            sunGo.transform.rotation = Quaternion.LookRotation(new Vector3(-0.50f, -0.62f, -0.50f).normalized);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.47f, 0.55f);
            RenderSettings.fog = false;
            RenderSettings.skybox = null;
        }

        static void BuildGround(GameObject root)
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "CITY_Ground";
            ground.transform.SetParent(root.transform);
            ground.transform.localScale = new Vector3(80, 1, 80);   // 800m
            var m = AssetDatabase.LoadAssetAtPath<Material>(MAT + "/M_BLD_Ground.mat");
            ground.GetComponent<MeshRenderer>().sharedMaterial = m;
            ground.isStatic = true;
        }

        static int PlaceBuildings(GameObject root, GameObject fbx, Mats mats, string manifest)
        {
            int n = 0;
            foreach (var (name, pos, yaw) in LAYOUT)
            {
                var src = FindChild(fbx, name);
                if (src == null) { Debug.LogError("[CITY] FBX 缺少 " + name); continue; }
                var go = UnityEngine.Object.Instantiate(src.gameObject, pos,
                                                        Quaternion.Euler(0, yaw, 0), root.transform);
                go.name = name + "_placed";
                WireMaterials(go, mats.facadeDay, mats, ReadManifestSlots(manifest, name));
                AttachWindows(go, fbx, name, pos, yaw, mats, root);
                AddBoxCollider(go);
                n++;
            }
            return n;
        }

        /// <summary>假人标尺: 摆在主楼街门旁, 不挂碰撞(规范里写明"不参与碰撞/玩法")。
        /// 实测高与 spec 的 human_h 对拍, 顺带把四个比例门打出来(见 Diag 的 CITY-SCALE)。</summary>
        static void PlaceScaleRef(GameObject root, Mats mats)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(HUMAN_FBX);
            if (fbx == null) { Debug.LogError("[CITY] 找不到假人 FBX: " + HUMAN_FBX); return; }
            var src = FindChild(fbx, "SM_Human_Scale175_v1") ?? fbx.transform;
            var go = UnityEngine.Object.Instantiate(src.gameObject, HUMAN_POS,
                                                    Quaternion.Euler(0, STREET_YAW, 0), root.transform);
            go.name = "SM_Human_Scale175_v1_placed";
            var r = go.GetComponentInChildren<MeshRenderer>();
            if (r != null)
            {
                var arr = r.sharedMaterials;
                for (int i = 0; i < arr.Length; i++) arr[i] = mats.scaleRef;
                if (arr.Length == 0) arr = new[] { mats.scaleRef };
                r.sharedMaterials = arr;
            }
            // ★ 轴向判据: 假人**脚尖朝 Blender -Y**(脚长 0.2625m 里脚尖占 72%), 所以 mesh 局部
            //   bounds 的 z 是**不对称**的 —— 用它可以一次判定 FBX 往返到底是 Blender +Y→Unity +Z
            //   还是 -Y→+Z(塔楼是正方形, 只有入口/雨棚能区分, 而雨棚 1.6m 的偏置太小容易看错)。
            var hf = go.GetComponentInChildren<MeshFilter>();
            if (hf != null && hf.sharedMesh != null)
                Debug.Log($"[CITY-AXIS] 假人 mesh 局部 z: min={hf.sharedMesh.bounds.min.z:F5} " +
                          $"max={hf.sharedMesh.bounds.max.z:F5}  " +
                          $"(Blender y ∈ [{-0.189:F3}(脚尖), {0.1225:F3}(脚跟)]; ×0.01 后比符号即可判轴)");
        }

        /// <summary>把该建筑的日/暮亮窗对象挂到主体下, 并按变体切换显隐(白天只开 WinDay)。</summary>
        static void AttachWindows(GameObject host, GameObject fbx, string name, Vector3 pos, float yaw,
                                  Mats mats, GameObject root)
        {
            var wmats = new[] { mats.winWarm, mats.winCool, mats.winNeut, mats.accent };
            foreach (var (suffix, active) in new[] { ("_WinDay", true), ("_WinDusk", false) })
            {
                var src = FindChild(fbx, name + suffix);
                if (src == null) continue;
                var go = UnityEngine.Object.Instantiate(src.gameObject, pos,
                                                        Quaternion.Euler(0, yaw, 0), root.transform);
                go.name = name + suffix;
                var r = go.GetComponentInChildren<MeshRenderer>();
                if (r != null)
                {
                    var arr = new Material[Mathf.Max(4, r.sharedMaterials.Length)];
                    for (int i = 0; i < arr.Length; i++) arr[i] = wmats[Mathf.Min(i, 3)];
                    r.sharedMaterials = arr;
                    r.shadowCastingMode = ShadowCastingMode.Off;   // 发光面片不投影
                    r.receiveShadows = false;
                }
                go.SetActive(active);
            }
        }

        static void SetVariant(bool dusk)
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                if (t.name.EndsWith("_WinDay")) t.gameObject.SetActive(!dusk);
                else if (t.name.EndsWith("_WinDusk")) t.gameObject.SetActive(dusk);
            }
        }

        static void PlaceSkyline(GameObject root, GameObject fbx, Mats mats, string manifest)
        {
            foreach (var (name, pos, yaw) in SKYLINE)
            {
                var src = FindChild(fbx, name);
                if (src == null) continue;
                var go = UnityEngine.Object.Instantiate(src.gameObject, pos,
                                                        Quaternion.Euler(0, yaw, 0), root.transform);
                go.name = name + "_sky";
                WireMaterials(go, mats.facadeDay, mats, ReadManifestSlots(manifest, name));
                // 远景楼提亮一档: 玻璃幕墙在远处被 mipmap 平均成深色, 会读成"黑块"(距离感) ——
                // 用 _BaseColor 当亮度旋钮(实例材质, 不改资产)
                var r = go.GetComponentInChildren<MeshRenderer>();
                if (r != null)
                {
                    var cur = r.sharedMaterials;
                    var br = new Material(cur[0]) { name = "SKY_" + cur[0].name };
                    br.SetColor("_BaseColor", new Color(1.45f, 1.45f, 1.45f));
                    cur[0] = br;
                    r.sharedMaterials = cur;
                }
                AttachWindows(go, fbx, name, pos, yaw, mats, root);
            }
        }

        static Transform FindChild(GameObject fbx, string name)
        {
            foreach (var t in fbx.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        /// <summary>按 **manifest 里记录的真实槽序**接线。
        /// 为什么不能按固定的 [立面,屋顶,白板,黑件] 接: FBX 材质表只写"被用到的槽", 顺序是
        /// 该槽多边形首次出现的先后 —— 每个对象都可能不同(实测: 核心筒只用白板+黑件, 表里
        /// 就两条; 裸楼只有立面+屋顶)。按固定槽序接会把核心筒的墙接上玻璃幕墙材质。
        /// 拿不到槽序数组时退回固定约定(并告警), 不静默接错。</summary>
        static void WireMaterials(GameObject go, Material facade, Mats mats, string[] slots)
        {
            var r = go.GetComponentInChildren<MeshRenderer>();
            if (r == null) return;
            int n = Mathf.Max(1, r.sharedMaterials.Length);
            var arr = new Material[n];
            if (slots == null || slots.Length == 0)
            {
                Debug.LogWarning($"[CITY] {go.name} 无 manifest 槽序, 退回固定约定(可能有槽位前移)");
                for (int i = 0; i < n; i++) arr[i] = FromSlotName(null, i, facade, mats);
            }
            else
            {
                for (int i = 0; i < n; i++)
                    arr[i] = FromSlotName(i < slots.Length ? slots[i] : null, i, facade, mats);
            }
            r.sharedMaterials = arr;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
        }

        static Material FromSlotName(string slot, int idx, Material facade, Mats mats)
        {
            switch (slot)
            {
                case "M_BLD_FacadeDay": return facade;
                case "M_BLD_Roof": return mats.roof;
                case "M_BLD_Panel": return mats.panel;
                case "M_BLD_Dark": return mats.dark;
            }
            switch (idx) { case 0: return facade; case 1: return mats.roof;
                           case 2: return mats.panel; default: return mats.dark; }
        }

        static string[] ReadManifestSlots(string manifest, string name)
        {
            if (string.IsNullOrEmpty(manifest)) return null;
            int i = manifest.IndexOf("\"name\": \"" + name + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int s = manifest.IndexOf("\"slots\":", i, StringComparison.Ordinal);
            if (s < 0) return null;
            int a = manifest.IndexOf("[", s), b = manifest.IndexOf("]", a);
            if (a < 0 || b < 0) return null;
            return manifest.Substring(a + 1, b - a - 1)
                .Split(',').Select(x => x.Trim().Trim('"')).Where(x => x.Length > 0).ToArray();
        }

        static void AddBoxCollider(GameObject go)
        {
            var mf = go.GetComponentInChildren<MeshFilter>();
            if (mf == null) return;
            var mb = mf.sharedMesh.bounds;                    // mesh 局部
            var ms = mf.transform.lossyScale;                 // mesh → 世界 的缩放
            var gs = go.transform.lossyScale;                 // collider 所在对象的缩放
            var bc = go.AddComponent<BoxCollider>();
            bc.center = go.transform.InverseTransformPoint(mf.transform.TransformPoint(mb.center));
            bc.size = new Vector3(
                mb.size.x * Mathf.Abs(ms.x) / Mathf.Max(Mathf.Abs(gs.x), 1e-6f),
                mb.size.y * Mathf.Abs(ms.y) / Mathf.Max(Mathf.Abs(gs.y), 1e-6f),
                mb.size.z * Mathf.Abs(ms.z) / Mathf.Max(Mathf.Abs(gs.z), 1e-6f));
        }

        // ==================================================== 诊断(出黑/出白时先看这里)
        static void Diag(GameObject root, Mats mats, Camera _)
        {
            foreach (var mn in new[] { "M_BLD_FacadeDay", "M_BLD_Roof", "M_BLD_Panel", "M_BLD_Dark" })
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(MAT + "/" + mn + ".mat");
                if (m == null) { Debug.LogError("[CITY-DIAG] 缺材质 " + mn); continue; }
                Debug.Log($"[CITY-DIAG] {mn}: shader={m.shader.name} " +
                          $"BC={(m.GetTexture("_BaseMap") != null)} N={(m.GetTexture("_BumpMap") != null)} " +
                          $"MS={(m.GetTexture("_MetallicGlossMap") != null)} E={(m.GetTexture("_EmissionMap") != null)} " +
                          $"kw=[{string.Join(",", m.shaderKeywords)}] " +
                          $"smoothCh={m.GetFloat("_SmoothnessTextureChannel"):F0} metallic={m.GetFloat("_Metallic"):F1}");
            }
            var t = FindChildByPrefix(root, "SM_BLD_TowerA");
            if (t != null)
            {
                var mf = t.GetComponentInChildren<MeshFilter>();
                var mesh = mf.sharedMesh;
                var nr = mesh.normals;
                string n6 = "";
                for (int i = 0; i < Mathf.Min(6, nr.Length); i++)
                    n6 += $"({nr[i].x:F2},{nr[i].y:F2},{nr[i].z:F2}) ";
                Debug.Log($"[CITY-DIAG] TowerA mesh: verts={mesh.vertexCount} subMeshes={mesh.subMeshCount} " +
                          $"meshBounds={mesh.bounds.size} bMin={mesh.bounds.min} bMax={mesh.bounds.max} " +
                          $"worldBounds={mf.GetComponent<MeshRenderer>().bounds.size} " +
                          $"lossyScale={t.lossyScale} 法线前6={n6} " +
                          $"rendererMats={(mf.GetComponent<MeshRenderer>().sharedMaterials.Length)}");
            }
            // 天际线是"裸楼"(2 槽: 立面+屋顶), 与英雄楼不同路径 —— 单独诊断
            var sky = FindChildByPrefix(root, "SM_BLD_Sky");
            if (sky != null)
            {
                var mr = sky.GetComponentInChildren<MeshRenderer>();
                var mf = sky.GetComponentInChildren<MeshFilter>();
                var mm = mr.sharedMaterials;
                string names = "";
                for (int i = 0; i < mm.Length; i++) names += (mm[i] == null ? "null" : mm[i].name) + " ";
                Debug.Log($"[CITY-DIAG] Skyline {sky.name}: mats=[{names}] subMeshes={mf.sharedMesh.subMeshCount} " +
                          $"uvs={mf.sharedMesh.uv.Length} worldBounds={mr.bounds.size} scale={sky.lossyScale.x:F0}");
            }
            // 纯白对照图: 同一机位换成无贴图材质的渲染(黑=法线/网格问题, 白=贴图接线问题)
            var flat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "DIAG_FLAT" };
            flat.SetColor("_BaseColor", Color.white);
            flat.SetFloat("_Smoothness", 0.2f);
            var swapped = new List<(Renderer r, Material[] orig)>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (r.sharedMaterials.Length == 0 || r.sharedMaterials[0] == null) continue;
                if (!r.sharedMaterials[0].name.StartsWith("M_BLD_Facade")) continue;
                var cur = r.sharedMaterials;
                var nw = new Material[cur.Length];
                for (int i = 0; i < cur.Length; i++) nw[i] = flat;
                swapped.Add((r, cur));
                r.sharedMaterials = nw;
            }
            var cam = MakeCam("CAM_DIAG", new Vector3(60, 40, 60), new Vector3(8, 25, 8), 45);
            Shot(cam, OUT + "/unity_diag_flatlit.png");
            UnityEngine.Object.DestroyImmediate(cam.gameObject);
            foreach (var (r, orig) in swapped) r.sharedMaterials = orig;
            UnityEngine.Object.DestroyImmediate(flat);
            Debug.Log($"[CITY-DIAG] 纯白对照图已出({swapped.Count} 渲染器), 全部还原");
            PrintScaleGates(root);
        }

        // ==================================================== 比例门(动作三)
        /// <summary>四个"人对建筑"的比例门。★ 常量全部从 spec_bld.json 读 —— 那是
        /// lib_bld_spec.py 导出的唯一尺寸源; 在 C# 里再写一份常量就等于又开了一处会漂移的副本。</summary>
        static void PrintScaleGates(GameObject root)
        {
            if (!File.Exists(SPEC_JSON))
            { Debug.LogWarning("[CITY-SCALE] 无 " + SPEC_JSON + ", 跳过比例门(先跑 build_city.py)"); return; }
            var j = File.ReadAllText(SPEC_JSON);
            float H = SpecF(j, "human_h"), doorH = SpecF(j, "door_h"), parapet = SpecF(j, "parapet_h");
            float floorH = SpecF(j, "floor_h"), rise = SpecF(j, "stair_rise");
            var rows = new (string label, float val, float target, float tol)[] {
                ("门高",     doorH,   1.16f, 0.05f),
                ("女儿墙高", parapet, 0.61f, 0.04f),
                ("层高",     floorH,  2.29f, 0.10f),
                ("踏步高",   rise,    0.097f, 0.005f),
            };
            int bad = 0;
            foreach (var (label, val, target, tol) in rows)
            {
                float ratio = val / H;
                bool ok = Mathf.Abs(ratio - target) <= tol;
                if (!ok) bad++;
                Debug.Log($"[CITY-SCALE] 比例门 {label}÷身高({H:F3}m) = {ratio:F3} " +
                          $"(目标 {target:F2}±{tol:F3}) {(ok ? "OK" : "FAIL")}");
            }
            var sc = FindChildByPrefix(root, "SM_Human_Scale175");
            if (sc != null)
            {
                var mr = sc.GetComponentInChildren<MeshRenderer>();
                if (mr != null)
                    Debug.Log($"[CITY-SCALE] 假人实测高 {mr.bounds.size.y:F3}m (spec {H:F3}m, " +
                              $"偏差 {(mr.bounds.size.y - H) * 1000f:F1}mm)");
            }
            Debug.Log($"[CITY-SCALE] 比例门 共 {rows.Length} 项, 未过 {bad} 项");
        }

        /// <summary>从 spec_bld.json 里取一个数(spec 与 derived 两个段都在同一份 JSON 里, 直接搜键名)。</summary>
        static float SpecF(string json, string key)
        {
            var rx = new System.Text.RegularExpressions.Regex("\"" + key + "\"\\s*:\\s*(-?[0-9][0-9.eE+-]*)");
            var m = rx.Match(json);
            if (!m.Success) { Debug.LogWarning("[CITY-SCALE] spec 里找不到键 " + key); return 0f; }
            return float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        static Transform FindChildByPrefix(GameObject root, string prefix)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith(prefix)) return t;
            return null;
        }

        // ==================================================== 验收(manifest 对拍)
        static int VerifyAgainstManifest(GameObject root)
        {
            var mp = MANIFEST;
            if (!File.Exists(mp)) { Debug.LogWarning("[CITY] 无 manifest, 跳过尺寸验收"); return 0; }
            var txt = File.ReadAllText(mp);
            int fails = 0, checkedN = 0;
            foreach (var (name, pos, yaw) in LAYOUT)
            {
                var go = root.transform.Cast<Transform>()
                    .Select(t => t.gameObject).FirstOrDefault(g => g.name == name + "_placed");
                if (go == null) continue;
                var mf = go.GetComponentInChildren<MeshFilter>();
                var mr = go.GetComponentInChildren<MeshRenderer>();
                if (mf == null || mr == null) continue;
                // **世界空间**包围盒: FBX 导入器会把 cm→m 的 0.01 缩放放在变换上,
                // 读 mesh.bounds(局部)会得到 1/100 的值(首轮实测 16m 读成 0.16m)。
                var sz = mr.bounds.size;
                // manifest(Blender 系) 尺寸 (sx, sy, sz) → 导入后 Unity 系 (sx, sz, sy)。
                // 摆场 yaw 只交换 X/Z, 所以: **Y 必须等于 Blender 的 z(高度, 抓躺倒)**,
                // X/Z 排序后比(抓缩放; 旋转实例的 AABB 会交换 X/Z)。
                var (bx, by, bz) = ReadManifestSize(txt, name);
                float dY = Mathf.Abs(sz.y - bz);
                float dXZ = Mathf.Max(
                    Mathf.Abs(Mathf.Max(sz.x, sz.z) - Mathf.Max(bx, by)),
                    Mathf.Abs(Mathf.Min(sz.x, sz.z) - Mathf.Min(bx, by)));
                float err = Mathf.Max(dY, dXZ);
                bool ok = err <= SIZE_TOL;
                if (!ok) fails++;
                checkedN++;
                Debug.Log($"[CITY-VERIFY] {name}: bl=({bx},{by},{bz}) got={sz} " +
                          $"dY={dY * 1000f:F1}mm dXZ={dXZ * 1000f:F1}mm {(ok ? "OK" : "FAIL")}");
            }
            Debug.Log($"[CITY-VERIFY] 共 {checkedN} 件, 失败 {fails} 件 (容差 {SIZE_TOL * 1000f:F0}mm)");
            return fails;
        }

        static (float, float, float) ReadManifestSize(string json, string name)
        {
            var key = "\"name\": \"" + name + "\"";
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return (0, 0, 0);
            int s = json.IndexOf("[", i);
            int e = json.IndexOf("]", s);
            var parts = json.Substring(s + 1, e - s - 1).Split(',');
            return (float.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[2].Trim(), System.Globalization.CultureInfo.InvariantCulture));
        }

        // ==================================================== 出图
        static void ShootAll(Dictionary<string, Camera> cams)
        {
            Directory.CreateDirectory(OUT);
            foreach (var kv in cams)
            {
                Shot(kv.Value, $"{OUT}/unity_day_{kv.Key}.png");
                Debug.Log($"[CITY-SHOT] unity_day_{kv.Key}.png");
            }

            // 暮色变体(临时换装: 立面→Dusk + 亮窗切到 Dusk 组 + 发光强度×1.8; 拍完还原, 不存盘)
            var mats = BuildMaterials();
            SetVariant(true);
            var swapped = new List<(Renderer r, Material[] orig)>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (r.sharedMaterials.Length == 0 || r.sharedMaterials[0] == null) continue;
                if (!r.sharedMaterials[0].name.Contains("FacadeDay")) continue;
                var cur = r.sharedMaterials;
                var nw = (Material[])cur.Clone();
                nw[0] = mats.facadeDusk;
                swapped.Add((r, cur));
                r.sharedMaterials = nw;
            }
            // 亮窗发光加档(克隆实例, 不动资产)
            var boosted = new List<(Renderer r, Material[] orig)>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (!r.gameObject.activeInHierarchy) continue;
                if (r.sharedMaterials.Length == 0 || r.sharedMaterials[0] == null) continue;
                if (!r.sharedMaterials[0].name.StartsWith("M_BLD_Emit")) continue;
                var cur = r.sharedMaterials;
                var nw = new Material[cur.Length];
                for (int i = 0; i < cur.Length; i++)
                {
                    var src = cur[i];
                    if (src == null) { nw[i] = src; continue; }
                    var t = new Material(src) { name = "DUSK_" + src.name };
                    t.SetColor("_BaseColor", src.GetColor("_BaseColor") * 1.7f);
                    nw[i] = t;
                }
                boosted.Add((r, cur));
                r.sharedMaterials = nw;
            }
            var sun = UnityEngine.Object.FindObjectOfType<Light>();
            float sunI = sun.intensity; Color sunC = sun.color;
            var amb = RenderSettings.ambientLight;
            sun.intensity = 0.10f; sun.color = new Color(0.62f, 0.72f, 0.95f);
            RenderSettings.ambientLight = new Color(0.075f, 0.10f, 0.16f);
            var bgSaved = new List<(Camera c, Color bg)>();
            foreach (var c in cams.Values)
            {
                bgSaved.Add((c, c.backgroundColor));
                c.backgroundColor = new Color(0.045f, 0.075f, 0.135f);   // 暮色天幕
            }
            Shot(cams["02_Street"], OUT + "/unity_dusk_02_Street.png");
            Shot(cams["01_Aerial"], OUT + "/unity_dusk_01_Aerial.png");
            Debug.Log($"[CITY-SHOT] 暮色变体 2 张(立面换装 {swapped.Count} / 亮窗加档 {boosted.Count})");
            foreach (var (r, orig) in boosted) r.sharedMaterials = orig;
            foreach (var (r, orig) in swapped) r.sharedMaterials = orig;
            foreach (var (r, _) in boosted)
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.name.StartsWith("DUSK_")) UnityEngine.Object.DestroyImmediate(m);
            sun.intensity = sunI; sun.color = sunC;
            RenderSettings.ambientLight = amb;
            foreach (var (c, bg) in bgSaved) c.backgroundColor = bg;
            SetVariant(false);

            // NPR 变体(TA 母材质直译, 非破坏, 只拍 1 张)
            var nprShader = Shader.Find("OurFunction/NPR/Deferred Toon");
            if (nprShader != null)
            {
                var npSwapped = new List<(Renderer r, Material[] orig)>();
                foreach (var kv in cams) { }
                foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
                {
                    if (r.sharedMaterials.Length == 0 || r.sharedMaterials[0] == null) continue;
                    if (!r.sharedMaterials[0].name.StartsWith("M_BLD_")) continue;
                    if (r.sharedMaterials[0].name.StartsWith("M_BLD_Emit")) continue;   // 发光件保持原样
                    var cur = r.sharedMaterials;
                    var nw = new Material[cur.Length];
                    for (int i = 0; i < cur.Length; i++)
                    {
                        var src = cur[i];
                        if (src == null || src.name == "M_BLD_Ground") { nw[i] = src; continue; }
                        var nm = new Material(nprShader) { name = "NPRSWAP_" + src.name };
                        if (src.HasProperty("_BaseMap")) nm.SetTexture("_BaseMap", src.GetTexture("_BaseMap"));
                        if (src.HasProperty("_BaseColor")) nm.SetColor("_BaseColor", src.GetColor("_BaseColor"));
                        if (src.HasProperty("_MetallicGlossMap")) nm.SetTexture("_MetallicGlossMap", src.GetTexture("_MetallicGlossMap"));
                        if (src.HasProperty("_Metallic")) nm.SetFloat("_Metallic", src.GetFloat("_Metallic"));
                        if (src.HasProperty("_Smoothness")) nm.SetFloat("_Smoothness", src.GetFloat("_Smoothness"));
                        if (src.HasProperty("_EmissionMap") && nm.HasProperty("_EmissionMap")) nm.SetTexture("_EmissionMap", src.GetTexture("_EmissionMap"));
                        nw[i] = nm;
                    }
                    npSwapped.Add((r, cur));
                    r.sharedMaterials = nw;
                }
                Shot(cams["02_Street"], OUT + "/unity_npr_02_Street.png");
                foreach (var (r, orig) in npSwapped) r.sharedMaterials = orig;
                Debug.Log($"[CITY-SHOT] NPR 变体(换装 {npSwapped.Count} 渲染器)已出图并还原");
            }
            else Debug.LogWarning("[CITY-SHOT] 找不到 OurFunction/NPR/Deferred Toon, 跳过 NPR 变体");
        }

        static Dictionary<string, Camera> BuildCameras()
        {
            var d = new Dictionary<string, Camera>();
            d["01_Aerial"] = MakeCam("CAM_01_Aerial", new Vector3(150, 118, 152), new Vector3(8, 32, 8), 40);
            // 街景: 退远 + 抬高一点, 一帧里同时有主楼基座、商铺橱窗、核心筒(动作二验收③)
            d["02_Street"] = MakeCam("CAM_02_Street", new Vector3(30, 6, 46), new Vector3(-6, 16, 4), 46);
            d["03_Roof"] = MakeCam("CAM_03_Roof", new Vector3(4, 66.5f, 6), new Vector3(36, 60, -30), 50);
            d["04_WallRun"] = MakeCam("CAM_04_WallRun", new Vector3(-14, 8, 16), new Vector3(-36, 22, -22), 45);
            // 街门口特写: 主楼门 + 雨棚 + 假人标尺(比例对拍就靠这张)
            d["05_Entrance"] = MakeCam("CAM_05_Entrance", new Vector3(-6, 2.2f, 24), new Vector3(0, 1.7f, 9), 42);
            // 楼梯核心筒: 俯视开口看双跑梯段 + 门洞(陡俯角 57° 才看得进 4m 深的内腔;
            // 平视只能看到一圈墙 —— 实测两次调机位, 41° 时近墙挡掉半层)
            d["06_Core"] = MakeCam("CAM_06_Core", new Vector3(30, 13, 26), new Vector3(24.3f, 0.5f, 20.3f), 40);
            return d;
        }

        static Camera MakeCam(string name, Vector3 pos, Vector3 look, float fov)
        {
            var go = new GameObject(name);
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = fov;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.31f, 0.40f, 0.56f);   // 白天天空(暮色时段会临时压暗)
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation((look - pos).normalized, Vector3.up);
            var ud = cam.GetUniversalAdditionalCameraData();
            ud.renderPostProcessing = true;   // 吃 TA 管线(TAA/GTAO/SSR/Bloom/体积光)
            return cam;
        }

        static void Shot(Camera cam, string path)
        {
            int W = 1920, H = 1080;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            var prev = cam.targetTexture;
            cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            var px = tex.GetPixels();
            var t8 = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
            var c8 = new Color[px.Length];
            for (int k = 0; k < px.Length; k++)
                c8[k] = new Color(L2S(Mathf.Clamp01(px[k].r)), L2S(Mathf.Clamp01(px[k].g)),
                                  L2S(Mathf.Clamp01(px[k].b)), 1f);
            t8.SetPixels(c8); t8.Apply();
            File.WriteAllBytes(path, t8.EncodeToPNG());
            cam.targetTexture = prev;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(t8);
        }

        static float L2S(float c) => c <= 0.0031308f ? c * 12.92f
            : 1.055f * Mathf.Pow(Mathf.Max(c, 0f), 1f / 2.4f) - 0.055f;
    }
}
#endif
