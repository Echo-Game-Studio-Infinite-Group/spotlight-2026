using System;
using GameJam.Actions;
using UnityEngine;

// 独立时间效果只旁读输入，不占用主动作、动画或 Motor 的控制权。
[DefaultExecutionOrder(-20)]
[DisallowMultipleComponent]
public sealed class TimeStopController : MonoBehaviour
{
    private enum HoldState { Idle, Waiting, Active, WaitForRelease }

    [Tooltip("能量与时间效果参数")]
    [SerializeField] private EnergyParams _params;
    [SerializeField] private VectorEnergy _energy;
    [SerializeField] private PlayerInputReader _input;
    [Tooltip("60Hz 输入帧计时，不受世界减速或玩家顿帧影响")]
    [SerializeField, Min(1)] private int _holdThresholdFrames = 30;
    [Tooltip("玩家和世界共用的全局倍率上限，不与局部顿帧重复相乘")]
    [SerializeField, Range(0f, 1f)] private float _globalRate = 0.2f;

    private HealthComponent _health;
    private PlayerMotor _motor;
    private PlayerActionRunner _runner;
    private IEnergyAccount _energyOverride;
    private HoldState _state;
    private int _heldFrames;
    private long _pressVersion;

    public bool IsTimeStopping => _state == HoldState.Active;
    public event Action<bool> TimeStopChanged;
    public int HeldFrames => _heldFrames;
    public int HoldThresholdFrames => _holdThresholdFrames;
    public float GlobalRate => _globalRate;
    private IEnergyAccount Account => _energyOverride ?? _energy;
    private bool InputAvailable => _input != null && _input.isActiveAndEnabled && _input.GameplayEnabled;

    public void Configure(EnergyParams parameters, PlayerInputReader input, IEnergyAccount energyAccount = null)
    {
        Unsubscribe();
        Interrupt();
        _params = parameters;
        _input = input;
        _energyOverride = energyAccount;
        if (isActiveAndEnabled) Subscribe();
    }

    private void OnEnable()
    {
        if (_input == null) _input = GetComponent<PlayerInputReader>();
        if (_energy == null) _energy = GetComponent<VectorEnergy>();
        _health = GetComponent<HealthComponent>();
        _motor = GetComponent<PlayerMotor>();
        _runner = GetComponent<PlayerActionRunner>();
        Subscribe();
        // 组件恢复时按键可能仍被按住，必须先松开才能重新触发。
        SetState(_input != null && _input.SkillHeldOnDevice ? HoldState.WaitForRelease : HoldState.Idle);
    }

    private void Subscribe()
    {
        if (_input != null) { _input.Cleared += Interrupt; _input.SkillHeldChanged += OnSkillHeldChanged; }
        if (_health != null) { _health.Died += OnHealthChanged; _health.Revived += OnHealthChanged; }
        if (_motor != null) _motor.Teleported += OnTeleported;
        if (_runner != null) _runner.Simulated += Charge;
    }

    private void Unsubscribe()
    {
        if (_input != null) { _input.Cleared -= Interrupt; _input.SkillHeldChanged -= OnSkillHeldChanged; }
        if (_health != null) { _health.Died -= OnHealthChanged; _health.Revived -= OnHealthChanged; }
        if (_motor != null) _motor.Teleported -= OnTeleported;
        if (_runner != null) _runner.Simulated -= Charge;
    }

    private void FixedUpdate()
    {
        if (!InputAvailable) { Interrupt(); return; }
        // 已接入的角色在 runner 完成模拟后计费，开始减速的首帧也只扣实际推进量。
        bool sequenceDriven = _runner != null && _runner.IsConnected && _runner.isActiveAndEnabled;
        SimulateTick(_input.SkillHeld, _input.SkillPressVersion, sequenceDriven ? 0f : TimeManager.PlayerFixedDeltaTime);
    }

