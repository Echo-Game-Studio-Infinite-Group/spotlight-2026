using UnityEngine;

// 120ms 环形输入缓冲：按键按下沿先进缓冲，再由组合键仲裁表映射为意图
// 时间戳走 unscaled：hitstop/时缓不应拉长输入窗口
public class InputBuffer
{
    public enum Intent
    {
        None,
        Rashomon,   // 连斩
        PushSlash,  // 推斩
        Turnaround, // 折返
        Accelerate, // 加速
        HighJump,   // 高跳
        TimeStop,   // 时停
    }

    private struct Entry
    {
        public KeyCode Key;
        public float Time;
    }

    private const float BufferWindow = 0.12f;
    private const int Capacity = 16;

    private readonly Entry[] _ring = new Entry[Capacity];
    private int _write; // 下一个写入位
    private int _count;

    public void Push(KeyCode key)
    {
        _ring[_write].Key = key;
        _ring[_write].Time = TimeManager.UnscaledTime;
        _write = (_write + 1) % Capacity;
        _count = Mathf.Min(_count + 1, Capacity); // 满时覆盖最旧条目
    }

    // 战斗组合键涉及的按键集合，由动作层每帧喂入；换输入方案时只改这里
    public void PollBattleKeys()
    {
        if (Input.GetKeyDown(KeyCode.Mouse0)) Push(KeyCode.Mouse0);
        if (Input.GetKeyDown(KeyCode.Mouse1)) Push(KeyCode.Mouse1);
        if (Input.GetKeyDown(KeyCode.W)) Push(KeyCode.W);
        if (Input.GetKeyDown(KeyCode.S)) Push(KeyCode.S);
        if (Input.GetKeyDown(KeyCode.Space)) Push(KeyCode.Space);
    }

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

    // 组合键仲裁（占位实现，优先级表内容待哈士奇确认）：连斩 > 推斩 > 折返 > 加速/高跳 > 时停
    // 修饰键读实时按住状态，触发键走缓冲；时停为长按持续型，调用方自行做持续结算
    public Intent ConsumeCombo()
    {
        Prune();
        bool rmbHeld = Input.GetKey(KeyCode.Mouse1);

        if (rmbHeld && Input.GetKey(KeyCode.W) && Consume(KeyCode.Mouse0)) return Intent.Rashomon;
        if (rmbHeld && Consume(KeyCode.Mouse0)) return Intent.PushSlash;
        if (Consume(KeyCode.Mouse1) && Input.GetKey(KeyCode.S)) return Intent.Turnaround;
        if (Consume(KeyCode.Mouse1) && Input.GetKey(KeyCode.W)) return Intent.Accelerate;
        if (rmbHeld && Consume(KeyCode.Space)) return Intent.HighJump;
        if (rmbHeld) return Intent.TimeStop;
        return Intent.None;
    }

    private void Prune()
    {
        float now = TimeManager.UnscaledTime;
        while (_count > 0 && now - _ring[IndexFromOldest(0)].Time > BufferWindow)
        {
            _count--;
        }
    }

    private int IndexFromNewest(int offset) => (_write - 1 - offset + Capacity * 2) % Capacity;
    private int IndexFromOldest(int offset) => (_write - _count + offset + Capacity) % Capacity;

    // 删除任意位：后续条目前移覆盖，保持旧→新顺序；被移除条目落在新写入区，随下次 Push 覆盖
    private void RemoveAt(int idx)
    {
        int i = idx;
        while (i != _write)
        {
            int next = (i + 1) % Capacity;
            _ring[i] = _ring[next];
            i = next;
        }
        _write = (_write - 1 + Capacity) % Capacity;
        _count--;
    }
}
