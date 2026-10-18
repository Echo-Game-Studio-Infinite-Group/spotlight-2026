using System;
using GameJam.Actions;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(CharacterController))]
public sealed class PlayerMotor : MonoBehaviour, IMotorCommand, IMotorActionMotion
{
    [SerializeField] private MovementParams _params;
    [SerializeField] private Transform _movementReference;
    private CharacterController _controller;
    private MovementContacts _contacts;
    private IPlayerInput _input;
    private Vector3 _velocity;
    private float _facingAngularVelocity;
    private Quaternion _initialMovementRotation;
    private float _groundStepOffset;
    private float _clock;
    private float _landTime = float.NegativeInfinity;
    private float _jumpUntil = float.NegativeInfinity;
    private float _wallTime;
    private float _wallLastContactTime;
    private float _wallSpeed;
    private Vector3 _wallNormal;
    private Vector3 _wallTangent;
    private bool _wallLocked;
    private bool _wallJumpExitProtected;
    private Vector3 _lockedNormal;
    private Vector3 _lockedPoint;
    private float _lastWallJump = float.NegativeInfinity;
    private object _simulationOwner;
    private ActionControlPolicy _actionControl;
    private bool _commandJump;
    private float _commandJumpHeightRatio;
    private float _commandAccelerateSpeedRatio;
    private float _commandSprintBoostRatio;
    private ActionMotionSettings _actionMotion;
    private long _motionInstance;
    private bool _slidePoseActive;
    private bool _brakeHoldLocked;
    private float _brakeStoredSpeed;
    private float _brakeRecoveryUntil = float.NegativeInfinity;
    private float _brakeJumpSpeed;
    private float _brakeJumpUntil = float.NegativeInfinity;
    private bool _inputSuppressed;
    public event Action<Vector3> MotionPathPoint;

    public void BeginMotion(ActionMotionSettings settings, long instanceId)
    {
        if (_motionInstance == instanceId && _actionMotion == settings) return;
        _actionMotion = settings; _motionInstance = instanceId;
        if (settings == null) return;
        if (settings.DiveInAir && IsWallSliding) ExitWall();
        float speed = HorizontalSpeed;
        // 高速惯性可以高于增速上限，但连段不能把冲量无限叠高，也不能截断已有速度。
        float boosted = Mathf.Max(speed, Mathf.Min(speed + settings.ForwardImpulse, settings.BoostSpeedLimit));
        SetHorizontal(transform.forward * Mathf.Min(boosted, _params.MaxSpeed));
    }
    public void EndMotion(long instanceId)
    {
        if (_motionInstance != instanceId) return;
        _actionMotion = null; _motionInstance = 0;
    }

    public MovementParams Params => _params;
    public MovementState State { get; private set; } = MovementState.Airborne;
    public Vector3 Velocity => _velocity;
    public float HorizontalSpeed => MovementMath.Horizontal(_velocity).magnitude;
    public bool IsGrounded => State == MovementState.Grounded;
    public GroundSupport GroundSupport => _contacts != null ? _contacts.Support : default;
    public float GroundStepHeight => _contacts != null ? _contacts.StepHeightDelta : 0f;
    public event Action<float> GroundStepped;
    public bool IsWallSliding => State == MovementState.WallSlide;
    public bool IsSliding { get; private set; }
    public bool IsSlideHeadBlocked { get; private set; }
    public bool HasForwardInput { get; private set; }
    public bool HasMovementInput { get; private set; }
    public bool IsWalking { get; private set; }
    public bool IsBraking { get; private set; }
    public bool CanBrake => IsGrounded && !IsSliding && !_slidePoseActive && !InputSuppressed &&
        (_actionControl == null || _actionControl.AllowBrake);
    public float BrakeRecoverySpeed => BrakeRecoveryValid ? _brakeStoredSpeed : 0f;
    public float BrakeJumpRecoverySpeed => Mathf.Max(BrakeRecoverySpeed, _clock <= _brakeJumpUntil ? _brakeJumpSpeed : 0f);
    public float BrakeRecoveryRemaining => Mathf.Max(0f, _brakeRecoveryUntil - _clock);
    private bool BrakeRecoveryValid => IsBraking || _clock <= _brakeRecoveryUntil || Mathf.Approximately(_clock, _brakeRecoveryUntil);
    public int JumpCount { get; private set; }
    public int WallJumpCount { get; private set; }
    public float WallApproachAngle { get; private set; }
    public Vector3 WallNormal => IsWallSliding ? _wallNormal : Vector3.zero;
    /// <summary>贴墙接触点（世界坐标）。不在划墙状态时为零向量——特效层别拿它当锚点。</summary>
    public Vector3 WallPoint => IsWallSliding && _contacts != null ? _contacts.WallPoint : Vector3.zero;
    public bool InFrictionWindow => IsGrounded && FrictionWindowRemaining > 0f;
    public float FrictionWindowRemaining => IsGrounded ? Mathf.Max(0f, _params.FrictionExemptWindow - (_clock - _landTime)) : 0f;
    public float WallWindowRemaining => IsWallSliding ? Mathf.Max(0f, _params.WallGraceTime - (_clock - _wallTime)) : 0f;
    public event Action<Vector3> Teleported;
    // 无法续接的划墙转角通知。
    public event Action<WallContact> WallCollision;
    /// <summary>
    /// 地面、空中与滑铲共用正撞接触报告；角度门槛与划墙一致，速度取裁剪前的接触快照。
    /// 去重交给消费方（同一面墙可能连续多帧命中）。
    /// </summary>
    public event Action<WallContact> WallImpact;

