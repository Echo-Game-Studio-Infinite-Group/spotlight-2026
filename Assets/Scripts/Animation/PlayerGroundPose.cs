using UnityEngine;

// 姿态只修正 Humanoid 骨骼；胶囊、速度和接地始终由 Motor 决定。
[DisallowMultipleComponent]
[DefaultExecutionOrder(-30)]
[RequireComponent(typeof(Animator), typeof(PlayerMotor), typeof(PlayerAnimation))]
public sealed class PlayerGroundPose : MonoBehaviour
{
    [SerializeField, Min(0f)] private float _maxFootCorrection = .3f;
    [SerializeField, Min(0f)] private float _maxPelvisDrop = .18f;
    [SerializeField, Min(.01f)] private float _swingHeight = .2f;
    [SerializeField, Min(0f)] private float _poseResponse = 16f;
    [SerializeField, Range(0f, 60f)] private float _maxSlideTilt = 45f;
    [SerializeField, Min(0f)] private float _slideGroundClearance = .02f;
    private readonly RaycastHit[] _hits = new RaycastHit[16];
    private Animator _animator;
    private PlayerMotor _motor;
    private PlayerAnimation _locomotion;
    private PlayerRagdollHitStun _ragdoll;
    private CharacterController _controller;
    private float _stepOffset, _stepVelocity, _pelvisDrop;
    private Quaternion _slideTilt = Quaternion.identity;
    private float _sampleDt;
    private static readonly int ActionPlayingId = Animator.StringToHash("ActionPlaying");
    public int IkEvaluationCount { get; private set; }
    public float LeftFootWeight { get; private set; }
    public float RightFootWeight { get; private set; }
    public float VisualStepOffset => _stepOffset;
    public Vector3 LeftFootTarget { get; private set; }
    public Vector3 RightFootTarget { get; private set; }
    public float SlideTiltAngle => Quaternion.Angle(Quaternion.identity, _slideTilt);

