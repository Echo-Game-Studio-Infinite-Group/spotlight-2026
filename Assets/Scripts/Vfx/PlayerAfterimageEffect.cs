using System;
using System.Collections.Generic;
using GameJam.Actions;
using UnityEngine;
using UnityEngine.Events;

[DisallowMultipleComponent]
[RequireComponent(typeof(PlayerActionRunner), typeof(PlayerMotor), typeof(HealthComponent))]
public sealed class PlayerAfterimageEffect : MonoBehaviour
{
    public const string StartEventKey = "vfx.afterimage.start";
    public const string StopEventKey = "vfx.afterimage.stop";

    [SerializeField] private PlayerAfterimageSettings _settings;
    [SerializeField] private SkinnedMeshRenderer[] _sources;
    // 渲染程序集通过持久事件接线，运行时程序集不反向依赖具体描边实现。
    [SerializeField] private UnityEvent<bool> _emissionChanged = new UnityEvent<bool>();
    private PlayerActionRunner _runner;
    private PlayerMotor _motor;
    private HealthComponent _health;
    private TimeStopController _timeStop;
    private PlayerAfterimageSnapshot[] _snapshots = Array.Empty<PlayerAfterimageSnapshot>();
    private bool _connected;
    private bool _timeStopActive;
    private bool _poolDirty;
    private bool _onlyAscending;
    private bool _waitingForTakeoff;
    private bool _firstSnapshot;
    private int _startJumpCount;
    private int _nextSnapshot;
    private long _instance;
    private float _sampleElapsed;
    private float _distance;
    private Vector3 _pathPosition;

    public PlayerAfterimageSettings Settings => _settings;
    public UnityEvent<bool> EmissionChanged => _emissionChanged;
    public IReadOnlyList<PlayerAfterimageSnapshot> Snapshots => _snapshots;
    public bool IsEmitting { get; private set; }
    public int CaptureCount { get; private set; }
    public int ActiveSnapshotCount
    {
        get
        {
            int count = 0;
            foreach (PlayerAfterimageSnapshot snapshot in _snapshots) if (snapshot.IsActive) count++;
            return count;
        }
    }

    private void Awake()
    {
        _runner = GetComponent<PlayerActionRunner>();
        _motor = GetComponent<PlayerMotor>();
        _health = GetComponent<HealthComponent>();
        _timeStop = GetComponent<TimeStopController>();
    }

    public void Configure(PlayerAfterimageSettings settings, SkinnedMeshRenderer[] sources)
    {
        _settings = settings;
        _sources = sources;
        _poolDirty = true;
    }

    private void OnEnable()
    {
        if (Application.isPlaying) Connect();
    }

    public bool Connect()
    {
        if (_connected) return true;
        if (_settings == null || _settings.Material == null || _sources == null || _sources.Length == 0) return false;
        EnsurePool();
        _runner.FrameEvent += OnFrameEvent;
        _runner.ActionEnded += OnActionEnded;
        _runner.ActionsReset += ClearAfterimages;
        _runner.Simulated += Simulate;
        _motor.MotionPathPoint += OnPathPoint;
        _motor.Teleported += OnTeleported;
        _health.Died += OnDied;
        if (_timeStop != null) _timeStop.TimeStopChanged += OnTimeStopChanged;
        _connected = true;
        ClearAfterimages();
        _emissionChanged.Invoke(false);
        OnTimeStopChanged(_timeStop != null && _timeStop.IsTimeStopping);
        return true;
    }

    private void OnValidate() => _poolDirty = true;

    private void EnsurePool()
    {
        int count = Mathf.Clamp(_settings.MaxSnapshots, 1, PlayerAfterimageSettings.MaximumPoolSize);
        if (!_poolDirty && _snapshots.Length == count) return;
        foreach (PlayerAfterimageSnapshot snapshot in _snapshots) snapshot.Dispose();
        _snapshots = new PlayerAfterimageSnapshot[count];
        for (int i = 0; i < count; i++) _snapshots[i] = new PlayerAfterimageSnapshot(_sources.Length, i);
        _nextSnapshot = 0;
        _poolDirty = false;
    }

    private void OnFrameEvent(ActionExecutionState state, ActionFrameEvent frameEvent)
    {
        if (frameEvent.EventKey == StartEventKey)
        {
            _instance = state.InstanceId;
            _onlyAscending = frameEvent.Value > 0f;
            _waitingForTakeoff = _onlyAscending;
            _startJumpCount = _motor.JumpCount;
            SetEmitting(true);
        }
        else if (frameEvent.EventKey == StopEventKey && _instance == state.InstanceId) StopEmission();
    }

