using Cinemachine;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[DefaultExecutionOrder(-10)]
public sealed class SpeedCameraFeedback : MonoBehaviour
{
    [SerializeField] private PlayerMotor _motor;
    [SerializeField] private CinemachineVirtualCamera _virtualCamera;
    [SerializeField] private Volume _volume;
    [SerializeField] private float _normalFov = 60f;
    [SerializeField] private float _fastFov = 85f;
    [SerializeField] private float _fullEffectSpeed = 25f;
    [SerializeField] private float _response = 5f;
    [SerializeField] private float _wallRoll = 5f;
    [SerializeField, Range(0f, 1f)] private float _chromaticIntensity = 0.5f;
    [SerializeField, Range(0f, 1f)] private float _motionBlurIntensity = 0.5f;
    [SerializeField] private float _customMotionBlur = 2.5f;
    [SerializeField] private float _customRadial;
    private ChromaticAberration _chromatic;
    private MotionBlur _motionBlur;
    private float _intensity, _roll;
    public void Configure(PlayerMotor motor, CinemachineVirtualCamera camera, Volume volume)
    { _motor = motor; _virtualCamera = camera; _volume = volume; }
    private void Start()
    {
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
        if (_motor == null || _virtualCamera == null) return;
        float blend = 1f - Mathf.Exp(-_response * TimeManager.UnscaledDeltaTime);
        float target = Mathf.InverseLerp(_motor.Params.WalkSpeed, _fullEffectSpeed, _motor.HorizontalSpeed);
        _intensity = Mathf.Lerp(_intensity, target, blend);
        float side = Vector3.Dot(_motor.WallNormal, _virtualCamera.transform.right);
        _roll = Mathf.Lerp(_roll, _motor.IsWallSliding ? -Mathf.Sign(side) * _wallRoll : 0f, blend);
        LensSettings lens = _virtualCamera.m_Lens;
        lens.FieldOfView = Mathf.Lerp(_normalFov, _fastFov, _intensity);
        lens.Dutch = _roll;
        _virtualCamera.m_Lens = lens;
        if (_chromatic != null) _chromatic.intensity.value = _intensity * _chromaticIntensity;
        if (_motionBlur != null) _motionBlur.intensity.value = _intensity * _motionBlurIntensity;
        RadialRedshiftFeature.SetSpeed(_intensity * _customMotionBlur, _intensity * _customRadial, 0f);
    }
    private void OnDisable()
    {
        RadialRedshiftFeature.SetSpeed(0f, 0f, 0f);
        if (_chromatic != null) _chromatic.intensity.value = 0f;
        if (_motionBlur != null) _motionBlur.intensity.value = 0f;
    }
}
