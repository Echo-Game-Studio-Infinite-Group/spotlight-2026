using UnityEngine;

namespace GameJam.Flow
{
    // 终点判定体积：放在最终房出口门洞里，尺寸和位置直接在层级里调。
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BoxCollider))]
    public sealed class FlowFinishArea : MonoBehaviour
    {
        private static readonly Collider[] Hits = new Collider[32];
        private BoxCollider _area;

        private void Awake() => _area = GetComponent<BoxCollider>();

        public bool ContainsPlayer(Transform player)
        {
            Bounds bounds = _area.bounds;
            int count = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents, Hits, Quaternion.identity,
                ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
                if (Hits[i] != null && Hits[i].transform.root == player.root) return true;
            return false;
        }
    }
}
