using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// 相机的「后处理」效果层（挂在 Main Camera 上）：只读写全局 Volume 与 RadialRedshift 材质，
// 不再碰 vcam 的 Lens / 跟随臂——镜头本体的震屏、滚转、缩放归 VCameraEffects（挂在 vcam 上）。
// 为什么这么分：vcam 的 LensSettings 每帧都要写，写在 vcam 自己的组件上才不会两处互相覆盖；
// 而 Volume 与后处理管线挂在渲染相机上，留在这一层。
[DefaultExecutionOrder(-10)]
public sealed class CameraPostProcessing : MonoBehaviour
{
    [SerializeField] private Volume _volume;
    [SerializeField, Range(0f, 1f)] private float _chromaticIntensity = 0.5f;
    [Tooltip("URP 相机运动模糊的满速强度；0 为关闭。")]
    [SerializeField, Range(0f, 1f)] private float _motionBlurIntensity = 0f;
    [Tooltip("运动向量拖影的满速强度；0 为关闭，与 URP 运动模糊独立。")]
    [SerializeField, Min(0f)] private float _customMotionBlur = 0f;
    [SerializeField] private float _customRadial;
    [SerializeField, Min(0.01f)] private float _response = 5f;
    [Tooltip("速度感开始生效的阈值（m/s）")]
    [SerializeField, Min(0f)] private float _effectStartSpeed = 3f;
    [Tooltip("速度感拉满的速度（m/s）")]
    [SerializeField, Min(0.01f)] private float _fullEffectSpeed = 25f;

    [Header("慢速反馈（全局时间被压慢时）")]
    // 只认 GlobalRate：parry 时缓、主动时停、招式自带的时间效果都挂在 global 层，
    // 而命中顿帧压的是 world + player 层。每一刀都闪一层暗角会糊得没法看，
    // 所以「慢速特效」只跟真正的全局慢速走。
    [Tooltip("全局时间压到最慢时的饱和度（负值去饱和）")]
    [SerializeField] private float _slowSaturation = -55f;
    [Tooltip("全局时间压到最慢时叠加的暗角强度")]
    [SerializeField, Min(0f)] private float _slowVignette = 0.45f;
    [Tooltip("全局时间压到最慢时的径向拖影（RadialRedshift._Strength）")]
    [SerializeField, Min(0f)] private float _slowRadial = 0.035f;
    [Tooltip("全局时间压到最慢时的边缘色差（RadialRedshift._ChromaticSpread）")]
    [SerializeField, Min(0f)] private float _slowChromatic = 0.012f;
    [Tooltip("进入慢速的响应速度：越大越干脆")]
    [SerializeField, Min(0.01f)] private float _slowAttackResponse = 20f;
    [Tooltip("退出慢速的响应速度：越小尾巴拖得越久")]
    [SerializeField, Min(0.01f)] private float _slowReleaseResponse = 6f;

    // 玩家在场景里唯一，Start 里从 PlayerMotor.Active 取一次，不进 Inspector。
    [HideInInspector, SerializeField] private PlayerMotor _motor;
    private ChromaticAberration _chromatic;
    private MotionBlur _motionBlur;
    private ColorAdjustments _color;
    private Vignette _vignette;
    // 慢速反馈要改的是「在原有基础上叠加」，所以得记住资产里的基准值，退出时还回去。
    private float _saturationBase, _vignetteBase;
    private float _slowWeight, _intensity;

    /// <summary>当前慢速反馈强度：0 = 常速，1 = 完全压停。调试与测试读它。</summary>
    public float SlowWeight => _slowWeight;

    /// <summary>慢速权重随全局时间倍率反向变化：1x → 0，0.1x → 0.9。</summary>
    public static float SlowWeightFor(float globalRate) => 1f - Mathf.Clamp01(globalRate);

    /// <summary>写进 Volume 的暗角强度（没接上 Volume 时为 -1），自检用。</summary>
    public float CurrentVignette => _vignette != null ? _vignette.intensity.value : -1f;

