using System.Collections.Generic;
using UnityEngine;

// 场景级命中结算（设计 §4.5）：固定步流水线「命中结算」位，执行序 +50（在移动之后）
// 顺序铁律：① parry 配对（先于一切伤害与无敌——「无敌不能阻止继续 parry」）
//           → ② 伤害配对（免疫过滤 → 去重 → 扣血）→ ③ 击退命令 → ④ 打击感链（hit-stop + 事件）
// 场景里放一个即可；测试与外部驱动可直接调 ProcessTick()
[DefaultExecutionOrder(50)]
public class DamageResolver : MonoBehaviour
{
    private readonly List<HitCandidate> _hits = new List<HitCandidate>(64);
    private readonly List<ParryCandidate> _parries = new List<ParryCandidate>(16);

    private void FixedUpdate() => ProcessTick();

    public void ProcessTick()
    {
        _hits.Clear();
        _parries.Clear();

        var registry = Hitbox.Registry;

        // —— 采集（物理查询只收候选，不在此处结算——设计原则 4）——
        for (int i = 0; i < registry.Count; i++)
        {
            Hitbox box = registry[i];
            if (!box.IsActive || box.Current.Profile == null) continue;
            if (box.Kind == HitboxKind.Damage) box.CollectDamageCandidates(_hits);
        }

        // parry 配对：我的 parry 框 × 他人伤害框（双方都在生效窗内——parry 是「攻击框碰攻击框」）
        for (int i = 0; i < registry.Count; i++)
        {
            Hitbox parryBox = registry[i];
            if (!parryBox.IsActive || parryBox.Kind != HitboxKind.Parry || parryBox.Current.Profile == null) continue;
            for (int j = 0; j < registry.Count; j++)
            {
                if (i == j) continue;
                Hitbox incoming = registry[j];
                if (!incoming.IsActive || incoming.Kind != HitboxKind.Damage || incoming.Current.Profile == null) continue;
                if (incoming.Current.Attacker == parryBox.Current.Attacker) continue; // 不招架自己的攻击
                if (ShapesOverlap(parryBox, incoming, out Vector3 point))
                {
                    _parries.Add(new ParryCandidate { ParryBox = parryBox, IncomingBox = incoming, ApproxPoint = point });
                }
            }
        }

        // —— ① parry 配对：回调接收方（PlayerCombat.OnParrySuccess → 授予无敌 + 时缓 + 派生窗口）——
        // 先于伤害循环执行，parry 换来的无敌在本 tick 就能挡掉配对攻击的伤害
        for (int i = 0; i < _parries.Count; i++)
        {
            ParryCandidate pc = _parries[i];
            if (!pc.ParryBox.MarkParryPairedIfNew(pc.IncomingBox)) continue;
            IParryReceiver receiver = pc.ParryBox.Current.Attacker != null
                ? pc.ParryBox.Current.Attacker.GetComponentInParent<IParryReceiver>()
                : null;
            receiver?.OnParrySuccess(new ParryInfo
            {
                Parrier = pc.ParryBox.Current.Attacker,
                SourceAttacker = pc.IncomingBox.Current.Attacker,
                SourceType = pc.IncomingBox.Current.DamageType,
                SourceInstanceId = pc.IncomingBox.Current.InstanceId,
                ApproxPoint = pc.ApproxPoint,
            });
        }

        // —— ②③④ 伤害：免疫 → 去重 → 扣血 → 击退命令 → hit-stop + 事件 ——
        for (int i = 0; i < _hits.Count; i++)
        {
            HitCandidate hc = _hits[i];
            HealthComponent health = hc.Target.Health;
            if (health == null || health.IsDead) continue;
            if (hc.Target.IsImmuneTo(hc.Source.Current.DamageType, health.LayerNow))
            {
                health.NotifyImmunityBlocked(BuildDamageInfo(hc)); // 免疫拦截反馈（滑铲挡弹类表现）
                continue;
            }
            if (!hc.Source.MarkHitIfNew(hc.Target)) continue; // 同实例同段同目标仅一次；换段重新合法

            DamageInfo info = BuildDamageInfo(hc);
            health.ApplyDamage(in info);        // ② 扣血（无敌拦截在 ApplyDamage 内再兜底一次）
            hc.Target.NotifyKnockback(in info); // ③ 击退命令（走 Motor/投射物接缝）
            if (info.HitStopSec > 0f) TimeManager.HitStop(info.HitStopSec); // ④ 打击感链
            hc.Source.NotifyHit(in info);       // 攻击侧命中事件（刀口火花/连击数）
        }
    }

