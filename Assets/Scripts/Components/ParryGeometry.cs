using UnityEngine;

// 分离轴测试保留旋转盒的真实边界，不能用外包 AABB 把背身和斜角空隙算成拼刀。
public static class ParryGeometry
{
    public static bool Overlaps(AttackVolumePose a, AttackVolumePose b)
    {
        Vector3 delta = b.Center - a.Center;
        for (int i = 0; i < 3; i++)
        {
            Vector3 axisA = Axis(a.Rotation, i), axisB = Axis(b.Rotation, i);
            if (Separates(axisA, delta, a, b) || Separates(axisB, delta, a, b)) return false;
            for (int j = 0; j < 3; j++)
                if (Separates(Vector3.Cross(axisA, Axis(b.Rotation, j)), delta, a, b)) return false;
        }
        return true;
    }

    public static bool Swept(AttackVolumePose a0, AttackVolumePose a1, AttackVolumePose b0, AttackVolumePose b1,
        float spacing, out Vector3 point)
    {
        Vector3 relativeStart = a0.Center - b0.Center;
        Vector3 relativeStep = a1.Center - b1.Center - relativeStart;
        float closest = relativeStep.sqrMagnitude > .000001f
            ? Mathf.Clamp01(-Vector3.Dot(relativeStart, relativeStep) / relativeStep.sqrMagnitude) : 0f;
        float bound = Mathf.Max(a0.Extents.magnitude, a1.Extents.magnitude) + Mathf.Max(b0.Extents.magnitude, b1.Extents.magnitude);
        if ((relativeStart + relativeStep * closest).sqrMagnitude > bound * bound)
        { point = default; return false; }
        float travel = Vector3.Distance(a1.Center - b1.Center, a0.Center - b0.Center)
            + Quaternion.Angle(a0.Rotation, a1.Rotation) * Mathf.Deg2Rad * Mathf.Max(a0.Extents.magnitude, a1.Extents.magnitude)
            + Quaternion.Angle(b0.Rotation, b1.Rotation) * Mathf.Deg2Rad * Mathf.Max(b0.Extents.magnitude, b1.Extents.magnitude);
        float thin = Mathf.Min(Mathf.Min(a0.Extents.x, Mathf.Min(a0.Extents.y, a0.Extents.z)),
            Mathf.Min(b0.Extents.x, Mathf.Min(b0.Extents.y, b0.Extents.z)));
        int steps = Mathf.Max(1, Mathf.CeilToInt(travel / Mathf.Max(.001f, Mathf.Min(spacing, thin))));
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            AttackVolumePose a = Lerp(a0, a1, t), b = Lerp(b0, b1, t);
            if (!Overlaps(a, b)) continue;
            point = (a.Center + b.Center) * .5f; return true;
        }
        point = default; return false;
    }

    public static AttackVolumePose Lerp(AttackVolumePose a, AttackVolumePose b, float t)
        => new AttackVolumePose(Vector3.Lerp(a.Center, b.Center, t), Vector3.Lerp(a.Extents, b.Extents, t),
            Quaternion.Slerp(a.Rotation, b.Rotation, t), Vector3.Lerp(a.Root, b.Root, t));

    private static Vector3 Axis(Quaternion rotation, int i) => rotation * (i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward);
    private static bool Separates(Vector3 axis, Vector3 delta, AttackVolumePose a, AttackVolumePose b)
    {
        if (axis.sqrMagnitude < .000001f) return false;
        float radius = 0f;
        for (int i = 0; i < 3; i++)
            radius += a.Extents[i] * Mathf.Abs(Vector3.Dot(axis, Axis(a.Rotation, i)))
                + b.Extents[i] * Mathf.Abs(Vector3.Dot(axis, Axis(b.Rotation, i)));
        return Mathf.Abs(Vector3.Dot(delta, axis)) > radius + .00001f;
    }
}
