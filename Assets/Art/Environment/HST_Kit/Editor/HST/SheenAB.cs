using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.IO;

// 一次性: Unity 04 机位 天空盒反射开/关 差分出"光泽场"(存 PNG 供 Python 对比)
namespace HST
{
    public static class SheenAB
    {
        [MenuItem("HST/光泽场A/B(04机位)")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var cam = System.Array.Find(UnityEngine.Object.FindObjectsOfType<Camera>(true), c => c.name.Contains("04"));
            var sky = RenderSettings.skybox;
            float ri = RenderSettings.reflectionIntensity;
            string dir = @"F:\VSCode\工业级渲染\tmp";
            // 暖机: 首帧有系统性偏暗(~0.02), 先渲两帧丢弃
            Shoot(cam); Shoot(cam);
            // A: 当前(反射 0.6)
            var tA = Shoot(cam);
            File.WriteAllBytes(Path.Combine(dir, "unity_sheen_A.png"), tA.EncodeToPNG());
            // B: 关反射
            RenderSettings.reflectionIntensity = 0f;
            var tB = Shoot(cam);
            File.WriteAllBytes(Path.Combine(dir, "unity_sheen_B.png"), tB.EncodeToPNG());
            // C: ×100 判决(地板到底采不采样)
            var cube = AssetDatabase.LoadAssetAtPath<Cubemap>("Assets/Art/Environment/HST_Kit/ReflectionProbes/RP_Manual_Corridor.asset");
            foreach (CubemapFace f in System.Enum.GetValues(typeof(CubemapFace)))
            {
                if (f == CubemapFace.Unknown) continue;
                var px = cube.GetPixels(f);
                float mean = 0f, mx = 0f;
                foreach (var c in px) { mean += 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; mx = Mathf.Max(mx, Mathf.Max(c.r, Mathf.Max(c.g, c.b))); }
                Debug.Log($"[SHEEN-AB] 手工cube {f}: mean={mean / px.Length:F4} max={mx:F2}");
            }
            foreach (CubemapFace f in System.Enum.GetValues(typeof(CubemapFace)))
            {
                if (f == CubemapFace.Unknown) continue;
                var px = cube.GetPixels(f);
                for (int i = 0; i < px.Length; i++) px[i] = px[i] * 100f;
                cube.SetPixels(px, f);
            }
            cube.Apply(true, false);
            RenderSettings.reflectionIntensity = ri;
            var tC = Shoot(cam);
            File.WriteAllBytes(Path.Combine(dir, "unity_sheen_C.png"), tC.EncodeToPNG());
            foreach (CubemapFace f in System.Enum.GetValues(typeof(CubemapFace)))
            {
                if (f == CubemapFace.Unknown) continue;
                var px = cube.GetPixels(f);
                for (int i = 0; i < px.Length; i++) px[i] = px[i] / 100f;
                cube.SetPixels(px, f);
            }
            cube.Apply(true, false);
            Debug.Log("[SHEEN-AB] A/B 已写 tmp/unity_sheen_A/B.png (skybox=" + (sky != null ? sky.name : "null") + ")");
        }

        static Texture2D Shoot(Camera cam)
        {
            const int w = 1920, h = 1080;
            float ml; int ov;
            var px = SceneBuilderRaw(cam, w, h, out ml, out ov);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var cols = new Color32[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                // 线性→sRGB(与出图同式)
                cols[i] = new Color32((byte)(Mathf.LinearToGammaSpace(c.r) * 255f + 0.5f),
                                      (byte)(Mathf.LinearToGammaSpace(c.g) * 255f + 0.5f),
                                      (byte)(Mathf.LinearToGammaSpace(c.b) * 255f + 0.5f), 255);
            }
            tex.SetPixels32(cols);
            tex.Apply();
            return tex;
        }

        // 反射 HstSceneBuilder.RenderRaw(internal)
        static Color[] SceneBuilderRaw(Camera cam, int w, int h, out float maxLin, out int over1)
        {
            // 通过公共入口 HstSceneBuilder 的 RenderRaw 是 private —— 这里用反射拿
            var mi = typeof(HST.SceneBuilder).GetMethod("RenderRaw",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            object[] args = { cam, w, h, 0f, 0 };
            var r = (Color[])mi.Invoke(null, args);
            maxLin = (float)args[3]; over1 = (int)args[4];
            return r;
        }
    }
}
