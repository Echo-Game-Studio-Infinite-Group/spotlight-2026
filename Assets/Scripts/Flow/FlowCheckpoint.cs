using UnityEngine;

namespace GameJam.Flow
{
    public sealed class FlowCheckpoint : MonoBehaviour
    {
        [InspectorName("记录点编号")] public string Id = "entry";
        [InspectorName("优先级")] public int Priority;
        [InspectorName("允许再次覆盖")] public bool AllowReactivation;
        [InspectorName("允许死亡扣为负数")] public bool AllowNegativeChaos;
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
