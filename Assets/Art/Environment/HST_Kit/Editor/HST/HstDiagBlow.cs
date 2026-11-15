#if UNITY_EDITOR
// 一次性诊断(2026-10-13): 概念厅 CAM_PodUpMega 过曝 14% 对 主光/环境/天空盒/玻璃 四个旋钮全部无响应,
// 逐个关闭 TA 渲染器特性出图, 定位过曝像素属于哪个 pass。只读诊断: 结束时全部还原, 不留任何改动。
// 注意: ScriptableRendererFeature.isActive 是只读属性, 开关必须走序列化字段 m_Active。
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HST
{
    public static class HstDiagBlow
    {
        const string OutDir = @"F:\VSCode\工业级渲染\tmp\relight";
        const string RendererAsset = "Assets/Settings/URP-Renderer.asset";
        const string Cam = "CAM_PodUpMega";

        [MenuItem("HST/诊断 PodUpMega 过曝来源(只读, 还原)")]
        public static void Run()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Atrium_Concept.unity");
            var camGo = GameObject.Find(Cam);
            if (camGo == null) { Debug.LogError("[DIAG] 找不到 " + Cam); return; }
            var cam = camGo.GetComponent<Camera>();
            bool goB = camGo.activeSelf; camGo.SetActive(true);

            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererAsset);
            var feats = renderer.rendererFeatures;
            string[] names = new string[feats.Count];
            bool[] saved = new bool[feats.Count];
            for (int i = 0; i < feats.Count; i++)
            {
                names[i] = feats[i].name;
                saved[i] = new SerializedObject(feats[i]).FindProperty("m_Active").boolValue;
            }

            DiagShot(cam, "0_全开");
            SetFeats(feats, f => f.Contains("Fog"), false);        DiagShot(cam, "1_关雾");
            SetFeats(feats, f => f.Contains("Fog"), true);
            SetFeats(feats, f => f.Contains("SSR"), false);        DiagShot(cam, "2_关SSR");
            SetFeats(feats, f => f.Contains("SSR"), true);
            SetFeats(feats, f => f.Contains("Bloom"), false);      DiagShot(cam, "3_关Bloom");
            SetFeats(feats, f => f.Contains("Bloom"), true);
            SetFeats(feats, f => f.Contains("Volumetric"), false); DiagShot(cam, "4_关体积光");
            SetFeats(feats, f => f.Contains("Volumetric"), true);
            SetFeats(feats, f => f.Contains("Fog") || f.Contains("SSR") || f.Contains("Bloom"), false);
            DiagShot(cam, "5_雾SSRBloom全关");

            for (int i = 0; i < feats.Count; i++)
            {
                var fso = new SerializedObject(feats[i]);
                fso.FindProperty("m_Active").boolValue = saved[i];
                fso.ApplyModifiedPropertiesWithoutUndo();
            }
            camGo.SetActive(goB);
            Debug.Log("[DIAG] 完成, 特性已还原");
        }

        static void SetFeats(System.Collections.Generic.List<ScriptableRendererFeature> feats,
            System.Func<string, bool> match, bool on)
        {
            foreach (var f in feats)
            {
                if (!match(f.name)) continue;
                var fso = new SerializedObject(f);
                fso.FindProperty("m_Active").boolValue = on;
                fso.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static void DiagShot(Camera cam, string label)
        {
            var rt = new RenderTexture(960, 540, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.aspect = 16f / 9f;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(960, 540, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, 960, 540), 0, 0);
            tex.Apply();
            var px = tex.GetPixels();
            double sum = 0; int blown = 0;
            var c8 = new Color[px.Length];
            for (int k = 0; k < px.Length; k++)
            {
                float r = S(Mathf.Clamp01(px[k].r)), g = S(Mathf.Clamp01(px[k].g)), b = S(Mathf.Clamp01(px[k].b));
                c8[k] = new Color(r, g, b, 1f);
                float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                sum += lum;
                if (lum > 0.98f) blown++;
            }
            var t8 = new Texture2D(960, 540, TextureFormat.RGBA32, false, false);
            t8.SetPixels(c8); t8.Apply();
            File.WriteAllBytes(Path.Combine(OutDir, $"diag_{label}.png"), t8.EncodeToPNG());
            cam.targetTexture = prev;
            RenderTexture.active = null;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(t8);
            Debug.Log($"[DIAG] {label}: mean={sum / px.Length:F3} 过曝={100.0 * blown / px.Length:F2}%");
        }

        static float S(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(Mathf.Max(c, 0f), 1f / 2.4f) - 0.055f;
    }
}
#endif