    private void OnActionEnded(ActionExecutionState state, ActionExitReason reason)
    {
        if (reason == ActionExitReason.Reset || reason == ActionExitReason.Interrupted) ClearAfterimages();
        else if (_instance == state.InstanceId) StopEmission();
    }

    private void OnPathPoint(Vector3 position)
    {
        if (IsEmitting) _distance += Vector3.Distance(_pathPosition, position);
        _pathPosition = position;
    }

    private void OnTimeStopChanged(bool active)
    {
        _timeStopActive = active && _runner.IsConnected && _runner.isActiveAndEnabled
            && _motor.isActiveAndEnabled && _health.IsAlive;
        // 动作与时停各自持有开启来源，收招只能释放当前动作自己的来源。
        SetEmitting(_instance != 0 || _timeStopActive);
    }

    private void Simulate(float deltaTime)
    {
        if (!_health.IsAlive || !_motor.isActiveAndEnabled) { ClearAfterimages(); return; }
        OnTimeStopChanged(_timeStop != null && _timeStop.IsTimeStopping);
        if (deltaTime <= 0f) return;
        EnsurePool();
        foreach (PlayerAfterimageSnapshot snapshot in _snapshots) snapshot.Advance(deltaTime, _settings.Lifetime);
        if (_waitingForTakeoff)
        {
            if (_motor.JumpCount > _startJumpCount) _waitingForTakeoff = false;
        }
        if (_instance != 0 && _onlyAscending && !_waitingForTakeoff
            && (_motor.IsGrounded || _motor.IsWallSliding || _motor.Velocity.y <= 0f)) StopEmission();
        if (!IsEmitting || _waitingForTakeoff && !_timeStopActive) return;
        _sampleElapsed += deltaTime;
        bool sample = _firstSnapshot ? _distance > 0f
            : _sampleElapsed + 0.000001f >= _settings.SampleInterval && _distance >= _settings.MinDistance;
        if (!sample) return;
        // 只保留实际采样时刻的姿态；同一帧不能复制当前姿态并倒推多个虚假的旧位置。
        if (_snapshots[_nextSnapshot].Capture(_sources, transform.position)) CaptureCount++;
        _nextSnapshot = (_nextSnapshot + 1) % _snapshots.Length;
        _sampleElapsed = _distance = 0f;
        _firstSnapshot = false;
    }

    private void StopEmission()
    {
        _instance = 0;
        _waitingForTakeoff = false;
        SetEmitting(_timeStopActive);
    }

    private void SetEmitting(bool value)
    {
        if (IsEmitting == value) return;
        if (value)
        {
            _sampleElapsed = _distance = 0f;
            _pathPosition = transform.position;
            _firstSnapshot = true;
        }
        IsEmitting = value;
        _emissionChanged.Invoke(value);
    }

    public void ClearAfterimages()
    {
        _timeStopActive = false;
        StopEmission();
        foreach (PlayerAfterimageSnapshot snapshot in _snapshots) snapshot.Clear();
        _pathPosition = transform.position;
        _sampleElapsed = _distance = 0f;
    }

    private void OnTeleported(Vector3 delta) => ClearAfterimages();
    private void OnDied(HealthComponent health) => ClearAfterimages();

    private void LateUpdate()
    {
        if (!_connected) return;
        if (!_runner.isActiveAndEnabled || !_runner.IsConnected || !_motor.isActiveAndEnabled || !_health.IsAlive)
        { ClearAfterimages(); return; }
        foreach (PlayerAfterimageSnapshot snapshot in _snapshots) snapshot.Draw(_settings);
    }

    public void Disconnect()
    {
        ClearAfterimages();
        if (!_connected) return;
        _runner.FrameEvent -= OnFrameEvent;
        _runner.ActionEnded -= OnActionEnded;
        _runner.ActionsReset -= ClearAfterimages;
        _runner.Simulated -= Simulate;
        _motor.MotionPathPoint -= OnPathPoint;
        _motor.Teleported -= OnTeleported;
        _health.Died -= OnDied;
        if (_timeStop != null) _timeStop.TimeStopChanged -= OnTimeStopChanged;
        _connected = false;
    }

    private void OnDisable() => Disconnect();
    private void OnDestroy()
    {
        Disconnect();
        foreach (PlayerAfterimageSnapshot snapshot in _snapshots) snapshot.Dispose();
        _snapshots = Array.Empty<PlayerAfterimageSnapshot>();
    }
}
