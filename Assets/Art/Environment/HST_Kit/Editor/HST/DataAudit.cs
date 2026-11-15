using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// 数据级对账(2026-10-07, 用户口径): 逐实体导出 Unity 侧实际值, 与 Blender manifest 逐项 diff。
// 判定三态: 一致 / 被换算(注明规则) / 结构性不可忠实(URP 无对应能力)。
public static class DataAudit
{
    public static void Run()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
        var sb = new StringBuilder();
        sb.AppendLine("== 灯 ==");
        foreach (var li in Object.FindObjectsOfType<Light>(true))
        {
            var p = li.transform.position;
            sb.AppendLine($"L|{li.name}|type={li.type}|baketype={li.lightmapBakeType}|pos=({p.x:F3},{p.y:F3},{p.z:F3})|" +
                          $"color=({li.color.r:F3},{li.color.g:F3},{li.color.b:F3})|int={li.intensity:F4}|range={li.range:F2}|" +
                          $"shadow={li.shadows}|bounce={li.bounceIntensity:F2}|attendist={li.range}");
        }
        sb.AppendLine("== 材质 ==");
        foreach (var m in AssetDatabase.FindAssets("t:Material", new[] { "Assets/Art/Environment/HST_Kit/Materials/HST_Prags" }))
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(m));
            var bc = mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.black;
            var mt = mat.GetTexture("_MainTex"); var mg = mat.GetTexture("_MetallicGlossMap"); var nm = mat.GetTexture("_BumpMap");
            sb.AppendLine($"M|{mat.name}|shader={mat.shader.name}|base=({bc.r:F3},{bc.g:F3},{bc.b:F3})|" +
                          $"metal={(mat.HasProperty("_Metallic") ? mat.GetFloat("_Metallic").ToString("F3") : "-")}|" +
                          $"smooth={(mat.HasProperty("_Smoothness") ? mat.GetFloat("_Smoothness").ToString("F3") : "-")}|" +
                          $"ch={(mat.HasProperty("_SmoothnessTextureChannel") ? mat.GetFloat("_SmoothnessTextureChannel").ToString("F0") : "-")}|" +
                          $"main={(mt != null ? mt.name : "null")}|ms={(mg != null ? mg.name : "null")}|norm={(nm != null ? nm.name : "null")}|" +
                          $"GI={mat.globalIlluminationFlags}");
        }
        sb.AppendLine("== 相机 ==");
        foreach (var cam in Object.FindObjectsOfType<Camera>(true))
            sb.AppendLine($"C|{cam.name}|pos=({cam.transform.position.x:F3},{cam.transform.position.y:F3},{cam.transform.position.z:F3})|" +
                          $"rot={cam.transform.rotation.eulerAngles}|fov={cam.fieldOfView:F3}|near={cam.nearClipPlane}|far={cam.farClipPlane}|" +
                          $"post={cam.GetUniversalAdditionalCameraData().renderPostProcessing}");
        sb.AppendLine("== 环境 ==");
        sb.AppendLine($"E|fog={RenderSettings.fog}|mode={RenderSettings.fogMode}|density={RenderSettings.fogDensity:F6}|" +
                      $"fogcolor=({RenderSettings.fogColor.r:F4},{RenderSettings.fogColor.g:F4},{RenderSettings.fogColor.b:F4})|" +
                      $"ambmode={RenderSettings.ambientMode}|amb=({RenderSettings.ambientLight.r:F5},{RenderSettings.ambientLight.g:F5},{RenderSettings.ambientLight.b:F5})|" +
                      $"sky={(RenderSettings.skybox != null ? RenderSettings.skybox.name : "null")}|reflmode={RenderSettings.defaultReflectionMode}|reflint={RenderSettings.reflectionIntensity:F2}");
        sb.AppendLine("== 网格 ==");
        foreach (var mf in Object.FindObjectsOfType<MeshFilter>(true))
            if (mf.sharedMesh != null)
                sb.AppendLine($"G|{mf.name}|tris={mf.sharedMesh.triangles.Length / 3}|mat={(mf.GetComponent<MeshRenderer>()?.sharedMaterial?.name ?? "null")}");
        File.WriteAllText(@"F:\VSCode\工业级渲染\tmp\unity_data_audit.txt", sb.ToString());
        Debug.Log("[HST-AUDIT] 数据对账已导出: tmp/unity_data_audit.txt 行数=" + sb.ToString().Split('\n').Length);
    }
}
