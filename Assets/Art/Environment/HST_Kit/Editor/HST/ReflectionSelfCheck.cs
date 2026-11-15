#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace HST
{
    // 反射探针自检(精简批处理版): 探针配置 + 材质反射开关 + 渲染器用法统计。
    // cubemap 内容另由 Blender 读 EXR 分析(不走 C# 脆弱 API)。
    public static class ReflectionSelfCheck
    {
        [MenuItem("HST/反射探针自检")]
        public static void Run()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
            var sb = new System.Text.StringBuilder("[RSC] 反射探针自检");
            foreach (var rp in Object.FindObjectsOfType<ReflectionProbe>(true))
                sb.Append($"\n探针 {rp.name}: mode={rp.mode} res={rp.resolution} inten={rp.intensity:F2} box={rp.size} " +
                          $"customTex={(rp.customBakedTexture ? rp.customBakedTexture.name : "无")}");
            foreach (var name in new[] { "M_ARCH_WallM", "M_ARCH_FloorM", "M_ARCH_CeilM" })
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Art/Environment/HST_Kit/Materials/HST_Prags/{name}.mat");
                if (m == null) continue;
                sb.Append($"\n材质 {name}: smooth={m.GetFloat("_Smoothness"):F2} 反射关词={m.IsKeywordEnabled("_ENVIRONMENTREFLECTIONS_OFF")} spec关词={m.IsKeywordEnabled("_SPECULARHIGHLIGHTS_OFF")}");
            }
            int off = 0, blend = 0, simple = 0;
            foreach (var r in Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (r.reflectionProbeUsage == ReflectionProbeUsage.Off) off++;
                else if (r.reflectionProbeUsage == ReflectionProbeUsage.Simple) simple++;
                else blend++;
            }
            sb.Append($"\n渲染器探针用法: Off={off} Simple={simple} Blend={blend}");
            var sky = RenderSettings.defaultReflectionMode;
            sb.Append($"\n场景反射源: defaultReflectionMode={sky} customReflectionTexture={(RenderSettings.customReflectionTexture ? RenderSettings.customReflectionTexture.name : "无")}");
            Debug.Log(sb.ToString());
        }
    }
}
#endif