    // 场景里玩家是唯一的，表现层（相机、音频）不必各自找一遍引用。
    // 不进 Inspector 手填：预制体实例换位置后手填的引用最容易悄悄指错。
    public static PlayerMotor Active { get; private set; }

    public void SetParams(MovementParams parameters)
    {
        _params = parameters;
        if (_controller != null) _contacts = new MovementContacts(_controller, _params);
    }
    public void SetInput(IPlayerInput input) => _input = input;
    public void SetSlidePoseActive(bool active) => _slidePoseActive = active;
    public void SetMovementReference(Transform reference) => _movementReference = reference;
    public bool IsExternallyDriven => _simulationOwner != null;
    public bool TryAcquireSimulation(object owner)
    {
        if (owner == null || _simulationOwner != null && !ReferenceEquals(owner, _simulationOwner)) return false;
        _simulationOwner = owner;
        ClearPendingInput();
        return true;
    }
    public void ReleaseSimulation(object owner)
    {
        if (!ReferenceEquals(owner, _simulationOwner)) return;
        _simulationOwner = null;
        _actionControl = null;
        _commandJump = false;
        _commandJumpHeightRatio = 0f;
        _commandAccelerateSpeedRatio = 0f;
        _commandSprintBoostRatio = 0f;
        ClearPendingInput();
    }
    public void SetActionControl(ActionControlPolicy policy)
    {
        _actionControl = policy;
        if (policy != null && !policy.AllowJump) ClearPendingInput();
    }

    private void Awake()
    {
        if (Active == null) Active = this;
        _controller = GetComponent<CharacterController>();
        _initialMovementRotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
        _groundStepOffset = _controller.stepOffset;
        _controller.minMoveDistance = 0f;
        if (_params == null)
        {
            Debug.LogError("[PlayerMotor] 缺少 MovementParams", this);
            enabled = false;
            return;
        }
        _contacts = new MovementContacts(_controller, _params);
        if (_input == null) _input = GetComponent<IPlayerInput>();
        ResetState();
    }

    private void Start()
    {
        if (_movementReference == null && Camera.main != null) _movementReference = Camera.main.transform;
    }

    private void OnEnable()
    {
        if (_input is PlayerInputReader reader) reader.Cleared += OnInputCleared;
    }
    public bool InputSuppressed
    {
        get => _inputSuppressed;
        set { _inputSuppressed = value; if (value) { _brakeHoldLocked = false; ClearBrakeRecovery(); } }
    }

    private void OnInputCleared() { _brakeHoldLocked = false; ClearPendingInput(); ClearBrakeRecovery(); }

    // 保留 S 的原始独立按键；运动与动作预输入共用低速方向，不能把后退跳误判为原地跳。
    public PlayerInputFrame ResolveDirectionalInput(PlayerInputFrame input)
    {
        if (!input.BrakeHeld || _brakeHoldLocked || InputSuppressed || !IsGrounded || IsSliding || _slidePoseActive ||
            HorizontalSpeed > _params.LowSpeedThreshold || (_actionControl != null && !_actionControl.AllowMove)) return input;
        input.Move = new Vector2(input.Move.x, input.Move.y > 0f ? 0f : -1f).normalized;
        input.BrakeHeld = false;
        return input;
    }

