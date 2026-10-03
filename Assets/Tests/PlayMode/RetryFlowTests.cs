using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 死亡重试灰盒回归（dev 地板版，D3–D5 验收「一个房间可重试」）：
// 死亡→延迟→全量复位（血量/能量/全场敌人/玩家位形），复位清单逐项守护防漏；
// 恢复策略开关（能量保留/敌人保持死）可切换——裁决落地后只调参数
public class RetryFlowTests
{
    private GameObject _gmGo;
    private PlayerData _health;
    private GameObject _player;
    private PlayerMotor _motor;
    private MovementParams _params;
    private EnergyParams _energyParams;
    private VectorEnergy _energy;
    private GameObject _enemy;
    private Enemy _enemyComponent;
    private CharacterController _enemyController;
    private GameObject _retryGo;
    private RetryFlow _retry;
    private GameObject _ground;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        LogAssert.ignoreFailingMessages = true; // 必须在任何 AddComponent 之前：Enemy 的 Awake 立即打缺跳字 LogError

        _gmGo = new GameObject("GameManager_RetryTests");
        GameManager gm = _gmGo.AddComponent<GameManager>();
        _health = gm.Player;

        _ground = GameObject.CreatePrimitive(PrimitiveType.Plane); // Motor 有重力，站住出生点才不漂移
        _ground.transform.localScale = new Vector3(50f, 1f, 50f);

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
        _energy = _player.AddComponent<VectorEnergy>();
        _energy.SetParams(_energyParams, _params);
        _player.SetActive(true);
        _player.transform.position = new Vector3(1f, 0.1f, 2f); // 非零出生点：位形复位断言有区分度
        Physics.SyncTransforms();

        _enemy = new GameObject("RetryTestEnemy");
        _enemy.transform.position = new Vector3(5f, 0f, 5f); // 先定位再挂组件：Enemy.Awake 要记录出生点
        _enemyComponent = _enemy.AddComponent<Enemy>();
        _enemyController = _enemy.AddComponent<CharacterController>();

        _retryGo = new GameObject("RetryFlowUT");
        _retry = _retryGo.AddComponent<RetryFlow>();
        _retry.RespawnDelaySec = 0.3f; // 测试紧凑延迟
        _retry.Wire(_player);

        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        LogAssert.ignoreFailingMessages = false;
        if (_enemy != null) Object.DestroyImmediate(_enemy);
        if (_player != null) Object.DestroyImmediate(_player);
        if (_retryGo != null) Object.DestroyImmediate(_retryGo);
        if (_gmGo != null) Object.DestroyImmediate(_gmGo);
        if (_ground != null) Object.DestroyImmediate(_ground);
        if (_params != null) Object.DestroyImmediate(_params);
        if (_energyParams != null) Object.DestroyImmediate(_energyParams);
        yield return null;
    }

    private void KillEnemy()
    {
        _enemyComponent.TakeDamage(99999f, Vector3.zero, Vector3.forward);
        Assert.That(_enemyComponent.IsAlive, Is.False, "前置：敌人已死");
    }

    // —— a. 死亡→延迟→全量复位（逐项守护）—— //
    [UnityTest]
    public IEnumerator Death_Delay_FullReset()
    {
        LogAssert.ignoreFailingMessages = true; // UTF 每测试重置：体内 TakeDamage 的缺跳字 LogError 需再放行
        yield return null; // 让 RetryFlow 见到玩家活着（死亡沿标记）
        KillEnemy();
        _health.SetInvulnerableTime(0f);
        _health.TakeDamage(999f);
        _energy.SetForDebug(88f);
        Assert.That(_health.IsAlive, Is.False, "前置：玩家已死");

        yield return new WaitForSecondsRealtime(0.5f); // > RespawnDelaySec 0.3（协程走墙钟）

        Assert.That(_health.IsAlive, Is.True, "重开应复活玩家");
        Assert.That(_health.Health, Is.EqualTo(_health.MaxHealth).Within(1e-3f), "重开应满血");
        Assert.That(_energy.Current, Is.EqualTo(0f).Within(1e-4f), "重开应清空能量（默认策略）");
        Assert.That(_enemyComponent.IsAlive, Is.True, "重开应复活敌人");
        Assert.That(_enemyController.enabled, Is.True, "重开应恢复敌人阻挡");
        Assert.That(_enemy.transform.position, Is.EqualTo(new Vector3(5f, 0f, 5f)).Within(0.5f), "重开应把敌人送回出生点");
        Vector3 playerPos = _player.transform.position;
        Assert.That(playerPos.x, Is.EqualTo(1f).Within(0.5f), "重开应把玩家送回出生点（x）");
        Assert.That(playerPos.z, Is.EqualTo(2f).Within(0.5f), "重开应把玩家送回出生点（z——y 由重力自然落地，不锁）");
    }

    // —— b. 恢复策略开关：能量保留模式 —— //
    [UnityTest]
    public IEnumerator Death_KeepEnergyStrategy_PreservesEnergy()
    {
        yield return null;
        _retry.ResetEnergy = false;
        _energy.SetForDebug(88f);

        _health.SetInvulnerableTime(0f);
        _health.TakeDamage(999f);
        yield return new WaitForSecondsRealtime(0.5f);

        Assert.That(_energy.Current, Is.EqualTo(88f).Within(1e-4f), "策略关清空时应保留能量");
    }

    // —— c. 恢复策略开关：敌人保持死模式 —— //
    [UnityTest]
    public IEnumerator Death_KeepEnemiesDeadStrategy_LeavesCorpses()
    {
        LogAssert.ignoreFailingMessages = true; // 同上：体内击杀的 LogError 放行
        yield return null;
        _retry.ReviveEnemies = false;
        KillEnemy();

        _health.SetInvulnerableTime(0f);
        _health.TakeDamage(999f);
        yield return new WaitForSecondsRealtime(0.5f);

        Assert.That(_enemyComponent.IsAlive, Is.False, "策略关复活时敌人应保持死");
    }

    // —— d. 玩家漂移后复位回出生点 —— //
    [UnityTest]
    public IEnumerator Death_PlayerDrift_ReturnsToSpawn()
    {
        yield return null;
        _player.transform.position = new Vector3(30f, 0.1f, 30f);
        Physics.SyncTransforms();

        _health.SetInvulnerableTime(0f);
        _health.TakeDamage(999f);
        yield return new WaitForSecondsRealtime(0.5f);

        Vector3 pos = _player.transform.position;
        Assert.That(pos.x, Is.EqualTo(1f).Within(0.5f), "漂移后重开应回出生点（x）");
        Assert.That(pos.z, Is.EqualTo(2f).Within(0.5f), "漂移后重开应回出生点（z——y 由重力自然落地，不锁）");
    }
}
