// ArtDirection.cs — Level1 美术基调：环境光 / 天空 / 主光 / 实用灯 / 反射 / ACES 调色
//
// 背景：交接反馈「墙还是黑的、机架墙没有金属光泽」。根因不在单张贴图：
//   · 机架墙 BC 均值 0.14（sRGB），线性反照率不到 2%，再好的灯也提不亮；
//   · 场景没有反射探针，金属只反射 128px 天空盒，practicals 强度 22–26 cd 在 30m 距离等于没有；
//   · Global Volume 没有 Tonemapping/Color Adjustments，线性直出，暗部全糊。
//
// 本工具只做「画面基调」，不改对象结构、不改碰撞、不改网格：
//   ① 天空盒 = 阴天工业天空（Skybox/Procedural，亮度足以让金属有反射内容）
//   ② 环境光 Gradient 三色（冷灰）
//   ③ 主方向光：柔阴影、强度 1.5、暖白
//   ④ 已有 HST_PL_ 点光统一抬到可读强度；机架墙沿线补 practical 洗墙灯
//   ⑤ 每个区块一个 Realtime ReflectionProbe（256px，box projection），并立即 RenderProbe
//   ⑥ 新建 Level1 专用 Volume Profile：ACES + Color Adjustments + Vignette + Film Grain
//   ⑦ 地面/墙面 MapSkin 材质统一 metallic/smoothness 由贴图驱动
//
// 菜单：MapSkin/① 美术基调（写盘）
// 批处理：-executeMethod MapSkin.ArtDirection.Run
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MapSkin
{
    public static class ArtDirection
    {
        static string ScenePath = "Assets/Scenes/Level1.unity";

        /// <summary>批处理可选 -scene &lt;Assets/Scenes/xxx.unity&gt;：换皮工作一律跑在 Level1_Skin。</summary>
        static void ParseArgs()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-scene") ScenePath = args[i + 1];
        }

        const string SkyMatPath = "Assets/Settings/Level1_Sky.mat";
        const string VolumePath = "Assets/Settings/Level1_ArtVolume.asset";
        const string ProbePrefix = "ART_RP_";
        const string PracticalPrefix = "ART_PL_";
        const string ReportPath = "F:/VSCode/taptap-game-jem/_session_audit/art_direction.txt";

        static readonly Color AmbientSky = new Color(0.55f, 0.62f, 0.70f);
        static readonly Color AmbientEquator = new Color(0.40f, 0.46f, 0.52f);
        static readonly Color AmbientGround = new Color(0.18f, 0.20f, 0.24f);
        static readonly Color KeyColor = new Color(1.00f, 0.96f, 0.90f);
        const float KeyIntensity = 1.5f;
        static readonly Vector3 KeyEuler = new Vector3(48f, -38f, 0f);

        const float SiteLightCd = 380f;      // HST_PL_ 洗地灯（原来 22–26）
        const float PracticalCd = 180f;      // 机架墙洗墙灯
        const float PracticalRange = 18f;

        const string ReflCubemapPath = "Assets/Settings/Level1_Reflection.cubemap";

        [MenuItem("MapSkin/① 美术基调（写盘）")]
        public static void RunMenu() => Run();

        public static void Run()
        {
            ParseArgs();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var rep = new StringBuilder();
            rep.AppendLine($"[ArtDirection] {System.DateTime.Now:yyyy-MM-dd HH:mm}");

            // ---------- ① 天空盒 ----------
            // 与团队其它场景保持一致：用 Unity 内置 Default-Skybox（Level2/LevelTest/战斗场景同款），
            // 不再自造天空材质，避免 Level1 与其它关卡天空断层。
            var sky = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
            if (sky != null)
            {
                RenderSettings.skybox = sky;
                RenderSettings.sun = null;
                rep.AppendLine("① 天空盒：Default-Skybox（与团队其它场景一致）");
            }
            else
            {
                rep.AppendLine("① 天空盒：Default-Skybox 未找到，保留场景原值");
            }

            // ---------- ② 环境光 ----------
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = AmbientSky;
            RenderSettings.ambientEquatorColor = AmbientEquator;
            RenderSettings.ambientGroundColor = AmbientGround;
            RenderSettings.ambientIntensity = 1.0f;
            rep.AppendLine($"② 环境光 Trilight sky={AmbientSky} eq={AmbientEquator} gnd={AmbientGround} ×1.15");

            // ---------- ③ 主方向光 ----------
            var dirs = Object.FindObjectsOfType<Light>().Where(l => l.type == LightType.Directional && l.enabled).ToList();
            Light key = dirs.FirstOrDefault(l => l.gameObject.name == "SunLight")
                     ?? dirs.FirstOrDefault(l => l.shadows == LightShadows.None)
                     ?? dirs.FirstOrDefault();
            if (key == null)
            {
                key = new GameObject("ART_KeyLight").AddComponent<Light>();
                key.type = LightType.Directional;
            }
            key.gameObject.name = "ART_KeyLight";
            key.enabled = true;
            key.intensity = KeyIntensity;
            key.color = KeyColor;
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.55f;
            key.shadowBias = 0.04f;
            key.shadowNormalBias = 0.4f;
            key.transform.rotation = Quaternion.Euler(KeyEuler);
            key.lightmapBakeType = LightmapBakeType.Realtime;
            rep.AppendLine($"③ 主光 {key.gameObject.name}: I={KeyIntensity} 柔阴影 强度0.55 旋转{KeyEuler}");
            foreach (var l in dirs)
            {
                if (l == key) continue;
                if (l.gameObject.name.StartsWith("ART_") || l.gameObject.name.StartsWith("HST_")) continue;
                l.intensity = Mathf.Min(l.intensity, 0.05f);
                l.shadows = LightShadows.None;
            }

            // ---------- ④ 点光：抬亮 + 机架洗墙 ----------
            int lifted = 0;
            foreach (var l in Object.FindObjectsOfType<Light>())
            {
                if (l.type != LightType.Point) continue;
                if (l.gameObject.name.StartsWith("HST_PL_"))
                {
                    l.intensity = SiteLightCd;
                    l.range = Mathf.Max(l.range, 55f);
                    l.shadows = LightShadows.None;
                    lifted++;
                }
            }
            rep.AppendLine($"④a HST 区块点光 {lifted} 盏 → {SiteLightCd} cd / range≥55");

            // 机架墙：沿每段墙面每 9m 一盏，离墙 3m、高度取墙高中点
            foreach (var go in Object.FindObjectsOfType<Transform>())
                if (go.name.StartsWith(PracticalPrefix)) Object.DestroyImmediate(go.gameObject);
            var rackRenderers = Object.FindObjectsOfType<Renderer>()
                .Where(r => r.enabled && r.sharedMaterial != null &&
                            (r.sharedMaterial.name.Contains("RackWall") || r.sharedMaterial.name.Contains("S03_Rack")))
                .OrderBy(r => r.name).ToList();
            int practicals = 0;
            foreach (var r in rackRenderers)
            {
                var b = r.bounds;
                // 墙的朝向：用局部 Z 判断（quad 默认面朝 -Z）
                var nrm = r.transform.forward;
                float span = Mathf.Max(b.size.x, b.size.z);
                int n = Mathf.Clamp(Mathf.RoundToInt(span / 9f), 2, 6);
                bool alongX = b.size.x >= b.size.z;
                for (int i = 0; i < n; i++)
                {
                    float t = (i + 0.5f) / n;
                    var p = b.center;
                    if (alongX) p.x = Mathf.Lerp(b.min.x, b.max.x, t);
                    else p.z = Mathf.Lerp(b.min.z, b.max.z, t);
                    p.y = b.center.y;
                    p += nrm * 3.0f;
                    var go = new GameObject($"{PracticalPrefix}{r.name}_{i:00}");
                    go.transform.position = p;
                    var l = go.AddComponent<Light>();
                    l.type = LightType.Point;
                    l.color = new Color(1.0f, 0.95f, 0.88f);
                    l.intensity = PracticalCd;
                    l.range = PracticalRange;
                    l.shadows = LightShadows.None;
                    l.lightmapBakeType = LightmapBakeType.Realtime;
                    practicals++;
                }
            }
            rep.AppendLine($"④b 机架洗墙灯 {practicals} 盏（{PracticalCd} cd / {PracticalRange}m）");

            // ---------- ⑤ 反射探针 ----------
            foreach (var rp in Object.FindObjectsOfType<ReflectionProbe>())
                if (rp.gameObject.name.StartsWith(ProbePrefix)) Object.DestroyImmediate(rp.gameObject);
            var blocks = scene.GetRootGameObjects().Where(g => g.name.StartsWith("BOX_S_")).OrderBy(g => g.name).ToList();
            int probes = 0;
            // 自定义反射 cubemap：金属需要「亮的、有结构的环境」才像金属。
            // 只用实时探针时，室内墙面把探针染黑，金属会变成黑镜子（用户反馈的核心问题）。
            var refl = EnsureReflectionCubemap(rep);
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = refl;
            RenderSettings.reflectionIntensity = 1.6f;
            foreach (var b in blocks)
            {
                var bb = BoundsOf(b);
                if (bb.size.sqrMagnitude < 1e-6f) continue;
                var go = new GameObject($"{ProbePrefix}{b.name.Replace("BOX_", "")}");
                go.transform.position = bb.center + Vector3.up * (bb.size.y * 0.1f);
                var rp = go.AddComponent<ReflectionProbe>();
                rp.mode = ReflectionProbeMode.Realtime;
                rp.refreshMode = ReflectionProbeRefreshMode.EveryFrame;
                rp.timeSlicingMode = ReflectionProbeTimeSlicingMode.AllFacesAtOnce;
                rp.resolution = 256;
                rp.boxProjection = true;
                rp.intensity = 1.4f;
                rp.blendDistance = 2f;
                rp.nearClipPlane = 0.3f;
                rp.farClipPlane = 1500f;
                rp.size = new Vector3(bb.size.x + 30f, Mathf.Max(bb.size.y + 20f, 60f), bb.size.z + 30f);
                probes++;
            }
            rep.AppendLine($"⑤ 反射：自定义 cubemap + {probes} 个 Realtime 探针（256px box projection），全局反射 1.6");

            // ---------- ⑥ 调色 Profile ----------
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, VolumePath);
            }
            var tone = GetOrAdd<Tonemapping>(profile);
            tone.mode.overrideState = true;
            tone.mode.value = TonemappingMode.ACES;
            var ca = GetOrAdd<ColorAdjustments>(profile);
            ca.postExposure.overrideState = true; ca.postExposure.value = 0.15f;
            ca.contrast.overrideState = true; ca.contrast.value = 12f;
            ca.saturation.overrideState = true; ca.saturation.value = 6f;
            ca.colorFilter.overrideState = true; ca.colorFilter.value = new Color(0.98f, 0.99f, 1.0f);
            var vig = GetOrAdd<Vignette>(profile);
            vig.intensity.overrideState = true; vig.intensity.value = 0.22f;
            vig.smoothness.overrideState = true; vig.smoothness.value = 0.45f;
            var grain = GetOrAdd<FilmGrain>(profile);
            grain.type.overrideState = true; grain.type.value = FilmGrainLookup.Thin1;
            grain.intensity.overrideState = true; grain.intensity.value = 0.12f;
            grain.response.overrideState = true; grain.response.value = 0.7f;
            EditorUtility.SetDirty(profile);

            var gv = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "Global Volume");
            if (gv == null)
            {
                gv = new GameObject("Global Volume");
                gv.AddComponent<Volume>();
            }
            var vol = gv.GetComponent<Volume>();
            vol.isGlobal = true;
            vol.weight = 1f;
            vol.sharedProfile = profile;
            EditorUtility.SetDirty(vol);
            rep.AppendLine($"⑥ Volume：ACES / exposure +0.25 / contrast +12 / saturation +6 / vignette .22 / grain .12 → {VolumePath}");

            // ---------- ⑦ MapSkin 材质口径 ----------
            int mats = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets/Art/Materials/MapSkin" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null || m.shader == null || !m.shader.name.Contains("Universal")) continue;
                m.SetFloat("_Metallic", 0.8f);
                m.SetFloat("_Smoothness", 1f);
                m.SetFloat("_SmoothnessTextureChannel", 0f);
                if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
                EditorUtility.SetDirty(m);
                mats++;
            }
            rep.AppendLine($"⑦ MapSkin 材质 {mats} 份：metallic/smoothness 交给贴图通道");

            // ---------- 存盘 + 渲染探针 ----------
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            foreach (var rp in Object.FindObjectsOfType<ReflectionProbe>())
                if (rp.gameObject.name.StartsWith(ProbePrefix)) rp.RenderProbe();
            rep.AppendLine("    场景已存盘，探针已 RenderProbe");

            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath) ?? ".");
            File.WriteAllText(ReportPath, rep.ToString(), Encoding.UTF8);
            Debug.Log(rep.ToString());
        }

        static T GetOrAdd<T>(VolumeProfile p) where T : VolumeComponent
        {
            if (p.TryGet<T>(out var c)) return c;
            return p.Add<T>(true);
        }

        /// <summary>
        /// 程序化反射 cubemap：顶部冷灰天光、地平线一条亮带（工业高窗/灯槽）、
        /// 地面深灰，正前方一个柔和的暖白亮斑。金属反射它就有「室内工业空间」的
        /// 层次，而不是把黑墙反射成黑镜子。
        /// </summary>
        static Cubemap EnsureReflectionCubemap(StringBuilder rep)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Cubemap>(ReflCubemapPath);
            if (existing != null) return existing;

            const int S = 128;
            var cube = new Cubemap(S, TextureFormat.RGBAFloat, true, false);
            var dirs = new[]
            {
                Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back
            };
            var px = new Color[S * S];
            for (int f = 0; f < 6; f++)
            {
                for (int y = 0; y < S; y++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        // 立方体面内坐标 → 方向
                        float u = (x + 0.5f) / S * 2f - 1f;
                        float v = (y + 0.5f) / S * 2f - 1f;
                        Vector3 d;
                        switch (f)
                        {
                            case 0: d = new Vector3(1, -v, -u); break;
                            case 1: d = new Vector3(-1, -v, u); break;
                            case 2: d = new Vector3(u, 1, v); break;
                            case 3: d = new Vector3(u, -1, -v); break;
                            case 4: d = new Vector3(u, -v, 1); break;
                            default: d = new Vector3(-u, -v, -1); break;
                        }
                        d.Normalize();
                        float h = d.y;
                        // 天顶 → 地平线 → 地面
                        Color top = new Color(0.34f, 0.42f, 0.55f);
                        Color horizon = new Color(0.85f, 0.90f, 0.98f);
                        Color ground = new Color(0.10f, 0.11f, 0.13f);
                        Color c = h >= 0f
                            ? Color.Lerp(horizon, top, Mathf.Pow(h, 0.55f))
                            : Color.Lerp(horizon, ground, Mathf.Pow(-h, 0.35f));
                        // 地平线亮带（tall windows / light coves）
                        float band = Mathf.Exp(-Mathf.Pow((h - 0.06f) / 0.055f, 2f));
                        c += new Color(0.55f, 0.62f, 0.72f) * (band * 0.8f);
                        // 暖色主光斑：偏向 -Z +X
                        Vector3 sunDir = new Vector3(0.45f, 0.55f, -0.70f).normalized;
                        float sun = Mathf.Pow(Mathf.Clamp01(Vector3.Dot(d, sunDir)), 220f);
                        c += new Color(1.0f, 0.85f, 0.62f) * (sun * 6.0f);
                        // 稀疏竖向暗条，给反射一点结构
                        float stripe = Mathf.SmoothStep(0.86f, 0.98f,
                            Mathf.Abs(Mathf.Sin(Mathf.Atan2(d.z, d.x) * 9f)));
                        c *= 1f - stripe * 0.18f * Mathf.Clamp01(h + 0.2f);
                        c.a = 1f;
                        px[y * S + x] = c;
                    }
                }
                cube.SetPixels(px, (CubemapFace)f);
            }
            cube.Apply(true, true);
            AssetDatabase.CreateAsset(cube, ReflCubemapPath);
            AssetDatabase.SaveAssets();
            rep.AppendLine($"    新建反射 cubemap {ReflCubemapPath}（{S}px，程序化工业室内）");
            return cube;
        }

        static Bounds BoundsOf(GameObject root)
        {
            var rs = root.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled).ToList();
            if (rs.Count == 0) return new Bounds(root.transform.position, Vector3.zero);
            var b = rs[0].bounds;
            foreach (var r in rs.Skip(1)) b.Encapsulate(r.bounds);
            return b;
        }
    }
}
