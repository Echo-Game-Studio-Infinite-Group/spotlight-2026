using System;
using System.Collections.Generic;
using UnityEngine;

// 跟随整局常驻场景，不跟随可卸载房间；重开会创建新实例并清除本局记录。
[DefaultExecutionOrder(-80)]
[DisallowMultipleComponent]
public sealed class ChaosManager : MonoBehaviour
{
    public const int SpecialValue = 999;
    public static ChaosManager Instance { get; private set; }
    public static bool Exists => Instance != null;
    [SerializeField] private ChaosSettings _settings;
    private readonly HashSet<string> _brokenLamps = new HashSet<string>(StringComparer.Ordinal);
    private int _value;
    public int Value => _value;
    public ChaosSettings Settings => _settings;
    public int NormalMaximum => _settings != null ? _settings.NormalMaximum : 100;
    public bool IsNegative => _value < 0;
    public bool IsSpecial => _value == SpecialValue;
    public bool NormalGoalReached => !IsSpecial && _value >= NormalMaximum;
    // 只提供条件，不在此处决定组环、退出循环或最终房间的优先级。
    public bool IsInLoopRange => !IsNegative && !IsSpecial &&
        (_value <= (_settings != null ? _settings.LowLoopMaximum : 30) ||
        _value >= (_settings != null ? _settings.HighLoopMinimum : 80) && _value <= NormalMaximum);
    public event Action<int, int> Changed;
    public event Action RunReset;

    private void Awake()
    {
        if (Exists && Instance != this) { Destroy(this); return; }
        Instance = this;
        ResetRun();
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    public void Configure(ChaosSettings settings)
    {
        _settings = settings;
        ResetRun();
    }

    public void ResetRun()
    {
        _brokenLamps.Clear();
        SetRaw(_settings != null ? _settings.InitialValue : 0);
        RunReset?.Invoke();
    }

    public void SetNormalValue(int value) => SetRaw(Mathf.Clamp(value, 0, NormalMaximum));

    // 特殊值只能显式进入，普通增减不会把 999 当成正常通关值。
    public bool SetSpecialValue(int value)
    {
        if (value >= 0 && value != SpecialValue) return false;
        SetRaw(value);
        return true;
    }

    public void ApplyDeathPenalty(bool allowNegative = false)
    {
        if (IsSpecial) return;
        long result = (long)_value - (_settings != null ? _settings.DeathPenalty : 10);
        int lower = allowNegative ? int.MinValue : 0;
        SetRaw((int)Math.Max(lower, Math.Min(NormalMaximum, result)));
    }

    public bool WasLampBroken(string roomId, string lampId) => _brokenLamps.Contains(LampKey(roomId, lampId));

    public bool TryBreakLamp(string roomId, string lampId, int gain)
    {
        if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(lampId) || gain <= 0 || IsSpecial) return false;
        if (!_brokenLamps.Add(LampKey(roomId, lampId))) return false;
        SetRaw((int)Math.Max(int.MinValue, Math.Min(NormalMaximum, (long)_value + gain)));
        return true;
    }

    public void ClearRoomLampRecords(string roomId)
    {
        string prefix = roomId + "\n";
        _brokenLamps.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string LampKey(string roomId, string lampId) => roomId + "\n" + lampId;
    private void SetRaw(int value)
    {
        if (_value == value) return;
        int previous = _value;
        _value = value;
        Changed?.Invoke(previous, value);
    }
}
