#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace HST
{
    // 槽内下照灯(2026-10-09, 用户令"光斑变小藏进条形灯带里"):
    // 原理 = 光不是藏的, 是定向的 —— 灯藏在灯带槽内朝正下打, 天花在锥形之外不吃光,
    // 光斑全部落在灯带正下方与墙面下部 = "光从槽里淌下来"(现实灯带的照明形态)。
    //   ①Build 重建走廊(恢复条带网格+灯槽+清掉圆形吊灯改造痕迹, ~1-2 分钟, 不烘焙);
    //   ②删全部代理灯阵(条带网格与灯槽保留可见);
    //   ③每侧槽内 8 盏朝正下聚光(110°锥/短range 4m/软影) → 光斑互叠成连续光带, 天花零光斑;
    //   ④门区收拢 5 点光; 附加光上限保 24。
    public static class CoveDownlights
    {
        [MenuItem("HST/槽内下照灯(条带恢复+光斑藏槽)")]
        public static void Apply()
        {
            Debug.Log("[COVE] 重建走廊(恢复条带/灯槽)…");
            SceneBuilder.Build("corridor");                       // 幂等重建, 条带与灯槽回归
            var scene = EditorSceneManager.GetActiveScene();

            int killed = 0;
            foreach (var li in Object.FindObjectsOfType<Light>(true))
                if (li.name.StartsWith("L_LUMEN_") || li.name.StartsWith("L_STRIP_") || li.name.StartsWith("L_AREA_") || li.name.StartsWith("HST_SeamGlow") || li.name.StartsWith("LAMP_"))
                { Object.DestroyImmediate(li.gameObject); killed++; }
            foreach (var tr in Object.FindObjectsOfType<Transform>(true))
                if (tr.name.StartsWith("LAMP_Round"))
                { Object.DestroyImmediate(tr.gameObject); killed++; }

            int n = 0;
            for (int s = 0; s < 2; s++)
                for (int i = 0; i < 8; i++)
                {
                    float x = Mathf.Lerp(-7.4f, 7.4f, i / 7f);
                    var go = new GameObject($"COVE_DL_{s}_{i}");
                    var li = go.AddComponent<Light>();
                    li.type = LightType.Spot; li.spotAngle = 110f; li.innerSpotAngle = 80f;
                    li.color = new Color(0.90f, 0.95f, 1.0f);
                    li.intensity = 7f; li.range = 4f;
                    li.shadows = LightShadows.Soft;
                    li.lightmapBakeType = LightmapBakeType.Realtime;
                    go.transform.position = new Vector3(x, 3.30f, s == 0 ? -1.05f : 1.05f);
                    go.transform.rotation = Quaternion.LookRotation(Vector3.down);
                    n++;
                }

            System.Action<Vector3, Color, float, float, string> pt = (p, c, i2, rg, nm) =>
            {
                var go = new GameObject(nm); var li = go.AddComponent<Light>();
                li.type = LightType.Point; li.color = c; li.intensity = i2; li.range = rg;
                li.shadows = LightShadows.Soft; li.lightmapBakeType = LightmapBakeType.Realtime;
                go.transform.position = p;
            };
            pt(new Vector3(8.9f, 2.3f, 0f), new Color(0.88f, 0.94f, 1f), 17f, 8f, "LAMP_DoorTop");
            pt(new Vector3(-6.25f, 1.8f, 0f), new Color(0.9f, 0.95f, 1f), 10f, 10f, "LAMP_EndWash");
            pt(new Vector3(-4.25f, 3.0f, 0f), new Color(0.95f, 0.97f, 1f), 14f, 14f, "LAMP_Fill");
            pt(new Vector3(-4.75f, 1.6f, -1.55f), Color.white, 2.6f, 6f, "LAMP_RackA");
            pt(new Vector3(3.25f, 1.6f, 1.55f), Color.white, 2.6f, 6f, "LAMP_RackB");

            var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/URP-Asset.asset");
            if (urp != null)
            {
                var so = new SerializedObject(urp);
                var pl = so.FindProperty("m_AdditionalLightsPerObjectLimit");
                if (pl != null && pl.intValue < 24) { pl.intValue = 24; so.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(urp); AssetDatabase.SaveAssets(); }
            }

            bool ok = EditorSceneManager.SaveScene(scene);
            Debug.Log($"[COVE] 槽内下照灯已保存={ok}: 重建+清{killed}项, 槽内下照{n}盏(110°锥/range4), 门区5点光, 天花零光斑。");
        }
    }
}
#endif