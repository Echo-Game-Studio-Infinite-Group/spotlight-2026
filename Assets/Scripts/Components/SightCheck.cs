using UnityEngine;

// 视线判定：远程 / 激光开火前的「隔墙不开火」。
// 只认真正的遮挡物——自己身上的碰撞体、目标身上的碰撞体都不算挡，
// 这样既不会自己挡自己（枪口常在模型内部），也不会因为先摸到目标的胶囊就判成被挡。
public static class SightCheck
{
    /// <summary>从 from 到目标身上（非触发碰撞体中心）有没有被别的东西挡住。</summary>
    public static bool HasLineOfSight(Vector3 from, Transform target, Transform self, LayerMask mask, RaycastHit[] buffer)
    {
        if (target == null || buffer == null || buffer.Length == 0) return false;
        Vector3 to = AimPoint(target);
        Vector3 ray = to - from;
        float distance = ray.magnitude;
        if (distance <= 0.0001f) return true;

        int count = Physics.RaycastNonAlloc(from, ray / distance, buffer, distance, mask, QueryTriggerInteraction.Ignore);
        // 非分配查询没有距离顺序，填满时也不能确信最近遮挡已入列。
        if (count == buffer.Length) return false;
        float nearestTarget = float.PositiveInfinity, nearestObstacle = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            Collider hit = buffer[i].collider;
            if (hit == null) continue;
            // 自己身上的碰撞体（枪口常埋在模型里）不算遮挡，继续往下看
            if (self != null && hit.transform.IsChildOf(self)) continue;
            if (hit.transform.IsChildOf(target)) nearestTarget = Mathf.Min(nearestTarget, buffer[i].distance);
            else nearestObstacle = Mathf.Min(nearestObstacle, buffer[i].distance);
        }
        return float.IsPositiveInfinity(nearestObstacle) || nearestTarget < nearestObstacle;
    }

    /// <summary>
    /// 瞄准点：优先取目标身上「非触发」碰撞体的包围盒中心。
    /// 不能直接用 target.position —— 角色原点通常在脚底，直接瞄会朝地面打；用碰撞体中心还能自动适配体型。
    /// </summary>
    public static Vector3 AimPoint(Transform target)
    {
        Collider[] colliders = target.GetComponentsInChildren<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            // 触发体是攻击判定盒之类，不代表身体，跳过。
            if (colliders[i].isTrigger) continue;
            return colliders[i].bounds.center;
        }
        return target.position;
    }
}
