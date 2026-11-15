#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HST
{
    // 把"无灯泡明亮"NPR 定稿观感固化为独立场景(2026-10-09, 用户"打开游戏看不到最新状态"):
    // 出图工具全是非破坏(拍完即还原、不存盘), 所以工程里打开原走廊永远是最初状态——这是设计。
    // 本入口 = 同一套 v7 配方, 但【保存为新场景】SC_Corridor_NPR_LookDev.unity, 原场景不动。
    // 配方: 8方位填充光(12°仰角×0.45) + 影带0.55/0.60/0.66 + 影带软化0.15 + 地板toon阈值2.5
    //       + 发光体÷4 + 原有54盏烘焙代理灯在此场景中删除。
    public static class NprPersistScene
    {
        [MenuItem("HST/生成NPR定稿场景(保存为新场景)")]
        public static void SaveLookDevScene()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);
            var shader = Shader.Find("OurFunction/NPR/Deferred Toon");
            if (shader == null) { Debug.LogError("[NPR-P] 找不到 OurFunction/NPR/Deferred Toon"); return; }
            var brightShadow = new Color(0.55f, 0.60f, 0.66f, 1f);

            int made = 0, emit = 0;
            foreach (var r in Object.FindObjectsOfType<MeshRenderer>(true))
            {
                if (!r.gameObject.activeInHierarchy) continue;
                bool any = false;
                var cur = r.sharedMaterials;
                foreach (var m in cur)
                    if (m != null && (m.name.StartsWith("M_ARCH_") || m.name.StartsWith("M_FORGE_") || m.name.StartsWith("M_M_"))) any = true;
                if (!any) continue;
                var npr = new Material[cur.Length];
                for (int k = 0; k < cur.Length; k++)
                {
                    var src = cur[k];
                    if (src == null) { npr[k] = src; continue; }
                    if (src.name.EndsWith("Emit"))
                    {
                        var t = new Material(src);   // 内嵌材质: 随场景序列化
                        var e = t.GetColor("_EmissionColor");
                        t.SetColor("_EmissionColor", new Color(e.r * 0.25f, e.g * 0.25f, e.b * 0.25f, 1f));
                        npr[k] = t; emit++; continue;
                    }
                    var nm = new Material(shader);
                    if (src.HasProperty("_BaseMap")) nm.SetTexture("_BaseMap", src.GetTexture("_BaseMap"));
                    if (src.HasProperty("_BaseColor")) nm.SetColor("_BaseColor", src.GetColor("_BaseColor"));
                    if (src.HasProperty("_MetallicGlossMap")) nm.SetTexture("_MetallicGlossMap", src.GetTexture("_MetallicGlossMap"));
                    if (src.HasProperty("_Metallic")) nm.SetFloat("_Metallic", src.GetFloat("_Metallic"));
                    if (src.HasProperty("_Smoothness")) nm.SetFloat("_Smoothness", src.GetFloat("_Smoothness"));
                    if (nm.HasProperty("_ShadowColor")) nm.SetColor("_ShadowColor", brightShadow);
                    if (nm.HasProperty("_ToonSoftness")) nm.SetFloat("_ToonSoftness", 0.15f);
                    if (src.name == "M_ARCH_FloorM" && nm.HasProperty("_ToonThreshold")) nm.SetFloat("_ToonThreshold", 2.5f);
                    npr[k] = nm; made++;
                }
                r.sharedMaterials = npr;
            }

            int killed = 0;
            foreach (var li in Object.FindObjectsOfType<Light>(true))
                if (li.name.StartsWith("L_LUMEN_") || li.name.StartsWith("L_STRIP_") || li.name.StartsWith("L_AREA_") || li.name.StartsWith("HST_SeamGlow"))
                { Object.DestroyImmediate(li.gameObject); killed++; }

            for (int a = 0; a < 8; a++)
            {
                var go = new GameObject($"NPR_FILL_{a}");
                var li2 = go.AddComponent<Light>();
                li2.type = LightType.Directional; li2.color = new Color(0.85f, 0.92f, 0.95f); li2.intensity = 0.45f;
                go.transform.rotation = Quaternion.Euler(12f, 45f * a, 0f);
            }

            bool ok = EditorSceneManager.SaveScene(scene, "Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_NPR_LookDev.unity");
            Debug.Log($"[NPR-P] 定稿场景已保存={ok}: 换装{made}+发光{emit}内嵌材质, 删灯{killed}盏, 8方位填充光上岗 → Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_NPR_LookDev.unity(原走廊未动)");
        }
    }
}
#endif