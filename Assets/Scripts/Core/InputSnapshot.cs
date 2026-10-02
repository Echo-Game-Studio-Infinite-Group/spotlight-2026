using UnityEngine;

// 逻辑帧输入快照（框架 4.1"统一采样"）：PlayerInputReader 每 Update 从 Input System 采样写入 InputSampler，
// FixedUpdate 消费。只承载"按住"类状态（修饰键/移动轴）；按下沿走 InputBuffer 缓冲——两类语义分开，
// 同一按下沿只消费一次的保证才能成立。字段为逻辑语义名，不出现物理键
public struct InputSnapshot
{
    public float Horizontal;  // 左右轴
    public float Vertical;    // 前后轴
    public bool ForwardHeld;  // W
    public bool BackHeld;     // S
    public bool LeftHeld;     // A
    public bool RightHeld;    // D
    public bool SprintHeld;   // 奔跑 / 闪避修饰（Shift）
    public bool SlideHeld;    // 滑铲（Ctrl，独立于 Shift——修掉旧"按住 Shift 松 W 误滑铲"）
    public bool DashHeld;     // 冲刺修饰（Q+左键）
    public bool AttackHeld;   // 左键按住
    public bool SkillHeld;    // 右键按住（组合修饰 / 时停长按）
    public bool JumpHeld;     // 空格按住

    public static readonly InputSnapshot Empty = default;
}
