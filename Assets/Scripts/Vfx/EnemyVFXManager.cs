using UnityEngine;

// 敌人身上的特效总控：与 PlayerVFXManager 同一套职责划分——AI/结算只喊一声，表现全在这里管。
//   · 攻击刀光：跟随右手骨的 AttackTrailVfx。
//
// 为什么用"盯判定盒状态"而不是只靠动画事件：
//   EnemyHeavy 的 Attack/Fire 片段上确实有 EnableHitbox / DisableHitbox 事件，但动画事件只发给
//   Animator 所在物体上的组件，中间任何一层节点/嵌套预制体对不上，表现就整条丢掉且没有任何报错。
//   判定盒的 collider.enabled 是"窗口真的开了没有"的唯一事实，所以这里每帧对一次状态：
//   需要表现层的其他敌人（近战/远程/召唤）都自动生效，不必各自补事件。
// 动画事件那条路仍然保留（EnemyBase.EnableHitbox/DisableHitbox 会直接调本组件），两条都收敛到 PlayAttack。
[DisallowMultipleComponent]
public sealed class EnemyVFXManager : MonoBehaviour
{
    [Header("攻击刀光")]
    [Tooltip("跟随右手的刀光拖尾；留空则运行时在子层级里找 AttackTrailVfx")]
    [SerializeField] private AttackTrailVfx _attackTrail;

    [Tooltip("判定窗口没给关闭事件时的兜底发射时长（秒）")]
    [SerializeField, Min(0.02f)] private float _attackEmitSeconds = 0.3f;

    [Tooltip("按判定盒开关自动启停刀光（动画事件不可靠时这是唯一兜底）")]
    [SerializeField] private bool _driveByHitbox = true;

    private Collider _hitboxCollider;
    private bool _hitboxWasOpen;
    private bool _polled;

    /// <summary>刀光是否正在发射（供自检 / 调试用）。</summary>
    public bool IsAttackTrailPlaying => _attackTrail != null && _attackTrail.IsEmitting;

    private void Awake()
    {
        ResolveTrail();
        ResolveHitbox();
    }

    // 装配工具会把引用接上；没接上（直接拖预制体进场景做试验）就在子层级里找一次。
    private AttackTrailVfx ResolveTrail()
    {
        if (_attackTrail == null) _attackTrail = GetComponentInChildren<AttackTrailVfx>(true);
        return _attackTrail;
    }

    // 判定盒可能在子层级里、也可能延迟装配（EnemyBase.ConfigureAttackHitbox），所以第一次用不上就再找。
    private void ResolveHitbox()
    {
        if (_hitboxCollider != null) return;
        Hitbox hitbox = GetComponentInChildren<Hitbox>(true);
        if (hitbox == null) return;
        _hitboxCollider = hitbox.GetComponent<Collider>();
        _polled = _hitboxCollider != null;
    }

    private void Update()
    {
        if (!_driveByHitbox) return;
        if (!_polled) { ResolveHitbox(); if (!_polled) return; }

        bool open = _hitboxCollider != null && _hitboxCollider.enabled;
        if (open == _hitboxWasOpen) return;
        _hitboxWasOpen = open;
        if (open) PlayAttack();
        else StopAttacks();
    }

    /// <summary>判定生效：开刀光（常与 EnableHitbox 同一帧）。</summary>
    public void PlayAttack()
    {
        AttackTrailVfx trail = ResolveTrail();
        if (trail != null) trail.Play(_attackEmitSeconds);
    }

    /// <summary>判定关闭 / 攻击被打断 / 死亡回池：立刻收干净。</summary>
    public void StopAttacks()
    {
        AttackTrailVfx trail = ResolveTrail();
        if (trail != null) trail.ClearNow();
    }

    /// <summary>装配工具用：把发射器接进来。</summary>
    public void Configure(AttackTrailVfx trail) => _attackTrail = trail;
}
