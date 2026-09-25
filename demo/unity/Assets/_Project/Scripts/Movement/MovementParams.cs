using UnityEngine;

// 移动手感层的全部可调参数（数据驱动，禁止在代码里散写魔法数字）
// 策划案数值均为占位符，以《超高速行者（暂定） v1.03》第 1-3 节为语义来源，调参以实际测试为准
[CreateAssetMenu(fileName = "MovementParams", menuName = "超高速行者/MovementParams")]
public class MovementParams : ScriptableObject
{
    [Header("地速与奔跑（策划案第 1-2 节）")]
    [Tooltip("地速阈值：摩擦门槛 / 矢量转换器起征点 / 受击框变细起点")]
    public float GroundSpeedThreshold = 10f;
    [Tooltip("行走目标速度：行走时 Quake accelerate 的 wishspeed 换成该值")]
    public float WalkSpeed = 3f;
    [Tooltip("Quake accelerate 系数：v += wishdir * min(accel * wishspeed * dt, addspeed)")]
    public float RunAccel = 10f;
    [Tooltip("地面摩擦系数 mu：v *= max(0, 1 - mu * dt)")]
    public float GroundFriction = 6f;
    [Tooltip("落地免摩擦窗口（秒）——兔子跳加速的唯一来源")]
    public float FrictionExemptWindow = 0.2f;

    [Header("跳跃与空中（策划案第 2 节）")]
    public float Gravity = 20f;
    public float JumpSpeed = 8f;
    [Tooltip("空中加速系数；本次骨架恒 0（不做空中加速），字段仅留接口")]
    public float AirControl = 0f;
    [Tooltip("跳跃预输入缓冲（秒）：滞空/落地前按跳仍在窗口内生效")]
    public float JumpBufferWindow = 0.12f;

    [Header("速度转向（策划案第 1 节：按前进保留速度大小、转向摄像机方向）")]
    [Tooltip("速度转向角速度上限（度/秒）。超过阈值后加速投影为负拐不了弯，高速转向全靠这条；限速率避免瞬移转向")]
    public float SpeedSteerTurnRate = 540f;
    [Tooltip("水平速度达到地速阈值该比例以上才启用速度转向；更低速度的转向交给加速投影")]
    public float SteerMinSpeedRatio = 0.5f;

    [Header("滑铲（策划案第 3 节）")]
    [Tooltip("水平速度达到地速阈值该比例时，Shift 才触发滑铲")]
    public float SlideSpeedRatio = 0.8f;
    [Tooltip("滑铲线性减速度（单位/秒²）：滑铲期间速度快速衰减")]
    public float SlideDecel = 15f;
    [Tooltip("低于该水平速度自然退出滑铲")]
    public float SlideEndSpeed = 2f;
    [Tooltip("滑铲期间碰撞胶囊高度（限高门净空须介于本值与常态高度之间）")]
    public float SlideCapsuleHeight = 1f;

    [Header("蹬墙（策划案第 1 节）")]
    [Tooltip("速度与墙面夹角小于该角度不可蹬墙")]
    public float WallJumpMinAngle = 30f;
    [Tooltip("碰撞法线朝上分量低于该值才认定为墙面（区分斜坡/地面）")]
    public float WallNormalMaxUpDot = 0.3f;
    [Tooltip("蹬墙水平速度 = 反射分量 * 该系数 + 沿法线冲量")]
    public float WallJumpReflectRatio = 0.8f;
    [Tooltip("蹬墙沿墙面法线方向的附加冲量")]
    public float WallJumpNormalImpulse = 3f;
    [Tooltip("蹬墙上抛分量")]
    public float WallJumpUpImpulse = 8f;
    [Tooltip("蹬墙冷却（秒）")]
    public float WallJumpCooldown = 0.3f;

    [Header("受击框随速度变细（策划案第 3 节：始终不细于肩宽）")]
    [Tooltip("开始变细的速度（一般等于地速阈值）")]
    public float CapsuleShrinkStartSpeed = 10f;
    [Tooltip("达到最细形态的速度")]
    public float CapsuleShrinkEndSpeed = 25f;
    [Tooltip("常态胶囊半径")]
    public float CapsuleBaseRadius = 0.5f;
    [Tooltip("最细半径下限——对应“不细于肩宽”")]
    public float CapsuleMinRadius = 0.3f;
    [Tooltip("常态胶囊高度")]
    public float CapsuleBaseHeight = 2f;
    [Tooltip("最细形态胶囊高度（随速度插值，占位为高速前倾姿态）")]
    public float CapsuleFastHeight = 1.7f;

    [Header("碰撞查询")]
    [Tooltip("起身头顶阻挡检测使用的层")]
    public LayerMask CollisionMask = ~0;

    [Header("泵油模型与上限（数学仿真回传，见 demo/movement-sim/results/report.md）")]
    [Tooltip("泵油模式。WindowPump=设计意图：免摩擦窗口内泵加速不受 Quake 投影上限约束，跑-跳循环线性加速（仿真 30 循环 5.833×阈值）；VerbatimQuake=任务公式字面模型，仅作对照——直线跑跳零增长，无法实现策划案『不断加速』")]
    public PumpMode Pump = PumpMode.WindowPump;
    [Tooltip("水平速度软上限（绝对速度）。仿真风险：泵油增速线性且无上界（每地面 tick +accel/60），必须软上限兜底；占位默认 = 12×地速阈值")]
    public float MaxSpeed = 120f;
    [Tooltip("矢量转换器能量上限（策划案第 4 节）")]
    public float EnergyMax = 200f;
    [Tooltip("每 tick 能量增量 = max(0, |v水平| - 地速阈值) × 本系数（策划案第 4 节）。仿真里速度用阈值倍数、增量乘 dt；本工程用绝对速度按 tick 直算，故占位 1")]
    public float EnergyPerTickPerExcessSpeed = 1f;
}

// 泵油模式：仿真的核心模型结论（详见 report.md 第 1 节）
public enum PumpMode
{
    VerbatimQuake, // 任务公式字面模型：加速始终受投影上限约束（仿真结论：只保速、零增长）
    WindowPump,    // 修正模型：免摩擦窗口内泵加速不受投影上限约束（默认，设计意图）
}