    // 测试与回放注入同一套逻辑，不调用 CaptureTick，避免清走动作序列的输入。
    public void SimulateTick(bool skillHeld, long pressVersion, float playerDeltaTime)
    {
        if (float.IsNaN(playerDeltaTime) || float.IsInfinity(playerDeltaTime) || playerDeltaTime < 0f)
            throw new ArgumentOutOfRangeException(nameof(playerDeltaTime));
        if (!isActiveAndEnabled || TimeManager.IsPaused || _health != null && !_health.IsAlive)
        { Interrupt(); return; }
        if (!skillHeld) { ResetHold(); return; }
        if (_state == HoldState.WaitForRelease) return;
        if (_state != HoldState.Idle && _pressVersion != pressVersion) ResetHold();
        if (_state == HoldState.Idle)
        {
            _pressVersion = pressVersion;
            _heldFrames = 0;
            SetState(HoldState.Waiting);
            return;
        }
        float previousRate = TimeManager.PlayerRate;
        if (_state == HoldState.Waiting)
        {
            _heldFrames++;
            if (_heldFrames < Mathf.Max(1, _holdThresholdFrames)) return;
            BeginTimeStop();
        }
        if (!IsTimeStopping) return;
        if (previousRate > 0f) playerDeltaTime *= TimeManager.PlayerRate / previousRate;
        Charge(playerDeltaTime);
    }

    private void Charge(float simulatedSeconds)
    {
        if (!IsTimeStopping || simulatedSeconds <= 0f) return;
        if (_params == null || Account == null) { Interrupt(); return; }
        // 账户支持小数；逐帧扣费避免反复松开把不足一个能量点的费用清掉。
        float amount = Mathf.Max(0f, _params.TimeStopCostPerTick) * simulatedSeconds * ActionSequencePlayer.FramesPerSecond;
        if (!Account.TrySpend(amount) || amount > 0f && Account.CurrentEnergy <= 0f) Interrupt();
    }

    public void BeginTimeStop()
    {
        if (IsTimeStopping) return;
        if (!isActiveAndEnabled || TimeManager.IsPaused || _health != null && !_health.IsAlive ||
            _params == null || Account == null || TimeManager.Resolve() == null ||
            Account.CurrentEnergy < Mathf.Max(0f, _params.TimeStopCostPerTick))
        { Interrupt(); return; }
        if (_input != null) _pressVersion = _input.SkillPressVersion;
        TimeManager.SetTimeStop(true, _globalRate, this);
        SetState(HoldState.Active);
    }

    // 外部结束也需要重新按下，避免仍按住的右键在下一 tick 自动开启。
    public void EndTimeStop() => Interrupt();

    public void Interrupt()
    {
        TimeManager.SetTimeStop(false, _globalRate, this);
        _heldFrames = 0;
        SetState(InputAvailable && !_input.SkillHeldOnDevice ? HoldState.Idle : HoldState.WaitForRelease);
    }

    private void ResetHold()
    {
        TimeManager.SetTimeStop(false, _globalRate, this);
        _heldFrames = 0;
        SetState(HoldState.Idle);
    }

    private void SetState(HoldState state)
    {
        bool wasActive = IsTimeStopping;
        _state = state;
        if (wasActive != IsTimeStopping) TimeStopChanged?.Invoke(IsTimeStopping);
    }

    private void OnSkillHeldChanged(bool held)
    {
        if (held) return;
        if (InputAvailable && !TimeManager.IsPaused) ResetHold();
        else Interrupt();
    }
    private void OnHealthChanged(HealthComponent health) => Interrupt();
    private void OnTeleported(Vector3 delta) => Interrupt();
    private void Update()
    {
        // 暂停会停止 FixedUpdate，必须在仍运行的渲染更新中收尾。
        if (TimeManager.IsPaused || !InputAvailable || _health != null && !_health.IsAlive) Interrupt();
    }
    private void OnApplicationFocus(bool focused) { if (!focused) Interrupt(); }
    private void OnDisable() { Unsubscribe(); Interrupt(); }
}
