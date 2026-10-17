using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using Cinemachine;

public static class MapStreamingSetup
{
    public const string SourcePath = "Assets/Scenes/BOX_S1013.unity";
    public const string Folder = "Assets/Scenes/MapStreaming";
    public const string DemoPath = Folder + "/MapStreamingDemo.unity";
    public const float RoomLength = 32f;

    [MenuItem("超高速行者/地图无缝加载/打开验收场景")]
    public static void OpenDemo()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("请先停止播放");
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        EditorSceneManager.OpenScene(DemoPath);
    }

    [MenuItem("超高速行者/地图无缝加载/生成三房间验收场景")]
    public static void Build()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("请先停止播放");
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/Scenes", "MapStreaming");
        Scene source = EditorSceneManager.OpenScene(SourcePath);
        GameObject[] roots = source.GetRootGameObjects();
        Material[] colors =
        {
            Material("Room01", new Color(.15f, .55f, .8f)),
            Material("Room02", new Color(.85f, .45f, .15f)),
            Material("Room03", new Color(.2f, .7f, .4f))
        };
        Material wood = Material("Door", new Color(.35f, .17f, .07f));

        for (int index = 0; index < 3; index++)
        {
            string sceneName = RoomName(index);
            GameObject box = roots.Single(root => root.name == $"BOX_S_0{index + 1}");
            Scene room = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.MoveGameObjectToScene(box, room);
            SceneManager.SetActiveScene(room);
            // 白盒不含统一门位，验收通道只验证流式加载；几何保持原尺寸，原始场景不变。
            Bounds bounds = BoundsOf(box);
            box.transform.position += new Vector3(-bounds.max.x - 8f, -bounds.min.y, index * RoomLength + RoomLength * .5f - bounds.center.z);
            foreach (Transform child in box.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(child.gameObject);
            foreach (Enemy enemy in box.GetComponentsInChildren<Enemy>(true)) enemy.gameObject.SetActive(false);
            var marker = new GameObject("MapRoom").AddComponent<MapRoom>();
            marker.Configure(index < 2 ? RoomName(index + 1) : string.Empty);
            float start = index * RoomLength;
            Cube("验收通道地板", new Vector3(0f, -.25f, start + RoomLength * .5f),
                new Vector3(10f, .5f, RoomLength), colors[index]);
            Cube("左护栏", new Vector3(-5f, .6f, start + RoomLength * .5f),
                new Vector3(.3f, 1.2f, RoomLength), colors[index]);
            Cube("右护栏", new Vector3(5f, .6f, start + RoomLength * .5f),
                new Vector3(.3f, 1.2f, RoomLength), colors[index]);
            Label($"ROOM {index + 1}", new Vector3(0f, 3.8f, start + 12f), colors[index].color);

            if (index > 0)
            {
                var trigger = new GameObject("进入房间后卸载上一房");
                trigger.transform.position = new Vector3(0f, 2f, start + 4f);
                var portal = trigger.AddComponent<MapRoomPortal>();
                // 120m/s 在 60Hz 下每步约 2m，触发区要比单步位移更厚，避免高速跨过整个区域。
                trigger.GetComponent<BoxCollider>().size = new Vector3(9f, 5f, 4f);
                portal.Configure(RoomName(index - 1), sceneName);
                // 角色整体通过门后才关回头门，避免上一房卸载后退回空地板。
                GameObject back = Cube("回头门", new Vector3(0f, 2f, start + .2f), new Vector3(10f, 4f, .3f), wood);
                new GameObject("进入后关闭回头门").AddComponent<MapRoomGate>().Configure(sceneName, back, true);
                back.SetActive(false);
            }
            if (index < 2)
            {
                float exit = start + RoomLength - .6f;
                Cube("门框左", new Vector3(-3.3f, 2.5f, exit), new Vector3(.6f, 5f, .6f), wood);
                Cube("门框右", new Vector3(3.3f, 2.5f, exit), new Vector3(.6f, 5f, .6f), wood);
                Cube("门楣", new Vector3(0f, 4.8f, exit), new Vector3(7.2f, .6f, .6f), wood);
                GameObject door = Cube("加载完成自动开门", new Vector3(0f, 2f, exit),
                    new Vector3(10f, 4f, .35f), wood);
                new GameObject("下一房加载保护").AddComponent<MapRoomGate>().Configure(RoomName(index + 1), door);
            }
            else
            {
                Cube("终点墙", new Vector3(0f, 2f, start + RoomLength - .2f), new Vector3(10f, 4f, .3f), colors[index]);
                Label("PASS / END", new Vector3(0f, 2f, start + RoomLength - .4f), Color.white);
            }
            SetLighting();
            if (!EditorSceneManager.SaveScene(room, RoomPath(index))) throw new IOException("房间保存失败");
        }

        Scene bootstrap = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        foreach (GameObject root in roots)
        {
            if (root == null || root.scene != source) continue;
            bool keep = root.name == "Player" || root.name == "Virtual Camera" || root.name == "Global Volume" ||
                root.name == "Directional Light" || root.name == "SunLight" ||
                (root.name == "Main Camera" && PrefabUtility.IsPartOfPrefabInstance(root));
            if (keep) SceneManager.MoveGameObjectToScene(root, bootstrap);
        }
        SceneManager.SetActiveScene(bootstrap);
        PlayerMotor motor = bootstrap.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<PlayerMotor>(true)).Single();
        motor.transform.position = new Vector3(0f, .1f, 3f);
        motor.transform.rotation = Quaternion.identity;
        var virtualCamera = bootstrap.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<CinemachineVirtualCamera>(true)).Single();
        var volume = bootstrap.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Volume>(true)).Single();
        foreach (CameraPostProcessing effect in bootstrap.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<CameraPostProcessing>(true)))
        {
            effect.Configure(motor, virtualCamera, volume);
            EditorUtility.SetDirty(effect);
            PrefabUtility.RecordPrefabInstancePropertyModifications(effect);
        }
        var startup = new GameObject("MapStartup");
        startup.AddComponent<MapBootstrapper>().Configure(RoomName(0), motor);
        startup.AddComponent<MapStreamingHUD>();
        SetLighting();
        if (!EditorSceneManager.SaveScene(bootstrap, DemoPath)) throw new IOException("启动场景保存失败");
        var buildScenes = EditorBuildSettings.scenes.ToList();
        foreach (string path in new[] { DemoPath, RoomPath(0), RoomPath(1), RoomPath(2) })
            if (!buildScenes.Any(entry => entry.path == path)) buildScenes.Add(new EditorBuildSettingsScene(path, true));
        EditorBuildSettings.scenes = buildScenes.ToArray();
        AssetDatabase.SaveAssets();
        EditorSceneManager.OpenScene(DemoPath);
        Debug.Log("MAP_SETUP_PASS: 三个白盒房间已拆分，常驻玩家/相机、保护门、触发器和 Build Settings 已接好");
    }

    public static string RoomName(int index) => $"Map_Room_0{index + 1}";
    public static string RoomPath(int index) => Folder + "/" + RoomName(index) + ".unity";

    private static Bounds BoundsOf(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        Bounds bounds = new Bounds(root.transform.position, Vector3.zero);
        if (renderers.Length == 0) return bounds;
        bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
        return bounds;
    }

    private static GameObject Cube(string name, Vector3 position, Vector3 size, Material material)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        cube.transform.position = position;
        cube.transform.localScale = size;
        cube.GetComponent<Renderer>().sharedMaterial = material;
        return cube;
    }

    private static Material Material(string name, Color color)
    {
        string path = Folder + "/" + name + ".mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null) return material;
        material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        material.color = color;
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    private static void Label(string text, Vector3 position, Color color)
    {
        var mesh = new GameObject(text).AddComponent<TextMesh>();
        mesh.text = text;
        mesh.anchor = TextAnchor.MiddleCenter;
        mesh.fontSize = 80;
        mesh.characterSize = .05f;
        mesh.color = color;
        mesh.transform.position = position;
    }

    private static void SetLighting()
    {
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(.7f, .7f, .7f);
        RenderSettings.fog = false;
    }

    public static void InspectWhitebox()
    {
        var scene = EditorSceneManager.OpenScene(SourcePath);
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            Bounds bounds = new Bounds(root.transform.position, Vector3.zero);
            if (renderers.Length > 0)
            {
                bounds = renderers[0].bounds;
                foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
            }
            Debug.Log($"MAP_INSPECT {root.name} pos={root.transform.position} min={bounds.min} max={bounds.max}");
            if (!root.name.StartsWith("BOX_S_")) continue;
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                Bounds floor = collider.bounds;
                if (floor.size.y < 3f && floor.size.x > 10f && floor.size.z > 10f)
                    Debug.Log($"MAP_FLOOR {root.name}/{collider.name} center={floor.center} size={floor.size}");
            }
        }
    }
}
