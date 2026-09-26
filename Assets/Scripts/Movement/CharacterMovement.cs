using System;
using UnityEngine;

// 玩家移动：相机相对行走 + 冲刺加速 + 预输入兔子跳
// 移植自 git-testfield/Assets/Scripts/CharacterMovement.cs，算法与默认值逐字保留，仅按本仓库规范适配
// （私有字段改 _camelCase、注释改中文、补疾跑状态事件给相机特效订阅）
// 逻辑按 60Hz 定步长推进：帧率变化不影响手感，掉帧时最多补 0.1s，避免长时间卡顿后瞬移
[DisallowMultipleComponent]
[RequireComponent(typeof(CharacterController))]
public sealed class CharacterMovement : MonoBehaviour
{
    private const float TickDuration = 1f / 60f;

    [Header("方向参考")]
    [Tooltip("移动以此 transform 的水平朝前方向为基准。留空时默认取 Main Camera")]
    [SerializeField] private Transform _movementReference;

    [Header("行走与冲刺（米 / 秒）")]
    [SerializeField, Min(0.1f)] private float _walkSpeed = 4f;
    [Tooltip("Shift 持续地面冲刺速度。兔子跳可以超过这个速度")]
    [SerializeField, Min(0.1f)] private float _groundSpeed = 10f;
    [SerializeField, Min(0.1f)] private float _walkAcceleration = 35f;
    [SerializeField, Min(0.1f)] private float _sprintAcceleration = 12f;
    [SerializeField, Min(0f)] private float _airSprintAcceleration = 8f;
    [SerializeField, Min(0.1f)] private float _groundDeceleration = 25f;

    [Header("跳跃")]
    [SerializeField, Min(0.1f)] private float _jumpHeight = 1.6f;
    [SerializeField, Min(0.1f)] private float _gravity = 25f;
    [SerializeField, Min(0.01f)] private float _jumpBufferTime = 0.12f;
    [Tooltip("落地后这段时间内不施加地面摩擦——兔子跳加速的唯一来源")]
    [SerializeField, Min(0f)] private float _landingGraceTime = 0.2f;

    private CharacterController _controller;
    private Vector3 _horizontalVelocity;
    private float _verticalVelocity;
    private float _jumpBufferRemaining;
    private float _landingGraceRemaining;
    private float _accumulator;
    private float _groundedStepOffset;
    private bool _grounded;

    public float CurrentSpeed => _horizontalVelocity.magnitude;
    public Vector3 Velocity => _horizontalVelocity + Vector3.up * _verticalVelocity;
    public bool IsGrounded => _grounded;
    public float LandingGraceRemaining => _landingGraceRemaining;

    // 相机特效（红移 / 动态模糊）按疾跑状态驱动，所以状态必须外露且只在变化时广播
    public bool IsSprinting { get; private set; }
    public event Action<bool> SprintChanged;

    private void Awake()
    {
        CacheController();
        if (_movementReference == null && Camera.main != null)
        {
            _movementReference = Camera.main.transform;
        }
    }

    private void OnEnable() => ResetMotion();

    private void OnDisable()
    {
        // 松开时还原 stepOffset：空中踏步会导致离地判定抖动，禁止后必须还回去
        if (_controller != null)
        {
            _controller.stepOffset = _groundedStepOffset;
        }
    }

    private void Update()
    {
        Vector2 movement = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        bool sprint = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        Simulate(Time.deltaTime, movement, sprint, Input.GetKeyDown(KeyCode.Space));
    }

    // 每帧只采样一次输入，随后按 60 tick/秒推进——测试可以脱离 Update 直接喂输入
    public void Simulate(float deltaTime, Vector2 movement, bool sprintHeld, bool jumpPressed)
    {
        CacheController();
        if (!_controller.enabled || !gameObject.activeInHierarchy || deltaTime <= 0f)
        {
            return;
        }

        if (jumpPressed)
        {
            _jumpBufferRemaining = _jumpBufferTime;
        }

        movement = Vector2.ClampMagnitude(movement, 1f);
        // 只有同时按住前向输入才算冲刺：原地按 Shift 不应该触发速度感特效
        bool sprinting = sprintHeld && movement.y > 0f;
        SetSprinting(sprinting);

        // 卡顿后补 tick 的上限，防止长时间停顿后一次性移动一大段
        _accumulator += Mathf.Min(deltaTime, 0.1f);
        while (_accumulator + 0.000001f >= TickDuration)
        {
            Tick(movement, sprinting);
            _accumulator = Mathf.Max(0f, _accumulator - TickDuration);
        }
    }

    // 清空动量与待处理输入：传送、重生、重新启用后调用，避免残留速度把人甩飞
    public void ResetMotion()
    {
        CacheController();
        _horizontalVelocity = Vector3.zero;
        _verticalVelocity = 0f;
        _jumpBufferRemaining = 0f;
        _landingGraceRemaining = 0f;
        _accumulator = 0f;
        _grounded = _controller.enabled && _controller.isGrounded;
        SetSprinting(false);
    }

    private void SetSprinting(bool sprinting)
    {
        if (IsSprinting == sprinting)
        {
            return;
        }

        IsSprinting = sprinting;
        SprintChanged?.Invoke(sprinting);
    }

    private void CacheController()
    {
        if (_controller != null)
        {
            return;
        }

        _controller = GetComponent<CharacterController>();
        _groundedStepOffset = _controller.stepOffset;
    }

    // 输入方向 → 世界方向：投影掉俯仰，避免抬头时前进方向带垂直分量
    private Vector3 GetMoveDirection(Vector2 movement)
    {
        Vector3 forward = _movementReference != null ? _movementReference.forward : Vector3.forward;
        forward = Vector3.ProjectOnPlane(forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.forward;
        }

        forward.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        return (forward * movement.y + right * movement.x).normalized;
    }

