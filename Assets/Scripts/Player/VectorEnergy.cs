using UnityEngine;

// 能量账户的对外契约
// 放在提供方模块（本文件）里：dev 上原来的落点 Core/CombatSeams.cs 已被删除，
// 调用方（PlayerCombat / DebugHUD）只认这个接口，不依赖 VectorEnergy 的具体实现
public interface IEnergyAccount
{
    float CurrentEnergy { get; }
    float MaxEnergy { get; }

    /// <summary>够就扣并返回 true；不够完全不改状态、返回 false，调用方据此决定是否中断原动作</summary>
    bool TrySpend(float amount);
}

// 矢量转换器能量账户
// 固定 tick 结算走 TimeManager.PlayerFixedDeltaTime，积能不受渲染帧率影响。
// 积能时机：本组件自己在 FixedUpdate 里调用 Accrue（执行序 10，排在 PlayerMotor(0) 的移动结算之后），
//           保证本 tick 读到的是移动结算后的速度；技能/战斗在更早的序上扣费，读到的是 tick 开始时的能量
// IEnergyAccount：战斗层（PlayerCombat）扣费的唯一入口
[DefaultExecutionOrder(10)]
public class VectorEnergy : MonoBehaviour, IEnergyAccount
{
    private static VectorEnergy _instance;

    [SerializeField] private EnergyParams _params;

    [Tooltip("地速阈值从移动参数读取，保证积能公式与移动用的阈值同源")]
    [SerializeField] private MovementParams _movementParams;

    private float _energy;

    public float Current => _energy;

    /// <summary>IEnergyAccount：战斗层查余额用（与 Current 同值，两个名字各服务一侧）</summary>
    public float CurrentEnergy => _energy;

    /// <summary>能量上限，供 HUD 显示。参数未装配时为 0</summary>
    public float MaxEnergy => _params != null ? _params.MaxEnergy : 0f;

    /// <summary>表现层（HUD、音效）监听这个，不反向依赖本组件</summary>
    public event System.Action<float> Changed;

    private void Awake()
    {
        // 不做"场景中只留一个"的判重：多场景流程里新实例会被旧实例误伤而禁用。
        // 账户状态全是实例字段，多个实例不会互相覆盖，谁挂在活着的角色上谁就在算
        _instance = this;
        _energy = 0f;
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    // 执行序 10：晚于 PlayerMotor(默认 0) 的移动结算，早于本 tick 的后续消费方
    private void FixedUpdate()
    {
        Accrue();
    }

    /// <summary>本 tick 的积能结算。由本组件的 FixedUpdate 自动调用；测试可直接调用做单帧结算</summary>
    public void Accrue()
    {
        if (_params == null || _movementParams == null) return;

        // 速度归一化：这样改地速阈值不会连带改变积能曲线
        float excess = Mathf.Max(0f, HorizontalSpeed() / _movementParams.GroundSpeedThreshold - 1f);
        if (excess <= 0f) return;

        // 策划案公式按 tick 给，先把本帧时长换算成"相当于几个 tick"
        // 命中可能在 tick 中改变时间倍率；接入角色使用已经实际积分的时长，避免少计本 tick 的能量。
        float tickSeconds = Time.fixedDeltaTime;
        if (tickSeconds <= 0f) return;

        GameJam.Actions.PlayerActionRunner runner = GetComponent<GameJam.Actions.PlayerActionRunner>();
        float elapsed = runner != null && runner.IsConnected ? runner.LastSimulatedSeconds : TimeManager.PlayerFixedDeltaTime;
        float tickRatio = elapsed / tickSeconds;
        AddInternal(excess * _params.EnergyPerTickPerExcessSpeed * tickRatio);
    }

    /// <summary>不够则完全不改变状态，调用方据此决定是否打断原动作</summary>
    public bool TrySpend(float amount)
    {
        if (amount <= 0f) return true;
        if (_energy < amount) return false;

        _energy -= amount;
        Changed?.Invoke(_energy);
        return true;
    }

    /// <summary>掉落、重生、整局重开的统一复位入口</summary>
    public void ResetEnergy()
    {
        AddInternal(-_energy);
    }

    /// <summary>仅调试与测试使用</summary>
    public void SetForDebug(float value)
    {
        AddInternal(value - _energy);
    }

    // 加/减统一入口：钳制与通知只写一遍
    private void AddInternal(float delta)
    {
        if (Mathf.Approximately(delta, 0f)) return;

        float max = _params != null ? _params.MaxEnergy : 0f;
        float next = Mathf.Clamp(_energy + delta, 0f, max);
        if (Mathf.Approximately(next, _energy)) return;

        _energy = next;
        Changed?.Invoke(_energy);
    }

    // 垂直速度不参与能量结算
    private float HorizontalSpeed()
    {
        var motor = GetComponent<PlayerMotor>();
        return motor != null ? motor.HorizontalSpeed : 0f;
    }
}
