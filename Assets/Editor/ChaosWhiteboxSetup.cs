using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// 只给已接通的白盒加联调组件，不重新生成地图、不覆盖队友原预制体。
public static class ChaosWhiteboxSetup
{
    public const string AssetFolder = "Assets/Prefabs/Maps/ChaosIntegration";
    public const string SettingsPath = "Assets/Settings/Chaos/ChaosRules.asset";
    private const string ContentName = "混乱度联调_占位内容";

    [MenuItem("超高速行者/混乱度/接入现有团队白盒")]
    public static void Install()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("请先停止播放并保存");
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        EnsureFolder(AssetFolder);
        EnsureFolder("Assets/Settings/Chaos");
        ChaosSettings settings = AssetDatabase.LoadAssetAtPath<ChaosSettings>(SettingsPath);
        if (settings == null)
        {
            settings = ScriptableObject.CreateInstance<ChaosSettings>();
            AssetDatabase.CreateAsset(settings, SettingsPath);
        }
        GameObject lampPrefab = MakeLampPrefab();
        GameObject[] variants = Enumerable.Range(0, 3).Select(MakeVariantPrefab).ToArray();
        Scene game = EditorSceneManager.OpenScene(TeamWhiteboxSetup.GamePath);
        ChaosManager chaos = game.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<ChaosManager>(true)).FirstOrDefault();
        if (chaos == null) chaos = new GameObject("ChaosSystem").AddComponent<ChaosManager>();
        chaos.Configure(settings);
        if (chaos.GetComponent<ChaosHUD>() == null) chaos.gameObject.AddComponent<ChaosHUD>();
        PlayerMotor player = game.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<PlayerMotor>(true)).Single();
        if (player.GetComponent<ChaosRunController>() == null) player.gameObject.AddComponent<ChaosRunController>();
        EditorUtility.SetDirty(chaos);
        EditorSceneManager.SaveScene(game);

        for (int index = 0; index < 5; index++)
        {
            string name = TeamWhiteboxSetup.RoomName(index);
            Scene scene = EditorSceneManager.OpenScene(TeamWhiteboxSetup.SceneFolder + "/" + name + ".unity");
            MapRoom room = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<MapRoom>(true)).Single();
            // 已存在的联调根保留，重复执行不会抹掉手工调整、重置编号或再放一套装置。
            if (scene.GetRootGameObjects().Any(root => root.name == ContentName)) continue;
            GameObject content = new GameObject(ContentName);
            content.transform.SetPositionAndRotation(room.RespawnPoint.position, room.EntryPoint.rotation);
            var checkpoint = new GameObject("入口记录点");
            checkpoint.transform.SetParent(content.transform, false);
            var trigger = checkpoint.AddComponent<ChaosCheckpoint>();
            trigger.Configure("入口", 0);
            checkpoint.GetComponent<BoxCollider>().center = Vector3.up;
            checkpoint.GetComponent<BoxCollider>().size = new Vector3(4f, 3f, 4f);
            GameObject marker = Primitive("记录点占位雕像", PrimitiveType.Cylinder, checkpoint.transform,
                new Vector3(-2.2f, .45f, 0f), new Vector3(.4f, .45f, .4f), Material("Checkpoint", Color.cyan));
            UnityEngine.Object.DestroyImmediate(marker.GetComponent<Collider>());
            Text("记录点提示", "记录点 / 自动记录", checkpoint.transform, new Vector3(-2.2f, 1.6f, 0f), .16f);

            for (int lampIndex = 0; lampIndex < 2; lampIndex++)
            {
                GameObject lamp = (GameObject)PrefabUtility.InstantiatePrefab(lampPrefab, scene);
                lamp.transform.SetParent(content.transform, false);
                lamp.transform.localPosition = new Vector3(lampIndex == 0 ? -1.7f : 1.7f, 0f, 2.8f);
                lamp.name = "入口稳定装置_" + (lampIndex == 0 ? "加10" : "加15");
                var device = lamp.GetComponent<ChaosLamp>();
                device.Configure(name, "入口灯_" + lampIndex, lampIndex == 0 ? 10 : 15,
                    lamp.transform.Find("完整装置").gameObject, lamp.transform.Find("碎片").gameObject, lamp.GetComponent<BoxCollider>());
                lamp.GetComponentInChildren<TextMesh>(true).text = "稳定装置\n左键击碎 +" + device.Gain;
                PrefabUtility.RecordPrefabInstancePropertyModifications(device);
                PrefabUtility.RecordPrefabInstancePropertyModifications(lamp.GetComponentInChildren<TextMesh>(true));
            }
            var variation = new GameObject("房间差分_联调阈值");
            variation.transform.SetParent(content.transform, false);
            variation.AddComponent<ChaosRoomVariants>().Configure(new[]
            {
                new ChaosRoomVariant { Id = "联调A", MinimumChaos = int.MinValue, MaximumChaos = 30, ContentPrefab = variants[0] },
                new ChaosRoomVariant { Id = "联调B", MinimumChaos = 31, MaximumChaos = 79, ContentPrefab = variants[1] },
                new ChaosRoomVariant { Id = "联调C", MinimumChaos = 80, MaximumChaos = int.MaxValue, ContentPrefab = variants[2] }
            });
            EditorSceneManager.SaveScene(scene);
        }
        AssetDatabase.SaveAssets();
        EditorSceneManager.OpenScene("Assets/Scenes/Boot.unity");
        Debug.Log("CHAOS_SETUP_PASS: 已给现有白盒接入混乱度、真实攻击装置、入口记录点及三种联调差分；未实现随机/循环生成");
    }

    private static GameObject MakeLampPrefab()
    {
        string path = AssetFolder + "/StabilityLamp.prefab";
        GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (existing != null) return existing;
        var root = new GameObject("StabilityLamp");
        try
        {
            var intact = new GameObject("完整装置"); intact.transform.SetParent(root.transform, false);
            Primitive("发光灯体", PrimitiveType.Sphere, intact.transform, new Vector3(0f, 1.1f, 0f),
                Vector3.one * .7f, Material("Lamp", new Color(1f, .8f, .1f)));
            Primitive("灯架", PrimitiveType.Cylinder, intact.transform, new Vector3(0f, .4f, 0f),
                new Vector3(.2f, .4f, .2f), Material("Metal", Color.gray));
            foreach (Collider shape in intact.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(shape);
            var lampLight = new GameObject("灯光").AddComponent<Light>();
            lampLight.transform.SetParent(intact.transform, false); lampLight.transform.localPosition = Vector3.up * 1.2f;
            lampLight.type = LightType.Point; lampLight.color = new Color(1f, .8f, .2f); lampLight.intensity = 3f; lampLight.range = 4f;
            var broken = new GameObject("碎片"); broken.transform.SetParent(root.transform, false);
            for (int i = 0; i < 3; i++)
            {
                GameObject piece = Primitive("碎片" + i, PrimitiveType.Cube, broken.transform,
                    new Vector3((i - 1) * .22f, .12f, (i % 2) * .2f), Vector3.one * .2f, Material("Metal", Color.gray));
                UnityEngine.Object.DestroyImmediate(piece.GetComponent<Collider>());
            }
            broken.SetActive(false);
            BoxCollider hit = root.AddComponent<BoxCollider>(); hit.center = Vector3.up; hit.size = new Vector3(.9f, 1.8f, .9f);
            root.AddComponent<ChaosLamp>().Configure(string.Empty, "稳定装置", 10, intact, broken, hit);
            Text("操作提示", "稳定装置\n左键击碎 +10", root.transform, new Vector3(0f, 2f, 0f), .16f);
            return PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    private static GameObject MakeVariantPrefab(int index)
    {
        string path = AssetFolder + "/IntegrationVariant_" + index + ".prefab";
        GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (existing != null) return existing;
        var root = new GameObject("联调差分_" + index);
        try
        {
            Color color = new[] { Color.cyan, new Color(1f, .6f, .1f), new Color(1f, .2f, .5f) }[index];
            for (int i = 0; i <= index; i++)
            {
                GameObject prop = Primitive("差分物件" + i, index == 0 ? PrimitiveType.Sphere : PrimitiveType.Cube,
                    root.transform, new Vector3((i - index * .5f) * 1.1f, 3.5f, 5f),
                    index == 2 ? new Vector3(.65f, 1.5f, .2f) : Vector3.one * .65f, Material("Variant" + index, color));
                // 联调物件不改变队友原跑酷路线和导航，正式障碍物另由关卡配置。
                UnityEngine.Object.DestroyImmediate(prop.GetComponent<Collider>());
            }
            Text("差分文字", "联调差分 " + (char)('A' + index) + " / 非正式关卡内容", root.transform,
                new Vector3(0f, 4.6f, 5f), .18f);
            var light = new GameObject("差分局部灯光").AddComponent<Light>();
            light.transform.SetParent(root.transform, false); light.transform.localPosition = new Vector3(0f, 2.5f, 4f);
            light.type = LightType.Point; light.color = color; light.intensity = 3f; light.range = 7f;
            return PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    private static GameObject Primitive(string name, PrimitiveType type, Transform parent, Vector3 position, Vector3 scale, Material material)
    {
        GameObject item = GameObject.CreatePrimitive(type); item.name = name;
        item.transform.SetParent(parent, false); item.transform.localPosition = position; item.transform.localScale = scale;
        item.GetComponent<Renderer>().sharedMaterial = material;
        return item;
    }
    private static void Text(string name, string text, Transform parent, Vector3 position, float size)
    {
        var label = new GameObject(name).AddComponent<TextMesh>();
        label.transform.SetParent(parent, false); label.transform.localPosition = position;
        label.text = text; label.characterSize = size; label.fontSize = 48;
        label.anchor = TextAnchor.MiddleCenter; label.alignment = TextAlignment.Center;
        label.transform.localScale = Vector3.one * .2f;
        label.color = new Color(.08f, .12f, .18f);
    }

    [MenuItem("超高速行者/混乱度/调整联调标注尺寸")]
    public static void TidyIntegrationLabels()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("请先停止播放并保存");
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        foreach (string path in new[] { AssetFolder + "/StabilityLamp.prefab" }.Concat(
            Enumerable.Range(0, 3).Select(index => AssetFolder + "/IntegrationVariant_" + index + ".prefab")))
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                foreach (TextMesh label in root.GetComponentsInChildren<TextMesh>(true)) TidyLabel(label);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        for (int index = 0; index < 5; index++)
        {
            Scene scene = EditorSceneManager.OpenScene(TeamWhiteboxSetup.SceneFolder + "/" + TeamWhiteboxSetup.RoomName(index) + ".unity");
            GameObject content = scene.GetRootGameObjects().Single(root => root.name == ContentName);
            foreach (TextMesh label in content.GetComponentsInChildren<TextMesh>(true))
            {
                TidyLabel(label);
                PrefabUtility.RecordPrefabInstancePropertyModifications(label);
                PrefabUtility.RecordPrefabInstancePropertyModifications(label.transform);
            }
            EditorSceneManager.SaveScene(scene);
        }
        AssetDatabase.SaveAssets();
        EditorSceneManager.OpenScene("Assets/Scenes/Boot.unity");
        Debug.Log("CHAOS_LABELS_PASS: 仅调整本模块新建的联调标注尺寸和颜色");
    }

    private static void TidyLabel(TextMesh label)
    {
        label.transform.localScale = Vector3.one * .2f;
        label.color = new Color(.08f, .12f, .18f);
    }
    private static Material Material(string name, Color color)
    {
        string path = AssetFolder + "/" + name + ".mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetColor("_BaseColor", color);
            AssetDatabase.CreateAsset(material, path);
        }
        return material;
    }
    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = folder.Substring(0, folder.LastIndexOf('/'));
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, folder.Substring(folder.LastIndexOf('/') + 1));
    }
}
