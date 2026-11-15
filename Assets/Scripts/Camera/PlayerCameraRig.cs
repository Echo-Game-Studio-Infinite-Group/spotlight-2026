using Cinemachine;
using UnityEngine;

// 独立控制视角目标，真实相机的位置、阻尼和避障交给 Cinemachine。
// 光标锁定属于输入态策略，由 PlayerInputReader 统一负责，这里不碰 Cursor。
[DefaultExecutionOrder(-20)]
public sealed class PlayerCameraRig : MonoBehaviour
{
    // 玩家在场景里唯一，Motor / InputReader 都从玩家身上现取，不进 Inspector——
    // 手填引用是跨场景复制后最容易指错、且错了只表现为「视角不动」的坏法。
    [HideInInspector, SerializeField] private PlayerMotor _motor;
    [HideInInspector, SerializeField] private PlayerInputReader _input;
    [SerializeField] private Transform _target;
    [SerializeField] private CinemachineVirtualCamera _virtualCamera;
    [SerializeField] private float _mouseSensitivity = 0.15f;
    [SerializeField] private Vector2 _pitchLimits = new Vector2(-25f, 70f);
    [SerializeField] private float _standHeight = 1.2f;
    [SerializeField] private float _slideHeight = 0.6f;
    [SerializeField] private float _heightResponse = 12f;
    private float _yaw, _pitch;
    private PlayerMotor _subscribedMotor;
    private float _stepOffset, _stepSmoothVelocity, _poseHeight;

    public void Configure(PlayerMotor motor, PlayerInputReader input, Transform target, CinemachineVirtualCamera camera)
    { _motor = motor; _input = input; _target = target; _virtualCamera = camera; }
    public Quaternion ViewRotation => Quaternion.Euler(_pitch, _yaw, 0f);
    public void SetViewRotation(Quaternion rotation)
    {
        _yaw = rotation.eulerAngles.y;
        _pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, rotation.eulerAngles.x), _pitchLimits.x, _pitchLimits.y);
        if (_target != null) _target.rotation = ViewRotation;
    }
    private void OnEnable()
    {
        ResolvePlayer();
        _poseHeight = _target != null ? _target.localPosition.y : _standHeight;
        if (_target != null) { _yaw = _target.eulerAngles.y; _pitch = Mathf.DeltaAngle(0f, _target.eulerAngles.x); }
    }
    private void OnDisable()
    {
        if (_subscribedMotor != null)
        {
            _subscribedMotor.Teleported -= OnTeleport;
            _subscribedMotor.GroundStepped -= OnGroundStepped;
        }
        _subscribedMotor = null;
    }
    // 场景刚加载时 Awake 顺序不保证，玩家可能还没注册；这里每帧兜一次，拿到即止。
    private void ResolvePlayer()
    {
        if (_motor == null) _motor = PlayerMotor.Active;
        if (_motor == null) return;
        if (_input == null) _input = _motor.GetComponent<PlayerInputReader>();
        if (_subscribedMotor == _motor) return;
        _subscribedMotor = _motor;
        _motor.Teleported += OnTeleport;
        _motor.GroundStepped += OnGroundStepped;
    }
    private void Update()
    {
        if (TimeManager.IsPaused) return;
        ResolvePlayer();
        if (_target == null || _input == null || _motor == null) return;
        Vector2 look = _input.LookDelta;
        _yaw += look.x * _mouseSensitivity;
        _pitch = Mathf.Clamp(_pitch - look.y * _mouseSensitivity, _pitchLimits.x, _pitchLimits.y);
        _target.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        UpdateHeight(TimeManager.UnscaledDeltaTime);
    }
    public void UpdateHeight(float dt)
    {
        if (_target == null || _motor == null || dt <= 0f) return;
        float height = _motor.IsSliding ? _slideHeight : _standHeight;
        _poseHeight = Mathf.Lerp(_poseHeight, height, 1f - Mathf.Exp(-_heightResponse * dt));
        _stepOffset = _motor.Params.StepVisualSmoothTime > 0f
            ? Mathf.SmoothDamp(_stepOffset, 0f, ref _stepSmoothVelocity, _motor.Params.StepVisualSmoothTime, Mathf.Infinity, dt) : 0f;
        _target.localPosition = Vector3.up * (_poseHeight + _stepOffset);
    }
    private void OnGroundStepped(float delta)
    {
        if (_motor.Params.StepVisualSmoothTime <= 0f) return;
        float limit = _motor.Params.MaxStepUp + _motor.Params.MaxStepDown;
        float previous = _stepOffset;
        _stepOffset = Mathf.Clamp(_stepOffset - delta, -limit, limit);
        if (_target != null) _target.localPosition += Vector3.up * (_stepOffset - previous);
    }
    private void LateUpdate()
    {
        // 目标是角色的子节点，在 Brain 更新前恢复世界朝向，避免角色转身带动视角。
        if (_target != null) _target.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
    }
    private void OnTeleport(Vector3 delta)
    {
        if (_target != null) _target.localPosition -= Vector3.up * _stepOffset;
        _stepOffset = _stepSmoothVelocity = 0f;
        if (_virtualCamera != null && _target != null) _virtualCamera.OnTargetObjectWarped(_target, delta);
    }
}