    private void Tick(Vector2 movement, bool sprinting)
    {
        bool hasInput = movement.sqrMagnitude > 0.0001f;
        Vector3 direction = hasInput ? GetMoveDirection(movement) : _horizontalVelocity.normalized;
        float speed = _horizontalVelocity.magnitude;

        if (hasInput)
        {
            transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
        }

        bool jumping = _grounded && _jumpBufferRemaining > 0f;
        if (jumping)
        {
            // 同帧方向 + 跳跃可以从静止起跳。保留已有的更快动量，而不是用行走速度覆盖掉
            if (hasInput)
            {
                speed = Mathf.Max(speed, _walkSpeed * movement.magnitude);
            }

            _verticalVelocity = Mathf.Sqrt(2f * _gravity * _jumpHeight);
            _jumpBufferRemaining = 0f;
            _grounded = false;
            _landingGraceRemaining = 0f;
            if (sprinting)
            {
                direction = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            }
        }

        if (_grounded)
        {
            float targetSpeed = hasInput ? (sprinting ? _groundSpeed : _walkSpeed * movement.magnitude) : 0f;
            if (_landingGraceRemaining <= 0.000001f && speed > targetSpeed)
            {
                speed = Mathf.MoveTowards(speed, targetSpeed, _groundDeceleration * TickDuration);
            }
            else if (hasInput && speed < targetSpeed)
            {
                speed = Mathf.MoveTowards(speed, targetSpeed,
                    (sprinting ? _sprintAcceleration : _walkAcceleration) * TickDuration);
            }
            else if (sprinting && _landingGraceRemaining > 0.000001f)
            {
                // 免摩擦窗口内持续加档：这就是跑-跳循环能无限涨速的实现
                speed += _sprintAcceleration * TickDuration;
            }
        }
        else if (sprinting)
        {
            speed += _airSprintAcceleration * TickDuration;
        }

        _horizontalVelocity = direction * speed;
        // 离地时把 stepOffset 归零，否则下落的碰撞查询会误判贴地
        _controller.stepOffset = _grounded ? _groundedStepOffset : 0f;
        if (_grounded && _verticalVelocity <= 0f)
        {
            _verticalVelocity = -2f;
        }

        float verticalDistance = _verticalVelocity * TickDuration - 0.5f * _gravity * TickDuration * TickDuration;
        _verticalVelocity -= _gravity * TickDuration;

        bool wasGrounded = _grounded;
        CollisionFlags lastFlags = CollisionFlags.None;
        bool hitCeiling = false;

        // 拆分位移：高速下一次性 Move 会穿透薄墙和台阶边缘，按半径切段更可靠
        float travel = _horizontalVelocity.magnitude * TickDuration + Mathf.Abs(verticalDistance);
        float segmentLength = Mathf.Max(0.05f, _controller.radius * 0.5f);
        int segments = Mathf.Clamp(Mathf.CeilToInt(travel / segmentLength), 1, 64);
        for (int i = 0; i < segments; i++)
        {
            Vector3 displacement = _horizontalVelocity * (TickDuration / segments);
            displacement.y = hitCeiling && verticalDistance > 0f ? 0f : verticalDistance / segments;
            lastFlags = _controller.Move(displacement);
            hitCeiling |= (lastFlags & CollisionFlags.Above) != 0;
        }

        if (hitCeiling && _verticalVelocity > 0f)
        {
            _verticalVelocity = 0f;
        }

        _grounded = (lastFlags & CollisionFlags.Below) != 0 && _verticalVelocity <= 0f;
        if (_grounded)
        {
            _verticalVelocity = -2f;
            // 落地当帧开窗，之后逐 tick 递减；离地立即清零
            _landingGraceRemaining = !wasGrounded
                ? _landingGraceTime
                : Mathf.Max(0f, _landingGraceRemaining - TickDuration);
        }
        else
        {
            _landingGraceRemaining = 0f;
        }

        _jumpBufferRemaining = Mathf.Max(0f, _jumpBufferRemaining - TickDuration);
    }

    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        // 撞墙丢弃法向动量，但保留切向——擦着墙过要能滑走而不是被粘住
        if (hit.normal.y >= Mathf.Cos(_controller.slopeLimit * Mathf.Deg2Rad) || hit.normal.y < -0.5f)
        {
            return;
        }

        Vector3 normal = Vector3.ProjectOnPlane(hit.normal, Vector3.up).normalized;
        float intoWall = Vector3.Dot(_horizontalVelocity, normal);
        if (intoWall < 0f)
        {
            _horizontalVelocity -= normal * intoWall;
        }
    }

    private void OnValidate()
    {
        _walkSpeed = Mathf.Max(0.1f, _walkSpeed);
        _groundSpeed = Mathf.Max(_walkSpeed, _groundSpeed);
        _walkAcceleration = Mathf.Max(0.1f, _walkAcceleration);
        _sprintAcceleration = Mathf.Max(0.1f, _sprintAcceleration);
        _airSprintAcceleration = Mathf.Max(0f, _airSprintAcceleration);
        _groundDeceleration = Mathf.Max(0.1f, _groundDeceleration);
        _jumpHeight = Mathf.Max(0.1f, _jumpHeight);
        _gravity = Mathf.Max(0.1f, _gravity);
        _jumpBufferTime = Mathf.Max(TickDuration, _jumpBufferTime);
        _landingGraceTime = Mathf.Max(0f, _landingGraceTime);
    }
}
