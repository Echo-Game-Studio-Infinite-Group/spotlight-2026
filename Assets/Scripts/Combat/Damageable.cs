using System;
using UnityEngine;

// 可受击实体：血量 / 攻击力 / 速度这类战斗属性的载体，实现 IDamageable 供攻击检测使用。
// 一次成功命中在同一个地方统一触发三件事——扣血、伤害跳字、震屏，
// 分散到调用方会让「打击感」在换武器/换敌人时各处不一致。
[DisallowMultipleComponent]
public class Damageable : MonoBehaviour, IDamageable
{
    [Header("战斗属性")]
    [SerializeField, Min(1f)] private float _maxHealth = 100f;

    [Header("打击反馈")]
    [SerializeField] private Color _damageTextColor = new Color(1f, 0.85f, 0.2f);
    [SerializeField, Min(0f)] private float _shakeAmplitude = 0.6f;
    // 无敌帧：受击后这段时间内免疫后续伤害，避免同一刀/连击把血瞬间打空。
    // 取自参考实现 LittleAdventure 的做法（isHurting + invulnerableTimer 计时）。
    [SerializeField, Min(0f)] private float _invulnerableTime = 0.45f;
    [SerializeField, Min(0.1f)] private float _numberLifetime = 1.2f;
    [SerializeField, Min(0f)] private float _riseSpeed = 2.2f;
    [SerializeField] private Transform _numberAnchor;
    // 跳字组件原型：在 Inspector 里挂一个不激活的 DamagePopup 实例。
    // 命中时从池里借一个复用，不再运行时动态生成对象。
    [SerializeField] private DamagePopup _damagePopup;
    [SerializeField, Min(1)] private int _popupPoolSize = 3;

    private float _health;
    private float _invulnerableUntil = float.NegativeInfinity;
    private DamagePopup[] _popupPool = System.Array.Empty<DamagePopup>();
    private int _nextPopup;

    public event Action<Damageable, float> Damaged;
    public event Action<Damageable> Died;

    public float MaxHealth => _maxHealth;
    public float Health => _health;
    public float HealthRatio => _maxHealth > 0f ? _health / _maxHealth : 0f;
    public bool IsAlive => _health > 0f;
    public float ShakeAmplitude => _shakeAmplitude;
    public DamagePopup DamagePopupTemplate => _damagePopup;
    public float InvulnerableTime => _invulnerableTime;

    // 无敌帧中：受击被忽略。用「到期时刻」而不是布尔 + 每帧累加，
    // 免得忘记递减时无敌永久生效（参考实现用布尔 + 计时器，这里更不容易出错）。
    public bool IsInvulnerable => TimeManager.UnscaledTime < _invulnerableUntil;

    // 受击次数：自检与测试用它判断「这一轮到底有没有打中」，比读血量更能区分「没命中」与「命中但血量没变」
    public int DamagedCount { get; private set; }

    // 供装配工具注入跳字组件（预制体与场景对象的序列化字段由它统一写入）
    public void SetDamagePopup(DamagePopup template)
    {
        _damagePopup = template;
    }

    public void SetMaxHealth(float value)
    {
        _maxHealth = Mathf.Max(1f, value);
        // 同时回满血：调用方可能是编辑模式下的装配工具，那里 AddComponent 不会触发 Awake，
        // 只改上限会让 _health 停在 0，实体一开始就是「已死」状态。
        _health = _maxHealth;
    }

    // 子类钩子：基类负责「扣血 + 跳字 + 震屏」，子类只关心自己的额外反应
    protected virtual void Awake()
    {
        _health = _maxHealth;
        _numberAnchor = _numberAnchor != null ? _numberAnchor : transform;
        InitializePopupPool();
    }

    // 从池里备好复用实例。池在 DamagePopup 内部维护，同场景的实体共用同一批原型，
    // 因此这里只借引用、不创建对象。
    // 注意：这里的日志都不要带 context 对象 —— 带上下文时 MCP 的日志读取会漏掉这条，
    // 排查「为什么没日志」时踩过这个坑。
    private void InitializePopupPool()
    {
        if (_damagePopup == null)
        {
            Debug.LogError($"[Damageable] {name} 没有配置跳字组件，伤害数字不会显示。"
                + "请在 Inspector 上把 DamagePopup 实例挂到 DamagePopup 字段");
            return;
        }

        DamagePopup.RegisterTemplate(_damagePopup);
        _popupPool = DamagePopup.AcquirePool(_popupPoolSize);
        _damagePopup.gameObject.SetActive(false);
        Debug.Log($"[Damageable] {name} 跳字池就绪: 请求={_popupPoolSize} 实得={_popupPool.Length}");
    }

    public float TakeDamage(float amount, Vector3 hitPoint, Vector3 hitDirection)
    {
        if (!IsAlive || amount <= 0f) return 0f;
        // 无敌帧内直接免疫：返回 0 让调用方知道「没造成伤害」，不触发减速与震屏
        if (IsInvulnerable) return 0f;

        float applied = Mathf.Min(amount, _health);
        _health -= applied;
        DamagedCount++;
        if (_invulnerableTime > 0f) _invulnerableUntil = TimeManager.UnscaledTime + _invulnerableTime;

        // 跳字落在受击点上方：命中点常常就在敌人躯干表面，直接从那里冒出会被模型挡住
        Vector3 basePoint = hitPoint != Vector3.zero ? hitPoint : _numberAnchor.position;
        Vector3 spawnPoint = basePoint + Vector3.up * 0.5f;
        PlayDamageNumber(applied, spawnPoint);
        CameraShaker.Shake(_shakeAmplitude);

        Damaged?.Invoke(this, applied);
        OnDamaged(applied);
        if (!IsAlive)
        {
            OnDied();
            Died?.Invoke(this);
        }

        return applied;
    }

    // 播放一次跳字：从池里借实例复用，零分配。
    // 没配置跳字组件时不静默失败，而是明确报错 —— 跳字缺失属于接线问题，必须暴露出来。
    private void PlayDamageNumber(float amount, Vector3 spawnPoint)
    {
        if (_popupPool.Length == 0)
        {
            Debug.LogError($"[Damageable] {name} 没有可用的跳字组件，伤害数字不会显示。"
                + "请在 Inspector 上把 DamagePopup 实例挂到 DamagePopup 字段");
            return;
        }

        DamagePopup popup = _popupPool[_nextPopup % _popupPool.Length];
        _nextPopup++;
        if (popup == null)
        {
            Debug.LogError($"[Damageable] {name} 的跳字池里有已销毁的实例，请重新装配");
            return;
        }

        popup.EnsureFont();
        popup.Show(amount, _damageTextColor, spawnPoint, _numberAnchor, Camera.main, _numberLifetime, _riseSpeed);
    }

    // 供装配工具与测试写入战斗数值后重置为满血
    public void ResetHealth()
    {
        _health = _maxHealth;
        _invulnerableUntil = float.NegativeInfinity;
        gameObject.SetActive(true);
    }

    // 供装配工具写入无敌帧时长
    public void SetInvulnerableTime(float seconds) => _invulnerableTime = Mathf.Max(0f, seconds);

    // 子类扩展点：默认什么都不做，只有需要的实体才覆写
    protected virtual void OnDamaged(float applied) { }
    protected virtual void OnDied() { }

}
