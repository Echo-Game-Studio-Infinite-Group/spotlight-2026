#if UNITY_EDITOR
// 走廊眩光沿线审计(只读场景, 不存盘) —— 用户报"CAM_06 大面积纯白, 但往前站一段就正常"。
// 做法: 沿走廊中轴每 4m 摆一台探针相机(高 1.6, 朝大门方向), 每点测 maxLin/过曝占比,
// 三轮对照: ①原样 ②关反射探针 ③关雾 —— 定位"出生点一段异常"的贡献方。
// 同时输出每点的"天花板直上方"采样(相机朝上看), 区分"天空花板照爆"与"墙面反射发白"。
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HST
{
    public static class HstGlareAudit
    {
        [MenuItem("HST/眩光沿线审计(只读)")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var tpl = GameObject.Find("CAM_02_Door");
            if (tpl == null) { Debug.LogError("[HST-AUDIT] 找不到 CAM_02_Door"); return; }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# 走廊眩光沿线审计 (960x540, 相机高 1.6m, 朝 -x 大门方向)");
            sb.AppendLine("# 走廊范围约 x -18(门) .. +41(端墙); 出生点在远端");

            // 生成本地探针相机(不存场景)
            var camGo = new GameObject("HST_AUDIT_CAM");
            var cam = camGo.AddComponent<Camera>();
            EditorUtility.CopySerialized(tpl.GetComponent<Camera>(), cam);
            var cd = cam.GetUniversalAdditionalCameraData();
            cd.renderPostProcessing = true;
            cam.nearClipPlane = 0.05f; cam.farClipPlane = 300f; cam.fieldOfView = 60f;

            float[] xs = { 38f, 30f, 22f, 14f, 6f, -2f, -8f, -12f, -15f, -17f };

            // 反射探针状态
            var probes = Object.FindObjectsOfType<ReflectionProbe>(true);
            var probeWas = probes.Select(p => p.enabled).ToArray();
            bool fogWas = RenderSettings.fog;

            // ① 原样
            sb.AppendLine("\n== ① 原样 (探针 " + probes.Length + " 个启用 " + probes.Count(p => p.enabled) + ", 雾 " + fogWas + ") ==");
            Scan(cam, camGo, xs, sb);

            // ② 关反射探针
            foreach (var p in probes) p.enabled = false;
            sb.AppendLine("\n== ② 关反射探针 ==");
            Scan(cam, camGo, xs, sb);
            for (int i = 0; i < probes.Length; i++) probes[i].enabled = probeWas[i];

            // ③ 关雾
            RenderSettings.fog = false;
            sb.AppendLine("\n== ③ 关雾 ==");
            Scan(cam, camGo, xs, sb);
            RenderSettings.fog = fogWas;

            Object.DestroyImmediate(camGo);
            File.WriteAllText("F:/VSCode/工业级渲染/tmp/glare_audit.txt", sb.ToString());
            Debug.Log("[HST-AUDIT] 完成 → tmp/glare_audit.txt");
        }

        static void Scan(Camera cam, GameObject camGo, float[] xs, System.Text.StringBuilder sb)
        {
            foreach (var x in xs)
            {
                // 朝 -x 看(大门方向)
                camGo.transform.position = new Vector3(x, 1.60f, 0f);
                camGo.transform.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
                var look = Probe(cam);
                // 朝上看天花板
                camGo.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
                var up = Probe(cam);
                sb.AppendLine($"  x={x,6:F0}  向前: {look}   看天花: {up}");
            }
        }

        static string Probe(Camera c)
        {
            const int W = 480, H = 270;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            bool was = c.enabled;
            c.enabled = true; c.targetTexture = rt; c.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBAHalf, false, true);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            var px = tex.GetPixels();
            c.targetTexture = null; c.enabled = was;
            float mx = 0f, sum = 0f; int over1 = 0, over09 = 0;
            for (int i = 0; i < px.Length; i++)
            {
                float l = 0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b;
                sum += l;
                if (l > mx) mx = l;
                if (l > 1f) over1++;
                if (l > 0.9f) over09++;
            }
            Object.DestroyImmediate(tex); Object.DestroyImmediate(rt);
            return $"max={mx,6:F2} mean={sum / px.Length:F3} >1={100f * over1 / px.Length,5:F2}% >0.9={100f * over09 / px.Length,5:F2}%";
        }
    }
}
#endif
