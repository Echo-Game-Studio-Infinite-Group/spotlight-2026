using System;
using System.Collections.Generic;
using UnityEngine;

// 攻守双方共用的战斗层：数值、状态、结算三件事都在这里。
//   · 数值：伤害 / 冷却 / 攻击窗口时长 / 阵营
//   · 状态：冷却计时、攻击窗口、动作序列的多段判定窗口
//   · 结算：判定盒报告命中后，扣谁、扣多少
// 刻意不含「什么时候攻击」（输入 or AI）与「打中之后怎么表现」（顿帧 or 震屏）——
// 那两件事属于实体，通过 Landed 事件挂上来。
//
// 时钟由调用方传入：玩家走 UnscaledTime（时停/减速不该影响输入窗口的判定），
// 敌人走 WorldTime（时停就该冻住敌人）。组件自己不取时钟，否则两边必有一边错。
[DisallowMultipleComponent]
public sealed class CombatComponent : MonoBehaviour
{
    [Header("攻击配置")]
    [SerializeField] private CampType _camp = CampType.Player;
    [SerializeField, Min(0f)] private float _damage = 25f;
    [SerializeField, Min(0f)] private float _cooldown = 0.35f;
    // 攻击窗口时长。大于 0 时到点自动关窗（玩家，时长＝动画片段长）；
    // 等于 0 表示不自动关，由动画状态机调 FinishAttack（敌人）。
    [SerializeField, Min(0f)] private float _windowSeconds = 1.6f;

    [Header("Parry 成功反馈")]
    [SerializeField, Min(0f)] private float _parryInvulnerableSeconds = .8f;
    [SerializeField, Min(0f), Tooltip("真实秒数；0 表示关闭时停")]
    private float _parryStopSeconds = .08f;
    [SerializeField, Range(0f, 1f)] private float _parryStopRate;

    [Header("Parry（本组件作为攻击方）")]
    [Tooltip("本组件的攻击要求玩家用哪种 parry 接。红色提示的重型招式填 Disadvantage")]
    [SerializeField] private AttackParryKind _parryKind = AttackParryKind.Normal;

    // 引用不进序列化：判定盒是自己的子节点、血量组件在自己身上，GetComponent 就能查到。
    // 摆进 Inspector 只会多出两个要手拖的槽；拖漏了还得靠兜底救，那不如直接不暴露。
    private Hitbox _hitbox;
    private HealthComponent _self;

    private float _readyAt = float.NegativeInfinity;
    private float _windowEnd = float.NegativeInfinity;
    // WindowSeconds 为 0 时的开窗标记：玩家由时长自动关窗，敌人由动画状态机调 FinishAttack。
    private bool _windowOpen;
    private long _sequenceInstance;
    // 序列招式的临时伤害。< 0 表示没有覆盖，此时用落盘的 _damage。
    private float _sequenceDamage = -1f;
    private readonly Dictionary<int, float> _groupDamage = new Dictionary<int, float>();
    private readonly Dictionary<int, HashSet<IAttackDamageReceiver>> _hitTargets = new Dictionary<int, HashSet<IAttackDamageReceiver>>();
    // 受击事件可能同步取消动作；正在结算的目标不能重入扣血。
    private readonly HashSet<IAttackDamageReceiver> _resolving = new HashSet<IAttackDamageReceiver>();
    public long SequenceInstanceId => _sequenceInstance;
    public event Action<IAttackDamageReceiver, AttackDamageRequest, AttackDamageResult> SequenceLanded;

    /// <summary>命中且真的扣了血时触发：目标、命中点、方向、实际伤害。</summary>
    public event Action<HealthComponent, Vector3, Vector3, float> Landed;

