// SkinLookTune.cs — 换皮场景的「外观基调」快速调节（Editor-only，可重复执行）
//
// 为什么单独一个工具：FacadeRebuild 生成的是构件与材质资产，ArtDirection 打的是
// 天空/环境光/主光/实用灯/探针；两者都需要一张「快照式」的调色板才能落到画面。
// 本工具只做数值口径，不动几何、不动碰撞、不动 Level1.unity：
//   ① 环境光（Trilight 三色 + 强度）
//   ② 主方向光强度（绝对值，幂等）
//   ③ 实用点光强度/射程（只动名字里带 PL / ART_ / HST_ 的，绝对值）
//   ④ 换皮专用 Volume Profile（Assets/Settings/Level1_Skin_Look.asset，自建不共享）
//      —— Tonemapping ACES / ColorAdjustments 曝光·对比·饱和 / Vignette / FilmGrain
//   ⑤ 包板材质（Assets/Art/Materials/MapSkin/Facade/M_Facade_*.mat）基色·金属度·光滑度
//      + NPR 三件套（_ShadowColor 中性化，避免整片青蓝影调）
//
// 菜单：MapSkin/外观基调/① 应用到当前换皮场景
// 批处理：-executeMethod MapSkin.SkinLookTune.Run
// 参数：-scene / -ambient / -amb / -key / -point / -pointrange / -exposure / -contrast
//       -satur / -shadow r,g,b / -base r,g,b / -panela r,g,b / -panelb r,g,b / -trim r,g,b
//       -metala / -smootha ...（省略则保留默认表）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MapSkin
{
    public static class SkinLookTune
    {
        static string ScenePath = "Assets/Scenes/Level1_Skin.unity";
        const string ProfilePath = "Assets/Settings/Level1_Skin_Look.asset";
        const string FacadeMatDir = "Assets/Art/Materials/MapSkin/Facade";

        // ---- 默认口径（工业写实：低环境、中性影调、暖主光、暗包板）----
        static float Ambient = 0.55f;
        static Color AmbSky = new Color(0.40f, 0.43f, 0.48f);
        static Color AmbEq = new Color(0.28f, 0.31f, 0.35f);
        static Color AmbGnd = new Color(0.11f, 0.12f, 0.14f);
        static float Key = 1.1f;          // 主方向光强度（绝对）
        static float PointCd = 140f;      // 实用点光强度（绝对）
        static float PointRange = 40f;    // 实用点光射程下限
        static float Exposure = -0.25f;
        static float Contrast = 6f;
        static float Satur = -6f;
        static float Vignette = 0.24f;
        static float Grain = 0.10f;
        static Color Shadow = new Color(0.17f, 0.19f, 0.23f);   // NPR 影调色，中性偏冷灰

        // 顶灯阵列 / 雾 / 天空
        static int CeilLights = 1;
        static float CeilCd = 130f;
        static float CeilRange = 30f;
        static float CeilGrid = 12f;      // 顶灯网格间距（米）
        static int CeilMax = 260;         // 顶灯总盏数上限（实时点光）
        static int Fog = 1;
        static float FogDensity = 0.0035f;
        static Color FogColor = new Color(0.30f, 0.34f, 0.39f);
        static float SkyExposure = 0.55f;
        const string SkyMatPath = "Assets/Settings/Level1_Skin_Sky.mat";
        const string FacesPath = "F:/VSCode/taptap-game-jem/_session_audit/face_flat.json";

        // 包板材质：基色 / 金属度 / 光滑度（按名字匹配到 v1 的 Base/PanelA/PanelB/Trim 与 v2 的 7 族）
        static readonly Color[] MatColors =
        {
            new Color(0.135f, 0.145f, 0.160f),   // Base 石墨
            new Color(0.330f, 0.335f, 0.345f),   // PanelA 浅暖灰
            new Color(0.215f, 0.220f, 0.230f),   // PanelB 中灰
            new Color(0.400f, 0.405f, 0.420f),   // Trim 钢
        };
        static readonly float[] MatMetal = { 0.55f, 0.15f, 0.25f, 0.85f };
        static readonly float[] MatSmooth = { 0.28f, 0.38f, 0.42f, 0.55f };

        [MenuItem("MapSkin/外观基调/① 应用到当前换皮场景")]
        public static void RunMenu() => Run();

        public static void Run()
        {
            ParseArgs();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var rep = new System.Text.StringBuilder();
            rep.AppendLine($"[外观基调] {DateTime.Now:yyyy-MM-dd HH:mm} 场景 {ScenePath}");

            // ---------- ① 环境光 ----------
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = AmbSky;
            RenderSettings.ambientEquatorColor = AmbEq;
            RenderSettings.ambientGroundColor = AmbGnd;
            RenderSettings.ambientIntensity = Ambient;
            rep.AppendLine($"① 环境光 ×{Ambient:F2} sky={C(AmbSky)} eq={C(AmbEq)} gnd={C(AmbGnd)}");

            // ---------- ②③ 灯光（绝对值，幂等）----------
            int dir = 0, pts = 0;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>(true))
            {
                if (l == null) continue;
                if (l.type == LightType.Directional) { l.intensity = Key; dir++; continue; }
                var n = l.gameObject.name.ToUpperInvariant();
                if (n.Contains("PL") || n.Contains("ART_") || n.Contains("HST_"))
                {
                    l.intensity = PointCd;
                    if (l.range < PointRange) l.range = PointRange;
                    pts++;
                }
            }
            rep.AppendLine($"②③ 主光 {dir} 盏 → {Key:F2}；实用灯 {pts} 盏 → {PointCd:F0} cd / range≥{PointRange:F0}");

            // ---------- ④ 换皮专用 Volume Profile ----------
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (profile == null)
            {
                EnsureFolder(ProfilePath);
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, ProfilePath);
            }
            var tone = GetOrAdd<Tonemapping>(profile);
            tone.mode.overrideState = true; tone.mode.value = TonemappingMode.ACES;
            var ca = GetOrAdd<ColorAdjustments>(profile);
            ca.postExposure.overrideState = true; ca.postExposure.value = Exposure;
            ca.contrast.overrideState = true; ca.contrast.value = Contrast;
            ca.saturation.overrideState = true; ca.saturation.value = Satur;
            var vg = GetOrAdd<Vignette>(profile);
            vg.intensity.overrideState = true; vg.intensity.value = Vignette;
            var fg = GetOrAdd<FilmGrain>(profile);
            fg.intensity.overrideState = true; fg.intensity.value = Grain;
            EditorUtility.SetDirty(profile);

            var gv = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "Global Volume");
            if (gv == null) gv = new GameObject("Global Volume");
            var vol = gv.GetComponent<Volume>() ?? gv.AddComponent<Volume>();
            vol.isGlobal = true; vol.priority = 10f; vol.sharedProfile = profile;
            rep.AppendLine($"④ Volume → {ProfilePath}：曝光 {Exposure:F2} / 对比 {Contrast:F0} / 饱和 {Satur:F0} / 暗角 {Vignette:F2}");

            // ---------- ⑤ 包板材质（按名字匹配，兼容 v1 的 4 族与 v2 的 7 族）----------
            int mats = 0;
            var matGuids = AssetDatabase.FindAssets("M_Facade_ t:Material", new[] { FacadeMatDir });
            foreach (var g in matGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null) continue;
                var n = m.name;
                Color col; float met, smo;
                if (n.Contains("Emissive") || n.Contains("Hazard")) { col = Color.clear; met = -1; smo = -1; }
                else if (n.Contains("PaintLight") || n.Contains("PanelA")) { col = MatColors[1]; met = MatMetal[1]; smo = MatSmooth[1]; }
                else if (n.Contains("PaintMid") || n.Contains("PanelB")) { col = MatColors[2]; met = MatMetal[2]; smo = MatSmooth[2]; }
                else if (n.Contains("MetalDark")) { col = new Color(0.075f, 0.080f, 0.090f); met = 0.88f; smo = 0.42f; }
                else if (n.Contains("Trim")) { col = MatColors[3]; met = MatMetal[3]; smo = MatSmooth[3]; }
                else { col = MatColors[0]; met = MatMetal[0]; smo = MatSmooth[0]; }   // Base 及其他
                if (col != Color.clear)
                {
                    if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", col);
                    if (m.HasProperty("_Metallic") && met >= 0) m.SetFloat("_Metallic", met);
                    if (m.HasProperty("_Smoothness") && smo >= 0) m.SetFloat("_Smoothness", smo);
                }
                if (m.HasProperty("_ShadowColor")) m.SetColor("_ShadowColor", Shadow);
                if (m.HasProperty("_ShadowColor2")) m.SetColor("_ShadowColor2", Shadow);
                EditorUtility.SetDirty(m);
                mats++;
            }
            AssetDatabase.SaveAssets();
            rep.AppendLine($"⑤ 包板材质 {mats} 个（按名匹配，v1/v2 兼容）：Base {C(MatColors[0])} / PaintLight {C(MatColors[1])} / PaintMid {C(MatColors[2])} / MetalDark (0.08,0.08,0.09) / Trim {C(MatColors[3])}；影调色 {C(Shadow)}");

            // ---------- ⑥ 雾 + 天空（只改本场景的 RenderSettings / 本场景专用天空材质）----------
            if (Fog == 1)
            {
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.ExponentialSquared;
                RenderSettings.fogDensity = FogDensity;
                RenderSettings.fogColor = FogColor;
                rep.AppendLine($"⑥ 雾 ExponentialSquared 密度 {FogDensity:F4} 色 {C(FogColor)}");
            }
            else RenderSettings.fog = false;

            var builtinSky = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
            if (builtinSky != null)
            {
                var sky = AssetDatabase.LoadAssetAtPath<Material>(SkyMatPath);
                if (sky == null)
                {
                    sky = new Material(builtinSky);
                    EnsureFolder(SkyMatPath);
                    AssetDatabase.CreateAsset(sky, SkyMatPath);
                }
                if (sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", SkyExposure);
                if (sky.HasProperty("_Tint")) sky.SetColor("_Tint", new Color(0.62f, 0.66f, 0.72f));
                EditorUtility.SetDirty(sky);
                RenderSettings.skybox = sky;
                AssetDatabase.SaveAssets();
                rep.AppendLine($"⑥ 天空 {SkyMatPath}（曝光 {SkyExposure:F2}，本场景专用，不动 builtin 资源）");
            }

            // ---------- ⑦ 顶灯阵列：按地板面铺网格，逐格取该处天花高度 ----------
            var old = new List<GameObject>();
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
                if (t != null && t.name.StartsWith("ART_CL_")) old.Add(t.gameObject);
            foreach (var go in old) UnityEngine.Object.DestroyImmediate(go);
            int placed = 0;
            if (CeilLights == 1)
            {
                var faces = LoadFaces();
                if (faces != null && faces.Count > 0)
                {
                    // 优先只照"可走地板"（玩得到的区域），太少再退回全部地板
                    var all = faces.Where(f => f.n != null && f.n.Length == 3 && f.n[1] > 0.6f && f.w * f.h >= 4f).ToList();
                    var walk = all.Where(f => f.isWalkable).ToList();
                    var floors = walk.Count >= 40 ? walk : all;
                    var ceils = faces.Where(f => f.n != null && f.n.Length == 3 && f.n[1] < -0.6f && f.w * f.h >= 4f).ToList();
                    var root = new GameObject("ART_CeilLights");
                    float minX = floors.Min(f => f.worldCenter[0]), maxX = floors.Max(f => f.worldCenter[0]);
                    float minZ = floors.Min(f => f.worldCenter[2]), maxZ = floors.Max(f => f.worldCenter[2]);
                    var cand = new List<Vector3>();
                    for (float x = minX; x <= maxX + 0.01f; x += CeilGrid)
                        for (float z = minZ; z <= maxZ + 0.01f; z += CeilGrid)
                        {
                            float fy = float.MinValue, cy = float.MaxValue;
                            foreach (var f in floors)
                                if (InFootprint(f, x, z, 6f) && f.worldCenter[1] > fy) fy = f.worldCenter[1];
                            if (fy == float.MinValue) continue;
                            foreach (var f in ceils)
                                if (InFootprint(f, x, z, 6f) && f.worldCenter[1] > fy + 1.5f && f.worldCenter[1] < cy)
                                    cy = f.worldCenter[1];
                            float y = cy == float.MaxValue ? fy + 5.5f : Mathf.Min(cy - 0.8f, fy + 6.5f);
                            cand.Add(new Vector3(x, y, z));
                        }
                    // 上限兜底：候选太多就按步长抽稀，保证总盏数 ≤ CeilMax（实时点光不能无脑铺）
                    int stride = Mathf.Max(1, Mathf.CeilToInt(cand.Count / (float)CeilMax));
                    for (int i = 0; i < cand.Count; i += stride)
                    {
                        var go = new GameObject($"ART_CL_{placed:D3}");
                        go.transform.SetParent(root.transform, false);
                        go.transform.position = cand[i];
                        var l = go.AddComponent<Light>();
                        l.type = LightType.Point;
                        l.color = new Color(1.0f, 0.97f, 0.92f);
                        l.intensity = CeilCd;
                        l.range = CeilRange;
                        l.shadows = LightShadows.None;
                        placed++;
                    }
                    rep.AppendLine($"⑦ 顶灯阵列 {placed}/{cand.Count} 盏（候选 {cand.Count}，抽稀步长 {stride}；网格 {CeilGrid:F0}m，{CeilCd:F0} cd / {CeilRange:F0} m，无阴影）");
                }
                else rep.AppendLine("⑦ 顶灯阵列：读不到面表，跳过");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log(rep.ToString());
        }

        static string C(Color c) => $"({c.r:F2},{c.g:F2},{c.b:F2})";

        // ---------------- 面表读取（只取顶灯阵列需要的字段；类名与 FacadeRebuild 的区分开）----------------

        [Serializable] class LookFaceDoc { public int version; public List<LookFace> faces = new List<LookFace>(); }

        [Serializable] class LookFace
        {
            public string id, objectPath, style, shape, sourceMaterial, seed, adjacentStyle;
            public int rendererIndex, submesh, panelIndex, panelCount, variantIndex;
            public float w, h, wallHeightM;
            public bool small, isWalkable;
            public float[] origin, u, v, n, outline, worldCenter, baseColor;
        }

        static List<LookFace> LoadFaces()
        {
            try
            {
                if (!System.IO.File.Exists(FacesPath)) return null;
                var doc = JsonUtility.FromJson<LookFaceDoc>(System.IO.File.ReadAllText(FacesPath));
                return doc?.faces;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[外观基调] 面表读取失败：{e.Message}");
                return null;
            }
        }

        /// <summary>点 (x,z) 是否落在这个水平面的投影里（含 pad）。只对地板/天花用（u、v 均水平）。</summary>
        static bool InFootprint(LookFace f, float x, float z, float pad)
        {
            if (f.origin == null || f.u == null || f.v == null) return false;
            float dx = x - f.origin[0], dz = z - f.origin[2];
            float lu = dx * f.u[0] + dz * f.u[2];
            float lv = dx * f.v[0] + dz * f.v[2];
            return Mathf.Abs(lu) <= f.w * 0.5f + pad && Mathf.Abs(lv) <= f.h * 0.5f + pad;
        }

        static T GetOrAdd<T>(VolumeProfile p) where T : VolumeComponent
        {
            if (p.TryGet<T>(out var c) && c != null) return c;
            return p.Add<T>(true);
        }

        static void EnsureFolder(string assetPath)
        {
            var dir = assetPath.Substring(0, assetPath.LastIndexOf('/'));
            EnsureFolderRec(dir);
        }

        static void EnsureFolderRec(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
            int i = path.LastIndexOf('/');
            var parent = i > 0 ? path.Substring(0, i) : "Assets";
            var name = i >= 0 ? path.Substring(i + 1) : path;
            EnsureFolderRec(parent);
            if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(parent, name);
        }

        static void ParseArgs()
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
            {
                switch (a[i])
                {
                    case "-scene": ScenePath = a[i + 1]; break;
                    case "-ambient": Ambient = Fl(a[i + 1], Ambient); break;
                    case "-amb": AmbSky = Col(a[i + 1], AmbSky); break;
                    case "-key": Key = Fl(a[i + 1], Key); break;
                    case "-point": PointCd = Fl(a[i + 1], PointCd); break;
                    case "-pointrange": PointRange = Fl(a[i + 1], PointRange); break;
                    case "-exposure": Exposure = Fl(a[i + 1], Exposure); break;
                    case "-contrast": Contrast = Fl(a[i + 1], Contrast); break;
                    case "-satur": Satur = Fl(a[i + 1], Satur); break;
                    case "-vignette": Vignette = Fl(a[i + 1], Vignette); break;
                    case "-grain": Grain = Fl(a[i + 1], Grain); break;
                    case "-shadow": Shadow = Col(a[i + 1], Shadow); break;
                    case "-base": MatColors[0] = Col(a[i + 1], MatColors[0]); break;
                    case "-panela": MatColors[1] = Col(a[i + 1], MatColors[1]); break;
                    case "-panelb": MatColors[2] = Col(a[i + 1], MatColors[2]); break;
                    case "-trim": MatColors[3] = Col(a[i + 1], MatColors[3]); break;
                    case "-ceillights": CeilLights = In(a[i + 1], CeilLights); break;
                    case "-ceilcd": CeilCd = Fl(a[i + 1], CeilCd); break;
                    case "-ceilrange": CeilRange = Fl(a[i + 1], CeilRange); break;
                    case "-ceilgrid": CeilGrid = Fl(a[i + 1], CeilGrid); break;
                    case "-fog": Fog = In(a[i + 1], Fog); break;
                    case "-fogdensity": FogDensity = Fl(a[i + 1], FogDensity); break;
                    case "-fogcolor": FogColor = Col(a[i + 1], FogColor); break;
                    case "-skyexposure": SkyExposure = Fl(a[i + 1], SkyExposure); break;
                }
            }
        }

        static float Fl(string s, float d) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;

        static int In(string s, int d) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;

        static Color Col(string s, Color d)
        {
            var parts = (s ?? "").Split(',');
            if (parts.Length < 3) return d;
            return new Color(Fl(parts[0], d.r), Fl(parts[1], d.g), Fl(parts[2], d.b), 1f);
        }
    }
}
