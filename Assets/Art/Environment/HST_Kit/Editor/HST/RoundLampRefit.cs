#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HST
{
    // 圆形吊灯改造(2026-10-09, 用户令"条状灯改成一个个圆形灯+光源同位, 用上一版真实感光影"):
    // 在原走廊场景(上一版 PBR 材质, 不动材质)里:
    //   ①删两条天花条状灯带(ARCH_LightPane)与其 54 盏烘焙代理灯阵、门缝灯;
    //   ②每侧天花装 7 个圆形吊灯(圆柱灯罩, 自发光) + 同位实时点光 —— 灯池即光源, 墙面光影真实;
    //   ③门区/机架照明收拢成 6 盏实时点光(位置/强度取自 manifest 换算);
    //   ④URP 每物体附加光上限 4→24(质量档, 20 盏实时灯 look-dev 必需)。
    // 保存场景(原场景就地改造; Build 一键可恢复条带+灯阵, 可逆)。
    public static class RoundLampRefit
    {
        [MenuItem("HST/圆形吊灯改造(上一版管线·保存场景)")]
        public static void Refit()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity", OpenSceneMode.Single);

            int killed = 0;
            foreach (var li in Object.FindObjectsOfType<Light>(true))
                if (li.name.StartsWith("L_LUMEN_") || li.name.StartsWith("L_STRIP_") || li.name.StartsWith("L_AREA_") || li.name.StartsWith("HST_SeamGlow"))
                { Object.DestroyImmediate(li.gameObject); killed++; }
            foreach (var tr in Object.FindObjectsOfType<Transform>(true))
                if (tr.name.StartsWith("ARCH_LightPane"))
                { Object.DestroyImmediate(tr.gameObject); killed++; }

            // 灯罩材质(内嵌, 随场景保存): 暖白自发光 ~3.0, 默认后处理下呈纯白灯具
            var lampMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            lampMat.SetColor("_BaseColor", new Color(0.9f, 0.92f, 0.95f));
            lampMat.SetColor("_EmissionColor", new Color(1.7f, 1.8f, 1.9f));   // v2: 3.0刺眼降档
            ForceEmission(lampMat);

            int lamps = 0;
            for (int s = 0; s < 2; s++)
                for (int i = 0; i < 7; i++)
                {
                    float x = Mathf.Lerp(-7.5f, 7.5f, i / 6f);
                    float z = s == 0 ? -1.05f : 1.05f;
                    var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    go.name = $"LAMP_Round_{s}_{i}";
                    Object.DestroyImmediate(go.GetComponent<Collider>());
                    go.transform.localScale = new Vector3(0.26f, 0.02f, 0.26f);   // 半径0.13m 圆盘
                    go.transform.position = new Vector3(x, 3.32f, z);
                    go.GetComponent<MeshRenderer>().sharedMaterial = lampMat;

                    var lgo = new GameObject($"LAMP_Round_{s}_{i}_PT");
                    var li = lgo.AddComponent<Light>();
                    li.type = LightType.Point; li.color = new Color(0.90f, 0.95f, 1.0f);
                    li.intensity = 10f; li.range = 12f;   // v2: 观感/光强配平(坎德拉 1/d², 1.0在3.3m外只剩0.09)
                    li.shadows = LightShadows.Soft;
                    li.lightmapBakeType = LightmapBakeType.Realtime;               // 无需烘焙即可见
                    lgo.transform.position = new Vector3(x, 3.24f, z);
                    lamps++;
                }

            // 门区/机架照明收拢(位置取 manifest, 强度=功率×kArea0.0213)
            System.Action<Vector3, Color, float, float, string> pt = (p, c, i2, rg, n) =>
            {
                var go = new GameObject(n); var li = go.AddComponent<Light>();
                li.type = LightType.Point; li.color = c; li.intensity = i2; li.range = rg;
                li.shadows = LightShadows.Soft; li.lightmapBakeType = LightmapBakeType.Realtime;
                go.transform.position = p;
            };
            pt(new Vector3(8.9f, 2.3f, 0f), new Color(0.88f, 0.94f, 1f), 17f, 8f, "LAMP_DoorTop");
            pt(new Vector3(-6.25f, 1.8f, 0f), new Color(0.9f, 0.95f, 1f), 10f, 10f, "LAMP_EndWash");
            pt(new Vector3(-4.25f, 3.0f, 0f), new Color(0.95f, 0.97f, 1f), 14f, 14f, "LAMP_Fill");
            pt(new Vector3(-4.75f, 1.6f, -1.55f), Color.white, 2.6f, 6f, "LAMP_RackA");
            pt(new Vector3(3.25f, 1.6f, 1.55f), Color.white, 2.6f, 6f, "LAMP_RackB");
            pt(new Vector3(8.95f, 1.2f, 0f), new Color(1f, 0.95f, 0.85f), 4f, 4f, "LAMP_Seam");

            // URP 每物体附加光上限 4→24(质量档; 20 盏实时灯必需; 不动渲染特性)
            var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/URP-Asset.asset");
            if (urp != null)
            {
                var so = new SerializedObject(urp);
                var pl = so.FindProperty("m_AdditionalLightsPerObjectLimit");
                if (pl != null) { pl.intValue = 24; so.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(urp); AssetDatabase.SaveAssets(); }
            }

            // v2: 关掉条带时代的灯槽(空槽挂着穿帮); SetActive(false) 可逆
            int coves = 0;
            foreach (var tr in Object.FindObjectsOfType<Transform>(true))
                if (tr.name.StartsWith("ARCH_Cove"))
                { tr.gameObject.SetActive(false); coves++; }

            bool ok = EditorSceneManager.SaveScene(scene);
            Debug.Log($"[LAMP] 圆形吊灯改造已保存={ok}: 删{killed}项(条带+灯阵), 装{lamps}盏圆形吊灯(灯+光同位, 实时软影), 门区收拢6点光, 附加光上限→24。可逆:HST/Build 恢复条带与灯阵。");
        }

        static void ForceEmission(Material m)
        {
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            m.SetFloat("_Surface", 0f);
        }
    }
}
#endif