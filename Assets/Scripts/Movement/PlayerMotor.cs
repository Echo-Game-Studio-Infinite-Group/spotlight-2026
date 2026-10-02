using UnityEngine;

// 运动学移动控制器：FixedUpdate 手动积分，CharacterController.Move 只做碰撞查询（禁止刚体力模拟手感）
// 算法为 Quake accelerate / friction 公式照写（数学不受版权保护，未拷贝任何 GPL 代码）
// 全部数值来自 MovementParams；碰撞胶囊 pivot 约定在脚底（center.y = height/2）
// 泵油模型与 MovementMath.cs（demo/movement-sim）语义一致：窗口内泵加速不受投影上限，窗口外一切照字面公式
[RequireComponent(typeof(CharacterController))]
public class PlayerMotor : MonoBehaviour, IEnergyAccount, IKnockbackReceiver
{
    [SerializeField] private MovementParams _params;

    private CharacterController _controller;
    private Transform _cameraTransform;
    private IPlayerInput _input;      // 输入缝：默认 LegacyPlayerInput，测试可注入脚本桩

    private Vector3 _velocity;
    private bool _grounded;
    private bool _sliding;

    private float _playerTime;        // 玩家时间轴时钟，窗口/冷却/缓冲统一基于它
    private float _lastLandTime;      // 落地时间戳，免摩擦窗口起点
    private float _lastWallJumpTime;
    private float _jumpPressedTime;   // 跳跃预输入时间戳（策划案第 2 节滞空预输入）
    private bool _jumpPressedPending; // Update 捕获的按下沿，避免 FixedUpdate 漏检

    private float _energy;            // 矢量转换器能量（策划案第 4 节）
    private int _jumpCount;           // 已执行跳跃次数（测试用只读统计）

    private Vector3 _wallNormal;
    private bool _wallTouch;

    // —— 战斗命令契约状态（战斗系统底层接口设计 §4.7）——
    private bool _gravitySuspended;    // 连斩滞空：悬停竖直速度
    private Vector3 _overrideVelocity; // 闪避钟摆：短时速度覆盖
    private float _overrideTimer;

    /// <summary>落地时问战斗侧"要不要清空动量"（高速普攻落地未派生则清）；由 PlayerCombat 挂接</summary>
    public IMotorLandingClient LandingClient { get; set; }

    private static readonly Collider[] StandUpOverlapBuffer = new Collider[8];

    public Vector3 Velocity => _velocity;
    public bool IsGrounded => _grounded;
    public bool IsSliding => _sliding;
    public MovementParams Params => _params;
    public float HorizontalSpeed => HorizontalVelocity().magnitude;
    public float Energy => _energy;
    // IEnergyAccount：能量账户当前由移动层代管（后续外迁 VectorEnergy 时换实现方，战斗侧经接口无感）
    public float CurrentEnergy => _energy;
    public int JumpCount => _jumpCount;
    // 免摩擦窗口内（严格小于窗口时长，与仿真 InFrictionFreeWindow 的判定一致）
    public bool InFrictionWindow => _grounded && (_playerTime - _lastLandTime) < _params.FrictionExemptWindow;
    public float FrictionWindowRemaining =>
        _params == null ? 0f : Mathf.Max(0f, _params.FrictionExemptWindow - (_playerTime - _lastLandTime));

    // 测试注入：必须在组件激活（Awake）前调用，否则 Awake 的默认/初始化逻辑会覆盖
    public void SetInput(IPlayerInput input) { if (input != null) _input = input; }
    public void SetParams(MovementParams parameters) { if (parameters != null) _params = parameters; }

    // 状态复位（掉落重生等）：速度/能量/滑铲清零，着地状态由下一次碰撞检测重建
    public void ResetState()
    {
        if (_sliding) SetCapsule(_params.CapsuleBaseHeight, _params.CapsuleBaseRadius);
        _velocity = Vector3.zero;
        _energy = 0f;
        _sliding = false;
        _grounded = false;
        _jumpPressedPending = false;
        _jumpPressedTime = -999f;
        _lastLandTime = -999f;
        _gravitySuspended = false;
        _overrideTimer = 0f;
        _overrideVelocity = Vector3.zero;
    }

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        if (_input == null) _input = new LegacyPlayerInput();
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
        if (_input.JumpPressed)
        {
            _jumpPressedPending = true;
        }
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

        bool shiftHeld = _input.RunHeld;
        bool running = shiftHeld; // 策划案 Shift+W 奔跑；骨架不细分无 W 的 Shift
        Vector3 wishDir = ComputeWishDir();

