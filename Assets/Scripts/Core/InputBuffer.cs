using System;
using UnityEngine;

// 战斗输入意图：组合键仲裁表的输出。
// 触发型意图各有触发键（提交时消费缓冲条目）；TimeStop 为持续型（长按右键，不消费任何条目）
public enum InputIntent
{
    None,
    Attack,     // 普攻（左键；速度门槛决定常态/高速变体）
    Rashomon,   // 连斩（按住右键+W 点左键）
    PushSlash,  // 推斩（按住右键点左键）
    Turnaround, // 折返（S+右键）
    Accelerate, // 加速（W+右键）
    HighJump,   // 高跳（右键+空格）
    TimeStop,   // 时停（长按右键，持续型——归技能层结算，动作层不消费）
    Dash,       // 冲刺（按住 Q 点左键；三段同键续接）
    Dodge,      // 闪避（A/D+Shift）
}

// 缓冲参数外置（AGENTS.md 第 2 条：可调参数不硬编码在逻辑里），由装配层在 Inspector 暴露
[Serializable]
public struct InputBufferConfig
{
    [Tooltip("缓冲窗口（秒）：按下沿在窗口内可被消费，超期作废")]
    public float BufferWindow;

    [Tooltip("环形缓冲容量（同时挂起的按下沿条数上限）")]
    public int Capacity;

    public static InputBufferConfig Default => new InputBufferConfig { BufferWindow = 0.12f, Capacity = 16 };
}

// 环形输入缓冲 + 组合键仲裁（框架 4.1）
// 修复旧版吞键病灶：仲裁"先匹配全部条件，再消费触发键"——旧版先 Consume 触发键再判修饰键，
// 规则顺序一短路（右键被折返检查吃掉）后续规则（加速）永远取不到该键，组合被静默吞掉
// 时间戳走 unscaled：hitstop/时缓不应拉长输入窗口（输入缓冲属"不缩放"层，框架 4.3）
public class InputBuffer
{
    private struct Entry
    {
        public KeyCode Key;
        public float Time;
    }

    private readonly float _bufferWindow;
    private readonly Entry[] _ring;
    private int _write; // 下一个写入位
    private int _count;

    // 组合键规则表：次序即优先级（特定组合在前，泛用触发在后）。
    // 优先级基线（待哈士奇确认）：连斩 > 推斩 > 折返 > 加速/高跳 > 时停；冲刺/普攻/闪避为本版补充占位
    private static readonly (InputIntent Intent, KeyCode Trigger, Func<InputSnapshot, bool> Modifiers)[] Rules =
    {
        (InputIntent.Rashomon, KeyCode.Mouse0, s => s.Mouse1Held && s.WHeld),
        (InputIntent.Dash, KeyCode.Mouse0, s => s.QHeld),
        (InputIntent.PushSlash, KeyCode.Mouse0, s => s.Mouse1Held),
        (InputIntent.Attack, KeyCode.Mouse0, s => true),
        (InputIntent.Dodge, KeyCode.LeftShift, s => s.AHeld || s.DHeld),
        (InputIntent.Turnaround, KeyCode.Mouse1, s => s.SHeld),
        (InputIntent.Accelerate, KeyCode.Mouse1, s => s.WHeld),
        (InputIntent.HighJump, KeyCode.Space, s => s.Mouse1Held),
    };

    public InputBuffer(float bufferWindow, int capacity)
    {
        _bufferWindow = Mathf.Max(0.01f, bufferWindow);
        _ring = new Entry[Mathf.Max(4, capacity)];
    }

    public InputBuffer(InputBufferConfig config) : this(config.BufferWindow, config.Capacity) { }

    public void Push(KeyCode key)
    {
        _ring[_write].Key = key;
        _ring[_write].Time = TimeManager.UnscaledTime;
        _write = (_write + 1) % _ring.Length;
        _count = Mathf.Min(_count + 1, _ring.Length); // 满时覆盖最旧条目
    }

    /// <summary>
    /// 仲裁（不消费）：按优先级找第一条"修饰键命中 + 触发键已缓冲"的规则。
    /// 消费延后到动作层统一提交（PlayerCombat.Commit）——激活失败零副作用（不吞输入）的保证在此成立
    /// </summary>
    public InputIntent PeekIntent(in InputSnapshot snapshot)
    {
        Prune();
        for (int i = 0; i < Rules.Length; i++)
        {
            if (HasBuffered(Rules[i].Item2) && Rules[i].Item3(snapshot))
            {
                return Rules[i].Item1;
            }
        }
        // 时停为长按持续型：无触发键、不消费；右键按下沿残留条目由窗口过期自然清理
        if (snapshot.Mouse1Held) return InputIntent.TimeStop;
        return InputIntent.None;
    }

    /// <summary>提交时消费该意图的触发键（PeekIntent 与 ConsumeFor 必须喂同一份快照，保证判定一致）</summary>
    public void ConsumeFor(InputIntent intent, in InputSnapshot snapshot)
    {
        if (intent == InputIntent.None || intent == InputIntent.TimeStop) return;
        for (int i = 0; i < Rules.Length; i++)
        {
            if (Rules[i].Item1 == intent)
            {
                Consume(Rules[i].Item2);
                return;
            }
        }
    }

    /// <summary>缓冲里是否还有该键的未消费按下沿（不删除）</summary>
    public bool HasBuffered(KeyCode key)
    {
        Prune();
        for (int i = 0; i < _count; i++)
        {
            if (_ring[IndexFromNewest(i)].Key == key) return true;
        }
        return false;
    }

    /// <summary>消费该键最近一次按下沿；无则返回 false</summary>
    public bool Consume(KeyCode key)
    {
        Prune();
        for (int i = 0; i < _count; i++)
        {
            int idx = IndexFromNewest(i);
            if (_ring[idx].Key == key)
            {
                RemoveAt(idx);
                return true;
            }
        }
        return false;
    }

    /// <summary>清空缓冲：暂停进入/重开复位时调用——旧按下沿不得在恢复后误触发（框架 4.1）</summary>
    public void Clear()
    {
        _write = 0;
        _count = 0;
    }

    private void Prune()
    {
        float now = TimeManager.UnscaledTime;
        while (_count > 0 && now - _ring[IndexFromOldest(0)].Time > _bufferWindow)
        {
            _count--;
        }
    }

    private int IndexFromNewest(int offset) => (_write - 1 - offset + _ring.Length * 2) % _ring.Length;
    private int IndexFromOldest(int offset) => (_write - _count + offset + _ring.Length) % _ring.Length;

    // 删除任意位：后续条目前移覆盖，保持旧→新顺序；被移除条目落在新写入区，随下次 Push 覆盖
    private void RemoveAt(int idx)
    {
        int i = idx;
        while (i != _write)
        {
            int next = (i + 1) % _ring.Length;
            _ring[i] = _ring[next];
            i = next;
        }
        _write = (_write - 1 + _ring.Length) % _ring.Length;
        _count--;
    }
}
