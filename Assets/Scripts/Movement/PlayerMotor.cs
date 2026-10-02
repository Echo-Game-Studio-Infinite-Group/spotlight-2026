using System;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(CharacterController))]
public sealed class PlayerMotor : MonoBehaviour
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

    public MovementParams Params => _params;
    public MovementState State { get; private set; } = MovementState.Airborne;
    public Vector3 Velocity => _velocity;
    public float HorizontalSpeed => MovementMath.Horizontal(_velocity).magnitude;
    public bool IsGrounded => State == MovementState.Grounded;
    public bool IsWallSliding => State == MovementState.WallSlide;
    public bool IsSliding { get; private set; }
    public bool IsSprinting { get; private set; }
    public float Energy { get; private set; }
    public int JumpCount { get; private set; }
    public int WallJumpCount { get; private set; }
    public float WallApproachAngle { get; private set; }
    public Vector3 WallNormal => IsWallSliding ? _wallNormal : Vector3.zero;
    public bool InFrictionWindow => IsGrounded && FrictionWindowRemaining > 0f;
    public float FrictionWindowRemaining => IsGrounded ? Mathf.Max(0f, _params.FrictionExemptWindow - (_clock - _landTime)) : 0f;
    public float WallWindowRemaining => IsWallSliding ? Mathf.Max(0f, _params.WallGraceTime - (_clock - _wallTime)) : 0f;
    public event Action<Vector3> Teleported;

    public void SetParams(MovementParams parameters)
    {
        _params = parameters;
        if (_controller != null) _contacts = new MovementContacts(_controller, _params);
    }
    public void SetInput(IPlayerInput input) => _input = input;
    public void SetMovementReference(Transform reference) => _movementReference = reference;

    private void Awake()
    {
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
        PlayerInputFrame frame = _input != null ? _input.ReadFrame() : default;
        Simulate(frame, TimeManager.PlayerFixedDeltaTime, TimeManager.UnscaledTime);
    }

    public void Simulate(PlayerInputFrame input, float dt, float inputTime)
    {
        if (_contacts == null || !_controller.enabled || dt < 0f) return;
        if (input.JumpPressed) _jumpUntil = input.JumpTime + _params.JumpBufferWindow;
        if (dt == 0f) return;
        _clock += dt;
        Vector3 wish = WishDirection(input.Move);
        IsSprinting = input.SprintHeld && input.Move.y > 0f;
        RearmWall();
        if (IsWallSliding)
        {
            bool closeToWall = _contacts.ProbeWall(_wallNormal, _params.WallProbeDistance, out _);
            if (closeToWall) _wallLastContactTime = _clock;
            float separation = Vector3.Dot(transform.position - _lockedPoint, _lockedNormal) - _controller.radius;
            if (Vector3.Dot(wish, _wallNormal) > 0.1f ||
                separation > _params.WallProbeDistance + _controller.skinWidth * 2f ||
                _clock - _wallLastContactTime > _params.WallContactGraceTime)
                ExitWall();
        }

        UpdateFacing(wish, dt);
        Vector3 drive = transform.forward * wish.magnitude;
        // 仅改变方向，不用向量插值，避免转弯时丢失速度；墙面与离墙保护优先。
        if (wish != Vector3.zero && !IsWallSliding && !_wallJumpExitProtected &&
            _clock - _lastWallJump >= _params.WallJumpCooldown)
            SetHorizontal(transform.forward * HorizontalSpeed);

        if (input.SlidePressed && IsGrounded && !IsSliding &&
            HorizontalSpeed >= _params.GroundSpeedThreshold * _params.SlideSpeedRatio)
        {
            IsSliding = true;
            SetCapsule(_params.SlideCapsuleHeight, _controller.radius);
        }

        bool jump = inputTime <= _jumpUntil;
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
            UpdateWall(dt, wish);
            if (jump) JumpFromWall();
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
        _velocity.y = IsGrounded ? -_params.Gravity * dt : _velocity.y - gravity * dt;
        if (IsWallSliding) _velocity.y = Mathf.Max(_velocity.y, -_params.WallMaxFallSpeed);
        UpdateCapsule();
        _controller.stepOffset = IsGrounded ? _groundStepOffset : 0f;

        bool wasGrounded = IsGrounded;
        _contacts.Move(ref _velocity, dt);
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
            if (!IsSliding && !IsWallSliding)
            {
                TryEnterWall(wish);
                if (jump && IsWallSliding) JumpFromWall();
            }
        }
        Energy = Mathf.Min(_params.EnergyMax, Energy + Mathf.Max(0f, HorizontalSpeed - _params.GroundSpeedThreshold)
            * _params.EnergyPerSecondPerExcessSpeed * dt);
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
    }

    private void UpdateWall(float dt, Vector3 wish)
    {
        if (_wallTangent != Vector3.zero) _wallSpeed = Mathf.Min(_wallSpeed, HorizontalSpeed);
        if (_wallTangent == Vector3.zero) _wallTangent = MovementMath.WallTangent(Vector3.zero, _wallNormal, wish);
        if (WallWindowRemaining <= 0f)
        {
            if (_wallTangent == Vector3.zero) _wallSpeed = 0f;
            else _wallSpeed *= Mathf.Exp(-_params.WallFriction * dt);
        }
        SetHorizontal(_wallTangent * _wallSpeed);
    }

    private void JumpFromWall()
    {
        if (_clock - _lastWallJump < _params.WallJumpCooldown) return;
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

    private void UpdateFacing(Vector3 wish, float dt)
    {
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
    private void OnControllerColliderHit(ControllerColliderHit hit) => _contacts?.RecordHit(hit);

    public void ResetState()
    {
        _velocity = Vector3.zero;
        _facingAngularVelocity = 0f;
        Energy = 0f;
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
        if (_input is PlayerInputReader reader) reader.Cleared -= ClearPendingInput;
        if (_controller != null) _controller.stepOffset = _groundStepOffset;
        if (_params != null) ResetState();
    }
}
