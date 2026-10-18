#if UNITY_EDITOR
// 门墙特写 (2026-10-11, 只渲染不存盘) —— 取证"门墙左中右三种观感"到底差在哪。
// 相机配置照抄 SceneBuilder.RenderRaw / ToSrgbTex 的口径(DefaultHDR RT + 线性读取 + sRGB 编码),
// 相机组件从 CAM_02_Door 复制(含 URP 附加数据), 只改位置/朝向/FOV。
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HST
{
    public static class HstDoorWallShot
    {
        const string OutDir = "F:/VSCode/工业级渲染/renders/unity_repro";

        [MenuItem("HST/门墙特写出图(不存盘)")]
        public static void Shot()
        {
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var tpl = GameObject.Find("CAM_02_Door");
            if (tpl == null) { Debug.LogError("[HST-DW] 找不到 CAM_02_Door"); return; }
            var tplCam = tpl.GetComponent<Camera>();
            Directory.CreateDirectory(OutDir);
            // 全貌: 站在走廊中段正对整面门墙
            RenderTo(tplCam, new Vector3(-8.600f, 2.600f, 0f), 30f, OutDir + "/_check_doorwall_wide.png");
            // 特写: 门扇 + 左右填充板
            RenderTo(tplCam, new Vector3(-14.000f, 2.000f, 0f), 42f, OutDir + "/_check_doorwall_close.png");
            Debug.Log("[HST-DW] 门墙特写出图完成");
        }

        internal static void RenderTo(Camera tpl, Vector3 pos, float fov, string path, Vector3? lookAt = null)
        {
            var go = new GameObject("HST_DW_TMP");
            var cam = go.AddComponent<Camera>();
            UnityEditor.EditorUtility.CopySerialized(tpl, cam);
            cam.fieldOfView = fov; cam.depth = 0f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 300f;
            cam.clearFlags = tpl.clearFlags;
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(
                lookAt.HasValue ? (lookAt.Value - pos).normalized : Vector3.left, Vector3.up);
            var cd = cam.GetUniversalAdditionalCameraData();
            cd.renderPostProcessing = true;
            cd.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;

            const int w = 1920, h = 1080;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            bool was = cam.enabled;
            cam.enabled = true; cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
            var px = tex.GetPixels();
            cam.targetTexture = null; cam.enabled = was;

            var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var o = new Color32[px.Length];
            float mx = 0f; int over = 0;
            for (int i = 0; i < px.Length; i++)
            {
                float l = 0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b;
                if (l > mx) mx = l; if (l > 1f) over++;
                o[i] = new Color32(
                    (byte)(Mathf.LinearToGammaSpace(Mathf.Clamp01(px[i].r)) * 255f),
                    (byte)(Mathf.LinearToGammaSpace(Mathf.Clamp01(px[i].g)) * 255f),
                    (byte)(Mathf.LinearToGammaSpace(Mathf.Clamp01(px[i].b)) * 255f), 255);
            }
            outTex.SetPixels32(o); outTex.Apply();
            File.WriteAllBytes(path, outTex.EncodeToPNG());
            Debug.Log("[HST-DW] " + Path.GetFileName(path) + " maxLin=" + mx.ToString("F2") +
                      " >1.0占比=" + (100f * over / px.Length).ToString("F2") + "%");

            Object.DestroyImmediate(outTex);
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(go);
        }
    }
}
#endif
