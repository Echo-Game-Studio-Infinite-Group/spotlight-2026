using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// 相机业务：鼠标轨道跟随 + FOV 冲刺推进 + 速度感后期（红移色差 / 动态模糊）
//
// 两个来源合并而成：
//   · 跟随部分 —— 取自本项目原 GrayboxFollowCamera：鼠标 yaw/pitch 轨道 + 指数平滑跟随 + 冲刺后拉
//   · 特效部分 —— 移植自 Rollaball/Assets/Scripts/CameraController.cs：
//     奔跑事件源由 Rollaball 的 PlayerController.OnRunChanged 改为本仓库 CharacterMovement.SprintChanged
//
// 跟随必须始终在玩家身后朝向（轨道相机），固定世界偏移会让玩家一转身相机就"看起来没跟随"
//
// 特效分两条通路，互不替代：
//   1) URP Volume（RunVolume.asset）：官方 色差 + 动态模糊，参数被 URP 硬性钳制（clamp 上限 0.2）
//   2) RadialRedshiftFeature：自研全屏 Pass，直接采样运动向量贴图，强度无上限，另含径向拖影
public class CameraController : MonoBehaviour
{
    [Header("外部引用")]
    [Tooltip("要跟随的玩家对象，必须挂着 CharacterMovement")]
    public GameObject player;

    [Tooltip("承载速度感效果的 URP Volume（RunVolume.asset）")]
    public Volume volume;

    [Header("跟随（鼠标轨道 + 平滑）")]
    [Tooltip("相机到焦点的距离")]
    [SerializeField] private float _distance = 6f;

    [Tooltip("焦点相对玩家 pivot 的抬高量（对准胸口）")]
    [SerializeField] private float _focusHeight = 1.2f;

    [Tooltip("跟随平滑速度：越大越跟手，越小越拖沓")]
    [SerializeField] private float _followLerp = 12f;

    [SerializeField] private float _mouseSensitivity = 3f;

    [Tooltip("俯角下限：太小会贴地平线，容易看到穿帮")]
    [SerializeField] private float _pitchMin = 5f;

    [Tooltip("俯角上限：太大变俯视")]
    [SerializeField] private float _pitchMax = 60f;

    [Tooltip("运行时锁定鼠标指针（按 ESC 由 Unity 默认行为解锁）")]
    [SerializeField] private bool _lockCursor = true;

    [Header("FOV 冲刺推进")]
    public float normalFov = 60f;
    public float runFov = 85f;
    public float fovSpeed = 5f;

    [Tooltip("冲刺时相机额外后拉的距离，强化速度感")]
    [SerializeField] private float _distanceGain = 1.5f;

    [Header("奔跑效果强度（URP 自带，Inspector 可调）")]
    [Tooltip("URP 自带色差的强度上限，0~1")]
    [Range(0f, 1f)] public float runChromatic = 1f;

    [Tooltip("URP 自带动态模糊的强度上限，0~1")]
    [Range(0f, 1f)] public float runMotionBlur = 1f;

    [Tooltip("URP 自带动态模糊的拖影长度上限，URP 硬上限 0.2")]
    [Range(0f, 0.2f)] public float runBlurClamp = 0.2f;

    [Header("自定义速度模糊（不受 URP 上限约束）")]
    [Tooltip("自研运动模糊倍率：1 = 物理正确长度，2~4 = 明显夸张")]
    public float customMotionBlur = 2.5f;

    [Tooltip("径向速度线强度（0 = 关闭），0.01 起就比较明显")]
    public float customRadial = 0f;

    [Tooltip("自定义色差分离量（UV 空间），0.005 左右已很明显；0 = 关闭")]
    public float customChromatic = 0f;

    [Tooltip("效果淡入淡出的速度")]
    public float effectSpeed = 5f;

    private Camera _camera;
    private CharacterMovement _movement;
    private ChromaticAberration _chromatic;
    private MotionBlur _motionBlur;

    private float _yaw;
    private float _pitch;
    private Vector3 _focus;

    // 目标冲刺系数：疾跑为 1，否则为 0；实际强度统一在 LateUpdate 平滑过渡
    private float _targetSprint;
    private float _currentSprint;

    private void Start()
    {
        if (player == null)
        {
            Debug.LogError("[CameraController] player 未赋值，相机无法工作", this);
            enabled = false;
            return;
        }

        // 初始朝向沿用场景里摆好的机位：从 forward 反解 yaw/pitch，避免直接读 eulerAngles 遇到 162/270 的等价解
        Vector3 forward = transform.forward;
        _yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        _pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg, _pitchMin, _pitchMax);
        _focus = player.transform.position + Vector3.up * _focusHeight;

        if (Application.isPlaying && _lockCursor)
        {
            Cursor.lockState = CursorLockMode.Locked;
        }

        _camera = GetComponent<Camera>();
        if (_camera == null)
        {
            Debug.LogError("[CameraController] 当前物体上没有 Camera 组件", this);
            enabled = false;
            return;
        }

