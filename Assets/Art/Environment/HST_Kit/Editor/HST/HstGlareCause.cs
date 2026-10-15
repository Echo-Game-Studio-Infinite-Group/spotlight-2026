#if UNITY_EDITOR
// 眩光主因判别(只读资产, 只改材质实例) —— CAM_06 区域 HDR 亮度 43 的来源拆解。
// 疑点: M_ARCH_PaneEmit 的 _EmissionColor 只有 ~1.15, 而实测峰值 43 → 自发光只可能是小头。
// 三个对照(均用 renderer.material 实例, 不脏资产):
//   ① 原样        ② 自发光清零(_EmissionColor=0)   ③ 去镜面高光(_Smoothness=0)
// 谁的降幅大, 主因就是谁。输出 tmp/glare_cause.txt
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HST
{
    public static class HstGlareCause
    {
        [MenuItem("HST/眩光主因判别(只读)")]
        public static void Diag()
        {
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var camGo = GameObject.Find("CAM_06_Spawn");
            if (camGo == null) { Debug.LogError("[HST-CAUSE] 找不到 CAM_06_Spawn"); return; }
            var cam = camGo.GetComponent<Camera>();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# CAM_06 眩光主因判别 (960x540 HDR, 后处理关以便看原始值)");

            // 收集发光条渲染器(实例化材质, 不动资产)
            var emitters = Object.FindObjectsOfType<MeshRenderer>(true)
                .Where(mr => mr.name.StartsWith("ARCH_LightPane") || mr.name.StartsWith("ARCH_FrameGlow"))
                .ToArray();
            var insts = emitters.Select(mr => mr.material).ToArray();   // 实例化
            sb.AppendLine($"# 发光条物件 {emitters.Length} 件(材质实例化, 资产未改)");
            if (insts.Length > 0)
            {
                var m0 = insts[0];
                sb.AppendLine($"# 样例材质 {m0.name}: emission={m0.GetColor("_EmissionColor")} base={m0.GetColor("_BaseColor")} " +
                              $"smooth={m0.GetFloat("_Smoothness")} metal={m0.GetFloat("_Metallic")} " +
                              $"shader={m0.shader.name} keyword_EMISSION={m0.IsKeywordEnabled("_EMISSION")}");
            }

            var origEmis = insts.Select(m => m.GetColor("_EmissionColor")).ToArray();
            var origSm = insts.Select(m => m.GetFloat("_Smoothness")).ToArray();

            sb.AppendLine("\n== ① 原样 ==");
            sb.AppendLine(Probe(cam));

            foreach (var m in insts) m.SetColor("_EmissionColor", Color.black);
            sb.AppendLine("== ② 自发光清零 ==");
            sb.AppendLine(Probe(cam));
            for (int i = 0; i < insts.Length; i++) insts[i].SetColor("_EmissionColor", origEmis[i]);

            foreach (var m in insts) m.SetFloat("_Smoothness", 0f);
            sb.AppendLine("== ③ 去镜面高光(smoothness=0) ==");
            sb.AppendLine(Probe(cam));
            for (int i = 0; i < insts.Length; i++) insts[i].SetFloat("_Smoothness", origSm[i]);

            // ④ 两者同时
            foreach (var m in insts) { m.SetColor("_EmissionColor", Color.black); m.SetFloat("_Smoothness", 0f); }
            sb.AppendLine("== ④ 自发光清零 + 去高光 ==");
            sb.AppendLine(Probe(cam));

            File.WriteAllText("F:/VSCode/工业级渲染/tmp/glare_cause.txt", sb.ToString());
            Debug.Log("[HST-CAUSE] 完成 → tmp/glare_cause.txt");
        }

        static string Probe(Camera c)
        {
            var cd = c.GetUniversalAdditionalCameraData();
            bool wasPost = cd.renderPostProcessing;
            cd.renderPostProcessing = false;      // 看原始 HDR 值
            const int W = 960, H = 540;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            bool was = c.enabled;
            c.enabled = true; c.targetTexture = rt; c.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBAHalf, false, true);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            var px = tex.GetPixels();
            c.targetTexture = null; c.enabled = was;
            cd.renderPostProcessing = wasPost;

            float mx = 0f; int over = 0;
            for (int i = 0; i < px.Length; i++)
            {
                float l = 0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b;
                if (l > mx) mx = l;
                if (l > 1f) over++;
            }
            Object.DestroyImmediate(tex); Object.DestroyImmediate(rt);
            return $"   maxLin={mx:F2}  >1.0占比={100f * over / (W * H):F2}%";
        }
    }
}
#endif
