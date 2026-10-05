using System;
using UnityEngine;

// 可挂到任意实体的血量：玩家和敌人共用同一份规则，无敌帧语义不会各自漂移。
// 只管「数值 + 无敌帧」；死了之后干什么由持有者决定，本组件不反向认识战斗或动作系统。
// 敌人的死亡表现与 hitDirection 强绑定（要按方向切 Gib），留在 Enemy.TakeDamage 里更直接；
// 玩家的死亡通知是真正跨系统的，由订阅 Died / Revived 的一方处理。
[DisallowMultipleComponent]
public sealed class HealthComponent : MonoBehaviour
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

    // 低频事件：血条刷新、死亡表现、音效、任务判定。
    // 每帧轮询的读路径（HUD、AI 判死）直接读 Health / IsAlive，不要走事件。
    public event Action<HealthComponent, float> Damaged;   // 参数：自己、实际扣掉的血
    public event Action<HealthComponent> Died;
    public event Action<HealthComponent> Revived;

    /// <summary>返回实际扣掉的血；被无敌帧、已死亡或非正伤害挡下时返回 0。</summary>
    public float TakeDamage(float amount)
    {
        EnsureStarted();
        if (!IsAlive || amount <= 0f || IsInvulnerable) return 0f;

        float applied = Mathf.Min(amount, _health);
        _health -= applied;
        // 所有受击入口共享无敌帧，避免同一帧被多个碰撞体重复扣血。
        _invulnerableUntil = TimeManager.UnscaledTime + _invulnerableTime;

        Damaged?.Invoke(this, applied);
        if (!IsAlive) Died?.Invoke(this);
        return applied;
    }

    public void Heal(float amount)
    {
        EnsureStarted();
        if (amount <= 0f) return;
        _health = Mathf.Min(_maxHealth, _health + amount);
    }

    public void Reset()
    {
        EnsureStarted();
        bool wasDead = _health <= 0f;
        _health = _maxHealth;
        _invulnerableUntil = float.NegativeInfinity;
        if (wasDead) Revived?.Invoke(this);
    }

    public void SetMaxHealth(float value)
    {
        _maxHealth = Mathf.Max(1f, value);
        Reset();
    }

    public void SetInvulnerableTime(float seconds) => _invulnerableTime = Mathf.Max(0f, seconds);

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
