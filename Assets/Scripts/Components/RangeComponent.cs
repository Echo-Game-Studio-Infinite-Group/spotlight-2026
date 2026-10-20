using UnityEngine;

// 远程层只负责蓄力与生成子弹；伤害及 Parry 统一由每颗子弹的 CombatComponent 结算。
//
// 时钟由调用方传入：敌人走 WorldTime（时停就该冻住敌人），组件自己不取时钟。
[RequireComponent(typeof(CombatComponent))]
[DisallowMultipleComponent]
public sealed class RangeComponent : MonoBehaviour
{
    [Header("远程配置")]
    [SerializeField] private CampType _camp = CampType.Enemy;
    [SerializeField, Min(0f)] private float _damage = 8f;
    [SerializeField, Min(0.01f)] private float _cooldown = 1.2f;
    [Tooltip("开火的距离上限")]
    [SerializeField, Min(0.1f)] private float _range = 16f;

    [Header("弹体")]
    [Tooltip("要生成的子弹预制体。留空则 CanFire 为 false。")]
    [SerializeField] private Projectile _projectilePrefab;
    [Tooltip("出膛点。挂一个空的子物体对准炮口即可；留空则用自身位置。")]
    [SerializeField] private Transform _muzzle;
    [Tooltip("池里预造子弹数量")]
    [SerializeField, Min(0)] private int _prewarm = 4;

    private CombatComponent _combat;
    private float _readyAt = float.NegativeInfinity;
    // 子弹池：与 EnemySpawner 同一套路 —— 自己找，找不到就挂一个。
    // 子弹归还走 ObjectPool.TryReturnToOwningPool，所以发射方不必记录"这颗子弹来自哪个池"。
    private ObjectPool _pool;

    [Header("预警窗口")]
    // 开枪前先亮提示的时长。策划要的是「开枪前给一段判定区间」；0 = 退回老规矩：秒发。
    [SerializeField, Min(0f)] private float _telegraphSeconds = 0.4f;

    [Header("视线")]
    [Tooltip("挡视线的层：射程内但视线被这些层挡住就不开火（隔墙不索敌）")]
    [SerializeField] private LayerMask _sightMask = Physics.DefaultRaycastLayers;

    [Tooltip("提示组件。留空则自动找同物体上的 AttackTelegraph。")]
    [SerializeField] private AttackTelegraph _telegraph;

    // 预警窗口的终点。用调用方传进来的时钟算，组件自己不取时钟。
    private float _chargeUntil;
    private bool _charging;
    // 视线检测的非分配缓冲：每逻辑步都会查一次，不能每次 new。
    private readonly RaycastHit[] _sightHits = new RaycastHit[8];

    public CampType Camp { get => _camp; set => _camp = value; }
    public float Damage { get => _damage; set => _damage = Mathf.Max(0f, value); }
    public float Cooldown { get => _cooldown; set => _cooldown = Mathf.Max(0.01f, value); }
    public float Range { get => _range; set => _range = Mathf.Max(0.1f, value); }

    /// <summary>出膛点。没配就用自身位置。</summary>
    public Transform Muzzle => _muzzle != null ? _muzzle : transform;

    /// <summary>配置齐了没有。AI 用它决定「这个敌人会不会远程」。</summary>
    public bool CanFire => _projectilePrefab != null;
    public bool IsCharging => _charging;
    public float TelegraphSeconds => _telegraphSeconds;
    public event System.Action Fired;

    private void Awake()
    {
        _combat = GetComponent<CombatComponent>();
        _combat.SetOwner(_camp, null, GetComponent<HealthComponent>());
        _pool = GetComponent<ObjectPool>();
        if (_pool == null) _pool = gameObject.AddComponent<ObjectPool>();
        // 先造几颗放着，避免第一次开火时 Instantiate 造成帧尖峰。
        if (_projectilePrefab != null && _prewarm > 0) _pool.Prewarm(_projectilePrefab.gameObject, 4);
    }

    private void OnEnable() => _combat.Parried += OnParried;
    private void OnParried(ParryContact contact)
    {
        _charging = false;
        _readyAt = Mathf.Max(_readyAt, TimeManager.WorldTime + _cooldown);
        StopTelegraph();
    }

