using UnityEngine;

// 灰盒跟随相机：位置平滑跟随 + 鼠标 yaw/pitch + 速度反馈（FOV 拉伸与距离后拉）
// 正式相机（Cinemachine）P1 接入后整体替换，本脚本不承载任何玩法逻辑
// PlayerMotor 的转向读的就是这台相机的 yaw
public class GrayboxFollowCamera : MonoBehaviour
{
    [SerializeField] private Transform _target;
    [SerializeField] private float _distance = 6f;
    [SerializeField] private float _focusHeight = 1.2f;
    [SerializeField] private float _followLerp = 12f;
    [SerializeField] private float _mouseSensitivity = 3f;
    [SerializeField] private float _pitchMin = 5f;
    [SerializeField] private float _pitchMax = 60f;

    [Header("速度反馈（速度感的最低配置：FOV 拉伸 + 相机后拉）")]
    [SerializeField] private float _baseFov = 60f;
    [SerializeField] private float _maxFov = 85f;
    [Tooltip("水平速度达到该倍数（×地速阈值）时 FOV/距离反馈拉满")]
    [SerializeField] private float _fullSpeedRatio = 3f;
    [Tooltip("满速时相机额外后拉的距离")]
    [SerializeField] private float _distanceGain = 1.5f;
    [SerializeField] private float _feedbackLerp = 6f;

    private float _yaw;
    private float _pitch = 18f;
    private Vector3 _focus;
    private PlayerMotor _motor;
    private float _speedT; // 平滑后的速度档位 0..1
    private Camera _camera;

    private void Start()
    {
        _camera = GetComponent<Camera>();
        if (_target == null)
        {
            Debug.LogError("[GrayboxFollowCamera] 未指定跟随目标", this);
            enabled = false;
            return;
        }
        _motor = _target.GetComponent<PlayerMotor>();
        _focus = _target.position + Vector3.up * _focusHeight;
        // 运行期锁定鼠标（ESC 由 Unity 默认行为解锁）；编辑器非运行态不锁定
        if (Application.isPlaying) Cursor.lockState = CursorLockMode.Locked;
    }

    private void LateUpdate()
    {
        _yaw += Input.GetAxis("Mouse X") * _mouseSensitivity;
        _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * _mouseSensitivity, _pitchMin, _pitchMax);

        Vector3 targetFocus = _target.position + Vector3.up * _focusHeight;
        _focus = Vector3.Lerp(_focus, targetFocus, 1f - Mathf.Exp(-_followLerp * Time.deltaTime));

        float ratio = 0f;
        if (_motor != null && _motor.Params != null && _motor.Params.GroundSpeedThreshold > 0f)
        {
            ratio = Mathf.Clamp01(_motor.HorizontalSpeed / (_motor.Params.GroundSpeedThreshold * _fullSpeedRatio));
        }
        _speedT = Mathf.Lerp(_speedT, ratio, 1f - Mathf.Exp(-_feedbackLerp * Time.deltaTime));

        if (_camera != null) _camera.fieldOfView = Mathf.Lerp(_baseFov, _maxFov, _speedT);

        Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
        float distance = _distance + _distanceGain * _speedT;
        transform.SetPositionAndRotation(_focus - rot * Vector3.forward * distance, rot);
    }
}
