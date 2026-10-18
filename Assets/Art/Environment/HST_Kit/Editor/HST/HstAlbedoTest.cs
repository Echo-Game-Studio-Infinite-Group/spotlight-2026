#if UNITY_EDITOR
// 白面反照率实验(只读资产, MaterialPropertyBlock 不改材质) —— 验证"过白"是否源于 albedo=1.0。
// Unity 侧 M_ARCH_WallM / _Band / CeilM / EndM 均为 albedo 1.0 纯白(Blender 侧用贴图, 均值更低),
// 大片白面被灯照亮后直接顶到显示上限 → 直观就是"哪都是纯白"。
// 本工具用 MPB 把白面 _BaseColor 乘一个系数(不动资产、可反复), 出图 + 统计 >0.9 占比。
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HST
{
    public static class HstAlbedoTest
    {
        const string OutDir = "F:/VSCode/工业级渲染/renders/unity_repro";

        [MenuItem("HST/白面反照率实验(只读)")]
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var camGo = GameObject.Find("CAM_06_Spawn");
            if (camGo == null) { Debug.LogError("[HST-ALB] 缺 CAM_06_Spawn"); return; }
            var cam = camGo.GetComponent<Camera>();
            var cd = cam.GetUniversalAdditionalCameraData();
            cd.renderPostProcessing = true;

            var whites = Object.FindObjectsOfType<MeshRenderer>(true)
                .Where(mr => mr.sharedMaterial != null && new[] {
                    "M_ARCH_WallM", "M_ARCH_WallM_Band", "M_ARCH_CeilM", "M_ARCH_EndM",
                    "M_ARCH_WallM_DoorHead", "M_ARCH_WallM_DoorLeaf" }
                    .Contains(mr.sharedMaterial.name))
                .ToArray();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# 白面反照率实验 —— 命中白面物件 {whites.Length} 件");

            // 注意: 不能用 MaterialPropertyBlock —— URP 的 SRP Batcher 会忽略它(实测无效果)。
            // 用材质实例(new Material(src))替换 sharedMaterial, 渲染后还原, 资产零改动。
            var origMat = whites.Select(mr => mr.sharedMaterial).ToArray();
            foreach (float sm in new[] { 1.0f, 0.5f, 0.2f, 0.0f })
            {
                var insts = new Material[whites.Length];
                for (int i = 0; i < whites.Length; i++)
                {
                    insts[i] = new Material(origMat[i]);
                    insts[i].SetFloat("_Smoothness", sm);      // 只改镜面: 判"白是不是反射来的"
                    whites[i].sharedMaterial = insts[i];
                }
                string res = Probe(cam);
                sb.AppendLine($"  _Smoothness = {sm:F2}: {res}");
                string f = $"{OutDir}/_check_smooth_{sm:F2}.png";
                HstDoorWallShot.RenderTo(cam, camGo.transform.position, cam.fieldOfView, f,
                    camGo.transform.position + camGo.transform.forward * 10f);
                for (int i = 0; i < whites.Length; i++)
                {
                    whites[i].sharedMaterial = origMat[i];
                    Object.DestroyImmediate(insts[i]);
                }
            }
            File.WriteAllText("F:/VSCode/工业级渲染/tmp/albedo_test.txt", sb.ToString());
            Debug.Log("[HST-ALB] 完成 → tmp/albedo_test.txt");
        }

        static string Probe(Camera c)
        {
            const int W = 960, H = 540;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            bool was = c.enabled;
            c.enabled = true; c.targetTexture = rt; c.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBAHalf, false, true);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
            var px = tex.GetPixels();
            c.targetTexture = null; c.enabled = was;
            float mx = 0f, sum = 0f; int o9 = 0, o95 = 0;
            for (int i = 0; i < px.Length; i++)
            {
                float l = 0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b;
                sum += l; if (l > mx) mx = l;
                if (l > 0.9f) o9++; if (l > 0.95f) o95++;
            }
            Object.DestroyImmediate(tex); Object.DestroyImmediate(rt);
            return $"max={mx:F3} mean={sum / px.Length:F4} >0.9={100f * o9 / px.Length:F2}% >0.95={100f * o95 / px.Length:F2}%";
        }
    }
}
#endif
