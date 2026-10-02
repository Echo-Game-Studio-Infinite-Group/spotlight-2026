using UnityEngine;

[CreateAssetMenu(fileName = "MovementParams", menuName = "超高速行者/MovementParams")]
public sealed class MovementParams : ScriptableObject
{
    [Header("地面")]
    [Min(0.01f)] public float GroundSpeedThreshold = 10f;
    [Min(0f)] public float WalkSpeed = 3f;
    [Min(0f)] public float RunAccel = 10f;
    [Min(0f)] public float GroundFriction = 6f;
    [Min(0f)] public float FrictionExemptWindow = 0.2f;
    public PumpMode Pump = PumpMode.WindowPump;
    [Min(0.01f)] public float MaxSpeed = 120f;

    [Header("空中与转向")]
    [Min(0f)] public float Gravity = 20f;
    [Min(0f)] public float JumpSpeed = 8f;
    [Min(0f)] public float AirControl;
    [Min(0f)] public float JumpBufferWindow = 0.12f;
    [Min(0f)] public float SpeedSteerTurnRate = 540f;
    [Min(0f)] public float SteerMinSpeedRatio = 0.5f;

    [Header("滑铲")]
    [Min(0f)] public float SlideSpeedRatio = 0.8f;
    [Min(0f)] public float SlideDecel = 15f;
    [Min(0f)] public float SlideEndSpeed = 2f;
    [Min(0.1f)] public float SlideCapsuleHeight = 1f;

    [Header("划墙与墙面 bhop")]
    [Range(0f, 90f)] public float WallMinApproachAngle = 30f;
    [Range(0f, 0.7f)] public float WallNormalMaxUpDot = 0.3f;
    [Min(0f)] public float WallGraceTime = 0.15f;
    [Min(0f)] public float WallFriction = 3f;
    [Min(0f)] public float WallGravityScale = 0.4f;
    [Min(0f)] public float WallMaxFallSpeed = 6f;
    [Min(0f)] public float WallJumpBoost = 1.5f;
    [Range(1f, 90f)] public float WallJumpAngle = 45f;
    [Min(0f)] public float WallJumpUpImpulse = 8f;
    [Min(0f)] public float WallJumpCooldown = 0.2f;
    [Min(0.01f)] public float WallRearmDistance = 0.15f;
    [Min(0.01f)] public float WallProbeDistance = 0.08f;
    [Range(0f, 45f)] public float WallSeamAngle = 15f;

    [Header("碰撞胶囊")]
    [Min(0f)] public float CapsuleShrinkStartSpeed = 10f;
    [Min(0f)] public float CapsuleShrinkEndSpeed = 25f;
    [Min(0.05f)] public float CapsuleBaseRadius = 0.5f;
    [Min(0.05f)] public float CapsuleMinRadius = 0.3f;
    [Min(0.1f)] public float CapsuleBaseHeight = 2f;
    [Min(0.1f)] public float CapsuleFastHeight = 1.7f;
    public LayerMask CollisionMask = ~0;
    [Range(0.1f, 1f)] public float MoveSegmentRadiusRatio = 0.5f;
    [Min(1)] public int MaxMoveSegments = 128;

    [Header("能量（按玩家时间秒结算）")]
    [Min(0f)] public float EnergyMax = 200f;
    [Min(0f)] public float EnergyPerSecondPerExcessSpeed = 60f;

    private void OnValidate()
    {
        MaxSpeed = Mathf.Max(GroundSpeedThreshold, MaxSpeed);
        CapsuleBaseHeight = Mathf.Max(CapsuleBaseHeight, CapsuleBaseRadius * 2f);
        CapsuleFastHeight = Mathf.Max(CapsuleFastHeight, CapsuleMinRadius * 2f);
        WallRearmDistance = Mathf.Max(WallRearmDistance, WallProbeDistance + 0.01f);
        MaxMoveSegments = Mathf.Max(1, MaxMoveSegments);
    }
}
