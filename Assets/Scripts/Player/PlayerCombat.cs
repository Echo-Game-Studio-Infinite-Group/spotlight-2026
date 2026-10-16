using UnityEngine;
using GameJam.Actions;

// 玩家的战斗决策层：只负责「什么时候攻击」与「打中之后怎么表现」。
//   · 输入消费 → 调 CombatComponent.TryBeginAttack
//   · 动作序列 → 翻译成原语转发给 CombatComponent
//   · 命中表现 → 订阅 Landed 做顿帧
// 数值（伤害/冷却/窗口）、状态机、判定盒开关、命中结算都在 CombatComponent 上，与敌人共用同一份。
//
// 时钟走 UnscaledTime：
//   · InputBuffer 内部按 TimeManager.UnscaledTime 记录按下时刻，消费时用同一时钟才不会「刚按下就过期」；
//   · TimeManager 的减速窗口也是按 UnscaledTime 记的，这样「窗口开着」的判定与写入必然一致。
[RequireComponent(typeof(CharacterController))]
[DisallowMultipleComponent]
public sealed class PlayerCombat : MonoBehaviour
{
    [Header("打击感")]
    [SerializeField, Min(0f)] private float _hitStopSeconds = 0.2f;
    [SerializeField, Range(0.05f, 1f)] private float _hitTimeScale = 0.75f;
    // 单纯 0.2s @ 0.75x 只比常速省 50ms，人眼几乎察觉不到。
    // 因此命中瞬间先来一段更强更短的冲击，再落回 _hitTimeScale 的尾巴。
    [SerializeField, Min(0f)] private float _impactSeconds = 0.09f;
    [SerializeField, Range(0.05f, 1f)] private float _impactTimeScale = 0.35f;

    [SerializeField] private bool _logHits;

    // 引用不进序列化：战斗层在自己身上，判定盒在自己的子节点上，GetComponent 就能查到。
    private CombatComponent _combat;
    private PlayerInputReader _input;
    private PlayerMotor _motor;
    private Hitbox _hitbox;
    private HealthComponent _self;
    private long _sequenceInstance;
    private ActionCombatSettings _sequenceCombat;
    private readonly ActionHitboxSampler _sampler = new ActionHitboxSampler();
    public event System.Action<IAttackDamageReceiver, AttackDamageRequest, AttackDamageResult> SequenceHitLanded;
    public bool IsSequenceDriven { get; private set; }

    /// <summary>判定盒从这里取伤害值，因此攻击力只有一处真值（序列招式会临时覆盖组件上的值）。</summary>
    public float AttackDamage { get { return Combat != null ? Combat.Damage : 0f; } }
    public float AttackDuration { get { return Combat != null ? Combat.WindowSeconds : 0f; } }
    public float AttackCooldown { get { return Combat != null ? Combat.Cooldown : 0f; } }
    public float HitStopSeconds => _hitStopSeconds;
    public float HitTimeScale => _hitTimeScale;
    public float ImpactSeconds => _impactSeconds;
    public float ImpactTimeScale => _impactTimeScale;
    public Hitbox Hitbox { get { EnsureReferences(); return _hitbox; } }

    private CombatComponent Combat { get { EnsureReferences(); return _combat; } }

    public bool IsAttacking => Combat != null && Combat.IsAttacking(TimeManager.UnscaledTime);

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

    public void Configure(float damage, float duration, float hitStopSeconds, float hitTimeScale)
    {
        _hitStopSeconds = Mathf.Max(0f, hitStopSeconds);
        _hitTimeScale = Mathf.Clamp(hitTimeScale, 0.05f, 1f);
        if (Combat != null)
        {
            Combat.Damage = damage;
            Combat.WindowSeconds = duration;
        }
        EnsureReferences();
    }

    public void SetReferences(Hitbox hitbox)
    {
        _hitbox = hitbox;
        EnsureReferences();
    }

    // 攻击窗口时长跟随动画片段：片段多长，窗口就多长，动画才不会被掐断。
    public void SetAttackDuration(float seconds)
    {
        if (Combat != null) Combat.WindowSeconds = Mathf.Max(0.05f, seconds);
    }

    private void Awake()
    {
        EnsureReferences();
    }

    private void OnEnable()
    {
        EnsureReferences();
        if (_combat != null) { _combat.Landed += OnLandedHit; _combat.SequenceLanded += OnSequenceLanded; }
    }

    private void OnDisable()
    {
        if (_combat != null) { _combat.Landed -= OnLandedHit; _combat.SequenceLanded -= OnSequenceLanded; }
        ResetAttackState();
    }

