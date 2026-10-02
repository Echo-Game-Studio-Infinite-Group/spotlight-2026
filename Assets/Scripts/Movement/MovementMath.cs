using UnityEngine;

public static class MovementMath
{
    public static Vector3 Horizontal(Vector3 velocity) => new Vector3(velocity.x, 0f, velocity.z);

    public static Vector3 Accelerate(Vector3 velocity, Vector3 direction, float target, float acceleration, float dt, bool pump)
    {
        if (direction.sqrMagnitude < 0.000001f) return velocity;
        float step = acceleration * target * dt;
        if (!pump) step = Mathf.Min(step, Mathf.Max(0f, target - Vector3.Dot(velocity, direction)));
        return velocity + direction * step;
    }

    public static Vector3 ClipIntoSurface(Vector3 velocity, Vector3 normal)
    {
        float into = Vector3.Dot(velocity, normal);
        return into < 0f ? velocity - normal * into : velocity;
    }

    public static float ApproachAngle(Vector3 velocity, Vector3 normal)
    {
        Vector3 horizontal = Horizontal(velocity);
        Vector3 wallNormal = Horizontal(normal).normalized;
        if (horizontal.sqrMagnitude < 0.000001f || wallNormal == Vector3.zero) return -1f;
        float into = -Vector3.Dot(horizontal.normalized, wallNormal);
        return into > 0f ? Mathf.Asin(Mathf.Clamp01(into)) * Mathf.Rad2Deg : -1f;
    }

    public static Vector3 WallTangent(Vector3 incoming, Vector3 normal, Vector3 wish)
    {
        Vector3 tangent = Vector3.ProjectOnPlane(Horizontal(incoming), normal);
        // 正撞时由玩家选择沿墙方向，不能凭浮点误差随机左右弹射。
        if (tangent.sqrMagnitude < 0.0001f) tangent = Vector3.ProjectOnPlane(Horizontal(wish), normal);
        return tangent.sqrMagnitude < 0.0001f ? Vector3.zero : tangent.normalized;
    }

    public static Vector3 WallJump(Vector3 tangent, Vector3 normal, float speed, float angle)
    {
        if (tangent == Vector3.zero) return normal * speed;
        float radians = angle * Mathf.Deg2Rad;
        return (tangent * Mathf.Cos(radians) + normal * Mathf.Sin(radians)).normalized * speed;
    }
}
