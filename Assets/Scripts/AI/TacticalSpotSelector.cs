using UnityEngine;
using UnityEngine.AI;

// 远程敌人警觉后的战术位置选择：不站着打，先找「看得见、又离玩家够远」的点再开火
// 策划案 8.(2)：瞄准发射瞬间的玩家位置，且发射前会尝试远离玩家——本类就是「远离到哪」的答案
// 三种策略并存是因为玩法仍在预研：靠 Inspector 切枚举就能对比手感，不必改代码重编译
public enum PositioningPolicy
{
    FastestWithVision,    // 压迫型：只挑最快能到的有视野点，开火节奏最紧
    FarthestWithVision,   // 风筝型：在可接受路程内挑最远的点，主打生存与封锁走位
    Hold                  // 原地型：完全不位移，零射线开销，高密度怪群的兜底
}

public class TacticalSpotSelector : MonoBehaviour
{
    [Header("策略")]
    public PositioningPolicy Policy = PositioningPolicy.FarthestWithVision;

    [Header("采样")]
    public int CandidateCount = 8;
    public float MinRadius = 4f;
    public float MaxRadius = 14f;
    public float MinDistanceToPlayer = 5f;   // 比这更近的候选直接淘汰，远程怪不该贴脸

    [Header("评分权重（风筝型用）")]
    public float DistanceWeight = 1f;        // 离玩家越远越加分
    public float TravelWeight = 1.4f;        // 路程惩罚。>1 表示不为「更远」付出过多跑动

    [Header("节流（高密度怪群靠这个保帧率）")]
    public float EvaluationCooldown = 0.7f;
    public static int MaxEvaluationsPerFrame = 4;

    [Header("视线检测")]
    public LayerMask BlockingLayers = ~0;
    public float EyeHeight = 1.5f;

    private static int _budgetFrame = -1;
    private static int _usedThisFrame;

    private float _nextEvaluationTime;

    // 选出目标点。返回 false 表示本次不移动（没找到、被节流、或策略就是原地）
    public bool TryPickSpot(Vector3 from, Vector3 playerPosition, out Vector3 spot)
    {
        spot = from;

        if (Policy == PositioningPolicy.Hold) return false;

        if (!TryConsumeBudget()) return false;

        // 冷却走不缩放时间：时停期间不需要重新规划位置，否则会白白吃掉射线预算
        if (TimeManager.UnscaledTime < _nextEvaluationTime) return false;
        _nextEvaluationTime = TimeManager.UnscaledTime + EvaluationCooldown;

        bool found = false;
        float bestScore = float.NegativeInfinity;
        Vector3 best = from;

        // 角度整体加随机偏移，避免所有敌人采到同一批点、往同一处挤
        float step = 360f / Mathf.Max(1, CandidateCount);
        float offset = Random.Range(0f, step);

        for (int i = 0; i < CandidateCount; i++)
        {
            float angle = (offset + i * step) * Mathf.Deg2Rad;
            float radius = Random.Range(MinRadius, MaxRadius);
            Vector3 candidate = from + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

            ProjectOnNavMesh(ref candidate);

            if (Vector3.Distance(candidate, playerPosition) < MinDistanceToPlayer) continue;
            if (!HasLineOfSight(candidate, playerPosition)) continue;

            float travel = Vector3.Distance(from, candidate);
            float score = Policy == PositioningPolicy.FastestWithVision
                ? -travel
                : (Vector3.Distance(candidate, playerPosition) / MaxRadius) * DistanceWeight
                  - (travel / MaxRadius) * TravelWeight;

            if (score <= bestScore) continue;

            bestScore = score;
            best = candidate;
            found = true;
        }

        if (!found) return false;

        spot = best;
        return true;
    }

    // 全场每帧最多允许几个敌人做评估。肉鸽式密度下，不加这个会一帧打出上百条射线
    private static bool TryConsumeBudget()
    {
        if (Time.frameCount != _budgetFrame)
        {
            _budgetFrame = Time.frameCount;
            _usedThisFrame = 0;
        }

        if (_usedThisFrame >= MaxEvaluationsPerFrame) return false;

        _usedThisFrame++;
        return true;
    }

    // 工程还没烘焙导航网格时保持原点不动，避免整个选点逻辑因为没 NavMesh 就失效
    private void ProjectOnNavMesh(ref Vector3 point)
    {
        if (!NavMesh.SamplePosition(point, out NavMeshHit hit, MaxRadius * 0.5f, NavMesh.AllAreas)) return;
        point = hit.position;
    }

    private bool HasLineOfSight(Vector3 from, Vector3 to)
    {
        Vector3 eye = from + Vector3.up * EyeHeight;
        Vector3 target = to + Vector3.up * EyeHeight;
        return !Physics.Linecast(eye, target, BlockingLayers);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, MinRadius);
        Gizmos.DrawWireSphere(transform.position, MaxRadius);
    }
}