        // 兜底开启后处理：相机上的勾选框一旦漏勾，整条后处理链会被静默跳过
        UniversalAdditionalCameraData cameraData = _camera.GetUniversalAdditionalCameraData();
        if (cameraData != null && !cameraData.renderPostProcessing)
        {
            cameraData.renderPostProcessing = true;
            Debug.Log("[CameraController] 已自动开启相机的 Post Processing", this);
        }

        // 先订阅再校验 Volume：避免后续 return 把回调丢掉
        _movement = player.GetComponent<CharacterMovement>();
        if (_movement != null)
        {
            _movement.SprintChanged += OnSprintChanged;
            // 订阅瞬间可能已经处于疾跑中，补一次当前状态，防止状态不同步
            OnSprintChanged(_movement.IsSprinting);
        }
        else
        {
            Debug.LogError("[CameraController] player 上没有 CharacterMovement 组件", this);
        }

        if (volume == null || volume.profile == null)
        {
            Debug.LogError("[CameraController] volume 未赋值或 profile 为空，后期处理无法生效", this);
            return;
        }

        // 红色滤镜彻底关闭 Override：RunVolume 里残留的 colorFilter 会让整个画面发红
        if (volume.profile.TryGet(out ColorAdjustments colorAdjustments))
        {
            colorAdjustments.colorFilter.overrideState = false;
        }

        volume.profile.TryGet(out _chromatic);
        volume.profile.TryGet(out _motionBlur);

        if (_chromatic != null)
        {
            _chromatic.intensity.overrideState = true;
            _chromatic.intensity.value = 0f;
        }
        else
        {
            Debug.LogWarning("[CameraController] profile 中缺少 ChromaticAberration", this);
        }

        if (_motionBlur != null)
        {
            _motionBlur.mode.overrideState = true;
            _motionBlur.quality.overrideState = true;
            _motionBlur.intensity.overrideState = true;
            _motionBlur.clamp.overrideState = true;

            // CameraOnly 最稳：只要相机在动就有拖影；High 质量采样更多、拖影更连贯
            _motionBlur.mode.value = MotionBlurMode.CameraOnly;
            _motionBlur.quality.value = MotionBlurQuality.High;
            _motionBlur.clamp.value = runBlurClamp;
            _motionBlur.intensity.value = 0f;
        }
        else
        {
            Debug.LogWarning("[CameraController] profile 中缺少 MotionBlur", this);
        }
    }

    private void OnDestroy()
    {
        // 退订：玩家销毁后事件仍持有本对象引用会泄漏
        if (_movement != null)
        {
            _movement.SprintChanged -= OnSprintChanged;
        }
    }

    // 只记录目标状态，强度统一在 LateUpdate 里平滑过渡——避免状态切换时效果闪烁
    private void OnSprintChanged(bool sprinting)
    {
        _targetSprint = sprinting ? 1f : 0f;
    }

    private void LateUpdate()
    {
        // 跟随放最前面且不依赖任何后处理引用：任何一处配置缺失都不该让相机不再跟随
        FollowPlayer();

        if (_camera == null)
        {
            return;
        }

        // 冲刺系数渐入渐出。这里刻意直接给满，不按速度比例缩放：
        // 按比例缩放会把强度压得很低，导致特效几乎看不见
        _currentSprint = Mathf.Lerp(_currentSprint, _targetSprint, Time.deltaTime * effectSpeed);

        ApplySpeedEffects();
    }

    // 鼠标轨道：yaw 无限制自由转，pitch 夹在上下限之间防止翻到地平面以下
    private void FollowPlayer()
    {
        if (player == null)
        {
            return;
        }

        _yaw += Input.GetAxis("Mouse X") * _mouseSensitivity;
        _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * _mouseSensitivity, _pitchMin, _pitchMax);

        Vector3 targetFocus = player.transform.position + Vector3.up * _focusHeight;
        // 用 1-exp(-k*dt) 而不是裸 Lerp(k*dt)：帧率变化时平滑手感才一致
        _focus = Vector3.Lerp(_focus, targetFocus, 1f - Mathf.Exp(-_followLerp * Time.deltaTime));

        Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        float distance = _distance + _distanceGain * _currentSprint;
        transform.SetPositionAndRotation(_focus - rotation * Vector3.forward * distance, rotation);
    }

    private void ApplySpeedEffects()
    {
        float targetFov = Mathf.Lerp(normalFov, runFov, _currentSprint);
        _camera.fieldOfView = Mathf.Lerp(_camera.fieldOfView, targetFov, Time.deltaTime * fovSpeed);

        // URP 自带色差：RGB 分离
        if (_chromatic != null)
        {
            _chromatic.intensity.value = _currentSprint * runChromatic;
        }

        // URP 自带动态模糊：intensity 与 clamp 都被 URP 硬性限制，无法再调大
        if (_motionBlur != null)
        {
            _motionBlur.intensity.value = _currentSprint * runMotionBlur;
            _motionBlur.clamp.value = Mathf.Max(0.01f, _currentSprint * runBlurClamp);
        }

        // 自研速度模糊：不受 URP 的 0.2 限制，可按倍率任意放大
        RadialRedshiftFeature.SetSpeed(
            _currentSprint * customMotionBlur,
            _currentSprint * customRadial,
            _currentSprint * customChromatic);
    }
}
