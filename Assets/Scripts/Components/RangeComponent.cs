using System;
using UnityEngine;

// 远程攻击层：与 CombatComponent 分工一致 —— 数值、状态、结算。
//   · 数值：伤害 / 冷却 / 射程 / 阵营
//   · 状态：冷却计时
//   · 结算：生成子弹 → 子弹命中后回报到这里 → 由这里做阵营过滤并扣血
//
// 刻意不含「什么时候开火」（输入或 AI）与「打中之后怎么表现」（震屏、音效）——
// 那两件事属于实体，通过 Landed 事件挂上来。与 CombatComponent 是同一个约定。
//
// 时钟由调用方传入：敌人走 WorldTime（时停就该冻住敌人），组件自己不取时钟。
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

    private float _readyAt = float.NegativeInfinity;
    // 子弹池：与 EnemySpawner 同一套路 —— 自己找，找不到就挂一个。
    // 子弹归还走 ObjectPool.TryReturnToOwningPool，所以发射方不必记录"这颗子弹来自哪个池"。
    private ObjectPool _pool;

    /// <summary>命中且真的扣了血时触发：目标、命中点、方向、实际伤害。</summary>
    public event Action<HealthComponent, Vector3, Vector3, float> Landed;

    public CampType Camp { get => _camp; set => _camp = value; }
    public float Damage { get => _damage; set => _damage = Mathf.Max(0f, value); }
    public float Cooldown { get => _cooldown; set => _cooldown = Mathf.Max(0.01f, value); }
    public float Range { get => _range; set => _range = Mathf.Max(0.1f, value); }

    /// <summary>出膛点。没配就用自身位置。</summary>
    public Transform Muzzle => _muzzle != null ? _muzzle : transform;

    /// <summary>配置齐了没有。AI 用它决定「这个敌人会不会远程」。</summary>
    public bool CanFire => _projectilePrefab != null;

    private void Awake()
    {
        _pool = GetComponent<ObjectPool>();
        if (_pool == null) _pool = gameObject.AddComponent<ObjectPool>();
        // 先造几颗放着，避免第一次开火时 Instantiate 造成帧尖峰。
        if (_projectilePrefab != null && _prewarm > 0) _pool.Prewarm(_projectilePrefab.gameObject, 4);
    }

    /// <summary>
    /// 尝试开火：查冷却、射程、目标存活，全过才生成子弹。
    /// now 由持有者按自己的时钟传（敌人传 TimeManager.WorldTime）。
    /// 所有前置检查都放在扣冷却之前，免得「够不着」也把冷却吃掉。
    /// </summary>
    public bool TryFire(float now, Transform target, HealthComponent targetHealth)
    {
        if (target == null || targetHealth == null || !targetHealth.IsAlive) return false;
        if (!CanFire) return false;
        if (now < _readyAt) return false;

        Vector3 origin = Muzzle.position;
        Vector3 aimPoint = AimPoint(target);
        Vector3 toTarget = aimPoint - origin;
        // 射程按「水平距离」算（与敌人的近战判定同口径），方向用完整三维（要能打高处的玩家）
        Vector3 flat = new Vector3(toTarget.x, 0f, toTarget.z);
        if (flat.magnitude > _range) return false;

        _readyAt = now + Mathf.Max(0.01f, _cooldown);

        // 朝「此刻目标位置」锁定：子弹有飞行时间，玩家靠速度躲开是设计的一部分，不做追踪。
        Vector3 direction = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : transform.forward;

        // 从池里取。池返回的实例一定是 active 的，且已回调过 IPooledObject.OnSpawnedFromPool。
        GameObject instance = _pool.Spawn(_projectilePrefab.gameObject, origin, Quaternion.identity);
        if (instance == null) return false;
        instance.GetComponent<Projectile>().Launch(direction, this);
        return true;
    }

    // 瞄准点：优先取目标身上「非触发」碰撞体的包围盒中心。
    // 不能直接用 target.position —— 角色的原点通常在脚底，直接瞄会朝地面打。
    // 用碰撞体中心还能自动适配不同体型，不必为每个角色填高度。
    private static Vector3 AimPoint(Transform target)
    {
        Collider[] colliders = target.GetComponentsInChildren<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            // 触发体是攻击判定盒之类，不代表身体，跳过。
            if (colliders[i].isTrigger) continue;
            return colliders[i].bounds.center;
        }
        // 没有可用碰撞体时退回原点，至少方向不会变成零向量。
        return target.position;
    }

    /// <summary>由 Projectile 命中后回调。阵营过滤与伤害结算都在这里，子弹不认识这些规则。</summary>
    public void ReportHit(HealthComponent target, Vector3 point, Vector3 direction)
    {
        if (target == null) return;

        // 同阵营不打。规则抄自 CombatComponent.TryHit：目标身上有 CombatComponent 且阵营相同 → 放过。
        // 目标没有 CombatComponent 时视为中立（例如训练靶子），可以被子弹打中。
        CombatComponent other = target.GetComponentInParent<CombatComponent>();
        if (other != null && other.Camp == _camp) return;

        // 走 IAttackDamageReceiver 这条正规入口：无敌帧、死亡事件、跳字都由 HealthComponent 处理。
        // ActionInstanceId 传 0 表示「不属于任何动作序列」——远程弹没有多段判定。
        var request = new AttackDamageRequest(_damage, point, direction, 0L, 0);
        AttackDamageResult result = target.ReceiveAttack(request);
        
        if (result.AppliedDamage > 0f) Landed?.Invoke(target, point, direction, result.AppliedDamage);
    }
}
