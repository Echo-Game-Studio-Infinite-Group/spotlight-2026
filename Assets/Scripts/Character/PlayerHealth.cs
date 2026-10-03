using UnityEngine;

// 玩家生命值：挂在玩家身上，作为「玩家可承受伤害」的唯一入口。
// 与 GameManager.Player 的关系：GameManager 是跨场景的整局状态，本组件是场景内实体的自我描述。
// 敌人的伤害只认本组件，不去碰 GameManager —— 后者的 Instance getter 会在找不到实例时
// 自动 new 一个不跑 Awake 的空壳，Player 字段为 null，直接读写会抛 NullReferenceException。
[DisallowMultipleComponent]
public sealed class PlayerHealth : MonoBehaviour
{
    [SerializeField, Min(1f)] private float _maxHealth = 100f;
    // 无敌帧：受击后这段时间内免疫后续伤害，避免被敌人连击瞬间打空。
    // 与 Damageable 保持同一套做法（受击后开窗口、到期自动失效）。
    [SerializeField, Min(0f)] private float _invulnerableTime = 0.6f;
    private float _health;
    private float _invulnerableUntil = float.NegativeInfinity;

    public float MaxHealth => _maxHealth;
    public float Health => _health;
    public bool IsAlive => _health > 0f;
    public float InvulnerableTime => _invulnerableTime;
    public bool IsInvulnerable => TimeManager.UnscaledTime < _invulnerableUntil;

    public void SetInvulnerableTime(float seconds) => _invulnerableTime = Mathf.Max(0f, seconds);

    private void Awake()
    {
        _health = _maxHealth;
    }

    // 供装配工具与测试在 Awake 之外初始化
    public void Configure(float maxHealth)
    {
        _maxHealth = Mathf.Max(1f, maxHealth);
        _health = _maxHealth;
    }

    // 返回实际扣掉的血量；无敌帧内或已死时返回 0，调用方据此决定要不要播受击反馈
    public float TakeDamage(float amount)
    {
        if (!IsAlive || amount <= 0f) return 0f;
        if (IsInvulnerable) return 0f;

        float applied = Mathf.Min(amount, _health);
        _health -= applied;
        if (_invulnerableTime > 0f) _invulnerableUntil = TimeManager.UnscaledTime + _invulnerableTime;
        // 同步到整局状态，保证 DebugHUD 与结算读到的是一致的
        SyncToManager();
        Debug.Log($"[PlayerHealth] 玩家受到 {applied:0.#} 点伤害，剩余 {_health:0.#}/{_maxHealth:0.#}");
        if (!IsAlive) Debug.LogWarning("[PlayerHealth] 玩家血量归零");
        return applied;
    }

    public void ResetHealth()
    {
        _health = _maxHealth;
        _invulnerableUntil = float.NegativeInfinity;
        SyncToManager();
    }

    private void SyncToManager()
    {
        GameManager manager = FindObjectOfType<GameManager>();
        if (manager == null) return;
        manager.Player.MaxHealth = _maxHealth;
        manager.Player.Health = _health;
    }
}
