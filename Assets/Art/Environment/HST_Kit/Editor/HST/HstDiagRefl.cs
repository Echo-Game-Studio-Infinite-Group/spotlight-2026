#if UNITY_EDITOR
// 一次性诊断(2026-10-13): 两厅换新反射立方图后全屏黑(仅自发光可见)。单进程内隔离变量:
// 同一场景连渲四态——①现状(新cube+旧探针停用) ②探针恢复 ③reflection换回EXR ④新cube+探针停用(复现),
// 定位"原生 .asset 立方图作 customReflection"与"停用旧烘焙探针"哪个把间接项打成黑。全程不 SaveScene。
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace HST
{
    public static class HstDiagRefl
    {
        const string OutDir = @"F:\VSCode\工业级渲染\tmp\relight";
        static Cubemap _newCube, _exrCube;
        static ReflectionProbe _probe;

        [MenuItem("HST/诊断 反射立方图黑屏来源(只读, 还原)")]
        public static void Run()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Atrium_Military.unity");
            var camGo = GameObject.Find("CAM_Panorama");
            var cam = camGo.GetComponent<Camera>();
            bool goB = camGo.activeSelf; camGo.SetActive(true);

            foreach (var p in UnityEngine.Object.FindObjectsOfType<ReflectionProbe>(true)) _probe = p;
            _newCube = AssetDatabase.LoadAssetAtPath<Cubemap>("Assets/Art/Environment/HST_Kit/ReflectionProbes/RP_Manual_Mil.asset");
            _exrCube = AssetDatabase.LoadAssetAtPath<Cubemap>("Assets/Art/Environment/HST_Kit/ReflectionProbes/RP_SC_Atrium_Military.exr");
            Debug.Log($"[DREFL] 新cube={(_newCube != null)} exr={(_exrCube != null)} probe={(_probe != null)} " +
                      $"probeEnabled={(_probe != null && _probe.enabled)} refl={RenderSettings.customReflection?.name} " +
                      $"mode={RenderSettings.defaultReflectionMode} ambient={RenderSettings.ambientMode} " +
                      $"ambLight={RenderSettings.ambientLight.r:F3}");

            // 先验证 cube 内容本身没有 NaN
            if (_newCube != null)
            {
                var px = _newCube.GetPixels(CubemapFace.PositiveZ);
                int nan = 0, neg = 0; float mx = 0;
                foreach (var c in px)
                {
                    if (float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b) ||
                        float.IsInfinity(c.r) || float.IsInfinity(c.g) || float.IsInfinity(c.b)) nan++;
                    if (c.r < 0 || c.g < 0 || c.b < 0) neg++;
                    mx = Mathf.Max(mx, Mathf.Max(c.r, Mathf.Max(c.g, c.b)));
                }
                Debug.Log($"[DREFL] 新cube +Z 面: NaN={nan} 负值={neg} max={mx:F2} (总 {px.Length} 像素)");
            }

            Shot(cam, "1_现状_新cube_探针关");

            if (_probe != null) { _probe.enabled = true; Shot(cam, "2_探针恢复"); }

            RenderSettings.customReflection = _exrCube;
            if (_probe != null) _probe.enabled = false;
            Shot(cam, "3_EXR反射_探针关");

            RenderSettings.customReflection = _newCube;
            Shot(cam, "4_新cube_探针关_复现");

            RenderSettings.customReflection = _exrCube;
            if (_probe != null) _probe.enabled = true;
            Shot(cam, "5_还原_EXR_探针开");
            camGo.SetActive(goB);
            Debug.Log("[DREFL] 完成, 未存盘");
        }

        static void Shot(Camera cam, string label)
        {
            var rt = new RenderTexture(960, 540, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            var prev = cam.targetTexture;
            cam.targetTexture = rt; cam.aspect = 16f / 9f; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(960, 540, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, 960, 540), 0, 0); tex.Apply();
            var px = tex.GetPixels();
            double sum = 0; int dark = 0;
            foreach (var c in px)
            {
                float l = 0.2126f * Mathf.Clamp01(c.r) + 0.7152f * Mathf.Clamp01(c.g) + 0.0722f * Mathf.Clamp01(c.b);
                sum += l; if (l < 0.05f) dark++;
            }
            var t8 = new Texture2D(960, 540, TextureFormat.RGBA32, false, false);
            var c8 = new Color[px.Length];
            for (int k = 0; k < px.Length; k++)
                c8[k] = new Color(
                    c(k, px[k].r, 0), c(k, px[k].g, 1), c(k, px[k].b, 2), 1f);
            t8.SetPixels(c8); t8.Apply();
            File.WriteAllBytes(Path.Combine(OutDir, $"drefl_{label}.png"), t8.EncodeToPNG());
            cam.targetTexture = prev; RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(t8);
            Debug.Log($"[DREFL] {label}: mean={sum / px.Length:F3} 死黑={100.0 * dark / px.Length:F2}%");
        }

        static float c(int k, float v, int ch) => float.IsNaN(v) || v < 0 ? 1f : (v > 4f ? 1f : (v <= 0.0031308f ? v * 12.92f : 1.055f * Mathf.Pow(v, 1f / 2.4f) - 0.055f));
    }
}
#endif
