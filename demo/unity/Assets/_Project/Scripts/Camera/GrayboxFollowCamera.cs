using UnityEngine;

// 灰盒跟随相机：位置平滑跟随 + 鼠标 yaw/pitch；PlayerMotor 的转向读的就是这台相机的 yaw
// 正式相机（Cinemachine）P1 接入后整体替换，本脚本不承载任何玩法逻辑
public class GrayboxFollowCamera : MonoBehaviour
{
    [SerializeField] private Transform _target;
    [SerializeField] private float _distance = 6f;
    [SerializeField] private float _focusHeight = 1.2f;
    [SerializeField] private float _followLerp = 12f;
    [SerializeField] private float _mouseSensitivity = 3f;
    [SerializeField] private float _pitchMin = 5f;
    [SerializeField] private float _pitchMax = 60f;

    private float _yaw;
    private float _pitch = 18f;
    private Vector3 _focus;

    private void Start()
    {
        if (_target == null)
        {
            Debug.LogError("[GrayboxFollowCamera] 未指定跟随目标", this);
            enabled = false;
            return;
        }
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

        Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
        transform.SetPositionAndRotation(_focus - rot * Vector3.forward * _distance, rot);
    }
}
