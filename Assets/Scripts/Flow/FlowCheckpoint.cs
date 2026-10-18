using UnityEngine;

namespace GameJam.Flow
{
    public sealed class FlowCheckpoint : MonoBehaviour
    {
        [Tooltip("同一箱庭内唯一；各差分应保留相同编号，保证恢复时能找到对应记录点。")]
        [InspectorName("记录点编号")] public string Id = "entry";
        [Tooltip("同一房间内，较低优先级默认不能覆盖较高优先级记录点。")]
        [InspectorName("优先级")] public int Priority;
        [Tooltip("允许本记录点覆盖同房间中更高优先级的另一个记录点；相同编号不会重复提交。")]
        [InspectorName("允许回覆盖高优先级记录点")] public bool AllowReactivation;
        [Tooltip("从此记录点死亡恢复时，允许混乱度扣到普通下限以下；需要特殊路线支持。")]
        [InspectorName("允许死亡扣为负数")] public bool AllowNegativeChaos;
        [Tooltip("玩家进入或高速扫过此半径时提交记录点，单位为米。")]
        [InspectorName("触发半径"), Min(0.1f)] public float Radius = 2.5f;
        private bool _inside;
        public bool Poll(Vector3 previous, Vector3 position)
        {
            bool inside = (position - transform.position).sqrMagnitude <= Radius * Radius;
            Vector3 segment = position - previous;
            float ratio = segment.sqrMagnitude > 0f ? Mathf.Clamp01(Vector3.Dot(transform.position - previous, segment) / segment.sqrMagnitude) : 0f;
            bool crossed = (previous + segment * ratio - transform.position).sqrMagnitude <= Radius * Radius;
            bool entered = crossed && !_inside; _inside = inside; return entered;
        }
        public void ResetProximity() => _inside = false;
        private void OnDrawGizmosSelected() { Gizmos.color = Color.cyan; Gizmos.DrawWireSphere(transform.position, Radius); }
    }
}
