using UnityEngine;


[DisallowMultipleComponent]
public sealed class PlayerAnimation : MonoBehaviour
{
    // 编号与装配工具的 MotionState 条件保持一致；动作状态使用完整路径，不依赖状态数组下标。
    public const int MotionStateIdle = 0;            // 0：站立/待机
    public const int MotionStateMove = 1;            // 1：移动（复用现有移动动画）
    public const int MotionStateJump = 2;            // 2：空中/下落
    public const int MotionStateSlide = 3;           // 3：滑铲（Running Slide）
    public const int MotionStateAttack = 4;          // 4：攻击（Attack）
    public const int MotionStateWallDashLeft = 5;    // 5：左侧贴墙冲刺
    public const int MotionStateWallDashRight = 6;   // 6：右侧贴墙冲刺
    public const int MotionStateHurtSmall = 7;       // 7：小受击
    public const int MotionStateHurtBig = 8;         // 8：大受击（被敌人重击或扣血太多）
    public const int MotionStateWalk = 9;

    public const string WalkXParameter = "WalkX";
    public const string WalkZParameter = "WalkZ";
    public const string WalkRateParameter = "WalkRate";
    public const string WalkLegsParameter = "WalkLegs";
    private static readonly int WalkXId = Animator.StringToHash(WalkXParameter);
    private static readonly int WalkZId = Animator.StringToHash(WalkZParameter);
    private static readonly int WalkRateId = Animator.StringToHash(WalkRateParameter);
    private static readonly int WalkLegsId = Animator.StringToHash(WalkLegsParameter);

    public const string SlideTimeParameter = "SlideTime";
    private static readonly int SlideTimeId = Animator.StringToHash(SlideTimeParameter);

    private static readonly int MotionStateId = Animator.StringToHash("MotionState");
    private static readonly int IsAttackingId = Animator.StringToHash("IsAttacking");
    private static readonly int IsHurtId = Animator.StringToHash("IsHurt");
    private static readonly int HurtId = Animator.StringToHash("Hurt");
    /// <summary>Animator 里那条只驱动腿部的 Override 层（avatarMask = LegsOnly）。受击与攻击招式共用。</summary>
    public const string HurtLegLayerName = "HurtLegs";
    public const string InactiveLegsStateName = HurtLegLayerName + ".Inactive";
    private static readonly int InactiveLegsStateId = Animator.StringToHash(InactiveLegsStateName);

    // 受伤专用参数，不复用动作序列的 ActionPlaying / ActionTime：
    // 那两个由 PlayerActionRunner 的装配层独占写入，两条线抢同一个参数会互相把状态顶掉。
    // Hurt 记录受击类型（0 无 / 1 小 / 2 大），入口显式播放对应片段；
    // IsHurt 是「正在受击」标志，受伤状态只能由它退出，因此播放期间不会被自己重复进入。
    private const int HurtKindNone = 0;
    private const int HurtKindSmall = 1;
    private const int HurtKindBig = 2;

    [Header("受伤")]
    [Tooltip("受击后禁止移动和攻击取消的秒数；解锁后移动只覆盖腿部。")]
    [SerializeField, Min(0f)] private float _hurtLockSeconds = 0.1f;
    [SerializeField, Min(0.1f)] private float _hurtMaxSeconds = 1.6f;
    [SerializeField, Min(0f)] private float _hurtExitBlendSeconds = 0.1f;

    [SerializeField] private Animator _animator;
    [SerializeField, Min(0f)] private float _idleSpeed = 0.1f;
    [SerializeField, Min(0f)] private float _fallGrace = 0.08f;
    [SerializeField, Range(0f, 1f)] private float _wallSideDeadZone = 0.05f;
    [Header("七方向行走混合")]
    [Tooltip("方向参数的平滑时间；0 表示立即切换，按玩家逻辑时间推进。")]
    [SerializeField, Min(0f)] private float _walkDirectionBlendSeconds = 0.12f;
    [Tooltip("步频跟随实际行走速度的平滑时间；0 表示立即跟随。")]
    [SerializeField, Min(0f)] private float _walkRateBlendSeconds = 0.08f;
    [Header("滑铲动画")]
    [SerializeField] private AnimationClip _slideClip;
    [Tooltip("播放到完全铲下的姿态后保持；退出物理滑铲后继续播放剩余起身段。")]
    [SerializeField, Range(0f, 1f)] private float _slideHoldNormalizedTime = 0.3f;
    private PlayerMotor _motor;
    private PlayerCombat _combat;
    private PlayerInputReader _input;
    private GameJam.Actions.PlayerActionRunner _actionRunner;
    private int _legsOnlyLayer = -1;
    private bool _legsOverrideActive;
    private float _hurtElapsed;
    private float _airborneTime;
    private int _wallMotionState = MotionStateWallDashLeft;
    private bool _hasSlideTime;
    private bool _hasWalkParameters;
    private bool _hasWalkLegsParameter;
    private bool _wasWalking;
    private Vector2 _walkBlendDirection;
    private float _walkBlendRate;
    private bool _wasSliding;
    private int _slideStartJumpCount;
    private float _slideProgress;

