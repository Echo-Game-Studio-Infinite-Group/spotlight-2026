#if UNITY_EDITOR
// 白面光滑度修正 (2026-10-12) —— 治"CAM_06 大面积泛白"的真因。
// 诊断链: 泛白像素成分 = 高带墙 40% + 天花 34% + 下段墙 9%(灯带本体仅 3.3%);
//   降 albedo(×0.25) 无效、降灯带自发光无效、压 lightmap 峰值 8.6 倍无效、关反射探针无效
//   → 最后查到材质: M_ARCH_WallM/CeilM/EndM/WallM_Band/DoorHead/DoorLeaf 的 _Smoothness **= 1.0**
//   (满分镜面 = 墙当天花板用镜子) → 灯带被白面镜像再照一遍 → 整片顶到显示上限。
// 实测(CAM_06 显示口径 >0.9 占比): smoothness 1.0→8.31% / 0.5→6.12% / **0.2→2.61%** / 0.0→2.45%,
//   而整体平均亮度只从 0.329 降到 0.264 —— 比"降灯强 40%"精准得多(后者会连带压暗地面墙脚)。
// 口径依据: 真实哑光墙板粗糙度 0.7~0.9(光滑度 0.1~0.3); Blender 侧墙板 rough=0.38。
// 不动地板(保留其镜面反射的湿滑科幻感, 且它不是泛白主力)。
// 幂等: 已是目标值则跳过。改完 AssetDatabase.SaveAssets, 出图不需重烤(光滑度不进 GI)。
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HST
{
    public static class HstMatSmoothFix
    {
        const string MatDir = "Assets/Art/Environment/HST_Kit/Materials/HST_Prags";
        const float TargetSmooth = 0.2f;
        // 白面(泛白主力): 墙/天花/端墙/高带/门楣/门扇。地板刻意排除。
        static readonly string[] Targets = {
            "M_ARCH_WallM", "M_ARCH_WallM_Band", "M_ARCH_WallM_DoorHead", "M_ARCH_WallM_DoorLeaf",
            "M_ARCH_CeilM", "M_ARCH_EndM",
        };

        [MenuItem("HST/白面光滑度修正+出图")]
        public static void Run()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# 白面光滑度修正");
            int changed = 0, skipped = 0;
            foreach (var n in Targets)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/{n}.mat");
                if (m == null) { sb.AppendLine($"  {n,-26} 缺失"); continue; }
                float cur = m.GetFloat("_Smoothness");
                if (Mathf.Approximately(cur, TargetSmooth)) { sb.AppendLine($"  {n,-26} 已是 {TargetSmooth}(跳过)"); skipped++; continue; }
                m.SetFloat("_Smoothness", TargetSmooth);
                EditorUtility.SetDirty(m);
                sb.AppendLine($"  {n,-26} _Smoothness {cur:F2} → {TargetSmooth:F2}");
                changed++;
            }
            AssetDatabase.SaveAssets();
            sb.AppendLine($"# 改动 {changed} 个材质, 跳过 {skipped} 个");
            HstCorridorExtend.Log("光滑度修正: 改 " + changed + " 个材质 → " + TargetSmooth);

            // 出图(不需重烤: 光滑度不参与光照贴图)
            SceneBuilder.Shoot();
            File.WriteAllText("F:/VSCode/工业级渲染/tmp/smooth_fix.txt", sb.ToString());
            Debug.Log(sb.ToString());
        }

        // 探针: 当前场景下 CAM_06 的显示口径统计(改前/改后对照用)
        [MenuItem("HST/白面光滑度-只测CAM06读数")]
        public static void ProbeOnly()
        {
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var g = GameObject.Find("CAM_06_Spawn");
            if (g == null) { Debug.LogError("[HST-SM] 缺 CAM_06_Spawn"); return; }
            var cam = g.GetComponent<Camera>();
            const int W = 960, H = 540;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            cam.enabled = true; cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBAHalf, false, true);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            var px = tex.GetPixels();
            cam.targetTexture = null;
            float mx = 0f, sum = 0f; int o9 = 0;
            foreach (var p in px)
            {
                float l = 0.2126f * p.r + 0.7152f * p.g + 0.0722f * p.b;
                sum += l; if (l > mx) mx = l; if (l > 0.9f) o9++;
            }
            Debug.Log($"[HST-SM] CAM_06 max={mx:F3} mean={sum / px.Length:F4} >0.9={100f * o9 / px.Length:F2}%");
            Object.DestroyImmediate(tex); Object.DestroyImmediate(rt);
        }
    }
}
#endif