    // 资格随这次跳跃预输入保留，速度只能在真正起跳时消费。
    public void BufferBrakeRecoveryJump(float bufferSeconds)
    {
        _brakeJumpSpeed = InputSuppressed ? 0f : BrakeRecoverySpeed;
        _brakeJumpUntil = _clock + Mathf.Max(0f, bufferSeconds);
    }

    public void ClearBrakeRecovery()
    {
        IsBraking = false;
        _brakeStoredSpeed = _brakeJumpSpeed = 0f;
        _brakeRecoveryUntil = _brakeJumpUntil = float.NegativeInfinity;
    }

    private void ClearPendingInput()
    {
        _jumpUntil = float.NegativeInfinity;
        HasForwardInput = false;
        HasMovementInput = IsWalking = false;
    }

    private void FixedUpdate()
    {
        if (IsExternallyDriven) return;
        PlayerInputFrame frame = _input != null ? _input.ReadFrame() : default;
        Simulate(frame, TimeManager.PlayerFixedDeltaTime, TimeManager.UnscaledTime);
    }

    public void Simulate(PlayerInputFrame input, float dt, float inputTime)
    {
        if (_contacts == null || !_controller.enabled || dt < 0f) return;
        if (_actionControl != null)
        {
            if (!_actionControl.AllowMove) input.Move = Vector2.zero;
            if (!_actionControl.AllowSprint) input.SprintHeld = false;
            if (!_actionControl.AllowJump) { input.JumpPressed = false; ClearPendingInput(); }
            if (!_actionControl.AllowSlide) input.SlidePressed = false;
        }
        if (InputSuppressed)
        {
            input.Move = Vector2.zero;
            input.SprintHeld = false;
            input.JumpPressed = false;
            input.SlidePressed = false;
            input.BrakeHeld = false;
            ClearBrakeRecovery();
            ClearPendingInput();
        }
        // 高速开始的这次 S 持续保持刹车，不能在跨过低速阈值后突然倒走。
        if (!input.BrakeHeld) _brakeHoldLocked = false;
        else if (HorizontalSpeed > _params.LowSpeedThreshold) _brakeHoldLocked = true;
        input = ResolveDirectionalInput(input);
        if (input.JumpPressed)
        {
            _jumpUntil = input.JumpTime + _params.JumpBufferWindow;
            BufferBrakeRecoveryJump(_params.JumpBufferWindow);
        }
        HasForwardInput = input.Move.y > 0f;
        HasMovementInput = !input.BrakeHeld && input.Move.sqrMagnitude > 0f;
        if (dt == 0f) return;
        bool wallJumpFacingLocked = _clock - _lastWallJump < _params.WallJumpFacingLockTime;
        _clock += dt;
        Vector3 wish = WishDirection(input.Move);
        // 低速侧移与后退保持面朝镜头前方；高速仍按旧规则转向位移方向并保留动量。
        IsWalking = HasMovementInput && (input.Move.x != 0f || input.Move.y < 0f) &&
            IsGrounded && !IsSliding && !_slidePoseActive && HorizontalSpeed <= _params.LowSpeedThreshold;
        RearmWall();
        if (IsWallSliding)
        {
            bool closeToWall = _contacts.ProbeWall(_wallNormal, _params.WallProbeDistance, out RaycastHit wall);
            bool blockedWall = closeToWall && !MovementContacts.AllowsWallSlide(wall.collider);
            if (closeToWall) _wallLastContactTime = _clock;
            float separation = Vector3.Dot(transform.position - _lockedPoint, _lockedNormal) - _controller.radius;
            if (blockedWall || separation > _params.WallProbeDistance + _controller.skinWidth * 2f ||
                _clock - _wallLastContactTime > _params.WallContactGraceTime)
                ExitWall();
        }

        // 墙面约束仍优先；普通角色转向可以由动作段锁定，相机继续独立转动。
        if (!input.BrakeHeld && (_actionControl == null || _actionControl.AllowTurn || IsWallSliding))
            UpdateFacing(IsWalking ? WishDirection(Vector2.up) : wish, dt, wallJumpFacingLocked);
        else _facingAngularVelocity = 0f;
        Vector3 drive = IsWalking ? wish : transform.forward * wish.magnitude;
        // 仅改变方向，不用向量插值，避免转弯时丢失速度；墙面与离墙保护优先。
        if (!input.BrakeHeld && wish != Vector3.zero && !IsWallSliding && !wallJumpFacingLocked && !_wallJumpExitProtected &&
            _clock - _lastWallJump >= _params.WallJumpCooldown)
            SetHorizontal((IsWalking ? wish.normalized : transform.forward) * HorizontalSpeed);

        if (input.SlidePressed && CanExecute(MotorCommandKind.EnterSlide))
        {
            IsSliding = true;
            SetCapsule(_params.SlideCapsuleHeight, _controller.radius);
        }

        IsBraking = input.BrakeHeld && CanBrake;
        if (IsSliding || IsBraking) IsWalking = false;
        if (IsBraking) _brakeRecoveryUntil = _clock + _params.BrakeRecoveryWindow;
        else if (!BrakeRecoveryValid) _brakeStoredSpeed = 0f;
        bool brakeThisStep = IsBraking;

        bool protectSlideFront = IsSliding || _slidePoseActive;
        bool jump = _commandJump || inputTime <= _jumpUntil;
        IsSlideHeadBlocked = IsSliding && !_contacts.HasHeadroom(_params.CapsuleBaseHeight);
        if (IsSliding)
        {
            if (!IsSlideHeadBlocked)
                SetHorizontal(Vector3.MoveTowards(MovementMath.Horizontal(_velocity), Vector3.zero, _params.SlideDecel * dt));
            else if (HorizontalSpeed <= _params.SlideBlockedMoveSpeed)
                SetHorizontal(MovementMath.Accelerate(MovementMath.Horizontal(_velocity), drive,
                    _params.SlideBlockedMoveSpeed, _params.RunAccel, dt, false));
            if ((jump || !input.SlideHeld || HorizontalSpeed == 0f) && TryStand())
            {
                if (jump && IsGrounded) Jump(RequestedJumpSpeed);
            }
        }
        else if (IsWallSliding)
        {
            UpdateWall(dt);
            if (jump) JumpFromWall();
            else if (_wallSpeed <= 0f) ExitWall();
        }
        else
        {
            UpdateFreeMovement(dt, drive);
            if (jump && IsGrounded)
            {
                Jump(RequestedJumpSpeed);
            }
        }

        if (_commandAccelerateSpeedRatio > 0f && !brakeThisStep && !IsSliding && !IsWallSliding)
            SetHorizontal(transform.forward * Mathf.Max(HorizontalSpeed, _params.GroundSpeedThreshold * _commandAccelerateSpeedRatio));
        if (_commandSprintBoostRatio > 0f && !brakeThisStep && !IsSliding && !IsWallSliding)
            SetHorizontal(transform.forward * (HorizontalSpeed + _params.LowSpeedThreshold * _commandSprintBoostRatio));
        SetHorizontal(Vector3.ClampMagnitude(MovementMath.Horizontal(_velocity), _params.MaxSpeed));
        float gravity = _params.Gravity * (IsWallSliding ? _params.WallGravityScale : 1f);
        gravity *= _actionControl != null ? _actionControl.GravityMultiplier : 1f;
        float gravityDt = IsWallSliding ? WallUnprotectedDeltaTime(dt) : dt;
        _velocity.y = IsGrounded ? -_params.Gravity * dt : _velocity.y - gravity * gravityDt;
        if (IsWallSliding && gravityDt > 0f) _velocity.y = Mathf.Max(_velocity.y, -_params.WallMaxFallSpeed);
        UpdateCapsule();
        _controller.stepOffset = 0f;
        _controller.slopeLimit = _params.MaxGroundAngle;

        bool wasGrounded = IsGrounded;
        bool wasWallSliding = IsWallSliding;
        if (_actionMotion != null && _actionMotion.DiveInAir && !IsGrounded)
        {
            if (IsWallSliding) ExitWall();
            float targetY = -Mathf.Min(_actionMotion.MaxDiveSpeed, HorizontalSpeed * Mathf.Tan(_actionMotion.DiveAngle * Mathf.Deg2Rad));
            _velocity.y = Mathf.Max(-_actionMotion.MaxDiveSpeed,
                Mathf.Lerp(_velocity.y, targetY, 1f - Mathf.Exp(-_actionMotion.DiveResponse * dt)));
        }
        _contacts.Move(ref _velocity, dt, point => MotionPathPoint?.Invoke(point), IsSliding,
            protectSlideFront ? _params.SlideForwardExtent : 0f, wasGrounded);
        bool hasImpact = _contacts.TryGetWallImpact(_params.WallMaxApproachAngle, out WallContact impact);
        // 同平面接缝也可能换成不可滑墙，不能只在法线变化的转角检查。
        if (IsWallSliding && _contacts.HasNonSlideWall) ExitWall();
        if (IsWallSliding && _contacts.HasWall &&
            Vector3.Angle(_wallNormal, _contacts.WallNormal) <= _params.WallSeamAngle)
            _wallLastContactTime = _clock;
        if (_contacts.Grounded)
        {
            if (!wasGrounded) _landTime = _clock;
            State = MovementState.Grounded;
            _wallSpeed = 0f;
        }
        else
        {
            if (wasGrounded) State = MovementState.Airborne;
            if (IsWallSliding && _contacts.TryGetWallTransition(_wallNormal, _params.WallSeamAngle, out WallContact corner))
            {
                if (corner.ApproachAngle + 0.01f < _params.WallMaxApproachAngle)
                    ContinueWall(corner);
                else
                {
                    WallCollision?.Invoke(corner);
                    ExitWall();
                }
            }
            if (!hasImpact && !IsSliding && !IsWallSliding && !wasWallSliding && !(_actionMotion?.DiveInAir == true))
            {
                TryEnterWall(wish);
                if (jump && IsWallSliding) JumpFromWall();
            }
        }
        // 能量不在这里结算：唯一账户是 VectorEnergy（执行序 10，在本组件之后自动积能）
        _commandJump = false;
        _commandJumpHeightRatio = 0f;
        _commandAccelerateSpeedRatio = 0f;
        _commandSprintBoostRatio = 0f;
        if (_contacts.StepHeightDelta != 0f) GroundStepped?.Invoke(_contacts.StepHeightDelta);
        // 消费方会关闭 Motor、切换布娃娃；所有状态结算完再上报，不能在回调后覆盖重置结果。
        if (!CanBrake) IsBraking = false;
        if (!IsGrounded || IsSliding || HorizontalSpeed > _params.LowSpeedThreshold) IsWalking = false;
        if (hasImpact) { ClearBrakeRecovery(); WallImpact?.Invoke(impact); }
    }

