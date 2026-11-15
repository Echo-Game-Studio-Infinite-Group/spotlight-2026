using System;
using System.IO;
using System.Linq;
using GameJam.Flow;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameJam.FlowEditor
{
    public static class CdwyBoxSetup
    {
        private const string SourceFolder = "Assets/Scenes/LevelTest_Cdwy 1/Prefabs/";
        private const string Folder = "Assets/Settings/FormalFlow";
        private static readonly string[] Sources = { "BOX_S01_2", "BOX_S02_2", "BOX_S03 _2", "BOX_S04 _2" };

        [MenuItem("超高速行者/流程与地图/接入 Cdwy 四个资产箱庭")]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出播放模式");
            var config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>(Folder + "/RunFlow.asset");
            if (config == null) throw new InvalidOperationException("请先配置正式流程");
            var pool = config.NormalPools.Single(p => p != null && !p.ChangesChaos);
            // 先确认四张都是有成对门口平台的资产版，避免误导入旁边的白盒。
            foreach (string source in Sources)
            {
                MapReport report = InspectSource(source);
                if (report.Renderers < 50 || report.Colliders.Count(c => c.Name.StartsWith("Torch 01", StringComparison.Ordinal)
                    && c.Size.y < 2 && c.Size.x > 10 && c.Size.z > 10) != 4)
                    throw new InvalidOperationException(source + "：资产或门口平台结构与确认结果不符");
            }
            RoomDefinition[] rooms = Enumerable.Range(0, Sources.Length).Select(i => BuildRoom(i, config.ConnectionMaterial)).ToArray();
            var additions = rooms.SelectMany(room => room.Variants).Where(variant => !pool.Entries.Any(entry => entry != null && entry.Variant == variant))
                .Select(variant => new RoomPoolEntry { Variant = variant });
            pool.Entries = pool.Entries.Concat(additions).ToArray();
            EditorUtility.SetDirty(pool);
            AssetDatabase.SaveAssets();
            // 教学关的本地敌人点草稿另行检查；这里完整验证新增地图、普通池、循环与终点。
            var validation = Object.Instantiate(config);
            try
            {
                validation.InitialRoom = rooms[0].Variants[0];
                var errors = FlowConfigValidator.Validate(validation);
                if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
            }
            finally { Object.DestroyImmediate(validation); }
            Debug.Log("[Cdwy] 四张资产版箱庭与 12 个混乱度差分已加入正式普通池；教学关、变化池、终点与已有权重保持原配置。");
        }

        private static RoomDefinition BuildRoom(int index, Material material)
        {
            string name = "CDWY_BOX_S" + (index + 1).ToString("00") + "_2";
            string roomPath = Folder + "/" + name + ".asset";
            string templatePath = Folder + "/Templates/" + name + ".prefab";
            var template = AssetDatabase.LoadAssetAtPath<FlowRoomTemplate>(templatePath);
            if (template == null)
                template = FormalBoxSetup.ExportTemplate(SourceFolder + Sources[index] + ".prefab", name, templatePath, material,
                    false, Vector3.right, Vector3.right, true);
            FormalBoxSetup.EnsureNarrative(template);
            var room = AssetDatabase.LoadAssetAtPath<RoomDefinition>(roomPath);
            if (room == null)
            {
                room = ScriptableObject.CreateInstance<RoomDefinition>();
                room.Id = name.ToLowerInvariant(); room.DisplayName = name; room.Template = template;
                AssetDatabase.CreateAsset(room, roomPath);
            }
            if (room.Variants.Length == 0)
            {
                room.Variants = new[]
                {
                    Variant(room, "default", 0, 30, new Color(.5f, .8f, 1f), Narrative(index, 0)),
                    Variant(room, "middle", 31, 79, new Color(1f, .7f, .25f), Narrative(index, 1)),
                    Variant(room, "high", 80, 100, new Color(1f, .25f, .18f), Narrative(index, 2))
                };
                EditorUtility.SetDirty(room);
            }
            return room;
        }

        private static RoomVariantDefinition Variant(RoomDefinition room, string suffix, int min, int max, Color color, string narrative)
        {
            string path = Folder + "/" + room.DisplayName + "." + suffix + ".asset";
            var variant = AssetDatabase.LoadAssetAtPath<RoomVariantDefinition>(path);
            if (variant != null) return variant;
            variant = ScriptableObject.CreateInstance<RoomVariantDefinition>();
            variant.Id = room.Id + "." + suffix; variant.Room = room;
            FormalBoxSetup.ConfigureVariant(variant, min, max, color, 0, narrative);
            AssetDatabase.CreateAsset(variant, path);
            return variant;
        }

        private static string Narrative(int index, int stage)
        {
            string[][] lines =
            {
                new[] { "穿过大厅，从另一侧离开。", "大厅的灯光正在改变。", "别被灯光干扰，找到对面的出口。" },
                new[] { "沿平台绕行，留意落脚点。", "这条路似乎绕回了原处。", "保持方向，出口仍在另一端。" },
                new[] { "沿高低平台向前，下方也有道路。", "脚下的道路开始变得陌生。", "灯光失序，先确认下一处落点。" },
                new[] { "穿过中央区域，前往另一端的平台。", "房间还是原来的形状。", "别停在中央，继续前进。" }
            };
            return lines[index][stage];
        }

        [Serializable] private sealed class Inspection { public MapReport[] Maps; }
        [Serializable] private sealed class MapReport
        {
            public string Source;
            public int Renderers;
            public string[] RootChildren;
            public ColliderReport[] Colliders;
        }
        [Serializable] private sealed class ColliderReport
        {
            public string Name;
            public Vector3 Center;
            public Vector3 Size;
            public Vector3 Rotation;
            public bool Trigger;
        }

        [MenuItem("超高速行者/流程与地图/检查 Cdwy 资产箱庭门口")]
        public static void InspectSources()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出播放模式");
            var report = new Inspection { Maps = Sources.Select(InspectSource).ToArray() };
            Directory.CreateDirectory("Library/CodexCdwy");
            File.WriteAllText("Library/CodexCdwy/inspection.json", JsonUtility.ToJson(report, true));
            Debug.Log("[Cdwy] 四张资产版箱庭的碰撞体与门口结构已写入 Library/CodexCdwy/inspection.json");
        }

        private static MapReport InspectSource(string source)
        {
            string path = SourceFolder + source + ".prefab";
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Physics.SyncTransforms();
                return new MapReport
                {
                    Source = path,
                    Renderers = root.GetComponentsInChildren<Renderer>(true).Length,
                    RootChildren = root.transform.Cast<Transform>().Select(t => t.name).ToArray(),
                    Colliders = root.GetComponentsInChildren<Collider>(true).Where(c => c.enabled)
                        .Select(c => new ColliderReport { Name = c.name, Center = c.bounds.center, Size = c.bounds.size,
                            Rotation = c.transform.eulerAngles, Trigger = c.isTrigger }).ToArray()
                };
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
