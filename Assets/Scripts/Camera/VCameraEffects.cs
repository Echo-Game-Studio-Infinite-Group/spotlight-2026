using Cinemachine;
using UnityEngine;

// 玩家相机的「镜头本体」效果层，挂 vcam 物体上，只维护三样东西：震屏、滚转、缩放。
//   · Shaker —— 包装 CinemachineImpulse；「伤害 → 幅度」的换算只有这一份
//   · Rotate —— Dutch 滚转：贴墙滑行压镜头，parry 推镜再叠一点倾斜
//   · Zoom   —— FOV + 跟随臂距离：速度感拉远、贴墙拉近、parry 推近
//
// 为什么不改 Main Camera：CinemachineBrain 每帧把 vcam 状态写进真实相机，
// 直接写 Camera.main 的 fov / transform 会被同帧覆盖，还会和 Brain 的阻尼互相打架。
// 也不写 Transform：位置与朝向归 vcam + Brain 所有，写了同样会被覆盖。
// 时间一律用 UnscaledTime：时缓 / 时停下镜头反馈不该跟着变慢（Brain 本身也是 IgnoreTimeScale）。
[DefaultExecutionOrder(-10)]
[DisallowMultipleComponent]
public sealed class VCameraEffects : MonoBehaviour
{
    // 和发射端的 Channel 要保持一致
    public const int ImpulseChannel = 1;

    [Header("Shaker（震屏）")]
    [Tooltip("基准伤害")]
    [SerializeField, Min(0.001f)] private float _refDmg = 20f;
    [Tooltip("基准伤害对应的抖动幅度")]
    [SerializeField, Min(0f)] private float _refAmp = 0.5f;

    [Header("Zoom（缩放：FOV + 跟随臂距离）")]
    [Tooltip("常速时的 FOV。会每帧写回 vcam 的 LensSettings")]
    [SerializeField] private float _normalFov = 60f;
    [Tooltip("满速时的 FOV，越大越接近广角冲刺")]
    [SerializeField] private float _fastFov = 85f;
    [Tooltip("速度感开始生效的阈值（m/s）")]
    [SerializeField, Min(0f)] private float _effectStartSpeed = 3f;
    [Tooltip("速度感拉满的速度（m/s）")]
    [SerializeField, Min(0.01f)] private float _fullEffectSpeed = 25f;
    [Tooltip("速度 → 缩放的响应速度，越大越跟手")]
    [SerializeField, Min(0.01f)] private float _zoomResponse = 5f;
    [Tooltip("贴墙滑行时跟随臂拉近到的距离；必须小于基准距离，否则按基准距离处理")]
    [SerializeField, Min(0f)] private float _wallSlideDistance = 3.5f;
    [Tooltip("跟随臂距离变化的响应速度")]
    [SerializeField, Min(0.01f)] private float _wallSlideResponse = 8f;

    [Header("Parry 推镜")]
    [Tooltip("parry 成功时 FOV 收窄的度数（收窄＝推近）")]
    [SerializeField, Min(0f)] private float _parryFovPunch = 6f;
    [Tooltip("parry 推镜同时把跟随臂拉近到基准距离的这个比例。1 = 不动")]
    [SerializeField, Range(0.5f, 1f)] private float _parryDistanceScale = 0.88f;
    [Tooltip("parry 推镜叠加的滚转角度，0 = 不滚")]
    [SerializeField] private float _parryRoll = 2f;
    [Tooltip("推镜保持满幅的时长；之后按释放响应回位")]
    [SerializeField, Min(0f)] private float _parryHoldSeconds = 0.4f;
    [Tooltip("推近的进入响应：越大越干脆（要压过时缓开头的顿感）")]
    [SerializeField, Min(0.01f)] private float _parryAttackResponse = 18f;
    [Tooltip("推镜的回位响应：越小回得越慢，越像一次运镜而不是弹簧")]
    [SerializeField, Min(0.01f)] private float _parryReleaseResponse = 5f;

