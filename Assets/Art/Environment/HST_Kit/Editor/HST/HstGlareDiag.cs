#if UNITY_EDITOR
// 眩光定位探针(只读) —— 判 CAM_06 过曝像素的空间分布, 区分"灯带本体"与"雾气弥散":
// 输出 >1.0 像素的数量/占比/包围盒/8×8 网格热力, 以及各网格块的 maxLin。
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HST
{
    public static class HstGlareDiag
    {
        [MenuItem("HST/眩光定位探针(只读)")]
        public static void Diag()
        {
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var cam = GameObject.Find("CAM_06_Spawn");
            if (cam == null) { Debug.LogError("[HST-GLARE] 找不到 CAM_06_Spawn"); return; }
            var c = cam.GetComponent<Camera>();
            var sb = new System.Text.StringBuilder();
            // 对照: 开/关后处理 —— 分辨主因是"发光面本身"还是"后处理 Bloom 放大"
            Probe(c, sb, true);
            Probe(c, sb, false);
            File.WriteAllText("F:/VSCode/工业级渲染/tmp/glare_diag.txt", sb.ToString());
            Debug.Log("[HST-GLARE] 完成 → tmp/glare_diag.txt");
        }

        static void Probe(Camera c, System.Text.StringBuilder sb, bool post)
        {
            var cd = c.GetUniversalAdditionalCameraData();
            bool wasPost = cd.renderPostProcessing;
            cd.renderPostProcessing = post;
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
            int x0 = W, x1 = -1, y0 = H, y1 = -1;
            const int G = 8;
            int[] cnt = new int[G * G]; float[] gmax = new float[G * G];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    var p = px[y * W + x];
                    float lum = 0.2126f * p.r + 0.7152f * p.g + 0.0722f * p.b;
                    int gi = (y * G / H) * G + (x * G / W);
                    if (lum > gmax[gi]) gmax[gi] = lum;
                    if (lum > 1f)
                    {
                        over++; cnt[gi]++;
                        if (x < x0) x0 = x; if (x > x1) x1 = x;
                        if (y < y0) y0 = y; if (y > y1) y1 = y;
                    }
                    if (lum > mx) mx = lum;
                }
            sb.AppendLine($"# === {W}x{H} 后处理{(post ? "开" : "关")}  maxLin={mx:F3}  >1.0={over} ({100f * over / (W * H):F2}%) ===");
            if (over > 0) sb.AppendLine($"# >1.0 包围盒: x {x0}..{x1}  y {y0}..{y1} (像素, y 自下而上)");
            sb.AppendLine("# 网格热力(块内占比 %, 行=从上到下):");
            for (int gy = G - 1; gy >= 0; gy--)
            {
                var line = new System.Text.StringBuilder("  ");
                for (int gx = 0; gx < G; gx++)
                {
                    int i = gy * G + gx;
                    float pct = 100f * cnt[i] / (W * H / (G * G));
                    line.Append(pct > 99f ? " ██ " : pct > 50f ? " ▓▓ " : pct > 20f ? " ▒▒ " :
                                pct > 5f ? " ░░ " : pct > 0.5f ? " .. " : "    ");
                }
                sb.AppendLine(line.ToString());
            }
            sb.AppendLine("# 网格 maxLin:");
            for (int gy = G - 1; gy >= 0; gy--)
            {
                var line = new System.Text.StringBuilder("  ");
                for (int gx = 0; gx < G; gx++) line.Append($"{gmax[gy * G + gx],8:F2}");
                sb.AppendLine(line.ToString());
            }
            Object.DestroyImmediate(tex); Object.DestroyImmediate(rt);
        }
    }
}
#endif