    public CampType Camp { get => _camp; set => _camp = value; }
    /// <summary>实际伤害。玩家的序列招式期间会被临时覆盖，收招后自动还原成落盘值。</summary>
    public float Damage
    {
        get => _sequenceDamage >= 0f ? _sequenceDamage : _damage;
        set => _damage = Mathf.Max(0f, value);
    }
    public float Cooldown { get => _cooldown; set => _cooldown = Mathf.Max(0f, value); }
    public float WindowSeconds { get => _windowSeconds; set => _windowSeconds = Mathf.Max(0f, value); }
    public float ParryInvulnerableSeconds { get => _parryInvulnerableSeconds; set => _parryInvulnerableSeconds = Mathf.Max(0f, value); }
    public float ParryStopSeconds { get => _parryStopSeconds; set => _parryStopSeconds = Mathf.Max(0f, value); }
    public float ParryStopRate { get => _parryStopRate; set => _parryStopRate = Mathf.Clamp01(value); }
    /// <summary>本组件的攻击要求玩家用哪种 parry 接。判定盒与远程结算构造请求时读它。</summary>
    public AttackParryKind ParryKind { get => _parryKind; set => _parryKind = value; }

    public Hitbox Hitbox { get { EnsureReferences(); return _hitbox; } }
    public HealthComponent Self { get { EnsureReferences(); return _self; } }
    public bool IsSequenceActive => _sequenceInstance != 0;

    /// <summary>只回填引用与阵营，不动数值——数值是落盘配置，不被运行时覆盖。</summary>
    public void SetOwner(CampType camp, Hitbox hitbox, HealthComponent self)
    {
        _camp = camp;
        if (hitbox != null) _hitbox = hitbox;
        if (self != null) _self = self;
        if (_hitbox != null) _hitbox.SetOwner(this);
    }

    /// <summary>攻击进行中。now 由持有者按自己的时钟传。</summary>
    public bool IsAttacking(float now)
        => _sequenceInstance != 0 || (WindowSeconds > 0f ? now < _windowEnd : _windowOpen);

    /// <summary>尝试起手：查冷却与窗口，成功则开窗。now 由持有者按自己的时钟传。</summary>
    public bool TryBeginAttack(float now)
    {
        if (now < _readyAt) return false;
        _readyAt = now + Cooldown;
        _windowEnd = now + WindowSeconds;
        _windowOpen = true;
        BeginAttackIdentity();
        return true;
    }

    /// <summary>收招：关窗并关判定盒。动画末帧事件或状态机退出时调。</summary>
    public void FinishAttack()
    {
        _windowEnd = float.NegativeInfinity;
        _windowOpen = false;
        _independentParryTarget = false;
        ParryResolver.Unregister(this);
        DisableHitbox();
    }

    [SerializeField, Tooltip("攻击属性由来源持有，通用 Hitbox 不再猜测近战/子弹")]
    private AttackSourceType _sourceType;
    [SerializeField, Tooltip("前摇至收招期间可被拼刀；子弹使用放大的独立框。帧锚点仅供玩家动作表使用。")]
    private GameJam.Actions.ActionHitVolume _parryTarget = new GameJam.Actions.ActionHitVolume
    { DisplayName = "被 Parry 框", Center = new Vector3(0f, .9f, .5f), Size = new Vector3(1.5f, 1.8f, 1.5f) };
    [SerializeField] private bool _canBeParried = true;
    private static long _nextAttackId;
    [SerializeField, Min(0f)] private float _parryStaggerSeconds = .3f;
    private bool _parryConsumed;
    private IParryMotionSource _parryMotionSource;
    private bool _independentParryTarget;
    private bool _targetOverride;
    private AttackVolumePose _overridePose;
    public long AttackId { get; private set; }
    public AttackSourceType SourceType { get => _sourceType; set => _sourceType = value; }
    public GameJam.Actions.ActionHitVolume ParryTarget => _parryTarget;
    public AttackVolumePose PreviousParryTarget { get; private set; }
    public event Action<ParryContact> Parried;

    public bool ReceiveAction(InputBuffer.Action action, float now)
        => action == InputBuffer.Action.Attack && TryBeginAttack(now);

