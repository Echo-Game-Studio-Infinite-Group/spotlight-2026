using UnityEngine;
using GameJam.Actions;

// 玩家的战斗决策层：只负责「什么时候攻击 / 什么时候 parry」与「打中之后怎么表现」。
//   · 输入消费 → 调 CombatComponent.TryBeginAttack / TryBeginParry
//   · 动作序列 → 翻译成原语转发给 CombatComponent
//   · 受击 parry → 实现 IParryReceiver，被敌人近战或飞行道具命中时判成不成立
//   · 命中表现 → 订阅 Landed 做顿帧；parry 成功做时缓 + 火花
// 数值（伤害/冷却/window/parry 窗口与无敌）、状态机、判定盒开关、命中结算都在 CombatComponent 上，与敌人共用同一份。
//
// 时钟走 UnscaledTime：
//   · InputBuffer 内部按 TimeManager.UnscaledTime 记录按下时刻，消费时用同一时钟才不会「刚按下就过期」；
//   · TimeManager 的减速窗口也是按 UnscaledTime 记的，这样「窗口开着」的判定与写入必然一致。
[RequireComponent(typeof(CharacterController))]
[DisallowMultipleComponent]
public sealed class PlayerCombat : MonoBehaviour, IParryReceiver
{
    [Header("打击感")]
    [SerializeField, Min(0f)] private float _hitStopSeconds = 0.2f;
    [SerializeField, Range(0.05f, 1f)] private float _hitTimeScale = 0.75f;
    // 单纯 0.2s @ 0.75x 只比常速省 50ms，人眼几乎察觉不到。
    // 因此命中瞬间先来一段更强更短的冲击，再落回 _hitTimeScale 的尾巴。
    [SerializeField, Min(0f)] private float _impactSeconds = 0.09f;
    [SerializeField, Range(0.05f, 1f)] private float _impactTimeScale = 0.35f;

    [Header("Parry")]
    [Tooltip("劣势 parry 把玩家向后弹开的小速度。策划案：速度清零 + 一个较小的向后速度")]
    [SerializeField, Min(0f)] private float _disadvantageKnockback = 3f;
    // 策划案 §6 规定时序：近战 parry 才给全局时缓，飞行道具与劣势 parry 不给。
    // 但「不给」在实机里是完全沉默的（时间没变、画面也没变），所以给两个调试开关：
    // 手感联调时打开就能确认这条链是通的，确认完再关回去。
    [Tooltip("飞行道具 parry 也给全局时缓。策划案默认不给，只为联调手感")]
    [SerializeField] private bool _slowOnProjectileParry;
    [Tooltip("劣势 parry 也给全局时缓。策划案默认不给，只为联调手感")]
    [SerializeField] private bool _slowOnDisadvantageParry;
    [Tooltip("parry 成功时打一行日志：走的是哪条分支、压到多少倍率")]
    [SerializeField] private bool _logParry = true;

    [SerializeField] private bool _logHits;

