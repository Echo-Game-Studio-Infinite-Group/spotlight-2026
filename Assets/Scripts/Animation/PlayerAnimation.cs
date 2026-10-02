using UnityEngine;

[RequireComponent(typeof(PlayerMotor))]
[DisallowMultipleComponent]
public sealed class PlayerAnimation : MonoBehaviour
{
    private static readonly int MotionStateId = Animator.StringToHash("MotionState");
    private static readonly int RunBlendId = Animator.StringToHash("RunBlend");

    [SerializeField] private Animator _animator;
    [SerializeField, Min(0f)] private float _idleSpeed = 0.1f;
    [SerializeField, Min(0f)] private float _blendResponse = 10f;
    [SerializeField, Min(0f)] private float _fallGrace = 0.08f;
    private PlayerMotor _motor;
    private float _runBlend;
    private float _airborneTime;

    public void Configure(Animator animator) => _animator = animator;

    private void Awake()
    {
        _motor = GetComponent<PlayerMotor>();
        if (_animator == null) _animator = GetComponentInChildren<Animator>();
    }

    private void Update()
    {
        if (_motor == null || _animator == null || _motor.Params == null) return;

        // CharacterController 在贴地移动时可能短暂丢失 Grounded，延迟进入下落动画；主动起跳立即播放。
        _airborneTime = _motor.IsGrounded ? 0f : _airborneTime + TimeManager.PlayerDeltaTime;
        bool airborne = _motor.IsWallSliding || _motor.Velocity.y > 0f || _airborneTime >= _fallGrace;
        int state = _motor.IsSliding ? 3 : airborne ? 2 :
            _motor.HorizontalSpeed > _idleSpeed ? 1 : 0;
        float runStart = _motor.Params.WalkSpeed;
        float runEnd = Mathf.Max(runStart + 0.01f, _motor.Params.GroundSpeedThreshold);
        float targetBlend = Mathf.InverseLerp(runStart, runEnd, _motor.HorizontalSpeed);
        _runBlend = Mathf.MoveTowards(_runBlend, targetBlend,
            _blendResponse * TimeManager.PlayerDeltaTime);

        _animator.SetInteger(MotionStateId, state);
        _animator.SetFloat(RunBlendId, _runBlend);
        _animator.speed = TimeManager.PlayerRate;
    }
}
