using UnityEngine;

public readonly struct AttackDamageRequest
{
    public readonly float Damage;
    public readonly Vector3 Point, Direction;
    public readonly long ActionInstanceId;
    public readonly int HitGroup;
    public AttackDamageRequest(float damage, Vector3 point, Vector3 direction, long actionInstanceId, int hitGroup)
    { Damage = damage; Point = point; Direction = direction; ActionInstanceId = actionInstanceId; HitGroup = hitGroup; }
}
