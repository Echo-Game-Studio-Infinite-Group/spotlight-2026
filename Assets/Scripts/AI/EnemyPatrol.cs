using UnityEngine;

// 敌人漫游 / 路点巡逻原型，对应策划案 8.(1)「成群出现，寻路带有一定程度的漫游效果」
// 约束（AGENTS.md 第 3 条）：位移手动积分，CharacterController 只用来做碰撞查询，不接刚体
// 约束（AGENTS.md 第 5 条）：delta 一律取 TimeManager.WorldDeltaTime，不得直读 Time.deltaTime
// 约束（AGENTS.md 第 2 条）：可调参数全部暴露到 Inspector，不在代码里写死数值
// 选型说明：暂不引入 NavMesh 烘焙。21 天工期内，路点 + 随机漫游足以验证成群杂兵的体感，
// 也不必为一张测试地图额外维护导航网格。
[RequireComponent(typeof(CharacterController))]
public class EnemyPatrol : MonoBehaviour
{
    [Header("巡逻")]
    public Transform[] Waypoints;          // 留空则自动退化为出生点附近的随机漫游
    public bool PingPong = true;           // 走到末端后是折返还是跳回起点
    public float ArriveDistance = 0.5f;
    public float WaitTime = 1f;            // 每个点位停留多久

    [Header("漫游（Waypoints 留空时生效）")]
    public float WanderRadius = 6f;

    [Header("运动")]
    public float MoveSpeed = 3f;
    public float TurnSpeed = 180f;         // 度/秒
    public float Gravity = 20f;

    private CharacterController _controller;
    private Vector3 _homePoint;
    private Vector3 _targetPoint;
    private int _waypointIndex;
    private int _step = 1;
    private float _waitTimer;
    private float _verticalSpeed;

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        _homePoint = transform.position;
        PickNextTarget();
    }

    private void Update()
    {
        // InHitStop 期间 WorldDeltaTime 已被压到近 0，敌人会自然停住，无需额外分支
        float delta = TimeManager.WorldDeltaTime;

        if (_waitTimer > 0f)
        {
            _waitTimer -= delta;
            if (_waitTimer <= 0f) PickNextTarget();
            ApplyGravity();
            Stick();
            return;
        }

        if (FlatDistance(transform.position, _targetPoint) <= ArriveDistance)
        {
            _waitTimer = WaitTime;
            return;
        }

        TurnTowards(_targetPoint, delta);
        ApplyGravity();

        Vector3 motion = transform.forward * MoveSpeed;
        motion.y = _verticalSpeed;
        _controller.Move(motion * delta);
    }

    private void PickNextTarget()
    {
        if (Waypoints == null || Waypoints.Length == 0)
        {
            Vector2 offset = Random.insideUnitCircle * WanderRadius;
            _targetPoint = _homePoint + new Vector3(offset.x, 0f, offset.y);
            return;
        }

        _waypointIndex += _step;

        if (_waypointIndex >= Waypoints.Length)
        {
            if (PingPong)
            {
                _step = -1;
                _waypointIndex = Waypoints.Length - 2;
            }
            else
            {
                _waypointIndex = 0;
            }
        }
        else if (_waypointIndex < 0)
        {
            _step = 1;
            _waypointIndex = Mathf.Min(1, Waypoints.Length - 1);
        }

        _waypointIndex = Mathf.Clamp(_waypointIndex, 0, Waypoints.Length - 1);
        _targetPoint = Waypoints[_waypointIndex].position;
    }

    private void TurnTowards(Vector3 point, float delta)
    {
        Vector3 flat = point - transform.position;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.0001f) return;

        // 只算水平朝向角，再写成 Euler(0, yaw, 0)：
        // 敌人的胶囊必须永远直立，不能因为目标点有高度差而在 X/Z 轴上前后左右倾。
        // 直接赋值四元数会带上俯仰分量，这里强行抹平，等于给姿态上了一道锁
        float targetYaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        float yaw = Mathf.MoveTowardsAngle(transform.eulerAngles.y, targetYaw, TurnSpeed * delta);
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    // 保险：任何来源（物理推挤、预制体自带旋转、调试时手动拽歪）造成的倾斜都在最后抹平
    private void LateUpdate()
    {
        transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
    }

    private void ApplyGravity()
    {
        // CharacterController 自身不带重力。每帧补一段向下的速度，否则走下坡时会飘起来
        if (_controller.isGrounded)
        {
            _verticalSpeed = -2f;
        }
        else
        {
            _verticalSpeed -= Gravity * TimeManager.WorldDeltaTime;
        }
    }

    // 站着不动时也得继续接地，不然停留在斜坡上会被后续帧慢慢推走
    private void Stick()
    {
        Vector3 settle = Vector3.up * _verticalSpeed;
        _controller.Move(settle * TimeManager.WorldDeltaTime);
    }

    private static float FlatDistance(Vector3 from, Vector3 to)
    {
        from.y = 0f;
        to.y = 0f;
        return Vector3.Distance(from, to);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;

        if (Waypoints != null && Waypoints.Length > 0)
        {
            for (int i = 0; i < Waypoints.Length; i++)
            {
                if (Waypoints[i] == null) continue;

                Gizmos.DrawWireSphere(Waypoints[i].position, 0.3f);
                if (i > 0 && Waypoints[i - 1] != null)
                {
                    Gizmos.DrawLine(Waypoints[i - 1].position, Waypoints[i].position);
                }
            }
            return;
        }

        Vector3 center = Application.isPlaying ? _homePoint : transform.position;
        Gizmos.DrawWireSphere(center, WanderRadius);
    }
}