    private void BeginAttackIdentity()
    {
        AttackId = ++_nextAttackId; _parryConsumed = false; _targetOverride = false; _independentParryTarget = false;
        if (TryGetParryTarget(out AttackVolumePose pose)) PreviousParryTarget = pose;
        ParryResolver.Register(this);
    }
    // 蓄力由远程组件推进，不能占用近战 IsAttacking，否则 AI 会停在前摇而不再推进发射。
    public void ArmParryTarget()
    {
        BeginAttackIdentity(); _independentParryTarget = true;
        if (TryGetParryTarget(out AttackVolumePose pose)) PreviousParryTarget = pose;
    }
    public bool TryGetParryTarget(out AttackVolumePose pose)
    {
        pose = default;
        if (!_canBeParried || _parryConsumed || AttackId == 0 || !isActiveAndEnabled
            || (_self != null && !_self.IsAlive) || (!_independentParryTarget && !IsAttacking(Camp == CampType.Player ? TimeManager.UnscaledTime : TimeManager.WorldTime))) return false;
        if (_targetOverride) { pose = _overridePose; return true; }
        if (!_parryTarget.TryGetPose(transform, out Vector3 center, out Vector3 extents, out Quaternion rotation)) return false;
        pose = new AttackVolumePose(center, extents, rotation, transform.position); return true;
    }
    public void SetParryMotionSource(IParryMotionSource source) => _parryMotionSource = source;
    public bool TryGetParryMotion(out AttackVolumePose from, out AttackVolumePose to)
    {
        from = default;
        if (!TryGetParryTarget(out to)) return false;
        if (_parryMotionSource != null) return _parryMotionSource.TryGetParryMotion(out from, out to);
        from = PreviousParryTarget; return true;
    }
    public void SetParryTargetPose(AttackVolumePose pose)
    {
        if (!_targetOverride) PreviousParryTarget = pose;
        _targetOverride = true; _overridePose = pose;
    }
    public bool ConsumeParry(ParryContact contact)
    {
        if (contact.AttackId != AttackId || !TryGetParryTarget(out _)) return false;
        _parryConsumed = true;
        if (_sequenceInstance != 0) EndSequence(_sequenceInstance);
        _readyAt = Mathf.Max(_readyAt, TimeManager.WorldTime + _parryStaggerSeconds);
        FinishAttack();
        Parried?.Invoke(contact);
        return true;
    }
    private void LateUpdate()
    {
        if (TryGetParryTarget(out AttackVolumePose pose)) PreviousParryTarget = pose;
    }
    private void OnDrawGizmosSelected()
    {
        if (!_parryTarget.TryGetPose(transform, out Vector3 center, out Vector3 extents, out Quaternion rotation)) return;
        Matrix4x4 prior = Gizmos.matrix;
        Gizmos.color = Color.cyan; Gizmos.matrix = Matrix4x4.TRS(center, rotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, extents * 2f); Gizmos.matrix = prior;
    }

    // 复位攻击状态：清掉冷却与窗口。
    // 连续两次自检/测试之间必须调用，否则上一轮的冷却会让 TryBeginAttack 失败。
    public void ResetAttackState()
    {
        _readyAt = float.NegativeInfinity;
        _windowEnd = float.NegativeInfinity;
        _windowOpen = false;
        _parryMotionSource = null;
        AttackId = 0; _parryConsumed = false; _targetOverride = false; _independentParryTarget = false;
        ParryResolver.Unregister(this);
        _sequenceInstance = 0;
        _sequenceDamage = -1f;
        _groupDamage.Clear();
        _hitTargets.Clear();
        if (_hitbox != null) _hitbox.EndSequence();
        DisableHitbox();
    }

    // 动画事件入口：与旧接口同名，.fbx 上的动画事件不用改。
    public void EnableHitbox() { EnsureReferences(); if (_parryConsumed) return; if (_hitbox != null) _hitbox.EnableHitbox(); }
    public void DisableHitbox() { EnsureReferences(); if (_hitbox != null) _hitbox.DisableHitbox(); }

    // ===== 多段判定（动作序列专用）=====
    // 动作系统只在持有者那一侧；这里只收原语（instanceId / 掩码 / 伤害），不认识 ActionExecutionState。

