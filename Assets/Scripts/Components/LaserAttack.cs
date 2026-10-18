using UnityEngine;

// 预警与实弹共用碰撞查询，表现只读结果，不能另算一条穿过遮挡的瞄准线。
[DisallowMultipleComponent]
[RequireComponent(typeof(HealthComponent), typeof(CombatComponent))]
public sealed class LaserAttack : MonoBehaviour
{
    public enum AttackPhase { Idle, Charging, Firing, Cooldown, Converging }

    [Header("激光时序（伤害与冷却使用同物体的 CombatComponent）")]
    [SerializeField, Min(0f)] private float _chargeSeconds = 1.2f;
    [Tooltip("红线追踪结束后固定方向，汇聚期间给玩家躲避窗口；设为 0 立即开火。")]
    [SerializeField, Min(0f)] private float _convergenceSeconds = .5f;
    [SerializeField, Min(.01f)] private float _beamSeconds = .5f;
    [SerializeField, Min(.1f)] private float _range = 30f;
    [Tooltip("光束的命中半径，设为 0 使用细射线。")]
    [SerializeField, Min(0f)] private float _hitRadius = .08f;
    [Tooltip("包含玩家和环境实体；触发器及自身碰撞体始终忽略。")]
    [SerializeField] private LayerMask _hitMask = Physics.DefaultRaycastLayers;

    [Header("表现引用")]
    [SerializeField] private Transform _muzzle;
    [SerializeField] private LineRenderer _beam;

    [Header("视线")]
    [Tooltip("挡视线的层：被这些层挡住就不开始蓄力（隔墙不索敌）；蓄力/汇聚中丢了视线也会中止")]
    [SerializeField] private LayerMask _sightMask = Physics.DefaultRaycastLayers;

    private HealthComponent _health;
    private CombatComponent _combat;
    private AttackTelegraph _telegraph;
    private readonly RaycastHit[] _hits = new RaycastHit[32];
    private readonly Collider[] _overlaps = new Collider[32];
    // 视线检测的非分配缓冲：每逻辑步都查，不能每次 new。
    private readonly RaycastHit[] _sightHits = new RaycastHit[8];
    private float _remaining;
    private bool _damageAttempted;
    private Vector3 _origin, _direction;

    public AttackPhase Phase { get; private set; }
    public bool IsCharging => Phase == AttackPhase.Charging;
    public bool IsConverging => Phase == AttackPhase.Converging;
    public bool IsFiring => Phase == AttackPhase.Firing;
    public Transform Muzzle => _muzzle != null ? _muzzle : transform;
    public Vector3 BeamStart => _origin;
    public Vector3 BeamEnd { get; private set; }
    public Vector3 BeamDirection => _direction;
    public float Remaining => _remaining;
    public float ChargeProgress => IsCharging ? 1f - Mathf.Clamp01(_remaining / Mathf.Max(.001f, _chargeSeconds)) : 0f;
    public float ConvergenceProgress => IsConverging ? 1f - Mathf.Clamp01(_remaining / Mathf.Max(.001f, _convergenceSeconds)) : 0f;
    public float FireProgress => IsFiring ? 1f - Mathf.Clamp01(_remaining / Mathf.Max(.01f, _beamSeconds)) : 0f;
    public bool HasBeamHit { get; private set; }
    public Vector3 ImpactPoint { get; private set; }
    public Vector3 ImpactNormal { get; private set; }
    public event System.Action Fired;

    private void Awake()
    {
        _health = GetComponent<HealthComponent>();
        _combat = GetComponent<CombatComponent>();
        _telegraph = GetComponent<AttackTelegraph>();
        _combat.SetOwner(CampType.Enemy, null, _health);
    }

    private void OnEnable()
    {
        ResetAttack();
        _health.Damaged += OnDamaged;
        _combat.Parried += OnParried;
        _health.Revived += OnRevived;
    }

    private void OnDisable()
    {
        _health.Damaged -= OnDamaged;
        _combat.Parried -= OnParried;
        _health.Revived -= OnRevived;
        ResetAttack();
    }

    /// <summary>由敌人固定逻辑帧驱动。dt 已经过世界时间缩放，不再重复乘速率。</summary>
    public void Simulate(float dt, HealthComponent target, bool canCharge = true)
    {
        if (!isActiveAndEnabled || !_health.IsAlive) { ResetAttack(); return; }
        if (TimeManager.IsPaused || TimeManager.IsParryStopped) return;
        dt = Mathf.Max(0f, dt);
        if (IsFiring)
        {
            _remaining -= dt;
            if (_remaining <= 0f) { BeginCooldown(); return; }
            // 世界时停只冻结计时和方向，已出现的光束仍能命中走进来的玩家。
            TraceBeam(target);
            return;
        }
        if (dt <= 0f) return;
        if (Phase == AttackPhase.Cooldown)
        {
            _remaining -= dt;
            if (_remaining <= 0f) Phase = AttackPhase.Idle;
            return;
        }
        if (target == null || !target.IsAlive || !canCharge)
        {
            if (IsCharging || IsConverging) BeginCooldown();
            return;
        }
        if (Phase == AttackPhase.Idle)
        {
            // 发起攻击沿用远程敌人的水平射程口径，实际光束始终受三维最大长度限制。
            Vector3 flat = target.transform.position - Muzzle.position;
            flat.y = 0f;
            if (flat.sqrMagnitude > _range * _range) return;
            // 隔墙不索敌：射程内但炮口看不到玩家就不蓄力、不亮预警。
            if (!HasLineOfSight(target.transform)) return;
            _combat.SourceType = AttackSourceType.Beam;
            _combat.ArmParryTarget();
            Phase = AttackPhase.Charging;
            _remaining = _chargeSeconds;
            _telegraph?.Play();
        }
        if (IsCharging)
        {
            _remaining -= dt;
            _origin = Muzzle.position;
            _direction = Muzzle.forward.normalized;
            // 蓄力中玩家躲到墙后：这一发作废，转冷却（预警一起收掉）
            if (!HasLineOfSight(target.transform)) { BeginCooldown(); return; }
            if (_remaining <= 0f)
            {
                // 汇聚起点即锁定点，后续不能重新读取炮口朝向，否则玩家没有稳定的躲避窗口。
                Phase = AttackPhase.Converging;
                _remaining = Mathf.Max(0f, _convergenceSeconds);
            }
        }
        else if (IsConverging)
        {
            _remaining -= dt;
            // 汇聚期同样要在视线内：贴上去躲进墙后不该被"已锁定"打死
            if (!HasLineOfSight(target.transform)) { BeginCooldown(); return; }
        }
        if (_remaining > 0f)
        {
            TraceBeam(target, false);
            return;
        }
        _combat.ResetAttackState();
        _combat.SourceType = AttackSourceType.Beam;
        _combat.WindowSeconds = 0f;
        _combat.TryBeginAttack(TimeManager.WorldTime);
        Phase = AttackPhase.Firing;
        _remaining = Mathf.Max(.01f, _beamSeconds);
        _damageAttempted = false;
        _telegraph?.Stop();
        Fired?.Invoke();
        TraceBeam(target);
    }

