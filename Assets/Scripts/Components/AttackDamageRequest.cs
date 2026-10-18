using UnityEngine;

public enum AttackSourceType
{
    Melee,
    Projectile
}

// 这一击要求玩家用哪种 parry 接：普通 parry（蓝/黄提示）还是劣势 parry（红提示）。
// 敌人侧在 CombatComponent 上配置，随请求一起走到受击方，受击方据此决定表现与位移。
public enum AttackParryKind
{
    Normal,
    Disadvantage
}

public readonly struct AttackDamageRequest
{
    public readonly float Damage;
    public readonly Vector3 Point, Direction;
    public readonly long ActionInstanceId;
    public readonly int HitGroup;
    public readonly AttackSourceType SourceType;
    public readonly AttackParryKind ParryKind;
    public AttackDamageRequest(float damage, Vector3 point, Vector3 direction, long actionInstanceId, int hitGroup)
        : this(damage, point, direction, actionInstanceId, hitGroup, AttackSourceType.Melee) { }
    public AttackDamageRequest(float damage, Vector3 point, Vector3 direction, long actionInstanceId, int hitGroup,
        AttackSourceType sourceType, AttackParryKind parryKind = AttackParryKind.Normal)
    { Damage = damage; Point = point; Direction = direction; ActionInstanceId = actionInstanceId; HitGroup = hitGroup; SourceType = sourceType; ParryKind = parryKind; }
}
