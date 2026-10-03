using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 能量收支回归（dev 地板适配版）：账户语义（拒付零副作用/等值边界/上限钳制）、
// 速度积能公式（直调精确断言 + FixedUpdate 自驱动冒烟）、命中/击杀两档返还（挂 dev 的 Damageable 事件）
// 装配：dev 版 PlayerMotor（反射注 _velocity 控速，dev 版无公开设速 API）+ VectorEnergy + Damageable
public class EnergyLoopTests
{
    private GameObject _player;
    private PlayerMotor _motor;
    private MovementParams _params;
    private EnergyParams _energyParams;
    private VectorEnergy _energy;
    private GameObject _binderGo;
    private CombatEnergyBinder _binder;
    private GameObject _enemy;
    private Damageable _enemyDamageable;

    private static FieldInfo _velocityField;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        // dev 侧 Damageable 在无跳字/渲染器的裸测试环境按设计打 LogError（缺表现组件提示）——
        // 对账户/事件语义测试是环境噪音，放行不判失败
        LogAssert.ignoreFailingMessages = true;

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
        _player.SetActive(true);

        _velocityField = typeof(PlayerMotor).GetField("_velocity",
            BindingFlags.NonPublic | BindingFlags.Instance);

        _enemy = new GameObject("EnergyTestEnemy");
        _enemyDamageable = _enemy.AddComponent<Damageable>();
        _enemyDamageable.SetMaxHealth(100f);

        _binderGo = new GameObject("CombatEnergyBinder");
        _binder = _binderGo.AddComponent<CombatEnergyBinder>();
        _binder.SetSources(_energy, _energyParams);

        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        LogAssert.ignoreFailingMessages = false; // 恢复全局日志判罚，不漏给下一个测试
        if (_enemy != null) Object.DestroyImmediate(_enemy);
        if (_binderGo != null) Object.DestroyImmediate(_binderGo);
        if (_player != null) Object.DestroyImmediate(_player);
        if (_params != null) Object.DestroyImmediate(_params);
        if (_energyParams != null) Object.DestroyImmediate(_energyParams);
        yield return null;
    }

    // —— 工具 ——

    private static IEnumerator RunSteps(float worldSeconds)
    {
        int steps = Mathf.RoundToInt(worldSeconds / Time.fixedDeltaTime);
        for (int i = 0; i < steps; i++) yield return new WaitForFixedUpdate();
    }

    private void SetHorizontalSpeed(float speed) =>
        _velocityField.SetValue(_motor, Vector3.forward * speed);

    // —— a. 积能公式（直调精确）：超额 1 × 系数 1/tick × PlayerRate(=1) —— //
    [UnityTest]
    public IEnumerator Accrue_HighSpeed_GainsExactAmount()
    {
        SetHorizontalSpeed(20f); // 阈值 10 → 超额 1
        _energy.Accrue();        // 单次直调：+1（PlayerRate 无实例时恒 1）

        Assert.That(_energy.Current, Is.EqualTo(1f).Within(1e-4f), "超额 1 × 1/tick 应积 1 点");
        yield return null;
    }

    // —— b. 低速不积：低于地速阈值零收益 —— //
    [UnityTest]
    public IEnumerator Accrue_LowSpeed_NoGain()
    {
        SetHorizontalSpeed(5f);
        _energy.Accrue();
        _energy.Accrue();

        Assert.That(_energy.Current, Is.EqualTo(0f).Within(1e-4f), "低于阈值不得积能");
        yield return null;
    }

    // —— c. 自驱动冒烟：FixedUpdate 推进期间能量持续增长（执行序/自驱动的存在性验证）—— //
    [UnityTest]
    public IEnumerator SelfDriven_FixedUpdate_Accrues()
    {
        SetHorizontalSpeed(30f); // 超额 2——Motor 摩擦会削速度，断言只锁「在涨」
        yield return RunSteps(0.3f);

        Assert.That(_energy.Current, Is.GreaterThan(5f), "固定步推进中应持续积能");
    }

    // —— d. 账户语义：不足拒付零副作用、等值边界可付 —— //
    [Test]
    public void TrySpend_Insufficient_RefusesWithoutSideEffect()
    {
        _energy.SetForDebug(10f);
        Assert.That(_energy.TrySpend(50f), Is.False, "余额不足应拒付");
        Assert.That(_energy.Current, Is.EqualTo(10f).Within(1e-4f), "拒付不得改变余额");

        Assert.That(_energy.TrySpend(10f), Is.True, "余额恰等于费用应可付");
        Assert.That(_energy.Current, Is.EqualTo(0f).Within(1e-4f));
    }

    // —— e. 上限钳制 —— //
    [Test]
    public void Grant_CappedAtMax()
    {
        _energy.SetForDebug(190f);
        _energy.Grant(30f);
        Assert.That(_energy.Current, Is.EqualTo(200f).Within(1e-4f), "获得量超上限应钳到 MaxEnergy");
    }

    // —— f. 战斗经济两档：命中返还 Hit 档、击杀返还 Kill 档（dev 的 Damageable 事件链）—— //
    [Test]
    public void Binder_HitThenKill_GrantsBothTiers()
    {
        LogAssert.ignoreFailingMessages = true; // UTF 每测试重置日志状态：TakeDamage 的缺跳字 LogError 需体内再放行
        _binder.Register(_enemyDamageable);
        _enemyDamageable.SetInvulnerableTime(0f); // 免受击无敌挡住第二击——本测试锁返还两档，无敌语义归 dev 侧
        _energy.SetForDebug(0f);

        float applied = _enemyDamageable.TakeDamage(10f, Vector3.zero, Vector3.forward);
        Assert.That(applied, Is.EqualTo(10f).Within(1e-4f), "前置：命中生效");
        Assert.That(_energy.Current, Is.EqualTo(5f).Within(1e-4f), "命中应返还 HitEnergyGain");

        _enemyDamageable.TakeDamage(999f, Vector3.zero, Vector3.forward);
        Assert.That(_enemyDamageable.IsAlive, Is.False, "前置：已击杀");
        Assert.That(_energy.Current, Is.EqualTo(35f).Within(1e-4f),
            "击杀应在命中档之上再返 KillEnergyGain（5+30）");
    }

    // —— g. 复位：ResetEnergy 清零（重开流程的能量侧）—— //
    [Test]
    public void ResetEnergy_ClearsBalance()
    {
        _energy.SetForDebug(88f);
        _energy.ResetEnergy();
        Assert.That(_energy.Current, Is.EqualTo(0f).Within(1e-4f));
    }
}