    private float RequestedJumpSpeed => _params.JumpSpeed * Mathf.Sqrt(_commandJumpHeightRatio > 0f ? _commandJumpHeightRatio : 1f);

    private void UpdateFreeMovement(float dt, Vector3 wish)
    {
        Vector3 horizontal = MovementMath.Horizontal(_velocity);
        if (IsGrounded)
        {
            float target = _params.GroundSpeedThreshold;
            if (!InFrictionWindow && _params.GroundFriction > 0f)
            {
                horizontal *= Mathf.Max(0f, 1f - _params.GroundFriction * dt);
                if (horizontal.sqrMagnitude < _params.GroundStopSpeed * _params.GroundStopSpeed)
                    horizontal = Vector3.zero;
            }
            if (IsBraking)
            {
                float speed = horizontal.magnitude;
                float reduced = Mathf.Max(0f, speed - _params.BrakeDeceleration * dt);
                if (_params.BrakeDeceleration > 0f && reduced <= _params.BrakeStopSpeed) reduced = 0f;
                _brakeStoredSpeed = Mathf.Min(_params.MaxSpeed, _brakeStoredSpeed + speed - reduced);
                horizontal = speed > 0f ? horizontal * (reduced / speed) : Vector3.zero;
            }
            else if (IsWalking)
                horizontal = wish.normalized * Mathf.MoveTowards(horizontal.magnitude,
                    _params.WalkSpeed * wish.magnitude, _params.WalkAcceleration * dt);
            else horizontal = MovementMath.Accelerate(horizontal, wish, target, _params.RunAccel, dt,
                    _params.Pump == PumpMode.WindowPump && InFrictionWindow);
        }
        else horizontal = MovementMath.Accelerate(horizontal, wish, _params.GroundSpeedThreshold, _params.AirControl, dt, false);
        SetHorizontal(horizontal);
    }

