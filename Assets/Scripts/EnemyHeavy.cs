using UnityEngine;
using UnityEngine.AI;

// 重型独立决策：追击、挥击、炮击蓄力与收招互斥。
[RequireComponent(typeof(LaserAttack))]
[DefaultExecutionOrder(50)]
public sealed class EnemyHeavy : EnemyBase
{
    [Header("炮台瞄准")]
    [Tooltip("独立的炮口俯仰节点，不依赖模型骨骼。")]
    [SerializeField] private Transform _aimPivot;
    [SerializeField, Range(0f, 180f)] private float _yawLimit = 180f;
    [SerializeField, Range(0f, 89f)] private float _aimUpLimit = 60f;
    [SerializeField, Range(0f, 89f)] private float _aimDownLimit = 80f;
    [SerializeField, Min(0f)] private float _yawSpeed = 180f;
    [SerializeField, Min(0f)] private float _pitchSpeed = 120f;

    [SerializeField, Min(0f)] private float _detectionRange = 128f;
    [SerializeField, Min(0f)] private float _chaseBetweenShots = 3f;
    [SerializeField, Min(0.1f)] private float _navMeshSnapDistance = 3f;
    private float _chaseRemaining;
    private Animator _animator;
    private bool _rangeActive;
    private bool _rangeStopping;
    private bool _enteredRangeStop;
    private LaserAttack _laser;
    private EnemyAnimation _modelAnimation;
    private Quaternion _homeRotation;
    private Quaternion _pivotRotation;
    private float _yaw, _pitch;
    private Transform _aimTarget;
    private Collider _targetBody;
    private bool _poseInitialized;

    protected override void Awake()
    {
        base.Awake();
        _laser = GetComponent<LaserAttack>();
        _modelAnimation = GetComponentInChildren<EnemyAnimation>();
        _animator = GetComponentInChildren<Animator>();
        // 寻路代理由基类 EnemyBase.Awake 取好，这里走 Agent 访问器，不再自己声明一份。
        SetChasePlayer(true);
    }

    protected override void OnEnable()
    {
        base.OnEnable();
        _rangeActive = _rangeStopping = _enteredRangeStop = false;
        _chaseRemaining = _chaseBetweenShots;
        // 导航驱动位移，实体碰撞体保留撞击判定。
        if (Agent != null) Agent.enabled = true;
        if (!_poseInitialized)
        {
            _pivotRotation = _aimPivot != null ? _aimPivot.localRotation : Quaternion.identity;
            _poseInitialized = true;
        }
        _homeRotation = transform.rotation;
        if (_aimPivot != null) _aimPivot.localRotation = _pivotRotation;
        _yaw = _pitch = 0f;
    }

    protected override void Start()
    {
        base.Start();
        // 场景中的旧实例可能悬在烘焙网格上方，先落到附近可走面再寻路。
        if (Agent != null && !Agent.isOnNavMesh && NavMesh.SamplePosition(transform.position, out NavMeshHit hit,
            _navMeshSnapDistance, Agent.areaMask)) Agent.Warp(hit.position);
    }

