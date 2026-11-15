using System;
using UnityEngine;

// 可挂到任意实体的血量：玩家和敌人共用同一份规则，无敌帧语义不会各自漂移。
// 只管「数值 + 无敌帧」；死了之后干什么由持有者决定，本组件不反向认识战斗或动作系统。
[DisallowMultipleComponent]
public sealed class HealthComponent : MonoBehaviour, IAttackDamageReceiver
{
    [SerializeField, Min(1f)] private float _maxHealth = 100f;
    [SerializeField, Min(0f)] private float _invulnerableTime = 0.6f;

    private float _health;
    private float _invulnerableUntil = float.NegativeInfinity;
    private bool _started;

    public float MaxHealth => _maxHealth;
    public float Health { get { EnsureStarted(); return _health; } }
    public float HealthRatio => _maxHealth > 0f ? Mathf.Clamp01(_health / _maxHealth) : 0f;
    public bool IsAlive { get { EnsureStarted(); return _health > 0f; } }
    public bool IsInvulnerable => TimeManager.UnscaledTime < _invulnerableUntil;
    public float InvulnerableTime => _invulnerableTime;

    // 低频事件：血条刷新、伤害跳字、死亡表现、音效、任务判定。
    // 每帧轮询的读路径（HUD、AI 判死）直接读 Health / IsAlive，不要走事件。
    /// <summary>参数：自己、实际扣掉的血、命中点、命中方向。</summary>
    public event Action<HealthComponent, float, Vector3, Vector3> Damaged;
    /// <summary>任何实际的生命值变化，包括伤害、治疗和恢复。</summary>
    public event Action<HealthComponent> HealthChanged;
    public event Action<HealthComponent> Died;
    public event Action<HealthComponent> Revived;

    public float TakeDamage(float amount) => TakeDamage(amount, Vector3.zero, Vector3.zero);

    public AttackDamageResult ReceiveAttack(AttackDamageRequest request)
    {
        float applied = TakeDamage(request.Damage, request.Point, request.Direction);
        return new AttackDamageResult(applied, applied > 0f && !IsAlive);
    }

    /// <summary>返回实际扣掉的血；被无敌帧、已死亡或非正伤害挡下时返回 0。</summary>
    public float TakeDamage(float amount, Vector3 hitPoint, Vector3 hitDirection)
        => ApplyDamage(amount, hitPoint, hitDirection, false);

    // 裂隙惩罚不能被战斗无敌帧抵消，但仍走同一套受伤与死亡通知。
    public float TakeEnvironmentalDamage(float amount) => ApplyDamage(amount, Vector3.zero, Vector3.zero, true);

    private float ApplyDamage(float amount, Vector3 hitPoint, Vector3 hitDirection, bool bypassInvulnerability)
    {
        EnsureStarted();
        if (!IsAlive || amount <= 0f || (!bypassInvulnerability && IsInvulnerable)) return 0f;

        float applied = Mathf.Min(amount, _health);
        _health -= applied;
        // 所有受击入口共享无敌帧，避免同一帧被多个碰撞体重复扣血。
        _invulnerableUntil = TimeManager.UnscaledTime + _invulnerableTime;

        // 先报 Damaged：订阅者要在这里完成跳字与死亡表现（它需要命中点与方向）。
        Damaged?.Invoke(this, applied, hitPoint, hitDirection);
        HealthChanged?.Invoke(this);
        if (!IsAlive) Died?.Invoke(this);
        return applied;
    }

    public void Heal(float amount)
    {
        EnsureStarted();
        if (amount <= 0f) return;
        float next = Mathf.Min(_maxHealth, _health + amount);
        if (Mathf.Approximately(next, _health)) return;
        _health = next;
        HealthChanged?.Invoke(this);
    }

    public void Reset()
    {
        EnsureStarted();
        bool wasDead = _health <= 0f;
        float previous = _health;
        _health = _maxHealth;
        _invulnerableUntil = float.NegativeInfinity;
        if (!Mathf.Approximately(previous, _health)) HealthChanged?.Invoke(this);
        if (wasDead) Revived?.Invoke(this);
    }

    public void SetMaxHealth(float value)
    {
        _maxHealth = Mathf.Max(1f, value);
        Reset();
    }

    public void RestoreHealth(float amount)
    {
        EnsureStarted();
        bool wasDead = _health <= 0f;
        float previous = _health;
        _health = Mathf.Clamp(amount, 1f, _maxHealth);
        _invulnerableUntil = float.NegativeInfinity;
        if (!Mathf.Approximately(previous, _health)) HealthChanged?.Invoke(this);
        if (wasDead) Revived?.Invoke(this);
    }

    public void SetInvulnerableTime(float seconds) => _invulnerableTime = Mathf.Max(0f, seconds);

    // 立刻给一段无敌（parry 成功、事件演出这类「现在就生效」的场合）。
    // SetInvulnerableTime 只改默认值，改完不会追溯已经过去的时间点，所以不能拿来即时无敌。
    // 只延长不缩短：与命中时刷新的无敌帧取更晚的那个终点。
    public void GrantInvulnerability(float seconds)
    {
        EnsureStarted();
        float until = TimeManager.UnscaledTime + Mathf.Max(0f, seconds);
        if (until > _invulnerableUntil) _invulnerableUntil = until;
    }

    private void Awake() => EnsureStarted();

    // 惰性初始化：编辑模式的装配工具与自检用 AddComponent 建实例时不跑 Awake，
    // 补一次填满血，避免把「没初始化」误读成「0 血、已死亡」。
    private void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        _health = _maxHealth;
        _invulnerableUntil = float.NegativeInfinity;
    }
}
