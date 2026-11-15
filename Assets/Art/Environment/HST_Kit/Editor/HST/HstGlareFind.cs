#if UNITY_EDITOR
// 眩光峰值定位(只读) —— 42.98 那颗最亮像素究竟打在什么物件上。
// 做法: 渲 CAM_06 到 HDR RT → 找最亮像素及其邻域 → 用相机 ScreenPointToRay 打射线看命中物。
// 另出"亮度>5 的像素命中物"统计(前 10), 判断眩光是"某个物件"还是"一片环境"。
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace HST
{
    public static class HstGlareFind
    {
        [MenuItem("HST/眩光峰值定位(只读)")]
        public static void Diag()
        {
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var camGo = GameObject.Find("CAM_06_Spawn");
            if (camGo == null) { Debug.LogError("[HST-FIND] 找不到 CAM_06_Spawn"); return; }
            var cam = camGo.GetComponent<Camera>();
            const int W = 960, H = 540;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            bool was = cam.enabled;
            cam.enabled = true; cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBAHalf, false, true);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            var px = tex.GetPixels();
            cam.targetTexture = null; cam.enabled = was;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# CAM_06 眩光峰值定位 (960x540, **后处理开** = 显示口径)");

            // 最亮像素
            int bi = 0; float bl = -1f;
            for (int i = 0; i < px.Length; i++)
            {
                float l = Lum(px[i]);
                if (l > bl) { bl = l; bi = i; }
            }
            int bx = bi % W, by = bi / W;
            sb.AppendLine($"# 最亮像素 ({bx},{by}) y自下而上  lum={bl:F2}");
            sb.AppendLine("# 该点及邻域打射线命中:");
            for (int d = 0; d <= 12; d += 6)
                sb.AppendLine($"   offset {d}px: " + Hit(cam, bx + d, by) + " | " + Hit(cam, bx, by + d));

            // 显示口径: 接近纯白(>0.9) 的像素命中物统计(抽样: 每 2 像素采 1)
            var cens = new Dictionary<string, int>();
            var csum = new Dictionary<string, float>();
            int hot = 0, tot = 0;
            for (int y = 0; y < H; y += 2)
                for (int x = 0; x < W; x += 2)
                {
                    tot++;
                    float l = Lum(px[y * W + x]);
                    if (l <= 0.9f) continue;
                    hot++;
                    var name = HitShort(cam, x, y);
                    cens[name] = cens.TryGetValue(name, out var c) ? c + 1 : 1;
                    csum[name] = (csum.TryGetValue(name, out var s) ? s : 0f) + l;
                }
            sb.AppendLine($"\n# 显示口径 亮度>0.9 像素(2x 抽样, 共 {hot}/{tot} = {100f * hot / tot:F2}%)命中物 Top10:");
            foreach (var kv in cens.OrderByDescending(k => k.Value).Take(10))
                sb.AppendLine($"   {kv.Key,-36} n={kv.Value,-5} ({100f * kv.Value / hot,5:F1}%) mean={csum[kv.Key] / kv.Value,6:F3}");

            File.WriteAllText("F:/VSCode/工业级渲染/tmp/glare_find.txt", sb.ToString());
            Object.DestroyImmediate(tex); Object.DestroyImmediate(rt);
            Debug.Log("[HST-FIND] 完成 → tmp/glare_find.txt");
        }

        static float Lum(Color p) => 0.2126f * p.r + 0.7152f * p.g + 0.0722f * p.b;

        static string Hit(Camera cam, int x, int y)
        {
            var ray = cam.ScreenPointToRay(new Vector3(x, y, 0f));
            return Physics.Raycast(ray, out var h, 400f) ? $"{Name(h)} d={h.distance:F1}" : "(无命中)";
        }

        static string HitShort(Camera cam, int x, int y)
        {
            var ray = cam.ScreenPointToRay(new Vector3(x, y, 0f));
            return Physics.Raycast(ray, out var h, 400f) ? Name(h) : "(无命中)";
        }

        static string Name(RaycastHit h)
        {
            var t = h.collider != null ? h.collider.transform : null;
            var r = h.collider != null ? h.collider.GetComponent<Renderer>() : null;
            string mat = (r != null && r.sharedMaterial != null) ? r.sharedMaterial.name : "-";
            return $"{h.collider.name} [{mat}]";
        }
    }
}
#endif
