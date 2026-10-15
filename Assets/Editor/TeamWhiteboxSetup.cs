using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using Unity.AI.Navigation;
using Cinemachine;

public static class TeamWhiteboxSetup
{
    public const string ArchivePath = "Assets/Scenes/LevelTest_Cdwy/LevelTest_Cdwy (2).zip";
    public const string ImportFolder = "Assets/Prefabs/Maps/TeamWhitebox";
    public const string SceneFolder = "Assets/Scenes/WhiteboxStreaming";
    public const string GamePath = SceneFolder + "/WhiteboxGame.unity";
    private static readonly string[] RoomAssets = { "BOX_S教程", "BOX_S01", "BOX_S02", "BOX_S03", "BOX_S04" };
    private static readonly string[] EntryDoors = { "Door", "Door", "Door (2)", "Door (2)", "Door (4)" };
    private static readonly string[] ExitDoors = { "Door (1)", "Door (1)", "Door (3)", "Door (3)", "Door (5)" };
    private const float BridgeGap = 1f;
    private const float TriggerDepth = 4f;
    private const float ThresholdCollisionOverlap = 1f;

    public static string RoomName(int index) => index == 0 ? "Whitebox_Tutorial" : $"Whitebox_Room_0{index}";
    private static string RoomPath(int index) => SceneFolder + "/" + RoomName(index) + ".unity";

