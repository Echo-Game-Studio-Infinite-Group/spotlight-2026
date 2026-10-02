using System.Collections.Generic;
using UnityEngine;

// 语义动作缓冲（3C 移动重写线）——与战斗侧 InputBuffer 并存，服务移动/技能输入链：
// PlayerInputReader（新 Input System 回调）把 Attack/Skill/Jump 动作推入本缓冲，ConsumeCombo 解析组合意图。
// 与战斗仲裁（InputBuffer.PeekIntent/ConsumeFor 两段式）的归一待 D1 输入归属裁决——两条链各自喂各自，
// 禁止交叉读写，谁赢删谁（InputBuffer 的优先级/零副作用语义有 15 个测试锁定，ActionBuffer 当前仅覆盖其子集）
public sealed class ActionBuffer
{
    public enum Action { Attack, Skill, Jump }
    public enum Intent { None, Rashomon, PushSlash, Turnaround, Accelerate, HighJump, TimeStop }
    private struct Entry { public Action Action; public float Time; }
    private readonly List<Entry> _entries = new List<Entry>(16);
    private readonly float _window;
    public ActionBuffer(float window = 0.12f) => _window = window;
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
