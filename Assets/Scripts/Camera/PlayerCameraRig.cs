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

    public void Configure(PlayerMotor motor, PlayerInputReader input, Transform target, CinemachineVirtualCamera camera)
    { _motor = motor; _input = input; _target = target; _virtualCamera = camera; }
    private void OnEnable()
    {
        ResolvePlayer();
        if (_motor != null) _motor.Teleported += OnTeleport;
        if (_target != null) { _yaw = _target.eulerAngles.y; _pitch = Mathf.DeltaAngle(0f, _target.eulerAngles.x); }
    }
    private void OnDisable()
    {
        if (_motor != null) _motor.Teleported -= OnTeleport;
    }
    // 场景刚加载时 Awake 顺序不保证，玩家可能还没注册；这里每帧兜一次，拿到即止。
    private void ResolvePlayer()
    {
        if (_motor == null) _motor = PlayerMotor.Active;
        if (_motor == null) return;
        if (_input == null) _input = _motor.GetComponent<PlayerInputReader>();
    }
    private void Update()
    {
        ResolvePlayer();
        if (_target == null || _input == null || _motor == null) return;
        Vector2 look = _input.LookDelta;
        _yaw += look.x * _mouseSensitivity;
        _pitch = Mathf.Clamp(_pitch - look.y * _mouseSensitivity, _pitchLimits.x, _pitchLimits.y);
        _target.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        float height = _motor.IsSliding ? _slideHeight : _standHeight;
        _target.localPosition = Vector3.Lerp(_target.localPosition, Vector3.up * height,
            1f - Mathf.Exp(-_heightResponse * TimeManager.UnscaledDeltaTime));
    }
    private void LateUpdate()
    {
        // 目标是角色的子节点，在 Brain 更新前恢复世界朝向，避免角色转身带动视角。
        if (_target != null) _target.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
    }
    private void OnTeleport(Vector3 delta)
    { if (_virtualCamera != null && _target != null) _virtualCamera.OnTargetObjectWarped(_target, delta); }
}