    public static void InspectDoorObstacles()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("请先停止播放");
        EditorSceneManager.OpenScene(RoomPath(1));
        EditorSceneManager.OpenScene(RoomPath(2), OpenSceneMode.Additive);
        foreach (MapRoomGate gate in UnityEngine.Object.FindObjectsOfType<MapRoomGate>())
        {
            var settings = new SerializedObject(gate);
            if (!settings.FindProperty("_closeAfterEntering").boolValue)
                ((GameObject)settings.FindProperty("_door").objectReferenceValue).SetActive(false);
        }
        Physics.SyncTransforms();
        MapRoom room = SceneManager.GetSceneByName(RoomName(1)).GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<MapRoom>(true)).Single();
        Vector3 direction = room.ExitPoint.forward;
        Vector3 start = room.ExitPoint.position - direction * 3f + Vector3.up * .15f;
        RaycastHit[] hits = Physics.CapsuleCastAll(start + Vector3.up * .3f, start + Vector3.up * 1.7f, .3f,
            direction, 25f, ~0, QueryTriggerInteraction.Ignore);
        foreach (RaycastHit hit in hits.OrderBy(hit => hit.distance).Take(12))
            Debug.Log($"TEAM_BLOCKER {hit.collider.gameObject.scene.name}/{hit.collider.name} parent={hit.collider.transform.parent?.name} distance={hit.distance} bounds={hit.collider.bounds} collider={hit.collider.GetType().Name}");
    }

    [MenuItem("超高速行者/地图无缝加载/打开团队白盒游戏")]
    public static void OpenGame()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("请先停止播放");
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        EditorSceneManager.OpenScene("Assets/Scenes/Boot.unity");
    }

    [MenuItem("超高速行者/地图无缝加载/修复团队白盒接缝闪烁")]
    public static void RepairSeams()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("请先停止播放");
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        DoorInfo previousExit = default;
        for (int i = 0; i < RoomAssets.Length; i++)
        {
            Scene scene = EditorSceneManager.OpenScene(RoomPath(i));
            GameObject root = scene.GetRootGameObjects().Single(item => item.name.StartsWith("BOX_S", StringComparison.Ordinal));
            Transform[] children = root.GetComponentsInChildren<Transform>(true);
            DoorInfo entry = ReadDoor(root, children.Single(child => child.name == EntryDoors[i]));
            DoorInfo exit = ReadDoor(root, children.Single(child => child.name == ExitDoors[i]));
            UpdateThreshold(children.Single(child => child.name == "入口门槛").gameObject, entry, root);
            UpdateThreshold(children.Single(child => child.name == "出口门槛").gameObject, exit, root);
            if (i > 0)
            {
                BridgeShape(previousExit, entry, out Vector3 center, out float width, out float length);
                GameObject[] roots = scene.GetRootGameObjects();
                UpdateBox(roots.Single(item => item.name == "两门间连接地板"), center - Vector3.up * .15f,
                    new Vector3(width, .3f, length), previousExit.Outward);
                Vector3 side = Vector3.Cross(Vector3.up, previousExit.Outward) * (width * .5f + .1f);
                UpdateBox(roots.Single(item => item.name == "连接段左侧墙"), center + side + Vector3.up * 2f,
                    new Vector3(.2f, 4f, length), previousExit.Outward);
                UpdateBox(roots.Single(item => item.name == "连接段右侧墙"), center - side + Vector3.up * 2f,
                    new Vector3(.2f, 4f, length), previousExit.Outward);
                Scene previousScene = EditorSceneManager.OpenScene(RoomPath(i - 1), OpenSceneMode.Additive);
                GameObject previousRoot = previousScene.GetRootGameObjects().Single(item => item.name.StartsWith("BOX_S", StringComparison.Ordinal));
                ConnectSeamSurfaces(previousRoot, root, roots.Single(item => item.name == "两门间连接地板"));
                EditorSceneManager.SaveScene(previousScene);
                EditorSceneManager.CloseScene(previousScene, true);
                SceneManager.SetActiveScene(scene);
            }
            Physics.SyncTransforms();
            BakeNavigation(root, RoomName(i));
            EditorSceneManager.SaveScene(scene);
            previousExit = exit;
        }
        AssetDatabase.SaveAssets();
        EditorSceneManager.OpenScene("Assets/Scenes/Boot.unity");
        Debug.Log("WHITEBOX_SEAM_REPAIR_PASS: 保留原关卡，裁剪共面门槛，仅在真实门口间隙保留连接段");
    }

    [MenuItem("超高速行者/地图无缝加载/接通团队白盒默认路线")]
    public static void BuildGame()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("请先停止播放");
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ImportAssets();
        foreach (string name in RoomAssets) WhiteboxMeshRecovery.RepairPrefab(FindPrefab(name), ImportFolder + "/RecoveredMeshes");
        if (!AssetDatabase.IsValidFolder(SceneFolder)) AssetDatabase.CreateFolder("Assets/Scenes", "WhiteboxStreaming");
        EditorSceneManager.OpenScene("Assets/Scenes/Boot.unity", OpenSceneMode.Single);
        Material wood = AssetDatabase.LoadAssetAtPath<Material>("Assets/Scenes/MapStreaming/Door.mat");
        DoorInfo previousExit = default;
        DoorInfo firstEntry = default;
        GameObject previousRoot = null;

        for (int i = 0; i < RoomAssets.Length; i++)
        {
            Scene roomScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(roomScene);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(FindPrefab(RoomAssets[i])), roomScene);
            PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            Transform entryFrame = root.GetComponentsInChildren<Transform>(true).Single(child => child.name == EntryDoors[i]);
            Transform exitFrame = root.GetComponentsInChildren<Transform>(true).Single(child => child.name == ExitDoors[i]);
            DoorInfo entry = ReadDoor(root, entryFrame);
            Vector3 wantedOutward = i == 0 ? Vector3.back : -previousExit.Outward;
            root.transform.rotation = Quaternion.FromToRotation(entry.Outward, wantedOutward) * root.transform.rotation;
            Physics.SyncTransforms();
            entry = ReadDoor(root, entryFrame);
            Vector3 destination = i == 0 ? Vector3.zero :
                previousExit.Floor + previousExit.Outward * ((previousExit.Depth + entry.Depth) * .5f + BridgeGap);
            root.transform.position += destination - entry.Floor;
            Physics.SyncTransforms();
            entry = ReadDoor(root, entryFrame);
            DoorInfo exit = ReadDoor(root, exitFrame);
            if (i == 0) firstEntry = entry;

            var marker = new GameObject("MapRoom").AddComponent<MapRoom>();
            Transform entryPoint = Anchor("EntryPoint", entry.Floor, -entry.Outward, marker.transform);
            Transform exitPoint = Anchor("ExitPoint", exit.Floor, exit.Outward, marker.transform);
            marker.Configure(i + 1 < RoomAssets.Length ? RoomName(i + 1) : string.Empty, entryPoint, exitPoint);
            Transform checkpoint = Anchor("RespawnPoint", entry.Floor - entry.Outward * (entry.Depth * .5f + 3f) + Vector3.up * .15f,
                -entry.Outward, marker.transform);
            float lowestFloor = float.PositiveInfinity;
            foreach (MeshCollider floor in root.GetComponentsInChildren<MeshCollider>(true))
                if (floor.enabled && floor.gameObject.activeInHierarchy && floor.sharedMesh != null &&
                    floor.sharedMesh.bounds.size.y < .001f && Mathf.Abs(Vector3.Dot(floor.transform.up, Vector3.up)) > .999f)
                    lowestFloor = Mathf.Min(lowestFloor, floor.bounds.center.y);
            if (float.IsPositiveInfinity(lowestFloor)) lowestFloor = BoundsOf(root).min.y;
            marker.ConfigureRespawn(checkpoint, lowestFloor - 10f);

            AddThreshold("入口门槛", entry, root.transform, wood);
            AddThreshold("出口门槛", exit, root.transform, wood);
            GameObject back = Panel("入口木门", entry, wood);
            back.SetActive(false);
            new GameObject("进入房间后关回头门").AddComponent<MapRoomGate>().Configure(RoomName(i), back, true);
            GameObject door = Panel("出口木门", exit, wood);
            if (i + 1 < RoomAssets.Length)
                new GameObject("后继加载保护门").AddComponent<MapRoomGate>().Configure(RoomName(i + 1), door);

            if (i > 0)
            {
                AddBridge(previousExit, entry, wood, previousRoot, root);
                var portalRoot = new GameObject("真实门后_进入房间");
                portalRoot.transform.SetPositionAndRotation(
                    entry.Floor - entry.Outward * (entry.Depth * .5f + 1f + TriggerDepth * .5f) + Vector3.up * entry.Height * .5f,
                    Quaternion.LookRotation(-entry.Outward, Vector3.up));
                var portal = portalRoot.AddComponent<MapRoomPortal>();
                portalRoot.GetComponent<BoxCollider>().size = new Vector3(entry.Width - .1f, entry.Height, TriggerDepth);
                portal.Configure(RoomName(i - 1), RoomName(i));
            }
            AddLabel(i + 1 < RoomAssets.Length ? "NEXT " + RoomName(i + 1) : "END", exit);

            BakeNavigation(root, RoomName(i));
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(.7f, .7f, .7f);
            if (!EditorSceneManager.SaveScene(roomScene, RoomPath(i))) throw new IOException("房间保存失败");
            previousExit = exit;
            previousRoot = root;
            Debug.Log($"TEAM_ROOM_CONNECTED {RoomName(i)} entry={entry.Floor} exit={exit.Floor} width={entry.Width:F2} depth={entry.Depth:F2}");
        }

        Scene bootstrap = EditorSceneManager.OpenScene(MapStreamingSetup.DemoPath, OpenSceneMode.Additive);
        SceneManager.SetActiveScene(bootstrap);
        PlayerMotor motor = bootstrap.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<PlayerMotor>(true)).Single();
        motor.transform.SetPositionAndRotation(firstEntry.Floor - firstEntry.Outward * (firstEntry.Depth * .5f + 3f) + Vector3.up * .15f,
            Quaternion.LookRotation(-firstEntry.Outward, Vector3.up));
        var startup = bootstrap.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<MapBootstrapper>(true)).Single();
        startup.Configure(RoomName(0), motor);
        var hud = startup.GetComponent<MapStreamingHUD>();
        hud.Configure("团队白盒 / 教程 → 01 → 02 → 03 → 04", "沿原有白盒移动，从黄色门洞进入下一房；门后房间会提前加载。");
        EditorUtility.SetDirty(startup);
        EditorUtility.SetDirty(hud);
        PrefabUtility.RecordPrefabInstancePropertyModifications(motor.transform);
        if (!EditorSceneManager.SaveScene(bootstrap, GamePath)) throw new IOException("游戏入口保存失败");

        var scenes = EditorBuildSettings.scenes.ToList();
        foreach (string path in new[] { GamePath }.Concat(Enumerable.Range(0, RoomAssets.Length).Select(RoomPath)))
        {
            int existingIndex = scenes.FindIndex(scene => scene.path == path);
            if (existingIndex >= 0) scenes[existingIndex] = new EditorBuildSettingsScene(path, true);
            else scenes.Add(new EditorBuildSettingsScene(path, true));
        }
        EditorBuildSettings.scenes = scenes.ToArray();
        Scene boot = EditorSceneManager.OpenScene("Assets/Scenes/Boot.unity");
        Bootloader loader = boot.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Bootloader>(true)).Single();
        var settings = new SerializedObject(loader);
        settings.FindProperty("_nextSceneName").stringValue = "WhiteboxGame";
        settings.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(loader);
        EditorSceneManager.SaveScene(boot);
        AssetDatabase.SaveAssets();
        Debug.Log("TEAM_GAME_SETUP_PASS: Boot → WhiteboxGame → 教程 → 01 → 02 → 03 → 04");
    }

    private struct DoorInfo
    {
        public Vector3 Floor;
        public Vector3 Outward;
        public float Width;
        public float Height;
        public float Depth;
    }

    private static DoorInfo ReadDoor(GameObject root, Transform frame)
    {
        Mesh mesh = frame.GetComponent<MeshFilter>().sharedMesh;
        if (mesh == null) throw new InvalidDataException(frame.name + " 缺少门洞网格");
        Bounds local = mesh.bounds;
        Vector2 horizontal = LargestInterval(mesh.vertices.Select(vertex => vertex.x));
        Vector2 vertical = LargestInterval(mesh.vertices.Select(vertex => vertex.y));
        Vector3 floor = frame.TransformPoint(new Vector3((horizontal.x + horizontal.y) * .5f, vertical.x, local.center.z));
        Vector3 outward = frame.TransformDirection(Vector3.forward).normalized;
        if (Vector3.Dot(outward, floor - BoundsOf(root).center) < 0f) outward = -outward;
        float depth = frame.TransformVector(Vector3.forward * local.size.z).magnitude;
        Vector3 probe = floor - outward * (depth * .5f + 1f) + Vector3.up * 3f;
        float nearest = float.PositiveInfinity;
        foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
        {
            if (!collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy) continue;
            // 新实例的无厚度 Plane 在编辑器当帧可能尚未进入物理查询树，直接按其本地平面投影。
            if (collider is MeshCollider plane && plane.sharedMesh != null &&
                plane.sharedMesh.bounds.size.y < .001f && Mathf.Abs(Vector3.Dot(plane.transform.up, Vector3.up)) > .999f)
            {
                Bounds localPlane = plane.sharedMesh.bounds;
                Vector3 point = plane.transform.InverseTransformPoint(probe);
                point.y = localPlane.center.y;
                if (localPlane.Contains(point))
                {
                    float height = plane.transform.TransformPoint(point).y;
                    float distance = probe.y - height;
                    if (distance >= 0f && distance <= 6f && distance < nearest)
                    {
                        nearest = distance;
                        floor.y = height;
                    }
                }
            }
            if (collider.Raycast(new Ray(probe, Vector3.down), out RaycastHit hit, 6f) && hit.distance < nearest)
            {
                nearest = hit.distance;
                floor.y = hit.point.y;
            }
        }
        return new DoorInfo
        {
            Floor = floor, Outward = outward, Depth = depth,
            Width = frame.TransformVector(Vector3.right * (horizontal.y - horizontal.x)).magnitude,
            Height = frame.TransformVector(Vector3.up * (vertical.y - vertical.x)).magnitude
        };
    }

    private static Vector2 LargestInterval(IEnumerable<float> source)
    {
        float[] values = source.Select(value => (float)Math.Round(value, 4)).Distinct().OrderBy(value => value).ToArray();
        if (values.Length < 2) throw new InvalidDataException("门洞顶点范围无效");
        Vector2 best = new Vector2(values[0], values[1]);
        for (int i = 1; i < values.Length; i++)
            if (values[i] - values[i - 1] > best.y - best.x) best = new Vector2(values[i - 1], values[i]);
        return best;
    }

    private static Transform Anchor(string name, Vector3 position, Vector3 forward, Transform parent)
    {
        var anchor = new GameObject(name).transform;
        anchor.SetParent(parent, true);
        anchor.SetPositionAndRotation(position, Quaternion.LookRotation(forward, Vector3.up));
        return anchor;
    }

    private static GameObject Box(string name, Vector3 center, Vector3 size, Vector3 forward, Material material)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.SetPositionAndRotation(center, Quaternion.LookRotation(forward, Vector3.up));
        box.transform.localScale = size;
        box.GetComponent<Renderer>().sharedMaterial = material;
        return box;
    }

    private static GameObject Panel(string name, DoorInfo info, Material material)
    {
        return Box(name, info.Floor + Vector3.up * info.Height * .5f,
            new Vector3(info.Width, info.Height, .2f), info.Outward, material);
    }

    private static void AddThreshold(string name, DoorInfo info, Transform parent, Material material)
    {
        GameObject floor = Box(name, info.Floor - Vector3.up * .15f,
            new Vector3(info.Width, .3f, info.Depth), info.Outward, material);
        floor.transform.SetParent(parent, true);
        UpdateThreshold(floor, info, parent.gameObject);
    }

    private static void UpdateThreshold(GameObject floor, DoorInfo info, GameObject root)
    {
        UpdateBox(floor, info.Floor - Vector3.up * .15f, new Vector3(info.Width, .3f, info.Depth), info.Outward);
        // 可见面止于门沿，碰撞仍向两侧各延伸一米，补住原白盒薄片地面的局部碰撞缺口。
        floor.GetComponent<BoxCollider>().size = new Vector3(1f, 1f, (info.Depth + ThresholdCollisionOverlap * 2f) / info.Depth);
        WhiteboxSeamMeshBuilder.TrimThreshold(floor, root, SceneFolder + "/SeamMeshes");
    }

    private static void ConnectSeamSurfaces(GameObject previousRoot, GameObject root, GameObject bridge)
    {
        GameObject previousExit = previousRoot.GetComponentsInChildren<Transform>(true)
            .Single(item => item.name == "出口门槛").gameObject;
        GameObject entry = root.GetComponentsInChildren<Transform>(true).Single(item => item.name == "入口门槛").gameObject;
        ConfigureSeam(previousExit, previousRoot, root);
        ConfigureSeam(entry, root, previousRoot);
        ConfigureSeam(bridge, root, previousRoot);
        TrimAuthoredOverlap(previousRoot, root);
    }

    private static void TrimAuthoredOverlap(GameObject owner, GameObject neighbor)
    {
        MeshFilter[] filters = owner.GetComponentsInChildren<MeshFilter>(true);
        for (int index = 0; index < filters.Length; index++)
        {
            MeshFilter filter = filters[index];
            if (filter.name == "入口门槛" || filter.name == "出口门槛" || filter.sharedMesh == null) continue;
            MapSeamSurface selector = filter.GetComponent<MapSeamSurface>();
            Mesh own = selector != null ? selector.OwnerOnlyMesh : filter.sharedMesh;
            Mesh combined = WhiteboxSeamMeshBuilder.TrimMesh(filter.gameObject, own, new[] { neighbor },
                SceneFolder + "/SeamMeshes", "_Authored_" + index + "_With_" + neighbor.scene.name, true);
            if (combined == own && selector == null) continue;
            if (selector == null) selector = filter.gameObject.AddComponent<MapSeamSurface>();
            selector.Configure(neighbor.scene.name, own, combined);
            EditorUtility.SetDirty(selector);
        }
    }

    private static void ConfigureSeam(GameObject surface, GameObject owner, GameObject neighbor)
    {
        string folder = SceneFolder + "/SeamMeshes";
        Mesh own = WhiteboxSeamMeshBuilder.TrimBox(surface, new[] { owner }, folder);
        Mesh combined = WhiteboxSeamMeshBuilder.TrimBox(surface, new[] { owner, neighbor }, folder, "_With_" + neighbor.scene.name);
        MapSeamSurface selector = surface.GetComponent<MapSeamSurface>();
        if (selector == null) selector = surface.AddComponent<MapSeamSurface>();
        selector.Configure(neighbor.scene.name, own, combined);
        EditorUtility.SetDirty(selector);
    }

    private static void UpdateBox(GameObject box, Vector3 center, Vector3 size, Vector3 forward)
    {
        box.transform.SetPositionAndRotation(center, Quaternion.LookRotation(forward, Vector3.up));
        box.transform.localScale = size;
    }

    private static void BridgeShape(DoorInfo source, DoorInfo target, out Vector3 center, out float width, out float length)
    {
        Vector3 start = source.Floor + source.Outward * source.Depth * .5f;
        Vector3 end = target.Floor + target.Outward * target.Depth * .5f;
        // 长门洞两边深度不同，必须用门洞外沿，不能用两扇门中心的中点。
        center = (start + end) * .5f;
        width = Mathf.Min(source.Width, target.Width);
        length = Vector3.Dot(end - start, source.Outward);
        if (length <= 0f) throw new InvalidDataException("两门之间没有正向连接间隙");
    }

    private static void AddBridge(DoorInfo source, DoorInfo target, Material material, GameObject previousRoot, GameObject root)
    {
        BridgeShape(source, target, out Vector3 center, out float width, out float length);
        GameObject floor = Box("两门间连接地板", center - Vector3.up * .15f, new Vector3(width, .3f, length), source.Outward, material);
        ConnectSeamSurfaces(previousRoot, root, floor);
        EditorSceneManager.SaveScene(previousRoot.scene);
        Vector3 side = Vector3.Cross(Vector3.up, source.Outward) * (width * .5f + .1f);
        Box("连接段左侧墙", center + side + Vector3.up * 2f, new Vector3(.2f, 4f, length), source.Outward, material);
        Box("连接段右侧墙", center - side + Vector3.up * 2f, new Vector3(.2f, 4f, length), source.Outward, material);
    }

    private static void BakeNavigation(GameObject root, string roomName)
    {
        var surface = root.GetComponent<NavMeshSurface>();
        if (surface == null) surface = root.AddComponent<NavMeshSurface>();
        foreach (NavMeshSurface child in root.GetComponentsInChildren<NavMeshSurface>(true))
            if (child != surface) UnityEngine.Object.DestroyImmediate(child);
        surface.collectObjects = CollectObjects.Children;
        surface.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.PhysicsColliders;
        // 门板加载后会打开，只收集房间固有几何和门槛，不把临时木门烘焙成永久阻挡。
        surface.BuildNavMesh();
        if (surface.navMeshData == null) return;
        string navPath = SceneFolder + "/NavMesh_" + roomName + ".asset";
        UnityEngine.AI.NavMeshData existing = AssetDatabase.LoadAssetAtPath<UnityEngine.AI.NavMeshData>(navPath);
        if (existing == null) AssetDatabase.CreateAsset(surface.navMeshData, navPath);
        else
        {
            surface.RemoveData();
            EditorUtility.CopySerialized(surface.navMeshData, existing);
            surface.navMeshData = existing;
            surface.AddData();
        }
    }

    private static void AddLabel(string text, DoorInfo exit)
    {
        var label = new GameObject(text).AddComponent<TextMesh>();
        label.text = text;
        label.fontSize = 60;
        label.characterSize = .08f;
        label.anchor = TextAnchor.MiddleCenter;
        label.color = Color.yellow;
        label.transform.SetPositionAndRotation(exit.Floor + Vector3.up * (exit.Height + .5f), Quaternion.LookRotation(exit.Outward, Vector3.up));
    }

    [MenuItem("超高速行者/地图无缝加载/导入并检查团队白盒门位")]
    public static void Inspect()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("请先停止播放");
        ImportAssets();
        foreach (string name in RoomAssets)
        {
            string path = FindPrefab(name);
            WhiteboxMeshRecovery.RepairPrefab(path, ImportFolder + "/RecoveredMeshes");
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Bounds bounds = BoundsOf(root);
                Debug.Log($"TEAM_ROOM {name} position={root.transform.position} min={bounds.min} max={bounds.max} agents={root.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true).Length}");
                foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                {
                    if (!child.name.StartsWith("Door", StringComparison.Ordinal)) continue;
                    Bounds door = BoundsOf(child.gameObject);
                    Debug.Log($"TEAM_DOOR {name}/{child.name} position={child.position} center={door.center} size={door.size} rotation={child.eulerAngles}");
                }
                foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
                {
                    Bounds floor = collider.bounds;
                    if (floor.size.y < 3f && floor.size.x > 10f && floor.size.z > 10f)
                        Debug.Log($"TEAM_FLOOR {name}/{collider.name} center={floor.center} size={floor.size}");
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }

    private static string FindPrefab(string name)
    {
        string[] matches = AssetDatabase.FindAssets(name + " t:Prefab");
        foreach (string guid in matches)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == name && path.StartsWith(ImportFolder + "/", StringComparison.Ordinal))
                return path;
        }
        throw new FileNotFoundException("未导入团队房间预制体：" + name);
    }

    private static void ImportAssets()
    {
        string absoluteRoot = Path.GetFullPath(ImportFolder) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(absoluteRoot);
        int added = 0;
        using (ZipArchive archive = ZipFile.OpenRead(ArchivePath))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string relative = entry.FullName;
                const string prefix = "LevelTest_Cdwy/";
                if (!relative.StartsWith(prefix, StringComparison.Ordinal) || relative.EndsWith("/", StringComparison.Ordinal) ||
                    relative.EndsWith(".meta", StringComparison.Ordinal)) continue;
                relative = relative.Substring(prefix.Length);
                string extension = Path.GetExtension(relative).ToLowerInvariant();
                if (extension != ".prefab" && extension != ".mat" && extension != ".png" && extension != ".asset") continue;
                ZipArchiveEntry meta = archive.GetEntry(entry.FullName + ".meta");
                if (meta == null) throw new InvalidDataException("资源缺少 meta：" + relative);
                string metaText;
                using (var reader = new StreamReader(meta.Open())) metaText = reader.ReadToEnd();
                string guid = Regex.Match(metaText, @"(?m)^guid: ([0-9a-f]{32})").Groups[1].Value;
                if (string.IsNullOrEmpty(guid)) throw new InvalidDataException("资源缺少 GUID：" + relative);
                // 已有 GUID 使用工程原资产，避免解压产生重复 GUID 或覆盖同学已更新的资源。
                string existing = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(existing)) continue;
                string destination = Path.GetFullPath(Path.Combine(absoluteRoot, relative));
                if (!destination.StartsWith(absoluteRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("压缩包路径越界：" + relative);
                if (File.Exists(destination)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                entry.ExtractToFile(destination);
                File.WriteAllText(destination + ".meta", metaText);
                added++;
            }
        }
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Debug.Log("TEAM_IMPORT: 新增团队资源 " + added + " 个");
    }

    private static Bounds BoundsOf(GameObject root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) throw new InvalidDataException(root.name + " 缺少可用网格");
        Bounds bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
        return bounds;
    }
}
