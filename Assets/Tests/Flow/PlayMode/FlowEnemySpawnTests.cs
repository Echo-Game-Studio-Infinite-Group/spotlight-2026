using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using GameJam.Flow;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace GameJam.FlowTests
{
    public sealed class FlowEnemySpawnTests
    {
        private GameObject _root, _prefab, _alternate;
        private FlowRoomTemplate _template;
        private RoomVariantDefinition _variant;
        private FlowRoomEnemies _enemies;

        [SetUp]
        public void SetUp()
        {
            TimeManager.SetPaused(false);
            _root = new GameObject("出生点运行测试");
            _root.transform.SetPositionAndRotation(new Vector3(33000f, 30f, 33000f), Quaternion.Euler(0f, 90f, 0f));
            _template = _root.AddComponent<FlowRoomTemplate>();
            var floor = new GameObject("地面"); floor.transform.SetParent(_root.transform, false);
            floor.transform.localPosition = Vector3.down * .5f;
            floor.AddComponent<BoxCollider>().size = new Vector3(30f, 1f, 30f);
            _prefab = Prototype("近战测试敌人"); _alternate = Prototype("另一个敌人预制体");
            _variant = ScriptableObject.CreateInstance<RoomVariantDefinition>(); _variant.Id = "runtime";
        }
        private static GameObject Prototype(string name)
        {
            var root = new GameObject(name); root.SetActive(false);
            root.AddComponent<HealthComponent>(); root.AddComponent<CombatComponent>(); root.AddComponent<Enemy>();
            return root;
        }
        private FlowEnemySpawnPoint Point(string id, bool enabled = true)
        {
            var root = new GameObject(id); root.transform.SetParent(_root.transform, false);
            root.transform.localPosition = new Vector3(2f, .5f, 3f); root.transform.localRotation = Quaternion.Euler(0f, 25f, 0f);
            FlowEnemySpawnPoint point = root.AddComponent<FlowEnemySpawnPoint>(); point.Id = id;
            var entries = _variant.EnemySpawns.ToList();
            entries.Add(new FlowEnemySpawnBinding { PointId = id, Enabled = enabled, EnemyPrefab = _prefab }); _variant.EnemySpawns = entries.ToArray();
            return point;
        }
        [TearDown]
        public void TearDown()
        {
            _enemies?.Clear();
            Object.DestroyImmediate(_root); Object.DestroyImmediate(_prefab); Object.DestroyImmediate(_alternate); Object.DestroyImmediate(_variant);
            TimeManager.SetPaused(false);
        }

        [Test]
        public void PreloadDoesNotSpawnAndRepeatedActivationKeepsTheSameEnemyAtRotatedPoint()
        {
            FlowEnemySpawnPoint point = Point("one");
            _enemies = new FlowRoomEnemies(_template, _variant); _enemies.SetCurrent(false);
            Assert.That(_enemies.Instances, Is.Empty);
            _enemies.SetCurrent(true);
            GameObject spawned = _enemies.Instances["one"];
            Assert.That(Vector3.ProjectOnPlane(spawned.transform.position - point.transform.position, Vector3.up).magnitude, Is.LessThan(.01f));
            Assert.That(spawned.transform.position.y, Is.EqualTo(_root.transform.position.y + point.HeightOffset).Within(.01f));
            Assert.That(Quaternion.Angle(spawned.transform.rotation, point.transform.rotation), Is.LessThan(.01f));
            _enemies.SetCurrent(true);
            Assert.That(_enemies.Instances["one"], Is.SameAs(spawned));
            Assert.That(_root.GetComponentInChildren<ObjectPool>().BorrowedCount, Is.EqualTo(1));
        }

        [Test]
        public void DisabledBindingsAndInactiveContentPointsDoNotSpawn()
        {
            Point("disabled", false);
            Point("inactive").gameObject.SetActive(false);
            _enemies = new FlowRoomEnemies(_template, _variant); _enemies.SetCurrent(true);
            Assert.That(_enemies.Instances, Is.Empty);
        }

        [Test]
        public void DifferentVariantsAssignDifferentPrefabsToTheSamePoint()
        {
            Point("one");
            _enemies = new FlowRoomEnemies(_template, _variant); _enemies.SetCurrent(true);
            Assert.That(_enemies.Instances["one"].name, Is.EqualTo(_prefab.name));
            _enemies.Clear();
            _variant.EnemySpawns[0].EnemyPrefab = _alternate;
            _enemies = new FlowRoomEnemies(_template, _variant); _enemies.SetCurrent(true);
            Assert.That(_enemies.Instances["one"].name, Is.EqualTo(_alternate.name));
        }

        [Test]
        public void LeavingAndReturningPausesSurvivorsWithoutHealingOrRespawningDeadEnemies()
        {
            Point("alive"); FlowEnemySpawnPoint second = Point("dead"); second.transform.localPosition += Vector3.right * 5f;
            _enemies = new FlowRoomEnemies(_template, _variant); _enemies.SetCurrent(true);
            GameObject alive = _enemies.Instances["alive"], dead = _enemies.Instances["dead"];
            HealthComponent health = alive.GetComponent<HealthComponent>(); health.TakeEnvironmentalDamage(10f);
            dead.GetComponent<HealthComponent>().TakeEnvironmentalDamage(1000f);
            _enemies.SetCurrent(false);
            Assert.That(alive.activeInHierarchy, Is.False);
            _enemies.SetCurrent(true);
            Assert.That(alive.activeInHierarchy, Is.True);
            Assert.That(health.Health, Is.EqualTo(health.MaxHealth - 10f));
            Assert.That(dead.activeInHierarchy, Is.False);
            Assert.That(_root.GetComponentInChildren<ObjectPool>().BorrowedCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator EnemyDeathUsesExistingLifecycleToReturnToRoomPool()
        {
            Point("one"); _enemies = new FlowRoomEnemies(_template, _variant); _enemies.SetCurrent(true);
            GameObject instance = _enemies.Instances["one"];
            typeof(EnemyPooledLifecycle).GetField("_destroyTime", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(instance.GetComponent<EnemyPooledLifecycle>(), 0f);
            instance.GetComponent<HealthComponent>().TakeEnvironmentalDamage(1000f);
            yield return null; yield return null;
            Assert.That(instance.activeSelf, Is.False);
            ObjectPool pool = _root.GetComponentInChildren<ObjectPool>();
            Assert.That(pool.BorrowedCount, Is.Zero); Assert.That(pool.IdleCount, Is.EqualTo(1));
            _enemies.SetCurrent(true); Assert.That(instance.activeInHierarchy, Is.False);
        }

        [Test]
        public void MissingNavigationReportsThePointAndDoesNotCreateAnEnemy()
        {
            Point("nav_required"); _prefab.AddComponent<NavMeshAgent>();
            _enemies = new FlowRoomEnemies(_template, _variant);
            LogAssert.Expect(LogType.Error, new Regex("出生点 nav_required：附近没有.*导航"));
            _enemies.SetCurrent(true);
            Assert.That(_enemies.Instances, Is.Empty);
            Assert.That(_enemies.Errors.Single(), Does.Contain("nav_required"));
        }

        [Test]
        public void ClearingReturnsAllBorrowedEnemiesAndLeavesNoActiveChildren()
        {
            Point("one"); _enemies = new FlowRoomEnemies(_template, _variant); _enemies.SetCurrent(true);
            GameObject instance = _enemies.Instances["one"];
            _enemies.Clear();
            Assert.That(_enemies.Instances, Is.Empty);
            Assert.That(instance.activeInHierarchy, Is.False);
            Assert.That(_root.GetComponentInChildren<ObjectPool>().BorrowedCount, Is.Zero);
        }
    }
}
