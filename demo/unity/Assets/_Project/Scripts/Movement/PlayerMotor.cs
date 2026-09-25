using UnityEngine;

// 运动学移动控制器：FixedUpdate 手动积分，CharacterController.Move 只做碰撞查询（禁止刚体力模拟手感）
// 算法为 Quake accelerate / friction 公式照写（数学不受版权保护，未拷贝任何 GPL 代码）
// 全部数值来自 MovementParams；碰撞胶囊 pivot 约定在脚底（center.y = height/2）
[RequireComponent(typeof(CharacterController))]
public class PlayerMotor : MonoBehaviour
{
    [SerializeField] private MovementParams _params;

    private CharacterController _controller;
    private Transform _cameraTransform;

    private Vector3 _velocity;
    private bool _grounded;
    private bool _sliding;

    private float _playerTime;        // 玩家时间轴时钟，窗口/冷却/缓冲统一基于它
    private float _lastLandTime;      // 落地时间戳，免摩擦窗口起点
    private float _lastWallJumpTime;
    private float _jumpPressedTime;   // 跳跃预输入时间戳（策划案第 2 节滞空预输入）
    private bool _jumpPressedPending; // Update 捕获的按下沿，避免 FixedUpdate 漏检

    private Vector3 _wallNormal;
    private bool _wallTouch;

    private static readonly Collider[] StandUpOverlapBuffer = new Collider[8];

