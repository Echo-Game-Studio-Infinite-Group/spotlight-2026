using UnityEngine;

// 判定框形状（设计 §4.4〔提案〕：先只做胶囊与盒）
public enum HitboxShape { Capsule, Box }

// 朝向模式：攻击框通常随攻击者面朝；运动向（刀尖随位移方向）留给高速位移类招式
public enum HitboxOrientation { AttackerForward, MovementDirection, LocalSpace }

// 判定框纯数据（设计 §4.4）：形状/尺寸/局部偏移/朝向，嵌在 AttackPhase 里由策划在 Inspector 调
// 局部偏移基于攻击者 pivot（脚底），y≈1 即胸口高度
[System.Serializable]
public class HitboxProfile
{
    public HitboxShape Shape = HitboxShape.Capsule;
    public HitboxOrientation Orientation = HitboxOrientation.AttackerForward;

    [Tooltip("胶囊半径（米）")]
    public float CapsuleRadius = 0.35f;

    [Tooltip("胶囊总长（米，含两端半球）——水平放倒，轴向 = 朝向方向（刀形）")]
    public float CapsuleHeight = 1.2f;

    [Tooltip("盒尺寸（本物体局部：x=宽 y=高 z=纵深）")]
    public Vector3 BoxSize = new Vector3(0.6f, 0.6f, 1.2f);

    [Tooltip("局部偏移（攻击者空间，z 正向前方）")]
    public Vector3 LocalOffset = new Vector3(0f, 1.0f, 0.5f);

    // 世界空间解析：胶囊输出两端点+半径，盒输出中心+半尺寸+旋转——两类都产出，消费方各取所需
    public void GetWorldSpace(Transform attacker, Vector3 moveDir,
        out Vector3 capsuleP0, out Vector3 capsuleP1, out float radius,
        out Vector3 boxCenter, out Vector3 boxHalfExtents, out Quaternion boxRotation)
    {
        Vector3 fwd = Orientation == HitboxOrientation.MovementDirection && moveDir.sqrMagnitude > 0.0001f
            ? moveDir.normalized
            : attacker.forward;
        Vector3 center = attacker.TransformPoint(LocalOffset);
        float halfSegment = Mathf.Max(0f, CapsuleHeight * 0.5f - CapsuleRadius);
        capsuleP0 = center - fwd * halfSegment;
        capsuleP1 = center + fwd * halfSegment;
        radius = CapsuleRadius;
        boxCenter = center;
        boxHalfExtents = BoxSize * 0.5f;
        boxRotation = Quaternion.LookRotation(fwd, Vector3.up);
    }

    // 包围球半径（parry 配对等近似几何运算用）
    public float BoundingRadius =>
        Shape == HitboxShape.Box ? BoxSize.magnitude * 0.5f : Mathf.Max(CapsuleHeight * 0.5f, CapsuleRadius);
}