    // 引用不进序列化：战斗层在自己身上，判定盒在自己的子节点上，GetComponent 就能查到。
    private CombatComponent _combat;
    private PlayerInputReader _input;
    private PlayerMotor _motor;
    private Hitbox _hitbox;
    private HealthComponent _self;
    // 序列驱动的普攻归动作执行器管，parry 取消它时要用到；没有执行器的宿主（测试）保持 null。
    private PlayerActionRunner _runner;
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
        if (_runner == null) _runner = GetComponent<PlayerActionRunner>();
        // 没有落盘战斗层的宿主（测试、自检用 AddComponent 搭的临时玩家）补一个。
        // 补出来的组件用 CombatComponent 自己的默认值（25 / 0.35 / 1.6s、parry 0.2/0.8s），
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
        if (_input == null || !_input.GameplayEnabled) return;
        TickAttack(TimeManager.UnscaledTime);
    }

    // 一帧战斗逻辑：先消费 parry，再消费普攻。判定交给动画事件驱动的 Hitbox，这里不再做物理扫描。
    // 序列驱动时普攻由动作集接管，但 parry 仍是本组件自己的判定窗口，所以照常轮询。
    public void TickAttack(float now)
    {
        if (_input != null && _input.ConsumeParry(now)) BeginParry(now);
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

    /// <summary>按下 parry：开判定窗口，并取消进行中的普攻（连续 parry 的前提）。</summary>
    public bool BeginParry(float now)
    {
        if (Combat == null || !Combat.ReceiveAction(InputBuffer.Action.Parry, now)) return false;
        // 序列驱动的普攻活在动作系统里，CombatComponent 的收招够不到它，要连动作一起中断。
        // 只中断带战斗的序列：跳跃 / 滑铲不该因为按了一下 parry 被打断。
        if (_sequenceInstance != 0 && _sequenceCombat != null && _runner != null) _runner.Interrupt();
        return true;
    }

    // ===== Parry 受击（IParryReceiver）=====
    // 敌人近战从 CombatComponent.TryHit、飞行道具从 RangeComponent.ReportHit 走到这里。
    // 「窗口是否成立」的判断与数值都在 CombatComponent 上；「成立之后怎么表现」属于实体，在这里结算。
    public bool TryParry(AttackDamageRequest request)
    {
        EnsureReferences();
        if (Combat == null ||
            !Combat.TryParry(request, TimeManager.UnscaledTime, out AttackSourceType source, out AttackParryKind kind))
            return false;

        // 无敌是规则，先落地：后面的表现（火花 / 时缓）哪怕报错也不该让玩家掉血。
        if (_self != null) _self.GrantInvulnerability(Combat.ParryInvulnerableSeconds);
        // 运镜：推近 + 一点滚转，保持一段时间后自动回位。三种 parry 都给（这是 parry 的手感反馈本体）。
        VCameraEffects.PlayParryZoom();

        // 劣势 parry：默认不触发时缓，改成把玩家弹开——接完重型招式正好撞进敌人怀里是最难受的一种失败。
        if (kind == AttackParryKind.Disadvantage)
        {
            ApplyDisadvantageKnockback();
            CombatSparks.PlayParry(request.Point);
            if (_slowOnDisadvantageParry) ApplyParrySlow("劣势");
            else LogParry("劣势", "按策划案不触发时缓，只把玩家弹开（可打开 _slowOnDisadvantageParry 联调）");
            return true;
        }

        // 飞行道具 parry 默认不触发时缓，表现用独立的弹道特效（策划案 §6）。
        if (source == AttackSourceType.Projectile)
        {
            CombatSparks.PlayParryProjectile(request.Point, request.Direction);
            if (_slowOnProjectileParry) ApplyParrySlow("飞行道具");
            else LogParry("飞行道具", "按策划案不触发时缓（可打开 _slowOnProjectileParry 联调）");
            return true;
        }

        CombatSparks.PlayParry(request.Point);
        ApplyParrySlow("近战");
        return true;
    }

    // 全局时缓：时长与倍率是 CombatComponent 上的落盘数值，实体只负责发号。
    // 收在一处是为了让「哪条分支给不给慢放」一眼看全，日志也只在这里打。
    private void ApplyParrySlow(string branch)
    {
        TimeManager.ParrySlow(Combat.ParrySlowSeconds, Combat.ParrySlowRate);
        LogParry(branch, $"全局时缓 {Combat.ParrySlowSeconds:0.##}s @ {Combat.ParrySlowRate:0.##}x");
    }

    // parry 是低频事件，一行日志就能回答「时缓到底走没走」。
    // 这条链断掉是完全沉默的（时间没变、画面也没变），不打印就只能靠猜。
    private void LogParry(string branch, string detail)
    {
        if (!_logParry) return;
        Debug.Log($"[PlayerCombat] parry 成功（{branch}）：{detail}。"
            + $" GlobalRate={TimeManager.GlobalRate:0.##} PlayerRate={TimeManager.PlayerRate:0.##}"
            + $" WorldRate={TimeManager.WorldRate:0.##}");
    }

    // 速度清零 + 沿朝向反方向给一个小速度。走 Motor 的公开入口，不直接写速度字段。
    private void ApplyDisadvantageKnockback()
    {
        if (_motor == null || _motor.Params == null || !_motor.isActiveAndEnabled) return;
        _motor.SetHorizontalSpeed(0f);
        if (_disadvantageKnockback > 0f) _motor.TryExecute(MotorCommandKind.AddForwardImpulse, -_disadvantageKnockback);
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
        // 命中火花：喷向命中方向的来路，力度随实际伤害变化（表意打得越重火花越猛）
        CombatSparks.PlayHit(point, direction, applied);
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