    /// <summary>炮口到玩家之间有没有被墙挡住。蓄力、汇聚、开火前都用它，避免隔墙锁人。</summary>
    public bool HasLineOfSight(Transform target)
        => SightCheck.HasLineOfSight(Muzzle.position, target, transform, _sightMask, _sightHits);

    private void TraceBeam(HealthComponent target, bool applyDamage = true)
    {
        float distance = _range;
        Collider closest = null;
        Vector3 point = _origin + _direction * distance;
        Vector3 normal = -_direction;
        // Cast 不报告起点内部的碰撞体，补一次重叠查询，避免贴身玩家或贴脸墙被漏掉。
        int overlaps = Physics.OverlapSphereNonAlloc(_origin, Mathf.Max(.0001f, _hitRadius), _overlaps,
            _hitMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < overlaps; i++)
        {
            Collider body = _overlaps[i];
            if (body.transform.IsChildOf(transform)) continue;
            distance = 0f;
            closest = body;
            point = _origin;
            // 同时与玩家和遮挡重叠时，实体遮挡优先，不能穿墙扣血。
            if (body.GetComponentInParent<HealthComponent>() != target) break;
        }
        int count = _hitRadius > 0f
            ? Physics.SphereCastNonAlloc(_origin, _hitRadius, _direction, _hits, _range,
                _hitMask, QueryTriggerInteraction.Ignore)
            : Physics.RaycastNonAlloc(_origin, _direction, _hits, _range, _hitMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = _hits[i];
            if (hit.collider.transform.IsChildOf(transform) || hit.distance >= distance) continue;
            distance = hit.distance;
            closest = hit.collider;
            point = hit.point;
            normal = hit.normal;
        }
        // 拥挤场景填满查询缓冲时保守截断，不能拿可能漏掉最近遮挡的结果穿墙攻击。
        if (count == _hits.Length || overlaps == _overlaps.Length) { distance = 0f; closest = null; }
        BeamEnd = _origin + _direction * distance;
        HasBeamHit = closest != null;
        ImpactPoint = point;
        ImpactNormal = normal;
        if (_beam != null && applyDamage)
        {
            _beam.useWorldSpace = true;
            _beam.positionCount = 2;
            _beam.SetPosition(0, _origin);
            _beam.SetPosition(1, BeamEnd);
            _beam.enabled = true;
        }
        if (applyDamage && IsFiring)
        {
            float beamLength = Vector3.Distance(_origin, BeamEnd);
            float width = Mathf.Max(.01f, _hitRadius * 2f);
            _combat.SetParryTargetPose(new AttackVolumePose((_origin + BeamEnd) * .5f,
                new Vector3(width, width, beamLength * .5f), Quaternion.LookRotation(_direction), transform.position));
            if (ParryResolver.ResolveIncoming(_combat)) return;
        }
        if (!applyDamage || _damageAttempted || closest == null || target == null || !target.IsAlive
            || closest.GetComponentInParent<HealthComponent>() != target) return;
        // 一轮只结算一次，命中无敌帧也消耗本轮资格。
        // 光束保留独立来源属性，命中前与同一套拼刀几何结算。
        _damageAttempted = true;
        _combat.TryHit(closest, target, new AttackDamageRequest(_combat.Damage, point, _direction, 0L, 0,
            AttackSourceType.Beam, _combat.ParryKind));
    }

    private void OnDamaged(HealthComponent source, float applied, Vector3 point, Vector3 direction)
    {
        if (!source.IsAlive) ResetAttack();
        else if (IsCharging || IsConverging) BeginCooldown();
    }

    private void OnParried(ParryContact contact) => BeginCooldown();

    private void OnRevived(HealthComponent source) => ResetAttack();

    private void BeginCooldown()
    {
        ResetAttack();
        Phase = AttackPhase.Cooldown;
        _remaining = _combat.Cooldown;
    }

    private void ResetAttack()
    {
        _combat.FinishAttack();
        Phase = AttackPhase.Idle;
        _remaining = 0f;
        _damageAttempted = false;
        HasBeamHit = false;
        _telegraph?.Stop();
        if (_beam != null) _beam.enabled = false;
    }
}
