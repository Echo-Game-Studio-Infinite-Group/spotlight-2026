using Cinemachine;
using UnityEngine;

// 投射物：自己飞、自己检测、自己回收。
//
// 分工（与 CombatComponent / HealthComponent 一致）：
//   · 这里只管「飞」和「撞到什么」
//   · 扣谁的血、扣多少 → 交子弹自己的 CombatComponent 结算（与近战同一条链）
//   · 什么时候发射 → 交 AI 或输入
//
//    位移用 TimeManager.WorldDeltaTime，不用 Time.deltaTime：
//    WorldDeltaTime 已经乘过 GameRate 与 WorldRate，时停时它正好为 0，子弹自然冻在空中。
//    如果再自己乘一次 Rate 就是二次缩放（这个坑项目以前踩过）。
//
// 命中判定用自带判定盒（Hitbox + 触发器），不再用 SphereCast：
//    子弹自带一个 CombatComponent 当判定宿主，阵营与伤害在发射时从发射者同步。
//    于是命中走的是和近战完全相同的结算链（CombatComponent.TryHit），
//    震屏也从子弹自己的 CinemachineImpulseSource 发出——谁打中的谁震屏，不必绕回敌人。
//
//    代价：触发器不会与墙发生物理阻挡，所以另外保留一条细射线**只查环境**，
//    撞墙/地面就把子弹回收，避免穿墙飞出去。
[DisallowMultipleComponent]
public sealed class Projectile : MonoBehaviour, IPooledObject
{
    [Header("飞行")]
    [Tooltip("子弹速度")]
    [SerializeField, Min(0.01f)] private float _speed = 30f;

    [Tooltip("最长存活秒数")]
    [SerializeField, Min(0.05f)] private float _lifetime = 4f;

    [Tooltip("最大飞行距离")]
    [SerializeField, Min(0.1f)] private float _maxDistance = 60f;

    [Header("环境碰撞")]
    [Tooltip("只用于判断「撞墙/地面」以回收子弹。带受击身份的目标会被跳过，交给 Hitbox。")]
    [SerializeField] private LayerMask _environmentMask = -1;

    [Header("命中表现")]
    [Tooltip("震屏发射端。留空则自动取自身上的 CinemachineImpulseSource。")]
    [SerializeField] private CinemachineImpulseSource _impulseSource;

    [Header("判定组件")]
    [Tooltip("命中判定盒。留空则自动取自身。")]
    [SerializeField] private Hitbox _hitbox;

    [Tooltip("伤害结算层：命中后由它做阵营过滤与扣血，并播报 Landed。留空则自动取自身。")]
    [SerializeField] private CombatComponent _combat;

    private Vector3 _direction;
    private float _speedScale = 1f;
    private float _age;
    private float _travelled;
    private bool _isFlying;
    private Rigidbody _body;
    private readonly RaycastHit[] _obstacles = new RaycastHit[8];

    /// <summary>发射者。结算已交给子弹自己的 CombatComponent，这里保留引用供诊断与回收时断开。</summary>
    private RangeComponent _source;

    /// <summary>发射。direction 不必归一化。返回时不做位移，移动从下一次 Update 开始。</summary>
    /// <param name="direction">飞行方向（三维，含俯仰）</param>
    /// <param name="source">发射者：阵营与伤害在发射瞬间从它同步过来</param>
    /// <param name="speedScale">速度倍率。留给以后的蓄力弹，普通弹传 1</param>
    public void Launch(Vector3 direction, RangeComponent source, float speedScale = 1f)
    {
        if (direction.sqrMagnitude < 0.000001f) return;

        _direction = direction.normalized;
        _source = source;
        _speedScale = Mathf.Max(0.01f, speedScale);
        _age = 0f;
        _travelled = 0f;
        _isFlying = true;

        // 朝向对齐飞行方向：弹体如果是长条模型，不用再挂跟随脚本
        transform.rotation = Quaternion.LookRotation(_direction, Vector3.up);

        ConfigureCombat(source);
    }

    private void Awake()
    {
        EnsureReferences();
        _body = GetComponent<Rigidbody>();
    }

    private void EnsureReferences()
    {
        _hitbox = GetComponent<Hitbox>();
        _combat = GetComponent<CombatComponent>();
        _impulseSource = GetComponent<CinemachineImpulseSource>();
    }

