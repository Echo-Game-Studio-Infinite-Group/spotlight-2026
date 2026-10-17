using Cinemachine;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[DefaultExecutionOrder(-10)]
public sealed class CameraPostProcessing : MonoBehaviour
{
    // 玩家在场景里唯一，Start 里从 PlayerMotor.Active 取一次，不进 Inspector。
    [HideInInspector, SerializeField] private PlayerMotor _motor;
    // 必须留：FOV 与 Dutch 是每帧直接写进 vcam 的 LensSettings 的，
    // 效果挂在主相机上但作用对象是这台 vcam——两者缺一不可。
    [SerializeField] private CinemachineVirtualCamera _virtualCamera;
    [SerializeField] private Volume _volume;
    [SerializeField] private float _normalFov = 60f;
    [SerializeField] private float _fastFov = 85f;
    [SerializeField] private float _fullEffectSpeed = 25f;
    [SerializeField] private float _response = 5f;
    [SerializeField] private float _wallRoll = 20f;
    [Tooltip("贴墙滑行时镜头拉近到的距离；必须小于 vcam 上的基准距离，否则按基准距离处理")]
    [SerializeField, Min(0f)] private float _wallSlideDistance = 3.5f;
    [Tooltip("镜头拉近与弹回的响应速度，越大越跟手")]
    [SerializeField, Min(0.01f)] private float _wallSlideResponse = 8f;
    [SerializeField, Range(0f, 1f)] private float _chromaticIntensity = 0.5f;
    [SerializeField, Range(0f, 1f)] private float _motionBlurIntensity = 0.5f;
    [SerializeField] private float _customMotionBlur = 2.5f;
    [SerializeField] private float _customRadial;
    private ChromaticAberration _chromatic;
    private MotionBlur _motionBlur;
    private Cinemachine3rdPersonFollow _follow;
    private float _baseDistance;
    private float _distance;
    private float _intensity, _roll;
    public void Configure(PlayerMotor motor, CinemachineVirtualCamera camera, Volume volume)
    { _motor = motor; _virtualCamera = camera; _volume = volume; }
    private void Awake()
    {
        // 只取一次：vcam 是场景固定引用，跟随臂的基准距离也只在装载时读一遍。
        _follow = _virtualCamera.GetCinemachineComponent<Cinemachine3rdPersonFollow>();
        _baseDistance = _follow.CameraDistance;
        _distance = _baseDistance;
    }
    private void Start()
    {
        // PlayerMotor 的 Awake 晚于本组件（执行顺序 -10），只能在 Start 取这个全局单例。
        _motor = PlayerMotor.Active;
        // 预制体不能保存场景引用，单独拖入场景时自动接上全局 Volume。
        if (_volume == null)
            foreach (Volume candidate in FindObjectsOfType<Volume>())
                if (candidate.isGlobal) { _volume = candidate; break; }
        if (_volume == null || _volume.profile == null) return;
        _volume.profile.TryGet(out _chromatic);
        _volume.profile.TryGet(out _motionBlur);
        if (_volume.profile.TryGet(out ColorAdjustments color)) color.colorFilter.overrideState = false;
        if (_chromatic != null) _chromatic.intensity.overrideState = true;
        if (_motionBlur != null)
        { _motionBlur.intensity.overrideState = true; _motionBlur.mode.Override(MotionBlurMode.CameraOnly); }
    }
    private void Update()
    {
        if (_motor == null) return;
        float blend = 1f - Mathf.Exp(-_response * TimeManager.UnscaledDeltaTime);
        float target = Mathf.InverseLerp(_motor.Params.WalkSpeed, _fullEffectSpeed, _motor.HorizontalSpeed);
        _intensity = Mathf.Lerp(_intensity, target, blend);
        float side = Vector3.Dot(_motor.WallNormal, _virtualCamera.transform.right);
        _roll = Mathf.Lerp(_roll, _motor.IsWallSliding ? -Mathf.Sign(side) * _wallRoll : 0f, blend);
        LensSettings lens = _virtualCamera.m_Lens;
        lens.FieldOfView = Mathf.Lerp(_normalFov, _fastFov, _intensity);
        lens.Dutch = _roll;
        _virtualCamera.m_Lens = lens;
        // 贴墙时把镜头拉近，强化贴脸的压迫感；基准距离取自 vcam，免得和资产里的配置打架。
        // 用 Unscaled 是相机层的统一约定：时间缩放时镜头反馈不该跟着变慢。
        float pullIn = Mathf.Min(_wallSlideDistance, _baseDistance);
        _distance = Mathf.Lerp(_distance, _motor.IsWallSliding ? pullIn : _baseDistance,
            1f - Mathf.Exp(-_wallSlideResponse * TimeManager.UnscaledDeltaTime));
        _follow.CameraDistance = _distance;
        if (_chromatic != null) _chromatic.intensity.value = _intensity * _chromaticIntensity;
        if (_motionBlur != null) _motionBlur.intensity.value = _intensity * _motionBlurIntensity;
        RadialRedshiftFeature.SetSpeed(_intensity * _customMotionBlur, _intensity * _customRadial, 0f);
    }
    private void OnDisable()
    {
        RadialRedshiftFeature.SetSpeed(0f, 0f, 0f);
        // 跟随臂要还回基准距离，否则效果被关掉时会卡在贴脸状态。
        _distance = _baseDistance;
        _follow.CameraDistance = _baseDistance;
        if (_chromatic != null) _chromatic.intensity.value = 0f;
        if (_motionBlur != null) _motionBlur.intensity.value = 0f;
    }
}
