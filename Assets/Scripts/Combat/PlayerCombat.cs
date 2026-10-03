using UnityEngine;

// 玩家攻击：消费输入 → 开攻击窗口 → 由**动画事件**开关 Hitbox → 结算伤害与打击感。
//
// 判定方式的演进（重要）：
//   最初用 SphereCast 每帧扫描，问题是要靠 _hitWindowStart 那类时间参数去「猜」判定时机，
//   而且会隔墙打到、会把玩家自己的碰撞体扫进来。
//   现在沿用参考实现 LittleAdventure 的做法：Hitbox 作为子物体挂在手上，默认关闭，
//   由动画事件 EnableHitbox / DisableHitbox 在挥砍帧精确开关 —— 判定时机完全由动画决定，
//   前摇不打人、后摇不打人，表现与判定天然一致。
[RequireComponent(typeof(CharacterController))]
[DisallowMultipleComponent]
public sealed class PlayerCombat : MonoBehaviour
{
    [Header("攻击")]
    [SerializeField, Min(0f)] private float _attackDamage = 25f;
    // 攻击窗口 = 动画播放时长，由装配工具从 Attack.fbx 量出后写入。
    // 窗口关掉会让 IsAttacking 转假，状态机立刻退出攻击状态，动画就被掐断。
    [SerializeField, Min(0f)] private float _attackDuration = 1.6f;
    [SerializeField, Min(0f)] private float _attackCooldown = 0.35f;

    [Header("打击感")]
    [SerializeField, Min(0f)] private float _hitStopSeconds = 0.2f;
    [SerializeField, Range(0.05f, 1f)] private float _hitTimeScale = 0.75f;
    // 单纯 0.2s @ 0.75x 只比常速省 50ms，人眼几乎察觉不到。
    // 因此命中瞬间先来一段更强更短的冲击，再落回 _hitTimeScale 的尾巴。
    [SerializeField, Min(0f)] private float _impactSeconds = 0.09f;
    [SerializeField, Range(0.05f, 1f)] private float _impactTimeScale = 0.35f;

    [Header("引用")]
    [SerializeField] private Hitbox _hitbox;
    [SerializeField] private PlayerVFXManager _vfx;
    [SerializeField] private bool _logHits;

    private PlayerInputReader _input;
    private PlayerMotor _motor;
    private float _windowEnd = float.NegativeInfinity;
    private float _readyAt = float.NegativeInfinity;

    /// <summary>Hitbox 从这里取伤害值，因此攻击力只有一处真值。</summary>
    public float AttackDamage => _attackDamage;
    public float AttackDuration => _attackDuration;
    public float AttackCooldown => _attackCooldown;
    public float HitStopSeconds => _hitStopSeconds;
    public float HitTimeScale => _hitTimeScale;
    public float ImpactSeconds => _impactSeconds;
    public float ImpactTimeScale => _impactTimeScale;
    public Hitbox Hitbox => _hitbox;

    // 判定射程由 Hitbox 的 Trigger Collider 决定，这里从它反推出来供诊断显示。
    // 用通用 Collider.bounds：判定盒是球还是盒都不影响这段计算。
    public float AttackRange
    {
        get
        {
            if (_hitbox == null) return 0f;
            Collider box = _hitbox.GetComponent<Collider>();
            if (box == null) return 0f;
            return Mathf.Max(0f,
                Vector3.Distance(transform.position, box.bounds.center) + box.bounds.extents.magnitude);
        }
    }

    // 攻击相关的所有计时统一走 UnscaledTime：
    //   · InputBuffer 内部按 TimeManager.UnscaledTime 记录按下时刻，消费时用同一时钟才不会「刚按下就过期」；
    //   · TimeManager 的减速窗口也是按 UnscaledTime 记的，这样「窗口开着」的判定与写入必然一致。
    public bool IsAttacking => TimeManager.UnscaledTime < _windowEnd;

    public void Configure(float damage, float duration, float hitStopSeconds, float hitTimeScale)
    {
        _attackDamage = Mathf.Max(0f, damage);
        _attackDuration = Mathf.Max(0f, duration);
        _hitStopSeconds = Mathf.Max(0f, hitStopSeconds);
        _hitTimeScale = Mathf.Clamp(hitTimeScale, 0.05f, 1f);
        SyncHitboxDamage();
    }

    public void SetReferences(Hitbox hitbox, PlayerVFXManager vfx)
    {
        _hitbox = hitbox;
        _vfx = vfx;
        SyncHitboxDamage();
    }

    // 攻击窗口时长跟随动画片段：片段多长，窗口就多长，动画才不会被掐断。
    public void SetAttackDuration(float seconds) => _attackDuration = Mathf.Max(0.05f, seconds);

