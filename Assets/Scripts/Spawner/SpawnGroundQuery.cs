using UnityEngine;
using UnityEngine.AI;

// 落地点查询：把一个"水平候选点"修正到它正下方的可站立地面上。
//
// 为什么需要它：Spawn Range 是圆形，随机取点只保证 XZ 在圈内，
// Y 仍然是 Spawner 自身的高度。地块有高低差（本项目地面由 Ground 平面 + 斜坡 + 高台组成，
// 且敌人靠 NavMeshAgent 移动），直接把 Spawner 的 Y 套上去会让敌人埋进地里或悬空——
// 埋进地里的敌人如果还没上 NavMesh，NavMeshAgent 会直接报 "not on navmesh" 而彻底不工作。
//
// 两级策略，与项目"敌人必须站在 NavMesh 上"的架构对齐：
//   1. NavMesh.SamplePosition —— 首选。它本身就理解"层"：给定上下重叠的两块地板，
//      它会返回半径内最近的那一块，而不是像向下射线那样必然打中最上面那块。
//      同时保证返回点对 Agent 是可达的（后续 SetDestination 才不会失败）。
//   2. Physics.Raycast 向下 —— 兜底。场景还没烘焙 NavMesh（或纯灰盒测试）时用。
//
// 纯静态无状态：调用方只关心"给我一个点"，不需要持有实例，也便于测试单独验证。
public static class SpawnGroundQuery
{
    // 候选点相对地面的最大抬升。给足余量，让"从高空往下找"能覆盖到下层地块，
    // 否则站在二楼平台边缘生成时，采样半径够不到一楼。
    public const float DefaultCeiling = 20f;

    /// <summary>
    /// 把候选点吸附到地面上。
    /// </summary>
    /// <param name="mode">查询策略，通常是 Spawner 上的枚举透传。</param>
    /// <param name="candidate">水平位置已确定的候选点；其 Y 只作为搜索起点。</param>
    /// <param name="groundMask">物理兜底时用的层掩码；为 0 时退化为全部层。</param>
    /// <param name="navMeshAreaMask">NavMesh 采样可用的区域掩码，-1 表示全部区域。</param>
    /// <param name="ceiling">搜索高度，向上抬升多少米开始找。</param>
    /// <param name="outPoint">成功时返回落地点（已包含 Y）。</param>
    /// <param name="usedNavMesh">成功时告知调用方走的是哪条分支，便于诊断。</param>
    /// <returns>找到地面返回 true；两条分支都失败返回 false，调用方应放弃该次生成。</returns>
    public static bool TryResolve(
        SpawnGroundMode mode,
        Vector3 candidate,
        LayerMask groundMask,
        int navMeshAreaMask,
        float ceiling,
        out Vector3 outPoint,
        out bool usedNavMesh)
    {
        outPoint = candidate;
        usedNavMesh = false;
        if (ceiling <= 0f) ceiling = DefaultCeiling;

        Vector3 rayOrigin = candidate + Vector3.up * ceiling;
        // 射线要足够长：从"候选点上方 ceiling"一直打到"候选点下方 ceiling"，
        // 这样即便候选点本身悬在半空也能找到下面的地。
        float rayDistance = ceiling * 2f;

        if (mode != SpawnGroundMode.PhysicsRaycastOnly &&
            TrySampleNavMesh(rayOrigin, rayDistance, navMeshAreaMask, out outPoint))
        {
            usedNavMesh = true;
            return true;
        }

        if (mode != SpawnGroundMode.NavMeshOnly &&
            TryRaycastGround(rayOrigin, rayDistance, groundMask, out outPoint))
        {
            return true;
        }

        return false;
    }

    private static bool TrySampleNavMesh(Vector3 origin, float maxDistance, int areaMask, out Vector3 point)
    {
        // 采样半径取"竖直搜索距离"，让采样球覆盖整条射线路径。
        // 半径给大是刻意的：SamplePosition 找的是"离 origin 最近的导航网格点"，
        // 半径过小会让上下层重叠处的候选点被判定为不可达而白白丢弃。
        var filter = new NavMeshQueryFilter
        {
            areaMask = areaMask,
            agentTypeID = 0
        };

        if (NavMesh.SamplePosition(origin, out NavMeshHit hit, maxDistance, filter))
        {
            point = hit.position;
            return true;
        }

        point = Vector3.zero;
        return false;
    }

    private static bool TryRaycastGround(Vector3 origin, float distance, LayerMask mask, out Vector3 point)
    {
        // mask 为 0 时 QueryTriggerInteraction 之外没有可用的层，
        // 这里显式把 0 解释成"不限层"，避免新手配置漏填导致静默失效。
        int effectiveMask = mask.value == 0 ? ~0 : mask.value;
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, distance, effectiveMask,
                QueryTriggerInteraction.Ignore))
        {
            point = hit.point;
            return true;
        }

        point = Vector3.zero;
        return false;
    }

    /// <summary>
    /// 按给定的圆形范围与角度取一个水平候选点。
    /// </summary>
    /// <param name="center">圆心（Spawner 位置，已拍平到 XZ 平面）。</param>
    /// <param name="radius">圆半径，单位为米。</param>
    /// <param name="innerRadius">内圈半径，用来排除"贴着 Spawner 脚下"的生成点。</param>
    /// <param name="random01">两个 [0,1) 随机数；由调用方传入以便测试注入确定性序列。</param>
    /// <remarks>
    /// 用 sqrt 补偿面积分布：若直接对半径线性取随机，靠近圆心的点会被过度采样
    /// （半径 r 处的周长是 2πr，内圈面积小却和外围一样频繁被选中），
    /// 取 sqrt 后单位面积内的概率才均匀。
    /// </remarks>
    public static Vector3 SampleDisc(Vector3 center, float radius, float innerRadius, Vector2 random01)
    {
        float r = Mathf.Sqrt(random01.x);
        float safeInner = Mathf.Clamp(innerRadius, 0f, radius);
        // 把 [0,1) 的 sqrt 结果重映射到 [inner, radius]，保持内圈排除与均匀性同时成立。
        float innerRatio = radius > 0f ? safeInner / radius : 0f;
        float mapped = Mathf.Lerp(innerRatio * innerRatio, 1f, r);
        float distance = Mathf.Sqrt(mapped) * radius;

        float angle = random01.y * Mathf.PI * 2f;
        return center + new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);
    }
}
