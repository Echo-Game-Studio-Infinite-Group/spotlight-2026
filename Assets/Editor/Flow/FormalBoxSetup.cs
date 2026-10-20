using System;
using System.IO;
using System.Linq;
using GameJam.Flow;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GameJam.FlowEditor
{
    public static class FormalBoxSetup
    {
        private const string Folder = "Assets/Settings/FormalFlow";
        private const string ConfigPath = Folder + "/RunFlow.asset";

        [MenuItem("超高速行者/流程与地图/接入七个正式箱庭")]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出播放模式");
            RunFlowConfig config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>(ConfigPath);
            if (config == null || config.InitialRoom == null) throw new InvalidOperationException("请先配置 BOX_TR 教学流程");
            EnsureFolder(Folder + "/Templates"); EnsureFolder(Folder + "/Navigation");
            RoomDefinition[] rooms = Enumerable.Range(1, 7).Select(i => BuildRoom(i, config.ConnectionMaterial)).ToArray();
            RoomPoolDefinition neutral = Pool("neutral", false, rooms.Where((room, index) => index != 2).SelectMany(room => room.Variants).ToArray());
            RoomPoolDefinition chaos = Pool("chaos", true, rooms[2].Variants);
            config.NormalPools = new[] { neutral, chaos };
            config.Loops = new[] { Loop("low", 0, 30, neutral, chaos), Loop("high", 80, 99, neutral, chaos) };
            config.FinalRoom = FinalRoom(rooms[6].Template);
            config.ContinueOnlyRooms = new RoomVariantDefinition[0];
            config.AlwaysStartAtInitialRoom = true;
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            var errors = FlowConfigValidator.Validate(config);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
            Debug.Log("[流程] BOX_S_001～007 已接入正式流程，BOX_TR 仍为新游戏入口；已有模板与差分编辑不会被重建。");
        }

        private static RoomDefinition BuildRoom(int index, Material material)
        {
            string name = "BOX_S_" + index.ToString("000");
            string path = Folder + "/Templates/" + name + ".prefab";
            FlowRoomTemplate template = AssetDatabase.LoadAssetAtPath<FlowRoomTemplate>(path);
            if (template == null) template = ExportTemplate(name, path, material, index == 3);
            RoomDefinition room = AssetDatabase.LoadAssetAtPath<RoomDefinition>(Folder + "/" + name + ".asset");
            if (room == null)
            {
                room = ScriptableObject.CreateInstance<RoomDefinition>();
                room.Id = name.ToLowerInvariant(); room.DisplayName = name; room.Template = template;
                AssetDatabase.CreateAsset(room, Folder + "/" + name + ".asset");
            }
            if (room.Variants.Length == 0)
            {
                var variant = ScriptableObject.CreateInstance<RoomVariantDefinition>();
                variant.Id = room.Id + ".default"; variant.Room = room;
                variant.StabilizerGain = index == 3 ? 15 : 0;
                variant.ContentGroups = index == 3 ? new[] { "stability" } : new string[0];
                AssetDatabase.CreateAsset(variant, Folder + "/" + name + ".default.asset");
                room.Variants = new[] { variant }; EditorUtility.SetDirty(room);
            }
            return room;
        }

        private static RoomVariantDefinition FinalRoom(FlowRoomTemplate template)
        {
            const string roomPath = Folder + "/Final.asset";
            RoomDefinition room = AssetDatabase.LoadAssetAtPath<RoomDefinition>(roomPath);
            if (room == null)
            {
                room = ScriptableObject.CreateInstance<RoomDefinition>();
                room.Id = "box_s_007_final"; room.DisplayName = "终点 BOX_S_007"; room.Template = template;
                AssetDatabase.CreateAsset(room, roomPath);
            }
            if (room.Variants.Length == 0)
            {
                // 同一张美术地图的普通房与终点必须使用不同差分，避免随机抽到 007 就提前结束流程。
                var variant = ScriptableObject.CreateInstance<RoomVariantDefinition>();
                variant.Id = room.Id + ".default"; variant.Room = room;
                AssetDatabase.CreateAsset(variant, Folder + "/Final.default.asset");
                room.Variants = new[] { variant }; EditorUtility.SetDirty(room);
            }
            return room.Variants[0];
        }

        private static FlowRoomTemplate ExportTemplate(string name, string path, Material material, bool changesChaos)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/" + name + ".prefab");
            if (source == null) throw new InvalidOperationException("找不到源预制体：" + name);
            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var wrapper = new GameObject(name);
                SceneManager.MoveGameObjectToScene(wrapper, preview);
                var geometry = (GameObject)PrefabUtility.InstantiatePrefab(source, preview);
                geometry.transform.SetParent(wrapper.transform, true);
                Physics.SyncTransforms();
                // 这些美术预制体的门口由上下两块 Torch 网格组成；只在导出时识别，运行时使用明确锚点。
                Collider[] platforms = geometry.GetComponentsInChildren<Collider>(true)
                    .Where(c => c.name.StartsWith("Torch 01", StringComparison.Ordinal) && c.enabled &&
                        c.bounds.size.y < 2 && c.bounds.size.x > 10 && c.bounds.size.z > 10)
                    .OrderBy(c => c.bounds.center.z).ToArray();
                if (platforms.Length != 4) throw new InvalidOperationException(name + "：需要人工确认两个门口平台");
                Collider entryFloor = platforms.Take(2).OrderBy(c => c.bounds.center.y).First();
                Collider exitFloor = platforms.Skip(2).OrderBy(c => c.bounds.center.y).First();
                Collider entryCeiling = platforms.Take(2).OrderByDescending(c => c.bounds.center.y).First();
                Collider exitCeiling = platforms.Skip(2).OrderByDescending(c => c.bounds.center.y).First();
                Vector3 entryCenter = FloorPoint(entryFloor), exitCenter = FloorPoint(exitFloor);
                Vector3 origin = entryCenter; origin.z = entryFloor.bounds.min.z;
                // 002、006 的出口位于侧面；平台的 Z 顺序仍决定入口/出口，门朝向须沿真实通道。
                bool sideExit = name == "BOX_S_002" || name == "BOX_S_006";
                Vector3 exit = exitCenter;
                if (sideExit) exit.x = exitFloor.bounds.max.x; else exit.z = exitFloor.bounds.max.z;
                Vector2 entrySize = new Vector2(16, Mathf.Min(7.5f, entryCeiling.bounds.min.y - entryCenter.y - .1f));
                Vector2 exitSize = new Vector2(16, Mathf.Min(7.5f, exitCeiling.bounds.min.y - exitCenter.y - .1f));
                geometry.transform.position -= origin;
                Physics.SyncTransforms();
                var template = wrapper.AddComponent<FlowRoomTemplate>();
                template.Entry = Anchor(wrapper.transform, "Entry", Vector3.zero);
                template.Exit = Anchor(wrapper.transform, "Exit", exit - origin);
                template.Exit.localRotation = sideExit ? Quaternion.Euler(0, 90, 0) : Quaternion.identity;
                template.EntrySize = entrySize; template.ExitSize = exitSize;
                template.EntryDoor = Door(wrapper.transform, "入口保护门", template.Entry, entrySize, material);
                template.ExitDoor = Door(wrapper.transform, "出口保护门", template.Exit, exitSize, material);
                var checkpoint = Anchor(wrapper.transform, "入口出生记录点", entryCenter - origin + Vector3.up * .15f).gameObject.AddComponent<FlowCheckpoint>();
                checkpoint.Id = "entry";
                if (changesChaos) AddDevice(wrapper.transform, exitCenter - origin, material);
                Renderer[] renderers = geometry.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled && r.bounds.size.sqrMagnitude > 0).ToArray();
                Bounds bounds = renderers[0].bounds;
                foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
                template.GeometryBounds = bounds;
                // 裂隙恢复应在跌离可玩平台后触发，不能等到高耸装饰的最低包围盒。
                template.FallHeight = Mathf.Min(entryCenter.y, exitCenter.y) - origin.y - 6;
                var surface = wrapper.AddComponent<NavMeshSurface>();
                surface.collectObjects = CollectObjects.Children; surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
                string navigationPath = Folder + "/Navigation/" + name + ".asset";
                NavMeshData navigation = AssetDatabase.LoadAssetAtPath<NavMeshData>(navigationPath);
                var sources = new System.Collections.Generic.List<NavMeshBuildSource>();
                UnityEditor.AI.NavMeshBuilder.CollectSourcesInStage(wrapper.transform, surface.layerMask,
                    NavMeshCollectGeometry.PhysicsColliders, surface.defaultArea, false,
                    new System.Collections.Generic.List<NavMeshBuildMarkup>(), false, preview, sources);
                if (sources.Count == 0) throw new InvalidOperationException(name + "：预览场景中没有导航烘焙源");
                bool created = navigation == null;
                if (created) navigation = new NavMeshData(surface.agentTypeID);
                // 同步更新原生瓦片；保留资产引用，也避免阻塞编辑器等待依赖主线程完成的异步操作。
                if (!NavMeshBuilder.UpdateNavMeshData(navigation, surface.GetBuildSettings(), sources, bounds))
                    throw new InvalidOperationException(name + "：导航烘焙失败");
                if (created) AssetDatabase.CreateAsset(navigation, navigationPath);
                else EditorUtility.SetDirty(navigation);
                template.Navigation = navigation;
                Object.DestroyImmediate(surface);
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(wrapper, path);
                return saved.GetComponent<FlowRoomTemplate>();
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }

        private static Vector3 FloorPoint(Collider floor)
        {
            Bounds bounds = floor.bounds;
            var ray = new Ray(new Vector3(bounds.center.x, bounds.max.y + 1, bounds.center.z), Vector3.down);
            if (!floor.Raycast(ray, out RaycastHit hit, 3) || hit.normal.y < .9f)
                throw new InvalidOperationException(floor.name + "：平台中央没有朝上的碰撞面");
            return hit.point;
        }

        private static void AddDevice(Transform parent, Vector3 exitCenter, Material material)
        {
            var group = Anchor(parent, "稳定装置内容组", Vector3.zero).gameObject.AddComponent<FlowContentGroup>();
            group.Id = "stability"; group.GameplayOnly = true;
            var device = Anchor(group.transform, "稳定装置", exitCenter + new Vector3(3, 1.2f, 0)).gameObject.AddComponent<StabilityDevice>();
            device.Visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            device.Visual.name = "可击碎灯"; device.Visual.transform.SetParent(device.transform, false);
            device.Visual.transform.localScale = Vector3.one * .8f;
            device.Visual.GetComponent<Renderer>().sharedMaterial = material;
            var light = device.gameObject.AddComponent<Light>(); light.type = LightType.Point; light.range = 8; light.intensity = 2;
        }

        private static Transform Anchor(Transform parent, string name, Vector3 position)
        {
            var anchor = new GameObject(name).transform; anchor.SetParent(parent, false); anchor.localPosition = position; return anchor;
        }

        private static GameObject Door(Transform parent, string name, Transform anchor, Vector2 size, Material material)
        {
            GameObject door = GameObject.CreatePrimitive(PrimitiveType.Cube);
            door.name = name; door.transform.SetParent(parent, false);
            door.transform.localPosition = anchor.localPosition + Vector3.up * size.y * .5f;
            door.transform.localRotation = anchor.localRotation;
            door.transform.localScale = new Vector3(size.x, size.y, .2f);
            door.GetComponent<Renderer>().sharedMaterial = material; door.SetActive(false); return door;
        }

        private static RoomPoolDefinition Pool(string id, bool changesChaos, RoomVariantDefinition[] variants)
        {
            string path = Folder + "/Pool_" + id + ".asset";
            RoomPoolDefinition pool = AssetDatabase.LoadAssetAtPath<RoomPoolDefinition>(path);
            if (pool == null) { pool = ScriptableObject.CreateInstance<RoomPoolDefinition>(); AssetDatabase.CreateAsset(pool, path); }
            pool.Id = "formal_" + id; pool.ChangesChaos = changesChaos;
            pool.Entries = variants.Select(variant => new RoomPoolEntry { Variant = variant }).ToArray();
            EditorUtility.SetDirty(pool); return pool;
        }

        private static LoopRuleDefinition Loop(string id, int min, int max, RoomPoolDefinition neutral, RoomPoolDefinition chaos)
        {
            string path = Folder + "/Loop_" + id + ".asset";
            LoopRuleDefinition rule = AssetDatabase.LoadAssetAtPath<LoopRuleDefinition>(path);
            if (rule == null) { rule = ScriptableObject.CreateInstance<LoopRuleDefinition>(); AssetDatabase.CreateAsset(rule, path); }
            rule.Id = "formal_" + id; rule.MinimumChaos = min; rule.MaximumChaos = max;
            rule.NeutralPool = neutral; rule.ChaosPool = chaos; EditorUtility.SetDirty(rule); return rule;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/'); EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