    private void TryEnterWall(Vector3 wish)
    {
        // 以入墙速度与墙切线的锐角判定；只接受确实向墙内运动且小于上限的接触。
        if (!_contacts.HasWall || _contacts.HasNonSlideWall) return;
        if (_contacts.WallAngle + 0.01f >= _params.WallMaxApproachAngle) return;
        bool sameWall = _wallLocked && Vector3.Angle(_lockedNormal, _contacts.WallNormal) <= _params.WallSeamAngle &&
            Mathf.Abs(Vector3.Dot(_contacts.WallPoint - _lockedPoint, _lockedNormal)) < _params.WallRearmDistance;
        if (sameWall) return;
        _wallNormal = _contacts.WallNormal;
        // 当前帧真实墙面碰撞足以入墙；贴墙时的 CapsuleCast 可能从重叠体积起步而漏报。
        _wallTangent = MovementMath.WallTangent(_contacts.WallIncoming, _wallNormal, wish);
        _wallSpeed = Mathf.Min(MovementMath.Horizontal(_contacts.WallIncoming).magnitude, _params.MaxSpeed);
        _wallTime = _clock;
        _wallLastContactTime = _clock;
        WallApproachAngle = _contacts.WallAngle;
        _wallLocked = true;
        _lockedNormal = _wallNormal;
        _lockedPoint = _contacts.WallPoint;
        State = MovementState.WallSlide;
        SetHorizontal(_wallTangent * _wallSpeed);
        FaceWallTangent();
    }

