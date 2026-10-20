using UnityEngine;
using UnityEngine.AI;

// 敌人之间的软分离。
//
// 为什么需要它：敌人之间的位移全靠 NavMeshAgent，而 CharacterController 只在「自己调 Move()」时
// 才做碰撞检测 —— 敌人从不 Move()，所以两只敌人的胶囊可以互相穿过去。
// 唯一的防线是 NavMeshAgent 的 local avoidance，但它在 isStopped = true 之后就不工作了，
// 而近战敌人一到射程（1m）就停步，于是全部叠在距玩家 1m 的同一圈上。
//
// 为什么不用触发箱：敌人根节点没有 Rigidbody（Rigidbody 在 AttackHitbox 子节点上，kinematic），
// 两个静态碰撞体之间不会产生 OnTriggerStay。这里改用非分配重叠查询轮询，
// 不改预制体结构，也和工程既有的 SightCheck / Hitbox 风格一致。
//
// 为什么必须走 agent.Move：NavMeshAgent 每帧直接改写 Transform，
// 直接写 transform.position 会在下一帧被覆盖，等于没推。
//
// 执行顺序放在基类之后：EnemyBase 是默认顺序，EnemyHeavy 是 50，这里 100，
// 保证「本帧的停步/追击已经处理完」再施加分离位移。
[DefaultExecutionOrder(100)]
[DisallowMultipleComponent]
[RequireComponent(typeof(NavMeshAgent))]
public sealed class EnemySeparation : MonoBehaviour
{
    [Header("分离")]
    [Tooltip("期望的敌人中心间距（米）。应略大于两倍胶囊半径：近战敌人胶囊半径 0.382，2×0.382≈0.765，所以填 0.8 左右。")]
    [SerializeField, Min(0.01f)] private float _separation = 0.8f;

    [Tooltip("推开速度（米/秒）。0 = 关闭分离。")]
    [SerializeField, Min(0f)] private float _pushSpeed = 1.5f;

    [Tooltip("重叠深度小于此值就不推，避免刚好贴合时每帧微调造成抖动。")]
    [SerializeField, Min(0f)] private float _deadZone = 0.02f;

    [Tooltip("单帧位移上限，防止一帧被弹飞。")]
    [SerializeField, Min(0f)] private float _maxStep = 0.1f;

    [Header("查询")]
    // 只查敌人层：地面的墙和地板都在 Default(0)，混进来会让每次查询多返回一堆无关碰撞体。
    [Tooltip("参与分离的层。默认 Enemy(6)。不要把地面/墙所在的层加进来。")]
    [SerializeField] private LayerMask _layerMask = 1 << 6;

    // 一次查询最多收多少个邻居。拥挤场景里截断只会让这一帧的推力偏弱，不会出错。
    private readonly Collider[] _neighbours = new Collider[16];

    private NavMeshAgent _agent;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
    }

    private void FixedUpdate()
    {
        if (_pushSpeed <= 0f) return;
        if (_agent == null || !_agent.isActiveAndEnabled || !_agent.isOnNavMesh) return;

        // 用世界时间：时停/顿帧期间敌人整体不动，分离也应该一起停下，否则会把敌人推着走。
        float dt = TimeManager.WorldFixedDeltaTime;
        if (dt <= 0f) return;

        Vector3 self = transform.position;
        int count = Physics.OverlapSphereNonAlloc(self, _separation, _neighbours, _layerMask,
            QueryTriggerInteraction.Ignore);
        if (count == 0) return;

        // 累加所有邻居的推离方向，并按重叠深度加权：
        // 越挤推得越狠，松开得快，也不会在临界处来回弹。
        Vector3 push = Vector3.zero;
        for (int i = 0; i < count; i++)
        {
            Collider other = _neighbours[i];
            if (other == null || other.transform.IsChildOf(transform)) continue;

            Vector3 offset = self - other.transform.position;
            // 只在水平面上分离：垂直方向交给 NavMeshAgent 贴地，插一脚会和贴地打架。
            offset.y = 0f;

            float distance = offset.magnitude;
            float overlap = _separation - distance;
            if (overlap <= _deadZone) continue;

            // 正好重合时径向方向没有意义，用自身右方向兜底，避免每帧随机换向（同 EnemyTopSlide 的处理）。
            Vector3 direction = distance > 0.0001f ? offset / distance : transform.right;
            push += direction * overlap;
        }

        if (push.sqrMagnitude <= 0.000001f) return;

        float step = Mathf.Min(_pushSpeed * dt, _maxStep);
        _agent.Move(push.normalized * step);
    }
}
