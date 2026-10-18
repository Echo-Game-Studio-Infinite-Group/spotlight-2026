using System;
using System.IO;
using System.Linq;
using GameJam.Flow;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GameJam.FlowEditor
{
    public static class FlowExampleSetup
    {
        public const string Folder = "Assets/Settings/FlowExamples";
        public const string ConfigPath = Folder + "/RunFlow.asset";
        public const string DemoPath = "Assets/Scenes/FlowGame.unity";

        [MenuItem("超高速行者/流程与地图/生成白盒配表示例")]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出播放模式");
            EnsureFolder(Folder); EnsureFolder(Folder + "/Templates"); EnsureFolder(Folder + "/Navigation");
            Material material = AssetDatabase.LoadAssetAtPath<Material>("Assets/Scenes/MapStreaming/Door.mat");
            RoomDefinition tutorial = BuildRoom("tutorial", "Whitebox_Tutorial", material, false);
            RoomDefinition[] ordinary = Enumerable.Range(1, 4).Select(index => BuildRoom("room0" + index, "Whitebox_Room_0" + index, material, index == 3)).ToArray();
            RoomDefinition final = Room("final", ordinary[3].Template);
            if (final.Variants.Length == 0)
            {
                final.Variants = new[] { Variant(final, "final.default", 0, 100, 0, "终点：到达出口完成通关") };
                EditorUtility.SetDirty(final);
            }
            RoomPoolDefinition neutral = Pool("neutral", false, ordinary.Where((room, index) => index != 2).SelectMany(room => room.Variants).ToArray());
            RoomPoolDefinition chaos = Pool("chaos", true, ordinary[2].Variants);
            LoopRuleDefinition low = Loop("low", 0, 30, neutral, chaos);
            LoopRuleDefinition high = Loop("high", 80, 99, neutral, chaos);
            RunFlowConfig config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>(ConfigPath);
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<RunFlowConfig>();
                config.InitialRoom = tutorial.Variants[0]; config.FinalRoom = final.Variants[0];
                config.NormalPools = new[] { neutral, chaos }; config.Loops = new[] { low, high }; config.ConnectionMaterial = material;
                AssetDatabase.CreateAsset(config, ConfigPath);
            }
            AssetDatabase.SaveAssets();
            var errors = FlowConfigValidator.Validate(config);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
            if (!File.Exists(DemoPath)) CreateDemo(config);
            Selection.activeObject = config;
            Debug.Log("[流程] 白盒样例已生成。使用“打开流程白盒”运行；再次生成会保留已有配表。场景未改动 Build Settings。");
        }

        [MenuItem("超高速行者/流程与地图/打开流程白盒")]
        public static void Open()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            { EditorApplication.isPlaying = false; EditorApplication.delayCall += Open; return; }
            if (!File.Exists(DemoPath)) Build();
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(DemoPath);
        }

        private static RoomDefinition BuildRoom(string id, string source, Material material, bool changesChaos)
        {
            string templatePath = Folder + "/Templates/" + id + ".prefab";
            FlowRoomTemplate template = AssetDatabase.LoadAssetAtPath<FlowRoomTemplate>(templatePath);
            if (template == null) template = ExportTemplate(id, source, templatePath, material);
            RoomDefinition room = Room(id, template);
            if (room.Variants.Length == 0)
            {
                room.Variants = id == "tutorial"
                    ? new[] { Variant(room, id + ".default", 0, 100, 0, "跑酷穿过房间，击碎发光稳定装置提升混乱度") }
                    : new[] {
                        Variant(room, id + ".low", 0, 30, changesChaos ? 10 : 0, "稳定区 / " + id),
                        Variant(room, id + ".middle", 31, 79, changesChaos ? 15 : 0, "变化区 / " + id),
                        Variant(room, id + ".high", 80, 100, changesChaos ? 10 : 0, "高混乱区 / " + id) };
                EditorUtility.SetDirty(room);
            }
            return room;
        }

        private static RoomDefinition Room(string id, FlowRoomTemplate template)
        {
            string path = Folder + "/" + id + ".asset";
            RoomDefinition room = AssetDatabase.LoadAssetAtPath<RoomDefinition>(path);
            if (room != null) return room;
            room = ScriptableObject.CreateInstance<RoomDefinition>(); room.Id = id; room.DisplayName = RoomName(id); room.Template = template;
            AssetDatabase.CreateAsset(room, path); return room;
        }
        private static string RoomName(string id) => id == "tutorial" ? "教程箱庭" : id == "final" ? "最终箱庭" :
            id.StartsWith("room", StringComparison.Ordinal) ? "示例箱庭" + id.Substring("room".Length) : "箱庭 " + id;
        private static RoomVariantDefinition Variant(RoomDefinition room, string id, int min, int max, int gain, string narrative)
        {
            string path = Folder + "/" + id + ".asset";
            RoomVariantDefinition variant = AssetDatabase.LoadAssetAtPath<RoomVariantDefinition>(path);
            if (variant != null) return variant;
            variant = ScriptableObject.CreateInstance<RoomVariantDefinition>();
            variant.Id = id; variant.Room = room; variant.MinimumChaos = min; variant.MaximumChaos = max;
            variant.StabilizerGain = gain; variant.ContentGroups = gain > 0 ? new[] { "stability" } : new string[0];
            variant.Narrative = narrative; variant.OverrideLightColor = true;
            variant.LightColor = min >= 80 ? new Color(1f, .25f, .18f) : min >= 31 ? new Color(1f, .7f, .25f) : new Color(.5f, .8f, 1f);
            AssetDatabase.CreateAsset(variant, path); return variant;
        }
        private static RoomPoolDefinition Pool(string id, bool changes, RoomVariantDefinition[] variants)
        {
            string path = Folder + "/Pool_" + id + ".asset";
            RoomPoolDefinition pool = AssetDatabase.LoadAssetAtPath<RoomPoolDefinition>(path);
            if (pool != null) return pool;
            pool = ScriptableObject.CreateInstance<RoomPoolDefinition>(); pool.Id = id; pool.ChangesChaos = changes;
            pool.Entries = variants.Select(variant => new RoomPoolEntry { Variant = variant }).ToArray();
            AssetDatabase.CreateAsset(pool, path); return pool;
        }
        private static LoopRuleDefinition Loop(string id, int min, int max, RoomPoolDefinition neutral, RoomPoolDefinition chaos)
        {
            string path = Folder + "/Loop_" + id + ".asset";
            LoopRuleDefinition rule = AssetDatabase.LoadAssetAtPath<LoopRuleDefinition>(path);
            if (rule != null) return rule;
            rule = ScriptableObject.CreateInstance<LoopRuleDefinition>(); rule.Id = id; rule.MinimumChaos = min; rule.MaximumChaos = max;
            rule.NeutralPool = neutral; rule.ChaosPool = chaos; AssetDatabase.CreateAsset(rule, path); return rule;
        }

        private static FlowRoomTemplate ExportTemplate(string id, string sourceName, string path, Material material)
        {
            string sourcePath = "Assets/Scenes/WhiteboxStreaming/" + sourceName + ".unity";
            Scene source = SceneManager.GetSceneByPath(sourcePath);
            bool opened = !source.IsValid() || !source.isLoaded;
            if (opened) source = EditorSceneManager.OpenScene(sourcePath, OpenSceneMode.Additive);
            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                MapRoom marker = source.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<MapRoom>(true)).Single();
                GameObject geometry = source.GetRootGameObjects().Single(root => root.name.StartsWith("BOX_S", StringComparison.Ordinal));
                Quaternion rotation = Quaternion.Inverse(marker.EntryPoint.rotation);
                Vector3 origin = marker.EntryPoint.position;
                var wrapper = new GameObject(id); SceneManager.MoveGameObjectToScene(wrapper, preview);
                GameObject copy = Object.Instantiate(geometry);
                SceneManager.MoveGameObjectToScene(copy, preview);
                copy.transform.SetParent(wrapper.transform, true);
                copy.transform.SetPositionAndRotation(rotation * (geometry.transform.position - origin), rotation * geometry.transform.rotation);
                foreach (Transform child in copy.GetComponentsInChildren<Transform>(true)) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(child.gameObject);
                foreach (MapSeamSurface seam in copy.GetComponentsInChildren<MapSeamSurface>(true))
                { seam.GetComponent<MeshFilter>().sharedMesh = seam.OwnerOnlyMesh; Object.DestroyImmediate(seam); }
                foreach (NavMeshSurface old in copy.GetComponentsInChildren<NavMeshSurface>(true)) Object.DestroyImmediate(old);
                var template = wrapper.AddComponent<FlowRoomTemplate>();
                template.Entry = Anchor(wrapper.transform, "Entry", Vector3.zero, Quaternion.identity);
                template.Exit = Anchor(wrapper.transform, "Exit", rotation * (marker.ExitPoint.position - origin), rotation * marker.ExitPoint.rotation);
                template.FallHeight = marker.FallDeathY - origin.y;
                // 门型由已有真实门洞尺寸导出，不依赖 Door (n) 名称在运行时查找。
                MapRoomGate[] gates = source.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<MapRoomGate>(true)).ToArray();
                Vector2 entrySize = ReadGateSize(gates, true), exitSize = ReadGateSize(gates, false);
                template.EntrySize = entrySize; template.ExitSize = exitSize;
                template.EntryDoor = Door(wrapper.transform, "入口保护门", template.Entry, entrySize, material);
                template.ExitDoor = Door(wrapper.transform, "出口保护门", template.Exit, exitSize, material);
                template.EntryDoor.SetActive(false); template.ExitDoor.SetActive(false);
                FlowCheckpoint checkpoint = Anchor(wrapper.transform, "入口记录点", new Vector3(0, .15f, 4), Quaternion.identity).gameObject.AddComponent<FlowCheckpoint>();
                checkpoint.Id = "entry";
                GameObject statue = Primitive(checkpoint.transform, "记录点雕像", PrimitiveType.Cylinder, new Vector3(-entrySize.x * .35f, .6f, 0), new Vector3(.3f, 1.2f, .3f), material);
                Object.DestroyImmediate(statue.GetComponent<Collider>());
                AddDevice(wrapper.transform, material);
                TextMesh text = Anchor(wrapper.transform, "叙事提示", new Vector3(0, 3, 6), Quaternion.identity).gameObject.AddComponent<TextMesh>();
                text.anchor = TextAnchor.MiddleCenter; text.characterSize = .06f; text.fontSize = 60;
                template.Narratives = new[] { text };
                Renderer[] renderers = copy.GetComponentsInChildren<Renderer>(true);
                Bounds bounds = renderers[0].bounds;
                foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
                template.GeometryBounds = bounds;
                var surface = wrapper.AddComponent<NavMeshSurface>();
                surface.collectObjects = CollectObjects.Children; surface.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.PhysicsColliders;
                surface.BuildNavMesh();
                if (surface.navMeshData != null)
                {
                    template.Navigation = surface.navMeshData;
                    surface.RemoveData();
                    AssetDatabase.CreateAsset(template.Navigation, Folder + "/Navigation/" + id + ".asset");
                }
                Object.DestroyImmediate(surface);
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(wrapper, path);
                return saved.GetComponent<FlowRoomTemplate>();
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
                if (opened) EditorSceneManager.CloseScene(source, true);
            }
        }

        private static Vector2 ReadGateSize(MapRoomGate[] gates, bool entry)
        {
            foreach (MapRoomGate gate in gates)
            {
                var data = new SerializedObject(gate);
                if (data.FindProperty("_closeAfterEntering").boolValue != entry) continue;
                var door = (GameObject)data.FindProperty("_door").objectReferenceValue;
                if (door != null) return new Vector2(door.transform.localScale.x, door.transform.localScale.y);
            }
            return new Vector2(6f, 5f);
        }
        private static void AddDevice(Transform parent, Material material)
        {
            var group = new GameObject("稳定装置内容组"); group.transform.SetParent(parent, false);
            var content = group.AddComponent<FlowContentGroup>(); content.Id = "stability"; content.GameplayOnly = true;
            var device = new GameObject("稳定装置"); device.transform.SetParent(group.transform, false); device.transform.localPosition = new Vector3(1.5f, 1.3f, 6);
            var stability = device.AddComponent<StabilityDevice>();
            stability.Visual = Primitive(device.transform, "可击碎灯", PrimitiveType.Sphere, Vector3.zero, Vector3.one * .6f, material);
            var light = device.AddComponent<Light>(); light.type = LightType.Point; light.range = 8; light.intensity = 2;
        }
        private static GameObject Door(Transform parent, string name, Transform socket, Vector2 size, Material material)
        {
            GameObject door = Primitive(parent, name, PrimitiveType.Cube, socket.localPosition + Vector3.up * size.y * .5f, new Vector3(size.x, size.y, .2f), material);
            door.transform.localRotation = socket.localRotation; return door;
        }
        private static Transform Anchor(Transform parent, string name, Vector3 position, Quaternion rotation)
        {
            var value = new GameObject(name).transform; value.SetParent(parent, false); value.localPosition = position; value.localRotation = rotation; return value;
        }
        private static GameObject Primitive(Transform parent, string name, PrimitiveType type, Vector3 position, Vector3 size, Material material)
        {
            GameObject value = GameObject.CreatePrimitive(type); value.name = name; value.transform.SetParent(parent, false);
            value.transform.localPosition = position; value.transform.localScale = size;
            if (material != null) value.GetComponent<Renderer>().sharedMaterial = material;
            return value;
        }
        private static void CreateDemo(RunFlowConfig config)
        {
            File.Copy("Assets/Scenes/WhiteboxStreaming/WhiteboxGame.unity", DemoPath);
            AssetDatabase.ImportAsset(DemoPath);
            Scene scene = EditorSceneManager.OpenScene(DemoPath, OpenSceneMode.Additive);
            try
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (MapBootstrapper old in root.GetComponentsInChildren<MapBootstrapper>(true)) Object.DestroyImmediate(old);
                    foreach (MapStreamingHUD old in root.GetComponentsInChildren<MapStreamingHUD>(true)) Object.DestroyImmediate(old);
                }
                var startup = new GameObject("流程与地图"); SceneManager.MoveGameObjectToScene(startup, scene);
                var flow = startup.AddComponent<RunFlowController>();
                flow.Configure(config, scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<PlayerMotor>(true)).Single());
                startup.AddComponent<FlowHUD>().Configure(flow, true);
                EditorUtility.SetDirty(flow); EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            AssetDatabase.CreateFolder(Path.GetDirectoryName(path).Replace('\\', '/'), Path.GetFileName(path));
        }
    }
}