    // 窗口横跨一个 tick 时只对过期后的部分施加重力和摩擦，保证完整的保护时长。
    private float WallUnprotectedDeltaTime(float dt)
    {
        float elapsed = _clock - _wallTime;
        // 浮点累加不能让窗口末帧提前触发下落限速，避免保速中的竖直速度突变。
        if (Mathf.Approximately(elapsed, _params.WallGraceTime)) return 0f;
        return Mathf.Clamp(elapsed - _params.WallGraceTime, 0f, dt);
    }

    private void ContinueWall(WallContact contact)
    {
        Vector3 tangent = MovementMath.WallTangent(contact.IncomingVelocity, contact.Normal, _wallTangent);
        if (tangent == Vector3.zero) { WallCollision?.Invoke(contact); ExitWall(); return; }
        _wallNormal = contact.Normal;
        _wallTangent = tangent;
        _lockedNormal = contact.Normal;
        _lockedPoint = contact.Point;
        _wallLastContactTime = _clock;
        WallApproachAngle = contact.ApproachAngle;
        // 连续滑墙只换墙面，不刷新 _wallTime，也不丢失已结算的墙面速度。
        SetHorizontal(_wallTangent * _wallSpeed);
        FaceWallTangent();
    }

    private void UpdateWall(float dt)
    {
        if (_wallTangent != Vector3.zero) _wallSpeed = Mathf.Min(_wallSpeed, HorizontalSpeed);
        float frictionDt = WallUnprotectedDeltaTime(dt);
        float protectedDt = dt - frictionDt;
        if (protectedDt > 0f && _params.WallVerticalFriction > 0f)
        {
            // 跨窗口的运动步只衰减受保护部分；蹬墙起跳随后覆盖竖直速度，保留完整起跳冲量。
            _velocity.y *= Mathf.Exp(-_params.WallVerticalFriction * protectedDt);
            if (Mathf.Abs(_velocity.y) <= _params.WallVerticalStopSpeed) _velocity.y = 0f;
        }
        if (frictionDt > 0f)
        {
            if (_wallTangent == Vector3.zero) _wallSpeed = 0f;
            else _wallSpeed *= Mathf.Exp(-_params.WallFriction * frictionDt);
        }
        if (_wallSpeed <= _params.WallStopSpeed) _wallSpeed = 0f;
        SetHorizontal(_wallTangent * _wallSpeed);
    }

    private void JumpFromWall()
    {
        float speed = Mathf.Min(_params.MaxSpeed, _wallSpeed + (WallWindowRemaining > 0f ? _params.WallJumpBoost : 0f));
        SetHorizontal(MovementMath.WallJump(_wallTangent, _wallNormal, speed, _params.WallJumpAngle));
        Jump(_params.WallJumpUpImpulse);
        _lastWallJump = _clock;
        _wallJumpExitProtected = true;
        WallJumpCount++;
    }

    private void RearmWall()
    {
        if (!_wallLocked || IsWallSliding) return;
        float separation = Vector3.Dot(transform.position - _lockedPoint, _lockedNormal) - _controller.radius;
        if (separation >= _params.WallRearmDistance &&
            !_contacts.ProbeWall(_lockedNormal, _params.WallRearmDistance, out _) &&
            _clock - _lastWallJump >= _params.WallJumpCooldown)
        {
            _wallLocked = false;
            _wallJumpExitProtected = false;
        }
    }

    private void ExitWall() { State = MovementState.Airborne; _wallSpeed = 0f; }
    private void Jump(float speed)
    {
        float recovered = BrakeJumpRecoverySpeed;
        if (recovered > 0f) SetHorizontalSpeed(HorizontalSpeed + recovered);
        ClearBrakeRecovery();
        IsWalking = false;
        _jumpUntil = float.NegativeInfinity;
        _velocity.y = speed;
        State = MovementState.Airborne;
        JumpCount++;
    }

