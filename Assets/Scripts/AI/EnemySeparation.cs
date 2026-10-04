using UnityEngine;

// 警觉后的分散力：把挤在一起的敌人互相推开
// 设计约束（用户与学长确认）：发现玩家前**不启用**。巡逻 / 漫游阶段各自乱走才自然，
// 提前分散会让怪群自己先摊平，失去策划案要的「成群出现」压迫感
// 性能：用重叠球 + 固定数组限制邻居数量，避免高密度怪群退化成两两比较
public class EnemySeparation : MonoBehaviour
{
    [Header("分散")]
    public float SeparationRadius = 2.2f;
    public float SeparationStrength = 1.5f;
    public int MaxNeighbours = 6;
    public LayerMask EnemyLayers = ~0;

    private readonly Collider[] _neighbours = new Collider[16];

    // 返回本帧应该额外施加的水平位移方向（已按强度缩放）。没有邻居时返回零向量，调用方直接跳过
    public Vector3 ComputeOffset(Vector3 selfPosition)
    {
        int count = Physics.OverlapSphereNonAlloc(selfPosition, SeparationRadius, _neighbours, EnemyLayers);
        if (count <= 1) return Vector3.zero;

        Vector3 push = Vector3.zero;
        int considered = 0;

        for (int i = 0; i < count && considered < MaxNeighbours; i++)
        {
            Collider other = _neighbours[i];
            if (other == null || other.transform == transform) continue;

            Vector3 away = selfPosition - other.transform.position;
            away.y = 0f;

            float distance = away.magnitude;

            // 完全重合时方向是零向量，推力会失效并让两个敌人永久叠在一起，这里随机挑一个方向解开
            if (distance < 0.0001f)
            {
                away = Random.insideUnitSphere;
                away.y = 0f;
            }
            else
            {
                // 距离越近推力越强，远处的同伴不该产生和贴脸一样的干扰
                away = (away / distance) * (1f - Mathf.Clamp01(distance / SeparationRadius));
            }

            push += away;
            considered++;
        }

        if (considered == 0) return Vector3.zero;

        return push.normalized * SeparationStrength;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.magenta;
        Gizmos.DrawWireSphere(transform.position, SeparationRadius);
    }
}
