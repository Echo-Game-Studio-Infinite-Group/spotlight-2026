using System.Collections.Generic;
using UnityEngine;

// 只缓存动作语义，不绑定设备；调用方在成功执行动作时消费。
public sealed class InputBuffer
{
    public enum Action { Attack, Skill, Jump }
    public enum Intent { None, Rashomon, PushSlash, Turnaround, Accelerate, HighJump, TimeStop }
    private struct Entry { public Action Action; public float Time; }
    private readonly List<Entry> _entries = new List<Entry>(16);
    private readonly float _window;
    public InputBuffer(float window = 0.12f) => _window = window;
    public void Clear() => _entries.Clear();
    public void Push(Action action, float time)
    {
        Prune(time);
        if (_entries.Count == 16) _entries.RemoveAt(0);
        _entries.Add(new Entry { Action = action, Time = time });
    }
    public bool Consume(Action action, float time)
    {
        Prune(time);
        int index = _entries.FindIndex(entry => entry.Action == action);
        if (index < 0) return false;
        _entries.RemoveAt(index);
        return true;
    }
    public Intent ConsumeCombo(float time, bool skillHeld, Vector2 move)
    {
        if (skillHeld && Consume(Action.Attack, time)) return move.y > 0f ? Intent.Rashomon : Intent.PushSlash;
        if (move.y != 0f && Consume(Action.Skill, time)) return move.y < 0f ? Intent.Turnaround : Intent.Accelerate;
        if (skillHeld && Consume(Action.Jump, time)) return Intent.HighJump;
        return skillHeld ? Intent.TimeStop : Intent.None;
    }
    private void Prune(float now) => _entries.RemoveAll(entry => now - entry.Time > _window);
}
