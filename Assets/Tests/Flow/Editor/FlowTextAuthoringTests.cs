using System.Collections.Generic;
using System.Linq;
using GameJam.Flow;
using GameJam.FlowEditor;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameJam.FlowTests
{
    public sealed class FlowTextAuthoringTests
    {
        private readonly List<Object> _objects = new List<Object>();
        private FlowRoomTemplate _template;
        private RoomDefinition _room;
        private RoomVariantDefinition _low, _high;

        [SetUp]
        public void SetUp()
        {
            var root = new GameObject("文字测试模板"); _objects.Add(root); _template = root.AddComponent<FlowRoomTemplate>();
            _room = Make<RoomDefinition>(); _room.Template = _template;
            _low = Make<RoomVariantDefinition>(); _low.Id = "low"; _low.Room = _room;
            _high = Make<RoomVariantDefinition>(); _high.Id = "high"; _high.Room = _room;
            _room.Variants = new[] { _low, _high };
        }
        private T Make<T>() where T : ScriptableObject { T value = ScriptableObject.CreateInstance<T>(); _objects.Add(value); return value; }
        private FlowTextPoint Point(string id)
        {
            var root = Object.Instantiate(FlowTextAuthoring.EnsurePrefab()); root.transform.SetParent(_template.transform, false);
            FlowTextPoint point = root.GetComponent<FlowTextPoint>(); point.Id = id; return point;
        }
        [TearDown]
        public void TearDown() { FlowTextPreview.Stop(); foreach (Object value in _objects) if (value != null) Object.DestroyImmediate(value); _objects.Clear(); }

        [Test]
        public void DeliveredPrefabHasChineseFontAndUsesImportedEffectsWithoutLegacyAutoplay()
        {
            GameObject prefab = FlowTextAuthoring.EnsurePrefab();
            FlowTextPoint point = prefab.GetComponent<FlowTextPoint>();
            Assert.That(point.Id, Is.Null.Or.Empty); Assert.That(point.Visual, Is.Not.Null);
            TextMeshPro text = point.Visual.GetComponent<TextMeshPro>();
            Assert.That(text.font.HasCharacters("前进不要相信出口地图文字", out _, true, true), Is.True);
            Assert.That(prefab.GetComponentsInChildren<Animator>(true), Is.Empty);
            Assert.That(point.DefaultAppearance.Duration, Is.EqualTo(2.5f).Within(.02f));
            Assert.That(point.DefaultDisappearance.Materials.Last().Material.shader.name, Is.EqualTo("GameJam/Flow/Dissolve"));
            Assert.That(point.DefaultAppearance.Tracks.Any(track => track.Property == "material._Right"), Is.True);
            foreach (FlowTextAnimation effect in new[] { point.DefaultAppearance, point.DefaultDisappearance })
                foreach (FlowTextAnimation.MaterialKey key in effect.Materials) Assert.That(ShaderUtil.ShaderHasError(key.Material.shader), Is.False, key.Material.shader.name);
        }

        [Test]
        public void SynchronizingMovedPointsKeepsIndependentTextAndAddsDisabledBindings()
        {
            FlowTextPoint point = Point("kept"), added = Point("added");
            _low.Texts = new[] { new FlowTextBinding { PointId = "kept", Enabled = true, Content = "安全出口" } };
            _high.Texts = new[] { new FlowTextBinding { PointId = "kept", Enabled = true, Content = "不要相信出口" } };
            point.name = "改名"; point.transform.localPosition = Vector3.right * 10f;
            FlowTextAuthoring.Synchronize(_low, new[] { added, point }, false);
            FlowTextAuthoring.Synchronize(_high, new[] { point, added }, false);
            Assert.That(_low.Texts.Single(binding => binding.PointId == "kept").Content, Is.EqualTo("安全出口"));
            Assert.That(_high.Texts.Single(binding => binding.PointId == "kept").Content, Is.EqualTo("不要相信出口"));
            Assert.That(_low.Texts.Single(binding => binding.PointId == "added").Enabled, Is.False);
            Assert.That(FlowTextValidation.Template(_template), Is.Empty);
            Assert.That(FlowTextValidation.Variant(_low), Is.Empty);
        }

        [Test]
        public void RepairPreservesOriginalIdAndCleanupIsExplicit()
        {
            FlowTextPoint first = Point("kept"), duplicate = Point("kept"), blank = Point("");
            _low.Texts = new[] { new FlowTextBinding { PointId = "kept", Content = "保留" }, new FlowTextBinding { PointId = "orphan" } };
            Assert.That(FlowTextAuthoring.RepairIds(_template, _room), Is.EqualTo(2));
            Assert.That(first.Id, Is.EqualTo("kept"));
            Assert.That(new[] { first.Id, duplicate.Id, blank.Id }.Distinct().Count(), Is.EqualTo(3));
            FlowTextAuthoring.Synchronize(_low, new[] { first }, false);
            Assert.That(_low.Texts.Any(binding => binding.PointId == "orphan"), Is.True);
            FlowTextAuthoring.Synchronize(_low, new[] { first }, true);
            Assert.That(_low.Texts.Length, Is.EqualTo(1)); Assert.That(_low.Texts[0].Content, Is.EqualTo("保留"));
        }

        [Test]
        public void ValidationRejectsDuplicatesEmptyTextAndNonFiniteParameters()
        {
            Point("same"); Point("same");
            _low.Texts = new[] { new FlowTextBinding { PointId = "same", Enabled = true, FontSize = float.NaN }, new FlowTextBinding { PointId = "same" } };
            Assert.That(FlowTextValidation.Template(_template).Any(error => error.Contains("编号为空或重复")), Is.True);
            List<string> errors = FlowTextValidation.Variant(_low);
            Assert.That(errors.Any(error => error.Contains("配置重复")), Is.True);
            Assert.That(errors.Any(error => error.Contains("文字内容")), Is.True);
            Assert.That(errors.Any(error => error.Contains("无效")), Is.True);
        }

        [Test]
        public void EffectsUseActualTextBoundsAndIndependentMaterialsAndRestoreRootPose()
        {
            FlowTextPoint a = Point("a"), b = Point("b");
            a.transform.localPosition = new Vector3(3f, 4f, 5f); a.transform.localRotation = Quaternion.Euler(0f, 43f, 0f);
            Vector3 position = a.transform.position; Quaternion rotation = a.transform.rotation;
            a.Visual.Configure(new FlowTextBinding { Content = "前进\n不要相信出口", Color = Color.red });
            b.Visual.Configure(new FlowTextBinding { Content = "保持速度", Color = Color.cyan });
            a.Visual.ShowFinal(a.DefaultAppearance); b.Visual.ShowFinal(b.DefaultAppearance);
            Material first = a.Visual.GetComponent<Renderer>().sharedMaterial, second = b.Visual.GetComponent<Renderer>().sharedMaterial;
            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(first.mainTexture, Is.Not.Null);
            Assert.That(first.GetFloat("_FlowTextUseBounds"), Is.EqualTo(1f));
            a.Visual.Sample(a.DefaultAppearance, 0f);
            Assert.That(second.GetFloat("_Right"), Is.EqualTo(.5f).Within(.001f));
            a.Visual.Hide(); Assert.That(a.transform.position, Is.EqualTo(position)); Assert.That(a.transform.rotation, Is.EqualTo(rotation));
            Assert.That(a.Visual.IsVisible, Is.False);
        }

        [Test]
        public void SavedVariantReloadsMultilineContentAndEffectReferences()
        {
            string folder = "Assets/__FlowTextTest_" + System.Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            try
            {
                FlowTextPoint point = Point("saved");
                var variant = ScriptableObject.CreateInstance<RoomVariantDefinition>();
                variant.Texts = new[] { new FlowTextBinding { PointId = "saved", Enabled = true, Content = "前进\n不要相信出口", Appearance = point.DefaultAppearance, MaximumDistance = 12f } };
                string path = folder + "/variant.asset"; AssetDatabase.CreateAsset(variant, path); EditorUtility.SetDirty(variant); AssetDatabase.SaveAssets();
                Resources.UnloadAsset(variant); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                FlowTextBinding binding = AssetDatabase.LoadAssetAtPath<RoomVariantDefinition>(path).Texts.Single();
                Assert.That(binding.Content, Is.EqualTo("前进\n不要相信出口")); Assert.That(binding.Enabled, Is.True);
                Assert.That(binding.Appearance, Is.SameAs(point.DefaultAppearance)); Assert.That(binding.MaximumDistance, Is.EqualTo(12f));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void PreviewDoesNotReplaceTemplateTextAndSavingStopsPreview()
        {
            string folder = "Assets/__FlowTextPreview_" + System.Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            try
            {
                Point("preview");
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(_template.gameObject, folder + "/template.prefab");
                _room.Template = prefab.GetComponent<FlowRoomTemplate>();
                _low.Texts = new[] { new FlowTextBinding { PointId = "preview", Enabled = true, Content = "差分专用内容" } };
                FlowTextPreview.Start(_low, false);
                UnityEditor.SceneManagement.PrefabStage stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
                TextMeshPro source = stage.prefabContentsRoot.GetComponentInChildren<TextMeshPro>(true);
                Assert.That(source.text, Is.EqualTo("地图文字"));
                Assert.That(stage.scene.GetRootGameObjects().Any(root => (root.hideFlags & HideFlags.DontSaveInEditor) != 0), Is.True);
                // 冻结版本的保存入口是内部方法，与导航包自身的编辑器测试采用相同调用方式。
                typeof(UnityEditor.SceneManagement.PrefabStage).GetMethod("SavePrefab", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(stage, null);
                Assert.That(stage.scene.GetRootGameObjects().Any(root => root.GetComponent<FlowTextPoint>() != null), Is.False);
                Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/template.prefab").GetComponentInChildren<TextMeshPro>(true).text, Is.EqualTo("地图文字"));
                Assert.That(_low.Texts.Single().Content, Is.EqualTo("差分专用内容"));
            }
            finally
            {
                FlowTextPreview.Stop(); UnityEditor.SceneManagement.StageUtility.GoToMainStage(); AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
