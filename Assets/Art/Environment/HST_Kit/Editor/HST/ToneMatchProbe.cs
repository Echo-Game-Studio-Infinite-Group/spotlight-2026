#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HST
{
    // 后处理恢复默认(2026-10-08 夜, 用户令: "调色和后处理都是默认参数, 只动其他的")。
    // 一键把走廊体积的 Tonemapping/ColorAdjustments/Bloom 全部写回 URP 默认值并存盘,
    // 再出 CAM_01/02 显示图。亮度对表从此只在光源强度/材质/烘焙层做, 后处理永远默认。
    public static class PostDefaults
    {
        [MenuItem("HST/后处理恢复默认+出图(用户令)")]
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var prof = AssetDatabase.LoadAssetAtPath<VolumeProfile>(
                "Assets/Art/Environment/HST_Kit/Materials/HST_Prags/PP_SC_Corridor_Pragmata.asset");
            Tonemapping tm; ColorAdjustments ca; Bloom bl;
            if (!prof.TryGet(out tm) || !prof.TryGet(out ca)) { Debug.LogError("[PD] 体积缺组件"); return; }
            prof.TryGet(out bl);

            tm.mode.value = TonemappingMode.None;                       // URP 默认 = 无曲线(纯 sRGB)
            ca.postExposure.value = 0f; ca.contrast.value = 0f; ca.saturation.value = 0f;
            ca.hueShift.value = 0f; ca.colorFilter.value = Color.white;
            if (bl != null)
            {
                bl.threshold.value = 0.9f; bl.intensity.value = 0f;      // 默认: 强度 0 = 不发光晕
                bl.scatter.value = 0.7f; bl.clamp.value = 65479f;
            }
            EditorUtility.SetDirty(tm); EditorUtility.SetDirty(ca);
            if (bl != null) EditorUtility.SetDirty(bl);
            EditorUtility.SetDirty(prof);
            AssetDatabase.SaveAssets();
            Debug.Log("[PD] 后处理已写回 URP 默认并存盘(None 曲线/EV0/对比度饱和 0/Bloom 关)");

            string outDir = @"F:/VSCode/工业级渲染/renders/qa_2026-10-08";
            Directory.CreateDirectory(outDir);
            foreach (string cn in new[] { "CAM_01_Hero", "CAM_02_Door" })
            {
                var go = GameObject.Find(cn);
                if (go == null) continue;
                Shot(go.GetComponent<Camera>(), $"{outDir}/post_default_{cn}.png");
                Debug.Log($"[PD] 已出 {cn}");
            }
        }

        static void Shot(Camera cam, string path) { ShotPublic(cam, path); }
        public static void ShotPublic(Camera cam, string path)
        {
            var rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            var prev = cam.targetTexture;
            cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1920, 1080, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); tex.Apply();
            var px = tex.GetPixels();
            var t8 = new Texture2D(1920, 1080, TextureFormat.RGBA32, false, false);
            var c8 = new Color[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                float r = L2S(Mathf.Clamp01(px[i].r)), g = L2S(Mathf.Clamp01(px[i].g)), b = L2S(Mathf.Clamp01(px[i].b));
                c8[i] = new Color(r, g, b, 1f);
            }
            t8.SetPixels(c8); t8.Apply();
            File.WriteAllBytes(path, t8.EncodeToPNG());
            cam.targetTexture = prev;
            Object.DestroyImmediate(rt); Object.DestroyImmediate(tex); Object.DestroyImmediate(t8);
        }

        static float L2S(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(Mathf.Max(c, 0f), 1f / 2.4f) - 0.055f;

        // EXP5(2026-10-08 夜): 面板发光 doubleSidedGI=false(源级杠杆, 不是后处理)——
        // Cycles 单面发光 vs Unity 双面计数 ≈ ×2 差; 关掉后正面亮度不变(亮池保留)、
        // 总照明减半, 对着实测的 π 级过量。烤完出一张 CAM_01(默认后处理, 单渲染防探针互锁)。
        [MenuItem("HST/EXP5(单面发光烘焙+出图)")]
        public static void Exp5EmitSingleSided()
        {
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var pane = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/Art/Environment/HST_Kit/Materials/HST_Prags/M_ARCH_PaneEmit.mat");
            var b = pane.doubleSidedGI;
            pane.doubleSidedGI = false;
            EditorUtility.SetDirty(pane); AssetDatabase.SaveAssets();
            Debug.Log($"[EXP5] PaneEmit doubleSidedGI {b} -> false, 烤 4096");
            // 借 SceneBuilder 的采样覆盖(同类内可见性: internal 走反射太绕, 直接复用公开烤入口)
            SceneBuilder.BakeCorridorOnly4096();
            var camGo = GameObject.Find("CAM_01_Hero");
            Shot(camGo.GetComponent<Camera>(), @"F:/VSCode/工业级渲染/renders/qa_2026-10-08/exp5_单面发光.png");
            Debug.Log("[EXP5] 完成(材质保持单面, 按数据定夺)");
        }
    }
}
#endif