    private void Awake()
    {
        Configure(GetComponent<Animator>(), GetComponent<PlayerMotor>(), GetComponent<PlayerAnimation>());
    }
    public void Configure(Animator animator, PlayerMotor motor, PlayerAnimation locomotion)
    {
        _animator = animator;
        _motor = motor;
        _locomotion = locomotion;
        _ragdoll = GetComponent<PlayerRagdollHitStun>();
        _controller = GetComponent<CharacterController>();
    }
    private void OnEnable()
    {
        _motor.GroundStepped += OnStep;
        _motor.Teleported += OnTeleport;
    }
    private void OnDisable()
    {
        _motor.GroundStepped -= OnStep;
        _motor.Teleported -= OnTeleport;
        ResetPose();
    }
    private void Update()
    {
        if (!_locomotion.IsSequenceDriven) Prepare(TimeManager.PlayerDeltaTime);
    }
    public void Prepare(float dt)
    {
        _sampleDt = Mathf.Max(0f, dt);
        if (dt <= 0f) return;
        _stepOffset = _motor.Params.StepVisualSmoothTime > 0f
            ? Mathf.SmoothDamp(_stepOffset, 0f, ref _stepVelocity, _motor.Params.StepVisualSmoothTime, Mathf.Infinity, dt) : 0f;
        Vector3 normal = _motor.IsGrounded ? _motor.GroundSupport.Normal : Vector3.up;
        if (_motor.IsGrounded && _motor.IsSliding)
        {
            Vector3 origin = transform.position + transform.forward * _motor.Params.SlideForwardExtent;
            origin.y += _motor.Params.MaxStepUp + _maxFootCorrection;
            if (TryGround(origin, _motor.Params.MaxStepUp + _maxFootCorrection + _motor.Params.MaxStepDown, out RaycastHit front))
            {
                if (normal.y > .999f && front.normal.y > .999f)
                {
                    Vector3 baseline = transform.position; baseline.y = _motor.GroundSupport.RootHeight;
                    Vector3 tangent = front.point - baseline;
                    Vector3 right = Vector3.Cross(Vector3.up, transform.forward);
                    normal = Vector3.Cross(tangent, right).normalized;
                    if (normal.y < 0f) normal = -normal;
                }
                else if (normal.y > .999f) normal = front.normal;
            }
        }
        Quaternion target = _motor.IsGrounded && _motor.IsSliding
            ? Quaternion.RotateTowards(Quaternion.identity, Quaternion.FromToRotation(Vector3.up, normal), _maxSlideTilt)
            : Quaternion.identity;
        _slideTilt = Quaternion.Slerp(_slideTilt, target, 1f - Mathf.Exp(-_poseResponse * dt));
    }
    private void OnAnimatorIK(int layer)
    {
        if (layer != 0 || !_animator.isHuman) return;
        IkEvaluationCount++;
        bool active = _motor.enabled && _motor.IsGrounded && !_locomotion.IsHurting
            && !(_ragdoll != null && _ragdoll.IsActive) && !_animator.GetBool(ActionPlayingId);
        bool slide = _motor.IsSliding || _locomotion.IsSlideRecovering;
        bool feet = active && !slide;
        Vector3 left = _animator.GetIKPosition(AvatarIKGoal.LeftFoot);
        Vector3 right = _animator.GetIKPosition(AvatarIKGoal.RightFoot);
        Quaternion leftRotation = _animator.GetIKRotation(AvatarIKGoal.LeftFoot);
        Quaternion rightRotation = _animator.GetIKRotation(AvatarIKGoal.RightFoot);
        float leftDrop = FootTarget(left, _animator.leftFeetBottomHeight, feet, out Vector3 leftTarget, out Vector3 leftNormal, out float leftWeight);
        float rightDrop = FootTarget(right, _animator.rightFeetBottomHeight, feet, out Vector3 rightTarget, out Vector3 rightNormal, out float rightWeight);
        float desiredDrop = feet ? Mathf.Clamp(Mathf.Min(leftDrop, rightDrop), -_maxPelvisDrop, 0f) : 0f;
        _pelvisDrop = Mathf.Lerp(_pelvisDrop, desiredDrop, 1f - Mathf.Exp(-_poseResponse * _sampleDt));
        if (active)
        {
            Vector3 body = _animator.bodyPosition;
            if (slide)
            {
                body = transform.position + _slideTilt * (body - transform.position);
                _animator.bodyRotation = _slideTilt * _animator.bodyRotation;
            }
            float skinCorrection = slide ? Mathf.Clamp(_motor.GroundSupport.RootHeight - transform.position.y,
                -_motor.Params.GroundTolerance, _motor.Params.GroundTolerance + _controller.skinWidth) + _slideGroundClearance : 0f;
            if (Mathf.Abs(_stepOffset + _pelvisDrop + skinCorrection) > .00001f || slide && Quaternion.Angle(_slideTilt, Quaternion.identity) > .001f)
                _animator.bodyPosition = body + Vector3.up * (_stepOffset + _pelvisDrop + skinCorrection);
        }
        LeftFootWeight = leftWeight; RightFootWeight = rightWeight;
        LeftFootTarget = leftTarget; RightFootTarget = rightTarget;
        ApplyFoot(AvatarIKGoal.LeftFoot, leftTarget, leftRotation, leftNormal, leftWeight);
        ApplyFoot(AvatarIKGoal.RightFoot, rightTarget, rightRotation, rightNormal, rightWeight);
    }
    private float FootTarget(Vector3 animated, float soleHeight, bool active, out Vector3 target, out Vector3 normal, out float weight)
    {
        target = animated; normal = Vector3.up; weight = 0f;
        if (!active) return 0f;
        Vector3 origin = animated;
        origin.y = transform.position.y + _motor.Params.MaxStepUp + _maxFootCorrection;
        if (!TryGround(origin, _motor.Params.MaxStepUp + _maxFootCorrection + _motor.Params.MaxStepDown, out RaycastHit hit)) return 0f;
        float correction = hit.point.y + soleHeight - animated.y;
        if (Mathf.Abs(correction) > _maxFootCorrection) return 0f;
        normal = hit.normal;
        target.y = hit.point.y + soleHeight;
        weight = _locomotion.CurrentMotionState == PlayerAnimation.MotionStateIdle ? 1f
            : Mathf.Clamp01(1f - Mathf.Max(0f, -correction) / _swingHeight);
        return correction * weight;
    }
    private void ApplyFoot(AvatarIKGoal goal, Vector3 target, Quaternion rotation, Vector3 normal, float weight)
    {
        _animator.SetIKPositionWeight(goal, weight);
        _animator.SetIKRotationWeight(goal, weight);
        _animator.SetIKPosition(goal, target);
        _animator.SetIKRotation(goal, Quaternion.FromToRotation(rotation * Vector3.up, normal) * rotation);
    }
    private bool TryGround(Vector3 origin, float distance, out RaycastHit surface)
    {
        surface = default;
        int count = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, distance, _motor.Params.CollisionMask, QueryTriggerInteraction.Ignore);
        if (count == _hits.Length) return false;
        float nearest = float.PositiveInfinity;
        float minUp = Mathf.Cos(_motor.Params.MaxGroundAngle * Mathf.Deg2Rad);
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = _hits[i];
            if (hit.transform.IsChildOf(transform) || hit.normal.y < minUp || hit.distance >= nearest) continue;
            surface = hit; nearest = hit.distance;
        }
        return surface.collider != null;
    }
    private void OnStep(float delta)
    {
        float limit = _motor.Params.MaxStepUp + _motor.Params.MaxStepDown;
        _stepOffset = Mathf.Clamp(_stepOffset - delta, -limit, limit);
    }
    private void OnTeleport(Vector3 delta) => ResetPose();
    private void ResetPose()
    {
        _stepOffset = _stepVelocity = _pelvisDrop = 0f;
        LeftFootWeight = RightFootWeight = 0f;
        _slideTilt = Quaternion.identity;
    }
}
