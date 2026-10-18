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
    // 镜之边缘风摩天楼 · Unity 侧装配(project-3, 2026-10-11)
    //
    // 用户令: "玻璃摩天大楼需要多种类型……棱角适当柔和……模仿镜之边缘的楼房质感,
    //          最终光影效果还是得以 Unity 里为准" + "Unity 得采用那几个 TA 的 NPR 母材质和管线"。
    //
    // 本脚本只做三件事: ①按 manifest 把 10 种楼摆进场并**按真实槽序**接 TA 的 NPR 母材质;
    // ②用 TA 的 9 特性管线的**默认后处理**(中性卷)出图; ③把每条判据打印成数字。
    //
    // ★ 一个必须留在案上的事实(本项目实测, 不是猜测):
    //   `Assets/Shaders/NPR/NPRBase.shader` 的 SubShader tag 写的是
    //   `"UniversalMaterialType" = "Unlit"`。URP 的 Deferred 路径**按这个 tag 决定要不要给直射光**,
    //   于是母材质只吃到间接(环境/GI): 用 5 面已知朝向的墙做标定台实测 —— 太阳强度 ×4 亮度只 +17%,
    //   关掉太阳只 -6%, 关掉环境光掉一半; 同一几何换 URP Lit 则 +z 面 0.32 / 45° 面 0.20。
    //   室外场景没有烘焙 GI ⇒ 整城发蓝黑且没有朝向分带。
    //   处置: 用母材质代码的**非破坏副本** `Assets/Shaders/NPR/NPRBase_ME_Lit.shader`(只改这一个 tag),
    //   原件一个字没动。材质仍然全部是 TA 的 NPR 母材质族, 管线仍然是 TA 那 9 个特性。
    public static class HstCityMeBuilder
    {
        const string FBX = "Assets/Art/Environment/HST_Kit/Models/HST_Prags/SM_ME_Skylines_v3.fbx";
        const string HUMAN_FBX = "Assets/Art/Environment/HST_Kit/Models/HST_Prags/SM_Human_Scale175_v1.fbx";
        const string TXT = "Assets/Art/Environment/HST_Kit/Textures/HST_Prags";
        const string MAT = "Assets/Art/Environment/HST_Kit/Materials/HST_Prags";
        // ★ 场景拆分(用户口径): 走廊 SC_Corridor_Pragmata 原地保留纯室内; 城市测试在独立新场景
        const string SCENE = "Assets/Art/Environment/HST_Kit/Scenes/SC_CityFacade_LookDev.unity";
        const string MANIFEST = "Assets/Art/Environment/HST_Kit/Manifests/HST_Prags/manifests/manifest_me.json";
        const string NPR_SRC = "Assets/Shaders/NPR/NPRBase.shader";
        const string NPR_ME = "Assets/Shaders/NPR/NPRBase_ME_Lit.shader";
        const string OUT = @"F:/VSCode/工业级渲染/renders/city_me";
        const float SIZE_TOL = 0.005f;

        static readonly Vector3 HUMAN_POS = new Vector3(24.5f, 0f, 8f);   // A1 大街东侧人行道

        // 10 种楼(与 Blender 侧 LAYOUT 同表, Unity 语义 (x, 高度0, z); v2 重排过:
        // 旧布局 Cyl(-40,22) 与 Podium(-22,30) 足印相互穿插, 假人还站在 Podium 里)。
        // blade 的立面是 WhiteRed(V-04: 旧表写死 "Night", 白天场景里插一栋夜模楼)。
        static readonly (string name, Vector3 pos, float yaw, string facade)[] LAYOUT = {
            ("SM_ME_TowerCham_v1",  new Vector3(0, 0, 0),      0,  "WhiteRed"),
            ("SM_ME_TowerCyl_v1",   new Vector3(-36, 0, -2),   0,  "WhiteRed"),
            ("SM_ME_Podium_v1",     new Vector3(-12, 0, 26),   0,  "WhiteRed"),
            ("SM_ME_TowerDS_v1",    new Vector3(14, 0, 58),    8,  "WhiteRed"),
            ("SM_ME_SlabRC_v1",     new Vector3(44, 0, 24),    24, "CoolBlue"),
            ("SM_ME_TowerStep_v1",  new Vector3(44, 0, -38),   12, "CoolBlue"),
            ("SM_ME_TowerBlade_v1", new Vector3(12, 0, -52),   40, "WhiteRed"),
            ("SM_ME_TowerEll_v1",   new Vector3(-52, 0, -34),  0,  "CoolBlue"),
            ("SM_ME_SkyTall_v1",    new Vector3(110, 0, 88),   20, "CoolBlue"),
            ("SM_ME_SkyStep_v1",    new Vector3(-118, 0, -98), 55, "CoolBlue"),
        };
        // 街网(与 Blender build_ground_and_streets 同一张表; Unity 语义 x0,z0→x1,z1 中心线)
        static readonly (string name, float x0, float z0, float x1, float z1)[] STREETS = {
            ("A1",  19, -30,  19, 44),
            ("A1b", 19,  72,  19, 140),
            ("A2", -52,  10, -52, 140),
            ("A3", -85, -18,  26, -18),
            ("A4", -85,  76,  98, 76),
            ("A5", -85, -78, 150, -78),
        };
        const float CORRIDOR_W = 12f;
        static readonly (string name, Vector3 pos, Vector3 look, float fov)[] CAMS = {
            // 与 Blender 侧 CAMS 同表(v2 重摆: 旧 05 贴墙、旧 06 距离不够、street 旧点贴 DS 外皮)
            ("01_Aerial",   new Vector3(150, 118, 152), new Vector3(6, 30, 6),    40),
            ("02_Street",   new Vector3(19, 2.2f, 24),  new Vector3(2, 8, 0),     46),
            ("03_Corner",   new Vector3(16, 14, 34),    new Vector3(-9, 16, 8),   52),
            ("04_Roof",     new Vector3(4, 74, 8),      new Vector3(36, 40, -34), 50),
            ("05_Blade",    new Vector3(100, 30, 24),   new Vector3(12, 28, -52), 45),
            ("06_Cylinder", new Vector3(-110, 18, 4),   new Vector3(-36, 22, -2), 55),
        };

        [MenuItem("HST/City ME 场景(建+验+出图)")]
        public static void BuildCityMeMenu() { BuildCityMe(); }

        public static void BuildCityMe()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            UnityEngine.SceneManagement.Scene city = default;
            try
            {
                EnsureNprTagFix();
                ConfigureImporters();
                var mats = BuildMaterials();
                var prof = BuildVolumeProfile();
                city = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var root = new GameObject("CITY_ME_ROOT");
                BuildLighting();
                BuildGround(root, mats);
                BuildVolume(prof);
                var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FBX);
                if (fbx == null) { Debug.LogError("[ME] 找不到 FBX: " + FBX); return; }
                string manifest = File.Exists(MANIFEST) ? File.ReadAllText(MANIFEST) : null;
                int placed = PlaceAll(root, fbx, mats, manifest);
                PlaceScaleRef(root, mats);
                Diag(root, mats, manifest);
                var cams = BuildCameras();
                bool ok = EditorSceneManager.SaveScene(city, SCENE);
                AssetDatabase.SaveAssets();
                Debug.Log($"[ME] 场景已存={ok} {SCENE}, {placed} 件 + {cams.Count} 台机位");
                ShootAll(cams, mats);
                Debug.Log("[ME] 收尾完成。结果: OK");
            }
            catch (Exception e)
            {
                Debug.LogError("[ME] 异常: " + e);
            }
        }

        /// <summary>把母材质代码复制一份、只改 `UniversalMaterialType` 标签(Unlit→Lit)。
        /// 为什么必须这么干: URP Deferred 按这个 tag 决定给不给直射光, 写成 Unlit 时母材质只吃
        /// 间接光 —— 室外无烘焙 GI 的城市会整片发蓝黑(标定台实测: 太阳×4 只 +17%)。
        /// **原件不动**(那是 TA 的资产); 本副本只用于本场景的材质, 并在报告里写明。</summary>
        static void EnsureNprTagFix()
        {
            string absSrc = Path.GetFullPath(Path.Combine(Application.dataPath, "..", NPR_SRC));
            string absDst = Path.GetFullPath(Path.Combine(Application.dataPath, "..", NPR_ME));
            if (!File.Exists(absSrc)) { Debug.LogError("[ME] 缺母材质源: " + absSrc); return; }
            var txt = File.ReadAllText(absSrc);
            const string from = "\"UniversalMaterialType\" = \"Unlit\"";
            const string to = "\"UniversalMaterialType\" = \"Lit\"";
            if (!txt.Contains(from))
                Debug.LogWarning("[ME] 母材质里没找到 " + from + " —— 上游可能已经改过了, 先看现状");
            txt = txt.Replace(from, to);
            txt = txt.Replace("Shader \"OurFunction/NPR/Deferred Toon\"",
                              "Shader \"OurFunction/NPR/Deferred Toon (Lit tag / ME)\"");
            bool need = !File.Exists(absDst) || File.ReadAllText(absDst) != txt;
            if (need)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(absDst));
                File.WriteAllText(absDst, txt);
                AssetDatabase.ImportAsset(NPR_ME, ImportAssetOptions.ForceUpdate);
                Debug.Log("[ME] 已写 NPR 母材质非破坏副本(只改 UniversalMaterialType→Lit): " + NPR_ME);
            }
        }

        static void ConfigureImporters()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { TXT }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var n = Path.GetFileName(p);
                if (!n.Contains("_ME_") && !n.StartsWith("T_HS_") && !n.StartsWith("T_BLD_")) continue;
                bool linear = n.EndsWith("_N.png") || n.EndsWith("_R.png") || n.EndsWith("_M.png")
                              || n.EndsWith("_AO.png") || n.EndsWith("_MS.png") || n.EndsWith("_ORM.png");
                var ti = AssetImporter.GetAtPath(p) as TextureImporter;
                if (ti == null) continue;
                bool ch = false;
                if (ti.sRGBTexture == linear) { ti.sRGBTexture = !linear; ch = true; }
                if (ti.wrapMode != TextureWrapMode.Repeat) { ti.wrapMode = TextureWrapMode.Repeat; ch = true; }
                if (ti.maxTextureSize < 2048) { ti.maxTextureSize = 2048; ch = true; }
                if (!ti.mipmapEnabled) { ti.mipmapEnabled = true; ch = true; }
                if (ti.anisoLevel < 8) { ti.anisoLevel = 8; ch = true; }   // V-07: 立面掠射摩尔纹
                if (ch) ti.SaveAndReimport();
            }
            foreach (var f in new[] { FBX, HUMAN_FBX })
            {
                var fi = AssetImporter.GetAtPath(f) as ModelImporter;
                if (fi == null) continue;
                bool ch = false;
                if (fi.materialImportMode != ModelImporterMaterialImportMode.None)
                { fi.materialImportMode = ModelImporterMaterialImportMode.None; ch = true; }
                if (!fi.bakeAxisConversion) { fi.bakeAxisConversion = true; ch = true; }
                if (ch) fi.SaveAndReimport();
            }
            Debug.Log("[ME] 导入设置完成(贴图 sRGB/linear 分流 + Repeat + 2048; FBX 不导材质)");
        }

        // ==================================================== 材质(全部 TA NPR 母材质族)
        class Mats
        {
            public Dictionary<string, Material> facade = new Dictionary<string, Material>();
            public Material roof, panel, dark, ground, scaleRef;
            public Material asphalt, walk, curb;   // V-14: 街道三层(沥青/人行道/路缘)
            public Material winWarm, winCool, winNeut;
            public Dictionary<Material, Material> nprToLit = new Dictionary<Material, Material>();
        }
        static readonly string[] KW = { "_NORMALMAP", "_METALLICSPECGLOSSMAP",
                                        "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A" };

        static Mats BuildMaterials()
        {
            var m = new Mats();
            var shader = Shader.Find("OurFunction/NPR/Deferred Toon (Lit tag / ME)")
                         ?? Shader.Find("OurFunction/NPR/Deferred Toon");
            Debug.Log($"[ME] NPR 母材质 shader = {shader.name}");
            // 三套镜之边缘立面(白红 / 冷青 / 夜)
            foreach (var v in new[] { "WhiteRed", "CoolBlue", "Night" })
                m.facade[v] = MakeToon("M_ME_Facade_" + v, TXT + "/T_ME_Facade_" + v, shader);
            m.roof = MakeToon("M_ME_Roof", TXT + "/T_HS_FloorPlate_White", shader);
            m.panel = MakeToon("M_ME_Panel", TXT + "/T_HS_PanelWall_White", shader);
            m.dark = MakeToon("M_ME_Dark", TXT + "/T_HS_DarkAccent_Black", shader);
            // 镜之边缘的街面是**浅灰混凝土**(不是城市线那种深灰沥青): 深灰地面会被高光玻璃原样反照出去,
            // 整片幕墙跟着发灰(实测街景/俯视 mean 只有 0.07~0.19 的主因)。
            m.ground = MakeToon("M_ME_Ground", null, shader,
                                new Color(0.300f, 0.310f, 0.330f, 1f), 1.0f);
            // 街道三层(V-14: 地面要有 ≥3 个明度层次)
            m.asphalt = MakeToon("M_ME_Asphalt", null, shader,
                                 new Color(0.155f, 0.160f, 0.170f, 1f), 1.0f);
            m.walk = MakeToon("M_ME_Walk", null, shader,
                              new Color(0.400f, 0.410f, 0.420f, 1f), 1.0f);
            m.curb = MakeToon("M_ME_Curb", null, shader,
                              new Color(0.500f, 0.500f, 0.500f, 1f), 1.0f);
            m.scaleRef = MakeToon("M_ME_ScaleRef", null, shader,
                                  new Color(0.72f, 0.72f, 0.74f, 1f), 1.0f);
            // (M_ME_Accent 已删 —— 2026-10-12 用户令: 出挑红环带撤除, 几何 v3 起不再有该槽;
            //  立面强调只剩贴图里的唇线+细带双细线与竖龙骨细线)
            // 亮窗 = URP Unlit + HDR(项目已验证口径: 不走 _EMISSION 关键字)
            m.winWarm = MakeUnlit("M_ME_Emit_Warm", new Color(1.00f, 0.74f, 0.46f) * 1.45f);
            m.winCool = MakeUnlit("M_ME_Emit_Cool", new Color(0.58f, 0.76f, 1.00f) * 1.5f);
            m.winNeut = MakeUnlit("M_ME_Emit_Neut", new Color(0.95f, 0.93f, 0.88f) * 1.5f);
            AssetDatabase.SaveAssets();
            return m;
        }

        /// <summary>建 TA NPR 母材质。贴图/颜色/金属光滑度直译; 卡通参数 = 母材质默认
        /// (阈 0.65 对应 half-Lambert dot≈0.30 —— NPRLighting.shader 里 band 的输入是
        /// dot(N,L)*.5+.5, 不是裸 dot, 所以别照"0.65=很亮"的直觉调)。
        /// baseOverride 给非贴图件(地面/标尺/强调带)用纯色。</summary>
        static Material MakeToon(string name, string folder, Shader shader,
                                 Color? baseOverride = null, float boost = 1.0f)
        {
            string mp = MAT + "/" + name + ".mat";
            var mm = AssetDatabase.LoadAssetAtPath<Material>(mp);
            if (mm == null) { mm = new Material(shader) { name = name }; AssetDatabase.CreateAsset(mm, mp); }
            mm.shader = shader;
            Texture2D bc = null, nrm = null, ms = null, em = null;
            if (!string.IsNullOrEmpty(folder))
            {
                string set = Path.GetFileName(folder);
                bc = AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/{set}_BC.png");
                nrm = AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/{set}_N.png");
                ms = AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/{set}_MS.png");
                em = AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/{set}_E.png");
            }
            mm.SetTexture("_BaseMap", bc);
            mm.SetTexture("_BumpMap", nrm);
            mm.SetTexture("_MetallicGlossMap", ms);
            var col = baseOverride ?? Color.white;
            mm.SetColor("_BaseColor", new Color(col.r * boost, col.g * boost, col.b * boost, 1f));
            mm.SetFloat("_Smoothness", 1f);
            mm.SetFloat("_SmoothnessTextureChannel", 1f);
            // 金属度: **有 MS 贴图的件**才给 1(那时金属度由贴图 R 通道逐像素给, 1 只是乘数);
            // 纯色件(地面/标尺/强调带)没有贴图 ⇒ 给 1 就是"全金属无漫反射" ⇒ 发黑(俯视一片黑的主因)
            mm.SetFloat("_Metallic", ms != null ? 1f : 0f);
            mm.SetFloat("_BumpScale", 0.6f);   // 高光在 TAA 下会碎成星点, 降法线强度
            mm.SetFloat("_OcclusionStrength", 0f);
            // 卡通参数: 母材质默认值(走廊那次"换装"就是照搬这一组)
            mm.SetColor("_ShadowColor", new Color(0.38f, 0.46f, 0.64f, 1f));
            mm.SetFloat("_ToonThreshold", 0.65f);
            mm.SetFloat("_ToonSoftness", 0.035f);
            mm.SetFloat("_ToonSpecular", 0.1f);
            mm.SetFloat("_ShadowPattern", 0f);
            mm.SetFloat("_Hatching", 0f);
            mm.SetFloat("_EmissionEnabled", 0f);
            mm.SetFloat("_EmissionUseMap", 0f);
            var kw = new List<string>(KW);
            foreach (var k in kw) mm.EnableKeyword(k);
            EditorUtility.SetDirty(mm);
            AssetDatabase.SaveAssets();
            PatchKeywords(mp, kw, new List<string> { "_EMISSION" });
            var m2 = AssetDatabase.LoadAssetAtPath<Material>(mp);
            if (m2 == null) return mm;
            EditorUtility.ClearDirty(m2);
            Debug.Log($"[ME] {name}: shader={m2.shader.name} BC={(m2.GetTexture("_BaseMap") != null)} " +
                      $"N={(m2.GetTexture("_BumpMap") != null)} MS={(m2.GetTexture("_MetallicGlossMap") != null)} " +
                      $"kw=[{string.Join(",", m2.shaderKeywords)}]");
            return m2;
        }

        static Material MakeUnlit(string name, Color hdr)
        {
            string mp = MAT + "/" + name + ".mat";
            var mm = AssetDatabase.LoadAssetAtPath<Material>(mp);
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (mm == null) { mm = new Material(sh) { name = name }; AssetDatabase.CreateAsset(mm, mp); }
            mm.shader = sh;
            mm.SetTexture("_BaseMap", null);
            mm.SetColor("_BaseColor", hdr);
            mm.SetFloat("_Surface", 0f);
            mm.SetFloat("_ZWrite", 1f);
            mm.SetFloat("_Cull", 0f);
            EditorUtility.SetDirty(mm); AssetDatabase.SaveAssets(); EditorUtility.ClearDirty(mm);
            return AssetDatabase.LoadAssetAtPath<Material>(mp);
        }

        /// <summary>.mat 是 YAML; 关键字要写 m_ValidKeywords(EnableKeyword 落不了盘)。
        /// 路径必须是绝对路径 —— 批处理 CWD 不是工程根, 相对路径会静默跳过。</summary>
        static void PatchKeywords(string path, List<string> add, List<string> remove)
        {
            string abs = Path.IsPathRooted(path) ? path
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));
            if (!File.Exists(abs)) { Debug.LogWarning("[ME] 材质文件不存在: " + abs); return; }
            var txt = File.ReadAllText(abs);
            var rxV = new Regex(@"m_ValidKeywords:(\s*\[\])?(?<v>(\r?\n  - [^\r\n]+)*)");
            var rxI = new Regex(@"m_InvalidKeywords:(\s*\[\])?(?<v>(\r?\n  - [^\r\n]+)*)");
            var mv = rxV.Match(txt);
            var mi = rxI.Match(txt);
            if (!mv.Success) { Debug.LogWarning("[ME] 无 m_ValidKeywords: " + path); return; }
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

        // ==================================================== 后处理卷(中性, 与走廊同口径)
        static VolumeProfile BuildVolumeProfile()
        {
            string pp = MAT + "/PP_SC_City_ME.asset";
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
            bl.intensity.overrideState = true; bl.intensity.value = 0f;
            EditorUtility.SetDirty(prof); AssetDatabase.SaveAssets();
            return prof;
        }

        static void BuildVolume(VolumeProfile prof)
        {
            var vgo = new GameObject("GLOBAL_ME_VOLUME");
            var vol = vgo.AddComponent<Volume>();
            vol.isGlobal = true; vol.priority = 1f; vol.weight = 1f; vol.sharedProfile = prof;
        }

        // ==================================================== 照明(镜之边缘: 高调白昼 + 青蓝天空)
        static void BuildLighting()
        {
            var sunGo = new GameObject("L_Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1.0f, 0.98f, 0.94f);
            sun.intensity = 1.05f;
            sun.shadows = LightShadows.Soft;
            // 主光主要朝 +z 打: 相机群在 +x/+z, 临街面要落进亮带(朝向分带的判据机位是 03_Corner)
            sunGo.transform.rotation = Quaternion.LookRotation(new Vector3(-0.34f, -0.62f, -0.71f).normalized);
            // ★ 环境光用 **Trilight(天空/赤道/地面 三色)**, 不用 Skybox:
            //   `ambientMode=Skybox` 走的是天空盒的 SH, 在本工程实测里**没有落到像素上**
            //   (俯视机位整城变黑剪影: 屋顶/地面这些**水平面**只吃环境光, 于是一片黑
            //    而竖直立面靠直射光仍然是亮的 —— 这个"只有水平面黑"的指纹就是它)。
            //   Trilight 是 Unity 原生三色环境光, 屋顶吃天空色、侧面吃赤道色、朝下吃地面色,
            //   正是室外场景该用的那一种, 也顺手给出镜之边缘"青得发白"的天光。
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.58f, 0.72f, 0.90f);
            RenderSettings.ambientEquatorColor = new Color(0.46f, 0.52f, 0.60f);
            RenderSettings.ambientGroundColor = new Color(0.30f, 0.31f, 0.32f);
            RenderSettings.ambientIntensity = 1.55f;
            // V-15: 远景要有大气衰减 —— 线性雾 150→800m, 颜色贴天空地平线
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 150f;
            RenderSettings.fogEndDistance = 800f;
            RenderSettings.fogColor = new Color(0.62f, 0.72f, 0.82f);
            var skySh = Shader.Find("Skybox/Procedural");
            if (skySh != null)
            {
                string sp = MAT + "/M_SKY_ME.mat";
                var sky = AssetDatabase.LoadAssetAtPath<Material>(sp);
                if (sky == null)
                {
                    sky = new Material(skySh) { name = "M_SKY_ME" };
                    AssetDatabase.CreateAsset(sky, sp);
                }
                sky.shader = skySh;
                sky.SetColor("_SkyTint", new Color(0.34f, 0.60f, 0.82f));
                sky.SetColor("_GroundColor", new Color(0.46f, 0.47f, 0.49f));
                sky.SetFloat("_AtmosphereThickness", 0.62f);
                sky.SetFloat("_Exposure", 1.15f);
                // V-16: 太阳要有个"盘"的形态, 不要一团过曝块 —— 盘径小 + 高质量边缘
                sky.SetFloat("_SunSize", 0.025f);
                sky.SetFloat("_SunSizeConvergence", 4f);
                sky.SetFloat("_SunDisk", 1f);   // 1 = High Quality(有边缘梯度)
                EditorUtility.SetDirty(sky); AssetDatabase.SaveAssets(); EditorUtility.ClearDirty(sky);
                RenderSettings.skybox = sky;
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
                RenderSettings.reflectionIntensity = 1.0f;
            }
        }

        static void BuildGround(GameObject root, Mats mats)
        {
            // V-14/V-26: 一张 90×90 无贴图灰板 → 广场底板 + 6 条大街(沥青/人行道/路缘)。
            // 与 Blender 预览的 build_ground_and_streets 同一张 STREETS 表、同一套高差口径:
            // 底板顶 0 / NS 沥青顶 0.022 / EW 沥青顶 0.018(路口 4mm 防共面闪) /
            // 人行道顶 0.085 / 路缘顶 0.115。
            AddSlab(root, mats.ground, "ME_Ground_Base", 0f, 0f, 400f, 400f, 0.4f, -0.2f);
            var ewMids = new List<float>();
            foreach (var s in STREETS) if (s.z0 == s.z1) ewMids.Add((s.z0 + s.z1) * 0.5f);
            foreach (var s in STREETS)
            {
                bool ns = s.x0 == s.x1;
                float len = ns ? Mathf.Abs(s.z1 - s.z0) : Mathf.Abs(s.x1 - s.x0);
                float cx = (s.x0 + s.x1) * 0.5f, cz = (s.z0 + s.z1) * 0.5f;
                float sx = ns ? len : CORRIDOR_W, sz = ns ? CORRIDOR_W : len;
                float top = ns ? 0.022f : 0.018f;
                AddSlab(root, mats.asphalt, "ME_Road_" + s.name, cx, cz, sx, sz, 0.18f, top - 0.09f);
                for (int sgn = -1; sgn <= 1; sgn += 2)
                {
                    float ox = ns ? cx + sgn * 4.45f : cx;
                    float oz = ns ? cz : cz + sgn * 4.45f;
                    AddSlab(root, mats.walk, $"ME_Walk_{s.name}_{sgn}", ox, oz, sx, sz, 0.12f, 0.025f);
                }
                if (!ns) continue;
                // 路缘: EW 街口两侧各 8m 断开, 供过街
                var cuts = new List<float>(ewMids.FindAll(z => z >= s.z0 && z <= s.z1));
                cuts.Sort();
                float cur = s.z0;
                var segs = new List<(float a, float b)>();
                foreach (var z in cuts) { if (z - 8f > cur) segs.Add((cur, z - 8f)); cur = z + 8f; }
                if (s.z1 > cur) segs.Add((cur, s.z1));
                int k = 0;
                foreach (var (a, b) in segs)
                {
                    if (b - a < 1f) { k++; continue; }
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                        AddSlab(root, mats.curb, $"ME_Curb_{s.name}_{k}_{sgn}",
                                cx + sgn * 3.39f, (a + b) * 0.5f, 0.28f, b - a, 0.12f, 0.055f);
                    k++;
                }
            }
        }

        static void AddSlab(GameObject root, Material mat, string name,
                            float cx, float cz, float sx, float sz, float thick, float topY)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(root.transform);
            go.transform.position = new Vector3(cx, topY - thick * 0.5f, cz);
            go.transform.localScale = new Vector3(sx, thick, sz);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            go.isStatic = true;
        }

        // ==================================================== 摆场
        static int PlaceAll(GameObject root, GameObject fbx, Mats mats, string manifest)
        {
            int n = 0;
            foreach (var (name, pos, yaw, fac) in LAYOUT)
            {
                var src = FindChild(fbx, name);
                if (src == null) { Debug.LogError("[ME] FBX 缺少 " + name); continue; }
                var go = UnityEngine.Object.Instantiate(src.gameObject, pos,
                                                        Quaternion.Euler(0, yaw, 0), root.transform);
                go.name = name + "_placed";
                var slots = ReadManifestSlots(manifest, name);
                if (slots == null || slots.Length == 0)
                    // V-05 的教训: 槽序拿不到就**响亮地失败**, 不许静默按槽位猜 ——
                    // 旧版静默回退把面板材质接到立面上, 黑墙就是这条路径的嫌疑
                    Debug.LogError($"[ME] {name} 在 manifest 里没有槽序, 材质接线不可信 " +
                                   "(检查 Assets/Art/Environment/HST_Kit/Manifests/HST_Prags/manifests/manifest_me.json)");
                Wire(go, mats.facade.ContainsKey(fac) ? mats.facade[fac] : mats.panel, mats, slots);
                AddBoxCollider(go);
                foreach (var (suffix, active) in new[] { ("_WinDay", false), ("_WinDusk", false) })
                {
                    var wsrc = FindChild(fbx, name + suffix);
                    if (wsrc == null) continue;
                    var wgo = UnityEngine.Object.Instantiate(wsrc.gameObject, pos,
                                                             Quaternion.Euler(0, yaw, 0), root.transform);
                    wgo.name = name + suffix;
                    var wr = wgo.GetComponentInChildren<MeshRenderer>();
                    if (wr != null)
                    {
                        var wm = new[] { mats.winWarm, mats.winCool, mats.winNeut };
                        var arr = new Material[Mathf.Max(3, wr.sharedMaterials.Length)];
                        for (int i = 0; i < arr.Length; i++) arr[i] = wm[Mathf.Min(i, 2)];
                        wr.sharedMaterials = arr;
                        wr.shadowCastingMode = ShadowCastingMode.Off;
                        wr.receiveShadows = false;
                    }
                    wgo.SetActive(active);
                }
                n++;
            }
            return n;
        }

        /// <summary>亮窗状态机(V-06): 白天 = 两组全灭(自发光压到 0, 判据 hot%≤0.3%);
        /// 夜里 = WinDusk 开。旧版白天两组全关、夜景拍完"还原"成两组全开 —— 于是补拍的
        /// AB 对照图里白天满屏亮窗; v2 中间版改成"白天 WinDay 开"又被 hot% 门打回。
        /// 任何拍摄段之前都显式设一遍, 不依赖"还原"。</summary>
        static void SetWindows(bool dusk)
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                if (t.name.EndsWith("_WinDay")) t.gameObject.SetActive(false);
                else if (t.name.EndsWith("_WinDusk")) t.gameObject.SetActive(dusk);
            }
        }

        static void PlaceScaleRef(GameObject root, Mats mats)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(HUMAN_FBX);
            if (fbx == null) { Debug.LogError("[ME] 找不到假人 FBX"); return; }
            var src = FindChild(fbx, "SM_Human_Scale175_v1") ?? fbx.transform;
            var go = UnityEngine.Object.Instantiate(src.gameObject, HUMAN_POS, Quaternion.identity,
                                                    root.transform);
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

        static Transform FindChild(GameObject fbx, string name)
        {
            foreach (var t in fbx.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        /// <summary>按 manifest 记录的**真实槽序**接材质(FBX 材质表 = 被用到的槽按首次出现排序)。</summary>
        static void Wire(GameObject go, Material facade, Mats mats, string[] slots)
        {
            var r = go.GetComponentInChildren<MeshRenderer>();
            if (r == null) return;
            int n = Mathf.Max(1, r.sharedMaterials.Length);
            var arr = new Material[n];
            if (slots == null || slots.Length == 0)
            {
                Debug.LogWarning($"[ME] {go.name} 无槽序, 退回固定约定");
                for (int i = 0; i < n; i++) arr[i] = FromSlot(null, i, facade, mats);
            }
            else
                for (int i = 0; i < n; i++)
                    arr[i] = FromSlot(i < slots.Length ? slots[i] : null, i, facade, mats);
            r.sharedMaterials = arr;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
        }

        static Material FromSlot(string slot, int idx, Material facade, Mats mats)
        {
            switch (slot)
            {
                case "M_ME_Facade": return facade;
                case "M_ME_Roof": return mats.roof;
                case "M_ME_Panel": return mats.panel;
                case "M_ME_Dark": return mats.dark;
                case "M_ME_Emit_Warm": return mats.winWarm;
                case "M_ME_Emit_Cool": return mats.winCool;
                case "M_ME_Emit_Neut": return mats.winNeut;
            }
            switch (idx)
            {
                case 0: return facade; case 1: return mats.roof; case 2: return mats.panel;
                case 3: return mats.dark; default: return mats.winNeut;
            }
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

        // ==================================================== 诊断 + 尺寸门
        static void Diag(GameObject root, Mats mats, string manifest)
        {
            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/URP-Renderer.asset");
            if (rd != null)
            {
                var on = rd.rendererFeatures.Where(f => f != null && f.isActive).Select(f => f.name).ToArray();
                Debug.Log($"[ME] 管线特性 {on.Length}/{rd.rendererFeatures.Count} 开: [{string.Join(", ", on)}]");
            }
            LogActualRenderingMode();
            // V-03: 阴影距离 —— URP 资产的 shadowDistance 不够时, 远机位整个在阴影距离外,
            // 地面上出不来楼的投影。只做**运行时**兜底(不 SaveAssets, 不动 TA 的资产文件)。
            var urp = UniversalRenderPipeline.asset;
            if (urp != null)
            {
                Debug.Log($"[ME] URP shadowDistance={urp.shadowDistance}m cascades={urp.shadowCascadeCount}");
                if (urp.shadowDistance < 400f)
                {
                    urp.shadowDistance = 400f;
                    Debug.Log("[ME] 阴影距离运行时提到 400m(会话内生效, 不入盘)");
                }
            }
            // 尺寸验收(世界包围盒; manifest 是 Blender 的 (x,y,z), Unity 里 y 必须等于 Blender 的 z)
            if (!string.IsNullOrEmpty(manifest))
            {
                int fails = 0, cnt = 0;
                foreach (var (name, pos, yaw, fac) in LAYOUT)
                {
                    var go = root.transform.Cast<Transform>()
                        .Select(t => t.gameObject).FirstOrDefault(g => g.name == name + "_placed");
                    if (go == null) continue;
                    var mf = go.GetComponentInChildren<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    // ★ 用**网格局部包围盒 × lossyScale**: 与摆场 yaw 无关(世界 AABB 会随旋转胀大,
                    //   实测非 90° yaw 的 6 件全被误判 FAIL)。lossyScale 里含 FBX 导入的 0.01 缩放。
                    var lb = mf.sharedMesh.bounds.size;
                    var ls = mf.transform.lossyScale;
                    var sz = new Vector3(lb.x * Mathf.Abs(ls.x), lb.y * Mathf.Abs(ls.y),
                                         lb.z * Mathf.Abs(ls.z));
                    var (bx, by, bz) = ReadSize(manifest, name);
                    float dY = Mathf.Abs(sz.y - bz);
                    float dXZ = Mathf.Max(Mathf.Abs(Mathf.Max(sz.x, sz.z) - Mathf.Max(bx, by)),
                                          Mathf.Abs(Mathf.Min(sz.x, sz.z) - Mathf.Min(bx, by)));
                    bool ok = Mathf.Max(dY, dXZ) <= SIZE_TOL;
                    if (!ok) fails++;
                    cnt++;
                    Debug.Log($"[ME-VERIFY] {name}: bl=({bx},{by},{bz}) got={sz} " +
                              $"dY={dY * 1000f:F1}mm dXZ={dXZ * 1000f:F1}mm {(ok ? "OK" : "FAIL")}");
                }
                Debug.Log($"[ME-VERIFY] 共 {cnt} 件, 失败 {fails} 件 (容差 {SIZE_TOL * 1000f:F0}mm)");
            }
            // 楼层比例门(门高/层高对 1.75m 假人)
            var sc = root.transform.Cast<Transform>().Select(t => t.gameObject)
                .FirstOrDefault(g => g.name.StartsWith("SM_Human_Scale175"));
            var specPath = "Assets/Art/Environment/HST_Kit/Manifests/HST_Prags/manifests/spec_bld.json";
            if (File.Exists(specPath))
            {
                var j = File.ReadAllText(specPath);
                float H = SpecF(j, "human_h"), doorH = SpecF(j, "door_h"), fh = SpecF(j, "floor_h");
                foreach (var (label, val, target, tol) in new (string, float, float, float)[] {
                    ("门高", doorH, 1.16f, 0.05f), ("层高", fh, 2.29f, 0.10f) })
                    Debug.Log($"[ME-SCALE] {label}÷身高 = {val / H:F3} (目标 {target:F2}±{tol:F3}) " +
                              $"{(MathF.Abs(val / H - target) <= tol ? "OK" : "FAIL")}");
            }
            if (sc != null)
            {
                var mr = sc.GetComponentInChildren<MeshRenderer>();
                if (mr != null) Debug.Log($"[ME-SCALE] 假人实测高 {mr.bounds.size.y:F3}m");
            }
        }

        static float SpecF(string json, string key)
        {
            var m = new Regex("\"" + key + "\"\\s*:\\s*(-?[0-9][0-9.eE+-]*)").Match(json);
            return m.Success
                ? float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0f;
        }

        static (float, float, float) ReadSize(string json, string name)
        {
            int i = json.IndexOf("\"name\": \"" + name + "\"", StringComparison.Ordinal);
            if (i < 0) return (0, 0, 0);
            int s = json.IndexOf("[", i), e = json.IndexOf("]", s);
            var parts = json.Substring(s + 1, e - s - 1).Split(',');
            return (float.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[2].Trim(), System.Globalization.CultureInfo.InvariantCulture));
        }

        static void LogActualRenderingMode()
        {
            var cam = UnityEngine.Object.FindObjectsOfType<Camera>(true).FirstOrDefault(c => c.enabled);
            if (cam == null) { Debug.LogWarning("[ME] 还没相机, 跳过渲染路径实测"); return; }
            var ud = cam.GetUniversalAdditionalCameraData();
            var prop = typeof(UniversalAdditionalCameraData).GetProperty("scriptableRenderer",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);
            object rend = prop != null ? prop.GetValue(ud) : null;
            if (rend == null) return;
            var t = rend.GetType();
            string info = "renderer=" + t.Name;
            foreach (var nm in new[] { "renderingModeActual" })
            {
                var p2 = t.GetProperty(nm, System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (p2 != null) info += $", {nm}={p2.GetValue(rend)}";
            }
            Debug.Log("[ME] 实测渲染路径: " + info + "  (Forward=0, Deferred=1)");
        }

        // ==================================================== 出图
        static void ShootAll(Dictionary<string, Camera> cams, Mats mats)
        {
            Directory.CreateDirectory(OUT);
            LogActualRenderingMode();
            SetWindows(false);      // 白天态: WinDay 开(V-06: 拍摄段之前显式设, 不靠"还原")
            foreach (var kv in cams)
            {
                Shot(kv.Value, $"{OUT}/unity_me_day_{kv.Key}.png");
                Debug.Log($"[ME-SHOT] unity_me_day_{kv.Key}.png");
            }
            // 夜景: 立面换 Night + 亮窗换 WinDusk + 压太阳/天空盒/雾 + 压环境光, 拍完还原
            // ★ 逐槽匹配换(旧版只看 sharedMaterials[0] —— DS 塔的槽 0 是 Panel,
            //   整栋楼夜里不换夜立面)
            var lit = new List<(Renderer r, Material[] orig)>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
            {
                var cur = r.sharedMaterials;
                bool has = false;
                foreach (var m in cur) if (m != null && m.name.StartsWith("M_ME_Facade_")) { has = true; break; }
                if (!has) continue;
                var nw = (Material[])cur.Clone();
                for (int i = 0; i < nw.Length; i++)
                    if (nw[i] != null && nw[i].name.StartsWith("M_ME_Facade_"))
                        nw[i] = mats.facade["Night"];
                lit.Add((r, cur));
                r.sharedMaterials = nw;
            }
            SetWindows(true);
            var sun = UnityEngine.Object.FindObjectsOfType<Light>()
                .FirstOrDefault(l => l.type == LightType.Directional);
            float si = sun.intensity; Color sc2 = sun.color;
            sun.intensity = 0.30f; sun.color = new Color(0.62f, 0.74f, 0.98f);
            var ambSky = RenderSettings.ambientSkyColor;
            var ambEq = RenderSettings.ambientEquatorColor;
            var ambGnd = RenderSettings.ambientGroundColor;
            float ambI = RenderSettings.ambientIntensity;
            RenderSettings.ambientIntensity = 0.55f;
            RenderSettings.ambientSkyColor = new Color(0.10f, 0.14f, 0.24f);
            RenderSettings.ambientEquatorColor = new Color(0.07f, 0.10f, 0.18f);
            RenderSettings.ambientGroundColor = new Color(0.04f, 0.05f, 0.09f);
            // 天空盒/雾跟着入夜(相机是 Skybox 清屏, 不压曝光夜景会顶着白天的天空)
            var skyMat = RenderSettings.skybox;
            float skyExp = skyMat != null ? skyMat.GetFloat("_Exposure") : 1f;
            var fogCol = RenderSettings.fogColor;
            if (skyMat != null) skyMat.SetFloat("_Exposure", 0.06f);
            RenderSettings.fogColor = new Color(0.03f, 0.05f, 0.10f);
            Shot(cams["02_Street"], OUT + "/unity_me_night_02_Street.png");
            Shot(cams["05_Blade"], OUT + "/unity_me_night_05_Blade.png");
            foreach (var (r, orig) in lit) r.sharedMaterials = orig;
            if (skyMat != null) skyMat.SetFloat("_Exposure", skyExp);
            RenderSettings.fogColor = fogCol;
            sun.intensity = si; sun.color = sc2;
            RenderSettings.ambientIntensity = ambI;
            RenderSettings.ambientSkyColor = ambSky;
            RenderSettings.ambientEquatorColor = ambEq;
            RenderSettings.ambientGroundColor = ambGnd;
            SetWindows(false);      // 回到白天态再拍对照
            Debug.Log($"[ME-SHOT] 夜景 2 张(立面换 {lit.Count} 渲染器)");
            // 母材质原件(Unlit tag) vs 本场景用的副本(Lit tag): 一机位对照, 证明这 1 个 tag 的差别
            var shSrc = Shader.Find("OurFunction/NPR/Deferred Toon");
            if (shSrc != null && shSrc != Shader.Find("OurFunction/NPR/Deferred Toon (Lit tag / ME)"))
            {
                var sw = new List<(Renderer r, Material[] orig)>();
                foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true))
                {
                    var cur = r.sharedMaterials;
                    if (cur.Length == 0 || cur[0] == null) continue;
                    if (cur[0].shader == null) continue;
                    if (!cur[0].shader.name.Contains("NPR")) continue;
                    var nw = new Material[cur.Length];
                    for (int i = 0; i < cur.Length; i++)
                    {
                        var m = cur[i];
                        if (m == null) { nw[i] = null; continue; }
                        var t2 = new Material(m) { name = "AB_" + m.name };
                        t2.shader = shSrc;
                        nw[i] = t2;
                    }
                    sw.Add((r, cur));
                    r.sharedMaterials = nw;
                }
                Shot(cams["03_Corner"], OUT + "/unity_me_ab_material_src.png");
                foreach (var (r, o) in sw) r.sharedMaterials = o;
                Debug.Log($"[ME-SHOT] 母材质原件对照 1 张({sw.Count} 渲染器, 拍完还原)");
            }
        }

        static Dictionary<string, Camera> BuildCameras()
        {
            var d = new Dictionary<string, Camera>();
            foreach (var (name, pos, look, fov) in CAMS)
                d[name] = MakeCam("CAM_" + name, pos, look, fov);
            return d;
        }

        static Camera MakeCam(string name, Vector3 pos, Vector3 look, float fov)
        {
            var go = new GameObject(name);
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = fov;
            cam.farClipPlane = 1200f;
            // V-15: 用真天空盒(旧版 SolidColor 蓝底, 天空永远是一条硬边色带, 太阳盘也出不来)
            cam.clearFlags = CameraClearFlags.Skybox;
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation((look - pos).normalized, Vector3.up);
            var ud = cam.GetUniversalAdditionalCameraData();
            ud.renderPostProcessing = true;
            return cam;
        }

        static void Shot(Camera cam, string path)
        {
            int W = 1920, H = 1080;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            // ★ 首帧热身(2026-10-12): 批处理进程里第一次 cam.Render() 时, 延迟管线的
            //   阴影/深度瀑布(GTAO、NPR 分带)尚未就绪 —— 症状是**当拍的第 1 机位
            //   (01_Aerial)楼体只剩环境光项, 太阳直射整片丢**(实测同机位第二遍就正常,
            //   分块对比楼面亮度比 0.51, 天空 1.00)。渲两遍取第二遍, 第一遍只当热身。
            cam.Render();
            cam.Render();
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
