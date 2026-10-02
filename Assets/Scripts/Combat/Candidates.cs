using UnityEngine;

// 采集候选（设计原则 4：物理查询只把候选写进缓冲，不在此处结算）
public struct HitCandidate
{
    public Hitbox Source;   // 伤害框
    public Hurtbox Target;  // 命中的受击框
    public Vector3 HitPoint;
}

// parry 配对候选：招架框 × 来袭伤害框（双方都在生效窗内——parry 是「攻击框碰攻击框」）
public struct ParryCandidate
{
    public Hitbox ParryBox;
    public Hitbox IncomingBox;
    public Vector3 ApproxPoint;
}
