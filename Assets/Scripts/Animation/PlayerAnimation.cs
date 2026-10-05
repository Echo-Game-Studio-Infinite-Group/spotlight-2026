using UnityEngine;


[DisallowMultipleComponent]
public sealed class PlayerAnimation : MonoBehaviour
{
    // 编号与装配工具的 MotionState 条件保持一致；动作状态使用完整路径，不依赖状态数组下标。
    public const int MotionStateIdle = 0;    // 0：站立/待机
    public const int MotionStateMove = 1;    // 1：移动（Walk↔Run 混合树，由 RunBlend 控制）
    public const int MotionStateJump = 2;    // 2：空中/下落
    public const int MotionStateSlide = 3;   // 3：滑铲（Soccer Tackle）
    public const int MotionStateAttack = 4;  // 4：攻击（Attack）

    private static readonly int MotionStateId = Animator.StringToHash("MotionState");
    private static readonly int RunBlendId = Animator.StringToHash("RunBlend");
    private static readonly int IsAttackingId = Animator.StringToHash("IsAttacking");

    [SerializeField] private Animator _animator;
    [SerializeField, Min(0f)] private float _idleSpeed = 0.1f;
    [SerializeField, Min(0f)] private float _blendResponse = 10f;
    [SerializeField, Min(0f)] private float _fallGrace = 0.08f;
    private PlayerMotor _motor;
    private PlayerCombat _combat;
    private float _runBlend;
    private float _airborneTime;

    // 供测试与诊断读取当前实际下发给动画机的状态编号
    public int CurrentMotionState { get; private set; }
    public Animator Animator => _animator;
    public bool IsSequenceDriven { get; private set; }
    public void SetSequenceDriven(bool driven) => IsSequenceDriven = driven;

    public void Configure(Animator animator)
    {
        _animator = animator;
        DisableRootMotion();
    }

    private void DisableRootMotion()
    {
        // 位移只允许由 PlayerMotor 驱动，模型子节点的根运动会脱离碰撞体和相机目标。
        if (_animator != null) _animator.applyRootMotion = false;
    }

    private void Awake()
    {
        _motor = GetComponentInParent<PlayerMotor>();
        _combat = GetComponentInParent<PlayerCombat>();
        if (_animator == null) _animator = GetComponentInChildren<Animator>();
        DisableRootMotion();
    }

    // Unity 只把动画事件发给 Animator 同节点组件。
    public void EnableHitbox() { if (_combat != null) _combat.EnableHitbox(); }
    public void DisableHitbox() { if (_combat != null) _combat.DisableHitbox(); }
    public void FinishAttack() { if (_combat != null) _combat.FinishAttack(); }

    private void Update()
    {
        if (IsSequenceDriven) return;
        UpdateLocomotion(TimeManager.PlayerDeltaTime);
        if (_animator != null) _animator.speed = TimeManager.PlayerRate;
    }

    public void UpdateLocomotion(float deltaTime)
    {
        if (_motor == null) _motor = GetComponentInParent<PlayerMotor>();
        if (_combat == null) _combat = GetComponentInParent<PlayerCombat>();
        if (_motor == null || _animator == null || _motor.Params == null) return;

        // CharacterController 在贴地移动时可能短暂丢失 Grounded，延迟进入下落动画；主动起跳立即播放。
        _airborneTime = _motor.IsGrounded ? 0f : _airborneTime + deltaTime;
        bool airborne = _motor.IsWallSliding || _motor.Velocity.y > 0f || _airborneTime >= _fallGrace;
        int state = _motor.IsSliding ? MotionStateSlide : airborne ? MotionStateJump :
            _motor.HorizontalSpeed > _idleSpeed ? MotionStateMove : MotionStateIdle;

        // 攻击优先级最高：攻击窗口由 PlayerCombat 独占计时，这里只读它的结论，
        // 不在动画侧另存一份计时器，否则两边的「攻击什么时候结束」会各说各话。
        bool attacking = !IsSequenceDriven && _combat != null && _combat.IsAttacking;
        if (attacking) state = MotionStateAttack;

        float runStart = _motor.Params.WalkSpeed;
        float runEnd = Mathf.Max(runStart + 0.01f, _motor.Params.GroundSpeedThreshold);
        float targetBlend = Mathf.InverseLerp(runStart, runEnd, _motor.HorizontalSpeed);
        _runBlend = Mathf.MoveTowards(_runBlend, targetBlend,
            _blendResponse * deltaTime);

        CurrentMotionState = state;
        _animator.SetInteger(MotionStateId, state);
        // 显式的攻击布尔：状态机的进/出边由它驱动，语义比复用 MotionState 编号清晰，
        // 也不会被每帧重写的移动编号互相干扰。
        _animator.SetBool(IsAttackingId, attacking);
        _animator.SetFloat(RunBlendId, _runBlend);
    }
}
