using UnityEngine;

public sealed class ScriptedPlayerInput : IPlayerInput
{
    private readonly PlayerMotor _motor;
    private readonly float _threshold;
    private bool _armed = true;
    public int JumpRequests { get; private set; }
    public ScriptedPlayerInput(PlayerMotor motor, float threshold) { _motor = motor; _threshold = threshold; }
    public PlayerInputFrame ReadFrame()
    {
        if (!_motor.IsGrounded) _armed = true;
        bool jump = _armed && _motor.IsGrounded && _motor.HorizontalSpeed >= _threshold;
        if (jump) { _armed = false; JumpRequests++; }
        return new PlayerInputFrame { Move = Vector2.up, SprintHeld = true, JumpPressed = jump, JumpTime = TimeManager.UnscaledTime };
    }
    public void Clear() { _armed = true; JumpRequests = 0; }
}
