using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 能量收支全链回归（D3–D5 闭环验收「能量收支接通」）：
// 速度积能（超阈值累积/低于阈值不积）、账户语义（不足拒付零副作用/上限钳制）、
// 战斗经济绑定（命中/击杀两档返还——「敌人即资源」决策 #8）
// 装配：纯代码搭玩家（Motor+VectorEnergy）与敌人（Hurtbox+Health）；结算走 resolver.ProcessTick 直调
public class EnergyLoopTests
{
    private GameObject _player;
    private PlayerMotor _motor;
    private MovementParams _params;
    private EnergyParams _energyParams;
    private VectorEnergy _energy;
    private GameObject _resolverGo;
    private GameObject _timeGo;

    private GameObject _enemy;
    private HealthComponent _enemyHealth;
    private Hitbox _playerHitbox;
    private CombatEnergyBinder _binder;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        _timeGo = new GameObject("TimeManager_EnergyTests");
        _timeGo.AddComponent<TimeManager>();

        _resolverGo = new GameObject("Resolver");
        _resolverGo.AddComponent<DamageResolver>();

        _params = ScriptableObject.CreateInstance<MovementParams>(); // 地速阈值默认 10
        _energyParams = ScriptableObject.CreateInstance<EnergyParams>();
        _energyParams.MaxEnergy = 200f;
        _energyParams.EnergyPerTickPerExcessSpeed = 1f;
        _energyParams.HitEnergyGain = 5f;
        _energyParams.KillEnergyGain = 30f;

        _player = new GameObject("EnergyTestPlayer");
        _player.SetActive(false);
        CharacterController controller = _player.AddComponent<CharacterController>();
        controller.radius = _params.CapsuleBaseRadius;
        controller.height = _params.CapsuleBaseHeight;
        controller.center = new Vector3(0f, controller.height * 0.5f, 0f);
        _motor = _player.AddComponent<PlayerMotor>();
        _motor.SetParams(_params);
        _energy = _player.AddComponent<VectorEnergy>();
        _energy.SetParams(_energyParams, _params);
        _playerHitbox = _player.AddComponent<Hitbox>();
        _playerHitbox.Kind = HitboxKind.Damage;
        _binder = _player.AddComponent<CombatEnergyBinder>();
        _binder.SetSources(_energy, _energyParams, _playerHitbox);
        _player.SetActive(true);

        _enemy = new GameObject("EnergyTestEnemy");
        _enemyHealth = _enemy.AddComponent<HealthComponent>();
        _enemyHealth.MaxHealth = 100f;
        _enemyHealth.Layer = TimeLayer.World;
        BoxCollider hurtCol = _enemy.AddComponent<BoxCollider>();
        hurtCol.center = new Vector3(0f, 1f, 0f);
        hurtCol.size = new Vector3(0.8f, 1.8f, 0.8f);
        _enemy.AddComponent<Hurtbox>();
        _enemy.transform.position = new Vector3(0f, 0f, 0.8f);