    private Vector3 WishDirection(Vector2 move)
    {
        move = Vector2.ClampMagnitude(move, 1f);
        Vector3 forward = _movementReference != null
            ? MovementMath.Horizontal(_movementReference.forward)
            : _initialMovementRotation * Vector3.forward;
        if (forward.sqrMagnitude < 0.0001f) forward = _initialMovementRotation * Vector3.forward;
        forward.Normalize();
        return Vector3.Cross(Vector3.up, forward) * move.x + forward * move.y;
    }

    private void FaceWallTangent()
    {
        // 离墙后的切线仍用于蹬墙保护，不能再把它应用为角色朝向。
        if (!IsWallSliding) return;
        _facingAngularVelocity = 0f;
        if (_wallTangent != Vector3.zero) transform.rotation = Quaternion.LookRotation(_wallTangent, Vector3.up);
    }

    private void UpdateFacing(Vector3 wish, float dt, bool wallJumpFacingLocked)
    {
        if (IsWallSliding) { FaceWallTangent(); return; }
        if (wallJumpFacingLocked) { _facingAngularVelocity = 0f; return; }
        if (wish == Vector3.zero) { _facingAngularVelocity = 0f; return; }
        float targetYaw = Mathf.Atan2(wish.x, wish.z) * Mathf.Rad2Deg;
        float yaw = Mathf.SmoothDampAngle(transform.eulerAngles.y, targetYaw, ref _facingAngularVelocity,
            _params.FacingSmoothTime, Mathf.Infinity, dt);
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    private bool CanStand() => _contacts.CanResize(_params.CapsuleBaseHeight, _controller.radius);

    private bool TryStand()
    {
        if (!CanStand()) return false;
        // 贴墙时允许先恢复站高；半径增大由 UpdateCapsule 单独检查，不能把退出滑铲锁死。
        SetCapsule(_params.CapsuleBaseHeight, _controller.radius);
        IsSliding = false;
        IsSlideHeadBlocked = false;
        return true;
    }

    private void UpdateCapsule()
    {
        if (IsSliding) return;
        float t = Mathf.InverseLerp(_params.CapsuleShrinkStartSpeed, _params.CapsuleShrinkEndSpeed, HorizontalSpeed);
        float height = Mathf.Lerp(_params.CapsuleBaseHeight, _params.CapsuleFastHeight, t);
        float radius = Mathf.Lerp(_params.CapsuleBaseRadius, _params.CapsuleMinRadius, t);
        if (height > _controller.height && !_contacts.CanResize(height, Mathf.Min(radius, _controller.radius)))
            height = _controller.height;
        if (radius > _controller.radius && !_contacts.CanResize(height, radius)) radius = _controller.radius;
        SetCapsule(height, radius);
    }

    private void SetCapsule(float height, float radius)
    {
        _controller.radius = radius;
        _controller.height = Mathf.Max(height, radius * 2f);
        _controller.center = Vector3.up * (_controller.height * 0.5f);
    }
    private void SetHorizontal(Vector3 value) { _velocity.x = value.x; _velocity.z = value.z; }
    public bool CanExecute(MotorCommandKind command)
    {
        if (_contacts == null || _controller == null || !_controller.enabled || _params == null) return false;
        switch (command)
        {
            case MotorCommandKind.Jump: return (IsGrounded || IsWallSliding) && (!IsSliding || CanStand());
            case MotorCommandKind.HighJump: return IsGrounded && (!IsSliding || CanStand());
            case MotorCommandKind.Accelerate: return !IsSliding && !IsWallSliding;
            case MotorCommandKind.SprintBoost: return !IsSliding && !IsWallSliding;
            case MotorCommandKind.EnterSlide: return IsGrounded && !IsSliding
                && HorizontalSpeed >= _params.GroundSpeedThreshold * _params.SlideSpeedRatio
                && _contacts.HasSlideFrontSpace(_params.SlideForwardExtent);
            case MotorCommandKind.ExitSlide: return !IsSliding || CanStand();
            default: return Enum.IsDefined(typeof(MotorCommandKind), command);
        }
    }
    public bool TryExecute(MotorCommandKind command, float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || !CanExecute(command)) return false;
        switch (command)
        {
            case MotorCommandKind.Jump:
                // 跳跃在运动步内执行，保留原先先结算地面/墙面加速、再起跳的 bhop 顺序。
                _commandJump = true;
                _commandJumpHeightRatio = 0f;
                break;
            case MotorCommandKind.HighJump:
                if (value <= 0f) return false;
                _commandJump = true;
                _commandJumpHeightRatio = value;
                break;
            case MotorCommandKind.Accelerate:
                if (value <= 0f) return false;
                _commandAccelerateSpeedRatio = value;
                break;
            case MotorCommandKind.SprintBoost:
                if (value <= 0f) return false;
                _commandSprintBoostRatio = value;
                break;
            case MotorCommandKind.EnterSlide: IsSliding = true; SetCapsule(_params.SlideCapsuleHeight, _controller.radius); break;
            case MotorCommandKind.ExitSlide: return !IsSliding || TryStand();
            case MotorCommandKind.SetHorizontalSpeed: SetHorizontalSpeed(value); break;
            case MotorCommandKind.LaunchVertical: LaunchVertical(value); break;
            case MotorCommandKind.ReverseHorizontal: ReverseHorizontal(); break;
            case MotorCommandKind.AddForwardImpulse: SetHorizontal(Vector3.ClampMagnitude(MovementMath.Horizontal(_velocity) + transform.forward * value, _params.MaxSpeed)); break;
            case MotorCommandKind.ClearHorizontal: SetHorizontal(Vector3.zero); ClearBrakeRecovery(); break;
        }
        return true;
    }
    public void CancelActionCommands()
    {
        _commandJump = false;
        _commandJumpHeightRatio = 0f;
        _commandAccelerateSpeedRatio = 0f;
        _commandSprintBoostRatio = 0f;
    }
    public void SetHorizontalSpeed(float speed)
    {
        if (_params == null) return;
        if (speed <= 0f) ClearBrakeRecovery();
        Vector3 direction = HorizontalSpeed > 0f ? MovementMath.Horizontal(_velocity).normalized : transform.forward;
        SetHorizontal(direction * Mathf.Clamp(speed, 0f, _params.MaxSpeed));
    }
    public void LaunchVertical(float speed)
    {
        if (_params == null || IsSliding && !TryStand()) return;
        Jump(Mathf.Max(0f, speed));
    }
    public void ReverseHorizontal()
    {
        Vector3 horizontal = -MovementMath.Horizontal(_velocity);
        SetHorizontal(horizontal);
        if (IsWallSliding) ExitWall();
        if (horizontal != Vector3.zero) transform.rotation = Quaternion.LookRotation(horizontal, Vector3.up);
        _facingAngularVelocity = 0f;
    }
    private void OnControllerColliderHit(ControllerColliderHit hit) => _contacts?.RecordHit(hit);

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        GroundSupport support = GroundSupport;
        if (!Application.isPlaying || !support.IsValid) return;
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(support.Point, .04f);
        Gizmos.DrawLine(support.Point, support.Point + support.Normal * .5f);
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(transform.position, transform.position + Vector3.up * GroundStepHeight);
    }