    // 受伤状态：_hurtKind 是当前受击类型，_hurtLockRemaining 是「还不许取消」的剩余时间
    private int _hurtKind = HurtKindNone;
    private float _hurtLockRemaining;
    private int _lastIsHurt = -1;

    // 供测试与诊断读取当前实际下发给动画机的状态编号
    public int CurrentMotionState { get; private set; }
    public Animator Animator => _animator;
    public bool IsSequenceDriven { get; private set; }
    public bool IsSlideRecovering { get; private set; }
    public bool IsHoldingSlidePose => _wasSliding && _slideProgress >= _slideHoldNormalizedTime;
    public float SlideNormalizedTime => _slideProgress;
    public float SlideHoldNormalizedTime => _slideHoldNormalizedTime;
    public void SetSequenceDriven(bool driven) => IsSequenceDriven = driven;
    /// <summary>正在受击（受击动作还没播完，或还没被取消）。</summary>
    public bool IsHurting => _hurtKind != HurtKindNone;
    /// <summary>受击锁还没走完，此时不接受操作取消。</summary>
    public bool IsHurtLocked => IsHurting && _hurtLockRemaining > 0f;
    /// <summary>当前受击动作的状态名（诊断用）。</summary>
    public string CurrentHurtState => IsHurting ? LocomotionStateName(CurrentMotionState) : string.Empty;

    // 收招与自动过渡共用状态名称；MotionState 编号不依赖 Animator 编辑器中的排列顺序。
    public static string LocomotionStateName(int motionState)
    {
        switch (motionState)
        {
            case MotionStateMove: return "Move";
            case MotionStateWalk: return "Walk";
            case MotionStateJump: return "Jump";
            case MotionStateSlide: return "Trackle";
            case MotionStateWallDashLeft: return "WallDashLeft";
            case MotionStateWallDashRight: return "WallDashRight";
            case MotionStateHurtSmall: return "HitStomach";
            case MotionStateHurtBig: return "HitBody";
            default: return "Idle";
        }
    }

    public void Configure(Animator animator)
    {
        _animator = animator;
        _legsOnlyLayer = _animator.GetLayerIndex(HurtLegLayerName);
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
        _input = GetComponentInParent<PlayerInputReader>();
        _actionRunner = GetComponentInParent<GameJam.Actions.PlayerActionRunner>();
        if (_animator == null) _animator = GetComponentInChildren<Animator>();
        DisableRootMotion();
        CacheSlideParameter();
        _legsOnlyLayer = _animator.GetLayerIndex(HurtLegLayerName);
    }

