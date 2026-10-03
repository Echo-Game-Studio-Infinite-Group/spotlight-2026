using UnityEngine;

// 矢量转换器能量账户（唯一能量账户——速度转能量、出招扣费、命中/击杀返还全走这里）
// 自驱动积能：执行序 +10（扣费 -50 → 移动 0 → 积能 +10），保证技能扣费读到的是本 tick 开始时的能量，
// 且积能用的水平速度是本 tick 移动结算后的值——顺序即语义，勿改执行序
[DefaultExecutionOrder(10)]
public class VectorEnergy : MonoBehaviour, IEnergyAccount
{
    private static VectorEnergy _instance;

    [SerializeField] private EnergyParams _params;

    [Tooltip("地速阈值从移动参数读取，保证积能公式与移动用的阈值同源")]
    [SerializeField] private MovementParams _movementParams;

    private float _energy;

    public float Current => _energy;

    // IEnergyAccount（CombatSeams.cs）：战斗侧经接口消费（PlayerCombat 统一提交扣费）
    public float CurrentEnergy => _energy;

    public float MaxEnergy => _params != null ? _params.MaxEnergy : 0f;

    /// <summary>表现层（HUD、音效）监听这个，不反向依赖本组件</summary>
    public event System.Action<float> Changed;

    /// <summary>
    /// 注入参数资产（装配工具与测试用，先注入再激活——地速阈值与移动侧同源）
    /// </summary>
    public void SetParams(EnergyParams parameters, MovementParams movementParams)
    {
        _params = parameters;
        _movementParams = movementParams;
    }

    private void Awake()
    {
        // 出现第二个账户时保留先来的，避免两份能量互相覆盖
        if (_instance != null && _instance != this)
        {
            Debug.LogWarning("[VectorEnergy] 场景中已存在能量账户，本组件已禁用", this);
            enabled = false;
            return;
        }
        _instance = this;
        _energy = 0f;
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    private void FixedUpdate()
    {
        if (TimeManager.IsPaused) return;
        Accrue();
    }

    /// <summary>速度积能：速度超出地速阈值的部分按 tick 换算累积（玩家冻结时 PlayerDeltaTime=0，自然停积）</summary>
    public void Accrue()
    {
        if (_params == null || _movementParams == null) return;

        // 速度归一化：这样改地速阈值不会连带改变积能曲线
        float excess = Mathf.Max(0f, HorizontalSpeed() / _movementParams.GroundSpeedThreshold - 1f);
        if (excess <= 0f) return;

        // 策划案公式按 tick 给，先把本帧时长换算成"相当于几个 tick"
        // tick 时长从引擎读（工程改 Fixed Timestep 时这里自动跟随），PlayerDeltaTime 除以它即 tick 数
        float tickSeconds = Time.fixedDeltaTime;
        if (tickSeconds <= 0f) return;

        float tickRatio = TimeManager.PlayerDeltaTime / tickSeconds;
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

    /// <summary>
    /// 一次性获得（命中/击杀返还——「敌人即资源」，决策 #8）。战斗经济绑定组件调，本类不反向依赖战斗
    /// </summary>
    public void Grant(float amount) => AddInternal(amount);

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