    [Header("Rotate（滚转）")]
    [Tooltip("贴墙滑行时的最大滚转角")]
    [SerializeField] private float _wallRoll = 20f;
    [Tooltip("滚转的响应速度")]
    [SerializeField, Min(0.01f)] private float _rollResponse = 5f;

    // 归属规则：本组件一定挂在 vcam 物体上，运行时自取即可（装配工具也会写一遍便于排障）。
    [HideInInspector, SerializeField] private CinemachineVirtualCamera _virtualCamera;

    private static VCameraEffects _instance;
    private Cinemachine3rdPersonFollow _follow;
    private PlayerMotor _motor;
    private float _baseDistance;
    private float _speedWeight, _roll, _parryZoom;
    private float _fov, _distance;
    private float _parryHoldRemaining;

    public static VCameraEffects Instance => _instance;
    /// <summary>当前 parry 推镜强度：0 = 常速取景，1 = 完全推近。</summary>
    public float ParryZoomWeight => _parryZoom;
    /// <summary>这一帧写进 vcam 的 FOV（没接上 vcam 时也照算，供测试与诊断读）。</summary>
    public float CurrentFieldOfView => _fov;
    public float CurrentDistance => _distance;
    public float CurrentRoll => _roll;

    // ===== Shaker =====
    public static float Normalize(float dmg)
    {
        if (_instance == null) return 0f;
        return Mathf.Max(0f, dmg) / _instance._refDmg * _instance._refAmp;
    }

    // 震屏：包装 CM 官方震屏，全局调用 VCameraEffects.Emit() 即可
    public static void Emit(CinemachineImpulseSource source, Vector3 direction, float dmg)
    {
        if (source == null) return;
        float amplitude = Normalize(dmg);
        if (amplitude <= 0f) return;
        // 防止浮点数精度问题，故跟 0.0001f 比较代替 != 0
        source.m_DefaultVelocity = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.back;
        source.GenerateImpulseWithForce(amplitude);
    }

    // ===== Zoom（parry 运镜）=====
    // parry 成功的推镜入口：推近 → 保持 _parryHoldSeconds → 自动回位。
    // 走静态入口是为了让战斗侧（PlayerCombat）不必持有相机引用。
    public static void PlayParryZoom()
    {
        if (_instance == null) return;
        _instance.BeginParryZoom();
    }

    public void BeginParryZoom() => _parryHoldRemaining = Mathf.Max(_parryHoldRemaining, _parryHoldSeconds);

    // ===== 帧推进 =====
    private void Awake()
    {
        _instance = this;
        if (_virtualCamera == null) _virtualCamera = GetComponent<CinemachineVirtualCamera>();
        // CM 2 的跟随组件位于隐藏管线子节点，同物体查询会让距离反馈失效。
        _follow = _virtualCamera.GetCinemachineComponent<Cinemachine3rdPersonFollow>();
        if (_follow != null) _baseDistance = Mathf.Max(0.01f, _follow.CameraDistance);
        _fov = _normalFov;
        _distance = _baseDistance;
        EnsureListener(_virtualCamera);
    }

    private void Start()
    {
        // PlayerMotor 的 Awake 晚于本组件（执行顺序 -10），只能在 Start 取这个全局单例。
        _motor = PlayerMotor.Active;
    }

    private void Update() => SimulateCameraEffects(TimeManager.UnscaledDeltaTime);

    /// <summary>推进一帧镜头效果。dt 由调用方给（运行时用 Unscaled，测试可注入固定步长）。</summary>
    public void SimulateCameraEffects(float dt)
    {
        if (dt <= 0f) return;
        UpdateParryZoom(dt);
        UpdateSpeedWeight(dt);
        UpdateZoom(dt);
        UpdateRoll(dt);
        ApplyLens();
    }

