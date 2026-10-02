using UnityEngine;

// 逻辑帧输入快照（框架 4.1"统一采样"）：InputSampler 每 Update 采样一次，FixedUpdate 消费
// 只承载"按住"类状态（修饰键/移动轴）；按下沿走 InputBuffer 缓冲——两类语义分开，
// 同一按下沿只消费一次的保证才能成立
public struct InputSnapshot
{
    public float Horizontal;  // A/D 轴
    public float Vertical;    // W/S 轴
    public bool WHeld;
    public bool SHeld;
    public bool AHeld;
    public bool DHeld;
    public bool ShiftHeld;    // 奔跑 / 闪避修饰
    public bool CtrlHeld;     // 下蹲 / 滑铲（Ctrl 独立于 Shift——修掉旧"按住 Shift 松 W 误滑铲"）
    public bool QHeld;        // 冲刺修饰（Q+左键，林晓风稿）
    public bool Mouse0Held;   // 左键（普攻触发）
    public bool Mouse1Held;   // 右键（组合修饰 / 时停长按）
    public bool JumpHeld;     // 空格

    public static readonly InputSnapshot Empty = default;
}
