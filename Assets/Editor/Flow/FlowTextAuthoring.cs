using System;
using System.Collections.Generic;
using System.Linq;
using GameJam.Flow;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using Object = UnityEngine.Object;

namespace GameJam.FlowEditor
{
    public static class FlowTextAuthoring
    {
        public const string PrefabPath = "Assets/Prefabs/Flow/TextPoint.prefab";
        public const string SettingsPath = "Assets/Settings/FlowText";
        public const string SourcePath = "Assets/Texture/Text3D/Assets/Prefabs/UI/Text3D.prefab";

        [MenuItem("超高速行者/流程与地图/文字点预制体")]
        public static void SelectPrefab() => Selection.activeObject = EnsurePrefab();

        public static void Build() { EnsurePrefab(); AssetDatabase.SaveAssets(); }

        public static GameObject EnsurePrefab()
        {
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (existing != null) return existing;
            if (!AssetDatabase.IsValidFolder(SettingsPath)) AssetDatabase.CreateFolder("Assets/Settings", "FlowText");
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs/Flow")) AssetDatabase.CreateFolder("Assets/Prefabs", "Flow");
            TMP_FontAsset font = EnsureFont();
            FlowTextAnimation appearance = EnsureEffect("Appearance", "TextAppearance");
            FlowTextAnimation disappearance = EnsureEffect("Dissolve", "TextDissolve");
            var root = new GameObject("地图文字点");
            try
            {
                FlowTextPoint point = root.AddComponent<FlowTextPoint>();
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePath);
                GameObject visual = source != null ? Object.Instantiate(source) : new GameObject("Text3D", typeof(TextMeshPro));
                visual.name = "Text3D";
                foreach (MonoBehaviour behaviour in visual.GetComponents<MonoBehaviour>())
                    if (!(behaviour is TextMeshPro)) Object.DestroyImmediate(behaviour);
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(visual);
                Animator animator = visual.GetComponent<Animator>();
                if (animator != null) Object.DestroyImmediate(animator);
                visual.transform.SetParent(root.transform, false);
                visual.transform.localPosition = Vector3.zero; visual.transform.localRotation = Quaternion.identity; visual.transform.localScale = Vector3.one;
                TextMeshPro text = visual.GetComponent<TextMeshPro>();
                text.font = font; text.text = "地图文字"; text.fontSize = 36f; text.raycastTarget = false;
                text.rectTransform.sizeDelta = new Vector2(20f, 5f);
                point.Visual = visual.AddComponent<FlowTextVisual>();
                point.DefaultAppearance = appearance; point.DefaultDisappearance = disappearance;
                return PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static TMP_FontAsset EnsureFont()
        {
            string path = SettingsPath + "/Chinese SDF.asset";
            TMP_FontAsset existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
            if (existing != null) return existing;
            Font source = AssetDatabase.LoadAssetAtPath<Font>("Assets/Art/UI/Fonts/NanoDotSong.ttf");
            if (source == null) throw new InvalidOperationException("地图文字需要现有的 NanoDotSong 中文字体。");
            TMP_FontAsset font = TMP_FontAsset.CreateFontAsset(source, 64, 8, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            font.name = "地图文字中文 SDF";
            AssetDatabase.CreateAsset(font, path);
            font.material.name = "地图文字字体材质";
            AssetDatabase.AddObjectToAsset(font.material, font);
            foreach (Texture2D atlas in font.atlasTextures) AssetDatabase.AddObjectToAsset(atlas, font);
            EditorUtility.SetDirty(font); AssetDatabase.SaveAssets();
            return font;
        }

        private static FlowTextAnimation EnsureEffect(string name, string clip)
        {
            string path = SettingsPath + "/" + name + ".asset";
            FlowTextAnimation effect = AssetDatabase.LoadAssetAtPath<FlowTextAnimation>(path);
            if (effect != null) return effect;
            effect = ScriptableObject.CreateInstance<FlowTextAnimation>();
            effect.Source = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Texture/Text3D/Assets/Animations/Text/" + clip + ".anim");
            if (effect.Source == null) throw new InvalidOperationException("缺少文字来源动画：" + clip);
            AssetDatabase.CreateAsset(effect, path); Bake(effect); return effect;
        }

        public static void Bake(FlowTextAnimation effect)
        {
            if (effect.Source == null) throw new InvalidOperationException("请先指定来源动画。");
            var tracks = new List<FlowTextAnimation.FloatTrack>();
            var materials = new List<FlowTextAnimation.MaterialKey>();
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(effect.Source))
            {
                if (!string.IsNullOrEmpty(binding.path) || !(binding.propertyName.StartsWith("material.") ||
                    binding.propertyName.StartsWith("m_LocalPosition.") || binding.propertyName.StartsWith("m_LocalScale.") ||
                    binding.propertyName.StartsWith("localEulerAnglesRaw.")))
                    throw new InvalidOperationException("文字动画暂不支持该曲线：" + binding.path + " / " + binding.propertyName);
                tracks.Add(new FlowTextAnimation.FloatTrack { Property = binding.propertyName, Curve = AnimationUtility.GetEditorCurve(effect.Source, binding) });
            }
            foreach (EditorCurveBinding binding in AnimationUtility.GetObjectReferenceCurveBindings(effect.Source))
            {
                if (binding.path != "" || binding.propertyName != "m_Materials.Array.data[0]")
                    throw new InvalidOperationException("文字动画暂不支持该引用轨道：" + binding.propertyName);
                foreach (ObjectReferenceKeyframe key in AnimationUtility.GetObjectReferenceCurve(effect.Source, binding))
                {
                    if (!(key.value is Material material)) throw new InvalidOperationException("文字动画包含缺失材质。");
                    // 导入包仍指向旧目录的材质，统一使用本次导入且具有独立 Shader 名称的效果。
                    Material imported = AssetDatabase.LoadAssetAtPath<Material>("Assets/Texture/Text3D/Assets/Art/Materials/" + material.name + ".mat");
                    materials.Add(new FlowTextAnimation.MaterialKey { Time = key.time, Material = imported != null ? imported : material });
                }
            }
            Undo.RecordObject(effect, "读取文字动画曲线");
            effect.Tracks = tracks.ToArray(); effect.Materials = materials.OrderBy(key => key.Time).ToArray();
            effect.Duration = Mathf.Max(.001f, effect.Source.length);
            AnimationCurve cutoff = tracks.FirstOrDefault(track => track.Property == "material._CutoffHeight")?.Curve;
            if (cutoff != null && cutoff.length > 0)
            { effect.CutoffMinimum = cutoff.keys.Min(key => key.value); effect.CutoffMaximum = cutoff.keys.Max(key => key.value); }
            EditorUtility.SetDirty(effect);
        }

        public static FlowTextPoint[] Points(FlowRoomTemplate template) => template.GetComponentsInChildren<FlowTextPoint>(true);

        public static void AddPoint(RoomVariantDefinition variant)
        {
            FlowRoomTemplate template = FlowEnemySpawnAuthoring.OpenTemplate(variant);
            if (template == null) return;
            var root = (GameObject)PrefabUtility.InstantiatePrefab(EnsurePrefab(), template.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(root, "新增文字点"); root.transform.SetParent(template.transform, false);
            FlowTextPoint point = root.GetComponent<FlowTextPoint>();
            point.Id = NewId(ReservedIds(template, variant.Room)); point.DisplayName = "新文字点";
            PrefabUtility.RecordPrefabInstancePropertyModifications(point); EditorUtility.SetDirty(point);
            EditorSceneManager.MarkSceneDirty(template.gameObject.scene); Selection.activeGameObject = root;
            SceneView.lastActiveSceneView?.FrameSelected();
        }

        public static void Synchronize(RoomVariantDefinition variant, IReadOnlyList<FlowTextPoint> points, bool removeOrphans)
        {
            var ids = new HashSet<string>(points.Where(point => !string.IsNullOrWhiteSpace(point.Id)).Select(point => point.Id));
            var bindings = (variant.Texts ?? new FlowTextBinding[0]).ToList();
            if (removeOrphans) bindings.RemoveAll(binding => binding == null || !ids.Contains(binding.PointId ?? ""));
            foreach (string id in ids)
                if (!bindings.Any(binding => binding != null && binding.PointId == id)) bindings.Add(new FlowTextBinding { PointId = id });
            Undo.RecordObject(variant, removeOrphans ? "清理失效文字配置" : "同步文字点");
            variant.Texts = bindings.ToArray(); EditorUtility.SetDirty(variant);
        }

        public static int RepairIds(FlowRoomTemplate template, RoomDefinition room)
        {
            HashSet<string> reserved = ReservedIds(template, room); var seen = new HashSet<string>(); int changed = 0;
            foreach (FlowTextPoint point in Points(template))
            {
                if (!string.IsNullOrWhiteSpace(point.Id) && seen.Add(point.Id)) continue;
                Undo.RecordObject(point, "修复文字点编号"); point.Id = NewId(reserved); seen.Add(point.Id);
                PrefabUtility.RecordPrefabInstancePropertyModifications(point); EditorUtility.SetDirty(point); changed++;
            }
            if (changed > 0 && template.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(template.gameObject.scene);
            return changed;
        }

        public static void Locate(RoomVariantDefinition variant, string id)
        {
            FlowRoomTemplate template = FlowEnemySpawnAuthoring.OpenTemplate(variant);
            FlowTextPoint point = template != null ? Points(template).FirstOrDefault(item => item.Id == id) : null;
            if (point == null) return;
            Selection.activeGameObject = point.gameObject; SceneView.lastActiveSceneView?.FrameSelected();
        }

        private static HashSet<string> ReservedIds(FlowRoomTemplate template, RoomDefinition room)
        {
            var ids = new HashSet<string>(Points(template).Select(point => point.Id));
            if (room != null)
                foreach (RoomVariantDefinition variant in room.Variants)
                    if (variant != null)
                        foreach (FlowTextBinding binding in variant.Texts ?? new FlowTextBinding[0]) if (binding != null) ids.Add(binding.PointId);
            return ids;
        }
        private static string NewId(HashSet<string> reserved)
        { string id; do { id = "TXT_" + Guid.NewGuid().ToString("N").Substring(0, 8); } while (!reserved.Add(id)); return id; }
    }
}
