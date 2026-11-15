using System;
using System.Collections.Generic;
using System.Linq;
using GameJam.Flow;
using GameJam.FlowEditor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GameJam.FlowTests
{
    public sealed class FlowEnemySpawnAuthoringTests
    {
        private readonly List<Object> _objects = new List<Object>();
        private FlowRoomTemplate _template;
        private RoomDefinition _room;
        private RoomVariantDefinition _low, _high;
        private string _assetFolder;

        [SetUp]
        public void SetUp()
        {
            var root = new GameObject("出生点测试模板"); _objects.Add(root);
            _template = root.AddComponent<FlowRoomTemplate>();
            _room = Make<RoomDefinition>(); _room.Template = _template;
            _low = Make<RoomVariantDefinition>(); _low.Id = "low"; _low.Room = _room;
            _high = Make<RoomVariantDefinition>(); _high.Id = "high"; _high.Room = _room;
            _room.Variants = new[] { _low, _high };
        }
        private T Make<T>() where T : ScriptableObject
        { T item = ScriptableObject.CreateInstance<T>(); _objects.Add(item); return item; }
        private FlowEnemySpawnPoint Point(string id)
        {
            var item = new GameObject(id ?? "未编号"); item.transform.SetParent(_template.transform);
            FlowEnemySpawnPoint point = item.AddComponent<FlowEnemySpawnPoint>(); point.Id = id; return point;
        }
        [TearDown]
        public void TearDown()
        {
            foreach (Object item in _objects) if (item != null) Object.DestroyImmediate(item);
            _objects.Clear();
            if (_assetFolder != null) { AssetDatabase.DeleteAsset(_assetFolder); _assetFolder = null; }
        }

        [Test]
        public void DeliveredPrefabIsAnUnassignedMarkerWithoutEnemyOrContinuousSpawner()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FlowEnemySpawnAuthoring.PrefabPath);
            Assert.That(prefab, Is.Not.Null);
            Assert.That(prefab.GetComponent<FlowEnemySpawnPoint>(), Is.Not.Null);
            Assert.That(prefab.GetComponent<FlowEnemySpawnPoint>().Id, Is.Null.Or.Empty);
            Assert.That(prefab.GetComponentsInChildren<EnemyBase>(true), Is.Empty);
            Assert.That(prefab.GetComponentsInChildren<EnemySpawner>(true), Is.Empty);
        }

        [Test]
        public void SynchronizingAndRenamingPointsPreserveIndependentVariantEnemyAssignments()
        {
            FlowEnemySpawnPoint first = Point("first"), added = Point("added");
            GameObject melee = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/EnemyTest.prefab");
            GameObject heavy = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/EnemyHeavy.prefab");
            _low.EnemySpawns = new[] { new FlowEnemySpawnBinding { PointId = first.Id, Enabled = true, EnemyPrefab = melee } };
            _high.EnemySpawns = new[] { new FlowEnemySpawnBinding { PointId = first.Id, Enabled = true, EnemyPrefab = heavy } };
            first.name = "改名并移动后的点"; first.transform.localPosition = Vector3.right * 5f;
            FlowEnemySpawnAuthoring.Synchronize(_low, new[] { added, first }, false);
            FlowEnemySpawnAuthoring.Synchronize(_high, new[] { first, added }, false);
            Assert.That(_low.EnemySpawns.Single(binding => binding.PointId == "first").EnemyPrefab, Is.SameAs(melee));
            Assert.That(_high.EnemySpawns.Single(binding => binding.PointId == "first").EnemyPrefab, Is.SameAs(heavy));
            Assert.That(_low.EnemySpawns.Single(binding => binding.PointId == "added").Enabled, Is.False);
            Assert.That(_high.EnemySpawns.Single(binding => binding.PointId == "added").EnemyPrefab, Is.Null);
        }

        [Test]
        public void MissingPointBindingsRemainUntilExplicitCleanup()
        {
            FlowEnemySpawnPoint point = Point("kept");
            _low.EnemySpawns = new[] { new FlowEnemySpawnBinding { PointId = "removed" } };
            FlowEnemySpawnAuthoring.Synchronize(_low, new[] { point }, false);
            Assert.That(_low.EnemySpawns.Select(binding => binding.PointId), Does.Contain("removed"));
            Assert.That(FlowEnemySpawnValidation.Variant(_low).Any(error => error.Contains("失效出生点配置")), Is.True);
            FlowEnemySpawnAuthoring.Synchronize(_low, new[] { point }, true);
            Assert.That(_low.EnemySpawns.Select(binding => binding.PointId), Is.EquivalentTo(new[] { "kept" }));
        }

        [Test]
        public void RepairingBlankAndDuplicateIdsPreservesOriginalBinding()
        {
            FlowEnemySpawnPoint first = Point("original"), duplicate = Point("original"), blank = Point("");
            _low.EnemySpawns = new[] { new FlowEnemySpawnBinding { PointId = "original" }, new FlowEnemySpawnBinding { PointId = "removed" } };
            Assert.That(FlowEnemySpawnAuthoring.RepairIds(_template, _room), Is.EqualTo(2));
            Assert.That(first.Id, Is.EqualTo("original"));
            Assert.That(new[] { first.Id, duplicate.Id, blank.Id }.Distinct().Count(), Is.EqualTo(3));
            Assert.That(new[] { duplicate.Id, blank.Id }, Does.Not.Contain("removed"));
            Assert.That(_low.EnemySpawns[0].PointId, Is.EqualTo("original"));
        }

        [Test]
        public void ValidationRejectsMissingEnemiesDuplicateBindingsAndDuplicateMarkers()
        {
            Point("same"); Point("same");
            _low.EnemySpawns = new[] { new FlowEnemySpawnBinding { PointId = "same", Enabled = true }, new FlowEnemySpawnBinding { PointId = "same" } };
            Assert.That(FlowEnemySpawnValidation.Template(_template).Any(error => error.Contains("编号为空或重复")), Is.True);
            List<string> errors = FlowEnemySpawnValidation.Variant(_low);
            Assert.That(errors.Any(error => error.Contains("敌人预制体资产")), Is.True);
            Assert.That(errors.Any(error => error.Contains("配置重复")), Is.True);
        }

        [Test]
        public void DisabledEmptyBindingsAreValidButSceneInstancesAreRejected()
        {
            Point("point");
            _low.EnemySpawns = new[] { new FlowEnemySpawnBinding { PointId = "point" } };
            Assert.That(FlowEnemySpawnValidation.Variant(_low), Is.Empty);
            var enemy = new GameObject("场景敌人"); _objects.Add(enemy); enemy.AddComponent<Enemy>();
            _low.EnemySpawns[0].Enabled = true; _low.EnemySpawns[0].EnemyPrefab = enemy;
            Assert.That(FlowEnemySpawnValidation.Variant(_low).Any(error => error.Contains("预制体资产")), Is.True);
        }

        [Test]
        public void SavedVariantReloadsSpawnIdsEnableFlagsAndPrefabReferences()
        {
            _assetFolder = "Assets/__FlowSpawnTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", _assetFolder.Substring("Assets/".Length));
            var saved = ScriptableObject.CreateInstance<RoomVariantDefinition>();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/EnemyTestRange.prefab");
            saved.EnemySpawns = new[] { new FlowEnemySpawnBinding { PointId = "persisted", Enabled = true, EnemyPrefab = prefab } };
            string path = _assetFolder + "/variant.asset";
            AssetDatabase.CreateAsset(saved, path); EditorUtility.SetDirty(saved); AssetDatabase.SaveAssets();
            Resources.UnloadAsset(saved); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            RoomVariantDefinition reloaded = AssetDatabase.LoadAssetAtPath<RoomVariantDefinition>(path);
            Assert.That(reloaded.EnemySpawns.Single().PointId, Is.EqualTo("persisted"));
            Assert.That(reloaded.EnemySpawns.Single().Enabled, Is.True);
            Assert.That(reloaded.EnemySpawns.Single().EnemyPrefab, Is.SameAs(prefab));
        }
    }
}
