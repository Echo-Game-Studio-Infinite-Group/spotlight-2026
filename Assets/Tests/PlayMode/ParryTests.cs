#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GameJam.Actions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class ParryTests
{
    private readonly List<Object> _created = new List<Object>();
    private readonly Vector3 _origin = new Vector3(11000f, 40f, 11000f);
    private PlayerCombat _player;
    private HealthComponent _health;
    private ActionDefinition _action;
    private int _successes;
    private IReadOnlyList<ParryContact> _last;
    private long _instance;

    [SetUp]
    public void SetUp()
    {
        _successes = 0; _last = null; _instance = 0;
        Create("ParryClock", Vector3.zero).AddComponent<TimeManager>();
        TimeManager.ClearSlowMotion();
        GameObject root = Create("ParryPlayer", _origin);
        _health = root.AddComponent<HealthComponent>();
        _health.SetInvulnerableTime(0f);
        _player = root.AddComponent<PlayerCombat>();
        _player.SetSequenceDriven(true);
        _player.ParrySucceeded += contacts => { _successes++; _last = contacts; };
        _action = ScriptableObject.CreateInstance<ActionDefinition>(); _created.Add(_action);
        _action.ActionId = "parry_test";
        _action.Timeline.Add(new ActionSegment { SegmentId = "startup", DurationFrames = 22 });
        _action.Timeline.Add(new ActionSegment { SegmentId = "active", DurationFrames = 16 });
        _action.Timeline.Add(new ActionSegment { SegmentId = "recovery", DurationFrames = 10 });
        _action.Combat.Enabled = true;
        _action.Combat.ParryVolumes.Add(new ActionHitVolume {
            Center = new Vector3(0f, .9f, 1f), Size = new Vector3(1.5f, 1.5f, 1f),
            Start = new ActionFrameAnchor { SegmentId = "active" },
            End = new ActionFrameAnchor { SegmentId = "active", RelativeTo = ActionFrameAnchor.Boundary.SegmentEnd }
        });
        Begin(0);
    }

    [TearDown]
    public void TearDown()
    {
        TimeManager.ClearSlowMotion(); TimeManager.SetPaused(false);
        for (int i = _created.Count - 1; i >= 0; i--)
            if (_created[i] != null) Object.DestroyImmediate(_created[i]);
        _created.Clear();
    }

    private GameObject Create(string name, Vector3 position)
    {
        var root = new GameObject(name); root.transform.position = position; _created.Add(root); return root;
    }
    private CombatComponent Enemy(float z = 1.5f, bool attack = true)
    {
        GameObject root = Create("ParryEnemy", _origin + Vector3.forward * z);
        root.AddComponent<HealthComponent>();
        var combat = root.AddComponent<CombatComponent>(); combat.Camp = CampType.Enemy; combat.WindowSeconds = 0f;
        if (attack) Assert.IsTrue(combat.TryBeginAttack(TimeManager.WorldTime));
        return combat;
    }
    private ActionExecutionState State(double frame)
        => new ActionExecutionState(_action, _instance, frame < 22 ? 0 : frame < 38 ? 1 : 2, frame, frame < 22 ? 0 : frame < 38 ? 22 : 38, default);
    private void Begin(double frame)
    {
        _instance++; _player.BeginSequenceAction(State(frame));
    }
    private void Sample(double frame) => _player.SampleSequenceBoundary(State(frame));
    private AttackDamageResult Hit(CombatComponent enemy)
        => enemy.TryHit(_player.GetComponent<CharacterController>(), _health,
            new AttackDamageRequest(25f, _origin, Vector3.back, 0, 0, enemy.SourceType, enemy.ParryKind));
    private Projectile Bullet(Vector3 position)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Bullet.prefab");
        Assert.IsNotNull(prefab);
        GameObject root = Object.Instantiate(prefab, position, Quaternion.identity); _created.Add(root);
        var projectile = root.GetComponent<Projectile>(); projectile.Launch(Vector3.back, null); return projectile;
    }

    [Test]
    public void StartupHasNoBodyGuardAndDoesNotConsumeIndependentCooldown()
    {
        CombatComponent enemy = Enemy();
        Assert.IsFalse(_player.GetComponent<CombatComponent>().ReceiveAction(InputBuffer.Action.Parry, TimeManager.UnscaledTime));
        Hit(enemy);
        Assert.AreEqual(75f, _health.Health); Assert.AreEqual(0, _successes);
    }
    [Test]
    public void BladeFramesParryDuringEnemyStartupAndCancelBothAttacks()
    {
        CombatComponent enemy = Enemy();
        Sample(22);
        Assert.AreEqual(1, _successes); Assert.AreEqual(100f, _health.Health);
        Assert.IsFalse(enemy.IsAttacking(TimeManager.WorldTime)); Assert.IsFalse(_player.IsAttacking);
        Assert.IsTrue(_health.IsInvulnerable); Assert.IsTrue(TimeManager.InHitStop);
        Assert.AreEqual(0f, TimeManager.GlobalRate); Assert.AreEqual(0f, TimeManager.PlayerFixedDeltaTime);
        Assert.AreEqual(AttackSourceType.Melee, _last[0].SourceType);
    }
    [Test]
    public void BodyTriggerFallbackUsesTheSameGeometryBeforeDamage()
    {
        Sample(22); CombatComponent enemy = Enemy(); Hit(enemy);
        Assert.AreEqual(1, _successes); Assert.AreEqual(100f, _health.Health);
    }
    [Test]
    public void ActiveFrameBehindPlayerCannotParry()
    {
        Sample(22); CombatComponent enemy = Enemy(-2f); Hit(enemy);
        Assert.AreEqual(0, _successes); Assert.AreEqual(75f, _health.Health);
    }
    [Test]
    public void RecoveryAndIdleEnemyHaveNoClash()
    {
        CombatComponent idle = Enemy(1.5f, false); Sample(22); Assert.AreEqual(0, _successes);
        CombatComponent attack = Enemy(); Sample(38); Hit(attack);
        Assert.AreEqual(0, _successes); Assert.AreEqual(75f, _health.Health);
    }
    [Test]
    public void EndedPlayerActionCannotLeaveParryGeometryBehind()
    {
        Sample(22); _player.EndSequenceAction(_instance);
        CombatComponent enemy = Enemy(); Hit(enemy);
        Assert.AreEqual(0, _successes); Assert.AreEqual(75f, _health.Health);
    }
    [Test]
    public void DisadvantageInterruptsSourceAndUsesTheSameTimeStop()
    {
        CombatComponent enemy = Enemy(); enemy.ParryKind = AttackParryKind.Disadvantage; Sample(22);
        Assert.AreEqual(AttackParryKind.Disadvantage, _last[0].Kind);
        Assert.IsFalse(enemy.IsAttacking(TimeManager.WorldTime)); Assert.IsTrue(TimeManager.IsParryStopped);
    }
    [Test]
    public void BatchConsumesTwoSourcesButNotifiesPlayerOnce()
    {
        CombatComponent first = Enemy(), second = Enemy(1.7f); Sample(22);
        Assert.AreEqual(1, _successes); Assert.AreEqual(2, _last.Count);
        Assert.IsFalse(first.IsAttacking(TimeManager.WorldTime)); Assert.IsFalse(second.IsAttacking(TimeManager.WorldTime));
        Hit(first); Hit(second); Assert.AreEqual(1, _successes);
    }
    [Test]
    public void AttackTokenRejectsDuplicateAndPreviousGeneration()
    {
        CombatComponent enemy = Enemy(); enemy.Cooldown = 0f;
        var old = new ParryContact(enemy, _origin, Vector3.back);
        Assert.IsTrue(enemy.ConsumeParry(old)); Assert.IsFalse(enemy.ConsumeParry(old));
        enemy.ResetAttackState(); enemy.TryBeginAttack(TimeManager.WorldTime);
        Assert.AreNotEqual(old.AttackId, enemy.AttackId); Assert.IsFalse(enemy.ConsumeParry(old));
    }
    [Test]
    public void ConsecutiveActionsDoNotHaveAnExtraParryCooldown()
    {
        Enemy(); Sample(22); TimeManager.ClearSlowMotion();
        Begin(22); Enemy(); Sample(22);
        Assert.AreEqual(2, _successes);
    }
    [Test]
    public void CoverPreventsParryThroughWall()
    {
        Enemy(1.5f);
        GameObject wall = Create("Cover", _origin + new Vector3(0f, .9f, .6f));
        wall.AddComponent<BoxCollider>().size = new Vector3(4f, 4f, .1f);
        Physics.SyncTransforms(); Sample(22); Assert.AreEqual(0, _successes);
    }
    [Test]
    public void RealBulletTriggerIsProjectileAndRecyclesWithoutLandedDamageEvent()
    {
        Sample(22); Projectile bullet = Bullet(_origin + new Vector3(0f, .9f, 1f));
        CombatComponent combat = bullet.GetComponent<CombatComponent>(); int landed = 0;
        combat.Landed += (target, point, direction, damage) => landed++;
        Hitbox hitbox = bullet.GetComponent<Hitbox>();
        typeof(Hitbox).GetMethod("OnTriggerEnter", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(hitbox, new object[] { _player.GetComponent<CharacterController>() });
        Assert.AreEqual(1, _successes); Assert.AreEqual(0, landed); Assert.AreEqual(100f, _health.Health);
        Assert.AreEqual(AttackSourceType.Projectile, _last[0].SourceType);
        Assert.IsFalse(bullet.gameObject.activeSelf);
        Assert.IsFalse(TimeManager.InHitStop);
        Assert.AreEqual(1f, TimeManager.GlobalRate);
        Assert.AreEqual(1f, TimeManager.WorldRate);
        Assert.AreEqual(1f, TimeManager.PlayerRate);
    }
    [TestCase(false)]
    [TestCase(true)]
    public void MixedBulletAndMeleeBatchStillStopsTime(bool bulletFirst)
    {
        CombatComponent melee = Enemy();
        CombatComponent bullet = Bullet(_origin + new Vector3(0f, .9f, 1f)).GetComponent<CombatComponent>();
        var meleeContact = new ParryContact(melee, _origin, Vector3.back);
        var bulletContact = new ParryContact(bullet, _origin, Vector3.back);
        _player.ReceiveParry(bulletFirst ? new[] { bulletContact, meleeContact } : new[] { meleeContact, bulletContact });
        Assert.IsTrue(TimeManager.InHitStop);
        Assert.AreEqual(0f, TimeManager.GlobalRate);
    }
    [Test]
    public void BatchParriesTwoBulletsWithoutStoppingTime()
    {
        Projectile first = Bullet(_origin + new Vector3(0f, .9f, 1f));
        Projectile second = Bullet(_origin + new Vector3(.2f, .9f, 1f));
        Sample(22);
        Assert.AreEqual(1, _successes);
        Assert.AreEqual(2, _last.Count);
        Assert.IsFalse(first.gameObject.activeSelf);
        Assert.IsFalse(second.gameObject.activeSelf);
        Assert.IsFalse(TimeManager.InHitStop);
        Assert.AreEqual(1f, TimeManager.GlobalRate);
    }
    [Test]
    public void BulletParryPreservesExistingSlowMotion()
    {
        Sample(22);
        TimeManager.ParryStop(1f, .25f);
        Projectile bullet = Bullet(_origin + new Vector3(0f, .9f, 1f));
        bullet.Simulate(.01f);
        Assert.AreEqual(1, _successes);
        Assert.AreEqual(.25f, TimeManager.GlobalRate);
        Assert.AreEqual(.25f, TimeManager.WorldRate);
        Assert.AreEqual(.25f, TimeManager.PlayerRate);
    }
    [TestCase(false)]
    [TestCase(true)]
    public void ParryMetalSparksEmitAndReplayFromPool(bool projectile)
    {
        CombatSparksInternal.Kind kind = projectile ? CombatSparksInternal.Kind.ParryProjectile : CombatSparksInternal.Kind.Parry;
        foreach (SparkInstance candidate in Object.FindObjectsOfType<SparkInstance>())
        {
            if (candidate.Kind != kind) continue;
            foreach (ParticleSystem layer in candidate.GetComponentsInChildren<ParticleSystem>())
                layer.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            candidate.gameObject.SetActive(false);
        }
        if (projectile) CombatSparks.PlayParryProjectile(_origin, Vector3.forward);
        else CombatSparks.PlayParry(_origin, Vector3.forward);
        SparkInstance spark = null;
        foreach (SparkInstance candidate in Object.FindObjectsOfType<SparkInstance>())
            if (candidate.Kind == kind && candidate.transform.position == _origin) { spark = candidate; break; }
        Assert.IsNotNull(spark);
        Transform metal = spark.transform.Find("MetalSparks");
        Assert.IsNotNull(metal);
        var particles = metal.GetComponent<ParticleSystem>();
        Assert.IsTrue(particles.main.useUnscaledTime);
        Assert.IsFalse(particles.main.loop);
        particles.Simulate(.02f, true, false, false);
        Assert.Greater(particles.particleCount, 0);

        foreach (ParticleSystem layer in spark.GetComponentsInChildren<ParticleSystem>())
            layer.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        spark.gameObject.SetActive(false);
        // 招架点可能在近身碰撞体内，反馈不能在散出角色轮廓前被碰撞耗尽。
        GameObject actor = Create("ParrySparkActor", _origin);
        actor.layer = LayerMask.NameToLayer("Enemy");
        actor.AddComponent<BoxCollider>().size = Vector3.one * 2f;
        Physics.SyncTransforms();
        // 模拟池回收后的复播，避免只验证首次创建成功。
        if (projectile) CombatSparks.PlayParryProjectile(_origin, Vector3.forward);
        else CombatSparks.PlayParry(_origin, Vector3.forward);
        Assert.IsTrue(spark.gameObject.activeSelf);
        particles.Simulate(.2f, true, false, false);
        Assert.Greater(particles.particleCount, 0);
    }
    [Test]
    public void FastBulletCrossesWholeParryVolumeWithinOneTickAndIsConsumed()
    {
        Sample(22); Projectile bullet = Bullet(_origin + new Vector3(0f, .9f, 4f));
        bullet.Launch(Vector3.back, null, 60f);
        bullet.Simulate(1f / 60f);
        Assert.AreEqual(1, _successes); Assert.IsFalse(bullet.gameObject.activeSelf);
    }
    [Test]
    public void BulletPoolRelaunchHasFreshTokenAndCanBeParriedAgain()
    {
        Sample(22); Projectile bullet = Bullet(_origin + new Vector3(0f, .9f, 1f));
        long token = bullet.GetComponent<CombatComponent>().AttackId;
        bullet.Simulate(.01f); Assert.AreEqual(1, _successes);
        TimeManager.ClearSlowMotion(); Begin(22); Sample(22);
        bullet.gameObject.SetActive(true); bullet.transform.position = _origin + new Vector3(0f, .9f, 1f);
        bullet.Launch(Vector3.back, null);
        Assert.AreNotEqual(token, bullet.GetComponent<CombatComponent>().AttackId);
        bullet.Simulate(.01f); Assert.AreEqual(2, _successes);
    }
    [Test]
    public void BulletTargetIsWiderThanDamageColliderInActualPrefabScale()
    {
        Projectile bullet = Bullet(_origin + Vector3.forward * 4f);
        var combat = bullet.GetComponent<CombatComponent>(); Assert.IsTrue(combat.TryGetParryTarget(out AttackVolumePose pose));
        Physics.SyncTransforms();
        Assert.Greater(pose.Extents.x, bullet.GetComponent<BoxCollider>().bounds.extents.x);
    }
    [Test]
    public void SlowMotionDoesNotCloseFrameOwnedWindowByRealTime()
    {
        Sample(22); TimeManager.Apply(TimeManager.TimeLayer.Player, .1f, 1f, this);
        Enemy(); Sample(22.1); Assert.AreEqual(1, _successes);
    }
    [UnityTest]
    public IEnumerator StopExpiresOnUnscaledClockAndZeroDurationDoesNotFreeze()
    {
        Enemy(); Sample(22); Assert.IsTrue(TimeManager.IsParryStopped);
        yield return new WaitForSecondsRealtime(.12f);
        Assert.AreEqual(1f, TimeManager.GlobalRate); Assert.IsFalse(TimeManager.InHitStop);
        TimeManager.ParryStop(0f); Assert.AreEqual(1f, TimeManager.PlayerRate);
    }
    [Test]
    public void RelativeSweepHandlesBothBoxesMovingAcrossEachOther()
    {
        var a0 = new AttackVolumePose(new Vector3(-3f, 0f, 0f), Vector3.one * .1f, Quaternion.identity, Vector3.zero);
        var a1 = new AttackVolumePose(new Vector3(3f, 0f, 0f), Vector3.one * .1f, Quaternion.identity, Vector3.zero);
        var b0 = new AttackVolumePose(new Vector3(3f, 0f, 0f), Vector3.one * .1f, Quaternion.identity, Vector3.zero);
        var b1 = new AttackVolumePose(new Vector3(-3f, 0f, 0f), Vector3.one * .1f, Quaternion.identity, Vector3.zero);
        Assert.IsFalse(ParryGeometry.Overlaps(a0, b0)); Assert.IsFalse(ParryGeometry.Overlaps(a1, b1));
        Assert.IsTrue(ParryGeometry.Swept(a0, a1, b0, b1, .05f, out _));
    }
    [Test]
    public void RealBeamParryTerminatesBeamAndStartsCooldown()
    {
        Sample(22);
        GameObject root = Create("ParryLaser", _origin + new Vector3(0f, .9f, 5f)); root.SetActive(false);
        root.transform.rotation = Quaternion.LookRotation(Vector3.back);
        LaserAttack laser = root.AddComponent<LaserAttack>();
        typeof(LaserAttack).GetField("_chargeSeconds", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(laser, 0f);
        typeof(LaserAttack).GetField("_convergenceSeconds", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(laser, 0f);
        root.SetActive(true); Physics.SyncTransforms(); laser.Simulate(.01f, _health);
        Assert.AreEqual(1, _successes); Assert.AreEqual(100f, _health.Health);
        Assert.AreEqual(AttackSourceType.Beam, _last[0].SourceType);
        Assert.AreEqual(LaserAttack.AttackPhase.Cooldown, laser.Phase);
        laser.Simulate(0f, _health); Assert.AreEqual(1, _successes);
    }
    [Test]
    public void ActualBulletReturnsToOwningPoolAndReusesWithFreshAttackIdentity()
    {
        ObjectPool pool = Create("ParryBulletPool", _origin).AddComponent<ObjectPool>();
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Bullet.prefab");
        Sample(22);
        GameObject first = pool.Spawn(prefab, _origin + new Vector3(0f, .9f, 1f), Quaternion.identity);
        Projectile bullet = first.GetComponent<Projectile>(); bullet.Launch(Vector3.back, null);
        long token = first.GetComponent<CombatComponent>().AttackId;
        bullet.Simulate(.01f);
        Assert.AreEqual(0, pool.BorrowedCount); Assert.AreEqual(1, pool.IdleCount);
        TimeManager.ClearSlowMotion(); Begin(22); Sample(22);
        GameObject second = pool.Spawn(prefab, _origin + new Vector3(0f, .9f, 1f), Quaternion.identity);
        Assert.AreSame(first, second); bullet.Launch(Vector3.back, null);
        Assert.AreNotEqual(token, second.GetComponent<CombatComponent>().AttackId);
        bullet.Simulate(.01f); Assert.AreEqual(2, _successes);
        Assert.AreEqual(0, pool.BorrowedCount);
    }
    [Test]
    public void RealEnemyPrefabInterruptsAnimationAndStaleEventsCannotReopenDamage()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/EnemyTest.prefab");
        GameObject root = Object.Instantiate(prefab, _origin + Vector3.forward * 1.5f, Quaternion.identity); _created.Add(root);
        EnemyBase enemy = root.GetComponent<EnemyBase>(); enemy.SetChasePlayer(false);
        CombatComponent source = root.GetComponent<CombatComponent>();
        EnemyAnimation animation = root.GetComponentInChildren<EnemyAnimation>();
        Assert.IsTrue(source.TryBeginAttack(TimeManager.WorldTime)); animation.PlayAttack();
        Sample(22); Assert.AreEqual(1, _successes); Assert.IsFalse(enemy.IsAttacking);
        animation.EnableHitbox(); Assert.IsFalse(source.Hitbox.GetComponent<BoxCollider>().enabled);
        TimeManager.ClearSlowMotion();
        typeof(EnemyAnimation).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(animation, null);
        animation.GetComponent<Animator>().Update(.1f);
        Assert.IsTrue(animation.IsHurting, "恢复后应进入真正的受击硬直状态");
    }
    [Test]
    public void LaserChargeCanBeInterruptedBeforeBeamIsCreated()
    {
        GameObject root = Create("ChargingParryLaser", _origin + Vector3.forward * 1.5f); root.SetActive(false);
        LaserAttack laser = root.AddComponent<LaserAttack>(); root.SetActive(true);
        laser.Simulate(.01f, _health); Assert.IsTrue(laser.IsCharging);
        Sample(22); Assert.AreEqual(1, _successes);
        Assert.AreEqual(LaserAttack.AttackPhase.Cooldown, laser.Phase);
    }
    [Test]
    public void RangeChargeCancelsBeforeAnyBulletCanSpawn()
    {
        GameObject root = Create("ChargingParryRange", _origin + Vector3.forward * 1.5f); root.SetActive(false);
        RangeComponent ranged = root.AddComponent<RangeComponent>();
        typeof(RangeComponent).GetField("_projectilePrefab", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(ranged, AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Bullet.prefab").GetComponent<Projectile>());
        root.SetActive(true); Assert.IsFalse(ranged.TryFire(TimeManager.WorldTime, _player.transform, _health));
        Sample(22); Assert.AreEqual(1, _successes);
        Assert.IsFalse(ranged.TryFire(TimeManager.WorldTime, _player.transform, _health));
        Assert.AreEqual(0, root.GetComponent<ObjectPool>().BorrowedCount);
    }
    [Test]
    public void RelativeSweepDoesNotInventContactForParallelMotion()
    {
        var a0 = new AttackVolumePose(Vector3.zero, Vector3.one * .1f, Quaternion.identity, Vector3.zero);
        var a1 = new AttackVolumePose(Vector3.right * 5f, Vector3.one * .1f, Quaternion.identity, Vector3.zero);
        var b0 = new AttackVolumePose(Vector3.right, Vector3.one * .1f, Quaternion.identity, Vector3.zero);
        var b1 = new AttackVolumePose(Vector3.right * 6f, Vector3.one * .1f, Quaternion.identity, Vector3.zero);
        Assert.IsFalse(ParryGeometry.Swept(a0, a1, b0, b1, .05f, out _));
    }

    [Test]
    public void MovingPlayerAndRealBulletInParallelDoNotClashFromSeparatePathSweeps()
    {
        _action.Combat.ParryVolumes[0].Size = Vector3.one * .2f;
        Begin(22); Sample(22);
        Projectile bullet = Bullet(_origin + new Vector3(1f, .9f, 1f)); bullet.Launch(Vector3.right, null, 30f);
        float distance = 300f * TimeManager.WorldFixedDeltaTime;
        _player.transform.position += Vector3.right * distance;
        _player.SampleSequenceHitbox(State(22), State(23), new[] { _player.transform.position });
        Sample(23); bullet.Simulate(TimeManager.WorldFixedDeltaTime);
        Assert.AreEqual(0, _successes); Assert.IsTrue(bullet.gameObject.activeSelf);
    }

}
#endif