    private void CacheSlideParameter()
    {
        _hasSlideTime = false;
        _hasWalkLegsParameter = false;
        _wasWalking = false;
        bool walkX = false, walkZ = false, walkRate = false;
        foreach (AnimatorControllerParameter parameter in _animator.parameters)
        {
            _hasWalkLegsParameter |= parameter.nameHash == WalkLegsId && parameter.type == AnimatorControllerParameterType.Bool;
            if (parameter.nameHash == SlideTimeId && parameter.type == AnimatorControllerParameterType.Float)
                _hasSlideTime = true;
            if (parameter.type != AnimatorControllerParameterType.Float) continue;
            walkX |= parameter.nameHash == WalkXId;
            walkZ |= parameter.nameHash == WalkZId;
            walkRate |= parameter.nameHash == WalkRateId;
        }
        _hasWalkParameters = walkX && walkZ && walkRate;
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
        UpdateWalkBlend(deltaTime);
        bool hurtFinished = false;

        // 移动不能取消受击，否则上身受击与腿部行走无法同时存在。
        if (IsHurting)
        {
            _hurtElapsed += deltaTime;
            if (_hurtLockRemaining > 0f) _hurtLockRemaining -= deltaTime;
            _motor.InputSuppressed = IsHurtLocked;
            AnimatorStateInfo hurtState = _animator.GetCurrentAnimatorStateInfo(0);
            bool finished = hurtState.IsName(CurrentHurtState) && hurtState.normalizedTime >= 1f
                && !_animator.IsInTransition(0);
            if (finished || _hurtElapsed >= _hurtMaxSeconds) { CancelHurt(); hurtFinished = true; }
            else { PushHurtState(); UpdateLegsOnlyLayer(); return; }
        }
        else _motor.InputSuppressed = false;
        UpdateLegsOnlyLayer();

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
            _motor.IsWalking && _motor.HorizontalSpeed > _idleSpeed ? MotionStateWalk :
            (_motor.IsBraking ? _motor.HorizontalSpeed > _idleSpeed : _motor.HasMovementInput) ? MotionStateMove : MotionStateIdle;

        // 攻击优先级最高：攻击窗口由 PlayerCombat 独占计时，这里只读它的结论，
        // 不在动画侧另存一份计时器，否则两边的「攻击什么时候结束」会各说各话。
        bool attacking = !IsSequenceDriven && _combat != null && _combat.IsAttacking;
        if (attacking) state = MotionStateAttack;

        CurrentMotionState = state;
        _animator.SetInteger(MotionStateId, state);
        // 显式的攻击布尔：状态机的进/出边由它驱动，语义比复用 MotionState 编号清晰，
        // 也不会被每帧重写的移动编号互相干扰。
        _animator.SetBool(IsAttackingId, attacking);
        // 受伤旧出口指向 Idle；显式回到当前移动姿态，避免七向行走被旧出口顶回跑步或待机。
        if (hurtFinished && !attacking)
            _animator.CrossFadeInFixedTime("Base Layer." + LocomotionStateName(state), _hurtExitBlendSeconds, 0);
    }

    private void UpdateWalkBlend(float deltaTime)
    {
        if (!_hasWalkParameters || !_motor.IsWalking)
        {
            _wasWalking = false;
            return;
        }
        Vector3 local = _motor.transform.InverseTransformDirection(MovementMath.Horizontal(_motor.Velocity)).normalized;
        var direction = new Vector2(local.x, local.z);
        float rate = Mathf.Min(_motor.Params.WalkMaxAnimationRate, _motor.HorizontalSpeed / _motor.Params.WalkSpeed);
        if (!_wasWalking)
        {
            // 入场姿态交给状态过渡混合，不能沿用上次行走的方向或从无中心动画的原点起步。
            _walkBlendDirection = direction;
            _walkBlendRate = rate;
        }
        else
        {
            // 执行器会额外发送零时长边界采样；指数平滑只消耗传入的玩家逻辑时间。
            float directionWeight = _walkDirectionBlendSeconds > 0f ? 1f - Mathf.Exp(-deltaTime / _walkDirectionBlendSeconds) : 1f;
            float rateWeight = _walkRateBlendSeconds > 0f ? 1f - Mathf.Exp(-deltaTime / _walkRateBlendSeconds) : 1f;
            _walkBlendDirection = Vector2.Lerp(_walkBlendDirection, direction, directionWeight);
            _walkBlendRate = Mathf.Lerp(_walkBlendRate, rate, rateWeight);
        }
        _wasWalking = true;
        _animator.SetFloat(WalkXId, _walkBlendDirection.x);
        _animator.SetFloat(WalkZId, _walkBlendDirection.y);
        _animator.SetFloat(WalkRateId, _walkBlendRate);
    }

    /// <summary>
    /// 受击入口：big = true 播大受击（HitBody），否则播小受击（HitStomach）。
    /// 伤害来源只管按伤害大小调这个，不用关心状态机怎么切。
    /// </summary>
    public void PlayHurt(bool big)
    {
        if (_animator == null) return;
        // 先结束旧动作的回调，再建立新受击，避免回调读取旧片段进度并提前解除新受击。
        _actionRunner?.Interrupt();
        _hurtKind = big ? HurtKindBig : HurtKindSmall;
        _hurtLockRemaining = _hurtLockSeconds;
        _hurtElapsed = 0f;
        if (_motor != null) _motor.InputSuppressed = IsHurtLocked;
        PushHurtState();
        if (_combat != null && !_combat.IsSequenceDriven) _combat.FinishAttack();
        _animator.SetBool(IsAttackingId, false);
        // 连续同类型命中也必须从头重播。
        _animator.Play("Base Layer." + CurrentHurtState, 0, 0f);
        UpdateLegsOnlyLayer();
    }

