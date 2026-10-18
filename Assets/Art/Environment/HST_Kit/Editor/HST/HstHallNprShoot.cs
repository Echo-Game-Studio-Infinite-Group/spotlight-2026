#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HST
{
    // 两个大厅按"走廊 NPR 口径"出图 (2026-10-12 用户令: 像走廊一样应用 NPR 母材质 + 按走廊方案重新打光 + 逐机位出图)。
    //
    // ① 材质换装: 换成 TA 的 NPR 母材质 —— 用**只改 UniversalMaterialType tag** 的非破坏副本
    //    "OurFunction/NPR/Deferred Toon (Lit tag / ME)"。原版 "…/Deferred Toon" 的 tag = Unlit,
    //    URP 延迟路径据此不给直射光 ⇒ 只吃间接光(走廊实测同机位 mean 0.043 → 副本 0.323)。
    // ② 发光件识别: 本两厅的灯带/全息/发光板是 **URP Unlit + HDR _BaseColor(>1)**, 不用 _EMISSION 关键字
    //    (城市线同一口径)。这类材质**保留原样**(换掉就不发光了), 只按 glowK 缩放 HDR 基色。
    // ③ 打光: 见 Tune —— 默认沿用场景自带灯(那是"实管线补光表"的落地), 环境光取走廊表的逐场景值;
    //    也可切 relight=grid 用走廊 TA 配方点光阵顶替。影带色/软化写进材质。
    // ④ 每机位 1920x1080 + 亮度读数(mean/死黑/过曝), 落 renders/npr_pair/。
    // ⑤ 拍完全部还原, 场景不保存(零残留) —— 与走廊 "NPR 走廊换装出图(非破坏)" 同一口径。
    public static class HstHallNprShoot
    {
        const string OutDir  = @"F:\VSCode\工业级渲染\renders\npr_pair";
        const string WorkDir = @"F:\VSCode\工业级渲染\tmp\hall_npr";
        const string ShaderName = "OurFunction/NPR/Deferred Toon (Lit tag / ME)";
        const int W = 1920, H = 1080;

        // 走廊"实管线补光表"里两个大厅的 Flat 环境光(scripts 里 HstSceneBuilder.AmbientByTag)
        static readonly Dictionary<string, Color> TableAmbient = new Dictionary<string, Color>
        {
            ["corridor"]  = new Color(0.007158f, 0.008055f, 0.009850f),
            ["atrium"]    = new Color(0.05298f, 0.0595f, 0.07506f),
            ["atrium_v6"] = new Color(0.1465f, 0.1562f, 0.1758f),
        };

        class Hall
        {
            public string scene, tag, ambTag;
            public string[] prefixes, cams;
            public float[] srcFog;   // Blender 源场景的 haze 色
            public bool needKeyLight;  // 该场景没有方向光 → NPR 直接项恒 0, 需要补一盏主光(走廊表里 atrium 的雾色是 ×0.20 的口径换算)
        }

        static readonly Hall[] Halls =
        {
            new Hall { scene = "Assets/Art/Environment/HST_Kit/Scenes/SC_Atrium_Concept.unity", tag = "ConcNPR", ambTag = "atrium", needKeyLight = false,
                       prefixes = new[] { "M_M_", "M_T_", "M_V6_" },
                       srcFog = new[] { 0.45f, 0.52f, 0.66f },
                       cams = new[] { "CAM_AtriumOverview", "CAM_InteriorDesk", "CAM_InteriorThroughGlass",
                                      "CAM_PodDownShaft", "CAM_PodUpMega" } },
            new Hall { scene = "Assets/Art/Environment/HST_Kit/Scenes/SC_Atrium_Military.unity", tag = "MilNPR", ambTag = "atrium_v6", needKeyLight = true,
                       prefixes = new[] { "M_V6_" },
                       srcFog = new[] { 0.72f, 0.78f, 0.90f },
                       cams = new[] { "CAM_Armory", "CAM_DepthAxis", "CAM_MezzOffice", "CAM_Panorama" } },
        };

        [System.Serializable]
        class Tune
        {
            public string relight = "own";        // own | grid
            public float ownLightK = 1f;          // 场景自带点/聚灯强度系数(relight=own)
            public float ownSunK = 1f;            // 场景自带方向灯强度系数(relight=own)
            public float gridSpacing = 6f;        // 点光阵间距(relight=grid)
            public float gridIntensity = 0.35f;
            public float gridRange = 10f;
            public bool useTableAmbient = true;   // 环境光取走廊表; 否则用 ambient 三元组
            public float ambientK = 1f;           // 走廊表环境光的整体倍率(均匀补光, 不造高光斑)
            public float[] ambient = { 0.05f, 0.06f, 0.075f };
            public float[] shadowColor = { 0.16f, 0.29f, 0.43f };
            public float toonSoftness = 0.15f;
            public float toonThreshold = -1f;     // <0 = 保持母材质默认
            public float glowK = 1f;              // 发光件 HDR 基色缩放
            public bool useSourceFog = false;     // 雾色用 Blender 源场景的 haze 色(见 Hall.srcFog)
            public string reflectionMode = "";    // 空=不动; "skybox"|"custom"(排障用: 烘焙会把 defaultReflectionMode 改成 Custom)
            public float reflectionIntensity = -1f;
            public float dirK = 1f;               // 非太阳方向光系数(排障用; 默认 1 = 不改)
            public bool addSun = false;
            public float sunIntensity = 0.4f;
        }

        static Tune LoadTune()
        {
            string p = Path.Combine(WorkDir, "tune.json");
            if (!File.Exists(p)) { Debug.Log("[HNP] 无 tune.json, 用默认"); return new Tune(); }
            var t = JsonUtility.FromJson<Tune>(File.ReadAllText(p));
            Debug.Log("[HNP] tune: " + File.ReadAllText(p).Replace("\n", " "));
            return t;
        }

        [MenuItem("HST/大厅 NPR 换装+走廊打光 逐机位出图")]
        public static void Run()
        {
            Directory.CreateDirectory(OutDir);
            Directory.CreateDirectory(WorkDir);
            var shader = Shader.Find(ShaderName);
            if (shader == null) { Debug.LogError("[HNP] 找不到 shader: " + ShaderName); return; }
            var tune = LoadTune();
            foreach (var hall in Halls) ShootHall(hall, shader, tune);
            Debug.Log("[HNP] ALL DONE → " + OutDir);
        }

        static bool IsGlow(Material m)
        {
            if (m.IsKeywordEnabled("_EMISSION") && m.HasProperty("_EmissionColor")
                && m.GetColor("_EmissionColor").maxColorComponent > 0.02f) return true;
            if (m.HasProperty("_BaseColor") && m.GetColor("_BaseColor").maxColorComponent > 1.02f) return true;
            return false;
        }

        // 透光件(玻璃): 母材质是不透明卡通 shader, 换上去玻璃就变白墙 —— 必须保留原材质
        static bool IsTransparent(Material m)
        {
            if (m.renderQueue >= 3000) return true;
            if (m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f) return true;
            return false;
        }

        static void ShootHall(Hall hall, Shader shader, Tune t)
        {
            EditorSceneManager.OpenScene(hall.scene, OpenSceneMode.Single);
            try { var ls = UnityEditor.Lightmapping.lightingSettings; if (ls != null) ls.autoGenerate = false; }
            catch (System.Exception) { }

            // ---------------- ① 材质换装 ----------------
            var shadowCol = t.shadowColor != null && t.shadowColor.Length >= 3
                ? new Color(t.shadowColor[0], t.shadowColor[1], t.shadowColor[2], 1f)
                : new Color(0.16f, 0.29f, 0.43f, 1f);
            var swap = new List<KeyValuePair<Renderer, Material[]>>();
            var temps = new List<Material>();
            int made = 0, glow = 0, trans = 0;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
            {
                if (!r.gameObject.activeInHierarchy) continue;
                var cur = r.sharedMaterials;
                if (!cur.Any(m => m != null && hall.prefixes.Any(p => m.name.StartsWith(p)))) continue;
                var npr = new Material[cur.Length];
                for (int k = 0; k < cur.Length; k++)
                {
                    var src = cur[k];
                    if (src == null) { npr[k] = null; continue; }
                    if (IsTransparent(src)) { npr[k] = src; trans++; continue; }   // 玻璃原样
                    if (IsGlow(src))
                    {
                        // 发光件: 保留原 shader(Unlit+HDR 基色), 只缩放 HDR 基色
                        var g = new Material(src) { name = "GLOWK_" + src.name };
                        if (g.HasProperty("_BaseColor"))
                        {
                            var c = g.GetColor("_BaseColor");
                            g.SetColor("_BaseColor", new Color(c.r * t.glowK, c.g * t.glowK, c.b * t.glowK, c.a));
                        }
                        temps.Add(g); npr[k] = g; glow++;
                        continue;
                    }
                    var nm = new Material(shader) { name = "NPRW_" + src.name };
                    if (src.HasProperty("_BaseMap") && nm.HasProperty("_BaseMap"))
                        nm.SetTexture("_BaseMap", src.GetTexture("_BaseMap"));
                    if (src.HasProperty("_BaseColor") && nm.HasProperty("_BaseColor"))
                        nm.SetColor("_BaseColor", src.GetColor("_BaseColor"));
                    if (src.HasProperty("_MetallicGlossMap") && nm.HasProperty("_MetallicGlossMap"))
                        nm.SetTexture("_MetallicGlossMap", src.GetTexture("_MetallicGlossMap"));
                    if (src.HasProperty("_Metallic") && nm.HasProperty("_Metallic"))
                        nm.SetFloat("_Metallic", src.GetFloat("_Metallic"));
                    if (src.HasProperty("_Smoothness") && nm.HasProperty("_Smoothness"))
                        nm.SetFloat("_Smoothness", src.GetFloat("_Smoothness"));
                    if (nm.HasProperty("_ShadowColor")) nm.SetColor("_ShadowColor", shadowCol);
                    if (nm.HasProperty("_ToonSoftness")) nm.SetFloat("_ToonSoftness", t.toonSoftness);
                    if (t.toonThreshold >= 0f && nm.HasProperty("_ToonThreshold"))
                        nm.SetFloat("_ToonThreshold", t.toonThreshold);
                    temps.Add(nm); npr[k] = nm; made++;
                }
                swap.Add(new KeyValuePair<Renderer, Material[]>(r, cur));
                r.sharedMaterials = npr;
            }
            Debug.Log($"[HNP] {hall.tag} 换装渲染器 {swap.Count} / NPR {made} / 发光保留 {glow} / 玻璃保留 {trans}");

            // ---------------- ② 打光 ----------------
            var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
            var lstate = lights.Select(l => l.enabled).ToArray();
            var linten = lights.Select(l => l.intensity).ToArray();
            var ambB = RenderSettings.ambientLight;
            var ambMB = RenderSettings.ambientMode;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = t.useTableAmbient && TableAmbient.ContainsKey(hall.ambTag)
                ? TableAmbient[hall.ambTag]
                : new Color(t.ambient[0], t.ambient[1], t.ambient[2], 1f);
            var fogB = RenderSettings.fogColor;
            bool fogBOn = RenderSettings.fog;
            if (t.useSourceFog && hall.srcFog != null && hall.srcFog.Length >= 3)
            {
                RenderSettings.fog = true;
                RenderSettings.fogColor = new Color(hall.srcFog[0], hall.srcFog[1], hall.srcFog[2], 1f);
                Debug.Log($"[HNP] {hall.tag} 雾色 → 源场景 haze ({hall.srcFog[0]},{hall.srcFog[1]},{hall.srcFog[2]}), 原 ({fogB.r:F3},{fogB.g:F3},{fogB.b:F3})");
            }

            var rig = new List<GameObject>();
            if (t.relight == "grid")
            {
                foreach (var l in lights) l.enabled = false;
                var pts = lights.Where(l => l.type == LightType.Point).Select(l => l.transform.position).ToList();
                if (pts.Count > 0)
                {
                    float x0 = pts.Min(p => p.x), x1 = pts.Max(p => p.x);
                    float z0 = pts.Min(p => p.z), z1 = pts.Max(p => p.z);
                    float yTop = pts.Max(p => p.y);
                    int nx = Mathf.Max(1, Mathf.RoundToInt((x1 - x0) / t.gridSpacing) + 1);
                    int nz = Mathf.Max(1, Mathf.RoundToInt((z1 - z0) / t.gridSpacing) + 1);
                    for (int i = 0; i < nx; i++)
                        for (int j = 0; j < nz; j++)
                        {
                            float x = nx == 1 ? (x0 + x1) * 0.5f : Mathf.Lerp(x0, x1, (float)i / (nx - 1));
                            float z = nz == 1 ? (z0 + z1) * 0.5f : Mathf.Lerp(z0, z1, (float)j / (nz - 1));
                            var go = new GameObject("HNPR_PT");
                            var li = go.AddComponent<Light>();
                            li.type = LightType.Point; li.color = new Color(0.85f, 0.93f, 0.93f);
                            li.intensity = t.gridIntensity; li.range = t.gridRange;
                            li.shadows = LightShadows.None;
                            go.transform.position = new Vector3(x, yTop, z);
                            rig.Add(go);
                        }
                    Debug.Log($"[HNP] {hall.tag} 走廊配方点光阵 {nx}x{nz}={nx * nz} I={t.gridIntensity} R={t.gridRange} yTop={yTop:F1}");
                }
            }
            else
            {
                // ownSunK 只压"太阳"(名字含 sun): 场景里像 L_ShaftFill 这种是**校准过的补光**
                // (中庭竖井方向补光, 替代 Cycles 巨型发光顶棚的远距反弹), 一起压会把竖井压黑。
                for (int k = 0; k < lights.Length; k++)
                {
                    var l = lights[k];
                    if (l.type == LightType.Point || l.type == LightType.Spot)
                        l.intensity = linten[k] * t.ownLightK;
                    else if (l.type == LightType.Directional && l.name.ToLower().Contains("sun"))
                        l.intensity = linten[k] * t.ownSunK;
                }
                Debug.Log($"[HNP] {hall.tag} 沿用场景自带灯(点/聚 ×{t.ownLightK}, 太阳名 ×{t.ownSunK}, 其余方向光不动)");
            }
            if (t.addSun)
            {
                var sunGo = new GameObject("HNPR_SUN");
                var sun = sunGo.AddComponent<Light>();
                sun.type = LightType.Directional; sun.color = new Color(0.796f, 0.796f, 0.796f);
                sun.intensity = t.sunIntensity;
                sunGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                rig.Add(sunGo);
            }

            // ---------------- ③ 逐机位出图 ----------------
            var camJson = new StringBuilder("{\n");
            bool first = true;
            foreach (var cn in hall.cams)
            {
                var go = GameObject.Find(cn);
                if (go == null) { Debug.LogWarning($"[HNP] {hall.tag} 缺机位 {cn}"); continue; }
                var cam = go.GetComponent<Camera>();
                var cd = cam.GetUniversalAdditionalCameraData();
                bool ppB = cd != null && cd.renderPostProcessing;
                if (cd != null) cd.renderPostProcessing = true;
                cam.nearClipPlane = 0.05f; cam.farClipPlane = 3000f;
                Shot(cam, Path.Combine(OutDir, $"unity_{hall.tag}_{cn}.png"), hall.tag + "/" + cn);
                if (cd != null) cd.renderPostProcessing = ppB;

                var p = cam.transform.position;
                var fwd = cam.transform.forward;
                var up = cam.transform.up;
                float hfov = 2f * Mathf.Atan(Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad * 0.5f) * (float)W / H) * Mathf.Rad2Deg;
                if (!first) camJson.Append(",\n");
                first = false;
                camJson.Append(string.Format(
                    "  \"{0}\": {{\"pos\":[{1:F4},{2:F4},{3:F4}], \"fwd\":[{4:F5},{5:F5},{6:F5}], \"up\":[{7:F5},{8:F5},{9:F5}], \"hfov\":{10:F3}}}",
                    cn, p.x, p.y, p.z, fwd.x, fwd.y, fwd.z, up.x, up.y, up.z, hfov));
            }
            camJson.Append("\n}\n");
            File.WriteAllText(Path.Combine(WorkDir, $"cams_{hall.tag}.json"), camJson.ToString());

            // ---------------- ④ 还原 ----------------
            foreach (var go in rig) UnityEngine.Object.DestroyImmediate(go);
            for (int k = 0; k < lights.Length; k++) { lights[k].enabled = lstate[k]; lights[k].intensity = linten[k]; }
            RenderSettings.ambientLight = ambB;
            RenderSettings.ambientMode = ambMB;
            RenderSettings.fogColor = fogB;
            RenderSettings.fog = fogBOn;
            foreach (var kv in swap) kv.Key.sharedMaterials = kv.Value;
            foreach (var x in temps) UnityEngine.Object.DestroyImmediate(x);
            Debug.Log($"[HNP] {hall.tag} 已还原(场景未保存, 零残留)");
        }

        // ---------------- 持久化: 把换装与打光**写进**场景与材质资产 ----------------
        // 用户令(2026-10-12): "为什么是临时换装? 直接替换啊" —— 不再拍完还原。
        // 材质**就地改写**(同一个 .mat 资产、GUID 不变 → 场景/预制体引用与布局全不动)。
        // 安全性已核: M_M_* / M_T_* / M_V6_* 只被这两个大厅场景引用, 不外溢。
        // 例外保留: 半透玻璃(URP Lit 透明, 更接近 Blender 玻璃)与发光件(URP Unlit + HDR 基色)。
        const string BaselineFile = @"F:\VSCode\工业级渲染\tmp\hall_npr\persist_baseline.psv";

        [MenuItem("HST/大厅 NPR 换装(持久写入场景)")]
        public static void Persist()
        {
            var shader = Shader.Find(ShaderName);
            if (shader == null) { Debug.LogError("[HNP] 找不到 shader: " + ShaderName); return; }
            var t = LoadTune();
            var baseline = LoadBaseline();
            foreach (var hall in Halls) PersistHall(hall, shader, t, baseline);
            SaveBaseline(baseline);
            Debug.Log("[HNP] 持久化完成(材质已就地改写, 场景已存盘)");
        }

        static Dictionary<string, float> LoadBaseline()
        {
            var d = new Dictionary<string, float>();
            if (!File.Exists(BaselineFile)) return d;
            foreach (var line in File.ReadAllLines(BaselineFile))
            {
                int i = line.LastIndexOf('=');
                if (i <= 0) continue;
                float v;
                if (float.TryParse(line.Substring(i + 1), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out v))
                    d[line.Substring(0, i)] = v;
            }
            return d;
        }

        static void SaveBaseline(Dictionary<string, float> d)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BaselineFile));
            var lines = d.Select(kv => kv.Key + "=" + kv.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            File.WriteAllLines(BaselineFile, lines.ToArray());
        }

        static Color ShadowColOf(Tune t)
        {
            return t.shadowColor != null && t.shadowColor.Length >= 3
                ? new Color(t.shadowColor[0], t.shadowColor[1], t.shadowColor[2], 1f)
                : new Color(0.16f, 0.29f, 0.43f, 1f);
        }

        static void PersistHall(Hall hall, Shader shader, Tune t, Dictionary<string, float> baseline)
        {
            var scene = EditorSceneManager.OpenScene(hall.scene, OpenSceneMode.Single);
            try { var ls = UnityEditor.Lightmapping.lightingSettings; if (ls != null) ls.autoGenerate = false; }
            catch (System.Exception) { }
            var shadowCol = ShadowColOf(t);

            // ① 材质资产就地改写
            var set = new HashSet<Material>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
            {
                if (!r.gameObject.activeInHierarchy) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null && hall.prefixes.Any(p => m.name.StartsWith(p))) set.Add(m);
            }
            int opaque = 0, glow = 0, trans = 0;
            foreach (var m in set)
            {
                if (IsGlow(m)) { glow++; continue; }
                if (IsTransparent(m)) { trans++; continue; }
                string path = AssetDatabase.GetAssetPath(m);
                if (string.IsNullOrEmpty(path)) { Debug.LogWarning("[HNP] 材质无资产路径(可能是场景内嵌): " + m.name); continue; }
                var tex = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : null;
                var col = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : Color.white;
                var mgm = m.HasProperty("_MetallicGlossMap") ? m.GetTexture("_MetallicGlossMap") : null;
                float met = m.HasProperty("_Metallic") ? m.GetFloat("_Metallic") : 0f;
                float smo = m.HasProperty("_Smoothness") ? m.GetFloat("_Smoothness") : 0.5f;
                m.shader = shader;
                // 注意: 不改关键字 —— 原材质带的 _NORMALMAP / _METALLICSPECGLOSSMAP 会被 NPR 母材质
                // 正常消费(_BumpMap 法线 / 金属光滑度贴图), 保留才对; 用 throwaway 克隆(只搬 5 个属性)
                // 会丢掉法线贴图 → 同一个机位明显偏暗(实测 0.321 vs 就地改写的 0.563, 而 Blender 是 0.531)。
                // (另: Material.DisableKeyword 在这条路径上对已存盘材质不生效, 是静默无效的, 别指望它。)
                if (tex != null && m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", col);
                if (mgm != null && m.HasProperty("_MetallicGlossMap")) m.SetTexture("_MetallicGlossMap", mgm);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", met);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smo);
                if (m.HasProperty("_ShadowColor")) m.SetColor("_ShadowColor", shadowCol);
                if (m.HasProperty("_ToonSoftness")) m.SetFloat("_ToonSoftness", t.toonSoftness);
                if (t.toonThreshold >= 0f && m.HasProperty("_ToonThreshold")) m.SetFloat("_ToonThreshold", t.toonThreshold);
                EditorUtility.SetDirty(m);
                opaque++;
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[HNP] {hall.tag} 材质就地改写 NPR {opaque} / 发光保留 {glow} / 玻璃保留 {trans}");

            // ② 灯光: 只压"太阳", 基线存盘 → 可重复运行不叠加
            int nSun = 0;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>(true))
            {
                if (l.type != LightType.Directional) continue;
                if (!l.name.ToLower().Contains("sun")) continue;
                string key = hall.scene + "|" + l.name;
                if (!baseline.ContainsKey(key)) baseline[key] = l.intensity;
                float target = baseline[key] * t.ownSunK;
                if (Mathf.Abs(l.intensity - target) > 1e-4f) { l.intensity = target; EditorUtility.SetDirty(l); }
                nSun++;
            }
            Debug.Log($"[HNP] {hall.tag} 太阳光 ×{t.ownSunK} 共 {nSun} 盏(基线已记, 重复运行不叠加)");

            // ③ 环境光: 走廊"实管线补光表"逐场景值, Flat
            RenderSettings.ambientMode = AmbientMode.Flat;
            var amb = TableAmbient.ContainsKey(hall.ambTag)
                ? TableAmbient[hall.ambTag] : new Color(t.ambient[0], t.ambient[1], t.ambient[2], 1f);
            RenderSettings.ambientLight = new Color(amb.r * t.ambientK, amb.g * t.ambientK, amb.b * t.ambientK, 1f);
            Debug.Log($"[HNP] {hall.tag} 环境光 ×{t.ambientK} → ({RenderSettings.ambientLight.r:F4},{RenderSettings.ambientLight.g:F4},{RenderSettings.ambientLight.b:F4})");
            if (t.useSourceFog && hall.srcFog != null && hall.srcFog.Length >= 3)
            {
                RenderSettings.fog = true;
                RenderSettings.fogColor = new Color(hall.srcFog[0], hall.srcFog[1], hall.srcFog[2], 1f);
            }

            // 补主方向光并**写进场景**(不能只在出图路径里临时加, 否则打开工程还是暗的)。
            // NPR 的直接项只来自 GetMainLight 那一盏方向光; 军用中庭本来一盏都没有。
            if (hall.needKeyLight)
            {
                // 幂等: 已有 HST_KeyLight 就改它的参数, 否则新建。**必须显式 Realtime** ——
                // 实测写进场景的方向光若不设 bakeType, 在这条管线下不作为主光(NPR 直接项恒 0)。
                Light key = null;
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>(true))
                    if (l.name == "HST_KeyLight") { key = l; break; }
                bool hasDir = key != null;
                if (key != null)
                {
                    key.type = LightType.Directional;
                    key.color = new Color(0.796f, 0.796f, 0.796f);
                    key.intensity = t.sunIntensity;
                    key.lightmapBakeType = LightmapBakeType.Realtime;
                    EditorUtility.SetDirty(key);
                    Debug.Log($"[HNP] {hall.tag} 更新主方向光 HST_KeyLight I={t.sunIntensity} bakeType=Realtime");
                }
                if (!hasDir)
                {
                    var go = new GameObject("HST_KeyLight");
                    var li = go.AddComponent<Light>();
                    li.type = LightType.Directional;
                    li.color = new Color(0.796f, 0.796f, 0.796f);
                    li.intensity = t.sunIntensity;
                    li.lightmapBakeType = LightmapBakeType.Realtime;
                    li.shadows = LightShadows.Soft;
                    go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                    EditorUtility.SetDirty(li);
                    Debug.Log($"[HNP] {hall.tag} 已写入主方向光 HST_KeyLight I={t.sunIntensity}");
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[HNP] {hall.tag} 场景已存盘: {hall.scene}");
        }

        // 一步跑完: 持久写入 + 读场出图(批处理用)
        [MenuItem("HST/大厅 NPR 持久化 + 出图(一步)")]
        public static void PersistAndShoot() { Persist(); ShootOnly(); }

        // 一步跑完: 重烤 + 出图(2026-10-13 用户令"大厅你也直接重新烤吧, 对齐走廊就行")
        [MenuItem("HST/大厅 重烤 + 出图(一步)")]
        public static void BakeThenShoot() { SceneBuilder.BakeHallsOnly(); ShootOnly(); }

        // 读现场景出图(持久化之后用; 不再动任何资产)
        [MenuItem("HST/大厅 NPR 逐机位出图(读现场景)")]
        public static void ShootOnly()
        {
            Directory.CreateDirectory(OutDir);
            var tune = LoadTune();
            foreach (var hall in Halls)
            {
                EditorSceneManager.OpenScene(hall.scene, OpenSceneMode.Single);
                try { var ls = UnityEditor.Lightmapping.lightingSettings; if (ls != null) ls.autoGenerate = false; }
                catch (System.Exception) { }
                // 排障用: 烘焙会把 defaultReflectionMode 改成 Custom 并挂上旧的 cubemap
                if (tune.reflectionMode == "skybox") RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
                else if (tune.reflectionMode == "custom") RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                if (tune.reflectionIntensity >= 0f) RenderSettings.reflectionIntensity = tune.reflectionIntensity;
                if (tune.dirK != 1f)
                    foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>(true))
                        if (l.type == LightType.Directional && !l.name.ToLower().Contains("sun"))
                        { l.intensity *= tune.dirK; Debug.Log($"[HNP] {hall.tag} 方向光 {l.name} ×{tune.dirK} → {l.intensity:F3}"); }
                // 真灯强度系数(渲染期, 不存盘): 环境光在这条 NPR 管线下是死旋钮(实测 ×3 对 8/9 台 0 变化),
                // 想"整体亮起来"只能动真灯。点/聚/方向一起乘, 方向里跳过名字含 sun 的。
                if (Mathf.Abs(tune.ownLightK - 1f) > 1e-3f)
                {
                    int nl = 0;
                    foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>(true))
                    {
                        if (!l.enabled) continue;
                        if (l.type == LightType.Directional && l.name.ToLower().Contains("sun")) continue;
                        l.intensity *= tune.ownLightK; nl++;
                    }
                    Debug.Log($"[HNP] {hall.tag} 真灯 ×{tune.ownLightK} 命中 {nl} 盏");
                }
                // 补一盏主方向光(渲染期, 不存盘): NPR 的直接项只来自 GetMainLight 那一盏,
                // 军用中庭一盏方向光都没有 → 直接项恒 0, 亮度全压在烘焙 GI 上.
                bool hasDirNow = false;
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>(true))
                    if (l.enabled && l.type == LightType.Directional) { hasDirNow = true; break; }
                // 出图路径显式接管主光: 关掉场景里那盏 HST_KeyLight(实测写进场景的方向光
                // 在这条管线下不作为主光), 改由本次渲染临时加一盏 —— 这个组合实测能到超净室亮度。
                if (tune.addSun && hall.needKeyLight)
                {
                    foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>(true))
                        if (l.name == "HST_KeyLight") l.enabled = false;
                    var go = new GameObject("HNPR_SUN");
                    var li = go.AddComponent<Light>();
                    li.type = LightType.Directional;
                    li.color = new Color(0.796f, 0.796f, 0.796f);
                    li.intensity = tune.sunIntensity;
                    go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                    Debug.Log($"[HNP] {hall.tag} 补主方向光 I={tune.sunIntensity}(该场景本来无方向光)");
                }
                // 发光件可见亮度系数(渲染期克隆, 资产原值不动 —— 走廊 NPR 当年也是这么收 Bloom 的)。
                // 与"发光当 GI 光源"是两件事: 这里只压灯板自己的像素值, 不影响烘焙。
                if (Mathf.Abs(tune.glowK - 1f) > 1e-3f)
                {
                    int ng = 0;
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
                    {
                        var cur = r.sharedMaterials; bool chg = false;
                        for (int i = 0; i < cur.Length; i++)
                        {
                            var m = cur[i];
                            if (m == null || IsTransparent(m) || !IsGlow(m)) continue;
                            if (!chg) { cur = (Material[])cur.Clone(); chg = true; }
                            var g = new Material(m) { name = "GLOWK_" + m.name };
                            var c = g.GetColor("_BaseColor");
                            g.SetColor("_BaseColor", new Color(c.r * tune.glowK, c.g * tune.glowK, c.b * tune.glowK, c.a));
                            cur[i] = g;
                        }
                        if (chg) { r.sharedMaterials = cur; ng++; }
                    }
                    Debug.Log($"[HNP] {hall.tag} 发光件 ×{tune.glowK}(渲染期克隆) 命中 {ng} 个渲染器");
                }
                if (tune.reflectionMode != "" || tune.reflectionIntensity >= 0f)
                    Debug.Log($"[HNP] {hall.tag} 反射: mode={RenderSettings.defaultReflectionMode} intensity={RenderSettings.reflectionIntensity} cube={(RenderSettings.customReflection != null)}");
                foreach (var cn in hall.cams)
                {
                    var go = GameObject.Find(cn);
                    if (go == null) { Debug.LogWarning($"[HNP] {hall.tag} 缺机位 {cn}"); continue; }
                    var cam = go.GetComponent<Camera>();
                    var cd = cam.GetUniversalAdditionalCameraData();
                    bool ppB = cd != null && cd.renderPostProcessing;
                    if (cd != null) cd.renderPostProcessing = true;
                    cam.nearClipPlane = 0.05f; cam.farClipPlane = 3000f;
                    Shot(cam, Path.Combine(OutDir, $"unity_{hall.tag}_{cn}.png"), hall.tag + "/" + cn);
                    if (cd != null) cd.renderPostProcessing = ppB;
                }
            }
            Debug.Log("[HNP] 读现场景出图完成");
        }

        static void Shot(Camera cam, string path, string label)
        {
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.aspect = (float)W / H;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            var px = tex.GetPixels();
            var t8 = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
            var c8 = new Color[px.Length];
            double sum = 0; int dark = 0, blown = 0;
            for (int k = 0; k < px.Length; k++)
            {
                float r = L2S(Mathf.Clamp01(px[k].r)), g = L2S(Mathf.Clamp01(px[k].g)), b = L2S(Mathf.Clamp01(px[k].b));
                c8[k] = new Color(r, g, b, 1f);
                float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                sum += lum;
                if (lum < 0.05f) dark++;
                if (lum > 0.98f) blown++;
            }
            t8.SetPixels(c8);
            t8.Apply();
            File.WriteAllBytes(path, t8.EncodeToPNG());
            cam.targetTexture = prev;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(t8);
            Debug.Log(string.Format("[HNP-STAT] {0} mean={1:F3} 死黑={2:F2}% 过曝={3:F2}%",
                label, sum / px.Length, 100.0 * dark / px.Length, 100.0 * blown / px.Length));
        }

        static float L2S(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(Mathf.Max(c, 0f), 1f / 2.4f) - 0.055f;
    }
}
#endif
