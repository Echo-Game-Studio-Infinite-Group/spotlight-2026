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
    private ActionMotionSettings _actionMotion;
    private long _motionInstance;
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
    public bool IsWallSliding => State == MovementState.WallSlide;
    public bool IsSliding { get; private set; }
    public bool IsSprinting { get; private set; }
    public int JumpCount { get; private set; }
    public int WallJumpCount { get; private set; }
    public float WallApproachAngle { get; private set; }
    public Vector3 WallNormal => IsWallSliding ? _wallNormal : Vector3.zero;
    public bool InFrictionWindow => IsGrounded && FrictionWindowRemaining > 0f;
    public float FrictionWindowRemaining => IsGrounded ? Mathf.Max(0f, _params.FrictionExemptWindow - (_clock - _landTime)) : 0f;
    public float WallWindowRemaining => IsWallSliding ? Mathf.Max(0f, _params.WallGraceTime - (_clock - _wallTime)) : 0f;
    public event Action<Vector3> Teleported;
    // 只报告不合格的新墙接触；反弹、伤害等撞墙反馈留给后续系统。
    public event Action<WallContact> WallCollision;

    // 场景里玩家是唯一的，表现层（相机、音频）不必各自找一遍引用。
    // 不进 Inspector 手填：预制体实例换位置后手填的引用最容易悄悄指错。
    public static PlayerMotor Active { get; private set; }

    public void SetParams(MovementParams parameters)
    {
        _params = parameters;
        if (_controller != null) _contacts = new MovementContacts(_controller, _params);
    }
    public void SetInput(IPlayerInput input) => _input = input;
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
        if (_input is PlayerInputReader reader) reader.Cleared += ClearPendingInput;
    }
    private void ClearPendingInput() => _jumpUntil = float.NegativeInfinity;

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
        if (input.JumpPressed) _jumpUntil = input.JumpTime + _params.JumpBufferWindow;
        if (dt == 0f) return;
        bool wallJumpFacingLocked = _clock - _lastWallJump < _params.WallJumpFacingLockTime;
        _clock += dt;
        Vector3 wish = WishDirection(input.Move);
        IsSprinting = input.SprintHeld && input.Move.y > 0f;
        RearmWall();
        if (IsWallSliding)
        {
            bool closeToWall = _contacts.ProbeWall(_wallNormal, _params.WallProbeDistance, out _);
            if (closeToWall) _wallLastContactTime = _clock;
            float separation = Vector3.Dot(transform.position - _lockedPoint, _lockedNormal) - _controller.radius;
            if (separation > _params.WallProbeDistance + _controller.skinWidth * 2f ||
                _clock - _wallLastContactTime > _params.WallContactGraceTime)
                ExitWall();
        }

        // 墙面约束仍优先；普通角色转向可以由动作段锁定，相机继续独立转动。
        if (_actionControl == null || _actionControl.AllowTurn || IsWallSliding)
            UpdateFacing(wish, dt, wallJumpFacingLocked);
        else _facingAngularVelocity = 0f;
        Vector3 drive = transform.forward * wish.magnitude;
        // 仅改变方向，不用向量插值，避免转弯时丢失速度；墙面与离墙保护优先。
        if (wish != Vector3.zero && !IsWallSliding && !wallJumpFacingLocked && !_wallJumpExitProtected &&
            _clock - _lastWallJump >= _params.WallJumpCooldown)
            SetHorizontal(transform.forward * HorizontalSpeed);

        if (input.SlidePressed && IsGrounded && !IsSliding &&
            HorizontalSpeed >= _params.GroundSpeedThreshold * _params.SlideSpeedRatio)
        {
            IsSliding = true;
            SetCapsule(_params.SlideCapsuleHeight, _controller.radius);
        }

        bool jump = _commandJump || inputTime <= _jumpUntil;
        if (IsSliding)
        {
            SetHorizontal(Vector3.MoveTowards(MovementMath.Horizontal(_velocity), Vector3.zero, _params.SlideDecel * dt));
            if ((jump || !input.SlideHeld || HorizontalSpeed < _params.SlideEndSpeed) && TryStand())
            {
                if (jump && IsGrounded) Jump(_params.JumpSpeed);
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
                if (!IsSprinting && wish != Vector3.zero) SetHorizontal(drive * _params.WalkSpeed);
                Jump(_params.JumpSpeed);
            }
        }

        SetHorizontal(Vector3.ClampMagnitude(MovementMath.Horizontal(_velocity), _params.MaxSpeed));
        float gravity = _params.Gravity * (IsWallSliding ? _params.WallGravityScale : 1f);
        gravity *= _actionControl != null ? _actionControl.GravityMultiplier : 1f;
        float gravityDt = IsWallSliding ? WallUnprotectedDeltaTime(dt) : dt;
        _velocity.y = IsGrounded ? -_params.Gravity * dt : _velocity.y - gravity * gravityDt;
        if (IsWallSliding && gravityDt > 0f) _velocity.y = Mathf.Max(_velocity.y, -_params.WallMaxFallSpeed);
        UpdateCapsule();
        _controller.stepOffset = IsGrounded ? _groundStepOffset : 0f;

        bool wasGrounded = IsGrounded;
        bool wasWallSliding = IsWallSliding;
        if (_actionMotion != null && _actionMotion.DiveInAir && !IsGrounded)
        {
            if (IsWallSliding) ExitWall();
            float targetY = -Mathf.Min(_actionMotion.MaxDiveSpeed, HorizontalSpeed * Mathf.Tan(_actionMotion.DiveAngle * Mathf.Deg2Rad));
            _velocity.y = Mathf.Max(-_actionMotion.MaxDiveSpeed,
                Mathf.Lerp(_velocity.y, targetY, 1f - Mathf.Exp(-_actionMotion.DiveResponse * dt)));
        }
        _contacts.Move(ref _velocity, dt, point => MotionPathPoint?.Invoke(point));
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
            if (!IsSliding && !IsWallSliding && !wasWallSliding && !(_actionMotion?.DiveInAir == true))
            {
                TryEnterWall(wish);
                if (jump && IsWallSliding) JumpFromWall();
            }
        }
        // 能量不在这里结算：唯一账户是 VectorEnergy（执行序 10，在本组件之后自动积能）
        _commandJump = false;
    }

    private void UpdateFreeMovement(float dt, Vector3 wish)
    {
        Vector3 horizontal = MovementMath.Horizontal(_velocity);
        if (IsGrounded)
        {
            float target = IsSprinting ? _params.GroundSpeedThreshold : _params.WalkSpeed;
            if (!InFrictionWindow && _params.GroundFriction > 0f)
            {
                horizontal *= Mathf.Max(0f, 1f - _params.GroundFriction * dt);
                if (horizontal.sqrMagnitude < _params.GroundStopSpeed * _params.GroundStopSpeed)
                    horizontal = Vector3.zero;
            }
            horizontal = MovementMath.Accelerate(horizontal, wish, target, _params.RunAccel, dt,
                _params.Pump == PumpMode.WindowPump && InFrictionWindow);
        }
        else horizontal = MovementMath.Accelerate(horizontal, wish, _params.GroundSpeedThreshold, _params.AirControl, dt, false);
        SetHorizontal(horizontal);
    }

    private void TryEnterWall(Vector3 wish)
    {
        // 以入墙速度与墙切线的锐角判定；只接受确实向墙内运动且小于上限的接触。
        if (!_contacts.HasWall || _contacts.WallAngle + 0.01f >= _params.WallMaxApproachAngle) return;
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

    private bool TryStand()
    {
        if (!_contacts.CanResize(_params.CapsuleBaseHeight, _params.CapsuleBaseRadius)) return false;
        IsSliding = false;
        return true;
    }

    private void UpdateCapsule()
    {
        if (IsSliding) return;
        float t = Mathf.InverseLerp(_params.CapsuleShrinkStartSpeed, _params.CapsuleShrinkEndSpeed, HorizontalSpeed);
        float height = Mathf.Lerp(_params.CapsuleBaseHeight, _params.CapsuleFastHeight, t);
        float radius = Mathf.Lerp(_params.CapsuleBaseRadius, _params.CapsuleMinRadius, t);
        if ((height > _controller.height || radius > _controller.radius) && !_contacts.CanResize(height, radius)) return;
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
            case MotorCommandKind.Jump: return (IsGrounded || IsWallSliding) && (!IsSliding || _contacts.CanResize(_params.CapsuleBaseHeight, _params.CapsuleBaseRadius));
            case MotorCommandKind.EnterSlide: return IsGrounded && !IsSliding && HorizontalSpeed >= _params.GroundSpeedThreshold * _params.SlideSpeedRatio;
            case MotorCommandKind.ExitSlide: return !IsSliding || _contacts.CanResize(_params.CapsuleBaseHeight, _params.CapsuleBaseRadius);
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
                break;
            case MotorCommandKind.EnterSlide: IsSliding = true; SetCapsule(_params.SlideCapsuleHeight, _controller.radius); break;
            case MotorCommandKind.ExitSlide: return !IsSliding || TryStand();
            case MotorCommandKind.SetHorizontalSpeed: SetHorizontalSpeed(value); break;
            case MotorCommandKind.LaunchVertical: LaunchVertical(value); break;
            case MotorCommandKind.ReverseHorizontal: ReverseHorizontal(); break;
            case MotorCommandKind.AddForwardImpulse: SetHorizontal(Vector3.ClampMagnitude(MovementMath.Horizontal(_velocity) + transform.forward * value, _params.MaxSpeed)); break;
            case MotorCommandKind.ClearHorizontal: SetHorizontal(Vector3.zero); break;
        }
        return true;
    }
    public void CancelActionCommands() => _commandJump = false;
    public void SetHorizontalSpeed(float speed)
    {
        if (_params == null) return;
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

    public void ResetState()
    {
        _velocity = Vector3.zero;
        _actionMotion = null; _motionInstance = 0;
        _commandJump = false;
        _facingAngularVelocity = 0f;
        JumpCount = WallJumpCount = 0;
        State = MovementState.Airborne;
        IsSliding = IsSprinting = _wallLocked = _wallJumpExitProtected = false;
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
        if (_input is PlayerInputReader reader) reader.Cleared -= ClearPendingInput;
        if (_controller != null) _controller.stepOffset = _groundStepOffset;
        if (_params != null) ResetState();
    }
}