    public void Configure(PlayerMotor motor, Volume volume)
    { _motor = motor; _volume = volume; }

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
        // 慢速反馈要用饱和度与暗角：全局 Volume 里没这两项就补一份运行时的（只在本次播放内生效）。
        // 不补的话表现是「时间明明慢下来了，画面一点没变」——这种沉默失败最难查。
        if (_volume.profile.TryGet(out _color)) _color.colorFilter.overrideState = false;
        else _color = _volume.profile.Add<ColorAdjustments>();
        if (!_volume.profile.TryGet(out _vignette)) _vignette = _volume.profile.Add<Vignette>();
        if (_chromatic != null) _chromatic.intensity.overrideState = true;
        if (_motionBlur != null)
        { _motionBlur.intensity.overrideState = true; _motionBlur.mode.Override(MotionBlurMode.CameraOnly); }
        if (_color != null) { _saturationBase = _color.saturation.value; _color.saturation.overrideState = true; }
        if (_vignette != null) { _vignetteBase = _vignette.intensity.value; _vignette.intensity.overrideState = true; }
    }

    private void Update()
    {
        float dt = TimeManager.UnscaledDeltaTime;
        UpdateSlowMotionFeedback(dt);
        if (_motor != null) UpdateSpeedWeight(dt);
        // 速度线与慢速拖影共用同一组材质参数，必须合成一次写入，
        // 否则后写的一方会把另一方清掉（表现为「跑起来时慢速特效消失」）。
        RadialRedshiftFeature.SetSpeed(_intensity * _customMotionBlur, _intensity * _customRadial + _slowWeight * _slowRadial,
            _slowWeight * _slowChromatic);
    }

    // 速度反馈：只做后处理那部分（色差 / 拖影）；FOV 与镜头位置归 VCameraEffects。
    private void UpdateSpeedWeight(float dt)
    {
        float target = Mathf.InverseLerp(_effectStartSpeed, _fullEffectSpeed, _motor.HorizontalSpeed);
        _intensity = Mathf.Lerp(_intensity, target, 1f - Mathf.Exp(-_response * dt));
        if (_chromatic != null) _chromatic.intensity.value = _intensity * _chromaticIntensity;
        if (_motionBlur != null) _motionBlur.intensity.value = _intensity * _motionBlurIntensity;
    }

    // 慢速反馈：去饱和 + 暗角 + 径向拖影 + 边缘色差，做「时间变稠」的读感。
    // 缓动走 Unscaled 时间——用缩放后的 dt 会让效果在慢动作里自己越走越慢，永远到不了位。
    private void UpdateSlowMotionFeedback(float dt)
    {
        if (dt <= 0f) return;
        float target = SlowWeightFor(TimeManager.GlobalRate);
        // 进入干脆、退出拖尾：两者用不同响应，慢动作收尾才不会糊成一团。
        float response = target > _slowWeight ? _slowAttackResponse : _slowReleaseResponse;
        _slowWeight = Mathf.Lerp(_slowWeight, target, 1f - Mathf.Exp(-response * dt));
        if (_color != null) _color.saturation.value = Mathf.Lerp(_saturationBase, _slowSaturation, _slowWeight);
        if (_vignette != null) _vignette.intensity.value = _vignetteBase + _slowVignette * _slowWeight;
    }

    /// <summary>自检与测试入口：按给定步长推进一帧慢速反馈，不依赖帧循环。</summary>
    public void SimulateSlowFeedback(float dt) => UpdateSlowMotionFeedback(dt);

    private void OnDisable()
    {
        RadialRedshiftFeature.SetSpeed(0f, 0f, 0f);
        // 慢速反馈把 Volume 的值改过，退出时还回基准值，否则热重载后画面停在暗角状态。
        _slowWeight = 0f;
        if (_color != null) _color.saturation.value = _saturationBase;
        if (_vignette != null) _vignette.intensity.value = _vignetteBase;
        if (_chromatic != null) _chromatic.intensity.value = 0f;
        if (_motionBlur != null) _motionBlur.intensity.value = 0f;
    }
}