    // 推镜包络：进入快、保持住、退出慢。恒定的深倍率读起来像弹簧，不像运镜。
    private void UpdateParryZoom(float dt)
    {
        bool holding = _parryHoldRemaining > 0f;
        if (holding) _parryHoldRemaining -= dt;
        float target = holding ? 1f : 0f;
        float response = target > _parryZoom ? _parryAttackResponse : _parryReleaseResponse;
        _parryZoom = Mathf.Lerp(_parryZoom, target, 1f - Mathf.Exp(-response * dt));
        // 别把 1e-5 的残值留到下一帧：FOV 会永远差一点点，看起来像镜头没回位。
        if (_parryZoom < 0.001f) _parryZoom = 0f;
    }

    private void UpdateSpeedWeight(float dt)
    {
        float target = _motor != null
            ? Mathf.InverseLerp(_effectStartSpeed, _fullEffectSpeed, _motor.HorizontalSpeed)
            : 0f;
        _speedWeight = Mathf.Lerp(_speedWeight, target, 1f - Mathf.Exp(-_zoomResponse * dt));
    }

    private void UpdateZoom(float dt)
    {
        // FOV：速度感拉广角，parry 收窄推近，两者直接相加（一次运镜里速度通常接近 0）
        _fov = Mathf.Lerp(_normalFov, _fastFov, _speedWeight) - _parryFovPunch * _parryZoom;
        if (_follow == null) return;
        // 贴墙拉近与 parry 推近都往「更近」走，取更近的那个，避免两个效果互相覆盖。
        float baseTarget = _motor != null && _motor.IsWallSliding
            ? Mathf.Min(_wallSlideDistance, _baseDistance) : _baseDistance;
        float parryTarget = _baseDistance * Mathf.Lerp(1f, _parryDistanceScale, _parryZoom);
        float target = Mathf.Min(baseTarget, parryTarget);
        _distance = Mathf.Lerp(_distance, target, 1f - Mathf.Exp(-_wallSlideResponse * dt));
        _follow.CameraDistance = _distance;
    }

    private void UpdateRoll(float dt)
    {
        float wallTarget = 0f;
        if (_motor != null && _motor.IsWallSliding && _virtualCamera != null)
        {
            // 往哪侧贴墙就往反方向压镜头，压多少由墙面法线和相机右向的点积决定
            float side = Vector3.Dot(_motor.WallNormal, _virtualCamera.transform.right);
            wallTarget = -Mathf.Sign(side) * _wallRoll;
        }
        float target = wallTarget + _parryRoll * _parryZoom;
        _roll = Mathf.Lerp(_roll, target, 1f - Mathf.Exp(-_rollResponse * dt));
    }

    // FOV 与 Dutch 是同一个 LensSettings：必须一次写入，分两处写会互相覆盖。
    private void ApplyLens()
    {
        if (_virtualCamera == null) return;
        LensSettings lens = _virtualCamera.m_Lens;
        lens.FieldOfView = _fov;
        lens.Dutch = _roll;
        _virtualCamera.m_Lens = lens;
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    // 给 Editor 自动装配用的
    public static CinemachineImpulseListener EnsureListener(CinemachineVirtualCamera vcam)
    {
        if (vcam == null) return null;
        CinemachineImpulseListener listener = vcam.GetComponent<CinemachineImpulseListener>();
        if (listener == null) listener = vcam.gameObject.AddComponent<CinemachineImpulseListener>();
        // 默认值只在 Inspector 手动添加时由 Reset() 写入，代码添加必须显式补齐。
        listener.m_ApplyAfter = CinemachineCore.Stage.Noise;
        listener.m_ChannelMask = ImpulseChannel;
        listener.m_Gain = 1f;
        listener.m_Use2DDistance = false;
        // 发射端写的是世界方向；开相机空间会把方向当成镜头局部向量，各方向的攻击会塌缩成同一个晃动。
        listener.m_UseCameraSpace = false;
        return listener;
    }
}