        // 滑铲进入：速度达到约 0.8x 地速阈值时按下 Ctrl（策划案第 3 节加工版口径；
        // Ctrl 独立于 Shift——修掉"按住 Shift 松 W 即滑铲"的旧判断，框架 4.1）
        bool slideTrigger = _input.SlideTrigger;
        if (!_sliding && _grounded && slideTrigger &&
            HorizontalSpeed >= _params.GroundSpeedThreshold * _params.SlideSpeedRatio)
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

        ApplySpeedSteering(dt);

        // 闪避钟摆：短时速度覆盖（战斗命令），覆盖期间本 tick 常规移动结算结果作废
        if (_overrideTimer > 0f)
        {
            _overrideTimer -= dt;
            _velocity = _overrideVelocity;
        }

        // 加速结算后对水平速度施加上限（软上限：仿真结论”增速线性且无上界”）
        ClampHorizontalSpeed();

        ApplyGravity(dt);
        UpdateCapsuleBySpeed();
        _controller.Move(_velocity * dt);
        UpdateGroundState();

        // 矢量转换器：每 tick 能量 += max(0, 水平速度 - 地速阈值)（策划案第 4 节），累加后立即钳制
        _energy = Mathf.Min(_params.EnergyMax,
            _energy + Mathf.Max(0f, HorizontalSpeed - _params.GroundSpeedThreshold) * _params.EnergyPerTickPerExcessSpeed);
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
        float x = _input.Horizontal;
        float z = _input.Vertical;
        if (x == 0f && z == 0f) return Vector3.zero;
        float yaw = _cameraTransform != null ? _cameraTransform.eulerAngles.y : transform.eulerAngles.y;
        Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * new Vector3(x, 0f, z);
        return dir.sqrMagnitude > 1f ? dir.normalized : dir;
    }

    // 摩擦只在超速时生效：平跑收敛于地速阈值；免摩擦窗口内跳过结算——兔子跳的全部实现
    private void ApplyGroundFriction(float dt, bool running)
    {
        float speed = HorizontalSpeed;
        // 行走时以行走目标速度为门槛，实现“行走快速减速至固定较小值”（策划案第 1 节）
        float threshold = running ? _params.GroundSpeedThreshold : _params.WalkSpeed;
        if (speed <= threshold) return;
        if (InFrictionWindow) return;

        float newSpeed = Mathf.Max(0f, speed * (1f - _params.GroundFriction * dt));
        Vector3 horiz = HorizontalVelocity();
        _velocity.x = horiz.x / speed * newSpeed;
        _velocity.z = horiz.z / speed * newSpeed;
    }

    // Quake accelerate：addspeed = wishspeed - dot(v, wishdir)，为正则
    // v += wishdir * min(accel * wishspeed * dt, addspeed)
    // WindowPump 下免摩擦窗口内去掉投影上限（模型唯一偏差）；窗口外与 VerbatimQuake 完全一致
    private void ApplyGroundAcceleration(float dt, Vector3 wishDir, bool running)
    {
        if (wishDir == Vector3.zero) return;
        float wishspeed = running ? _params.GroundSpeedThreshold : _params.WalkSpeed;
        float step = _params.RunAccel * wishspeed * dt;
        if (_params.Pump == PumpMode.WindowPump && InFrictionWindow)
        {
            _velocity += wishDir * step; // 泵加速：窗口内摩擦既不作用，也不再受投影上限
            return;
        }
        float addspeed = wishspeed - Vector3.Dot(HorizontalVelocity(), wishDir);
        if (addspeed <= 0f) return;
        _velocity += wishDir * Mathf.Min(step, addspeed);
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

    // 策划案第 1 节：按住前进时保留速度大小，以受限角速度把速度转向摄像机朝向（空中同样生效）
    // 超过阈值后投影上限使加速无法改变方向，高速转向完全依赖本函数
    private void ApplySpeedSteering(float dt)
    {
        if (_sliding || _input.Vertical <= 0.1f) return;
        float speed = HorizontalSpeed;
        if (speed < _params.GroundSpeedThreshold * _params.SteerMinSpeedRatio) return;

        Vector3 forward = Vector3.forward;
        if (_cameraTransform != null)
        {
            Vector3 camForward = _cameraTransform.forward;
            camForward.y = 0f;
            if (camForward.sqrMagnitude > 0.0001f) forward = camForward.normalized;
        }

        Vector3 dir = HorizontalVelocity().normalized;
        Vector3 newDir = Vector3.RotateTowards(dir, forward, _params.SpeedSteerTurnRate * Mathf.Deg2Rad * dt, 0f);
        _velocity.x = newDir.x * speed;
        _velocity.z = newDir.z * speed;
    }

    // 水平速度软上限（仿真风险兜底）；只缩放水平分量，垂直不受影响
    private void ClampHorizontalSpeed()
    {
        Vector3 horiz = HorizontalVelocity();
        float speed = horiz.magnitude;
        if (speed <= _params.MaxSpeed || speed <= 0f) return;
        float scale = _params.MaxSpeed / speed;
        _velocity.x *= scale;
        _velocity.z *= scale;
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
            _jumpCount++;
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
        _jumpCount++;
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
                _jumpCount++;
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

        // 滑铲保持需要按住 Ctrl；头顶受阻（限高门内）时保持滑铲姿态
        if (speed < _params.SlideEndSpeed || !_input.CrouchHeld)
        {
            ExitSlideIfRoom();
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
        if (_gravitySuspended && !_grounded)
        {
            return; // 连斩滞空：竖直速度保持（悬停语义）；落地后恢复贴地下压
        }
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
        float t = Mathf.InverseLerp(_params.CapsuleShrinkStartSpeed, _params.CapsuleShrinkEndSpeed, HorizontalSpeed);
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
                // §4.7：高速普攻落地未派生 → 清空动量（清不清由战斗侧决定，Motor 只执行）
                if (LandingClient != null && LandingClient.ShouldClearMomentumOnLanding())
                {
                    _velocity.x = 0f;
                    _velocity.z = 0f;
                }
            }
        }
    }

    private Vector3 HorizontalVelocity() => new Vector3(_velocity.x, 0f, _velocity.z);

    // —— 战斗命令契约（战斗系统底层接口设计 §4.7；接口形状待 3C 评审）——
    // 战斗不直接动 Transform/速度字段，全部经以下方法请求；Motor 是速度与位移的唯一执行者

    /// <summary>沿方向位移请求（高速普攻前移/冲刺）：经 CharacterController 扫掠，返回实际位移（撞墙自动截断）</summary>
    public Vector3 RequestMove(Vector3 direction, float distance)
    {
        Vector3 before = transform.position;
        if (distance > 0f && direction.sqrMagnitude > 0.0001f)
        {
            _controller.Move(direction.normalized * distance);
        }
        return transform.position - before;
    }

    /// <summary>直接设置水平速度（连斩清零、传送台球式重定向）</summary>
    public void SetHorizontalSpeed(Vector3 horizontalVelocity)
    {
        _velocity.x = horizontalVelocity.x;
        _velocity.z = horizontalVelocity.z;
    }

    /// <summary>水平速度缩放（推斩击退后的自身减速）</summary>
    public void ScaleHorizontalSpeed(float factor)
    {
        _velocity.x *= factor;
        _velocity.z *= factor;
    }

    /// <summary>临时竖直速度覆盖（连斩滞空的起手/调整）</summary>
    public void SetVerticalSpeed(float verticalSpeed)
    {
        _velocity.y = verticalSpeed;
    }

    /// <summary>重力悬挂开关（连斩滞空期间保持竖直速度）</summary>
    public void SetGravitySuspended(bool suspended)
    {
        _gravitySuspended = suspended;
    }

    /// <summary>短时速度覆盖（闪避钟摆位移，参考 DMC 但丁拳套）</summary>
    public void OverrideVelocity(Vector3 velocity, float durationSec)
    {
        _overrideVelocity = velocity;
        _overrideTimer = Mathf.Max(0f, durationSec);
    }

    // IKnockbackReceiver：受击退（方向与量级由结算给出，敌人侧抗击退力折算归攻击方配）
    public void OnKnockback(Vector3 impulse)
    {
        _velocity += impulse;
    }

    // IEnergyAccount：一次性扣费；不足返回 false 且零副作用（框架 4.2"能量不足不扣费"）
    public bool TrySpend(float amount)
    {
        if (_energy < amount) return false;
        _energy -= amount;
        return true;
    }

    // 调试：速度向量（红）；落地免摩擦窗口内（黄）；滑铲中（青）
    private void OnDrawGizmos()
    {
        if (!Application.isPlaying || _params == null) return;
        Gizmos.color = Color.red;
        Gizmos.DrawLine(transform.position, transform.position + _velocity);

        if (InFrictionWindow)
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
