using System.Collections.Generic;
using UnityEngine;

// 攻击方阵营。命名与职责照抄参考实现 LittleAdventure 的 CampaignType。
public enum CampType
{
    Player,
    Enemy,
}

// 伤害来源：Hitbox 只负责「碰到谁」，打多少由持有者的伤害来源决定。
public interface IDamageSource
{
    float AttackDamage { get; }
}

// 攻击判定盒 —— 照参考实现 LittleAdventure 的 Hitbox.cs 写：
//   · 作为子物体挂在武器/身上，配一个 Trigger Collider，默认关闭
//   · 判定完全交给 OnTriggerEnter：目标撞进来才结算
//   · 开关由动画事件 EnableHitbox / DisableHitbox 在挥砍帧控制
//
// ⚠️ 这里**不要**再加 FixedUpdate 里的周期性重叠检测。
// 参考实现只靠 OnTriggerEnter，一次挥砍最多结算一次；改成轮询会让贴着不动就一直掉血。
[DisallowMultipleComponent]
public sealed class Hitbox : MonoBehaviour
{
    [SerializeField] private CampType _campType = CampType.Player;
    [SerializeField] private MonoBehaviour _damageSourceBehaviour;
    [SerializeField, Min(0f)] private float _knockbackStrength = 1f;
    [SerializeField] private bool _logHits;

    private IDamageSource _damageSource;
    private Collider _collider;
    private PlayerCombat _combat;
    private bool _combatResolved;
    private readonly HashSet<int> _hitTargets = new HashSet<int>();

    public CampType Camp => _campType;

    public void Configure(CampType campType, MonoBehaviour damageSource, float knockbackStrength)
    {
        _campType = campType;
        SetDamageSource(damageSource);
        _knockbackStrength = Mathf.Max(0f, knockbackStrength);
    }

    public void SetDamageSource(MonoBehaviour source)
    {
        _damageSourceBehaviour = source;
        _damageSource = source as IDamageSource;
    }

    private void Awake()
    {
        // 静态敌人只有 Collider，武器必须提供运动学刚体才能收到 Trigger 回调。
        Rigidbody body = GetComponent<Rigidbody>();
        if (body == null) body = gameObject.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        _collider = GetComponent<Collider>();
        if (_damageSourceBehaviour != null) _damageSource = _damageSourceBehaviour as IDamageSource;
        // 默认关闭：等动画事件来开。忘了关会让敌人一进场就被打。
        if (_collider != null) _collider.enabled = false;
    }

    // 动画事件调用：挥砍起手
    public void EnableHitbox()
    {
        if (_collider == null) _collider = GetComponent<Collider>();
        if (_collider == null || _collider.enabled) return;
        _hitTargets.Clear();
        _collider.isTrigger = true;
        _collider.enabled = true;
    }

    // 动画事件调用：收招
    public void DisableHitbox()
    {
        if (_collider == null) _collider = GetComponent<Collider>();
        if (_collider != null) _collider.enabled = false;
    }

    private void OnDisable() => DisableHitbox();

    // 目标撞进判定盒 —— 唯一的判定入口
    private void OnTriggerEnter(Collider other)
    {
        if (other == null || _collider == null || !_collider.enabled || !isActiveAndEnabled) return;

        IDamageSource source = ResolveDamageSource();
        if (source == null) return;

        float damage = source.AttackDamage;
        if (damage <= 0f) return;

        if (_campType == CampType.Player) StrikePlayerCamp(other, damage);
        else StrikeEnemyCamp(other, damage);
    }

    // 兜底解析伤害来源：组件初始化顺序不保证，Awake 时可能还拿不到持有者
    private IDamageSource ResolveDamageSource()
    {
        if (_damageSource != null) return _damageSource;
        if (_damageSourceBehaviour != null)
        {
            _damageSource = _damageSourceBehaviour as IDamageSource;
            if (_damageSource != null) return _damageSource;
        }

        _damageSource = GetComponentInParent<IDamageSource>();
        if (_damageSource == null)
        {
            Debug.LogError($"[Hitbox] {name} 找不到伤害来源（IDamageSource），命中不会结算伤害");
        }

        return _damageSource;
    }

    // 玩家打敌人：目标是实现了 IDamageable 的实体
    private void StrikePlayerCamp(Collider other, float damage)
    {
        Damageable target = other.GetComponentInParent<Damageable>();
        if (target == null || !target.IsAlive) return;
        if (_damageSourceBehaviour != null && target.gameObject == _damageSourceBehaviour.gameObject) return;
        if (!_hitTargets.Add(target.GetInstanceID())) return;

        ApplyDamage(target, damage, other);
    }

    // 敌人打玩家：目标带 Player（玩家走 GameManager.Player，不走 IDamageable）
    private void StrikeEnemyCamp(Collider other, float damage)
    {
        Player marker = other.GetComponentInParent<Player>();
        if (marker == null) return;
        if (!_hitTargets.Add(marker.GetInstanceID())) return;

        PlayerData health = GameManager.Instance.Player;
        if (health == null || !health.IsAlive) return;

        float applied = health.TakeDamage(damage);
        if (applied > 0f) CameraShaker.Shake(0.35f);
        if (_logHits) Debug.Log($"[Hitbox] {name} 命中玩家 {applied:0.#}");
    }

    private void ApplyDamage(Damageable target, float damage, Collider other)
    {
        // 命中点用对方包围盒上离本盒最近的点，跳字才会落在被打的那一侧
        Vector3 hitPoint = other.ClosestPoint(transform.position);
        Vector3 direction = (target.transform.position - transform.position).normalized;
        if (direction.sqrMagnitude < 0.0001f) direction = transform.forward;

        float applied = target.TakeDamage(damage, hitPoint, direction);
        // 无敌帧挡下时 applied 为 0：不播击退、不播打击感
        if (applied <= 0f) return;

        if (target is Enemy enemy) enemy.ApplyKnockback(direction, _knockbackStrength);
        // 打击感（时间减速等）由玩家的战斗组件统一负责
        if (ResolveCombat() is PlayerCombat combat) combat.OnLandedHit(target, hitPoint, direction, applied);
        if (_logHits) Debug.Log($"[Hitbox] {name} 命中 {target.name} 伤害 {applied:0.#}");
    }

    // 惰性解析：Hitbox 是手部子物体，PlayerCombat 在根节点上
    private PlayerCombat ResolveCombat()
    {
        if (!_combatResolved)
        {
            _combat = GetComponentInParent<PlayerCombat>();
            _combatResolved = true;
        }

        return _combat;
    }
}
