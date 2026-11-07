using System.Collections.Generic;
using UnityEngine;

// 视线判定：远程 / 激光开火前的「隔墙不开火」。
// 只认真正的遮挡物——自己身上的碰撞体、目标身上的碰撞体都不算挡，
// 这样既不会自己挡自己（枪口常在模型内部），也不会因为先摸到目标的胶囊就判成被挡。
public static class SightCheck
{
    // 复用的碰撞体缓冲（全工程共用一份，不是每只敌人一个）。
    // 原实现每次 AimPoint 都调 GetComponentsInChildren<Collider>()，那个重载返回新数组；
    // 远程敌人在射程内约 20 次/秒/只地调它，是「视线检查本身」唯一的托管分配点。
    // 换成带 List 的重载（Unity 的非分配版本）后，同一次查询不再产生托管分配。
    // 容量按玩家实体的碰撞体数量（约 14 个）留足余量，避免内部扩容时再分配一次。
    //
    // 共用一份是安全的，但依赖两条不变量，改这个类的人必须守住：
    //   1. 只在主线程被调用 —— 本函数要用的 GetComponentsInChildren 与 Physics.RaycastNonAlloc
    //      都是主线程专属 API，所以它结构上就进不了工作线程；Unity 的 FixedUpdate/Update
    //      也是逐只串行执行，多个敌人「同一帧查视线」实际是排队，不是并发。
    //   2. AimPoint 内部不许重入 —— 当前实现是在 Clear 之后一口气填完、遍历完就 return，
    //      循环体里没有任何回调/事件/Unity 查询，没有嵌套入口。若将来在循环里加了回调，
    //      外层缓冲会被内层 Clear 掉，取到的碰撞体就错了。
    // 返回值是 Vector3 值拷贝，不持有本 List 的引用，所以调用方拿到的结果不会被下一次调用污染。
    //
    // 注意：这里只解决了分配，没解决遍历成本——仍然会扫一遍目标的整棵层级。
    private static readonly List<Collider> AimColliders = new List<Collider>(32);

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
        // 用非分配的 List 重载：遍历顺序与原数组重载一致（先自身、再子节点深度优先），
        // 所以取到的「第一个非触发碰撞体」与改造前完全相同，行为不变。
        AimColliders.Clear();
        target.GetComponentsInChildren<Collider>(false, AimColliders);
        for (int i = 0; i < AimColliders.Count; i++)
        {
            Collider collider = AimColliders[i];
            if (collider == null) continue;
            // 触发体是攻击判定盒之类，不代表身体，跳过。
            if (collider.isTrigger) continue;
            return collider.bounds.center;
        }
        return target.position;
    }
}
