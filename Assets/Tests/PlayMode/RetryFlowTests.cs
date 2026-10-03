using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 死亡重试灰盒回归（D3–D5 闭环验收「一个房间可重试」）：
// 死亡 → 延迟 → 全量复位（时间层/攻击机/血量/位形/能量/全场敌人），复位清单逐项守护防漏；
// 恢复策略开关（能量保留/敌人保持死）可切换——裁决落地后只调参数不改代码
public class RetryFlowTests
{
    private GameObject _player;
    private PlayerMotor _motor;
    private HealthComponent _playerHealth;
    private PlayerCombat _combat;
    private MovementParams _params;
    private EnergyParams _energyParams;
    private VectorEnergy _energy;
    private GameObject _enemy;
    private HealthComponent _enemyHealth;
    private CharacterController _enemyController;
    private GameObject _retryGo;
    private RetryFlow _retry;
    private GameObject _resolverGo;
    private GameObject _timeGo;
    private GameObject _ground;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        _timeGo = new GameObject("TimeManager_RetryTests");
        _timeGo.AddComponent<TimeManager>();

        _ground = GameObject.CreatePrimitive(PrimitiveType.Plane); // 玩家 Motor 有重力，站住出生点才不漂移
        _ground.transform.localScale = new Vector3(50f, 1f, 50f);

        _resolverGo = new GameObject("Resolver");
        _resolverGo.AddComponent<DamageResolver>();

        _params = ScriptableObject.CreateInstance<MovementParams>();
        _energyParams = ScriptableObject.CreateInstance<EnergyParams>();

        _player = new GameObject("RetryTestPlayer");
        _player.SetActive(false);
        CharacterController controller = _player.AddComponent<CharacterController>();
        controller.radius = _params.CapsuleBaseRadius;
        controller.height = _params.CapsuleBaseHeight;
        controller.center = new Vector3(0f, controller.height * 0.5f, 0f);
        _motor = _player.AddComponent<PlayerMotor>();
        _motor.SetParams(_params);
        _playerHealth = _player.AddComponent<HealthComponent>();
        _playerHealth.MaxHealth = 100f;
        _playerHealth.Layer = TimeLayer.Player;
        _energy = _player.AddComponent<VectorEnergy>();
        _energy.SetParams(_energyParams, _params);
        _combat = _player.AddComponent<PlayerCombat>();
        _player.SetActive(true);
        _player.transform.position = new Vector3(1f, 0.05f, 2f); // 非零出生点：位形复位断言才有区分度

        _enemy = new GameObject("RetryTestEnemy");
        _enemyHealth = _enemy.AddComponent<HealthComponent>();
        _enemyHealth.MaxHealth = 50f;
        _enemyHealth.Layer = TimeLayer.World;
        _enemyController = _enemy.AddComponent<CharacterController>();
        _enemy.AddComponent<EnemyCombat>(); // 只复位语义，不需要攻击资产

        _retryGo = new GameObject("RetryFlowUT");
        _retry = _retryGo.AddComponent<RetryFlow>();
        _retry.RespawnDelaySec = 0.3f; // 测试紧凑延迟
        _retry.Wire(_player);

        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        TimeManager.ResetAll();
        if (_enemy != null) Object.DestroyImmediate(_enemy);
        if (_player != null) Object.DestroyImmediate(_player);
        if (_retryGo != null) Object.DestroyImmediate(_retryGo);
        if (_resolverGo != null) Object.DestroyImmediate(_resolverGo);
        if (_timeGo != null) Object.DestroyImmediate(_timeGo);
        if (_ground != null) Object.DestroyImmediate(_ground);
        if (_params != null) Object.DestroyImmediate(_params);
        if (_energyParams != null) Object.DestroyImmediate(_energyParams);
        yield return null;
    }

    private static IEnumerator RunSteps(float worldSeconds)
    {
        int steps = Mathf.RoundToInt(worldSeconds / Time.fixedDeltaTime);
        for (int i = 0; i < steps; i++) yield return new WaitForFixedUpdate();
    }

    private void KillPlayer()
    {
        _playerHealth.ApplyDamage(new DamageInfo { Type = DamageType.Melee, Damage = 999f, KnockbackDir = Vector3.forward });
        Assert.That(_playerHealth.IsDead, Is.True, "前置：玩家已死");
    }

    // —— a. 死亡→延迟→全量复位（逐项守护，防复位清单漏项）—— //
    [UnityTest]
    public IEnumerator Death_Delay_FullReset()
    {
        KillPlayer();
        _enemyHealth.ApplyDamage(new DamageInfo { Type = DamageType.Melee, Damage = 999f, KnockbackDir = Vector3.forward });
        _energy.SetForDebug(88f);
        _player.transform.position = new Vector3(20f, 0f, 20f); // 死亡漂移后远离出生点
        Physics.SyncTransforms();

        yield return RunSteps(0.5f); // > RespawnDelaySec 0.3（协程走墙钟，固定步推进期间照常计时）

        Assert.That(_playerHealth.IsDead, Is.False, "重开应复活玩家");
        Assert.That(_playerHealth.CurrentHealth, Is.EqualTo(100f).Within(1e-3f), "重开应满血");
        Assert.That(_player.transform.position, Is.EqualTo(new Vector3(1f, 0.05f, 2f)).Within(1e-2f), "重开应回出生点");
        Assert.That(_energy.Current, Is.EqualTo(0f).Within(1e-4f), "重开应清空能量（默认策略）");
        Assert.That(_enemyHealth.IsDead, Is.False, "重开应复活敌人");
        Assert.That(_enemyController.enabled, Is.True, "重开应恢复敌人阻挡（断肢复位）");
    }

    // —— b. 恢复策略开关：能量保留模式（裁决「保留能量」时只调参数）—— //
    [UnityTest]
    public IEnumerator Death_KeepEnergyStrategy_PreservesEnergy()
    {
        _retry.ResetEnergy = false;
        _energy.SetForDebug(88f);

        KillPlayer();
        yield return RunSteps(0.5f);

        Assert.That(_energy.Current, Is.EqualTo(88f).Within(1e-4f), "策略关清空时应保留能量");
    }

    // —— c. 恢复策略开关：敌人保持死模式（裁决「不重刷」时只调参数）—— //
    [UnityTest]
    public IEnumerator Death_KeepEnemiesDeadStrategy_LeavesCorpses()
    {
        _retry.ReviveEnemies = false;
        _enemyHealth.ApplyDamage(new DamageInfo { Type = DamageType.Melee, Damage = 999f, KnockbackDir = Vector3.forward });

        KillPlayer();
        yield return RunSteps(0.5f);

        Assert.That(_enemyHealth.IsDead, Is.True, "策略关复活时敌人应保持死");
    }

    // —— d. 时间层无残留：死亡时挂着 hit-stop 与来源缩放，重开后全部清干净 —— //
    [UnityTest]
    public IEnumerator Death_ClearsTimeLayerResidue()
    {
        TimeManager.HitStop(0.5f);                                   // 死亡瞬间常挂着 hit-stop
        TimeManager.RegisterScale(TimeLayer.World, 0.3f, "residue"); // 以及 parry 时缓类来源

        KillPlayer();
        yield return RunSteps(0.5f);

        Assert.That(TimeManager.InHitStop, Is.False, "重开应清 hit-stop");
        Assert.That(TimeManager.WorldDeltaTime, Is.EqualTo(Time.fixedDeltaTime).Within(1e-6f), "重开应清来源缩放（世界层 dt 恢复全速）");
    }
}
