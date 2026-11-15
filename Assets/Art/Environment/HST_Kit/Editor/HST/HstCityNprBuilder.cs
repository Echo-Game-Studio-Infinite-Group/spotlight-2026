#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HST
{
    // 城市高楼 · NPR 渲染线(project-3, 2026-10-11)
    //
    // 用户令: "你直接做到 Unity 里吧, 你从隔壁 Project-0 拉一个分支作为 Project-3, 然后在里面把
    //          高楼放到 Unity 里, 然后采用差不多的渲染技巧, 就那个 Unity 走廊差不多的渲染技巧。"
    //
    // "走廊那套渲染技巧"在本工程里由四件事组成(逐条都在工程里查证过):
    //   ① 全局渲染器 = Assets/Settings/URP-Renderer.asset(TA 的 9 个特性: TAA / GTAO / SSR /
    //      延迟描边 / 性能 Bloom / NPR 光照 / 雾×2 / 体积光 / 径向红移), m_RenderingMode=1(Deferred)。
    //      这是**全工程生效**的, 本脚本只需要把相机 renderPostProcessing 打开。
    //   ② 材质换装到 "OurFunction/NPR/Deferred Toon" —— 关键机制: 全工程只有这个 shader 写了
    //      LightMode=NPRStyleData 的 pass(Assets/Shaders/NPR/NPRBase.shader:460), 而斜的
    //      NPRLightingFeature 正是用 ShaderTagId("NPRStyleData") 抓这批几何, 再全屏合成
    //      卡通分带+描边。所以 "URP Lit 材质" 在这条管线里**不会**变卡通, 只有换成 Deferred Toon 才会。
    //   ③ 中性后处理卷(Tonemapping=None / EV0 / 对比=0 / 饱和=0 / Bloom 强度=0), 与走廊的
    //      PP_SC_Corridor_Pragmata 同口径 —— 调色一律默认, 亮度只在光源/材质上做(工作区纪律)。
    //   ④ 发光体保持"几何面片 + URP Unlit HDR"不动(走廊换装脚本的做法: 名字以 Emit 结尾的材质跳过)。
    //
    // 与走廊取证脚本 NprSceneShoot.CorridorNprSwapShoot 的唯一区别: 那条是"拍完还原"的非破坏取证,
    // 本脚本**把 NPR 材质落成资产并存进场景** —— 因为 project-3 的定位就是一个"打开即 NPR"的城市沙盒。
    public static class HstCityNprBuilder
    {
        const string FBX = "Assets/Art/Environment/HST_Kit/Models/HST_Prags/SM_City_Rooftops_v1.fbx";
        const string HUMAN_FBX = "Assets/Art/Environment/HST_Kit/Models/HST_Prags/SM_Human_Scale175_v1.fbx";
        const string TXT = "Assets/Art/Environment/HST_Kit/Textures/HST_Prags";
        const string MAT = "Assets/Art/Environment/HST_Kit/Materials/HST_Prags";
        // 两条材质路线共用同一套摆场/机位/照明/出图代码, 只换材质 —— 这样"哪个好看"的比较才是干净的
        //   lit = URP Lit(走廊主场景自己的材质族, 也是工程里"一直很亮"的那条)
        //   npr = OurFunction/NPR/Deferred Toon(走廊做过的那次"换装")
        static bool LitMode;
        static string ScenePath => LitMode ? "Assets/Art/Environment/HST_Kit/Scenes/SC_City_Lit.unity"
                                           : "Assets/Art/Environment/HST_Kit/Scenes/SC_City_NPR.unity";
        static string OutDir => LitMode ? @"F:/VSCode/工业级渲染/renders/city_lit"
                                        : @"F:/VSCode/工业级渲染/renders/city_npr";
        static string Tag => LitMode ? "lit" : "npr";
        const string MANIFEST = "Assets/Art/Environment/HST_Kit/Manifests/HST_Prags/manifests/manifest_city.json";
        const string SPEC_JSON = "Assets/Art/Environment/HST_Kit/Manifests/HST_Prags/manifests/spec_bld.json";
        const string NPR_SHADER = "OurFunction/NPR/Deferred Toon";
        const float SIZE_TOL = 0.005f;   // 5mm

        static readonly Vector3 HUMAN_POS = new Vector3(-1.8f, 0f, 8.6f);

        // ---- 轴向(实测, 别照抄"Blender -Y 正面 → Unity +Z 正面"的经验说法) ----
        // 本工程 FBX 往返的映射是 **Unity(x, y, z) = Blender(x, z, y)** —— 即 Blender +Y → Unity +Z。
        // 判据(实测): 假人脚尖在 Blender -Y, 进 Unity 后 mesh 局部 z ∈ [-0.00189, +0.00123] 与
        // Blender y ∈ [-0.189, +0.123] **同号**。⇒ kit 的临街面(Blender -Y)朝 Unity **-Z**,
        // 要它对着 +z 的机位群, 摆放 yaw 必须 +180°(漏了的表现是"门/雨棚/假人全在楼背面")。
        const float STREET_YAW = 180f;

        static readonly (string name, Vector3 pos, float yaw)[] LAYOUT = {
            ("SM_BLD_TowerA_v1", new Vector3(0, 0, 0), STREET_YAW),
            ("SM_BLD_SlabB_v1", new Vector3(44, 0, 30), 90 + STREET_YAW),
            ("SM_BLD_CylC_v1", new Vector3(-36, 0, -22), STREET_YAW),
            ("SM_BLD_StepD_v1", new Vector3(36, 0, -30), STREET_YAW),
            ("SM_BLD_Podium_v1", new Vector3(-20, 0, 24), STREET_YAW),
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

        [MenuItem("HST/City NPR 场景(建+验+出图)")]
        public static void BuildCityNprMenu() { BuildCityNpr(); }

        /// <summary>一次跑两条材质路线: SC_City_Lit(URP Lit, 推荐) + SC_City_NPR(走廊的 Deferred Toon 换装)。
        /// 除材质 shader 以外, 摆场/照明/机位/后处理卷/出图流程完全同源 —— 便于同机位对拍。</summary>
        public static void BuildBoth()
        {
            BuildOne(false);
            BuildOne(true);
            Debug.Log("[CITY-NPR] 两条路线都跑完(lit + npr)");
        }

        public static void BuildCityNpr() { BuildOne(false); }
        public static void BuildCityLit() { BuildOne(true); }

        static void BuildOne(bool lit)
        {
            LitMode = lit;
            string tg = Tag;
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            UnityEngine.SceneManagement.Scene city = default;
            try
            {
                EnsureDeferred();
                ConfigureImporters();
                var mats = BuildMaterials();
                var prof = BuildVolumeProfile();
                city = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var root = new GameObject($"CITY_{tg.ToUpper()}_ROOT");
                BuildLighting();
                BuildGround(root);
                BuildVolume(prof);
                var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FBX);
                if (fbx == null) { Debug.LogError("[CITY-NPR] 找不到 FBX: " + FBX); return; }
                string manifest = File.Exists(MANIFEST) ? File.ReadAllText(MANIFEST) : null;
                int placed = PlaceBuildings(root, fbx, mats, manifest);
                PlaceSkyline(root, fbx, mats, manifest);
                PlaceScaleRef(root, mats);
                Diag(root, mats);
                VerifyAgainstManifest(root);
                var cams = BuildCameras();
                // 机位必须在**存盘之前**建, 否则打开场景看不到任何机位(走廊那条线实测踩过)
                bool ok = EditorSceneManager.SaveScene(city, ScenePath);
                AssetDatabase.SaveAssets();
                Debug.Log($"[CITY-NPR] [{tg}] 场景已存={ok} {ScenePath}, 主楼 {placed} 件 + 天际线 {SKYLINE.Length} 件 + {cams.Count} 台机位");
                ShootAll(cams, mats);
                Debug.Log($"[CITY-NPR] [{tg}] 收尾完成。结果: OK");
            }
            catch (Exception e)
            {
                Debug.LogError($"[CITY-NPR] [{tg}] 异常: " + e);
            }
        }

        // ==================================================== 渲染路径闸门(本轮最贵的一个坑)
        /// <summary>★ 本工程"走廊那套渲染技巧"有一半挂在 **Deferred** 上:
        ///   NPR 光照特性(NPRLightingFeature.cs 里 `ActualMode != RenderingMode.Deferred` 直接 return)、
        ///   延迟描边、SSR、GTAO —— 全是 Deferred-only。
        /// 而 URP **不支持 MSAA + Deferred**: URP-Asset 的 `m_MSAA: 1`(2x) 会让渲染器静默退回 Forward,
        /// 于是上面这些特性全部"装着但没跑"(实测证据: 关掉 NPR 光照特性后整张图 mean 0.0979→0.0984,
        /// 即那条全屏合成根本没碰过像素; 材质于是走 shader 的 ForwardLit "预览"分支 —— 那个分支
        /// **没有环境反射、不吃主光分带**, 只剩 albedo×平环境光, 于是白天的城市一片发蓝黑)。
        /// 修法: 把 MSAA 关回 1, 让渲染器真的进 Deferred。只在 project-3 生效。</summary>
        static void EnsureDeferred()
        {
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp == null) { Debug.LogWarning("[CITY-NPR] 取不到 URP 资产, 跳过渲染路径闸门"); return; }
            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/URP-Renderer.asset");
            int mode = -1;
            if (rd != null)
            {
                var so = new SerializedObject(rd);
                var p = so.FindProperty("m_RenderingMode");
                if (p != null)
                {
                    mode = p.intValue;
                    if (p.intValue != 1)   // 1 = Deferred, 与 URP-Renderer 里 9 个特性的前提一致
                    {
                        p.intValue = 1;
                        so.ApplyModifiedProperties();
                        EditorUtility.SetDirty(rd);
                        AssetDatabase.SaveAssets();
                        Debug.Log("[CITY-NPR] Renderer.m_RenderingMode 0→1(Deferred)");
                    }
                }
            }
            Debug.Log($"[CITY-NPR] 渲染路径闸门: URP 资产 MSAA={urp.msaaSampleCount}, Renderer.m_RenderingMode={mode}(0=Forward,1=Deferred)");
            if (urp.msaaSampleCount > 1)
            {
                Debug.LogWarning($"[CITY-NPR] MSAA={urp.msaaSampleCount} 与 Deferred 不能共存 → URP 会退回 Forward, " +
                                 "本工程的 NPR 光照/描边/SSR/GTAO 全部失效。关到 1。");
                urp.msaaSampleCount = 1;
                EditorUtility.SetDirty(urp);
                AssetDatabase.SaveAssets();
                Debug.Log($"[CITY-NPR] MSAA 已改为 {urp.msaaSampleCount}");
            }
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
                if (!ti.mipmapEnabled) { ti.mipmapEnabled = true; ch = true; }
                if (ch) ti.SaveAndReimport();
            }
            foreach (var f in new[] { FBX, HUMAN_FBX })
            {
                var fi = AssetImporter.GetAtPath(f) as ModelImporter;
                if (fi == null) continue;
                bool ch = false;
                if (fi.materialImportMode != ModelImporterMaterialImportMode.None)
                { fi.materialImportMode = ModelImporterMaterialImportMode.None; ch = true; }
                // 轴转换: 不开会把 Blender 的 Z-up 直接当 Unity 的 Z-up → 楼躺倒(实测轴序 24/24/14)
                if (!fi.bakeAxisConversion) { fi.bakeAxisConversion = true; ch = true; }
                if (ch) fi.SaveAndReimport();
            }
            Debug.Log("[CITY-NPR] 导入设置完成(贴图 sRGB/Repeat/2048, FBX 不导材质)");
        }

        // ==================================================== 材质
        class Mats
        {
            public Material facadeDay, facadeDusk, roof, panel, dark, ground, scaleRef;
            public Material winWarm, winCool, winNeut, accent;
            public Dictionary<Material, Material> nprToLit = new Dictionary<Material, Material>();
        }
        static readonly string[] KW_COMMON = { "_NORMALMAP", "_METALLICSPECGLOSSMAP",
                                               "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A" };

        static Mats BuildMaterials()
        {
            var m = new Mats();
            // ① URP Lit 底材: 优先**复用场景里已有的**(project-3 从 project-0 同步过来了, 关键字
            //    已在磁盘上打好); 缺失才新建。用它当 NPR 的"源", 属性名逐一对得上。
            var litDay = EnsureLit("M_BLD_FacadeDay", TXT + "/T_BLD_Facade_White_Day", 1.6f);
            var litDusk = EnsureLit("M_BLD_FacadeDusk", TXT + "/T_BLD_Facade_White_Dusk", 3.2f);
            var litRoof = EnsureLit("M_BLD_Roof", TXT + "/T_HS_FloorPlate_White", 0f);
            var litPanel = EnsureLit("M_BLD_Panel", TXT + "/T_HS_PanelWall_White", 0f);
            var litDark = EnsureLit("M_BLD_Dark", TXT + "/T_HS_DarkAccent_Black", 0f);
            var litGround = EnsureSolidLit("M_BLD_Ground", new Color(0.155f, 0.165f, 0.175f, 1f), 0.35f);
            var litScale = EnsureSolidLit("M_ScaleRef_Neutral", new Color(0.72f, 0.72f, 0.74f, 1f), 0.30f);
            if (LitMode)
            {
                // ---- lit 路线: 直接用 URP Lit(走廊主场景自己的材质族) ----
                m.facadeDay = litDay; m.facadeDusk = litDusk; m.roof = litRoof;
                m.panel = litPanel; m.dark = litDark; m.ground = litGround; m.scaleRef = litScale;
                Debug.Log($"[CITY-NPR] [lit] 材质: URP Lit x7(立面日/暮/屋顶/白板/黑件/地面/标尺)");
            }
            else
            {
                // ---- npr 路线: 走廊那次"换装"的落资产版 ----
                // 换装口径 = 贴图/颜色/金属光滑度直译, 卡通参数留母材质默认(见 MakeNpr 的注释)
                m.facadeDay = MakeNpr("M_BLD_NPR_FacadeDay", litDay);
                m.facadeDusk = MakeNpr("M_BLD_NPR_FacadeDusk", litDusk);
                m.roof = MakeNpr("M_BLD_NPR_Roof", litRoof);
                m.panel = MakeNpr("M_BLD_NPR_Panel", litPanel);
                m.dark = MakeNpr("M_BLD_NPR_Dark", litDark);
                m.nprToLit[m.facadeDay] = litDay; m.nprToLit[m.facadeDusk] = litDusk;
                m.nprToLit[m.roof] = litRoof; m.nprToLit[m.panel] = litPanel; m.nprToLit[m.dark] = litDark;
                m.ground = MakeNpr("M_BLD_NPR_Ground", litGround, 1.15f);
                m.nprToLit[m.ground] = litGround;
                m.scaleRef = MakeNpr("M_BLD_NPR_ScaleRef", litScale, 1.0f);
                m.nprToLit[m.scaleRef] = litScale;
            }
            // ③ 亮窗/发光带: **URP Unlit + HDR _BaseColor**, 不做 NPR(走廊口径: 发光体保持原样)
            m.winWarm = EnsureUnlit("M_BLD_Emit_AWarm", new Color(1.00f, 0.72f, 0.42f) * 0.90f);
            m.winCool = EnsureUnlit("M_BLD_Emit_BCool", new Color(0.55f, 0.72f, 1.00f) * 0.85f);
            m.winNeut = EnsureUnlit("M_BLD_Emit_CNeut", new Color(0.95f, 0.93f, 0.88f) * 0.85f);
            m.accent = EnsureUnlit("M_BLD_Emit_DAccent", new Color(0.039f, 0.557f, 0.890f) * 0.95f);
            AssetDatabase.SaveAssets();
            return m;
        }

        /// <summary>复用已有 URP Lit 材质; 缺失才按立面套件新建并打关键字。</summary>
        static Material EnsureLit(string name, string folder, float emis)
        {
            string mp = MAT + "/" + name + ".mat";
            var mm = AssetDatabase.LoadAssetAtPath<Material>(mp);
            if (mm != null) return mm;
            mm = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
            string set = Path.GetFileName(folder);
            mm.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/{set}_BC.png"));
            mm.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/{set}_N.png"));
            mm.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/{set}_MS.png"));
            mm.SetTexture("_EmissionMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/{set}_E.png"));
            mm.SetFloat("_Smoothness", 1f);
            mm.SetFloat("_SmoothnessTextureChannel", 1f);
            mm.SetFloat("_Metallic", 1f);
            mm.SetFloat("_OcclusionStrength", 0f);
            if (emis > 0)
            {
                mm.SetColor("_EmissionColor", new Color(0.92f, 0.94f, 1f) * emis);
                mm.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            }
            var kw = new List<string>(KW_COMMON);
            if (emis > 0) kw.Add("_EMISSION");
            foreach (var k in kw) mm.EnableKeyword(k);
            AssetDatabase.CreateAsset(mm, mp);
            PatchKeywordsOnDisk(mp, kw, null);
            return AssetDatabase.LoadAssetAtPath<Material>(mp);
        }

        static Material EnsureSolidLit(string name, Color col, float smooth)
        {
            string mp = MAT + "/" + name + ".mat";
            var mm = AssetDatabase.LoadAssetAtPath<Material>(mp);
            if (mm == null)
            {
                mm = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
                AssetDatabase.CreateAsset(mm, mp);
            }
            mm.SetColor("_BaseColor", col);
            mm.SetFloat("_Smoothness", smooth);
            mm.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(mm);
            AssetDatabase.SaveAssets();
            EditorUtility.ClearDirty(mm);
            return mm;
        }

        static Material EnsureUnlit(string name, Color hdr)
        {
            string mp = MAT + "/" + name + ".mat";
            var mm = AssetDatabase.LoadAssetAtPath<Material>(mp);
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (mm == null) { mm = new Material(sh) { name = name }; AssetDatabase.CreateAsset(mm, mp); }
            mm.shader = sh;
            mm.SetTexture("_BaseMap", null);
            mm.SetColor("_BaseColor", hdr);     // HDR >1 → 吃 TA 性能 Bloom
            mm.SetFloat("_Surface", 0f);        // Opaque
            mm.SetFloat("_ZWrite", 1f);
            mm.SetFloat("_Cull", 0f);           // 双面: 面片进出视角都可见
            EditorUtility.SetDirty(mm);
            AssetDatabase.SaveAssets();
            EditorUtility.ClearDirty(mm);
            return AssetDatabase.LoadAssetAtPath<Material>(mp);
        }

        /// <summary>走廊同款换装: **拷贝 Lit 材质再换 shader**。
        /// 为什么是拷贝而不是 new Material(nprShader): 拷贝会把源材质的 m_ValidKeywords
        /// (BC/N/MS 三个)一起带过来, 而属性名两者完全一致; 从零 new 再 SetTexture 不会自动开
        /// 关键字, 且这套关键字"内存开、落盘丢"在本工程实测过(见 README 已修坑)。
        /// boost: _BaseColor 亮度旋钮(1.0 = 照搬)。</summary>
        static Material MakeNpr(string name, Material lit, float boost = 1.0f)
        {
            var sh = Shader.Find(NPR_SHADER);
            if (sh == null) { Debug.LogError("[CITY-NPR] 找不到 shader: " + NPR_SHADER); return lit; }
            string mp = MAT + "/" + name + ".mat";
            var old = AssetDatabase.LoadAssetAtPath<Material>(mp);
            if (old != null) AssetDatabase.DeleteAsset(mp);
            var mm = new Material(lit) { name = name };
            mm.shader = sh;
            var baseCol = lit.HasProperty("_BaseColor") ? lit.GetColor("_BaseColor") : Color.white;
            mm.SetColor("_BaseColor", new Color(baseCol.r * boost, baseCol.g * boost, baseCol.b * boost, 1f));
            // ★ 金属度必须归零 —— 这是"城市换 NPR 会不会整栋发黑"的开关。
            // 走廊 NPRLighting.shader 的漫反射是 `diffuseColor = gb0.rgb*(1-metallic)`, 而我们的
            // 幕墙材质本就带 `_Metallic=1`(URP Lit 下靠环境反射取亮, 是玻璃的正确做法); 原样搬进
            // toon 管线 ⇒ 漫反射被 ×0, 只剩 0.1 强度的卡通高光 ⇒ **整城发蓝黑, 且两个朝向的亮度
            // 一模一样**(实测: 塔正面 0.1222 / 侧面 0.1209 —— "两面同亮度"就是"漫反射死了"的指纹)。
            // 归零后由反照率直接受光, 卡通分带才成立。光滑度仍在(走 _SmoothnessTextureChannel=1
            // 的 BC.a 通道), 只是不再有金属镜面。
            mm.SetFloat("_Metallic", 0f);
            // 卡通参数 = 走廊换装的母材质默认值。注意分带的输入是 **half-Lambert**
            // (NPRLighting.shader: `halfLambert = dot(N,L)*.5+.5`), 不是裸 dot ——
            // 所以 0.65 对应的是 dot≈0.30: 竖直幕墙 dot 落在 0.3 以上就进亮带。
            mm.SetColor("_ShadowColor", new Color(0.38f, 0.46f, 0.64f, 1f));
            mm.SetFloat("_ToonThreshold", 0.65f);
            mm.SetFloat("_ToonSoftness", 0.035f);
            mm.SetFloat("_ToonSpecular", 0.1f);
            mm.SetFloat("_ShadowPattern", 0f);
            mm.SetFloat("_Hatching", 0f);
            // 自身不发光: 亮窗是独立几何面片, 立面再发光会与之叠亮
            mm.SetFloat("_EmissionEnabled", 0f);
            mm.SetFloat("_EmissionUseMap", 0f);
            mm.SetTexture("_EmissionMap", null);
            mm.SetColor("_EmissionColor", Color.black);
            mm.DisableKeyword("_EMISSION");
            AssetDatabase.CreateAsset(mm, mp);
            PatchKeywordsOnDisk(mp, new List<string>(KW_COMMON), new List<string> { "_EMISSION" });
            var m2 = AssetDatabase.LoadAssetAtPath<Material>(mp);
            if (m2 == null) { Debug.LogError("[CITY-NPR] 材质落盘失败: " + mp); return mm; }
            EditorUtility.ClearDirty(m2);
            Debug.Log($"[CITY-NPR] {name}: shader={m2.shader.name} " +
                      $"BC={(m2.GetTexture("_BaseMap") != null)} N={(m2.GetTexture("_BumpMap") != null)} " +
                      $"MS={(m2.GetTexture("_MetallicGlossMap") != null)} " +
                      $"kw=[{string.Join(",", m2.shaderKeywords)}] " +
                      $"smoothCh={m2.GetFloat("_SmoothnessTextureChannel"):F0} metallic={m2.GetFloat("_Metallic"):F1}");
            return m2;
        }

        /// <summary>.mat 是 YAML 文本; 关键字写进 m_ValidKeywords 并自 m_InvalidKeywords 移除。
        /// 路径坑: `Assets/...` 是相对路径, 批处理 CWD 不是工程根 → File.Exists 恒 false 而静默跳过。</summary>
        static void PatchKeywordsOnDisk(string path, List<string> add, List<string> remove)
        {
            string abs = Path.IsPathRooted(path)
                ? path : Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
            if (!File.Exists(abs)) { Debug.LogWarning("[CITY-NPR] 文件不存在: " + abs); return; }
            var txt = File.ReadAllText(abs);
            var rxV = new Regex(@"m_ValidKeywords:(\s*\[\])?(?<v>(\r?\n  - [^\r\n]+)*)");
            var rxI = new Regex(@"m_InvalidKeywords:(\s*\[\])?(?<v>(\r?\n  - [^\r\n]+)*)");
            var mv = rxV.Match(txt);
            var mi = rxI.Match(txt);
            if (!mv.Success) { Debug.LogWarning("[CITY-NPR] 无 m_ValidKeywords: " + path); return; }
            string v = mv.Groups["v"].Value;
            string iv = mi.Success ? mi.Groups["v"].Value : "";
            bool ch = false;
            if (add != null)
                foreach (var k in add)
                {
                    if (v.Contains("\n  - " + k)) continue;
                    v = "\n  - " + k + v;
                    iv = iv.Replace("\n  - " + k, "");
                    ch = true;
                }
            if (remove != null)
                foreach (var k in remove)
                    if (v.Contains("\n  - " + k)) { v = v.Replace("\n  - " + k, ""); ch = true; }
            if (!ch) return;
            txt = txt.Remove(mv.Index, mv.Length).Insert(mv.Index, "m_ValidKeywords:" + v);
            var mi2 = rxI.Match(txt);
            if (mi2.Success)
                txt = txt.Remove(mi2.Index, mi2.Length).Insert(mi2.Index, "m_InvalidKeywords:" + iv);
            File.WriteAllText(abs, txt);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        // ==================================================== 后处理卷(中性; 走廊同口径)
        static VolumeProfile BuildVolumeProfile()
        {
            string pp = MAT + "/PP_SC_City_NPR.asset";
            var prof = AssetDatabase.LoadAssetAtPath<VolumeProfile>(pp);
            if (prof == null)
            {
                prof = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(prof, pp);
            }
            for (int i = prof.components.Count - 1; i >= 0; i--)
            {
                var c = prof.components[i];
                prof.components.RemoveAt(i);
                UnityEngine.Object.DestroyImmediate(c, true);
            }
            var tm = prof.Add<Tonemapping>(true);
            tm.mode.overrideState = true; tm.mode.value = TonemappingMode.None;
            var ca = prof.Add<ColorAdjustments>(true);
            ca.postExposure.overrideState = true; ca.postExposure.value = 0f;
            ca.contrast.overrideState = true; ca.contrast.value = 0f;
            ca.saturation.overrideState = true; ca.saturation.value = 0f;
            ca.hueShift.overrideState = true; ca.hueShift.value = 0f;
            ca.colorFilter.overrideState = true; ca.colorFilter.value = Color.white;
            var bl = prof.Add<Bloom>(true);
            bl.threshold.overrideState = true; bl.threshold.value = 0.9f;
            bl.intensity.overrideState = true; bl.intensity.value = 0f;   // URP 卷里的 Bloom 关; 辉光交给 TA 性能 Bloom 特性
            EditorUtility.SetDirty(prof); AssetDatabase.SaveAssets();
            Debug.Log($"[CITY-NPR] 后处理卷 {pp}: {prof.components.Count} 个分量(tonemap=None, EV0, 对比/饱和=0, bloom=0)");
            return prof;
        }

        static void BuildVolume(VolumeProfile prof)
        {
            var vgo = new GameObject("GLOBAL_NPR_VOLUME");
            var vol = vgo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 1f;
            vol.weight = 1f;
            vol.sharedProfile = prof;
        }

        // ==================================================== 照明
        static void BuildLighting()
        {
            var sunGo = new GameObject("L_Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1.0f, 0.975f, 0.93f);
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft;
            // 太阳方向按"卡通的判据"选, 不是按物理好看选:
            //   卡通分带 = dot(N, L) 与阈值的比较, 而**竖直幕墙的 dot 上限就是太阳的水平分量**。
            //   相机群在 +x/+z 象限, 楼朝相机的两个面是 +x 与 +z。要让"临街正面"落进亮带,
            //   太阳得**主要朝 +z 打**(L_toward≈(0.28,0.60,0.75)): 正面 dot≈0.75 亮, 侧面 dot≈0.28
            //   暗 —— 一栋楼两个面一明一暗, 正是卡通楼该有的读法。
            //   首轮把太阳均分给 +x/+z(各 0.5)时两面**同时**落进暗带, 整城发蓝黑(实测 mean 0.107
            //   对 URP Lit 0.172), 这就是方向写不对的症状。
            sunGo.transform.rotation = Quaternion.LookRotation(new Vector3(-0.28f, -0.60f, -0.75f).normalized);
            // Flat 环境光: 卡通要的是"受光面亮/背光面一档暗", 环境光只给暗面兜底, 别用天光把分带糊掉
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.46f, 0.51f, 0.59f);
            RenderSettings.fog = false;
            // 楼梯核心筒内一盏检修灯(楼梯间本来就该有灯; 内腔在室外光下几乎没有照度,
            // 没有它那张"看楼梯"的机位会整张糊黑 —— 实测 mean 0.033 / 73.9% 像素 <0.02)
            var coreLamp = new GameObject("L_CoreStairwell");
            coreLamp.transform.position = new Vector3(24f, 2.2f, 20f);
            var cl = coreLamp.AddComponent<Light>();
            cl.type = LightType.Point;
            cl.color = new Color(1.0f, 0.88f, 0.72f);
            cl.intensity = 6f;
            cl.range = 9f;
            cl.shadows = LightShadows.None;
            // 天空盒只服务反射(相机 clearFlags=SolidColor, 不会把天空画出来)
            var skySh = Shader.Find("Skybox/Procedural");
            if (skySh != null)
            {
                string sp = MAT + "/M_SKY_CityProcedural.mat";
                var sky = AssetDatabase.LoadAssetAtPath<Material>(sp);
                if (sky == null)
                {
                    sky = new Material(skySh) { name = "M_SKY_CityProcedural" };
                    AssetDatabase.CreateAsset(sky, sp);
                }
                sky.shader = skySh;
                sky.SetColor("_SkyTint", new Color(0.42f, 0.53f, 0.68f));
                sky.SetColor("_GroundColor", new Color(0.26f, 0.27f, 0.29f));
                sky.SetFloat("_AtmosphereThickness", 0.75f);
                sky.SetFloat("_Exposure", 1.15f);
                sky.SetFloat("_SunSize", 0.04f);
                sky.SetFloat("_SunDisk", 0f);
                EditorUtility.SetDirty(sky); AssetDatabase.SaveAssets(); EditorUtility.ClearDirty(sky);
                RenderSettings.skybox = sky;
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
                RenderSettings.reflectionIntensity = 1.0f;
            }
        }

        static void BuildGround(GameObject root)
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "CITY_NPR_Ground";
            ground.transform.SetParent(root.transform);
            ground.transform.localScale = new Vector3(80, 1, 80);   // 800m
            ground.GetComponent<MeshRenderer>().sharedMaterial =
                AssetDatabase.LoadAssetAtPath<Material>(MAT + "/M_BLD_NPR_Ground.mat");
            ground.isStatic = true;
        }

        // ==================================================== 摆场
        static int PlaceBuildings(GameObject root, GameObject fbx, Mats mats, string manifest)
        {
            int n = 0;
            foreach (var (name, pos, yaw) in LAYOUT)
            {
                var src = FindChild(fbx, name);
                if (src == null) { Debug.LogError("[CITY-NPR] FBX 缺少 " + name); continue; }
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

        static void PlaceScaleRef(GameObject root, Mats mats)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(HUMAN_FBX);
            if (fbx == null) { Debug.LogError("[CITY-NPR] 找不到假人 FBX: " + HUMAN_FBX); return; }
            var src = FindChild(fbx, "SM_Human_Scale175_v1") ?? fbx.transform;
            var go = UnityEngine.Object.Instantiate(src.gameObject, HUMAN_POS,
                                                    Quaternion.Euler(0, STREET_YAW, 0), root.transform);
            go.name = "SM_Human_Scale175_v1_placed";
            var r = go.GetComponentInChildren<MeshRenderer>();
            if (r != null)
            {
                var arr = r.sharedMaterials;
                if (arr.Length == 0) arr = new[] { mats.scaleRef };
                else for (int i = 0; i < arr.Length; i++) arr[i] = mats.scaleRef;
                r.sharedMaterials = arr;
            }
        }

        /// <summary>把该建筑的日/暮亮窗对象挂到主体下, 按变体切显隐(白天只开 WinDay)。
        /// 亮窗 = 几何面片 + URP Unlit HDR —— 走廊口径, 换装 NPR 时**不动**它。</summary>
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
                // 远景楼提亮一档: 玻璃幕墙在远处被 mipmap 平均成深色会读成"黑块" ——
                // 用 _BaseColor 当亮度旋钮(实例材质, 不改资产)
                var r = go.GetComponentInChildren<MeshRenderer>();
                if (r != null && r.sharedMaterials.Length > 0 && r.sharedMaterials[0] != null)
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

        /// <summary>按 **manifest 记录的真实槽序**接线(FBX 材质表只写"被用到的槽", 顺序是该槽
        /// 多边形首次出现的先后, 每个对象都可能不同; 按固定槽序接会把核心筒的墙接上玻璃幕墙)。</summary>
        static void WireMaterials(GameObject go, Material facade, Mats mats, string[] slots)
        {
            var r = go.GetComponentInChildren<MeshRenderer>();
            if (r == null) return;
            int n = Mathf.Max(1, r.sharedMaterials.Length);
            var arr = new Material[n];
            if (slots == null || slots.Length == 0)
            {
                Debug.LogWarning($"[CITY-NPR] {go.name} 无 manifest 槽序, 退回固定约定");
                for (int i = 0; i < n; i++) arr[i] = FromSlotName(null, i, facade, mats);
            }
            else
                for (int i = 0; i < n; i++)
                    arr[i] = FromSlotName(i < slots.Length ? slots[i] : null, i, facade, mats);
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
            var mb = mf.sharedMesh.bounds;
            var ms = mf.transform.lossyScale;
            var gs = go.transform.lossyScale;
            var bc = go.AddComponent<BoxCollider>();
            bc.center = go.transform.InverseTransformPoint(mf.transform.TransformPoint(mb.center));
            bc.size = new Vector3(
                mb.size.x * Mathf.Abs(ms.x) / Mathf.Max(Mathf.Abs(gs.x), 1e-6f),
                mb.size.y * Mathf.Abs(ms.y) / Mathf.Max(Mathf.Abs(gs.y), 1e-6f),
                mb.size.z * Mathf.Abs(ms.z) / Mathf.Max(Mathf.Abs(gs.z), 1e-6f));
        }

        // ==================================================== 诊断
        static void Diag(GameObject root, Mats mats)
        {
            // 渲染器特性快照: 这张清单就是"走廊那套技巧"的全局部分, 出图前先确认它们真在
            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/URP-Renderer.asset");
            if (rd != null)
            {
                var on = rd.rendererFeatures.Where(f => f != null && f.isActive).Select(f => f.name).ToArray();
                Debug.Log($"[CITY-NPR] 渲染器特性 {on.Length}/{rd.rendererFeatures.Count} 开: [{string.Join(", ", on)}]");
            }
            // ★ 实测渲染路径: NPR 光照特性只在 `renderingModeActual == Deferred` 时入队
            //   (NPRLightingFeature.cs 里那句 return), 而 renderingModeActual 是**私有**属性 ——
            //   资产里写 Deferred 不等于运行时就是 Deferred(URP 会因平台/特性/设置静默退回 Forward)。
            //   机位建好后在 ShootAll 里实测(这里还没相机)。
            foreach (var mn in (LitMode ? new[] { "M_BLD_FacadeDay", "M_BLD_Roof", "M_BLD_Panel", "M_BLD_Dark" }
                                            : new[] { "M_BLD_NPR_FacadeDay", "M_BLD_NPR_Roof", "M_BLD_NPR_Panel", "M_BLD_NPR_Dark" }))
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(MAT + "/" + mn + ".mat");
                if (m == null) { Debug.LogError("[CITY-NPR-DIAG] 缺材质 " + mn); continue; }
                // 属性名要按 shader 存在与否取 —— 对 URP Lit 取 `_ToonThreshold` 会打一串
                // "Material doesn't have a float property" 报错刷屏(实测), 用 HasProperty 兜住
                string toon = m.HasProperty("_ToonThreshold")
                    ? $"toonThr={m.GetFloat("_ToonThreshold"):F2} shadow={m.GetColor("_ShadowColor")} " +
                      $"outline={m.GetFloat("_OutlineEnabled"):F0}"
                    : "toon=(n/a)";
                Debug.Log($"[CITY-NPR-DIAG] {mn}: shader={m.shader.name} " +
                          $"BC={(m.GetTexture("_BaseMap") != null)} N={(m.GetTexture("_BumpMap") != null)} " +
                          $"MS={(m.GetTexture("_MetallicGlossMap") != null)} kw=[{string.Join(",", m.shaderKeywords)}] " +
                          toon);
            }
            var t = FindChildByPrefix(root, "SM_BLD_TowerA");
            if (t != null)
            {
                var mr = t.GetComponentInChildren<MeshRenderer>();
                var mm = mr.sharedMaterials;
                var names = string.Join(" | ", mm.Select(x => x == null ? "null" : x.name));
                Debug.Log($"[CITY-NPR-DIAG] TowerA: mats=[{names}] worldBounds={mr.bounds.size} " +
                          $"lossyScale={t.lossyScale}");
            }
            PrintScaleGates(root);
        }

        // ==================================================== 比例门(动作三)
        static void PrintScaleGates(GameObject root)
        {
            if (!File.Exists(SPEC_JSON))
            { Debug.LogWarning("[CITY-NPR-SCALE] 无 " + SPEC_JSON + ", 跳过比例门"); return; }
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
                Debug.Log($"[CITY-NPR-SCALE] 比例门 {label}÷身高({H:F3}m) = {ratio:F3} " +
                          $"(目标 {target:F2}±{tol:F3}) {(ok ? "OK" : "FAIL")}");
            }
            var sc = FindChildByPrefix(root, "SM_Human_Scale175");
            if (sc != null)
            {
                var mr = sc.GetComponentInChildren<MeshRenderer>();
                if (mr != null)
                    Debug.Log($"[CITY-NPR-SCALE] 假人实测高 {mr.bounds.size.y:F3}m (spec {H:F3}m, " +
                              $"偏差 {(mr.bounds.size.y - H) * 1000f:F1}mm)");
            }
            Debug.Log($"[CITY-NPR-SCALE] 比例门 共 {rows.Length} 项, 未过 {bad} 项");
        }

        static float SpecF(string json, string key)
        {
            var m = new Regex("\"" + key + "\"\\s*:\\s*(-?[0-9][0-9.eE+-]*)").Match(json);
            if (!m.Success) { Debug.LogWarning("[CITY-NPR-SCALE] spec 里找不到键 " + key); return 0f; }
            return float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>把渲染器**运行时**的路径打出来(反射读私有成员)。
        /// 资产里写 Deferred ≠ 运行时是 Deferred —— URP 会因平台/特性/设置静默退回 Forward,
        /// 而 NPR 光照/延迟描边/SSR/GTAO 全是 Deferred-only。少这一行就只能靠"关掉看看变不变"猜。</summary>
        static void LogActualRenderingMode()
        {
            var cam = UnityEngine.Object.FindObjectsOfType<Camera>(true).FirstOrDefault(c => c.enabled);
            if (cam == null) { Debug.LogWarning("[CITY-NPR] 没有相机, 跳过渲染路径实测"); return; }
            var ud = cam.GetUniversalAdditionalCameraData();
            var prop = typeof(UniversalAdditionalCameraData).GetProperty("scriptableRenderer",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);
            object rend = prop != null ? prop.GetValue(ud) : null;
            if (rend == null) { Debug.LogWarning("[CITY-NPR] 反射取不到 scriptableRenderer 实例"); return; }
            var t = rend.GetType();
            string info = "renderer=" + t.Name;
            foreach (var nm in new[] { "renderingModeActual", "renderingMode" })
            {
                var p2 = t.GetProperty(nm, System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (p2 != null) info += $", {nm}={p2.GetValue(rend)}";
            }
            Debug.Log("[CITY-NPR] 实测渲染路径: " + info + "  (Forward=0, Deferred=1)");
        }

        static Transform FindChildByPrefix(GameObject root, string prefix)        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith(prefix)) return t;
            return null;
        }

        // ==================================================== 验收(manifest 对拍)
        static int VerifyAgainstManifest(GameObject root)
        {
            if (!File.Exists(MANIFEST)) { Debug.LogWarning("[CITY-NPR] 无 manifest, 跳过尺寸验收"); return 0; }
            var txt = File.ReadAllText(MANIFEST);
            int fails = 0, checkedN = 0;
            foreach (var (name, pos, yaw) in LAYOUT)
            {
                var go = root.transform.Cast<Transform>()
                    .Select(t => t.gameObject).FirstOrDefault(g => g.name == name + "_placed");
                if (go == null) continue;
                var mr = go.GetComponentInChildren<MeshRenderer>();
                if (mr == null) continue;
                var sz = mr.bounds.size;   // **世界空间**包围盒(FBX 导入器把 cm→m 的 0.01 缩放放在变换上)
                var (bx, by, bz) = ReadManifestSize(txt, name);
                float dY = Mathf.Abs(sz.y - bz);
                float dXZ = Mathf.Max(
                    Mathf.Abs(Mathf.Max(sz.x, sz.z) - Mathf.Max(bx, by)),
                    Mathf.Abs(Mathf.Min(sz.x, sz.z) - Mathf.Min(bx, by)));
                float err = Mathf.Max(dY, dXZ);
                bool ok = err <= SIZE_TOL;
                if (!ok) fails++;
                checkedN++;
                Debug.Log($"[CITY-NPR-VERIFY] {name}: bl=({bx},{by},{bz}) got={sz} " +
                          $"dY={dY * 1000f:F1}mm dXZ={dXZ * 1000f:F1}mm {(ok ? "OK" : "FAIL")}");
            }
            Debug.Log($"[CITY-NPR-VERIFY] 共 {checkedN} 件, 失败 {fails} 件 (容差 {SIZE_TOL * 1000f:F0}mm)");
            return fails;
        }

        static (float, float, float) ReadManifestSize(string json, string name)
        {
            int i = json.IndexOf("\"name\": \"" + name + "\"", StringComparison.Ordinal);
            if (i < 0) return (0, 0, 0);
            int s = json.IndexOf("[", i), e = json.IndexOf("]", s);
            var parts = json.Substring(s + 1, e - s - 1).Split(',');
            return (float.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[2].Trim(), System.Globalization.CultureInfo.InvariantCulture));
        }

        // ==================================================== 出图
        static void ShootAll(Dictionary<string, Camera> cams, Mats mats)
        {
            Directory.CreateDirectory(OutDir);
            LogActualRenderingMode();
            foreach (var kv in cams)
            {
                Shot(kv.Value, $"{OutDir}/unity_{Tag}_day_{kv.Key}.png");
                Debug.Log($"[CITY-NPR-SHOT] unity_{Tag}_day_{kv.Key}.png");
            }

            // --- 暮色变体(临时换装: 立面→Dusk NPR + 亮窗切 Dusk 组 + 发光×1.7 + 太阳压暗; 拍完还原) ---
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
            // 找**方向光**而不是 FindObjectOfType<Light>() —— 场景里还有核心筒检修灯,
            // 后者返回哪一个是不确定的, 压暗错了灯的表现是"暮色里太阳还在"(静默型坑)
            var sun = UnityEngine.Object.FindObjectsOfType<Light>()
                .FirstOrDefault(l => l.type == LightType.Directional);
            float sunI = sun.intensity; Color sunC = sun.color;
            var amb = RenderSettings.ambientLight;
            sun.intensity = 0.10f; sun.color = new Color(0.62f, 0.72f, 0.95f);
            RenderSettings.ambientLight = new Color(0.075f, 0.10f, 0.16f);
            var bgSaved = new List<(Camera c, Color bg)>();
            foreach (var c in cams.Values)
            {
                bgSaved.Add((c, c.backgroundColor));
                c.backgroundColor = new Color(0.045f, 0.075f, 0.135f);
            }
            Shot(cams["02_Street"], $"{OutDir}/unity_{Tag}_dusk_02_Street.png");
            Shot(cams["01_Aerial"], $"{OutDir}/unity_{Tag}_dusk_01_Aerial.png");
            foreach (var (r, orig) in boosted) r.sharedMaterials = orig;
            foreach (var (r, orig) in swapped) r.sharedMaterials = orig;
            sun.intensity = sunI; sun.color = sunC;
            RenderSettings.ambientLight = amb;
            foreach (var (c, bg) in bgSaved) c.backgroundColor = bg;
            SetVariant(false);
            Debug.Log($"[CITY-NPR-SHOT] 暮色 2 张(立面换装 {swapped.Count} / 亮窗加档 {boosted.Count})");

            // --- Lit 对照 + 标定台: 只对 npr 路线有意义(lit 路线本来就是 Lit 材质, nprToLit 为空) ---
            if (!LitMode)
            {
                var litSwapped = new List<(Renderer r, Material[] orig)>();
                foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
                {
                    var cur = r.sharedMaterials;
                    if (cur.Length == 0) continue;
                    if (!cur.Any(x => x != null && mats.nprToLit.ContainsKey(x))) continue;
                    var nw = new Material[cur.Length];
                    for (int i = 0; i < cur.Length; i++)
                        nw[i] = (cur[i] != null && mats.nprToLit.ContainsKey(cur[i])) ? mats.nprToLit[cur[i]] : cur[i];
                    litSwapped.Add((r, cur));
                    r.sharedMaterials = nw;
                }
                Shot(cams["02_Street"], $"{OutDir}/unity_{Tag}_litcmp_02_Street.png");
                foreach (var (r, orig) in litSwapped) r.sharedMaterials = orig;
                Debug.Log($"[CITY-NPR-SHOT] Lit 对照 1 张(换回 URP Lit {litSwapped.Count} 渲染器)");

                BandRampProbe(mats);
            }
        }

        /// <summary>卡通分带标定台(诊断): 在空地上摆 5 面朝向已知的墙(法线 +z / 45° / +x / 135° / -z),
        /// 贴同一个 NPR 立面材质, 用同一盏太阳拍一张 —— 直接读出"分带落在哪个角度上"。
        /// 为什么需要它: 楼"两面同亮度"时, 是(光源方向不对)/(AO 掐掉主光)/(分带阈不对)三种原因
        /// 都会长得一样; 把朝向做成自变量, 一眼就能把三者分开。
        /// 追加"特性 A/B"(同一台机位同一材质, 逐个关渲染器特性): 用来定位"是谁把画面压暗了"。</summary>
        static void BandRampProbe(Mats mats)
        {
            var root = new GameObject("DIAG_BANDRAMP");
            var yaws = new[] { 0f, 45f, 90f, 135f, 180f };
            for (int i = 0; i < yaws.Length; i++)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);   // Quad 的默认法线 = +z
                q.name = $"RAMP_{yaws[i]:F0}";
                q.transform.SetParent(root.transform);
                q.transform.localScale = new Vector3(11, 14, 1);
                q.transform.position = new Vector3(-30 + i * 15, 12, 330);
                q.transform.rotation = Quaternion.Euler(0, yaws[i], 0);
                var mr = q.GetComponent<MeshRenderer>();
                mr.sharedMaterial = mats.facadeDay;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                var col = q.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.DestroyImmediate(col);
            }
            var cam = MakeCam("CAM_DIAG_RAMP", new Vector3(0, 12, 375), new Vector3(0, 12, 330), 50);
            Shot(cam, $"{OutDir}/unity_{Tag}_diag_bandramp_thr065.png");

            // 同机位再拍一张"阈 0.35"的: 阈若真的在起作用, 分带边界必须移动
            var probe = new Material(mats.facadeDay) { name = "DIAG_RAMP_THR035" };
            probe.SetFloat("_ToonThreshold", 0.35f);
            var walls = root.GetComponentsInChildren<MeshRenderer>();
            foreach (var w in walls) w.sharedMaterial = probe;
            Shot(cam, $"{OutDir}/unity_{Tag}_diag_bandramp_thr035.png");
            foreach (var w in walls) w.sharedMaterial = mats.facadeDay;

            // 同几何 + **URP Lit 对照材质**: 把"shader 差异"和"场景/光照差异"分开
            foreach (var w in walls) w.sharedMaterial = mats.nprToLit[mats.facadeDay];
            Shot(cam, $"{OutDir}/unity_{Tag}_diag_bandramp_lit.png");
            foreach (var w in walls) w.sharedMaterial = mats.facadeDay;

            // --- 光照阶梯: 把"太阳 vs 环境光"各自贡献量出来 -----------------------------
            // 判据用法: 立面反照率实测 0.407(线性), 太阳 1.15 + 平环境 0.5 的账面预期是 0.5 上下,
            // 而实测只有 0.10 —— 差 4~5 倍。这一组 4 张(原样 / 太阳×4 / 关环境 / 关太阳)能把
            // "光没打上去"和"打上去被吃掉"分开。
            var sun2 = UnityEngine.Object.FindObjectsOfType<Light>()
                .FirstOrDefault(l => l.type == LightType.Directional);
            float I0 = sun2.intensity; Color C0 = sun2.color;
            var amb0 = RenderSettings.ambientLight;
            sun2.intensity = I0 * 4f;
            Shot(cam, $"{OutDir}/unity_{Tag}_diag_ramp_sun4x.png");
            sun2.intensity = I0;
            RenderSettings.ambientLight = Color.black;
            Shot(cam, $"{OutDir}/unity_{Tag}_diag_ramp_noamb.png");
            sun2.intensity = 0f;
            RenderSettings.ambientLight = amb0;
            Shot(cam, $"{OutDir}/unity_{Tag}_diag_ramp_nosun.png");
            sun2.intensity = I0; sun2.color = C0;
            Debug.Log($"[CITY-NPR-SHOT] 光照阶梯 3 张(太阳 {I0:F2}×4 / 关环境 / 关太阳)");

            UnityEngine.Object.DestroyImmediate(probe);
            UnityEngine.Object.DestroyImmediate(cam.gameObject);
            UnityEngine.Object.DestroyImmediate(root);
            Debug.Log("[CITY-NPR-SHOT] 分带标定台 + 特性 A/B 完成(阈 0.65/0.35 + 关NPR光照/关GTAO/关描边)");
        }

        static Dictionary<string, Camera> BuildCameras()
        {
            var d = new Dictionary<string, Camera>();
            d["01_Aerial"] = MakeCam("CAM_01_Aerial", new Vector3(150, 118, 152), new Vector3(8, 32, 8), 40);
            d["02_Street"] = MakeCam("CAM_02_Street", new Vector3(30, 6, 46), new Vector3(-6, 16, 4), 46);
            d["03_Roof"] = MakeCam("CAM_03_Roof", new Vector3(4, 66.5f, 6), new Vector3(36, 60, -30), 50);
            d["04_WallRun"] = MakeCam("CAM_04_WallRun", new Vector3(-14, 8, 16), new Vector3(-36, 22, -22), 45);
            d["05_Entrance"] = MakeCam("CAM_05_Entrance", new Vector3(-6, 2.2f, 24), new Vector3(0, 1.7f, 9), 42);
            d["06_Core"] = MakeCam("CAM_06_Core", new Vector3(30, 13, 26), new Vector3(24.3f, 0.5f, 20.3f), 40);
            return d;
        }

        static Camera MakeCam(string name, Vector3 pos, Vector3 look, float fov)
        {
            var go = new GameObject(name);
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = fov;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.31f, 0.40f, 0.56f);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation((look - pos).normalized, Vector3.up);
            var ud = cam.GetUniversalAdditionalCameraData();
            ud.renderPostProcessing = true;   // 吃 TA 管线(TAA/GTAO/SSR/描边/性能Bloom/体积光)
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
