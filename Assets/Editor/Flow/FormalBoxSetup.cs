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

        [MenuItem("超高速行者/流程与地图/配置正式箱庭混乱度差分")]
        public static void BuildChaosVariants() => Build();

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
            Debug.Log("[流程] BOX_S_001～007 已配置低、中、高三档混乱度差分；BOX_TR 与终点保持独立。已有三档差分内容不会被覆盖。");
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
            EnsureChaosVariants(room, index);
            return room;
        }

        private static void EnsureChaosVariants(RoomDefinition room, int index)
        {
            if (room.Variants.Length != 1 || room.Variants[0].Id != room.Id + ".default") return;
            EnsureNarrative(room.Template);
            // 低档沿用已发布的 default 资产和编号，旧存档仍能识别所在箱庭。
            RoomVariantDefinition low = room.Variants[0];
            ConfigureVariant(low, 0, 30, new Color(.5f, .8f, 1f), index == 3 ? 10 : 0, Narrative(index, 0));
            RoomVariantDefinition middle = ChaosVariant(room, "middle", 31, 79, new Color(1f, .7f, .25f), index == 3 ? 15 : 0, Narrative(index, 1));
            RoomVariantDefinition high = ChaosVariant(room, "high", 80, 100, new Color(1f, .25f, .18f), index == 3 ? 10 : 0, Narrative(index, 2));
            room.Variants = new[] { low, middle, high };
            EditorUtility.SetDirty(room);
        }

        private static RoomVariantDefinition ChaosVariant(RoomDefinition room, string suffix, int min, int max, Color color, int gain, string narrative)
        {
            string path = Folder + "/" + room.DisplayName + "." + suffix + ".asset";
            RoomVariantDefinition variant = AssetDatabase.LoadAssetAtPath<RoomVariantDefinition>(path);
            if (variant != null) return variant;
            variant = ScriptableObject.CreateInstance<RoomVariantDefinition>();
            variant.Id = room.Id + "." + suffix; variant.Room = room;
            ConfigureVariant(variant, min, max, color, gain, narrative);
            AssetDatabase.CreateAsset(variant, path); return variant;
        }

        internal static void ConfigureVariant(RoomVariantDefinition variant, int min, int max, Color color, int gain, string narrative)
        {
            variant.MinimumChaos = min; variant.MaximumChaos = max;
            variant.OverrideLightColor = true; variant.LightColor = color;
            variant.StabilizerGain = gain; variant.Narrative = narrative;
            variant.ContentGroups = variant.ContentGroups.Union(gain > 0 ? new[] { "narrative", "stability" } : new[] { "narrative" }).ToArray();
            EditorUtility.SetDirty(variant);
        }

        private static string Narrative(int index, int stage)
        {
            string[][] lines = {
                new[] { "沿平台向前，出口在对面。", "熟悉的平台，陌生的灯光。", "灯光失序，出口仍在对面。" },
                new[] { "绕过中庭，从侧面离开。", "标识开始偏移，留意侧面的出口。", "别跟随阴影，找到侧面的出口。" },
                new[] { "保持速度，穿过折返通道。", "稳定装置亮起，击碎它继续前进。", "通道没有改变，别在这里停下。" },
                new[] { "沿着两侧的平台前进。", "这片空间似曾相识。", "房间在重复，前进才有出口。" },
                new[] { "留意平台之间的距离。", "光线开始摇晃，看清下一处落点。", "信任脚下的落点，别停下来。" },
                new[] { "沿通道前进，在尽头转向。", "转角后的光线正在改变。", "穿过转角，出口就在前面。" },
                new[] { "长廊通向另一端。", "走廊还在延伸。", "别回头，出口在前方。" }
            };
            return lines[index - 1][stage];
        }

        internal static void EnsureNarrative(FlowRoomTemplate template)
        {
            if (template.Narratives.Length > 0) return;
            string path = AssetDatabase.GetAssetPath(template);
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var loaded = root.GetComponent<FlowRoomTemplate>();
                FlowCheckpoint checkpoint = loaded.GetComponentsInChildren<FlowCheckpoint>(true).OrderBy(p => p.Priority).First();
                var group = Anchor(root.transform, "差分叙事内容组", Vector3.zero).gameObject.AddComponent<FlowContentGroup>();
                group.Id = "narrative"; group.GameplayOnly = true;
                var text = Anchor(group.transform, "入口差分叙事", checkpoint.transform.localPosition + new Vector3(0, 2.6f, 6)).gameObject.AddComponent<TextMesh>();
                // 沿用示例的叙事接口，指定项目中文字体，避免默认 Arial 缺失中文字形。
                text.font = AssetDatabase.LoadAssetAtPath<Font>("Assets/Art/UI/Fonts/NanoDotSong.ttf");
                if (text.font == null) throw new InvalidOperationException("找不到地图叙事所需的中文字体");
                text.GetComponent<MeshRenderer>().sharedMaterial = text.font.material;
                text.anchor = TextAnchor.MiddleCenter; text.alignment = TextAlignment.Center;
                text.fontSize = 64; text.characterSize = .06f; text.color = Color.white; text.text = string.Empty;
                loaded.Narratives = new[] { text };
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
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
            RoomVariantDefinition final = room.Variants[0];
            if (string.IsNullOrEmpty(final.Narrative))
                ConfigureVariant(final, 0, 100, new Color(1f, .9f, .6f), 0, "抵达出口，结束这次循环。");
            return final;
        }

        private static FlowRoomTemplate ExportTemplate(string name, string path, Material material, bool changesChaos)
            => ExportTemplate("Assets/Prefabs/" + name + ".prefab", name, path, material, changesChaos,
                Vector3.forward, name == "BOX_S_002" || name == "BOX_S_006" ? Vector3.right : Vector3.forward);

        internal static FlowRoomTemplate ExportTemplate(string sourcePath, string name, string path, Material material,
            bool changesChaos, Vector3 entryForward, Vector3 exitForward, bool includeLowerPlatforms = false)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
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
                    .OrderBy(c => Vector3.Dot(c.bounds.center, entryForward)).ToArray();
                if (platforms.Length != 4) throw new InvalidOperationException(name + "：需要人工确认两个门口平台");
                Collider entryFloor = platforms.Take(2).OrderBy(c => c.bounds.center.y).First();
                Collider exitFloor = platforms.Skip(2).OrderBy(c => c.bounds.center.y).First();
                Collider entryCeiling = platforms.Take(2).OrderByDescending(c => c.bounds.center.y).First();
                Collider exitCeiling = platforms.Skip(2).OrderByDescending(c => c.bounds.center.y).First();
                Vector3 entryCenter = FloorPoint(entryFloor), exitCenter = FloorPoint(exitFloor);
                Vector3 origin = entryCenter - entryForward * Vector3.Dot(entryFloor.bounds.extents, Abs(entryForward));
                Vector3 exit = exitCenter;
                exit += exitForward * Vector3.Dot(exitFloor.bounds.extents, Abs(exitForward));
                Vector2 entrySize = new Vector2(16, Mathf.Min(7.5f, entryCeiling.bounds.min.y - entryCenter.y - .1f));
                Vector2 exitSize = new Vector2(16, Mathf.Min(7.5f, exitCeiling.bounds.min.y - exitCenter.y - .1f));
                // 保留美术预制体嵌套引用，将任意轴向的门口统一到模板入口 +Z。
                Quaternion normalize = Quaternion.Inverse(Quaternion.LookRotation(entryForward, Vector3.up));
                geometry.transform.position = normalize * (geometry.transform.position - origin);
                geometry.transform.rotation = normalize * geometry.transform.rotation;
                Physics.SyncTransforms();
                var template = wrapper.AddComponent<FlowRoomTemplate>();
                template.Entry = Anchor(wrapper.transform, "Entry", Vector3.zero);
                template.Exit = Anchor(wrapper.transform, "Exit", normalize * (exit - origin));
                template.Exit.localRotation = Quaternion.LookRotation(normalize * exitForward, Vector3.up);
                template.EntrySize = entrySize; template.ExitSize = exitSize;
                template.EntryDoor = Door(wrapper.transform, "入口保护门", template.Entry, entrySize, material);
                template.ExitDoor = Door(wrapper.transform, "出口保护门", template.Exit, exitSize, material);
                var checkpoint = Anchor(wrapper.transform, "入口出生记录点", normalize * (entryCenter - origin) + Vector3.up * .15f).gameObject.AddComponent<FlowCheckpoint>();
                checkpoint.Id = "entry";
                if (changesChaos) AddDevice(wrapper.transform, normalize * (exitCenter - origin), material);
                Renderer[] renderers = geometry.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled && r.bounds.size.sqrMagnitude > 0).ToArray();
                Bounds bounds = renderers[0].bounds;
                foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
                template.GeometryBounds = bounds;
                // 裂隙恢复应在跌离可玩平台后触发，不能等到高耸装饰的最低包围盒。
                template.FallHeight = Mathf.Min(entryCenter.y, exitCenter.y) - origin.y - 6;
                if (includeLowerPlatforms)
                {
                    // Cdwy 箱庭内有低于两端门口的可走地面，不能把正常落地误判为裂隙掉落。
                    foreach (Collider floor in geometry.GetComponentsInChildren<Collider>(true))
                    {
                        Bounds floorBounds = floor.bounds;
                        if (!floor.enabled || floor.isTrigger || floorBounds.size.y >= 2 || floorBounds.size.x < 8 || floorBounds.size.z < 8) continue;
                        var ray = new Ray(floorBounds.center + Vector3.up * (floorBounds.extents.y + 1), Vector3.down);
                        if (floor.Raycast(ray, out var hit, floorBounds.size.y + 2) && hit.normal.y > .9f)
                            template.FallHeight = Mathf.Min(template.FallHeight, hit.point.y - 6);
                    }
                }
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

        private static Vector3 Abs(Vector3 vector) => new Vector3(Mathf.Abs(vector.x), Mathf.Abs(vector.y), Mathf.Abs(vector.z));

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
            RoomPoolEntry[] existing = pool.Entries;
            // 接入其他批次后，重跑七图装配不能清掉这些地图及其手调权重。
            pool.Entries = variants.Select(variant => existing.FirstOrDefault(entry => entry != null && entry.Variant == variant)
                ?? new RoomPoolEntry { Variant = variant }).Concat(existing.Where(entry => entry != null && entry.Variant != null
                    && !variants.Contains(entry.Variant))).ToArray();
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