    // 判定层随发射者走：阵营决定打谁，伤害决定扣多少。
    // 只在发射瞬间同步一次——子弹出膛后发射者改数值，不该影响已经在空中的弹。
    private void ConfigureCombat(RangeComponent source)
    {
        EnsureReferences();
        if (_combat == null || _hitbox == null) return;

        _combat.SetOwner(source != null ? source.Camp : CampType.Enemy, _hitbox, null);
        if (source != null) _combat.Damage = source.Damage;

        // 先减后加保证幂等：同一颗子弹被连续发射两次也不会重复订阅。
        _combat.Landed -= OnLanded;
        _combat.Landed += OnLanded;
        // Hitbox.Awake 把碰撞体关掉了，必须显式开，否则永远收不到 OnTriggerEnter。
        _combat.EnableHitbox();

        // 子弹按 Update 位移（不是物理步），高速下触发器容易漏检。
        // 运动学刚体支持 ContinuousSpeculative，用它把扫掠补上，避免穿过玩家。
        if (_body != null) _body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
    }

    private void Update()
    {
        if (!_isFlying) return;

        // 时停 → WorldDeltaTime 为 0 → 本帧位移为 0，子弹冻住
        float dt = TimeManager.WorldDeltaTime;
        if (dt <= 0f) return;

        // 活太久
        _age += dt;
        if (_age >= _lifetime)
        {
            Recycle();
            return;
        }

        float step = _speed * _speedScale * dt;

        // 只查环境的细射线：撞墙/地面就回收。
        // 命中「人」时这里故意不管——交给 Hitbox 的触发器去判定与结算，
        // 否则这条射线会抢在触发器之前把子弹从玩家身上回收掉，命中反而丢了。
        if (BlockedByEnvironment(transform.position, _direction, step, out _))
        {
            Recycle();
            return;
        }

        transform.position += _direction * step;

        // 飞太远
        _travelled += step;
        if (_travelled >= _maxDistance) Recycle();
    }

    // 找最近的「非受击目标」遮挡物；命中受击目标时返回 false，让子弹继续飞。
    private bool BlockedByEnvironment(Vector3 from, Vector3 direction, float distance, out RaycastHit hit)
    {
        hit = default;
        int count = Physics.RaycastNonAlloc(from, direction, _obstacles, distance, _environmentMask,
            QueryTriggerInteraction.Ignore);
        bool found = false;
        float nearest = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            Collider collider = _obstacles[i].collider;
            if (collider == null) continue;
            // 有受击身份的是「人」，不是环境——判定与结算都归战斗层，这里不抢。
            if (collider.GetComponentInParent<IAttackDamageReceiver>() != null) continue;
            if (_obstacles[i].distance >= nearest) continue;
            nearest = _obstacles[i].distance;
            hit = _obstacles[i];
            found = true;
        }
        return found;
    }

    // 命中播报：结算已完成（阵营过滤、无敌帧都在 CombatComponent.TryHit 里做过），
    // 这里只做表现——由子弹自己的发射端震屏。
    // 力度用实际造成的伤害，方向用结算传回的命中方向；被挡下时 Landed 根本不会触发。
    private void OnLanded(HealthComponent target, Vector3 point, Vector3 direction, float applied)
    {
        // 震屏就保留x y，不要z轴视觉效果好些
        //CameraShaker.Emit(_impulseSource, new Vector3(direction.x, direction.y, 0f), applied);
        CameraShaker.Emit(_impulseSource, direction, applied);
        if (_isFlying) Recycle();
    }

    // 命中、超时、超距都走这里
    private void Recycle()
    {
        _isFlying = false;
        if (ObjectPool.TryReturnToOwningPool(gameObject)) return;
        gameObject.SetActive(false);
    }

    // 池取出时：Launch 紧接着就会被调用并补齐全部飞行状态，
    // 这里只需保证「取出到 Launch 之间」不会先按上一次的旧方向飞一帧。
    public void OnSpawnedFromPool()
    {
        _isFlying = false;
    }

    // 归还进池时收尾：退订合并关掉判定盒，免得池里的子弹还在接收触发器事件。
    public void OnReturnedToPool()
    {
        _isFlying = false;
        if (_combat != null)
        {
            _combat.Landed -= OnLanded;
            _combat.DisableHitbox();
        }
        _source = null;
    }
}