    private static DamageInfo BuildDamageInfo(HitCandidate hc)
    {
        HitboxActivation a = hc.Source.Current;
        Vector3 origin = a.Attacker != null ? a.Attacker.transform.position : hc.Source.transform.position;
        Vector3 dir = hc.Target.transform.position - origin;
        dir.y = 0f;
        dir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.forward;
        return new DamageInfo
        {
            Attacker = a.Attacker,
            Target = hc.Target,
            AttackInstanceId = a.InstanceId,
            PhaseIndex = a.PhaseIndex,
            Type = a.DamageType,
            Damage = a.BaseDamage * (1f + a.SpeedDamageScale * a.SpeedRatioSnapshot),
            HitPoint = hc.HitPoint,
            KnockbackDir = dir,
            Knockback = a.Knockback,
            AttackerSpeedSnapshot = a.SpeedSnapshot,
            SpeedRatioSnapshot = a.SpeedRatioSnapshot,
            HitStopSec = a.HitStopSec,
            FromParryDerive = a.FromParryDerive,
        };
    }

    // —— parry 配对的几何近似 ——
    // 两框各自折算成「线段 + 半径」（盒退化为点 + 包围球半径），线段最近距 ≤ 半径和即判重叠。
    // 近似略微放宽 parry——与「parry 要给得宽松」的设计方向一致；伤人对 Hurtbox 走精确物理查询，不受此影响
    private static bool ShapesOverlap(Hitbox a, Hitbox b, out Vector3 point)
    {
        GetCapsuleApprox(a, out Vector3 a0, out Vector3 a1, out float ra);
        GetCapsuleApprox(b, out Vector3 b0, out Vector3 b1, out float rb);
        float distSq = SegmentSegmentDistanceSq(a0, a1, b0, b1, out float ta, out _);
        point = Vector3.Lerp(a0, a1, ta);
        return distSq <= (ra + rb) * (ra + rb);
    }

    private static void GetCapsuleApprox(Hitbox box, out Vector3 p0, out Vector3 p1, out float radius)
    {
        box.GetWorldShape(out p0, out p1, out radius, out Vector3 center, out _, out _);
        if (box.Current.Profile.Shape == HitboxShape.Box)
        {
            p0 = center;
            p1 = center;
            radius = box.Current.Profile.BoundingRadius;
        }
    }

    // 两线段最近点对（公式照写——Ericson《Real-Time Collision Detection》§5.1.9；数学不受版权保护）
    private static float SegmentSegmentDistanceSq(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
        out float s, out float t)
    {
        Vector3 d1 = q1 - p1;
        Vector3 d2 = q2 - p2;
        Vector3 r = p1 - p2;
        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);
        const float eps = 1e-10f;

        if (a <= eps && e <= eps)
        {
            s = 0f;
            t = 0f;
            return (p1 - p2).sqrMagnitude;
        }
        if (a <= eps)
        {
            s = 0f;
            t = Mathf.Clamp01(f / e);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= eps)
            {
                t = 0f;
                s = Mathf.Clamp01(-c / a);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;
                s = denom > eps ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                t = (b * s + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Mathf.Clamp01((b - c) / a);
                }
            }
        }
        Vector3 closest1 = p1 + d1 * s;
        Vector3 closest2 = p2 + d2 * t;
        return (closest1 - closest2).sqrMagnitude;
    }
}