#endif

    public void ResetState()
    {
        _brakeHoldLocked = false;
        ClearBrakeRecovery();
        _velocity = Vector3.zero;
        _actionMotion = null; _motionInstance = 0;
        _commandJump = false;
        _commandJumpHeightRatio = 0f;
        _commandAccelerateSpeedRatio = 0f;
        _commandSprintBoostRatio = 0f;
        _facingAngularVelocity = 0f;
        JumpCount = WallJumpCount = 0;
        State = MovementState.Airborne;
        IsSliding = HasForwardInput = HasMovementInput = IsWalking = _wallLocked = _wallJumpExitProtected = false;
        IsSlideHeadBlocked = false;
        _slidePoseActive = false;
        _clock = _wallTime = _wallLastContactTime = _wallSpeed = WallApproachAngle = 0f;
        _landTime = _jumpUntil = _lastWallJump = float.NegativeInfinity;
        _wallNormal = _wallTangent = _lockedNormal = _lockedPoint = Vector3.zero;
        _input?.Clear();
        if (_controller != null && _params != null) SetCapsule(_params.CapsuleBaseHeight, _params.CapsuleBaseRadius);
    }

    public void Teleport(Vector3 position)
    {
        Vector3 delta = position - transform.position;
        bool wasEnabled = _controller.enabled;
        _controller.enabled = false;
        transform.position = position;
        ResetState();
        _controller.enabled = wasEnabled;
        Physics.SyncTransforms();
        Teleported?.Invoke(delta);
    }

    private void OnDisable()
    {
        if (Active == this) Active = null;
        if (_input is PlayerInputReader reader) reader.Cleared -= OnInputCleared;
        if (_controller != null) _controller.stepOffset = _groundStepOffset;
        if (_params != null) ResetState();
    }
}
