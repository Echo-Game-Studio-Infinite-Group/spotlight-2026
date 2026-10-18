using UnityEngine;

[CreateAssetMenu(fileName = "MovementParams", menuName = "超高速行者/MovementParams")]
public sealed class MovementParams : ScriptableObject
{
    [Header("地面")]
    [Min(0.01f)] public float GroundSpeedThreshold = 6f;
    [Tooltip("普通冲刺与低速/高速动作判定共用的地速阈值比例")]
    [Range(0f, 1f)] public float LowSpeedRatio = 1f;
    [Min(0f)] public float RunAccel = 4f;
    [Min(0f)] public float GroundFriction = 2f;
    [Min(0f)] public float GroundStopSpeed = 0.05f;
    [Min(0f)] public float FrictionExemptWindow = 0.2f;
    public PumpMode Pump = PumpMode.WindowPump;
    [Min(0.01f)] public float MaxSpeed = 30f;

    [Header("低速七方向行走")]
    [Tooltip("A/D、W+A/W+D、S/A+S/S+D 共用的行走速度；W 单独奔跑，高速 S 保持刹车")]
    [Min(0.01f)] public float WalkSpeed = 3f;
    [Tooltip("行走接近目标速度的加减速度，单位 m/s²")]
    [Min(0f)] public float WalkAcceleration = 12f;
    [Min(1f)] public float WalkMaxAnimationRate = 2f;

    [Header("地面刹车与速度回收")]
    [Tooltip("高速按住 S 时额外扣除的水平速度，单位 m/s²；保持刹车直到松开，不包含地面、滑铲或划墙摩擦")]
    [Min(0f)] public float BrakeDeceleration = 24f;
    [Tooltip("回收摩擦停止后，跳跃仍可返还暂存速度的玩家时间")]
    [Min(0f)] public float BrakeRecoveryWindow = 0.7f;
    [Min(0f)] public float BrakeStopSpeed = 0.05f;

    [Header("台阶与坡面")]
    [Min(0f)] public float MaxStepUp = 0.3f;
    [Min(0f)] public float MaxStepDown = 0.35f;
    [Range(0f, 80f)] public float MaxGroundAngle = 45f;
    [Min(0.001f)] public float GroundTolerance = 0.03f;
    [Tooltip("仅平滑台阶造成的高度修正，不延迟起跳和下落。")]
    [Min(0f)] public float StepVisualSmoothTime = 0.08f;

    [Header("空中与转向")]
    [Min(0f)] public float Gravity = 20f;
    [Min(0f)] public float JumpSpeed = 8f;
    [Min(0f)] public float AirControl;
    [Min(0f)] public float JumpBufferWindow = 0.12f;
    [Min(0.01f)] public float FacingSmoothTime = 0.12f;

    [Header("滑铲")]
    [Min(0f)] public float SlideSpeedRatio = 0.8f;
    [Min(0f)] public float SlideDecel = 1f;
    [Min(0.1f)] public float SlideCapsuleHeight = 1f;
    [Tooltip("滑铲及起身姿态从角色根节点向前占用的长度；只增加前向保护，不加宽窄缝碰撞。")]
    [Min(0f)] public float SlideForwardExtent = 1.1f;
    [Tooltip("低顶下撞停后，主动移动脱困的目标速度；已有高速惯性不受此上限截断。")]
    [Min(0f)] public float SlideBlockedMoveSpeed = 2f;

    [Header("划墙与墙面 bhop")]
    [Range(0f, 90f)] public float WallMaxApproachAngle = 70f;
    [Range(0f, 0.7f)] public float WallNormalMaxUpDot = 0.3f;
    [Tooltip("入墙后免重力与水平摩擦的玩家时间窗口，竖直速度仍受独立阻尼影响")]
    [Min(0f)] public float WallGraceTime = 2.5f;
    [Tooltip("仅在上墙保护窗口内衰减竖直速度，单位为每秒；设为 0 关闭阻尼")]
    [Min(0f)] public float WallVerticalFriction = 2f;
    [Tooltip("保护窗口内启用竖直阻尼时，低于此速度的上升或下落惯性归零")]
    [Min(0f)] public float WallVerticalStopSpeed = 0.01f;
    [Min(0f)] public float WallFriction = 2.5f;
    [Tooltip("滑墙减速到该水平速度时视为停止，避免指数摩擦永远无法归零")]
    [Min(0f)] public float WallStopSpeed = 0.01f;
    [Min(0f)] public float WallGravityScale = 0.6f;
    [Min(0f)] public float WallMaxFallSpeed = 1f;
    [Min(0f)] public float WallJumpBoost = 10f;
    [Range(1f, 90f)] public float WallJumpAngle = 25f;
    [Min(0f)] public float WallJumpUpImpulse = 8f;
    [Min(0f)] public float WallJumpCooldown = 0.2f;
    [Tooltip("蹬墙跳发动后保持发动时朝向的玩家时间")]
    [Min(0f)] public float WallJumpFacingLockTime = 0.1f;
    [Min(0.01f)] public float WallRearmDistance = 0.17f;
    [Min(0.01f)] public float WallProbeDistance = 0.08f;
    [Min(0f)] public float WallContactGraceTime = 0.08f;
    [Range(0f, 45f)] public float WallSeamAngle = 15f;

    [Header("碰撞胶囊")]
    [Min(0f)] public float CapsuleShrinkStartSpeed = 10f;
    [Min(0f)] public float CapsuleShrinkEndSpeed = 25f;
    [Min(0.05f)] public float CapsuleBaseRadius = 0.3793888f;
    [Min(0.05f)] public float CapsuleMinRadius = 0.15f;
    [Min(0.1f)] public float CapsuleBaseHeight = 1.7f;
    [Min(0.1f)] public float CapsuleFastHeight = 1.7f;
    public LayerMask CollisionMask = ~0;
    [Range(0.1f, 1f)] public float MoveSegmentRadiusRatio = 0.50018f;
    [Min(1)] public int MaxMoveSegments = 128;

    public float LowSpeedThreshold => GroundSpeedThreshold * LowSpeedRatio;

    private void OnValidate()
    {
        MaxSpeed = Mathf.Max(GroundSpeedThreshold, MaxSpeed);
        WalkSpeed = Mathf.Clamp(WalkSpeed, .01f, Mathf.Max(.01f, LowSpeedThreshold));
        CapsuleBaseHeight = Mathf.Max(CapsuleBaseHeight, CapsuleBaseRadius * 2f);
        CapsuleFastHeight = Mathf.Max(CapsuleFastHeight, CapsuleMinRadius * 2f);
        WallRearmDistance = Mathf.Max(WallRearmDistance, WallProbeDistance + 0.01f);
        MaxMoveSegments = Mathf.Max(1, MaxMoveSegments);
        GroundTolerance = Mathf.Max(.001f, GroundTolerance);
    }
}