        Physics.SyncTransforms();
        yield return null; // Start（binder 订阅）在首帧后已跑
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        TimeManager.ResetAll();
        if (_enemy != null) Object.DestroyImmediate(_enemy);
        if (_player != null) Object.DestroyImmediate(_player);
        if (_resolverGo != null) Object.DestroyImmediate(_resolverGo);
        if (_timeGo != null) Object.DestroyImmediate(_timeGo);
        if (_params != null) Object.DestroyImmediate(_params);
        if (_energyParams != null) Object.DestroyImmediate(_energyParams);
        yield return null;
    }

    private static IEnumerator RunSteps(float worldSeconds)
    {
        int steps = Mathf.RoundToInt(worldSeconds / Time.fixedDeltaTime);
        for (int i = 0; i < steps; i++) yield return new WaitForFixedUpdate();
    }

    private static int _instanceCounter; // 攻击实例号：每击递增，防同段去重吞第二击

    private void ActivatePlayerHitbox(float damage)
    {
        _playerHitbox.Activate(new HitboxActivation
        {
            Attacker = _player,
            InstanceId = ++_instanceCounter,
            PhaseIndex = 0,
            Profile = new HitboxProfile
            {
                Shape = HitboxShape.Capsule,
                CapsuleRadius = 1.5f,
                CapsuleHeight = 2.5f,
                LocalOffset = Vector3.up,
            },
            BaseDamage = damage,
            DamageType = DamageType.Melee,
            Knockback = 0f,
            HitStopSec = 0f,
        });
    }

    // —— a. 速度积能：水平速度超地速阈值，按 tick 累积（速度经济学主链）—— //
    [UnityTest]
    public IEnumerator Accrue_HighSpeed_GainsEnergy()
    {
        _motor.SetHorizontalSpeed(Vector3.forward * 20f); // 阈值 10 → 每步超额 1 × 1/tick
        float before = _energy.Current;
        yield return RunSteps(0.5f); // 30 步名义 +30（空中速度衰减会让实际略低，断言只锁量级）

        Assert.That(_energy.Current - before, Is.EqualTo(30f).Within(10f),
            "超阈值速度应按 (速度/阈值-1)×每 tick 系数 积能");
    }

    // —— b. 低速不积：低于阈值零收益（bhop 攒速度才换能量的前提）—— //
    [UnityTest]
    public IEnumerator Accrue_LowSpeed_NoGain()
    {
        _motor.SetHorizontalSpeed(Vector3.forward * 5f); // 阈值 10 以下
        yield return RunSteps(0.3f);

        Assert.That(_energy.Current, Is.EqualTo(0f).Within(1e-4f), "低于地速阈值不得积能");
    }

    // —— c. 账户语义：余额不足拒付且零副作用；等值边界可付 —— //
    [Test]
    public void TrySpend_Insufficient_RefusesWithoutSideEffect()
    {
        _energy.SetForDebug(10f);
        Assert.That(_energy.TrySpend(50f), Is.False, "余额不足应拒付");
        Assert.That(_energy.Current, Is.EqualTo(10f).Within(1e-4f), "拒付不得改变余额");

        Assert.That(_energy.TrySpend(10f), Is.True, "余额恰等于费用应可付");
        Assert.That(_energy.Current, Is.EqualTo(0f).Within(1e-4f));
    }

    // —— d. 上限钳制：Grant 超上限截到 Max —— //
    [Test]
    public void Grant_CappedAtMax()
    {
        _energy.SetForDebug(190f);
        _energy.Grant(30f);
        Assert.That(_energy.Current, Is.EqualTo(200f).Within(1e-4f), "获得量超上限应钳到 MaxEnergy");
    }

    // —— e. 战斗经济·命中档：结算命中返还 HitEnergyGain —— //
    [UnityTest]
    public IEnumerator Binder_Hit_GrantsHitTier()
    {
        _energy.SetForDebug(0f);
        ActivatePlayerHitbox(damage: 10f); // 敌血 100，非致死
        _resolverGo.GetComponent<DamageResolver>().ProcessTick();
        yield return null;

        Assert.That(_enemyHealth.IsDead, Is.False, "前置：未击杀");
        Assert.That(_energy.Current, Is.EqualTo(5f).Within(1e-4f), "命中应返还 HitEnergyGain");
    }

    // —— f. 战斗经济·击杀档：致死一击按 KillEnergyGain 结（结算序 Died 先于命中回调）—— //
    [UnityTest]
    public IEnumerator Binder_Kill_GrantsKillTier()
    {
        _energy.SetForDebug(0f);
        ActivatePlayerHitbox(damage: 999f); // 致死
        _resolverGo.GetComponent<DamageResolver>().ProcessTick();
        yield return null;

        Assert.That(_enemyHealth.IsDead, Is.True, "前置：已击杀");
        Assert.That(_energy.Current, Is.EqualTo(30f).Within(1e-4f),
            "击杀应按 KillEnergyGain 档返还（而非命中档）");
    }

    // —— g. 扣费消费闭环：积能攒够 → 出招可扣（速度→能量→技能的正向循环底座）—— //
    [UnityTest]
    public IEnumerator Accrue_ThenSpend_LoopCloses()
    {
        _motor.SetHorizontalSpeed(Vector3.forward * 30f); // 超额 2 × 1/tick
        yield return RunSteps(0.25f); // 15 步 ≈ +30

        Assert.That(_energy.TrySpend(25f), Is.True, "泵油攒出的能量应可支付技能扣费");
    }

    // —— h. 冻结不积：玩家层 dt=0（冻结/时停中玩家层被压）期间高速也不积能 —— //
    //      （Motor 侧旧断言「冻结不积能」随能量外迁迁到此处）
    [UnityTest]
    public IEnumerator Accrue_FrozenLayer_NoGain()
    {
        _motor.SetHorizontalSpeed(Vector3.forward * 30f);
        TimeScaleHandle freeze = TimeManager.RegisterScale(TimeLayer.Player, 0f, "test_freeze");
        yield return RunSteps(0.2f);
        TimeManager.Release(freeze);

        Assert.That(_energy.Current, Is.EqualTo(0f).Within(1e-4f), "玩家层冻结期间不得积能");
    }
}
