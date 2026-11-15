using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class EnemyDropsTests
{
    private readonly List<Object> _created = new List<Object>();
    private readonly Vector3 _origin = new Vector3(14000f, 100f, 14000f);
    private Player _player;
    private VectorEnergy _energy;
    private HealthComponent _health;
    private UnityEngine.Random.State _randomState;

    [SetUp]
    public void SetUp()
    {
        _randomState = UnityEngine.Random.state;
        Create("DropTestTime").AddComponent<TimeManager>();
        GameObject root = Create("DropTestPlayer");
        root.SetActive(false);
        _health = root.AddComponent<HealthComponent>();
        PlayerMotor motor = root.AddComponent<PlayerMotor>();
        var movement = ScriptableObject.CreateInstance<MovementParams>();
        _created.Add(movement);
        Set(motor, "_params", movement);
        _energy = root.AddComponent<VectorEnergy>();
        var energyParams = ScriptableObject.CreateInstance<EnergyParams>();
        _created.Add(energyParams);
        energyParams.MaxEnergy = 100f;
        Set(_energy, "_params", energyParams);
        _player = root.AddComponent<Player>();
        root.SetActive(true);
        motor.enabled = false;
        _health.SetInvulnerableTime(0f);
    }

    [TearDown]
    public void TearDown()
    {
        TimeManager.SetPaused(false);
        foreach (ResourcePickup pickup in Object.FindObjectsOfType<ResourcePickup>(true))
            if (pickup.name.StartsWith("DropTest")) Object.DestroyImmediate(pickup.gameObject);
        for (int i = _created.Count - 1; i >= 0; i--)
            if (_created[i] != null) Object.DestroyImmediate(_created[i]);
        _created.Clear();
        UnityEngine.Random.state = _randomState;
    }

    private GameObject Create(string name)
    {
        var root = new GameObject(name);
        root.transform.position = _origin;
        _created.Add(root);
        return root;
    }

    private static void Set(object target, string field, object value)
        => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

    private ResourcePickup Pickup(ResourcePickup.ResourceKind kind, float amount = 25f)
    {
        GameObject root = Create("DropTestPickup" + kind);
        root.SetActive(false);
        ResourcePickup pickup = root.AddComponent<ResourcePickup>();
        Set(pickup, "_kind", kind);
        Set(pickup, "_amount", amount);
        // 这些几何测试在水平面上布置目标；正式预制体吸附到角色身体附近。
        Set(pickup, "_attractHeight", 0f);
        root.SetActive(true);
        return pickup;
    }

    [Test]
    public void HealthPickupClampsAndCannotBeCollectedTwice()
    {
        _health.TakeEnvironmentalDamage(10f);
        ResourcePickup pickup = Pickup(ResourcePickup.ResourceKind.Health);
        Assert.IsTrue(pickup.TryCollect(_player));
        Assert.AreEqual(100f, _health.Health);
        Assert.IsFalse(pickup.TryCollect(_player));
    }

    [Test]
    public void FullResourcesStillConsumePickupsWithoutExceedingCaps()
    {
        ResourcePickup health = Pickup(ResourcePickup.ResourceKind.Health);
        Assert.IsTrue(health.TryCollect(_player));
        Assert.IsFalse(health.gameObject.activeSelf);
        Assert.AreEqual(100f, _health.Health);
        _energy.AddEnergy(100f);
        ResourcePickup energy = Pickup(ResourcePickup.ResourceKind.Energy);
        Assert.IsTrue(energy.TryCollect(_player));
        Assert.IsFalse(energy.gameObject.activeSelf);
        Assert.AreEqual(100f, _energy.CurrentEnergy);
    }

    [Test]
    public void AttractionStartsInRangeAndCollectsOnlyAfterArrival()
    {
        _health.TakeEnvironmentalDamage(30f);
        ResourcePickup pickup = Pickup(ResourcePickup.ResourceKind.Health);
        Set(pickup, "_attractSpeed", 10f);
        pickup.transform.position = _origin + Vector3.right * 6f;
        pickup.SendMessage("OnPathPoint", _player.transform.position);
        pickup.Simulate(.1f);
        Assert.That(pickup.transform.position.x, Is.EqualTo(_origin.x + 6f).Within(.01f));

        _player.transform.position = _origin + Vector3.right * 2f;
        pickup.SendMessage("OnPathPoint", _player.transform.position);
        pickup.Simulate(.1f);
        Assert.That(pickup.transform.position.x, Is.EqualTo(_origin.x + 5f).Within(.01f));
        Assert.AreEqual(70f, _health.Health);
        pickup.Simulate(.3f);
        Assert.IsFalse(pickup.gameObject.activeSelf);
        Assert.AreEqual(95f, _health.Health);
    }

    [TestCase(ResourcePickup.ResourceKind.Health)]
    [TestCase(ResourcePickup.ResourceKind.Energy)]
    public void FullResourcesStillAttractAndConsume(ResourcePickup.ResourceKind kind)
    {
        _energy.AddEnergy(100f);
        ResourcePickup pickup = Pickup(kind);
        pickup.transform.position = _origin + Vector3.right * 4f;
        pickup.SendMessage("OnPathPoint", _player.transform.position);
        pickup.Simulate(.5f);
        Assert.IsFalse(pickup.gameObject.activeSelf);
        Assert.AreEqual(100f, _health.Health);
        Assert.AreEqual(100f, _energy.CurrentEnergy);
    }

    [Test]
    public void AttractionStopsForPauseAndZeroWorldTime()
    {
        ResourcePickup pickup = Pickup(ResourcePickup.ResourceKind.Health);
        Vector3 start = _origin + Vector3.right * 4f;
        pickup.transform.position = start;
        pickup.SendMessage("OnPathPoint", _player.transform.position);
        pickup.Simulate(0f);
        Assert.AreEqual(start, pickup.transform.position);
        TimeManager.SetPaused(true);
        pickup.Simulate(1f);
        Assert.AreEqual(start, pickup.transform.position);
        TimeManager.SetPaused(false);
        pickup.Simulate(.1f);
        Assert.Less(Vector3.Distance(pickup.transform.position, _origin), 4f);
    }

    [Test]
    public void TeleportCancelsAnAlreadyAttractedPickup()
    {
        ResourcePickup pickup = Pickup(ResourcePickup.ResourceKind.Health);
        Vector3 start = _origin + Vector3.right * 4f;
        pickup.transform.position = start;
        pickup.SendMessage("OnPathPoint", _player.transform.position);
        _player.Motor.Teleport(_origin + Vector3.right * 20f);
        pickup.Simulate(1f);
        Assert.AreEqual(start, pickup.transform.position);
        Assert.IsTrue(pickup.gameObject.activeSelf);
    }

    [Test]
    public void SettingAttractionRadiusToZeroStopsAnActiveMagnet()
    {
        ResourcePickup pickup = Pickup(ResourcePickup.ResourceKind.Health);
        Vector3 start = _origin + Vector3.right * 4f;
        pickup.transform.position = start;
        pickup.SendMessage("OnPathPoint", _player.transform.position);
        Set(pickup, "_attractRadius", 0f);
        pickup.Simulate(1f);
        Assert.AreEqual(start, pickup.transform.position);
        Assert.IsTrue(pickup.gameObject.activeSelf);
    }

    [Test]
    public void HighSpeedPassStartsAttractionWithoutInstantCollection()
    {
        _health.TakeEnvironmentalDamage(30f);
        ResourcePickup pickup = Pickup(ResourcePickup.ResourceKind.Health);
        pickup.transform.position = _origin + Vector3.right * 5f + Vector3.forward * 2f;
        Vector3 end = _origin + Vector3.right * 10f;
        _player.transform.position = end;
        var path = (Action<Vector3>)typeof(PlayerMotor).GetField("MotionPathPoint", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_player.Motor);
        path.Invoke(end);
        Assert.AreEqual(70f, _health.Health);
        float before = Vector3.Distance(pickup.transform.position, end);
        pickup.Simulate(.1f);
        Assert.Less(Vector3.Distance(pickup.transform.position, end), before);
        Assert.IsTrue(pickup.gameObject.activeSelf);
    }

    [Test]
    public void EnergyRefillClampsAndNotifiesOnlyForActualChanges()
    {
        int changed = 0;
        _energy.Changed += value => changed++;
        Assert.AreEqual(100f, _energy.AddEnergy(120f));
        Assert.AreEqual(0f, _energy.AddEnergy(10f));
        Assert.AreEqual(0f, _energy.AddEnergy(-10f));
        Assert.AreEqual(1, changed);
    }

    [Test]
    public void DeadOrPausedPlayerCannotConsumePickup()
    {
        ResourcePickup pickup = Pickup(ResourcePickup.ResourceKind.Health);
        _health.TakeEnvironmentalDamage(10f);
        TimeManager.SetPaused(true);
        Assert.IsFalse(pickup.TryCollect(_player));
        TimeManager.SetPaused(false);
        _health.TakeEnvironmentalDamage(100f);
        Assert.IsFalse(pickup.TryCollect(_player));
        Assert.IsTrue(pickup.gameObject.activeSelf);
    }

    [Test]
    public void LifetimeUsesSuppliedWorldTimeAndZeroMeansUnlimited()
    {
        ResourcePickup pickup = Pickup(ResourcePickup.ResourceKind.Health);
        Set(pickup, "_lifetime", 1f);
        pickup.Simulate(0f);
        Assert.IsTrue(pickup.gameObject.activeSelf);
        pickup.Simulate(.5f);
        Assert.IsTrue(pickup.gameObject.activeSelf);
        pickup.Simulate(.5f);
        Assert.IsFalse(pickup.gameObject.activeSelf);
        ResourcePickup unlimited = Pickup(ResourcePickup.ResourceKind.Health);
        Set(unlimited, "_lifetime", 0f);
        unlimited.Simulate(10000f);
        Assert.IsTrue(unlimited.gameObject.activeSelf);
    }

    [Test]
    public void HighSpeedPathCollectsEvenWhenBothEndpointsAreOutsideRadius()
    {
        _health.TakeEnvironmentalDamage(30f);
        ResourcePickup pickup = Pickup(ResourcePickup.ResourceKind.Health);
        pickup.transform.position = _origin + Vector3.right * 5f;
        Vector3 end = _origin + Vector3.right * 10f;
        _player.transform.position = end;
        var path = (Action<Vector3>)typeof(PlayerMotor).GetField("MotionPathPoint", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_player.Motor);
        path.Invoke(end);
        Assert.AreEqual(95f, _health.Health);
        Assert.IsFalse(pickup.gameObject.activeSelf);
    }

    [Test]
    public void TeleportDoesNotCollectAlongSkippedPath()
    {
        _health.TakeEnvironmentalDamage(30f);
        ResourcePickup pickup = Pickup(ResourcePickup.ResourceKind.Health);
        pickup.transform.position = _origin + Vector3.right * 5f;
        _player.Motor.Teleport(_origin + Vector3.right * 10f);
        pickup.SendMessage("Update");
        Assert.AreEqual(70f, _health.Health);
        Assert.IsTrue(pickup.gameObject.activeSelf);
    }

    [TestCase(0f, 1f, 0, ResourcePickup.ResourceKind.Health)]
    [TestCase(1f, 1f, 1, ResourcePickup.ResourceKind.Health)]
    [TestCase(1f, 0f, 1, ResourcePickup.ResourceKind.Energy)]
    public void DeathProbabilityEndpointsSelectAtMostOnePickup(float chance, float share, int expected, ResourcePickup.ResourceKind kind)
    {
        ResourcePickup health = Pickup(ResourcePickup.ResourceKind.Health);
        ResourcePickup energy = Pickup(ResourcePickup.ResourceKind.Energy);
        GameObject enemy = Create("DropTestEnemy");
        enemy.SetActive(false);
        var drops = enemy.AddComponent<EnemyDrops>();
        Set(drops, "_dropChance", chance);
        Set(drops, "_healthShare", share);
        Set(drops, "_healthPickup", health);
        Set(drops, "_energyPickup", energy);
        enemy.SetActive(true);
        HealthComponent hp = enemy.GetComponent<HealthComponent>();
        hp.TakeEnvironmentalDamage(100f);
        hp.TakeEnvironmentalDamage(100f);
        ResourcePickup[] spawned = Object.FindObjectsOfType<ResourcePickup>().Where(p => p != health && p != energy && p.name.StartsWith("DropTest")).ToArray();
        Assert.AreEqual(expected, spawned.Length);
        if (expected == 1)
        {
            Assert.IsTrue(spawned[0].name.Contains(kind.ToString()));
            Assert.AreEqual(enemy.scene, spawned[0].gameObject.scene);
            enemy.SetActive(false);
            Assert.IsTrue(spawned[0].gameObject.activeSelf);
        }
    }

    [Test]
    public void ReusedEnemyCanDropAgainAndReviveAlsoRearmsDrop()
    {
        ResourcePickup template = Pickup(ResourcePickup.ResourceKind.Health);
        GameObject enemy = Create("DropTestEnemy");
        enemy.SetActive(false);
        var drops = enemy.AddComponent<EnemyDrops>();
        Set(drops, "_dropChance", 1f);
        Set(drops, "_healthShare", 1f);
        Set(drops, "_healthPickup", template);
        enemy.SetActive(true);
        HealthComponent hp = enemy.GetComponent<HealthComponent>();
        hp.TakeEnvironmentalDamage(100f);
        enemy.SetActive(false);
        enemy.SetActive(true);
        hp.Reset();
        hp.TakeEnvironmentalDamage(100f);
        hp.Reset();
        hp.TakeEnvironmentalDamage(100f);
        Assert.AreEqual(4, Object.FindObjectsOfType<ResourcePickup>().Count(p => p.name.StartsWith("DropTest")));
    }

    [Test]
    public void RealObjectPoolResetsHealthAndAllowsAnotherDrop()
    {
        ResourcePickup template = Pickup(ResourcePickup.ResourceKind.Health);
        GameObject enemyTemplate = Create("DropTestPooledEnemy");
        enemyTemplate.SetActive(false);
        EnemyDrops drops = enemyTemplate.AddComponent<EnemyDrops>();
        enemyTemplate.AddComponent<EnemyPooledLifecycle>();
        Set(drops, "_dropChance", 1f);
        Set(drops, "_healthShare", 1f);
        Set(drops, "_healthPickup", template);
        enemyTemplate.SetActive(true);
        ObjectPool pool = Create("DropTestPool").AddComponent<ObjectPool>();

        GameObject first = pool.Spawn(enemyTemplate, _origin + Vector3.right * 10f, Quaternion.identity);
        _created.Add(first);
        first.GetComponent<HealthComponent>().TakeEnvironmentalDamage(100f);
        Assert.IsTrue(pool.Return(first));
        GameObject reused = pool.Spawn(enemyTemplate, _origin + Vector3.right * 20f, Quaternion.identity);
        Assert.AreSame(first, reused);
        HealthComponent hp = reused.GetComponent<HealthComponent>();
        Assert.IsTrue(hp.IsAlive);
        hp.TakeEnvironmentalDamage(100f);
        Assert.AreEqual(3, Object.FindObjectsOfType<ResourcePickup>().Count(p => p.name.StartsWith("DropTest")));
    }

    [UnityTest]
    public IEnumerator DropBelongsToEnemyRoomAndIsDestroyedOnUnload()
    {
        ResourcePickup template = Pickup(ResourcePickup.ResourceKind.Health);
        Scene room = SceneManager.CreateScene("DropTestRoom");
        try
        {
            GameObject enemy = Create("DropTestRoomEnemy");
            enemy.SetActive(false);
            SceneManager.MoveGameObjectToScene(enemy, room);
            EnemyDrops drops = enemy.AddComponent<EnemyDrops>();
            Set(drops, "_dropChance", 1f);
            Set(drops, "_healthShare", 1f);
            Set(drops, "_healthPickup", template);
            enemy.SetActive(true);
            enemy.GetComponent<HealthComponent>().TakeEnvironmentalDamage(100f);
            ResourcePickup spawned = Object.FindObjectsOfType<ResourcePickup>().Single(p => p != template && p.name.StartsWith("DropTest"));
            Assert.AreEqual(room, spawned.gameObject.scene);
            Assert.IsNull(spawned.transform.parent);
            yield return SceneManager.UnloadSceneAsync(room);
            Assert.IsTrue(spawned == null);
        }
        finally
        {
            if (room.IsValid() && room.isLoaded) SceneManager.UnloadSceneAsync(room);
        }
    }
}
