using UnityEngine;

// 攻击编号独立于动作序列编号：敌人的动画攻击和池化子弹也必须逐次去重。
public readonly struct ParryContact
{
    public readonly CombatComponent Source;
    public readonly long AttackId;
    public readonly Vector3 Point, Direction;
    public readonly AttackSourceType SourceType;
    public readonly AttackParryKind Kind;

    public ParryContact(CombatComponent source, Vector3 point, Vector3 direction)
    {
        Source = source; AttackId = source.AttackId;
        Point = point; Direction = direction;
        SourceType = source.SourceType; Kind = source.ParryKind;
    }
}
