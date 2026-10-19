// DeckSupports.cs — S_04 浮空道路的支撑柱装配（Editor-only，可回退）
//
// 输入：工业级渲染/exports/deck_road/SUPPORTS.fbx（44 根，世界坐标已烘焙）
// 输出：DEC_Kit/S_04/Supports 容器 + 4 份材质资产
//
// 口径：
//   · 只加视觉层，不加 Collider（碰撞仍由原灰盒承担）
//   · 材质：结构钢（metallic .9 / rough .45）、柱头发光缝（青）、底部警示（橙）、柱头深灰
//   · 可重复执行（幂等：先清同名容器再建）
//
// 菜单：MapSkin/Deck/① 装配支撑柱
// 批处理：-executeMethod MapSkin.DeckSupports.Run
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Linq;
using UnityEngine;

namespace MapSkin
{
    public static class DeckSupports
    {
        static string ScenePath = "Assets/Scenes/Level1.unity";
        const string FbxPath = "Assets/Art/Environment/Deck/Support/SUPPORTS.fbx";
        const string MatRoot = "Assets/Art/Materials/MapSkin/DeckSupport";
        const string RootName = "DEC_Kit/S_04/Supports";

        /// <summary>批处理可选 -scene &lt;Assets/Scenes/xxx.unity&gt;：换皮工作一律跑在 Level1_Skin。</summary>
        static void ParseArgs()
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-scene") ScenePath = args[i + 1];
        }

        [MenuItem("MapSkin/Deck/② 打印支撑柱包围盒")]
        public static void Dump()
        {
            ParseArgs();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            foreach (var t in Object.FindObjectsOfType<Transform>(true))
            {
                if (!t.name.Contains("Supports") && !t.name.Contains("Support")) continue;
                var rs = t.GetComponentsInChildren<Renderer>(true);
                if (rs.Length == 0) continue;
                var b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                Debug.Log($"[DeckSupports·dump] {t.name} parent={t.parent?.name} pos={t.position} " +
                          $"scale={t.lossyScale} renderers={rs.Length} bounds={b.size} center={b.center}");
            }
        }

        [MenuItem("MapSkin/Deck/① 装配支撑柱")]
        public static void Run()
        {
            ParseArgs();
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var kit = GameObject.Find("DEC_Kit/S_04");
            if (kit == null)
            {
                Debug.LogError("[DeckSupports] 找不到 DEC_Kit/S_04，先跑 S_04 deck kit");
                return;
            }
            var old = kit.transform.Find("Supports");
            if (old != null) Object.DestroyImmediate(old.gameObject);

            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            if (fbx == null)
            {
                Debug.LogError($"[DeckSupports] 找不到 {FbxPath}");
                return;
            }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            inst.name = "Supports";
            inst.transform.SetParent(kit.transform, false);
            inst.transform.localPosition = Vector3.zero;
            // Blender→FBX 这次导出把世界 X/Z 镜像了（实测中心 -550,-49），
            // 绕 Y 转 180° 正好把整组柱网映射回 (550,49)；高度轴不受影响。
            inst.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            inst.transform.localScale = Vector3.one;

            foreach (var c in inst.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Cube").ToArray())
                Object.DestroyImmediate(c.gameObject);

            var mats = new[]
            {
                EnsureMat("M_Support_Struct", new Color(0.30f, 0.31f, 0.33f), 0.6f, 0.5f, Color.black, 0f),
                EnsureMat("M_Support_Emit",  new Color(0.25f, 0.65f, 0.75f), 0.2f, 0.25f, new Color(0.30f, 0.85f, 1.0f), 3.2f),
                EnsureMat("M_Support_Hazard",new Color(0.73f, 0.33f, 0.05f), 0.3f, 0.5f, Color.black, 0f),
                EnsureMat("M_Support_Cap",   new Color(0.18f, 0.19f, 0.21f), 0.75f, 0.45f, Color.black, 0f),
            };
            int n = 0;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                // FBX 的材质槽可能被合并，按节点名分配最稳：
                // CAP→柱头 / EMIT→发光缝 / HAZ→警示带 / 其余→结构钢
                string nm = r.gameObject.name.ToUpperInvariant();
                Material m = nm.Contains("EMIT") ? mats[1]
                           : nm.Contains("HAZ")  ? mats[2]
                           : nm.Contains("CAP")  ? mats[3]
                           : mats[0];
                var arr = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                for (int i = 0; i < arr.Length; i++) arr[i] = m;
                r.sharedMaterials = arr;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
                n++;
            }
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[DeckSupports] 装配 {n} 个 renderer，位置取 SUPPORTS.fbx 烘焙坐标");
        }

        static Material EnsureMat(string name, Color baseCol, float metallic, float smooth, Color emit, float emitIntensity)
        {
            if (!AssetDatabase.IsValidFolder(MatRoot))
            {
                if (!AssetDatabase.IsValidFolder("Assets/Art/Materials/MapSkin"))
                    AssetDatabase.CreateFolder("Assets/Art/Materials", "MapSkin");
                AssetDatabase.CreateFolder("Assets/Art/Materials/MapSkin", "DeckSupport");
            }
            string path = $"{MatRoot}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", baseCol);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smooth);
            if (emitIntensity > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emit * emitIntensity);
            }
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