    protected override void FixedUpdate()
    {
        base.FixedUpdate();
        Player player = Player.Current;
        HealthComponent target = player != null ? player.Health : null;
        float dt = TimeManager.WorldFixedDeltaTime;
        if (!_rangeActive) _chaseRemaining = Mathf.Max(0f, _chaseRemaining - dt);
        bool canAim = _chaseRemaining <= 0f && IsAlive && IsChasing && !_rangeStopping && !IsAttacking
            && (_modelAnimation == null || (!_modelAnimation.IsHurting && _modelAnimation.CanAttack))
            && target != null && target.IsAlive && Vector3.Distance(transform.position, target.transform.position) > AttackRange
            && Vector3.Distance(transform.position, target.transform.position) <= _detectionRange;
        if (!_rangeActive && canAim && !_laser.IsCharging && !_laser.IsConverging && !_laser.IsFiring)
        {
            _homeRotation = transform.rotation;
            _yaw = 0f;
        }
        if (IsAlive && dt > 0f && canAim && _laser.IsCharging && target != null && target.IsAlive)
            AimAt(target.transform, dt);
        _laser.Simulate(dt, target, canAim || (_rangeActive && !_rangeStopping && IsAlive && IsChasing && !_modelAnimation.IsHurting));
        bool shooting = _laser.IsCharging || _laser.IsConverging || _laser.IsFiring;
        if (!_rangeActive && shooting)
        {
            StopMoving();
            _rangeActive = true;
            _animator.CrossFadeInFixedTime("RangeStart", 0.1f);
        }
        if (_rangeActive && !shooting && !_rangeStopping)
        {
            if (IsAlive && !_modelAnimation.IsHurting)
            {
                _rangeStopping = true;
                _enteredRangeStop = false;
                _animator.CrossFadeInFixedTime("RangeStop", 0.1f);
            }
            else _rangeActive = false;
        }
        if (_rangeStopping && _animator.GetCurrentAnimatorStateInfo(0).IsName("RangeStop")) _enteredRangeStop = true;
        if (_rangeStopping && (_modelAnimation.IsHurting || !IsAlive || (_enteredRangeStop && !_animator.GetCurrentAnimatorStateInfo(0).IsName("RangeStop")
            && !_animator.IsInTransition(0))))
        {
            _rangeActive = _rangeStopping = false;
            _chaseRemaining = _chaseBetweenShots;
        }
    }


    protected override void Chase()
    {
        Player player = Player.Current;
        if (_rangeActive || player == null || !player.Health.IsAlive)
        { StopMoving(); return; }
        Vector3 offset = player.transform.position - transform.position;
        offset.y = 0f;
        if (offset.magnitude > _detectionRange) { StopMoving(); return; }
        if (offset.magnitude <= AttackRange)
        {
            StopMoving();
            if (offset.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.RotateTowards(transform.rotation,
                    Quaternion.LookRotation(offset), _yawSpeed * TimeManager.WorldFixedDeltaTime);
            if (Vector3.Angle(transform.forward, offset) < 20f) TryAttack(player.Motor);
            return;
        }
        if (Agent == null || !Agent.isActiveAndEnabled || !Agent.isOnNavMesh) return;
        Agent.speed = WalkSpeed * TimeManager.WorldRate;
        Agent.stoppingDistance = Mathf.Max(0f, AttackRange - Agent.radius);
        Agent.isStopped = false;
        Agent.SetDestination(player.transform.position);
        Speed = Agent.velocity.magnitude / Mathf.Max(TimeManager.WorldRate, 0.0001f);
    }

    private void AimAt(Transform target, float dt)
    {
        Vector3 point = target.position;
        // 与远程敌人一致，瞄准实体碰撞体中心，避免角色原点在脚底导致朝地面射击。
        if (_aimTarget != target || _targetBody == null || !_targetBody.enabled)
        {
            _aimTarget = target;
            _targetBody = null;
            foreach (Collider body in target.GetComponentsInChildren<Collider>())
                if (body.enabled && !body.isTrigger) { _targetBody = body; break; }
        }
        if (_targetBody != null) point = _targetBody.bounds.center;
        Vector3 local = Quaternion.Inverse(_homeRotation) * (point - _laser.Muzzle.position);
        if (local.sqrMagnitude < .000001f) return;
        float yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, -_yawLimit, _yawLimit);
        float pitch = Mathf.Clamp(Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg,
            -_aimDownLimit, _aimUpLimit);
        // 有转角限制时不能走 MoveTowardsAngle 的跨界捷径，否则会绕到允许范围之外。
        _yaw = _yawLimit >= 180f ? Mathf.MoveTowardsAngle(_yaw, yaw, _yawSpeed * dt)
            : Mathf.MoveTowards(_yaw, yaw, _yawSpeed * dt);
        _pitch = Mathf.MoveTowards(_pitch, pitch, _pitchSpeed * dt);
        transform.rotation = _homeRotation * Quaternion.Euler(0f, _yaw, 0f);
        if (_aimPivot != null) _aimPivot.localRotation = Quaternion.Euler(-_pitch, 0f, 0f) * _pivotRotation;
    }
}
