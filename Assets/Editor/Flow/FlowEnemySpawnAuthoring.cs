using System;
using System.Collections.Generic;
using System.Linq;
using GameJam.Flow;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameJam.FlowEditor
{
    public static class FlowEnemySpawnAuthoring
    {
        public const string PrefabPath = "Assets/Prefabs/Flow/EnemySpawnPoint.prefab";

        [MenuItem("超高速行者/流程与地图/敌人出生点预制体")]
        public static void SelectPrefab() => Selection.activeObject = EnsurePrefab();

        public static GameObject EnsurePrefab()
        {
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (existing != null) return existing;
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs/Flow")) AssetDatabase.CreateFolder("Assets/Prefabs", "Flow");
            var root = new GameObject("敌人出生点");
            try { root.AddComponent<FlowEnemySpawnPoint>(); return PrefabUtility.SaveAsPrefabAsset(root, PrefabPath); }
            finally { Object.DestroyImmediate(root); }
        }

        public static FlowRoomTemplate ResolveTemplate(RoomVariantDefinition variant)
        {
            if (variant.Room == null || variant.Room.Template == null) return null;
            PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
            return stage != null && stage.assetPath == AssetDatabase.GetAssetPath(variant.Room.Template)
                ? stage.prefabContentsRoot.GetComponent<FlowRoomTemplate>() : variant.Room.Template;
        }

        public static FlowRoomTemplate OpenTemplate(RoomVariantDefinition variant)
        {
            if (variant.Room == null || variant.Room.Template == null) return null;
            string path = AssetDatabase.GetAssetPath(variant.Room.Template);
            if (string.IsNullOrEmpty(path)) return null;
            PrefabStage stage = PrefabStageUtility.OpenPrefab(path);
            return stage.prefabContentsRoot.GetComponent<FlowRoomTemplate>();
        }

        public static void AddPoint(RoomVariantDefinition variant)
        {
            FlowRoomTemplate template = OpenTemplate(variant);
            if (template == null) return;
            GameObject pointObject = (GameObject)PrefabUtility.InstantiatePrefab(EnsurePrefab(), template.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(pointObject, "新增敌人出生点");
            pointObject.transform.SetParent(template.transform, false);
            FlowEnemySpawnPoint point = pointObject.GetComponent<FlowEnemySpawnPoint>();
            Undo.RecordObject(point, "分配出生点编号");
            point.Id = NewId(ReservedIds(template, variant.Room));
            point.DisplayName = "新出生点";
            PrefabUtility.RecordPrefabInstancePropertyModifications(point);
            EditorUtility.SetDirty(point);
            EditorSceneManager.MarkSceneDirty(template.gameObject.scene);
            Selection.activeGameObject = pointObject;
            SceneView.lastActiveSceneView?.FrameSelected();
        }

        public static int RepairIds(FlowRoomTemplate template, RoomDefinition room)
        {
            HashSet<string> reserved = ReservedIds(template, room);
            var seen = new HashSet<string>();
            int changed = 0;
            foreach (FlowEnemySpawnPoint point in template.GetComponentsInChildren<FlowEnemySpawnPoint>(true))
            {
                if (!string.IsNullOrWhiteSpace(point.Id) && seen.Add(point.Id)) continue;
                Undo.RecordObject(point, "修复出生点编号");
                point.Id = NewId(reserved); seen.Add(point.Id);
                PrefabUtility.RecordPrefabInstancePropertyModifications(point);
                EditorUtility.SetDirty(point); changed++;
            }
            if (changed > 0 && template.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(template.gameObject.scene);
            return changed;
        }

        public static void Synchronize(RoomVariantDefinition variant, IReadOnlyList<FlowEnemySpawnPoint> points, bool removeOrphans)
        {
            var ids = new HashSet<string>(points.Where(point => !string.IsNullOrWhiteSpace(point.Id)).Select(point => point.Id));
            var bindings = (variant.EnemySpawns ?? new FlowEnemySpawnBinding[0]).ToList();
            if (removeOrphans) bindings.RemoveAll(binding => binding == null || !ids.Contains(binding.PointId ?? string.Empty));
            foreach (string id in ids)
                if (!bindings.Any(binding => binding != null && binding.PointId == id)) bindings.Add(new FlowEnemySpawnBinding { PointId = id });
            Undo.RecordObject(variant, removeOrphans ? "清理失效出生点配置" : "同步出生点");
            variant.EnemySpawns = bindings.ToArray(); EditorUtility.SetDirty(variant);
        }

        public static void Locate(RoomVariantDefinition variant, FlowEnemySpawnPoint point)
        {
            FlowRoomTemplate source = ResolveTemplate(variant);
            var indices = new List<int>();
            for (Transform cursor = point.transform; cursor != source.transform; cursor = cursor.parent) indices.Add(cursor.GetSiblingIndex());
            FlowRoomTemplate opened = OpenTemplate(variant);
            if (opened == null) return;
            Transform selected = opened.transform;
            for (int i = indices.Count - 1; i >= 0; i--) selected = selected.GetChild(indices[i]);
            Selection.activeGameObject = selected.gameObject; SceneView.lastActiveSceneView?.FrameSelected();
        }

        private static HashSet<string> ReservedIds(FlowRoomTemplate template, RoomDefinition room)
        {
            var result = new HashSet<string>(template.GetComponentsInChildren<FlowEnemySpawnPoint>(true).Select(point => point.Id));
            if (room != null)
                foreach (RoomVariantDefinition variant in room.Variants)
                    if (variant != null)
                        foreach (FlowEnemySpawnBinding binding in variant.EnemySpawns ?? new FlowEnemySpawnBinding[0])
                            if (binding != null) result.Add(binding.PointId);
            return result;
        }

        private static string NewId(HashSet<string> reserved)
        {
            string id;
            do { id = "SP_" + Guid.NewGuid().ToString("N").Substring(0, 8); } while (!reserved.Add(id));
            return id;
        }
    }
}
