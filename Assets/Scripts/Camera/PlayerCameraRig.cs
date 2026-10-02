using Cinemachine;
using UnityEngine;

// 控制视角目标与角色朝向，真实相机的位置、阻尼和避障交给 Cinemachine。
[DefaultExecutionOrder(-20)]
public sealed class PlayerCameraRig : MonoBehaviour
{
    [SerializeField] private PlayerMotor _motor;
    [SerializeField] private PlayerInputReader _input;
    [SerializeField] private Transform _target;
    [SerializeField] private CinemachineVirtualCamera _virtualCamera;
    [SerializeField] private float _mouseSensitivity = 0.15f;
    [SerializeField] private Vector2 _pitchLimits = new Vector2(-25f, 70f);
    [SerializeField] private float _standHeight = 1.2f;
    [SerializeField] private float _slideHeight = 0.6f;
    [SerializeField] private float _heightResponse = 12f;
    [SerializeField] private bool _lockCursor = true;
    private float _yaw, _pitch;

    public void Configure(PlayerMotor motor, PlayerInputReader input, Transform target, CinemachineVirtualCamera camera)
    { _motor = motor; _input = input; _target = target; _virtualCamera = camera; }
    private void OnEnable()
    {
        CinemachineCore.CameraUpdatedEvent.AddListener(OnCameraUpdated);
        if (_input != null) _input.GameplayChanged += SetCursor;
        if (_motor != null) _motor.Teleported += OnTeleport;
        if (_target != null) { _yaw = _target.eulerAngles.y; _pitch = Mathf.DeltaAngle(0f, _target.eulerAngles.x); }
        SetCursor(_input != null && _input.GameplayEnabled);
    }
    private void OnDisable()
    {
        CinemachineCore.CameraUpdatedEvent.RemoveListener(OnCameraUpdated);
        if (_input != null) _input.GameplayChanged -= SetCursor;
        if (_motor != null) _motor.Teleported -= OnTeleport;
        if (_lockCursor) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
    }
    private void Update()
    {
        if (_target == null || _input == null || _motor == null) return;
        Vector2 look = _input.LookDelta;
        _yaw += look.x * _mouseSensitivity;
        _pitch = Mathf.Clamp(_pitch - look.y * _mouseSensitivity, _pitchLimits.x, _pitchLimits.y);
        _target.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        float height = _motor.IsSliding ? _slideHeight : _standHeight;
        _target.localPosition = Vector3.Lerp(_target.localPosition, Vector3.up * height,
            1f - Mathf.Exp(-_heightResponse * TimeManager.UnscaledDeltaTime));
    }
    private void SetCursor(bool gameplay)
    {
        if (!_lockCursor) return;
        Cursor.lockState = gameplay ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !gameplay;
    }
    private void OnCameraUpdated(CinemachineBrain brain)
    {
        if (_motor == null || _target == null || _virtualCamera == null ||
            brain.OutputCamera == null || !brain.IsLive(_virtualCamera)) return;

        Vector3 forward = Vector3.ProjectOnPlane(brain.OutputCamera.transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f) return;

        // 使用本帧最终视角；俯仰和划墙倾斜不传给角色，速度仍由运动模块独立结算。
        // 两个相机节点都在 character 下，必须保留世界姿态，避免转身再次带动相机。
        Vector3 targetPosition = _target.position;
        Quaternion targetRotation = _target.rotation;
        Transform cameraTransform = _virtualCamera.transform;
        Vector3 cameraPosition = cameraTransform.position;
        Quaternion cameraRotation = cameraTransform.rotation;
        _motor.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        _target.SetPositionAndRotation(targetPosition, targetRotation);
        cameraTransform.SetPositionAndRotation(cameraPosition, cameraRotation);
    }
    private void OnTeleport(Vector3 delta)
    { if (_virtualCamera != null && _target != null) _virtualCamera.OnTargetObjectWarped(_target, delta); }
}
