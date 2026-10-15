#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HST
{
    // 墙区光照分解(2026-10-08, 用户判据「灯条正下方 vs 两灯条中间」的取证工具)
    // 把当前场景拆成三态各出一张线性图 + 一张数字表, 用于回答"墙面为什么平":
    //   A 总光(烘焙 GI + 实时/混合直射)      —— 与 Play 出图同源
    //   B 只烘焙 GI(关所有灯 + 环境黑)      —— 发光网格的贡献在这里
    //   C 只直射(灯改 Realtime + 摘掉光贴图) —— 灯串/阴影结构在这里
    // 参考值(Blender cam01, 显示值): 墙带 y=411 时 x=800/876/952/1028/1104 = 0.29/0.57/0.50/0.58/0.44
    public static class WallLightBreakdown
    {
        // 判据列/行(scripts/qa/wall_pool_contrast.py 同口径): 池L=900 中间=960 池R=1075; 行 400/412
        static readonly int[] XS = { 876, 900, 914, 952, 960, 990, 1028, 1075, 1142 };
        static readonly int[] YS = { 400, 412, 424 };
        const string OUT = "F:/VSCode/工业级渲染/tmp";

        [MenuItem("HST/墙区光照分解(直射/GI)")]
        public static void Run() { Run(""); }

        // tag 附加到输出文件名(实验轮区分: _exp1 门框GI关 / _exp2 真自发光)
        public static void Run(string tag)
        {
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var camGo = GameObject.Find("CAM_01_Hero");
            if (camGo == null) { Debug.LogError("[HST-BD] 找不到 CAM_01_Hero"); return; }
            var cam = camGo.GetComponent<Camera>();
            var cd = cam.GetUniversalAdditionalCameraData();
            bool pp = cd.renderPostProcessing; cd.renderPostProcessing = false;

            var lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
            var lightOn = lights.Select(l => l.enabled).ToArray();
            var bakeTypes = lights.Select(l => l.lightmapBakeType).ToArray();
            var mrs = UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true);
            var lmIdx = mrs.Select(r => r.lightmapIndex).ToArray();
            var lmSO = mrs.Select(r => r.lightmapScaleOffset).ToArray();
            var ambB = RenderSettings.ambientLight; var ambModeB = RenderSettings.ambientMode;

            // A 总光
            var A = Shot(cam, OUT + "/bd_A_all" + tag + ".png");
            // B 只烘焙 GI
            foreach (var l in lights) l.enabled = false;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            var B = Shot(cam, OUT + "/bd_B_gi" + tag + ".png");
            // C 只直射: 灯恢复 + 改 Realtime + 摘光贴图
            foreach (var l in lights) { l.enabled = true; l.lightmapBakeType = LightmapBakeType.Realtime; }
            RenderSettings.ambientLight = ambB; RenderSettings.ambientMode = ambModeB;
            foreach (var r in mrs) { r.lightmapIndex = 65535; r.lightmapScaleOffset = new Vector4(1, 1, 0, 0); }
            var C = Shot(cam, OUT + "/bd_C_direct" + tag + ".png");

            // 还原(含 scaleOffset —— 门区 ×8 密度的对象偏移不是单位阵)
            for (int i = 0; i < mrs.Length; i++) { mrs[i].lightmapIndex = 65535; mrs[i].lightmapScaleOffset = lmSO[i]; }
            for (int i = 0; i < mrs.Length; i++) mrs[i].lightmapIndex = lmIdx[i];
            for (int i = 0; i < lights.Length; i++) { lights[i].enabled = lightOn[i]; lights[i].lightmapBakeType = bakeTypes[i]; }
            RenderSettings.ambientLight = ambB; RenderSettings.ambientMode = ambModeB;

            // D 显示空间(后处理开, 光贴图在位): 与 Play 出图同观感通道, 供 wall_pool_contrast.py 直接判
            cd.renderPostProcessing = true;
            var D = Shot(cam, OUT + "/bd_D_display" + tag + ".png");
            cd.renderPostProcessing = pp;

            Report("U总光", A);
            Report("U只GI", B); Report("U只直射", C);
            ReportDisp("U显示(判据)", D);
        }

        // 显示值(逐像素 L2S 后平均, 与 python 判据同口径) + Δ 判据行
        static void ReportDisp(string tag, Color[] px)
        {
            Report(tag, px, true);
            var sb = new System.Text.StringBuilder();
            foreach (var y in new[] { 400, 412 })
            {
                float l = Disp(px, 1920, 900, y), m = Disp(px, 1920, 960, y), r = Disp(px, 1920, 1075, y);
                sb.Append($"  y={y}: 池L {l:F3} 中 {m:F3} 池R {r:F3} → Δ(池−中) = {(l + r) / 2f - m:+0.000;-0.000}  ");
            }
            Debug.Log($"[HST-BD] 判据(Blender 参考: y400 +0.090 / y412 +0.112)\n{sb}");
        }

        static float Disp(Color[] px, int w, int x, int y)
        { int yy = 1080 - 1 - y; float acc = 0; int n = 0;
            for (int dy = -3; dy <= 3; dy++) for (int dx = -3; dx <= 3; dx++)
            { int xx = Mathf.Clamp(x + dx, 0, w - 1), yq = Mathf.Clamp(yy + dy, 0, 1079);
                var c = px[yq * w + xx]; acc += L2S(0.2126f * c.r + 0.7152f * c.g + 0.072f * c.b); n++; }
            return acc / n;
        }

        static float s(Color[] px, int w, int x, int y)
        {
            // 注意: ReadPixels 行序自下而上 —— 传入的 y 是"屏幕上往下数"的坐标
            int yy = 1080 - 1 - y;
            float acc = 0f; int n = 0;
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    int xx = Mathf.Clamp(x + dx, 0, w - 1), yq = Mathf.Clamp(yy + dy, 0, 1079);
                    var c = px[yq * w + xx];
                    acc += 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; n++;
                }
            return acc / n;
        }

        static void Report(string tag, Color[] px, bool disp = false)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[HST-BD] {tag} {(disp ? "显示值" : "线性值")} (行=屏幕 y, 列=屏幕 x)\n     ");
            foreach (var x in XS) sb.Append($"{x,6}");
            sb.AppendLine();
            foreach (var y in YS)
            {
                sb.Append($"{y,4} ");
                foreach (var x in XS) sb.Append($"{(disp ? Disp(px, 1920, x, y) : s(px, 1920, x, y)),6:F3}");
                sb.AppendLine();
            }
            Debug.Log(sb.ToString());
        }

        static Color[] Shot(Camera cam, string path)
        {
            var rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            var prev = cam.targetTexture;
            cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1920, 1080, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); tex.Apply();
            var px = tex.GetPixels();
            // 线性 → sRGB 另存一张, 便于直接看
            var t8 = new Texture2D(1920, 1080, TextureFormat.RGB24, false, false);
            var c8 = new Color[px.Length];
            for (int i = 0; i < px.Length; i++)
                c8[i] = new Color(L2S(px[i].r), L2S(px[i].g), L2S(px[i].b), 1f);
            t8.SetPixels(c8); t8.Apply();
            File.WriteAllBytes(path, t8.EncodeToPNG());
            cam.targetTexture = prev;
            UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex); UnityEngine.Object.DestroyImmediate(t8);
            Debug.Log($"[HST-BD] saved {path}");
            return px;
        }

        static float L2S(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(Mathf.Max(c, 0f), 1f / 2.4f) - 0.055f;
    }
}
#endif