    // 惰性解析：Awake 不保证跑过（编辑模式装配工具、测试里 AddComponent 都不会触发它）。
    // 所以引用为空时必须能重新找到，不能只在 Awake 里缓存一次。
    private void EnsureReferences()
    {
        if (_motor == null) _motor = GetComponent<PlayerMotor>();
        if (_input == null) _input = GetComponent<PlayerInputReader>();
        if (_self == null) _self = GetComponent<HealthComponent>();
        if (_hitbox == null) _hitbox = GetComponentInChildren<Hitbox>(true);
        if (_combat == null) _combat = GetComponent<CombatComponent>();
        // 没有落盘战斗层的宿主（测试、自检用 AddComponent 搭的临时玩家）补一个。
        // 补出来的组件用 CombatComponent 自己的默认值（25 / 0.35 / 1.6s），
        // 与玩家预制体上落盘的配置一致，所以这里不需要再灌一次数值。
        if (_combat == null) _combat = gameObject.AddComponent<CombatComponent>();
        _combat.SetOwner(CampType.Player, _hitbox, _self);
    }

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
        if (IsSequenceDriven || !enabled) return false;
        return Combat != null && Combat.TryBeginAttack(now);
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
        if (_combat != null) _combat.FinishAttack();
        else DisableHitbox();
    }

    // 复位攻击状态：清掉冷却与窗口。
    // 连续两次自检/测试之间必须调用，否则上一轮的冷却会让 BeginAttack 失败。
    public void ResetAttackState()
    {
        _sampler.End(_sequenceInstance);
        _sequenceInstance = 0; _sequenceCombat = null;
        if (Combat != null) Combat.ResetAttackState();
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
        _sequenceInstance = state.InstanceId; _sequenceCombat = state.Action.Combat;
        _sampler.Begin(state.InstanceId);
        Combat.BeginSequence(state.InstanceId, _sequenceCombat.HitMask, _sequenceCombat.Damage);
    }
    public void HandleSequenceEvent(ActionExecutionState state, ActionFrameEvent frameEvent)
    {
        if (!IsSequenceDriven || _sequenceInstance != state.InstanceId || _sequenceCombat == null || Combat == null) return;
        if (_sequenceCombat.HitVolumes.Count > 0) return;
        if (frameEvent.EventKey == "combat.hitbox.open")
        { SequenceDamage(frameEvent.HitGroup); Combat.OpenSequenceWindow(frameEvent.HitGroup); }
        else if (frameEvent.EventKey == "combat.hitbox.close") Combat.CloseSequenceWindow();
    }
    public void SampleSequenceHitbox(long instanceId)
    {
        if (IsSequenceDriven && _sequenceInstance == instanceId && _sequenceCombat != null && _sequenceCombat.HitVolumes.Count == 0)
            Combat.SampleSequenceWindow();
    }
    public float SequenceDamage(int group)
    {
        if (_sequenceCombat == null) return AttackDamage;
        if (Combat.TryGetSequenceDamage(_sequenceInstance, group, out float damage)) return damage;
        float ratio = _motor != null && _motor.Params != null && _motor.Params.GroundSpeedThreshold > 0f
            ? _motor.HorizontalSpeed / _motor.Params.GroundSpeedThreshold : 0f;
        damage = _sequenceCombat.Damage * (_sequenceCombat.ScaleDamageWithSpeed
            ? Mathf.Max(0f, _sequenceCombat.SpeedDamage.Evaluate(ratio)) : 1f);
        Combat.SetSequenceDamage(_sequenceInstance, group, damage);
        return damage;
    }
    public void SampleSequenceHitbox(ActionExecutionState from, ActionExecutionState to, System.Collections.Generic.IReadOnlyList<Vector3> path)
    {
        if (!IsSequenceDriven || _sequenceInstance != to.InstanceId || _sequenceCombat == null || _hitbox == null) return;
        if (_sequenceCombat.HitVolumes.Count > 0) SampleConfigured(from, to, path);
        else if (System.Math.Abs(to.FrameProgress - System.Math.Round(to.FrameProgress)) > .000001) Combat.SampleSequenceWindow();
    }
    public void SampleSequenceBoundary(ActionExecutionState state)
    {
        if (!IsSequenceDriven || _sequenceInstance != state.InstanceId || _sequenceCombat == null || _hitbox == null) return;
        if (_sequenceCombat.HitVolumes.Count > 0) SampleConfigured(state, state, null);
        else Combat.SampleSequenceWindow();
    }
    private void SampleConfigured(ActionExecutionState from, ActionExecutionState to, System.Collections.Generic.IReadOnlyList<Vector3> path)
    {
        CharacterController controller = GetComponent<CharacterController>();
        Vector3 offset = Vector3.up * controller.height * .5f;
        int mask = _motor != null && _motor.Params != null ? _motor.Params.CollisionMask.value : ~0;
        _sampler.Sample(from, to, path, transform, _hitbox, offset, mask, group => SequenceDamage(group));
    }
    public void EndSequenceAction(long instanceId)
    {
        if (_sequenceInstance != instanceId) return;
        _sampler.End(instanceId); _sequenceInstance = 0; _sequenceCombat = null;
        if (Combat != null) Combat.EndSequence(instanceId);
    }
    private void OnSequenceLanded(IAttackDamageReceiver target, AttackDamageRequest request, AttackDamageResult result)
    {
        // 命中扩展事件不再执行顿帧；顿帧只由 Landed 订阅处理一次。
        SequenceHitLanded?.Invoke(target, request, result);
    }

    // ===== 打击感 =====
    // 由 CombatComponent 在成功造成伤害后回调（伤害被无敌帧挡下时不会走到这里）
    private void OnLandedHit(HealthComponent target, Vector3 point, Vector3 direction, float applied)
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
