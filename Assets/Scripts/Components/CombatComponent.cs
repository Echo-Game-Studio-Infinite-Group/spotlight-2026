using System;
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
        return true;
    }

    /// <summary>收招：关窗并关判定盒。动画末帧事件或状态机退出时调。</summary>
    public void FinishAttack()
    {
        _windowEnd = float.NegativeInfinity;
        _windowOpen = false;
        DisableHitbox();
    }

    // 复位攻击状态：清掉冷却与窗口。
    // 连续两次自检/测试之间必须调用，否则上一轮的冷却会让 TryBeginAttack 失败。
    public void ResetAttackState()
    {
        _readyAt = float.NegativeInfinity;
        _windowEnd = float.NegativeInfinity;
        _windowOpen = false;
        _sequenceInstance = 0;
        _sequenceDamage = -1f;
        if (_hitbox != null) _hitbox.EndSequence();
        DisableHitbox();
    }

    // 动画事件入口：与旧接口同名，.fbx 上的动画事件不用改。
    public void EnableHitbox() { EnsureReferences(); if (_hitbox != null) _hitbox.EnableHitbox(); }
    public void DisableHitbox() { EnsureReferences(); if (_hitbox != null) _hitbox.DisableHitbox(); }

    // ===== 多段判定（动作序列专用）=====
    // 动作系统只在持有者那一侧；这里只收原语（instanceId / 掩码 / 伤害），不认识 ActionExecutionState。

    /// <summary>开始一次序列招式。伤害临时覆盖在 _sequenceDamage 上，收招后自动还原。</summary>
    public void BeginSequence(long instanceId, int hitMask, float damage)
    {
        EnsureReferences();
        _sequenceInstance = instanceId;
        _sequenceDamage = Mathf.Max(0f, damage);
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
        if (_hitbox != null) _hitbox.EndSequence();
    }

    private void Awake()
    {
        EnsureReferences();
        if (_hitbox != null) _hitbox.SetOwner(this);
    }

    private void OnEnable()
    {
        EnsureReferences();
        if (_hitbox != null) _hitbox.Hit += OnHit;
    }

    private void OnDisable()
    {
        if (_hitbox != null) _hitbox.Hit -= OnHit;
    }

    private void OnHit(Hitbox box, Collider other, HealthComponent target)
    {
        if (target == null) return;
        EnsureReferences();
        if (target == _self) return;

        // 阵营过滤：目标没有 CombatComponent 时视为中立，可打。
        CombatComponent otherCombat = other.GetComponentInParent<CombatComponent>();
        if (otherCombat != null && otherCombat != this && otherCombat._camp == _camp) return;

        Vector3 point = other.ClosestPoint(transform.position);
        Vector3 direction = target.transform.position - transform.position;
        float applied = target.TakeDamage(Damage, point, direction);
        if (applied > 0f) Landed?.Invoke(target, point, direction, applied);
    }

    // 惰性解析：Awake 不保证跑过（编辑模式装配工具、测试里 AddComponent 都不会触发它），
    // 所以引用为空时必须能重新找到，不能只在 Awake 里缓存一次。
    private void EnsureReferences()
    {
        if (_hitbox == null) _hitbox = GetComponentInChildren<Hitbox>(true);
        if (_self == null) _self = GetComponent<HealthComponent>();
    }
}
