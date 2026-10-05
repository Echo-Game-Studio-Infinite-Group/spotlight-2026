using UnityEngine;
using GameJam.Actions;

// 动作序列接入时只有逻辑帧事件能开关判定；旧输入、秒计时和动画事件仅作为未接入角色的兼容路径。
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
    [SerializeField] private bool _logHits;

    private PlayerInputReader _input;
    private PlayerMotor _motor;
    private float _windowEnd = float.NegativeInfinity;
    private float _readyAt = float.NegativeInfinity;
    private long _sequenceInstance;
    private ActionCombatSettings _sequenceCombat;
    public bool IsSequenceDriven { get; private set; }

    /// <summary>Hitbox 从这里取伤害值，因此攻击力只有一处真值。</summary>
    public float AttackDamage => _sequenceCombat != null ? _sequenceCombat.Damage : _attackDamage;
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

    // 旧攻击兼容路径的计时走 UnscaledTime：
    //   · InputBuffer 内部按 TimeManager.UnscaledTime 记录按下时刻，消费时用同一时钟才不会「刚按下就过期」；
    //   · TimeManager 的减速窗口也是按 UnscaledTime 记的，这样「窗口开着」的判定与写入必然一致。
    public bool IsAttacking => IsSequenceDriven ? _sequenceInstance != 0 : TimeManager.UnscaledTime < _windowEnd;

    public void Configure(float damage, float duration, float hitStopSeconds, float hitTimeScale)
    {
        _attackDamage = Mathf.Max(0f, damage);
        _attackDuration = Mathf.Max(0f, duration);
        _hitStopSeconds = Mathf.Max(0f, hitStopSeconds);
        _hitTimeScale = Mathf.Clamp(hitTimeScale, 0.05f, 1f);
        SyncHitboxDamage();
    }

    public void SetReferences(Hitbox hitbox)
    {
        _hitbox = hitbox;
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
        if (IsSequenceDriven || _input == null || !_input.GameplayEnabled) return;
        TickAttack(TimeManager.UnscaledTime);
    }

    // 一帧战斗逻辑：消费输入 + 开窗口。判定交给动画事件驱动的 Hitbox，这里不再做物理扫描。
    public void TickAttack(float now)
    {
        if (IsSequenceDriven) return;
        // 攻击窗口内不重复响应输入：挥砍播完之前按左键无效，动画才不会被新攻击打断。
        // 输入仍留在 InputBuffer 里，窗口结束的那一帧会被消费，形成自然的连击节奏。
        if (IsAttacking) return;
        if (_input != null && _input.ConsumeAttack(now)) BeginAttack(now);
    }

    public bool BeginAttack(float now)
    {
        if (IsSequenceDriven || !enabled || now < _readyAt) return false;
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
        if (IsSequenceDriven) return;
        if (!IsAttacking) return;
        EnsureReferences();
        if (_hitbox != null) _hitbox.EnableHitbox();
        else Debug.LogError("[PlayerCombat] 没有判定盒，挥砍不会造成伤害");
    }

    /// <summary>收招：由 Attack 动画的动画事件调用。</summary>
    public void DisableHitbox()
    {
        if (IsSequenceDriven) return;
        EnsureReferences();
        if (_hitbox != null) _hitbox.DisableHitbox();
    }

    public void FinishAttack()
    {
        if (IsSequenceDriven) return;
        _windowEnd = float.NegativeInfinity;
        DisableHitbox();
    }

    // 复位攻击状态：清掉冷却与窗口。
    // 连续两次自检/测试之间必须调用，否则上一轮的冷却会让 BeginAttack 失败。
    public void ResetAttackState()
    {
        _readyAt = float.NegativeInfinity;
        _windowEnd = float.NegativeInfinity;
        _sequenceInstance = 0;
        _sequenceCombat = null;
        EnsureReferences();
        if (_hitbox != null) { _hitbox.EndSequence(); _hitbox.DisableHitbox(); }
    }

    public void SetSequenceDriven(bool driven)
    {
        ResetAttackState();
        IsSequenceDriven = driven;
    }
    public void BeginSequenceAction(ActionExecutionState state)
    {
        if (!IsSequenceDriven || state.Action?.Combat == null || !state.Action.Combat.Enabled) return;
        EnsureReferences();
        SyncHitboxDamage();
        _sequenceInstance = state.InstanceId;
        _sequenceCombat = state.Action.Combat;
        if (_hitbox != null) _hitbox.BeginSequence(_sequenceInstance, _sequenceCombat.HitMask);
    }
    public void HandleSequenceEvent(ActionExecutionState state, ActionFrameEvent frameEvent)
    {
        if (!IsSequenceDriven || _sequenceInstance != state.InstanceId || _hitbox == null) return;
        if (frameEvent.EventKey == "combat.hitbox.open") _hitbox.OpenSequenceWindow(frameEvent.HitGroup);
        else if (frameEvent.EventKey == "combat.hitbox.close") _hitbox.CloseSequenceWindow();
    }
    public void SampleSequenceHitbox(long instanceId)
    {
        if (IsSequenceDriven && _sequenceInstance == instanceId && _hitbox != null) _hitbox.SampleSequenceWindow();
    }
    public void EndSequenceAction(long instanceId)
    {
        if (_sequenceInstance != instanceId) return;
        _sequenceInstance = 0;
        _sequenceCombat = null;
        if (_hitbox != null) _hitbox.EndSequence();
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