    /// <summary>开始一次序列招式。伤害临时覆盖在 _sequenceDamage 上，收招后自动还原。</summary>
    public void BeginSequence(long instanceId, int hitMask, float damage)
    {
        EnsureReferences();
        ResetAttackState();
        _sequenceInstance = instanceId;
        _sequenceDamage = Mathf.Max(0f, damage);
        BeginAttackIdentity();
        if (_hitbox != null) _hitbox.BeginSequence(instanceId, hitMask);
    }

    public void OpenSequenceWindow(int hitGroup) { EnsureReferences(); if (_hitbox != null) _hitbox.OpenSequenceWindow(hitGroup); }
    public void CloseSequenceWindow() { EnsureReferences(); if (_hitbox != null) _hitbox.CloseSequenceWindow(); }
    public void SampleSequenceWindow() { EnsureReferences(); if (_hitbox != null) _hitbox.SampleSequenceWindow(); }

    public void EndSequence(long instanceId)
    {
        if (_sequenceInstance != instanceId) return;
        _sequenceInstance = 0;
        _sequenceDamage = -1f;
        _groupDamage.Clear();
        _hitTargets.Clear();
        ParryResolver.Unregister(this);
        if (_hitbox != null) _hitbox.EndSequence();
    }

    private void Awake()
    {
        EnsureReferences();
        if (_hitbox != null) _hitbox.SetOwner(this);
    }

    private void OnDisable() => ResetAttackState();

    public bool TryGetSequenceDamage(long instanceId, int group, out float damage)
    {
        damage = 0f;
        return instanceId != 0 && _sequenceInstance == instanceId && _groupDamage.TryGetValue(group, out damage);
    }

    public void SetSequenceDamage(long instanceId, int group, float damage)
    {
        if (instanceId != 0 && _sequenceInstance == instanceId && !_groupDamage.ContainsKey(group))
            _groupDamage.Add(group, Mathf.Max(0f, damage));
    }

    public float SequenceDamage(int group) => _groupDamage.TryGetValue(group, out float damage) ? damage : Damage;

    // 所有判定入口在此结算；Hitbox 的事件只通知结果，不能再由订阅者扣血。
    public AttackDamageResult TryHit(Collider other, IAttackDamageReceiver target, AttackDamageRequest request)
    {
        if (!isActiveAndEnabled || _parryConsumed || other == null || target == null) return default;
        if (request.ActionInstanceId != _sequenceInstance) return default;
        EnsureReferences();
        HealthComponent health = target as HealthComponent;
        if (health == _self && health != null || other.transform.IsChildOf(transform)) return default;
        CombatComponent otherCombat = health != null ? health.GetComponentInParent<CombatComponent>() : other.GetComponentInParent<CombatComponent>();
        if (otherCombat != null && otherCombat != this && otherCombat.Camp == Camp) return default;
        HashSet<IAttackDamageReceiver> hit = null;
        if (_sequenceInstance != 0)
        {
            if (!_hitTargets.TryGetValue(request.HitGroup, out hit))
                _hitTargets.Add(request.HitGroup, hit = new HashSet<IAttackDamageReceiver>());
            if (hit.Contains(target)) return default;
        }
        if (!_resolving.Add(target)) return default;
        AttackDamageResult result;
        try
        {
            if (ParryResolver.ResolveIncoming(this)) return default;
            result = target.ReceiveAttack(request);
        }
        finally { _resolving.Remove(target); }
        if (result.AppliedDamage <= 0f) return result;
        // 死亡表现可同步结束攻击。记录归属旧组，不得污染新动作的去重集。
        hit?.Add(target);
        if (health != null) Landed?.Invoke(health, request.Point, request.Direction, result.AppliedDamage);
        if (request.ActionInstanceId != 0) SequenceLanded?.Invoke(target, request, result);
        return result;
    }

    // 惰性解析：Awake 不保证跑过（编辑模式装配工具、测试里 AddComponent 都不会触发它），
    // 所以引用为空时必须能重新找到，不能只在 Awake 里缓存一次。
    private void EnsureReferences()
    {
        if (_hitbox == null) _hitbox = GetComponentInChildren<Hitbox>(true);
        if (_self == null) _self = GetComponent<HealthComponent>();
    }
}
