using UnityEngine;
using UnityEngine.AI;

// 血量与受击沿用 EnemyBase，固定炮台不参与普通敌人的追击和近战决策。
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

    private LaserAttack _laser;
    private EnemyAnimation _modelAnimation;
    private NavMeshAgent _agent;
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
        _agent = GetComponent<NavMeshAgent>();
        SetChasePlayer(false);
    }

    protected override void OnEnable()
    {
        base.OnEnable();
        // 保留实体碰撞体，关闭导航驱动，保证没有寻路位移和导航转向。
        if (_agent != null) _agent.enabled = false;
        if (!_poseInitialized)
        {
            _pivotRotation = _aimPivot != null ? _aimPivot.localRotation : Quaternion.identity;
            _poseInitialized = true;
        }
        _homeRotation = transform.rotation;
        if (_aimPivot != null) _aimPivot.localRotation = _pivotRotation;
        _yaw = _pitch = 0f;
    }

    protected override void FixedUpdate()
    {
        // 仍让公共层记录前一帧姿态，供模型插值；外部打开追击开关也不能让炮台走路或近战。
        if (IsChasing) SetChasePlayer(false);
        base.FixedUpdate();
        Player player = Player.Current;
        HealthComponent target = player != null ? player.Health : null;
        float dt = TimeManager.WorldFixedDeltaTime;
        bool canAim = _modelAnimation == null || !_modelAnimation.IsHurting;
        if (IsAlive && dt > 0f && canAim && !_laser.IsFiring && target != null && target.IsAlive)
            AimAt(target.transform, dt);
        _laser.Simulate(dt, target, canAim);
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