    private void OnDisable()
    {
        _combat.Parried -= OnParried;
        _combat.FinishAttack();
        // 回收进池：别把预警留着，下次复用会带着亮着的提示出场。
        _charging = false;
        StopTelegraph();
    }

    /// <summary>
    /// 尝试开火：查冷却、射程、目标存活，全过才生成子弹。
    /// now 由持有者按自己的时钟传（敌人传 TimeManager.WorldTime）。
    /// 所有前置检查都放在扣冷却之前，免得「够不着」也把冷却吃掉。
    /// cooldownScale 只缩放「本次开火之后」的冷却，默认 1 与原行为完全一致（残血保护用它）。
    /// 刻意不缩放 _telegraphSeconds：前摇期间每帧都要复验视线，拉长前摇会增加射线次数。
    /// </summary>
    public bool TryFire(float now, Transform target, HealthComponent targetHealth, float cooldownScale = 1f)
    {
        // 蓄力中也要先验证目标，不能等前摇结束才取消过期的瞄准。
        if (_charging)
        {
            if (target == null || targetHealth == null || !targetHealth.IsAlive || !CanFire
                || !IsInRange(target) || !HasLineOfSight(target))
            {
                CancelCharge();
                return false;
            }
            if (now < _chargeUntil) return false;
            _charging = false;
            _combat.FinishAttack();
            return Fire(target);
        }

        if (target == null || targetHealth == null || !targetHealth.IsAlive) return false;
        if (!CanFire) return false;
        if (now < _readyAt) return false;
        if (!IsInRange(target)) return false;

        // 隔墙不索敌：射程内但视线被挡就不开火，也不亮预警、不扣冷却。
        // 放在扣冷却之前——「看不到」不该算一次开火机会。
        if (!HasLineOfSight(target)) return false;

        _readyAt = now + Mathf.Max(0.01f, _cooldown * Mathf.Max(0f, cooldownScale));

        // 第一段：先开预警窗口（亮提示、扣冷却），窗口到点才出膛。
        // 没配预警时长（0）就按老规矩秒发。
        if (_telegraphSeconds > 0f)
        {
            _chargeUntil = now + _telegraphSeconds;
            _charging = true;
            _combat.ArmParryTarget();
            PlayTelegraph();
            return false;
        }

        return Fire(target);
    }

    public void CancelCharge()
    {
        if (!_charging) return;
        _charging = false;
        _combat.FinishAttack();
        StopTelegraph();
    }

    // 与原有射程保持同一口径：枪口到目标的水平距离，不受高度差影响。
    private bool IsInRange(Transform target)
    {
        Vector3 flat = SightCheck.AimPoint(target) - Muzzle.position;
        flat.y = 0f;
        return flat.sqrMagnitude <= _range * _range;
    }

    // 真正生成子弹：秒发路径和预警路径共用的出口。
    // 瞄准点在「出膛这一刻」重算 —— 策划案写的是「始终瞄准发射时人物所在位置」，不做追踪。
    private bool Fire(Transform target)
    {
        Vector3 origin = Muzzle.position;
        Vector3 toTarget = SightCheck.AimPoint(target) - origin;
        Vector3 direction = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : transform.forward;

        // 从池里取。池返回的实例一定是 active 的，且已回调过 IPooledObject.OnSpawnedFromPool。
        GameObject instance = _pool.Spawn(_projectilePrefab.gameObject, origin, Quaternion.identity);
        if (instance == null) return false;
        instance.GetComponent<Projectile>().Launch(direction, this);
        Fired?.Invoke();
        StopTelegraph();
        return true;
    }

    /// <summary>枪口到目标之间有没有被墙挡住。开火与索敌都用它，避免隔墙打空气。</summary>
    public bool HasLineOfSight(Transform target)
        => SightCheck.HasLineOfSight(Muzzle.position, target, transform, _sightMask, _sightHits);

    // 提示组件按需解析：编辑模式装配 / 自检用 AddComponent 搭的对象不跑 Awake。
    private void EnsureTelegraph()
    {
        if (_telegraph == null) _telegraph = GetComponent<AttackTelegraph>();
    }

    private void PlayTelegraph() { EnsureTelegraph(); _telegraph?.Play(); }
    private void StopTelegraph() { EnsureTelegraph(); _telegraph?.Stop(); }

}
