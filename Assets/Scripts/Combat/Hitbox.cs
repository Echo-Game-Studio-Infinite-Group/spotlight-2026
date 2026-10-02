using System.Collections.Generic;
using UnityEngine;

public enum HitboxKind { Damage, Parry }

// 攻击判定采集（设计 §4.4）：由 PlayerCombat / 敌人 AI 按段激活
// 职责边界：只收集候选写入调用方缓冲，绝不结算（设计原则 4）——不要以为调脚本顺序能改变物理回调时机
// 静态注册表：DamageResolver 每 tick 拉取全部活跃框；组件 OnEnable/OnDisable 自管登记
[DisallowMultipleComponent]
public class Hitbox : MonoBehaviour
{
    [Tooltip("框种类：伤害框（打对方 Hurtbox）/ parry 框（碰对方的伤害框）——一个组件二选一")]
    public HitboxKind Kind = HitboxKind.Damage;

    private static readonly List<Hitbox> _registry = new List<Hitbox>();
    private static readonly Collider[] _overlapBuffer = new Collider[32];
    private static readonly RaycastHit[] _castBuffer = new RaycastHit[32];

    public static IReadOnlyList<Hitbox> Registry => _registry;

    public HitboxActivation Current { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>攻击侧命中事件（结算完成后由 DamageResolver 回调）：刀口火花/连击数/命中音效订阅；订阅随绑定解除</summary>
    public event System.Action<DamageInfo> HitResolved;

    // 扫掠状态：上一 tick 的形状采样
    private Vector3 _prevP0, _prevP1, _prevCenter;
    private Quaternion _prevRot;
    private bool _hasPrev;
    private Vector3 _lastPosForDir;

    // 同段去重（设计 §4.5 ②）：同攻击实例同段同目标仅一次，换段后重新合法
    private readonly HashSet<int> _hitTargets = new HashSet<int>();
    private int _dedupInstance = -1;
    private int _dedupPhase = -1;

    // parry 配对去重：同一来袭攻击只配对一次（防一帧多 tick 重复回调）
    private readonly HashSet<(int BoxId, int InstanceId)> _parryPaired = new HashSet<(int, int)>();

    private void OnEnable() => _registry.Add(this);

    private void OnDisable()
    {
        _registry.Remove(this);
        Deactivate();
    }

    public void Activate(in HitboxActivation activation)
    {
        Current = activation;
        IsActive = true;
        _hasPrev = false;
        _lastPosForDir = transform.position;
        _hitTargets.Clear();
        _parryPaired.Clear();
        _dedupInstance = activation.InstanceId;
        _dedupPhase = activation.PhaseIndex;
    }

    public void Deactivate()
    {
        IsActive = false;
        _hitTargets.Clear();
        _parryPaired.Clear();
    }

    /// <summary>去重：新目标登记并返回 true；同实例同段已命中过返回 false</summary>
    public bool MarkHitIfNew(Hurtbox target)
    {
        if (!IsActive) return false;
        if (_dedupInstance != Current.InstanceId || _dedupPhase != Current.PhaseIndex)
        {
            // 激活方重用了组件推进段（防御路径）：换段后目标重新合法
            _dedupInstance = Current.InstanceId;
            _dedupPhase = Current.PhaseIndex;
            _hitTargets.Clear();
        }
        return _hitTargets.Add(target.GetInstanceID());
    }

    /// <summary>parry 配对去重：同一 (来袭框, 来袭实例) 只配对一次</summary>
    public bool MarkParryPairedIfNew(Hitbox incoming)
    {
        if (!IsActive) return false;
        return _parryPaired.Add((incoming.GetInstanceID(), incoming.Current.InstanceId));
    }

    /// <summary>当前形状的世界空间采样（采集与 parry 几何配对共用；朝向随位移方向时用近期位移）</summary>
    public void GetWorldShape(out Vector3 capsuleP0, out Vector3 capsuleP1, out float radius,
        out Vector3 boxCenter, out Vector3 boxHalfExtents, out Quaternion boxRotation)
    {
        Current.Profile.GetWorldSpace(transform, transform.position - _lastPosForDir,
            out capsuleP0, out capsuleP1, out radius, out boxCenter, out boxHalfExtents, out boxRotation);
    }

    /// <summary>采集伤害候选：瞬时框（生效窗内每 tick 重叠）+ 扫掠框（覆盖上一 tick 刀根到当前的路径）</summary>
    public void CollectDamageCandidates(List<HitCandidate> results)
    {
        if (!IsActive || Kind != HitboxKind.Damage || Current.Profile == null) return;

        GetWorldShape(out Vector3 p0, out Vector3 p1, out float radius,
            out Vector3 center, out Vector3 halfExtents, out Quaternion rotation);

        int count = Current.Profile.Shape == HitboxShape.Box
            ? Physics.OverlapBoxNonAlloc(center, halfExtents, _overlapBuffer, rotation, ~0, QueryTriggerInteraction.Collide)
            : Physics.OverlapCapsuleNonAlloc(p0, p1, radius, _overlapBuffer, ~0, QueryTriggerInteraction.Collide);
        AppendCandidates(results, _overlapBuffer, count, center);

        // 扫掠：上一 tick 刀根 → 当前的整段路径都算命中区——高速位移一 tick 跨过整个敌人时不漏
        if (Current.Sweep && _hasPrev)
        {
            Vector3 dir = center - _prevCenter;
            float dist = dir.magnitude;
            if (dist > 0.0001f)
            {
                Vector3 dirNormalized = dir / dist; // Cast 系 API 要求方向单位向量，非归一化会触发引擎断言
                int hits = Current.Profile.Shape == HitboxShape.Box
                    ? Physics.BoxCastNonAlloc(_prevCenter, halfExtents, dirNormalized, _castBuffer, _prevRot, dist, ~0, QueryTriggerInteraction.Collide)
                    : Physics.CapsuleCastNonAlloc(_prevP0, _prevP1, radius, dirNormalized, _castBuffer, dist, ~0, QueryTriggerInteraction.Collide);
                for (int i = 0; i < hits; i++)
                {
                    Hurtbox hb = Hurtbox.FromCollider(_castBuffer[i].collider);
                    if (hb == null || IsOwnHurtbox(hb)) continue;
                    results.Add(new HitCandidate { Source = this, Target = hb, HitPoint = _castBuffer[i].point });
                }
            }
        }

        _prevP0 = p0;
        _prevP1 = p1;
        _prevCenter = center;
        _prevRot = rotation;
        _hasPrev = true;
        _lastPosForDir = transform.position;
    }

    /// <summary>结算完成回调转发（攻击侧命中事件）</summary>
    public void NotifyHit(in DamageInfo info) => HitResolved?.Invoke(info);

    private void AppendCandidates(List<HitCandidate> results, Collider[] colliders, int count, Vector3 referencePoint)
    {
        for (int i = 0; i < count; i++)
        {
            Hurtbox hb = Hurtbox.FromCollider(colliders[i]);
            if (hb == null || IsOwnHurtbox(hb)) continue;
            results.Add(new HitCandidate
            {
                Source = this,
                Target = hb,
                HitPoint = colliders[i].ClosestPoint(referencePoint),
            });
        }
    }

    private bool IsOwnHurtbox(Hurtbox hb) =>
        Current.Attacker != null && hb.IsOwnedBy(Current.Attacker);

    // 可视化（设计 §五）：伤害框红、parry 框蓝、扫掠路径黄——判定框可视化是调手感的标配
    private void OnDrawGizmos()
    {
        if (!CombatDebug.DrawHitboxes || !Application.isPlaying || !IsActive || Current.Profile == null) return;

        GetWorldShape(out Vector3 p0, out Vector3 p1, out float radius,
            out Vector3 center, out Vector3 halfExtents, out Quaternion rotation);

        Color color = Kind == HitboxKind.Damage ? Color.red : Color.blue;
        Gizmos.color = color;
        if (Current.Profile.Shape == HitboxShape.Box)
        {
            Gizmos.matrix = Matrix4x4.TRS(center, rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, Current.Profile.BoxSize);
            Gizmos.matrix = Matrix4x4.identity;
        }
        else
        {
            Gizmos.DrawLine(p0, p1);
            Gizmos.DrawWireSphere(p0, radius);
            Gizmos.DrawWireSphere(p1, radius);
        }

        if (Current.Sweep && _hasPrev)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(_prevCenter, center);
        }
    }
}
