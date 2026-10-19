using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MapSkin
{
    /// <summary>
    /// S_03 机架墙贴墙补光：沿四面机架墙各放 2–3 盏 Realtime 点光，照亮 U 位与门框。
    /// 只加灯，不改材质；Clear 删除。
    /// 批处理：-executeMethod MapSkin.S03RackLights.Run / Clear
    /// </summary>
    public static class S03RackLights
    {
        const string ScenePath = "Assets/Scenes/Level1.unity";
        const string RootName = "S03_RackLights";

        public static void Run()
        {
            OpenScene();
            var old = GameObject.Find(RootName);
            if (old != null) Object.DestroyImmediate(old);
            var root = new GameObject(RootName);

            // 四面机架墙的世界中心（x, z 长轴范围来自 S_03_Beam_009..012）
            var walls = new[]
            {
                (x: 379f, z0: 18f,  z1: 90f),
                (x: 347f, z0: 14f,  z1: 99f),
                (x: 363f, z0: 25f,  z1: 96f),
                (x: 397f, z0: 9f,   z1: 97f),
            };
            int n = 0;
            foreach (var w in walls)
            {
                for (int i = 0; i < 3; i++)
                {
                    float z = Mathf.Lerp(w.z0 + 8f, w.z1 - 8f, i / 2f);
                    var go = new GameObject($"S03RackLight_{n:00}");
                    go.transform.SetParent(root.transform, false);
                    go.transform.position = new Vector3(w.x - 3.5f, 55f, z);
                    var l = go.AddComponent<Light>();
                    l.type = LightType.Point;
                    l.range = 30f;
                    l.intensity = 45f;
                    l.color = new Color(0.82f, 0.90f, 1.0f);
                    l.shadows = LightShadows.None;
                    l.renderMode = LightRenderMode.ForcePixel;
                    n++;
                }
            }
            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[S03灯光] 新增 {n} 盏贴墙点光 → {RootName}");
        }

        public static void Clear()
        {
            OpenScene();
            var go = GameObject.Find(RootName);
            if (go != null) { Object.DestroyImmediate(go); Debug.Log("[S03灯光] 已删除补光"); }
            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
        }

        static void OpenScene()
        {
            var cur = EditorSceneManager.GetActiveScene();
            if (cur.IsValid() && cur.path == ScenePath) return;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }
    }
}
