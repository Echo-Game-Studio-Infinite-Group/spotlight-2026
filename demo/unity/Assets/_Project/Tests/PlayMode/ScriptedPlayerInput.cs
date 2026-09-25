using UnityEngine;

// 测试桩：仿真的“理想 bhop 机器人”——W+奔跑恒按住；着地且水平速度达到阈值后的第一个 Update 发跳跃按下沿。
// 与 demo/movement-sim 的 RunCycles 语义对应：着地后先奔跑结算（泵油），再起跳。
public sealed class ScriptedPlayerInput : IPlayerInput
{
    private readonly PlayerMotor _motor;
    private readonly float _jumpSpeedThreshold;
    private bool _armed = true;

    public int JumpRequests { get; private set; }

    public ScriptedPlayerInput(PlayerMotor motor, float jumpSpeedThreshold)
    {
        _motor = motor;
        _jumpSpeedThreshold = jumpSpeedThreshold;
    }

    public float Horizontal => 0f;   // 直线前进
    public float Vertical => 1f;     // W 恒按住
    public bool RunHeld => true;     // 奔跑恒按住
    public bool RunPressed => false;
    public bool SlideTrigger => false;

    // 空中重新武装；着地且达到阈值后只发一次按下沿（模拟落地窗口内的 1 tick 起跳）
    public bool JumpPressed
    {
        get
        {
            if (!_motor.IsGrounded)
            {
                _armed = true;
                return false;
            }
            if (_armed && _motor.HorizontalSpeed >= _jumpSpeedThreshold)
            {
                _armed = false;
                JumpRequests++;
                return true;
            }
            return false;
        }
    }
}