    /// <summary>
    /// 结束受伤状态：播放结束、攻击取消或死亡/重生时调用。
    /// </summary>
    public void CancelHurt()
    {
        if (_hurtKind == HurtKindNone) return;
        _hurtKind = HurtKindNone;
        _hurtLockRemaining = 0f;
        if (_motor != null) _motor.InputSuppressed = false;
        UpdateLegsOnlyLayer();
        // 先摘 IsHurt：控制器里受伤状态的唯一出口就是 IsHurt == false，
        // 摘掉之后状态机下一帧自己回移动状态，这里不需要再指定目标状态。
        PushHurtState();
    }

    // 类型参数与受击标志保持同步，供状态退出和诊断使用。
    private void PushHurtState()
    {
        _animator.SetInteger(HurtId, _hurtKind);
        int isHurt = _hurtKind == HurtKindNone ? 0 : 1;
        if (isHurt != _lastIsHurt)
        {
            _animator.SetBool(IsHurtId, isHurt != 0);
            _lastIsHurt = isHurt;
        }
        if (_hurtKind == HurtKindNone) return;
        CurrentMotionState = _hurtKind == HurtKindBig ? MotionStateHurtBig : MotionStateHurtSmall;
        _animator.SetInteger(MotionStateId, CurrentMotionState);
    }

    // 只驱动腿部的 Override 层（Animator 里叫 HurtLegs，avatarMask = LegsOnly，默认权重 0）：
    //   · 受击：上身播受击、腿继续走（受击锁定期不让步，否则等于用移动取消受击）
    //   · 攻击招式：普攻片段只该管上半身，腿必须跟移动走——否则「站着出刀 → 开始移动」时腿会僵在攻击姿态
    // 两种情况的门槛一致：接地、没滑铲、真的在动。
    private void UpdateLegsOnlyLayer()
    {
        if (_legsOnlyLayer < 0) return;
        if (_hasWalkLegsParameter) _animator.SetBool(WalkLegsId, _motor != null && _motor.IsWalking);
        bool moving = _input != null && _input.MoveInput.sqrMagnitude > 0f;
        bool groundedMoving = moving && _motor != null && _motor.IsGrounded && !_motor.IsSliding
            && _motor.HorizontalSpeed > _idleSpeed;
        bool hurtLegs = IsHurting && !IsHurtLocked && groundedMoving;
        bool attackLegs = !IsHurting && _combat != null && _combat.IsAttackSequenceActive && groundedMoving;
        bool active = isActiveAndEnabled && (hurtLegs || attackLegs);
        if (_animator.HasState(_legsOnlyLayer, InactiveLegsStateId))
        {
            bool inactive = _animator.GetCurrentAnimatorStateInfo(_legsOnlyLayer).fullPathHash == InactiveLegsStateId;
            // 权重归零后也明确停止腿部片段，避免停用层继续留在走跑状态。
            if (!active && (_legsOverrideActive || !inactive)) _animator.Play(InactiveLegsStateId, _legsOnlyLayer, 0f);
            else if (active && (!_legsOverrideActive || inactive))
                _animator.Play(HurtLegLayerName + (_motor.IsWalking ? ".Walk" : ".Move"), _legsOnlyLayer, 0f);
        }
        _legsOverrideActive = active;
        _animator.SetLayerWeight(_legsOnlyLayer, active ? 1f : 0f);
    }

    private void OnDisable()
    {
        if (_motor != null) _motor.SetSlidePoseActive(false);
        _wasSliding = IsSlideRecovering = false;
        _wasWalking = false;
        _slideProgress = 0f;
        // 组件被关掉时不能把输入门控留在打开状态，否则玩家的移动会永久失效。
        if (_motor != null) _motor.InputSuppressed = false;
        _hurtKind = HurtKindNone;
        _hurtLockRemaining = 0f;
        _lastIsHurt = -1;
        if (_animator != null)
        {
            PushHurtState();
            UpdateLegsOnlyLayer();
        }
    }
}