    public Vector3 Velocity => _velocity;
    public bool IsGrounded => _grounded;
    public bool IsSliding => _sliding;

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        _lastLandTime = -999f;
        _lastWallJumpTime = -999f;
        _jumpPressedTime = -999f;
        if (_params == null)
        {
            Debug.LogError("[PlayerMotor] MovementParams 未赋值", this);
            enabled = false;
        }
    }

    private void Update()
    {
        // 相机只取 yaw 作“摄像机对应方向”参与转向；接 Cinemachine 时仅需替换此来源
        if (_cameraTransform == null && Camera.main != null)
        {
            _cameraTransform = Camera.main.transform;
        }

        // 按下沿在 Update 捕获：FixedUpdate 可能漏检无固定步进帧上的按键
        if (Input.GetKeyDown(KeyCode.Space))
        {
            _jumpPressedPending = true;
        }

        // TODO: speed/energy 的 HUD 显示（本次骨架不做 UI）
    }

    private void FixedUpdate()
    {
        float dt = TimeManager.PlayerDeltaTime;
        _playerTime += dt;

        if (_jumpPressedPending)
        {
            _jumpPressedPending = false;
            _jumpPressedTime = _playerTime;
        }

        bool shiftHeld = Input.GetKey(KeyCode.LeftShift);
        bool shiftPressed = Input.GetKeyDown(KeyCode.LeftShift);
        bool running = shiftHeld; // 策划案 Shift+W 奔跑；骨架不细分无 W 的 Shift
        Vector3 wishDir = ComputeWishDir();

        // 滑铲进入：速度达到约 0.8x 地速阈值时按下 Shift（策划案第 3 节）
        // Shift 被奔跑共用，故“保持 Shift 但松开 W”也视为滑铲意图
        bool slideTrigger = shiftPressed || (shiftHeld && !Input.GetKey(KeyCode.W));
        if (!_sliding && _grounded && slideTrigger &&
            HorizontalSpeed() >= _params.GroundSpeedThreshold * _params.SlideSpeedRatio)
        {
            EnterSlide();
        }

        if (_sliding)
        {
            UpdateSlide();
        }
        else
        {
            if (_grounded)
            {
                ApplyGroundFriction(dt, running);
                ApplyGroundAcceleration(dt, wishDir, running);
            }
            else if (_params.AirControl > 0f)
            {
                ApplyAirAcceleration(dt, wishDir); // 本次骨架 AirControl 恒 0，空中不加速
            }
            HandleJump(wishDir, running);
            HandleWallJump();
        }

        ApplyGravity(dt);
        UpdateCapsuleBySpeed();
        _controller.Move(_velocity * dt);
        UpdateGroundState();

        // TODO: 矢量转换器——能量每 tick 增加 max(0, 水平速度 - 地速阈值)（策划案第 4 节）
    }

    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        // 空中接触近竖直面时记录法线，供下一 tick 蹬墙判定
        if (!_grounded && Vector3.Dot(hit.normal, Vector3.up) < _params.WallNormalMaxUpDot)
        {
            _wallNormal = hit.normal;
            _wallTouch = true;
        }
    }

    // 输入方向 → 世界方向：绕相机 yaw 旋转（“按前进保留速度大小、转向摄像机对应方向”的基础）
    private Vector3 ComputeWishDir()
    {
        float x = Input.GetAxisRaw("Horizontal");
        float z = Input.GetAxisRaw("Vertical");
        if (x == 0f && z == 0f) return Vector3.zero;
        float yaw = _cameraTransform != null ? _cameraTransform.eulerAngles.y : transform.eulerAngles.y;
        Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * new Vector3(x, 0f, z);
        return dir.sqrMagnitude > 1f ? dir.normalized : dir;
    }

    // 摩擦只在超速时生效：平跑收敛于地速阈值；免摩擦窗口内跳过结算——兔子跳的全部实现
    private void ApplyGroundFriction(float dt, bool running)
    {
        float speed = HorizontalSpeed();
        // 行走时以行走目标速度为门槛，实现“行走快速减速至固定较小值”（策划案第 1 节）
        float threshold = running ? _params.GroundSpeedThreshold : _params.WalkSpeed;
        if (speed <= threshold) return;
        if (_playerTime - _lastLandTime <= _params.FrictionExemptWindow) return;

        float newSpeed = Mathf.Max(0f, speed * (1f - _params.GroundFriction * dt));
        Vector3 horiz = HorizontalVelocity();
        _velocity.x = horiz.x / speed * newSpeed;
        _velocity.z = horiz.z / speed * newSpeed;
    }

    // Quake accelerate：addspeed = wishspeed - dot(v, wishdir)，为正则
    // v += wishdir * min(accel * wishspeed * dt, addspeed)
    // 奔跑 wishspeed 取地速阈值：免摩擦窗口内小幅转向可产生增益，即兔子跳的加速来源
    private void ApplyGroundAcceleration(float dt, Vector3 wishDir, bool running)
    {
        if (wishDir == Vector3.zero) return;
        float wishspeed = running ? _params.GroundSpeedThreshold : _params.WalkSpeed;
        float addspeed = wishspeed - Vector3.Dot(HorizontalVelocity(), wishDir);
        if (addspeed <= 0f) return;
        float accel = Mathf.Min(_params.RunAccel * wishspeed * dt, addspeed);
        _velocity += wishDir * accel;
    }

    // 空中加速与地面同构；骨架 AirControl 为 0，仅保留接口
    private void ApplyAirAcceleration(float dt, Vector3 wishDir)
    {
        if (wishDir == Vector3.zero || _params.AirControl <= 0f) return;
        float addspeed = _params.GroundSpeedThreshold - Vector3.Dot(HorizontalVelocity(), wishDir);
        if (addspeed <= 0f) return;
        float accel = Mathf.Min(_params.AirControl * _params.GroundSpeedThreshold * dt, addspeed);
        _velocity += wishDir * accel;
    }

    private void HandleJump(Vector3 wishDir, bool running)
    {
        if (_grounded && _playerTime - _jumpPressedTime <= _params.JumpBufferWindow)
        {
            _jumpPressedTime = -999f;
            Vector3 horiz = HorizontalVelocity();
            if (!running && wishDir != Vector3.zero)
            {
                // 非奔跑的前跳/斜向跳取行走速度；奔跑中方向锁定为面朝方向，保留全部水平动量
                _velocity.x = wishDir.x * _params.WalkSpeed;
                _velocity.z = wishDir.z * _params.WalkSpeed;
            }
            _velocity.y = _params.JumpSpeed;
            _grounded = false;
        }
    }

    // 蹬墙：空中贴墙 + 速度与墙面夹角大于阈值（小于 30 度不可蹬，策划案第 1 节）
    // 蹬出速度 = 反射分量 * 系数 + 法线冲量，垂直分量取上抛冲量
    private void HandleWallJump()
    {
        if (_grounded || !_wallTouch) return;
        if (_playerTime - _jumpPressedTime > _params.JumpBufferWindow) return;
        if (_playerTime - _lastWallJumpTime < _params.WallJumpCooldown) return;

        Vector3 horiz = HorizontalVelocity();
        float speed = horiz.magnitude;
        if (speed < 0.01f) return;

        // 与墙面的夹角 = 与“指向墙内的法线”夹角的余角；与墙平行（冲浪角）时不可蹬
        float angleToWall = 90f - Vector3.Angle(horiz, -_wallNormal);
        if (angleToWall <= _params.WallJumpMinAngle) return;

        _jumpPressedTime = -999f;
        Vector3 outHoriz = Vector3.Reflect(horiz, _wallNormal) * _params.WallJumpReflectRatio
                           + _wallNormal * _params.WallJumpNormalImpulse;
        _velocity.x = outHoriz.x;
        _velocity.z = outHoriz.z;
        _velocity.y = _params.WallJumpUpImpulse;
        _lastWallJumpTime = _playerTime;
        _wallTouch = false;
        _grounded = false;
    }

    private void UpdateSlide()
    {
        Vector3 horiz = HorizontalVelocity();
        float speed = horiz.magnitude;

        // 滑铲可被前跳取消（策划案第 3 节）：缓冲窗内按跳立即终结滑铲并保留水平动量起跳
        if (_playerTime - _jumpPressedTime <= _params.JumpBufferWindow)
        {
            _jumpPressedTime = -999f;
            if (ExitSlideIfRoom())
            {
                _velocity.y = _params.JumpSpeed;
                _grounded = false;
                return;
            }
        }

        // 滑铲期间速度快速线性衰减
        if (speed > 0f)
        {
            float newSpeed = Mathf.Max(0f, speed - _params.SlideDecel * TimeManager.PlayerDeltaTime);
            _velocity.x = horiz.x / speed * newSpeed;
            _velocity.z = horiz.z / speed * newSpeed;
        }

        bool shiftHeld = Input.GetKey(KeyCode.LeftShift);
        if (speed < _params.SlideEndSpeed || !shiftHeld)
        {
            ExitSlideIfRoom(); // 头顶受阻（限高门内）时保持滑铲姿态
        }
    }

    private void EnterSlide()
    {
        _sliding = true;
        SetCapsule(_params.SlideCapsuleHeight, _controller.radius);
    }

    private bool ExitSlideIfRoom()
    {
        if (!CanStandUp(_params.CapsuleBaseHeight)) return false;
        _sliding = false;
        SetCapsule(_params.CapsuleBaseHeight, _controller.radius);
        return true;
    }

    // 起身头顶阻挡检测：OverlapCapsule 排除自身碰撞体
    private bool CanStandUp(float standHeight)
    {
        float skin = _controller.skinWidth;
        float radius = Mathf.Max(_controller.radius - skin, 0.05f);
        Vector3 bottom = transform.position + Vector3.up * (skin * 2f);
        Vector3 top = transform.position + Vector3.up * Mathf.Max(standHeight - skin * 2f, bottom.y - transform.position.y + 0.05f);
        int count = Physics.OverlapCapsuleNonAlloc(bottom, top, radius, StandUpOverlapBuffer,
            _params.CollisionMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            if (StandUpOverlapBuffer[i].transform.IsChildOf(transform)) continue;
            return false;
        }
        return true;
    }

    private void ApplyGravity(float dt)
    {
        if (_grounded)
        {
            // 贴地下压取“一帧重力”：量级由 Gravity 推导，保证 isGrounded 稳定
            _velocity.y = -_params.Gravity * dt;
        }
        else
        {
            _velocity.y -= _params.Gravity * dt;
        }
    }

    // 受击框随速度变细：胶囊高度/半径按 |v| 插值，半径有“不细于肩宽”的下限
    private void UpdateCapsuleBySpeed()
    {
        if (_sliding) return; // 滑铲高度由 Enter/Exit 管理
        float t = Mathf.InverseLerp(_params.CapsuleShrinkStartSpeed, _params.CapsuleShrinkEndSpeed, HorizontalSpeed());
        SetCapsule(Mathf.Lerp(_params.CapsuleBaseHeight, _params.CapsuleFastHeight, t),
            Mathf.Lerp(_params.CapsuleBaseRadius, _params.CapsuleMinRadius, t));
    }

    private void SetCapsule(float height, float radius)
    {
        _controller.height = height;
        _controller.radius = radius;
        _controller.center = new Vector3(0f, height * 0.5f, 0f); // 底面始终贴住 pivot（脚底）
    }

    private void UpdateGroundState()
    {
        bool wasGrounded = _grounded;
        _grounded = _controller.isGrounded;
        if (_grounded)
        {
            _wallTouch = false;
            if (!wasGrounded)
            {
                _lastLandTime = _playerTime; // 免摩擦窗口自落地 tick 起算
            }
        }
    }

    private Vector3 HorizontalVelocity() => new Vector3(_velocity.x, 0f, _velocity.z);
    private float HorizontalSpeed() => HorizontalVelocity().magnitude;

    // 调试：速度向量（红）；落地免摩擦窗口内（黄）；滑铲中（青）
    private void OnDrawGizmos()
    {
        if (!Application.isPlaying || _params == null) return;
        Gizmos.color = Color.red;
        Gizmos.DrawLine(transform.position, transform.position + _velocity);

        if (_grounded && _playerTime - _lastLandTime <= _params.FrictionExemptWindow)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position + Vector3.up * (_controller.radius * 0.5f), _controller.radius);
        }

        if (_sliding)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(transform.position + Vector3.up * (_controller.height * 0.5f),
                new Vector3(_controller.radius * 2f, _controller.height, _controller.radius * 2f));
        }
    }
}
