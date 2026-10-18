#if UNITY_EDITOR
// 后处理状态普查(只读) —— 出图(RenderRaw, 不经任何强制设置)与游戏里后处理到底生不生效。
// 疑点: 同一机位同后处理设置下, 960x540 探针得 maxLin=0.992 / 0 过曝, 而 Shoot 出的 1080p 图是 25.48 / 3.69% 过曝。
// 查: ①每台相机的 renderPostProcessing 现值 ②场景 Volume(isGlobal/priority/profile) ③URP Renderer 的 postProcessData
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HST
{
    public static class HstPostInspect
    {
        [MenuItem("HST/后处理状态普查(只读)")]
        public static void Dump()
        {
            EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# 后处理状态普查 —— " + EditorSceneManager.GetActiveScene().name);

            sb.AppendLine("\n## 相机 (renderPostProcessing / antialiasing / renderer)");
            foreach (var cam in Object.FindObjectsOfType<Camera>(true).OrderBy(c => c.name))
            {
                var d = cam.GetUniversalAdditionalCameraData();
                string rdr = d.scriptableRenderer != null ? d.scriptableRenderer.GetType().Name : "-";
                sb.AppendLine($"  {cam.name,-18} enabled={cam.enabled} post={d.renderPostProcessing} aa={d.antialiasing} " +
                              $"renderer={rdr} depth={d.renderType} hdr={cam.allowHDR}");
            }

            sb.AppendLine("\n## Volume 组件");
            var vols = Object.FindObjectsOfType<Volume>(true);
            if (vols.Length == 0) sb.AppendLine("  (场景内无 Volume 组件)");
            foreach (var v in vols)
                sb.AppendLine($"  {v.name,-24} isGlobal={v.isGlobal} priority={v.priority} weight={v.weight} " +
                              $"profile={(v.sharedProfile != null ? v.sharedProfile.name : "null")} " +
                              $"bounds={v.GetComponent<Collider>() != null}");

            sb.AppendLine("\n## 场景 Lighting Settings (反射读全部可读属性)");
            var ls = Lightmapping.GetLightingSettingsForScene(EditorSceneManager.GetActiveScene());
            if (ls != null)
                foreach (var p in ls.GetType().GetProperties(
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    object v;
                    try { v = p.GetValue(ls); } catch { continue; }
                    if (v is float f) sb.AppendLine($"  {p.Name} = {f}");
                }

            sb.AppendLine("\n## 相机上是否存在自定义后处理脚本");
            foreach (var cam in Object.FindObjectsOfType<Camera>(true))
                foreach (var mb in cam.GetComponents<MonoBehaviour>())
                    if (mb != null) sb.AppendLine($"  {cam.name}: {mb.GetType().Name}");

            File.WriteAllText("F:/VSCode/工业级渲染/tmp/post_inspect.txt", sb.ToString());
            Debug.Log("[HST-POST] 完成 → tmp/post_inspect.txt");
        }
    }
}
#endif
