using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class HeavyLaserTests
{
    private readonly List<GameObject> _created = new List<GameObject>();
    private LaserAttack _laser;
    private HealthComponent _self, _target;
    private CombatComponent _combat;
    private LineRenderer _line;
    private Vector3 _origin;

    [SetUp]
    public void SetUp()
    {
        _origin = new Vector3(9000f, 40f, 9000f);
        GameObject root = Create("HeavyLaser", _origin);
        root.SetActive(false);
        _laser = root.AddComponent<LaserAttack>();
        _self = root.GetComponent<HealthComponent>();
        _combat = root.GetComponent<CombatComponent>();
        _combat.Damage = 40f;
        _combat.Cooldown = .4f;
        _line = Create("TemporaryBeam", _origin).AddComponent<LineRenderer>();
        _line.transform.SetParent(root.transform, true);
        Set(_laser, "_beam", _line);
        Set(_laser, "_chargeSeconds", .2f);
        Set(_laser, "_convergenceSeconds", 0f);
        Set(_laser, "_beamSeconds", .5f);
        Set(_laser, "_range", 20f);
        Set(_laser, "_hitRadius", .1f);
        root.SetActive(true);
        _self.SetInvulnerableTime(0f);
        GameObject target = Create("LaserPlayer", _origin + Vector3.forward * 10f);
        target.AddComponent<BoxCollider>();
        _target = target.AddComponent<HealthComponent>();
        _target.SetInvulnerableTime(0f);
        Physics.SyncTransforms();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (GameObject value in _created)
            if (value != null) Object.DestroyImmediate(value);
        _created.Clear();
    }

    private GameObject Create(string name, Vector3 position)
    {
        var value = new GameObject(name);
        value.transform.position = position;
        _created.Add(value);
        return value;
    }

    private static void Set(object owner, string name, object value)
        => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(owner, value);

    private void Fire()
    {
        _laser.Simulate(.1f, _target);
        Assert.IsTrue(_laser.IsCharging);
        _laser.Simulate(.11f, _target);
        Assert.IsTrue(_laser.IsFiring);
    }

    [Test]
    public void ConvergenceFreezesAimAndLetsTargetEvadeBeforeFiring()
    {
        Set(_laser, "_convergenceSeconds", .5f);
        _laser.Simulate(.21f, _target);
        Assert.IsTrue(_laser.IsConverging);
        Assert.AreEqual(0f, _laser.ConvergenceProgress);
        Assert.AreEqual(100f, _target.Health);
        Assert.IsFalse(_line.enabled);
        Vector3 origin = _laser.BeamStart;
        Vector3 direction = _laser.BeamDirection;
        _laser.transform.SetPositionAndRotation(_origin + Vector3.right, Quaternion.Euler(0f, 45f, 0f));
        _target.transform.position += Vector3.right * 5f;
        Physics.SyncTransforms();
        _laser.Simulate(.25f, _target);
        Assert.IsTrue(_laser.IsConverging);
        Assert.That(_laser.ConvergenceProgress, Is.EqualTo(.5f).Within(.001f));
        _laser.Simulate(0f, _target);
        Assert.That(_laser.Remaining, Is.EqualTo(.25f).Within(.001f));
        Assert.AreEqual(origin, _laser.BeamStart);
        Assert.AreEqual(direction, _laser.BeamDirection);
        Assert.AreEqual(100f, _target.Health);
        _laser.Simulate(.26f, _target);
        Assert.IsTrue(_laser.IsFiring);
        Assert.AreEqual(origin, _laser.BeamStart);
        Assert.AreEqual(direction, _laser.BeamDirection);
        Assert.AreEqual(100f, _target.Health);
    }

    [Test]
    public void ChargePreviewUsesCoverSurfaceWithoutDamageAndFreezesWithLogic()
    {
        GameObject wall = Create("PreviewCover", _origin + Vector3.forward * 5f);
        wall.AddComponent<BoxCollider>().size = new Vector3(10f, 10f, 1f);
        Physics.SyncTransforms();
        _laser.Simulate(.1f, _target);
        Assert.IsTrue(_laser.HasBeamHit);
        Assert.That(_laser.ChargeProgress, Is.EqualTo(.5f).Within(.001f));
        Assert.That(_laser.ImpactPoint.z, Is.EqualTo(_origin.z + 4.5f).Within(.01f));
        Assert.That(Vector3.Dot(_laser.ImpactNormal, Vector3.back), Is.GreaterThan(.99f));
        Assert.AreEqual(100f, _target.Health);
        Assert.IsFalse(_line.enabled);
        Vector3 end = _laser.BeamEnd;
        wall.transform.position += Vector3.forward;
        Physics.SyncTransforms();
        _laser.Simulate(0f, _target);
        Assert.AreEqual(end, _laser.BeamEnd);
        Assert.That(_laser.ChargeProgress, Is.EqualTo(.5f).Within(.001f));
        _laser.Simulate(.11f, _target);
        Assert.IsTrue(_laser.IsFiring);
        Assert.That(_laser.ImpactPoint.z, Is.EqualTo(_origin.z + 5.5f).Within(.01f));
        Assert.AreEqual(0f, _laser.FireProgress);
        Assert.AreEqual(100f, _target.Health);
        _laser.enabled = false;
        Assert.IsFalse(_laser.HasBeamHit);
    }

    [Test]
    public void OneAttackOnlyDamagesOnceAndCooldownAllowsNextAttack()
    {
        Fire();
        Assert.AreEqual(60f, _target.Health);
        for (int i = 0; i < 20; i++) _laser.Simulate(.01f, _target);
        Assert.AreEqual(60f, _target.Health);
        _laser.Simulate(.4f, _target);
        Assert.IsFalse(_line.enabled);
        Assert.AreEqual(LaserAttack.AttackPhase.Cooldown, _laser.Phase);
        _laser.Simulate(.41f, _target);
        Fire();
        Assert.AreEqual(20f, _target.Health);
    }

    [Test]
    public void BeamLocksOriginAndDirectionEvenWhenMuzzleAndTargetMove()
    {
        _target.transform.position += Vector3.right * 5f;
        Physics.SyncTransforms();
        Fire();
        Vector3 origin = _laser.BeamStart;
        Vector3 direction = _laser.BeamDirection;
        _laser.transform.SetPositionAndRotation(_origin + Vector3.right * 2f, Quaternion.Euler(0f, 45f, 0f));
        _target.transform.position += Vector3.right;
        Physics.SyncTransforms();
        _laser.Simulate(.01f, _target);
        Assert.AreEqual(origin, _laser.BeamStart);
        Assert.AreEqual(direction, _laser.BeamDirection);
        Assert.AreEqual(100f, _target.Health);
    }

    [Test]
    public void NearestWallStopsBeamAndProtectsPlayer()
    {
        GameObject wall = Create("LaserCover", _origin + Vector3.forward * 5f);
        wall.AddComponent<BoxCollider>().size = new Vector3(10f, 10f, 1f);
        Physics.SyncTransforms();
        Fire();
        Assert.Less(_laser.BeamEnd.z, wall.transform.position.z);
        Assert.AreEqual(100f, _target.Health);
    }

    [Test]
    public void NoHitEndsAtMaximumRangeAndTriggerDoesNotBlock()
    {
        _target.transform.position += Vector3.right * 5f;
        GameObject trigger = Create("ParryTrigger", _origin + Vector3.forward * 3f);
        trigger.AddComponent<BoxCollider>().isTrigger = true;
        Physics.SyncTransforms();
        Fire();
        Assert.That(Vector3.Distance(_laser.BeamStart, _laser.BeamEnd), Is.EqualTo(20f).Within(.001f));
    }

    [Test]
    public void SelfCollidersDoNotBlockBeam()
    {
        GameObject body = Create("HeavyBody", _origin + Vector3.forward * 2f);
        body.transform.SetParent(_laser.transform, true);
        body.AddComponent<BoxCollider>();
        Physics.SyncTransforms();
        Fire();
        Assert.AreEqual(60f, _target.Health);
    }

    [Test]
    public void DamageInterruptsChargeButDoesNotInterruptActiveBeam()
    {
        _laser.Simulate(.1f, _target);
        _self.TakeDamage(1f);
        Assert.AreEqual(LaserAttack.AttackPhase.Cooldown, _laser.Phase);
        Assert.IsFalse(_line.enabled);
        _laser.Simulate(.41f, _target);
        Fire();
        _self.TakeDamage(1f);
        Assert.IsTrue(_laser.IsFiring);
        Assert.IsTrue(_line.enabled);
    }

    [Test]
    public void DeathClosesBeamImmediatelyAndReviveStartsFresh()
    {
        Fire();
        _self.TakeDamage(1000f);
        Assert.IsFalse(_laser.IsFiring);
        Assert.IsFalse(_line.enabled);
        _self.Reset();
        Fire();
        Assert.AreEqual(20f, _target.Health);
    }

    [Test]
    public void FrozenChargeAndCooldownDoNotAdvance()
    {
        _laser.Simulate(.1f, _target);
        float remaining = _laser.Remaining;
        for (int i = 0; i < 10; i++) _laser.Simulate(0f, _target);
        Assert.IsTrue(_laser.IsCharging);
        Assert.AreEqual(remaining, _laser.Remaining);
        _self.TakeDamage(1f);
        remaining = _laser.Remaining;
        for (int i = 0; i < 10; i++) _laser.Simulate(0f, _target);
        Assert.AreEqual(LaserAttack.AttackPhase.Cooldown, _laser.Phase);
        Assert.AreEqual(remaining, _laser.Remaining);
    }

    [Test]
    public void FrozenBeamCanDamageLateEntryOnceWithoutAdvancingLifetime()
    {
        _target.transform.position += Vector3.right * 5f;
        Physics.SyncTransforms();
        Fire();
        float remaining = _laser.Remaining;
        _target.transform.position = _origin + Vector3.forward * 10f;
        Physics.SyncTransforms();
        for (int i = 0; i < 10; i++) _laser.Simulate(0f, _target);
        Assert.AreEqual(60f, _target.Health);
        Assert.AreEqual(remaining, _laser.Remaining);
        Assert.IsTrue(_line.enabled);
        _laser.Simulate(.1f, _target);
        Assert.AreEqual(60f, _target.Health);
    }

    [Test]
    public void InvulnerabilityConsumesThisAttacksOnlyDamageAttempt()
    {
        _target.SetInvulnerableTime(10f);
        _target.TakeDamage(1f);
        Fire();
        Assert.AreEqual(99f, _target.Health);
        _target.Reset();
        _laser.Simulate(.1f, _target);
        Assert.AreEqual(100f, _target.Health);
    }

    [Test]
    public void DisableAndReenableClearsBeamAndDamageDeduplication()
    {
        Fire();
        _laser.gameObject.SetActive(false);
        Assert.IsFalse(_line.enabled);
        _laser.gameObject.SetActive(true);
        Fire();
        Assert.AreEqual(20f, _target.Health);
    }

    [Test]
    public void InitialOverlapWithCoverBlocksDamage()
    {
        Create("MuzzleInCover", _origin).AddComponent<BoxCollider>();
        Physics.SyncTransforms();
        Fire();
        Assert.AreEqual(_laser.BeamStart, _laser.BeamEnd);
        Assert.AreEqual(100f, _target.Health);
    }

    [Test]
    public void PointBlankPlayerInsideCastOriginIsStillHit()
    {
        _target.transform.position = _origin;
        Physics.SyncTransforms();
        Fire();
        Assert.AreEqual(60f, _target.Health);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void HeavyDisablesNavigationAndRespectsAimLimits(bool above)
    {
        GameObject root = Create("HeavyAim", _origin + Vector3.right * 100f);
        root.SetActive(false);
        NavMeshAgent agent = root.AddComponent<NavMeshAgent>();
        EnemyHeavy heavy = root.AddComponent<EnemyHeavy>();
        GameObject pivot = Create("AimPivot", root.transform.position);
        pivot.transform.SetParent(root.transform, true);
        Set(heavy, "_aimPivot", pivot.transform);
        Set(heavy, "_yawLimit", 30f);
        Set(heavy, "_aimUpLimit", 20f);
        Set(heavy, "_aimDownLimit", 25f);
        root.SetActive(true);
        Assert.IsFalse(agent.enabled);
        Assert.IsFalse(heavy.IsChasing);
        Vector3 position = root.transform.position;
        _target.transform.position = position + new Vector3(10f, above ? 20f : -20f, 1f);
        Physics.SyncTransforms();
        MethodInfo aim = typeof(EnemyHeavy).GetMethod("AimAt", BindingFlags.Instance | BindingFlags.NonPublic);
        aim.Invoke(heavy, new object[] { _target.transform, 1f });
        Assert.That(Mathf.DeltaAngle(0f, root.transform.eulerAngles.y), Is.EqualTo(30f).Within(.01f));
        Assert.That(Mathf.DeltaAngle(0f, pivot.transform.localEulerAngles.x), Is.EqualTo(above ? -20f : 25f).Within(.01f));
        Assert.AreEqual(position, root.transform.position);
    }

#if UNITY_EDITOR
    [UnityTest]
    public IEnumerator ProceduralVisualsSwitchPhasesReuseGeometryAndClearOnDisable()
    {
        Set(_laser, "_convergenceSeconds", .5f);
        LaserAttackVisuals visuals = _laser.gameObject.AddComponent<LaserAttackVisuals>();
        Material material = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(
            "Assets/Art/Materials/ProceduralLaser.mat");
        Assert.IsNotNull(material);
        Assert.AreEqual("GameJam/ProceduralLaser", material.shader.name);
        Set(visuals, "_material", material);
        Set(visuals, "_core", _line);
        yield return null;

        FirePreview();
        yield return null;
        LineRenderer aim = _laser.transform.Find("Laser Aim").GetComponent<LineRenderer>();
        LineRenderer marker = _laser.transform.Find("Laser Lock").GetComponent<LineRenderer>();
        Assert.IsTrue(aim.enabled);
        Assert.IsTrue(marker.enabled);
        Assert.IsFalse(_line.enabled);
        Assert.AreEqual(_laser.BeamEnd, aim.GetPosition(1));
        int count = _laser.GetComponentsInChildren<LineRenderer>(true).Length;

        _laser.Simulate(.11f, _target);
        yield return null;
        Assert.IsTrue(_laser.IsConverging);
        Assert.IsTrue(aim.enabled);
        Assert.IsTrue(marker.enabled);
        Assert.IsFalse(_line.enabled);
        float initialSpan = Vector3.Distance(marker.GetPosition(0), marker.GetPosition(marker.positionCount / 2));
        _laser.Simulate(.25f, _target);
        yield return null;
        Assert.Less(Vector3.Distance(marker.GetPosition(0), marker.GetPosition(marker.positionCount / 2)), initialSpan);
        Assert.AreEqual(_laser.BeamEnd, aim.GetPosition(1));
        Assert.AreEqual(100f, _target.Health);
        _laser.Simulate(.26f, _target);
        yield return null;
        Assert.IsTrue(_line.enabled);
        Assert.IsFalse(aim.enabled);
        Assert.IsFalse(marker.enabled);
        Assert.IsTrue(_laser.transform.Find("Laser Halo").GetComponent<LineRenderer>().enabled);
        Assert.IsTrue(_laser.transform.Find("Laser Impact").GetComponent<LineRenderer>().enabled);
        Assert.AreEqual(_laser.BeamEnd, _line.GetPosition(1));

        _laser.Simulate(.6f, _target);
        yield return null;
        foreach (LineRenderer line in _laser.GetComponentsInChildren<LineRenderer>(true))
            Assert.IsFalse(line.enabled);
        _laser.Simulate(.41f, _target);
        FirePreview();
        yield return null;
        Assert.AreEqual(count, _laser.GetComponentsInChildren<LineRenderer>(true).Length);
        Assert.IsTrue(aim.enabled);
        visuals.enabled = false;
        foreach (LineRenderer line in _laser.GetComponentsInChildren<LineRenderer>(true))
            Assert.IsFalse(line.enabled);
    }

    private void FirePreview() => _laser.Simulate(.1f, _target);

    [UnityTest]
    public IEnumerator ActualHeavyFrontalCollisionTriggersRagdoll() => CheckHeavyCollision();

    private IEnumerator CheckHeavyCollision()
    {
        Vector3 enemyPosition = _origin + Vector3.right * 150f + Vector3.up * 1.25f;
        GameObject enemy = Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/Prefabs/EnemyHeavy.prefab"), enemyPosition, Quaternion.identity);
        _created.Add(enemy);
        enemy.GetComponent<EnemyHeavy>().enabled = false;
        Vector3 start = enemyPosition + Vector3.back * 4f + Vector3.down * 1.25f;
        GameObject player = Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/Prefabs/Player.prefab"), start, Quaternion.identity);
        _created.Add(player);
        foreach (Camera camera in player.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        player.GetComponent<GameJam.Actions.PlayerActionRunner>().enabled = false;
        player.GetComponent<PlayerInputReader>().SetGameplayEnabled(false);
        PlayerMotor motor = player.GetComponent<PlayerMotor>();
        var parameters = Object.Instantiate(motor.Params);
        parameters.Gravity = parameters.AirControl = parameters.GroundFriction = parameters.RunAccel = 0f;
        motor.SetParams(parameters);
        try
        {
            Physics.SyncTransforms();
            yield return new WaitForFixedUpdate();
            motor.Teleport(start);
            player.transform.rotation = Quaternion.identity;
            int impacts = 0;
            motor.WallImpact += contact => impacts++;
            motor.SetHorizontalSpeed(20f);
            PlayerRagdollHitStun stun = player.GetComponent<PlayerRagdollHitStun>();
            for (int i = 0; i < 30 && !stun.IsActive; i++) motor.Simulate(default, 1f / 60f, 0f);
            Assert.Greater(impacts, 0, $"应报告重型敌人的正面撞击；玩家={player.transform.position} 敌人={enemyPosition}");
            Assert.IsTrue(stun.IsActive, "真实玩家预制体必须进入撞击布娃娃硬直");
            Assert.IsFalse(motor.IsWallSliding);
        }
        finally { Object.DestroyImmediate(parameters); }
    }


    [UnityTest]
    public IEnumerator ActualHeavyPrefabUsesPublicTimeStopRateAndFiresWithoutMoving()
    {
        GameObject root = Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/Prefabs/EnemyHeavy.prefab"), _origin + Vector3.right * 100f, Quaternion.identity);
        _created.Add(root);
        EnemyHeavy heavy = root.GetComponent<EnemyHeavy>();
        LaserAttack laser = root.GetComponent<LaserAttack>();
        Assert.IsTrue(root.CompareTag("NoWallSlide"));
        Assert.IsNotNull(laser.Muzzle);
        Assert.IsFalse(root.GetComponent<NavMeshAgent>().enabled);
        GameObject player = Create("HeavyIntegrationPlayer", root.transform.position + Vector3.forward * 8f);
        player.SetActive(false);
        player.AddComponent<CharacterController>();
        PlayerMotor motor = player.AddComponent<PlayerMotor>();
        var parameters = ScriptableObject.CreateInstance<MovementParams>();
        var energyParameters = ScriptableObject.CreateInstance<EnergyParams>();
        energyParameters.TimeStopCostPerTick = 0f;
        Create("HeavyTestTime", Vector3.zero).AddComponent<TimeManager>();
        TimeStopController timeStop = null;
        try
        {
            motor.SetParams(parameters);
            HealthComponent health = player.AddComponent<HealthComponent>();
            player.AddComponent<Player>();
            timeStop = player.AddComponent<TimeStopController>();
            timeStop.Configure(energyParameters, null, new TestEnergy());
            player.SetActive(true);
            motor.enabled = false;
            health.SetInvulnerableTime(0f);
            player.GetComponent<CharacterController>().Move(Vector3.zero);
            Physics.SyncTransforms();
            // CharacterController 的新建原生形状须经过物理步注册，不能用同帧 AddComponent 代替实机。
            yield return new WaitForFixedUpdate();
            MethodInfo tick = typeof(EnemyHeavy).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            heavy.SetChasePlayer(true);
            tick.Invoke(heavy, null);
            Assert.IsFalse(heavy.IsChasing);
            Assert.IsTrue(laser.IsCharging);
            float remaining = laser.Remaining;
            Vector3 position = root.transform.position;
            Quaternion rotation = root.transform.rotation;
            timeStop.BeginTimeStop();
            Assert.IsTrue(timeStop.IsTimeStopping);
            Assert.That(TimeManager.WorldRate, Is.EqualTo(.2f).Within(.001f));
            Vector3 targetPosition = player.transform.position;
            player.transform.position += Vector3.right * 4f;
            Physics.SyncTransforms();
            for (int i = 0; i < 90; i++) tick.Invoke(heavy, null);
            Assert.That(laser.Remaining, Is.EqualTo(remaining - 90f * TimeManager.WorldFixedDeltaTime).Within(.0001f));
            Assert.Greater(Quaternion.Angle(rotation, root.transform.rotation), 1f, "0.2 倍时间仍须允许瞄准推进");
            Assert.AreEqual(100f, health.Health);
            timeStop.EndTimeStop();
            player.transform.position = targetPosition;
            Physics.SyncTransforms();
            for (int i = 0; i < 100 && !laser.IsFiring; i++) tick.Invoke(heavy, null);
            Assert.IsTrue(laser.IsFiring);
            string hits = string.Join(", ", System.Array.ConvertAll(Physics.SphereCastAll(laser.BeamStart, .08f,
                laser.BeamDirection, 30f, ~0, QueryTriggerInteraction.Ignore),
                hit => hit.collider.name + "@" + hit.distance + "/" + hit.collider.GetComponentInParent<HealthComponent>()));
            Assert.AreEqual(60f, health.Health, $"start={laser.BeamStart} end={laser.BeamEnd} direction={laser.BeamDirection} "
                + $"target={player.GetComponent<CharacterController>().bounds} enabled={player.GetComponent<CharacterController>().enabled} "
                + $"trigger={player.GetComponent<CharacterController>().isTrigger} phase={laser.Phase} hits={hits}");
            Assert.AreEqual(position, root.transform.position);
            LineRenderer line = root.transform.Find("LaserBeam").GetComponent<LineRenderer>();
            Assert.IsTrue(line.enabled);
            Assert.IsNotNull(line.sharedMaterial);
            Assert.AreEqual(laser.BeamEnd, line.GetPosition(1));
        }
        finally
        {
            if (timeStop != null) timeStop.EndTimeStop();
            Object.DestroyImmediate(parameters);
            Object.DestroyImmediate(energyParameters);
        }
    }
#endif

    private sealed class TestEnergy : IEnergyAccount
    {
        public float CurrentEnergy => 100f;
        public float MaxEnergy => 100f;
        public bool TrySpend(float amount) => true;
    }
}
