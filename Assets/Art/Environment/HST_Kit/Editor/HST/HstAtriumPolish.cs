#if UNITY_EDITOR
// 中庭抛光 (2026-10-13, 两项用户令):
// ① 点缀灯轻微发光: M_M_cyan / M_M_blue_dot 从 tmp/relight/glow_baseline.psv 恢复原 HDR 基色
//    (亮度 1.494 / 1.385, 刚过 TA Bloom 亮度阈值 1.0 + 软膝 0.5 → 轻微光晕, 不是原来那种大光晕)。
//    HstRelight.FixGlow 已加同名豁免, 之后重打光不会再把它们压回 0.92。
// ② 室内天花板防闪烁 (coplanar_audit 实测, 2026-10-13):
//    - 天花面板 SM_panels_room_ceil_m0..m3 顶面 z=9.000 与 天花基底 SM_sub_room_ceil 底面 z=9.000
//      共面 1678 对 ≈148.9m² —— 正对玩家头顶, 相机一动即闪;
//    - 天花灯带 roomlight_000..005 底面 z=8.925 与面板底面共面 ≈33m²。
//    修法 (用户令"直接改Unity端", 不动 FBX/不重导): 基底 +0.02m, 灯带 -0.02m —— 共面分离,
//    不产生新共面(灯带顶 8.96 < 新基底底 9.02)。位移与守卫见 ApplyCeilingFix, 基线记
//    tmp/relight/atrium_polish.psv, 幂等可重跑。
// ③ 出图验证: 天花板专项仰视图(修前/修后) + 概念厅 5 机位 → renders/atrium_polish/。
// 触发: GUI 编辑器占锁时投递 project-0/Temp/hst_atrium_polish.request (2 秒轮询, 空闲才跑)。
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HST
{
    public static class HstAtriumPolish
    {
        const string ScenePath = "Assets/Art/Environment/HST_Kit/Scenes/SC_Atrium_Concept.unity";
        const string OutDir = @"F:\VSCode\工业级渲染\renders\atrium_polish";
        const string WorkDir = @"F:\VSCode\工业级渲染\tmp\relight";
        const string Marker = WorkDir + "/atrium_polish.psv";
        const int W = 1920, H = 1080;

        // 基色恢复目标 (与 glow_baseline.psv 一致; psv 缺失时兜底)
        static readonly Dictionary<string, Color> AccentFallback = new Dictionary<string, Color>
        {
            { "M_M_cyan", new Color(0.36f, 1.755f, 2.25f, 1f) },
            { "M_M_blue_dot", new Color(0.45f, 1.5f, 3.0f, 1f) },
        };

        [MenuItem("HST/中庭 抛光(点缀灯微光+天花板防闪烁+出图)")]
        public static void Run()
        {
            Directory.CreateDirectory(OutDir);
            Directory.CreateDirectory(WorkDir);

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            int guardL = Object.FindObjectsOfType<Light>(true).Length;
            int guardC = Object.FindObjectsOfType<Camera>(true).Length;
            if (guardL == 0 || guardC == 0)
            {
                Debug.LogError($"[POL] 场景打开后 灯{guardL}/相机{guardC}, 疑似损坏, 全部中止不写盘");
                return;
            }

            Shot(GetCeilingCam("POL_before"), Path.Combine(OutDir, "ceiling_before.png"), "ceiling/before");
            ClearCam();

            ApplyCeilingFix();
            ApplyAccentGlow();

            Shot(GetCeilingCam("POL_after"), Path.Combine(OutDir, "ceiling_after.png"), "ceiling/after");
            ClearCam();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[POL] 场景已存盘 (天花板位移 + 本次会话临时相机未写入)");

            ShootConcept();
            Debug.Log("[POL] 全部完成 → " + OutDir);
        }

        // ---------------------------------------------------------------- ② 天花板防闪烁
        static void ApplyCeilingFix()
        {
            var done = new HashSet<string>();
            if (File.Exists(Marker))
                foreach (var line in File.ReadAllLines(Marker)) done.Add(line.Trim());

            // 基底 +0.02
            if (!done.Contains("sub+0.02"))
            {
                var sub = Object.FindObjectsOfType<MeshFilter>(true)
                    .FirstOrDefault(mf => mf.name == "SM_sub_room_ceil");
                if (sub == null) { Debug.LogError("[POL] 未找到 SM_sub_room_ceil, 天花板修复中止"); return; }
                var b = sub.GetComponent<Renderer>().bounds;
                if (Mathf.Abs(b.min.y - 9.0f) > 0.02f)
                {
                    Debug.LogError($"[POL] SM_sub_room_ceil bounds.min.y={b.min.y:F3} ≠ 9.000, 几何对不上, 中止不写盘");
                    return;
                }
                sub.transform.position += Vector3.up * 0.02f;
                EditorUtility.SetDirty(sub.transform);
                done.Add("sub+0.02");
                Debug.Log($"[POL] SM_sub_room_ceil y +0.02 (bounds.min.y {b.min.y:F3} → {(b.min.y + 0.02f):F3})");
            }

            // 灯带 -0.02
            if (!done.Contains("roomlight-0.02"))
            {
                int n = 0;
                foreach (var mf in Object.FindObjectsOfType<MeshFilter>(true)
                             .Where(m => m.name.StartsWith("roomlight_")))
                {
                    var b = mf.GetComponent<Renderer>().bounds;
                    if (Mathf.Abs(b.min.y - 8.925f) > 0.02f || Mathf.Abs(b.max.y - 8.98f) > 0.02f)
                    {
                        Debug.LogWarning($"[POL] {mf.name} bounds y=[{b.min.y:F3},{b.max.y:F3}] 与预期 [8.925,8.98] 不符, 跳过");
                        continue;
                    }
                    mf.transform.position += Vector3.down * 0.02f;
                    EditorUtility.SetDirty(mf.transform);
                    n++;
                }
                done.Add("roomlight-0.02");
                Debug.Log($"[POL] roomlight_* y -0.02, 共 {n} 条");
            }

            File.WriteAllLines(Marker, done);
            LogCeilingGap();
        }

        // 修后量化断言: 基底底面与面板顶面的间隙
        static void LogCeilingGap()
        {
            float panelTop = -999f;
            foreach (var mf in Object.FindObjectsOfType<MeshFilter>(true)
                         .Where(m => m.name.StartsWith("SM_panels_room_ceil")))
                panelTop = Mathf.Max(panelTop, mf.GetComponent<Renderer>().bounds.max.y);
            var sub = Object.FindObjectsOfType<MeshFilter>(true)
                .FirstOrDefault(m => m.name == "SM_sub_room_ceil");
            if (sub == null) return;
            float gap = sub.GetComponent<Renderer>().bounds.min.y - panelTop;
            Debug.Log($"[POL-ASSERT] 天花间隙 = 基底底({sub.GetComponent<Renderer>().bounds.min.y:F3}) - 面板顶({panelTop:F3}) = {gap * 1000:F1}mm (目标 ≥15mm, 原 0.0mm)");
        }

        // ---------------------------------------------------------------- ① 点缀灯轻微发光
        static void ApplyAccentGlow()
        {
            var baseline = new Dictionary<string, Color>(AccentFallback);
            var psv = Path.Combine(WorkDir, "glow_baseline.psv");
            if (File.Exists(psv))
                foreach (var line in File.ReadAllLines(psv))
                {
                    var kv = line.Split('=');
                    if (kv.Length != 2) continue;
                    var nm = Path.GetFileNameWithoutExtension(kv[0]);
                    if (!AccentFallback.ContainsKey(nm)) continue;
                    var rgba = kv[1].Split(',');
                    if (rgba.Length != 4) continue;
                    baseline[nm] = new Color(float.Parse(rgba[0]), float.Parse(rgba[1]),
                                             float.Parse(rgba[2]), float.Parse(rgba[3]));
                }

            foreach (var kv in baseline)
            {
                var guid = AssetDatabase.FindAssets(kv.Key + " t:Material",
                    new[] { "Assets/Art/Environment/HST_Kit/Materials/HST_Prags" })
                    .Select(g => AssetDatabase.GUIDToAssetPath(g))
                    .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == kv.Key);
                if (guid == null) { Debug.LogWarning($"[POL] 未找到材质 {kv.Key}"); continue; }
                var m = AssetDatabase.LoadAssetAtPath<Material>(guid);
                var orig = m.GetColor("_BaseColor");
                m.SetColor("_BaseColor", kv.Value);
                EditorUtility.SetDirty(m);
                Debug.Log($"[POL] 点缀灯 {kv.Key}: ({orig.r:F2},{orig.g:F2},{orig.b:F2}) → " +
                          $"({kv.Value.r:F3},{kv.Value.g:F3},{kv.Value.b:F3}) " +
                          $"亮度={Lum(kv.Value):F3} (Bloom 阈值 1.0, 轻微过阈=轻微光晕)");
            }
            AssetDatabase.SaveAssets();
        }

        static float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        // ---------------------------------------------------------------- ③ 出图
        static Camera _polishCam;

        static Camera GetCeilingCam(string label)
        {
            var go = new GameObject(label);
            _polishCam = go.AddComponent<Camera>();
            // Unity 系坐标 = (Blender -x, z, -y); 房间中心 Blender (0, 3.8, 1.6) 仰视天花
            _polishCam.transform.position = new Vector3(0f, 1.6f, -3.8f);
            _polishCam.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.back);
            _polishCam.fieldOfView = 90f;
            _polishCam.nearClipPlane = 0.05f;
            _polishCam.farClipPlane = 3000f;
            _polishCam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            return _polishCam;
        }

        static void ClearCam()
        {
            if (_polishCam != null) Object.DestroyImmediate(_polishCam.gameObject);
            _polishCam = null;
        }

        static void ShootConcept()
        {
            var cams = Object.FindObjectsOfType<Camera>(true)
                .Where(c => c.name.StartsWith("CAM_")).OrderBy(c => c.name).ToArray();
            if (cams.Length == 0) { Debug.LogWarning("[POL] 无 CAM_ 机位"); return; }
            Shot(cams[0], Path.Combine(WorkDir, "pol_warmup.png"), "warmup");
            foreach (var cam in cams)
            {
                var go = cam.gameObject;
                bool goB = go.activeSelf, enB = cam.enabled;
                go.SetActive(true); cam.enabled = true;
                var cd = cam.GetUniversalAdditionalCameraData();
                bool ppB = cd != null && cd.renderPostProcessing;
                if (cd != null) cd.renderPostProcessing = true;
                cam.nearClipPlane = 0.05f; cam.farClipPlane = 3000f;
                Shot(cam, Path.Combine(WorkDir, "pol_warm2.png"), cam.name + "/warm2");   // TAA 第二帧
                Shot(cam, Path.Combine(OutDir, "unity_Conc_" + cam.name + ".png"), cam.name);
                if (cd != null) cd.renderPostProcessing = ppB;
                cam.enabled = enB; go.SetActive(goB);
            }
        }

        // 与 HstRelight.Shot 同口径 (HDR → sRGB PNG + Rec.709 统计)
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
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(t8);
            Debug.Log(string.Format("[POL-STAT] {0} mean={1:F3} 死黑={2:F2}% 过曝={3:F2}%",
                label, sum / px.Length, 100.0 * dark / px.Length, 100.0 * blown / px.Length));
        }

        static float L2S(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(Mathf.Max(c, 0f), 1f / 2.4f) - 0.055f;
    }

    // 编辑器占锁时的投递通道 (结构照抄 HstHallNprTrigger)
    [InitializeOnLoad]
    public static class HstAtriumPolishTrigger
    {
        const string REQ = "Temp/hst_atrium_polish.request";
        static double _nextCheck;

        static HstAtriumPolishTrigger() { EditorApplication.update += Tick; }

        static void Tick()
        {
            if (EditorApplication.timeSinceStartup < _nextCheck) return;
            _nextCheck = EditorApplication.timeSinceStartup + 2.0;
            if (!File.Exists(REQ)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            foreach (var s in EditorSceneManager.GetAllScenes())
                if (s.isDirty)
                { Debug.Log("[POL-REQ] 场景有未保存改动, 请求挂起(保存后自动继续)"); return; }

            File.Delete(REQ);
            Debug.Log("[POL-REQ] 收到中庭抛光请求, 开始…");
            HstAtriumPolish.Run();
            Debug.Log("[POL-REQ] 请求完成");
        }
    }
}
#endif
