using UnityEngine;
using GameJam.Actions;

// 玩家动作表拥有伤害与拼刀的独立帧区间；成功反馈只在批次结算后执行一次。
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
    private readonly ActionHitboxSampler _parrySampler = new ActionHitboxSampler();
    private readonly System.Collections.Generic.Dictionary<CombatComponent, ParryContact> _parryContacts
        = new System.Collections.Generic.Dictionary<CombatComponent, ParryContact>();
    private ActionExecutionState _parryState;
    private readonly System.Collections.Generic.List<ParryVolumeSweep> _parryMotion = new System.Collections.Generic.List<ParryVolumeSweep>();
    private float _parryMotionTick = float.NaN;
    private bool _parryMotionAdvanced;
    public event System.Action<System.Collections.Generic.IReadOnlyList<ParryContact>> ParrySucceeded;

    public event System.Action<IAttackDamageReceiver, AttackDamageRequest, AttackDamageResult> SequenceHitLanded;
    public bool IsSequenceDriven { get; private set; }

    /// <summary>
    /// 动作集里的「攻击招式」正在播放（有战斗载荷的序列动作）。
    /// 动画侧用它决定腿部层要不要让位给移动：普攻片段只该管上半身。
    /// </summary>
    public bool IsAttackSequenceActive => _sequenceInstance != 0 && _sequenceCombat != null;

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

    public bool IsParryVolumeActive => _sequenceInstance != 0 && _sequenceCombat != null
        && _sequenceCombat.ParryVolumes.Exists(volume => volume.IsActive(_parryState.Action, _parryState.FrameProgress));
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
        ParryResolver.Register(this);
        if (_combat != null) { _combat.Landed += OnLandedHit; _combat.SequenceLanded += OnSequenceLanded; }
    }

    private void OnDisable()
    {
        ParryResolver.Unregister(this);
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
        // 补出来的组件使用 CombatComponent 默认值，
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

    // 序列拥有攻击输入与拼刀帧；此入口只支持未接入序列的旧攻击宿主。
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

    // 成功已经由统一几何判定消费所有来源，此处只提交一次玩家状态和反馈。
    public void ReceiveParry(System.Collections.Generic.IReadOnlyList<ParryContact> contacts)
    {
        if (contacts.Count == 0) return;
        EnsureReferences();
        if (_self != null) _self.GrantInvulnerability(Combat.ParryInvulnerableSeconds);
        // 混合招架保留近战的时停，纯子弹招架也不能清掉此前已生效的时间效果。
        bool stopTime = false;
        foreach (ParryContact contact in contacts)
            stopTime |= contact.SourceType != AttackSourceType.Projectile;
        if (stopTime) TimeManager.ParryStop(Combat.ParryStopSeconds, Combat.ParryStopRate);
        _runner?.CancelForParry();
        if (_runner == null) { ResetAttackState(); Combat.FinishAttack(); }
        VCameraEffects.PlayParryZoom();
        bool disadvantage = false;
        foreach (ParryContact contact in contacts)
        {
            disadvantage |= contact.Kind == AttackParryKind.Disadvantage;
            if (contact.SourceType == AttackSourceType.Projectile || contact.SourceType == AttackSourceType.Beam)
                CombatSparks.PlayParryProjectile(contact.Point, contact.Direction);
            else CombatSparks.PlayParry(contact.Point, contact.Direction);
        }
        if (disadvantage) ApplyDisadvantageKnockback();
        if (_logParry) Debug.Log($"[PlayerCombat] 拼刀成功：{contacts.Count} 个攻击，时停 {(stopTime ? Combat.ParryStopSeconds : 0f):0.##} 秒", this);
        ParrySucceeded?.Invoke(contacts);
    }

    private int EnvironmentMask => _motor != null && _motor.Params != null ? _motor.Params.CollisionMask.value : ~0;
    private Vector3 ObstacleOffset => Vector3.up * GetComponent<CharacterController>().height * .5f;

    private void SampleParry(ActionExecutionState from, ActionExecutionState to, System.Collections.Generic.IReadOnlyList<Vector3> path)
    {
        _parryState = to;
        bool advancing = from.FrameProgress != to.FrameProgress;
        if (_parryMotionTick != TimeManager.FixedTickTime || advancing)
        {
            _parryMotion.Clear(); _parryMotionAdvanced = advancing; _parryMotionTick = TimeManager.FixedTickTime;
        }
        _parryContacts.Clear();
        _parrySampler.SampleVolumes(from, to, path, transform, _sequenceCombat.ParryVolumes,
            (group, pose, previous, spacing, start, end) =>
            {
                float spanStart = !advancing && !_parryMotionAdvanced ? 0f : start;
                _parryMotion.Add(new ParryVolumeSweep(previous, pose, spacing, spanStart, end));
                ParryResolver.Collect(this, previous, pose, spacing,
                    _sequenceCombat.HitMask, EnvironmentMask, ObstacleOffset, _parryContacts, start, end);
            });
        ParryResolver.Commit(this, _parryContacts);
    }

    public bool ResolveIncomingParry(CombatComponent source, AttackVolumePose? from = null, AttackVolumePose? to = null)
    {
        if (!isActiveAndEnabled || !IsSequenceDriven || _sequenceInstance == 0 || _sequenceCombat == null || !IsParryVolumeActive
            || _self != null && !_self.IsAlive || source.Camp == CampType.Player
            || (_sequenceCombat.HitMask.value & (1 << source.gameObject.layer)) == 0
            || !source.TryGetParryTarget(out AttackVolumePose target)) return false;
        _parryContacts.Clear();
        AttackVolumePose targetFrom = from ?? source.PreviousParryTarget, targetTo = to ?? target;
        if (_parryMotionTick != TimeManager.FixedTickTime || _parryMotion.Count == 0)
        {
            _parryMotion.Clear();
            foreach (ActionHitVolume volume in _sequenceCombat.ParryVolumes)
                if (volume.IsActive(_parryState.Action, _parryState.FrameProgress)
                    && volume.TryGetPose(transform, out Vector3 center, out Vector3 extents, out Quaternion rotation))
                {
                    var pose = new AttackVolumePose(center, extents, rotation, transform.position);
                    _parryMotion.Add(new ParryVolumeSweep(pose, pose, volume.SweepSpacing, 0f, 1f));
                }
        }
        foreach (ParryVolumeSweep sweep in _parryMotion)
        {
            ParryResolver.Collect(this, sweep.From, sweep.To, sweep.Spacing, _sequenceCombat.HitMask, EnvironmentMask,
                ObstacleOffset, _parryContacts, sweep.Start, sweep.End);
            if (ParryGeometry.Swept(sweep.From, sweep.To, ParryGeometry.Lerp(targetFrom, targetTo, sweep.Start),
                ParryGeometry.Lerp(targetFrom, targetTo, sweep.End), sweep.Spacing, out Vector3 point)
                && !ParryResolver.Blocked(transform, source.transform, sweep.To.Root + ObstacleOffset, point, EnvironmentMask))
                _parryContacts[source] = new ParryContact(source, point, targetTo.Center - sweep.To.Center);
        }
        bool hit = _parryContacts.ContainsKey(source);
        ParryResolver.Commit(this, _parryContacts);
        return hit;
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
        _sampler.End(_sequenceInstance); _parrySampler.End(_sequenceInstance); _parryState = default; _parryMotion.Clear(); _parryMotionTick = float.NaN; _parryMotionAdvanced = false;
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
        _sampler.Begin(state.InstanceId); _parrySampler.Begin(state.InstanceId); _parryState = state; _parryMotion.Clear(); _parryMotionTick = float.NaN; _parryMotionAdvanced = false;
        Combat.BeginSequence(state.InstanceId, _sequenceCombat.HitMask, _sequenceCombat.Damage);
    }
    public void HandleSequenceEvent(ActionExecutionState state, ActionFrameEvent frameEvent)
    {
        if (!IsSequenceDriven || _sequenceInstance != state.InstanceId || _sequenceCombat == null || Combat == null) return;
        _parryState = state;
        if (_sequenceCombat.HitVolumes.Count > 0) return;
        if (frameEvent.EventKey == "combat.hitbox.open")
        { SequenceDamage(frameEvent.HitGroup); Combat.OpenSequenceWindow(frameEvent.HitGroup); }
        else if (frameEvent.EventKey == "combat.hitbox.close") Combat.CloseSequenceWindow();
    }
    public void SampleSequenceHitbox(long instanceId)
    {
        if (!IsSequenceDriven || _sequenceInstance != instanceId || _sequenceCombat == null) return;
        SampleParry(_parryState, _parryState, null);
        if (_sequenceInstance == instanceId && _sequenceCombat.HitVolumes.Count == 0) Combat.SampleSequenceWindow();
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
        if (!IsSequenceDriven || _sequenceInstance != to.InstanceId || _sequenceCombat == null) return;
        SampleParry(from, to, path);
        if (_sequenceInstance != to.InstanceId) return;
        if (_sequenceCombat.HitVolumes.Count > 0) SampleConfigured(from, to, path);
        else if (System.Math.Abs(to.FrameProgress - System.Math.Round(to.FrameProgress)) > .000001) Combat.SampleSequenceWindow();
    }
    public void SampleSequenceBoundary(ActionExecutionState state)
    {
        if (!IsSequenceDriven || _sequenceInstance != state.InstanceId || _sequenceCombat == null) return;
        SampleParry(state, state, null);
        if (_sequenceInstance != state.InstanceId) return;
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
        _sampler.End(instanceId); _parrySampler.End(instanceId); _parryState = default; _parryMotion.Clear(); _parryMotionTick = float.NaN; _parryMotionAdvanced = false; _sequenceInstance = 0; _sequenceCombat = null;
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
