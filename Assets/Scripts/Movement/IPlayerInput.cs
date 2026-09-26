// PlayerMotor 的输入缝：只暴露 Motor 实际查询的项，生产实现包旧版 Input，测试可注入脚本桩。
// 约束（AGENTS.md 第 4 条）：不引入新 Input System；按下沿的帧内捕获语义由实现方负责。
public interface IPlayerInput
{
    float Horizontal { get; }   // 水平轴（旧版 Input.GetAxisRaw("Horizontal")）
    float Vertical { get; }     // 垂直轴（旧版 Input.GetAxisRaw("Vertical")）
    bool RunHeld { get; }       // 奔跑按住（Shift）
    bool RunPressed { get; }    // 奔跑按下沿（Shift GetKeyDown）
    bool JumpPressed { get; }   // 跳跃按下沿（Space GetKeyDown）
    bool SlideTrigger { get; }  // 滑铲触发沿（按 Shift，或持有 Shift 时松开前进）
}
