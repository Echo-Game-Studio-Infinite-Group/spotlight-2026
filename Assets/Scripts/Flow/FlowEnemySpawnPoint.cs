using UnityEngine;
using UnityEngine.AI;

namespace GameJam.Flow
{
    [DisallowMultipleComponent]
    public sealed class FlowEnemySpawnPoint : MonoBehaviour
    {
        [InspectorName("出生点编号"), Tooltip("同一地图模板内唯一。新增或复制后可在差分窗口分配编号；移动或改名时保持编号不变。")]
        public string Id;
        [InspectorName("中文备注"), Tooltip("在差分表和场景中显示，例如“入口左侧”。")]
        public string DisplayName = "敌人出生点";
        [InspectorName("落地搜索距离"), Tooltip("只在出生点附近搜索地面或导航，避免生成到其他楼层。"), Min(.01f)]
        public float GroundSearchDistance = 2f;
        [InspectorName("最大水平吸附距离"), Tooltip("导航落点偏离出生点的水平距离上限，单位为米。"), Min(0f)]
        public float MaximumHorizontalSnap = .5f;
        [InspectorName("离地偏移"), Tooltip("生成位置相对地面的高度偏移，单位为米。"), Min(0f)]
        public float HeightOffset = .05f;
        [InspectorName("地面碰撞层"), Tooltip("没有启用导航代理的敌人使用这些碰撞层向下找地面。")]
        public LayerMask GroundMask = ~0;

        public bool TryGetPose(GameObject enemyPrefab, out Pose pose, out string error)
        {
            pose = default;
            error = string.Empty;
            Vector3 candidate = transform.position;
            Vector3 ground;
            NavMeshAgent agent = enemyPrefab.GetComponent<NavMeshAgent>();
            if (agent != null && agent.enabled)
            {
                var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = agent.areaMask };
                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, GroundSearchDistance, filter))
                { error = "附近没有适合该敌人的导航网格"; return false; }
                ground = hit.position;
                if (Vector3.ProjectOnPlane(ground - candidate, Vector3.up).magnitude > MaximumHorizontalSnap)
                { error = "导航落点超出最大水平吸附距离"; return false; }
            }
            else if (!SpawnGroundQuery.TryResolve(SpawnGroundMode.PhysicsRaycastOnly, candidate, GroundMask,
                         NavMesh.AllAreas, GroundSearchDistance, out ground, out _))
            { error = "附近没有可用地面"; return false; }
            pose = new Pose(ground + Vector3.up * HeightOffset, Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
            return true;
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(transform.position, .3f);
            Gizmos.DrawRay(transform.position, transform.forward);
        }
    }
}