    private void Awake()
    {
        EnsureReferences();
        SyncHitboxDamage();
    }

    // 惰性解析：Awake 不保证跑过（编辑模式装配工具、测试里 AddComponent 都不会触发它）。
    // 所以引用为空时必须能重新找到，不能只在 Awake 里缓存一次。
    private void EnsureReferences()
    {
        if (_motor == null) _motor = GetComponent<PlayerMotor>();
        if (_input == null) _input = GetComponent<PlayerInputReader>();
        if (_hitbox == null) _hitbox = GetComponentInChildren<Hitbox>(true);
        if (_vfx == null) _vfx = GetComponentInChildren<PlayerVFXManager>(true);
    }

    private void SyncHitboxDamage()
    {
        if (_hitbox != null) _hitbox.Configure(CampType.Player, this);
    }

    private void OnDisable() => ResetAttackState();

    private void Update()
    {
        // 攻击被状态切换或计时结束打断时，收招事件可能来不及播放。
        if (!IsAttacking && _hitbox != null) _hitbox.DisableHitbox();
    }

    private void FixedUpdate()
    {
        if (_input == null || !_input.GameplayEnabled) return;
        TickAttack(TimeManager.UnscaledTime);
    }

    // 一帧战斗逻辑：消费输入 + 开窗口。判定交给动画事件驱动的 Hitbox，这里不再做物理扫描。
    public void TickAttack(float now)
    {
        // 攻击窗口内不重复响应输入：挥砍播完之前按左键无效，动画才不会被新攻击打断。
        // 输入仍留在 InputBuffer 里，窗口结束的那一帧会被消费，形成自然的连击节奏。
        if (IsAttacking) return;
        if (_input != null && _input.ConsumeAttack(now)) BeginAttack(now);
    }

    public bool BeginAttack(float now)
    {
        if (!enabled || now < _readyAt) return false;
        EnsureReferences();
        // 开窗前重新绑定伤害来源，避免初始化顺序问题导致 Hitbox 拿不到攻击力
        SyncHitboxDamage();
        _readyAt = now + _attackCooldown;
        _windowEnd = now + _attackDuration;
        return true;
    }

    // ===== 动画事件入口（与参考实现同名，动画事件可直接对接）=====

    /// <summary>挥砍起手：由 Attack 动画的动画事件调用。</summary>
    public void EnableHitbox()
    {
        if (!IsAttacking) return;
        EnsureReferences();
        if (_hitbox != null) _hitbox.EnableHitbox();
        else Debug.LogError("[PlayerCombat] 没有判定盒，挥砍不会造成伤害");
    }

    /// <summary>收招：由 Attack 动画的动画事件调用。</summary>
    public void DisableHitbox()
    {
        EnsureReferences();
        if (_hitbox != null) _hitbox.DisableHitbox();
    }

    public void FinishAttack()
    {
        _windowEnd = float.NegativeInfinity;
        DisableHitbox();
    }

    /// <summary>攻击特效：第 cnt 段。由动画事件调用，与参考实现 UpdateAttack(int) 同名。</summary>
    public void UpdateAttack(int cnt = 1)
    {
        EnsureReferences();
        if (_vfx != null) _vfx.UpdateAttack(cnt);
        else Debug.LogWarning($"[PlayerCombat] 没有配置攻击特效管理器，第 {cnt} 段特效未播放");
    }

    // 复位攻击状态：清掉冷却与窗口。
    // 连续两次自检/测试之间必须调用，否则上一轮的冷却会让 BeginAttack 失败。
    public void ResetAttackState()
    {
        _readyAt = float.NegativeInfinity;
        _windowEnd = float.NegativeInfinity;
        DisableHitbox();
    }

    // ===== 打击感 =====
    // 由 Hitbox 在成功造成伤害后回调（伤害被无敌帧挡下时不会走到这里）
    public void OnLandedHit(Enemy target, Vector3 point, Vector3 direction, float applied)
    {
        if (applied <= 0f) return;
        if (_logHits)
        {
            Debug.Log($"[PlayerCombat] 命中 {target.name} 伤害 {applied:0.#}，剩余 {target.Health:0.#}/{target.MaxHealth:0.#}");
        }

        // 先叠一层短促强冲击，再压上 0.75x 的尾巴。
        // TimeManager 的规则是「取更强的那一档、按更长的时间延长」，两段自然合成
        // 「瞬间 0.35x → 剩余 0.75x」的手感，不需要额外缓动曲线。
        if (_impactSeconds > 0f) TimeManager.SlowMotion(_impactSeconds, _impactTimeScale);
        TimeManager.SlowMotion(_hitStopSeconds, _hitTimeScale);
    }
}
