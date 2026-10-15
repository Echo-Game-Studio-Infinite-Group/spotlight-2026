using UnityEngine;


[DisallowMultipleComponent]
public sealed class PlayerAnimation : MonoBehaviour
{
    // 编号与装配工具的 MotionState 条件保持一致；动作状态使用完整路径，不依赖状态数组下标。
    public const int MotionStateIdle = 0;    // 0：站立/待机
    public const int MotionStateMove = 1;    // 1：奔跑
    public const int MotionStateJump = 2;    // 2：空中/下落
    public const int MotionStateSlide = 3;   // 3：滑铲（Running Slide）
    public const int MotionStateAttack = 4;  // 4：攻击（Attack）
    public const int MotionStateWallDashLeft = 5;
    public const int MotionStateWallDashRight = 6;
    public const string SlideTimeParameter = "SlideTime";

    private static readonly int MotionStateId = Animator.StringToHash("MotionState");
    private static readonly int IsAttackingId = Animator.StringToHash("IsAttacking");
    private static readonly int SlideTimeId = Animator.StringToHash(SlideTimeParameter);

    [SerializeField] private Animator _animator;
    [SerializeField, Min(0f)] private float _fallGrace = 0.08f;
    [SerializeField, Range(0f, 1f)] private float _wallSideDeadZone = 0.05f;
    [Header("滑铲动画")]
    [SerializeField] private AnimationClip _slideClip;
    [Tooltip("播放到完全铲下的姿态后保持；退出物理滑铲后继续播放剩余起身段。")]
    [SerializeField, Range(0f, 1f)] private float _slideHoldNormalizedTime = 0.3f;
    private PlayerMotor _motor;
    private PlayerCombat _combat;
    private float _airborneTime;
    private int _wallMotionState = MotionStateWallDashLeft;
    private bool _hasSlideTime;
    private bool _wasSliding;
    private int _slideStartJumpCount;
    private float _slideProgress;

    // 供测试与诊断读取当前实际下发给动画机的状态编号
    public int CurrentMotionState { get; private set; }
    public Animator Animator => _animator;
    public bool IsSequenceDriven { get; private set; }
    public bool IsSlideRecovering { get; private set; }
    public bool IsHoldingSlidePose => _wasSliding && _slideProgress >= _slideHoldNormalizedTime;
    public float SlideNormalizedTime => _slideProgress;
    public float SlideHoldNormalizedTime => _slideHoldNormalizedTime;
    public void SetSequenceDriven(bool driven) => IsSequenceDriven = driven;

    // 收招与自动过渡共用状态名称；MotionState 编号不依赖 Animator 编辑器中的排列顺序。
    public static string LocomotionStateName(int motionState)
    {
        switch (motionState)
        {
            case MotionStateMove: return "Move";
            case MotionStateJump: return "Jump";
            case MotionStateSlide: return "Trackle";
            case MotionStateWallDashLeft: return "WallDashLeft";
            case MotionStateWallDashRight: return "WallDashRight";
            default: return "Idle";
        }
    }

    public void Configure(Animator animator)
    {
        _animator = animator;
        DisableRootMotion();
        CacheSlideParameter();
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
        CacheSlideParameter();
    }

    private void CacheSlideParameter()
    {
        _hasSlideTime = false;
        foreach (AnimatorControllerParameter parameter in _animator.parameters)
            if (parameter.nameHash == SlideTimeId && parameter.type == AnimatorControllerParameterType.Float)
            { _hasSlideTime = true; break; }
    }

    private void UpdateSlide(float deltaTime)
    {
        if (!_hasSlideTime || _slideClip == null || _slideClip.length <= 0f) return;
        if (_motor.IsSliding)
        {
            if (!_wasSliding) { _slideProgress = 0f; _slideStartJumpCount = _motor.JumpCount; }
            IsSlideRecovering = false;
            _slideProgress = Mathf.Min(_slideHoldNormalizedTime, _slideProgress + deltaTime / _slideClip.length);
        }
        else
        {
            if (_wasSliding)
            {
                IsSlideRecovering = _motor.IsGrounded && _motor.JumpCount == _slideStartJumpCount;
                // 提前松键也直接进入起身段，不再把铲下段补播一遍。
                if (IsSlideRecovering) _slideProgress = Mathf.Max(_slideProgress, _slideHoldNormalizedTime);
            }
            if (!_motor.IsGrounded || _motor.JumpCount != _slideStartJumpCount) IsSlideRecovering = false;
            if (IsSlideRecovering)
            {
                _slideProgress = Mathf.Min(1f, _slideProgress + deltaTime / _slideClip.length);
                if (_slideProgress >= 1f) IsSlideRecovering = false;
            }
        }
        _wasSliding = _motor.IsSliding;
        _motor.SetSlidePoseActive(_wasSliding || IsSlideRecovering);
        // 退出时保留源姿态的采样时间，让过渡从铲下姿态开始；下次进入再归零。
        _animator.SetFloat(SlideTimeId, _slideProgress);
    }

    private void OnDisable()
    {
        if (_motor != null) _motor.SetSlidePoseActive(false);
        _wasSliding = IsSlideRecovering = false;
        _slideProgress = 0f;
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
        UpdateSlide(deltaTime);

        // CharacterController 在贴地移动时可能短暂丢失 Grounded，延迟进入下落动画；主动起跳立即播放。
        _airborneTime = _motor.IsGrounded ? 0f : _airborneTime + deltaTime;
        bool airborne = _motor.Velocity.y > 0f || _airborneTime >= _fallGrace;
        if (_motor.IsWallSliding)
        {
            // 法线从墙指向角色；用角色右侧判断，避免相机转动导致左右动画翻转。
            float side = Vector3.Dot(_motor.WallNormal, _motor.transform.right);
            if (side > _wallSideDeadZone) _wallMotionState = MotionStateWallDashLeft;
            else if (side < -_wallSideDeadZone) _wallMotionState = MotionStateWallDashRight;
        }
        else _wallMotionState = MotionStateWallDashLeft;
        int state = _motor.IsWallSliding ? _wallMotionState : _motor.IsSliding || IsSlideRecovering ? MotionStateSlide : airborne ? MotionStateJump :
            _motor.HasForwardInput ? MotionStateMove : MotionStateIdle;

        // 攻击优先级最高：攻击窗口由 PlayerCombat 独占计时，这里只读它的结论，
        // 不在动画侧另存一份计时器，否则两边的「攻击什么时候结束」会各说各话。
        bool attacking = !IsSequenceDriven && _combat != null && _combat.IsAttacking;
        if (attacking) state = MotionStateAttack;

        CurrentMotionState = state;
        _animator.SetInteger(MotionStateId, state);
        // 显式的攻击布尔：状态机的进/出边由它驱动，语义比复用 MotionState 编号清晰，
        // 也不会被每帧重写的移动编号互相干扰。
        _animator.SetBool(IsAttackingId, attacking);
    }
}
