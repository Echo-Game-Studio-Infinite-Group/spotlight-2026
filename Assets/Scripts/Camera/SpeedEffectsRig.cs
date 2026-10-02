using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// 速度感后期：把玩家水平速度写进 URP Volume（色差 / 动态模糊）与自研 RadialRedshiftFeature（无上限运动模糊 / 径向拖影 / 色差）
// 与机位彻底解耦——不碰 transform、不写 FOV，所以能与 CinemachineBrain 共存；这是它取代 CameraController 的原因
// 特效分两条通路，互不替代：URP 自带项受 clamp(0.2) 硬限，自研项直接采样运动向量、强度无上限
public class SpeedEffectsRig : MonoBehaviour
{
    [Header("速度源")]
    [Tooltip("玩家身份组件；速度真值在它持有的 PlayerMotor 上")]
    [SerializeField] private Player _player;

    [Header("速度归一化")]
    [Tooltip("低于该速度不产生任何速度感")]
    [SerializeField] private float _minSpeed = 10f;

    [Tooltip("达到该速度视为满强度")]
    [SerializeField] private float _fullSpeed = 40f;

    [Tooltip("强度渐入渐出锐度（每秒指数逼近）")]
    [SerializeField] private float _sharpness = 5f;

    [Header("URP Volume（承载 RunVolume 的 Global Volume）")]
    [SerializeField] private Volume _volume;

    [Tooltip("URP 自带色差强度上限，0~1")]
    [Range(0f, 1f)] [SerializeField] private float _chromatic = 1f;

    [Tooltip("URP 自带动态模糊强度上限，0~1")]
    [Range(0f, 1f)] [SerializeField] private float _motionBlur = 1f;

    [Tooltip("URP 自带动态模糊拖影长度上限；URP 硬上限 0.2，调不上去")]
    [Range(0f, 0.2f)] [SerializeField] private float _blurClamp = 0.2f;

    [Header("自研 RadialRedshift（不受 URP clamp 约束）")]
    [Tooltip("运动模糊倍率：1 = 物理正确长度，2~4 = 明显夸张")]
    [SerializeField] private float _customMotionBlur = 2.5f;

    [Tooltip("径向速度线强度（0 = 关闭）；源码内部再 clamp 到 0.1")]
    [SerializeField] private float _customRadial = 0.01f;

    [Tooltip("自定义色差分离量（UV 空间），0.005 左右已很明显；0 = 关闭")]
    [SerializeField] private float _customChromatic = 0f;

    private ChromaticAberration _chromaticFx;
    private MotionBlur _motionBlurFx;
    private float _strength;

    private void Start()
    {
        if (_player == null)
        {
            Debug.LogError("[SpeedEffectsRig] _player 未赋值，速度感后期不会生效", this);
            enabled = false;
            return;
        }

        // 兜底开启后处理：相机上的勾选框一旦漏勾，整条 Volume 链会被静默跳过
        Camera cam = GetComponent<Camera>();
        if (cam != null)
        {
            UniversalAdditionalCameraData cameraData = cam.GetUniversalAdditionalCameraData();
            if (cameraData != null && !cameraData.renderPostProcessing)
            {
                cameraData.renderPostProcessing = true;
                Debug.Log("[SpeedEffectsRig] 已自动开启相机的 Post Processing", this);
            }
        }

        if (_volume == null || _volume.profile == null)
        {
            Debug.LogError("[SpeedEffectsRig] _volume 未赋值或 profile 为空，Volume 侧效果不会生效", this);
            return;
        }

        // 红色滤镜彻底关闭 Override：RunVolume 里残留的 colorFilter 会让整个画面发红
        if (_volume.profile.TryGet(out ColorAdjustments colorAdjustments))
        {
            colorAdjustments.colorFilter.overrideState = false;
        }

        _volume.profile.TryGet(out _chromaticFx);
        _volume.profile.TryGet(out _motionBlurFx);

        if (_chromaticFx != null)
        {
            _chromaticFx.intensity.overrideState = true;
            _chromaticFx.intensity.value = 0f;
        }
        else
        {
            Debug.LogWarning("[SpeedEffectsRig] profile 中缺少 ChromaticAberration", this);
        }

        if (_motionBlurFx != null)
        {
            _motionBlurFx.mode.overrideState = true;
            _motionBlurFx.quality.overrideState = true;
            _motionBlurFx.intensity.overrideState = true;
            _motionBlurFx.clamp.overrideState = true;

            // CameraOnly 最稳：只要相机在动就有拖影；High 质量采样更多、拖影更连贯
            _motionBlurFx.mode.value = MotionBlurMode.CameraOnly;
            _motionBlurFx.quality.value = MotionBlurQuality.High;
            _motionBlurFx.clamp.value = _blurClamp;
            _motionBlurFx.intensity.value = 0f;
        }
        else
        {
            Debug.LogWarning("[SpeedEffectsRig] profile 中缺少 MotionBlur", this);
        }
    }

    private void LateUpdate()
    {
        float target = Mathf.Clamp01(Mathf.InverseLerp(_minSpeed, _fullSpeed, _player.Speed));
        _strength = Mathf.Lerp(_strength, target, 1f - Mathf.Exp(-_sharpness * Time.deltaTime));

        ApplyVolume();
        ApplyRadialRedshift();
    }

    private void OnDisable()
    {
        // 复位：禁用或切场景后不该留下拖影
        RadialRedshiftFeature.SetSpeed(0f, 0f, 0f);
    }

    private void ApplyVolume()
    {
        if (_chromaticFx != null)
        {
            _chromaticFx.intensity.value = _strength * _chromatic;
        }

        if (_motionBlurFx != null)
        {
            _motionBlurFx.intensity.value = _strength * _motionBlur;
            _motionBlurFx.clamp.value = Mathf.Max(0.01f, _strength * _blurClamp);
        }
    }

    private void ApplyRadialRedshift()
    {
        RadialRedshiftFeature.SetSpeed(
            _strength * _customMotionBlur,
            _strength * _customRadial,
            _strength * _customChromatic);
    }
}
