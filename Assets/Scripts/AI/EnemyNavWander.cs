using UnityEngine;
using UnityEngine.AI;

// 游荡取点的范围。地图是提前做好的静态图，默认直接在整张导航网格上选点
public enum WanderScope
{
    WholeMap,   // 整张地图随机取点，走起来会覆盖全图
    Local       // 只在自身周围 SampleRadius 内取点，适合把敌人限制在某个区域
}

// 基于 NavMesh 的随机游荡：挑一个网格上的可达点，沿算出来的路径走过去，到了再换一个
// 与 EnemyPatrol 的区别：EnemyPatrol 走预设路点且是直线冲过去；本类不预设路线，靠 NavMesh 绕开障碍
// 约束（AGENTS.md 第 3 条）：NavMesh 只负责算路径，位移仍由 CharacterController 手动积分
// 约束（AGENTS.md 第 5 条）：时间走 TimeManager.WorldDeltaTime，敌人属世界时间层
// 上下坡约定：可行走的坡面（角度在 MaxWalkableSlope 以内）不做方向投影，交给 CharacterController
//   自己爬（与 PlayerMotor 同一套做法）——投影到坡面会削掉 cosθ 那部分水平速度，还会和贴地下压拉锯；
//   陡于上限的面一律当墙，推进方向沿它滑动，敌人就不会顶着坡角原地磨
// 导航网格约定：路径起点先 SamplePosition 吸附，推进方向受 NavMesh.Raycast 边界约束——
//   烘焙对坡道侧壁与平台边缘会向内侵蚀 agentRadius（0.5m），胶囊半径正好也是 0.5m，
//   贴边站就会掉进网格空洞里，直接拿 transform.position 当路径起点会次次失败
[RequireComponent(typeof(CharacterController))]
public class EnemyNavWander : MonoBehaviour
{
    [Header("游荡")]
    public WanderScope Scope = WanderScope.WholeMap;
    public float SampleRadius = 25f;        // 仅 Local 模式生效
    public float MinTravelDistance = 4f;    // 目标太近就重选，免得蹭两步又换点
    public int SampleTries = 8;
    public float ArriveDistance = 0.6f;
    public float WaitTime = 1.5f;
    public float RepathInterval = 0.8f;     // 路径重算间隔，每帧算会把 CPU 吃光

    [Header("运动")]
    public float MoveSpeed = 3f;
    public float TurnSpeed = 180f;          // 度/秒
    public float Gravity = 20f;

    [Header("同伴避让")]
    public bool AvoidNeighbours = true;     // 不避让的话，几个敌人挤在一起会互相顶死
    public LayerMask NeighbourLayers = ~0;
    public float NeighbourRadius = 1.8f;
    public float AvoidStrength = 1.6f;
    public float StuckTimeout = 1.5f;       // 这么久没更靠近终点就判定卡住：先丢拐点，再丢目标
    public float ProgressEpsilon = 0.25f;   // 判定"更靠近终点"的最小距离增量：太小会被蹭出来的新低反复清零
    public float NetProgressWindow = 2.5f;  // 净位移观测窗
    public float NetProgressDistance = 1.2f; // 窗口内净移动不足它就算卡住（治"贴着边界来回蹭"）
                                             // 实测贴边蠕行约 0.2~0.3 m/s，门槛必须明显高于它：
                                             // 0.6m/2.5s(=0.24 m/s) 量级相同抓不住，1.2m/2.5s(=0.48 m/s) 才拦得住；
                                             // 敌人正常速度 3 m/s，0.5 m/s 只有 17%，不会误伤正常赶路
    public float NetSampleInterval = 0.25f; // 净位移采样间隔（窗口 ÷ 间隔 = 参与比较的样本数）
    public float PathImprovementRatio = 0.8f; // 重算出的新路径要比当前剩余路程短这么多才替换
                                              // 必须有它：两条近乎等长的路线在分水岭附近，
                                              // 每 0.8 秒重算都会选到另一条 → 敌人满速左右横跳
                                              // （实测 30 秒掉头 45 次、路程 60.7m、净位移 0m）
    public float OscillationWindow = 5f;      // 徘徊兜底观测窗
    public float OscillationTravel = 8f;      // 窗口内走了这么多路程……
    public float OscillationNet = 1.5f;       // ……净位移却不足这么点 → 判定在跑圈，换个目标

    [Header("斜坡与阻挡")]
    public LayerMask GroundLayers = ~0;
    public float GroundProbe = 1.5f;        // 向下探地的距离，要能覆盖脚下的坡面
    // 陡于此角度的面按墙处理。实际生效值取 min(此值, 胶囊 slopeLimit)：
    // 胶囊爬得上去的坡一律交给它自己爬（上下文包 §7.5：可行走的坡别做方向投影），
    // 爬不上去的必须当墙，否则敌人会顶着坡面磨。默认 60 表示"以胶囊 slopeLimit 为准"，
    // 以后按 §7.5 把 Slope Limit 从 45 调到 55~60 时这里自动跟随，不用再改代码
    [Tooltip("陡于此角度的面按墙处理。实际生效值取 min(此值, CharacterController.slopeLimit)")]
    public float MaxWalkableSlope = 60f;
    public float GroundStickSpeed = 2f;     // 贴地下压速度：走下坡时把胶囊按在坡面上

    [Header("导航网格边界")]
    public float StartSnapRadius = 4f;      // 路径起点吸附半径：贴墙站时脚下可能没落在网格上
    public float BoundaryProbe = 0.35f;     // 沿网格边界滑动的前探距离，防止走出可行走区域
    public bool KeepOnNavMesh = true;       // 关掉后敌人会走出网格边界（坡道/平台边缘）并掉下去
    public bool ReturnToNavMesh = true;     // 已经掉到网格外时主动走回最近的网格点
    public float OffMeshEnterDistance = 0.35f;  // 离网格超过它才启动"回网格"（进入阈值）
    public float OffMeshEpsilon = 0.08f;    // 回到这个距离以内才算"已在网格上"（退出阈值）
    public float RecoverTimeout = 2f;       // 回网格的最长尝试时间，超时就交回普通游荡 + 卡住判定

    private CharacterController _controller;
    private NavMeshPath _path;
    private NavMeshPath _scratchPath;   // 重算路径时先算到这里比较，满意了才替换 _path（见 RepathToCurrentTarget）
    private NavMeshTriangulation _triangulation;   // 整图模式的网格缓存
    private float[] _cumulativeAreas;              // 按三角形累加的面积，用于加权抽样
    private float _totalArea;
    private int _cornerIndex;
    private float _waitTimer;
    private float _repathTimer;
    private float _verticalSpeed;
    private bool _hasPath;

    private readonly Collider[] _neighbours = new Collider[16];

    private float _walkableSlopeDot = 1f;                          // 可行走上限的余弦，省得每次接触都算 acos
    private Vector3 _wallNormal = Vector3.up;
    private bool _wallTouch;
    private float _bestRemaining = float.PositiveInfinity;         // 离终点最近到过的平面距离
    private float _stuckTimer;
    private readonly Vector3[] _recentPositions = new Vector3[24]; // 净位移/总路程环形缓冲（24 × 0.25s = 6s，够 5s 徘徊窗口用）
    private int _recentCount;
    private int _recentIndex;
    private float _recentTimer;
    private bool _returningToMesh;                                 // 本帧在做"走回网格"的恢复
    private float _returnTimer;
    private Vector3 _meshReturnTarget;                             // 已选定的回网格目标：不许每帧重挑，否则会在边界左右摆
    private bool _holdPosition;                                    // 决策层要求原地待命（守战术位）

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        _path = new NavMeshPath();

        // 可行走上限取"Inspector 值"与"胶囊 slopeLimit"的较小者：
        // 比 slopeLimit 更陡的面 CharacterController 无论如何都爬不上去，
        // 若还当成可行走面，敌人就会顶着它磨——上坡卡坡角就是这么来的
        float slope = Mathf.Clamp(Mathf.Min(MaxWalkableSlope, _controller.slopeLimit), 0f, 89f);
        _walkableSlopeDot = Mathf.Cos(slope * Mathf.Deg2Rad);
    }

    private void Update()
    {
        // InHitStop 期间 WorldDeltaTime 已被压到近 0，敌人自然停住，无需额外分支
        float delta = TimeManager.WorldDeltaTime;

        if (_waitTimer > 0f)
        {
            _waitTimer -= delta;
            ApplyGravity();
            Stick();
            return;
        }

        if (!_hasPath)
        {
            // 没路径时也要贴地：可能正停在坡面上等下一次取点
            ApplyGravity();
            Stick();
            if (!_holdPosition) PickNewDestination();
            return;
        }

        if (_cornerIndex >= _path.corners.Length)
        {
            _waitTimer = WaitTime;
            _hasPath = false;
            return;
        }

        Vector3 corner = _path.corners[_cornerIndex];

        if (ReachedCorner(corner))
        {
            _cornerIndex++;
            return;
        }

        TurnTowards(corner, delta);
        ApplyGravity();
        ProbeSupportSurface();

        // 朝拐点推进，而不是朝身体正面推进：TurnSpeed 180°/s 配 MoveSpeed 3m/s 的转弯半径约 0.95m，
        // 比 ArriveDistance 还大，按朝向走会绕着拐点外圈转，永远进不了到达半径
        Vector3 toCorner = corner - transform.position;
        toCorner.y = 0f;
        Vector3 moveDir = toCorner.normalized;

        // 已经掉到网格外（坡道边缘那圈侵蚀带）时优先走回去：
        // 只朝拐点走的话，目标方向可能与网格边界相切，敌人会沿着边界一直蹭、永远回不来
        if (TryGetMeshReturnDirection(delta, out Vector3 returnDir)) moveDir = returnDir;

        Vector3 wish = moveDir * MoveSpeed + ComputeAvoidance();
        wish = SlideAlongObstacle(wish);

        // 回网格途中不做边界滑动：那正是把敌人挡在网格外的原因
        if (!_returningToMesh) wish = SlideAlongNavMeshBoundary(wish, delta);

        Vector3 motion = wish;
        motion.y += _verticalSpeed;

        // 墙法线在本次 Move 的碰撞回调里重新采集，避免一个旧接触长期改方向
        _wallTouch = false;
        _controller.Move(motion * delta);

        // 回网格期间不计进度：这时的推进方向本来就不是拐点方向，计了会被误判成卡住
        if (!_returningToMesh) TrackProgress(delta);

        // 走偏或路被堵住时重算，避免贴着墙原地磨
        _repathTimer += delta;
        if (_repathTimer >= RepathInterval)
        {
            _repathTimer = 0f;
            RepathToCurrentTarget();
        }
    }

    // 脚下探地：只用来判断支撑面能不能走，陡面直接记成墙。
    // 早一帧就知道"脚下这块爬不上去"，这一帧就沿它滑开，而不是顶着它磨
    private void ProbeSupportSurface()
    {
        // 从腰高往下打一条短射线取最近的支撑面；够不到地面（腾空）就不用管墙
        if (!Physics.Raycast(transform.position + Vector3.up * 0.2f, Vector3.down,
                out RaycastHit hit, GroundProbe, GroundLayers))
        {
            return;
        }

        if (hit.normal.y >= _walkableSlopeDot) return;

        _wallNormal = hit.normal;
        _wallTouch = true;
    }

    // 推进方向被墙/陡面挡住时，把它投影到那个面上：顺着面滑是唯一能脱困的方向，
    // 硬推会被 CharacterController 的 slopeLimit 判成撞墙，位移为零（表现为卡在坡角）
    private Vector3 SlideAlongObstacle(Vector3 wish)
    {
        if (!_wallTouch) return wish;

        // 已经在往墙外走就别投影：投影会把"离开墙"的法线分量也削掉，敌人会顺着墙一路蹭下去
        if (Vector3.Dot(wish, _wallNormal) >= 0f) return wish;

        Vector3 slid = Vector3.ProjectOnPlane(wish, _wallNormal);
        slid.y = 0f;   // 沿墙滑动只保留水平分量，竖直方向交给重力与贴地下压

        // 正对墙面时投影结果是零，滑不动；交给卡住判定去换拐点/换目标
        if (slid.sqrMagnitude < 0.0001f) return Vector3.zero;

        // 投影会缩短长度，按原速补回来，否则贴墙时会越走越慢直到彻底站住
        return slid.normalized * wish.magnitude;
    }

    // 走在导航网格边界上时沿边界滑：不处理的话，敌人会直接走出可行走区域
    // （坡道侧边、平台边缘），落到网格外——那里的路径起点算不出来，它会当场卡死
    private Vector3 SlideAlongNavMeshBoundary(Vector3 wish, float delta)
    {
        if (!KeepOnNavMesh || wish.sqrMagnitude < 0.0001f) return wish;

        Vector3 direction = wish.normalized;
        float reach = wish.magnitude * delta + BoundaryProbe;

        if (!NavMesh.Raycast(transform.position, transform.position + direction * reach,
                out NavMeshHit hit, NavMesh.AllAreas))
        {
            return wish;   // 前方还在网格内，照直走
        }

        Vector3 slid = Vector3.ProjectOnPlane(wish, hit.normal);
        slid.y = 0f;

        // 正对边界时滑不动，交给卡住判定去换拐点/换目标
        return slid.sqrMagnitude < 0.0001f ? Vector3.zero : slid.normalized * wish.magnitude;
    }

    // 判断自己是否已经掉到导航网格外，并给出"走回最近的网格点"的方向。
    // 这是"卡在坡道边缘"的最后一道保险：边界滑动只能防止走出去，防不住已经被挤出去的情况
    // （贴边站时胶囊中心本来就在侵蚀带里），而当时的推进方向可能与边界相切，越走越回不来
    private bool TryGetMeshReturnDirection(float delta, out Vector3 direction)
    {
        direction = Vector3.zero;

        if (!ReturnToNavMesh)
        {
            _returningToMesh = false;
            return false;
        }

        // 进入阈值：只有真掉出去（超过 OffMeshEnterDistance）才启动回网格；
        // 退出用更小的 OffMeshEpsilon —— 两个阈值必须分开，否则在边界上会高频互切：
        // 实测敌人会卡在离网格 0.43m 处每 0.1~0.3 秒"回网格/放弃"来回翻，1.6 秒只挪 3 厘米（就是左右徘徊）
        if (!_returningToMesh &&
            NavMesh.SamplePosition(transform.position, out NavMeshHit enterHit, StartSnapRadius, NavMesh.AllAreas))
        {
            Vector3 enterOffset = enterHit.position - transform.position;
            enterOffset.y = 0f;
            if (enterOffset.magnitude <= OffMeshEnterDistance)
            {
                _returnTimer = 0f;
                return false;
            }
        }

        // 已经在回网格的路上：锁着目标走到 OffMeshEpsilon 以内才收工
        if (_returningToMesh)
        {
            Vector3 heading = _meshReturnTarget - transform.position;
            heading.y = 0f;

            if (heading.magnitude > OffMeshEpsilon)
            {
                _returnTimer += delta;
                if (_returnTimer <= RecoverTimeout)
                {
                    direction = heading.normalized;
                    return true;
                }

                _returningToMesh = false;   // 目标一直走不到就放弃，交回普通游荡 + 卡住判定
                return false;
            }

            _returningToMesh = false;
            _returnTimer = 0f;
            return false;
        }

        if (!NavMesh.SamplePosition(transform.position, out NavMeshHit hit, StartSnapRadius, NavMesh.AllAreas))
        {
            _returningToMesh = false;
            return false;
        }

        Vector3 offset = hit.position - transform.position;
        offset.y = 0f;

        if (offset.magnitude <= OffMeshEpsilon)
        {
            _returningToMesh = false;   // 已经在网格上（或贴着边界内侧）
            _returnTimer = 0f;
            return false;
        }

        _meshReturnTarget = hit.position;   // 记下这次选中的点，下一帧不许换（见上面的抖动说明）
        _returningToMesh = true;
        direction = offset.normalized;
        return true;
    }

    // 路径起点先吸附到导航网格上。这是"卡在坡道边缘走不动"的关键修复：
    // 烘焙会从坡道两侧的垂直壁向内侵蚀 agentRadius（0.5m），胶囊半径正好也是 0.5m，
    // 敌人贴着坡侧边缘站时 transform.position 就落在网格空洞里，CalculatePath 每次都返回 false，
    // 于是每次取点都失败、只能等 WaitTime 再试——旧版在这里是死循环，卡住检测也不会被执行
    private bool TryGetPathStart(out Vector3 start)
    {
        if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, StartSnapRadius, NavMesh.AllAreas))
        {
            start = hit.position;
            return true;
        }

        start = transform.position;
        return false;
    }

    // CharacterController 的真实接触是"谁挡住了我"最可靠的来源
    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        // 脚下坡面的接触不算墙，否则正常沿坡行走会被自己误判成撞墙
        if (hit.normal.y >= _walkableSlopeDot) return;

        // 触发体不挡路，不能当墙
        if (hit.collider.isTrigger) return;

        // 同伴/玩家的胶囊交给避让力处理：当成静止墙面滑动会让两个敌人贴着互相侧移
        if (hit.collider is CharacterController) return;

        _wallNormal = hit.normal;
        _wallTouch = true;
    }

    // 拐点判定：贴近了算到，已经越过去了也算到。
    // 只看平面距离不够用：撞墙滑动、被同伴挤开都会让敌人从拐点旁边擦过去，
    // 这时距离永远收敛不到 ArriveDistance，敌人就会绕着拐点打转
    private bool ReachedCorner(Vector3 corner)
    {
        if (FlatDistance(transform.position, corner) <= ArriveDistance) return true;

        // 最后一个拐点就是终点：越过去不算到达，否则会在半路就"到了"
        if (_cornerIndex >= _path.corners.Length - 1) return false;

        Vector3 segment = _path.corners[_cornerIndex + 1] - corner;
        segment.y = 0f;
        if (segment.sqrMagnitude < 0.0001f) return false;

        Vector3 offset = transform.position - corner;
        offset.y = 0f;

        // 与"本拐点→下一拐点"同向，说明已经站在下一段路上了
        return Vector3.Dot(offset, segment) > 0f;
    }

    // 卡住判定有两道判据，缺一不可：
    //   ① 沿线剩余路程有没有创新低——绕障碍时直线距离会先变大，所以看的是剩余路程而不是直线距离；
    //   ② 净位移——贴着网格边界来回蹭时，每蹭一点都会刷出"新低"，①的计时被反复清零，
    //      实测能磨 10 秒以上；②看的是窗口内净挪了多远，蹭来蹭去净位移接近 0，于是照样判定卡住
    private void TrackProgress(float delta)
    {
        RecordRecentPosition(delta);

        // 跑圈兜底——它和"卡住"是两种病，必须分开治：
        //   卡住 = 动不了（速度接近 0）；跑圈 = 满速在动、净位移却几乎为 0（在两条路线之间横跳）。
        // 后者用速度、剩余路程、进度基准任何一项都抓不到（实测满速 3.36 m/s、目标不变、离网格 0.00），
        // 只有"路程 ÷ 净位移"这个比值能识别。一旦判定跑圈就立刻丢目标重挑，不陪它继续横跳。
        if (RecentTravel(OscillationSamples()) > OscillationTravel &&
            NetDisplacement(OscillationSamples()) < OscillationNet)
        {
            _hasPath = false;
            _waitTimer = 0f;    // 不吃 WaitTime，立刻挑新目标
            ResetProgress();
            return;
        }

        bool creeping = NetDisplacement() < NetProgressDistance;
        float remaining = RemainingTravel();

        if (!creeping && remaining < _bestRemaining - ProgressEpsilon)
        {
            _bestRemaining = remaining;
            _stuckTimer = 0f;
        }
        else
        {
            _stuckTimer += delta;
        }

        if (_stuckTimer < StuckTimeout) return;

        _stuckTimer = 0f;
        EscapeStuck();
    }

    private void RecordRecentPosition(float delta)
    {
        _recentTimer += delta;
        if (_recentTimer < NetSampleInterval) return;

        _recentTimer = 0f;
        _recentPositions[_recentIndex] = transform.position;
        _recentIndex = (_recentIndex + 1) % _recentPositions.Length;
        if (_recentCount < _recentPositions.Length) _recentCount++;
    }

    // 窗口内"最新位置"与"窗口前那个位置"的平面距离。窗口没攒满时返回 +∞（先不算卡）
    private float NetDisplacement()
    {
        return NetDisplacement(ProgressSamples());
    }

    private float NetDisplacement(int window)
    {
        if (_recentCount <= window) return float.PositiveInfinity;

        int length = _recentPositions.Length;
        int newest = (_recentIndex - 1 + length) % length;
        int oldest = (_recentIndex - 1 - window + length * 2) % length;

        Vector3 from = _recentPositions[oldest];
        Vector3 to = _recentPositions[newest];
        from.y = 0f;
        to.y = 0f;
        return Vector3.Distance(from, to);
    }

    // 窗口内的实际路程：把环形缓冲里相邻采样点之间的距离加起来
    private float RecentTravel(int window)
    {
        if (_recentCount <= window) return 0f;

        int length = _recentPositions.Length;
        float sum = 0f;
        for (int i = 0; i < window; i++)
        {
            int newer = (_recentIndex - 1 - i + length * 2) % length;
            int older = (_recentIndex - 2 - i + length * 2) % length;

            Vector3 from = _recentPositions[older];
            Vector3 to = _recentPositions[newer];
            from.y = 0f;
            to.y = 0f;
            sum += Vector3.Distance(from, to);
        }

        return sum;
    }

    private int ProgressSamples()
    {
        return WindowSamples(NetProgressWindow);
    }

    private int OscillationSamples()
    {
        return WindowSamples(OscillationWindow);
    }

    private int WindowSamples(float seconds)
    {
        return Mathf.Clamp(Mathf.RoundToInt(seconds / Mathf.Max(0.01f, NetSampleInterval)),
            1, _recentPositions.Length - 1);
    }

    // 剩余路程 = 到当前拐点的直线距离 + 之后各段拐点间距离之和。
    // 不能用"到终点的直线距离"：绕障碍时路径会先横着走，直线距离反而变大，
    // 沿墙正常滑动也会被误判成卡住
    private float RemainingTravel()
    {
        if (_path.corners.Length == 0 || _cornerIndex >= _path.corners.Length)
        {
            return float.PositiveInfinity;   // 路径已失效：不计进度，让卡住计时继续走
        }

        float total = FlatDistance(transform.position, _path.corners[_cornerIndex]);

        for (int i = _cornerIndex; i < _path.corners.Length - 1; i++)
        {
            total += FlatDistance(_path.corners[i], _path.corners[i + 1]);
        }

        return total;
    }

    // 脱困分两级：先丢掉当前拐点（贴着墙角时路径会切到敌人到不了的拐点，跳过它往往就通了），
    // 拐点全丢完还卡才丢掉整个目标，下一帧重新随机取点
    private void EscapeStuck()
    {
        if (_cornerIndex < _path.corners.Length - 1)
        {
            _cornerIndex++;
            ResetBaselineOnly();
            return;
        }

        _hasPath = false;
    }

    // 只重置"剩余路程基准"，**不动净位移窗口**：
    // 窗口一清就等于每次丢拐点都给自己 2.5 秒免检期，而贴边蠕行恰恰会连续丢拐点，
    // 那样第二道判据永远攒不满（第一版就踩了这个坑）
    private void ResetBaselineOnly()
    {
        _bestRemaining = float.PositiveInfinity;
        _stuckTimer = 0f;
    }

    // 进度基准只在"换了目标"时重置。重算路径不能重置：每 0.8s 清一次的话，
    // 卡住计时永远攒不满 StuckTimeout，又会退回"卡住也不脱困"
    private void ResetProgress()
    {
        _bestRemaining = float.PositiveInfinity;
        _stuckTimer = 0f;
        _recentCount = 0;    // 换了目标/拐点就重新攒净位移窗口，否则会拿旧位置比
        _recentIndex = 0;
        _recentTimer = 0f;
    }

    // 把同伴往两边推：CharacterController 之间会互相阻挡，不避让就会几个人顶在一起谁也走不了
    private Vector3 ComputeAvoidance()
    {
        if (!AvoidNeighbours) return Vector3.zero;

        int count = Physics.OverlapSphereNonAlloc(transform.position, NeighbourRadius, _neighbours, NeighbourLayers);
        if (count == 0) return Vector3.zero;

        Vector3 push = Vector3.zero;
        int considered = 0;

        for (int i = 0; i < count && considered < 8; i++)
        {
            Collider other = _neighbours[i];
            if (other == null || other.transform == transform) continue;

            Vector3 away = transform.position - other.transform.position;
            away.y = 0f;

            float distance = away.magnitude;

            // 完全重合时方向为零，推力失效会让两个敌人永久叠在一起，随机挑个方向解开
            if (distance < 0.0001f)
            {
                away = Random.insideUnitSphere;
                away.y = 0f;
            }
            else
            {
                away = (away / distance) * (1f - Mathf.Clamp01(distance / NeighbourRadius));
            }

            push += away;
            considered++;
        }

        if (considered == 0) return Vector3.zero;

        // 只保留水平分量并限幅，避免多个同伴把推力叠成弹射
        push.y = 0f;
        return Vector3.ClampMagnitude(push, 1f) * AvoidStrength;
    }

    // —— 调试只读接口：给编辑器实时探针（Assets/Editor/AILiveProbe.cs）看内部状态用 ——
    // 只读暴露，游戏逻辑不要依赖这些值，它们随时可能因为内部重构而改名
    public bool HasPath => _hasPath;
    public int CornerIndex => _cornerIndex;
    public int CornerCount => _path != null ? _path.corners.Length : 0;
    public float StuckTimer => _stuckTimer;
    public float BestRemaining => _bestRemaining;
    public bool ReturningToMesh => _returningToMesh;
    public bool HoldingPosition => _holdPosition;
    public Vector3 CurrentGoal => _hasPath && _path != null && _path.corners.Length > 0
        ? _path.corners[_path.corners.Length - 1]
        : transform.position;

    // 原地待命开关：决策层守住战术位时用。开着就不再随机取点，只贴地站着；
    // MoveTo 仍然有效——待命只禁止"自己乱走"，不拒绝显式命令
    public void HoldPosition(bool hold)
    {
        _holdPosition = hold;
        if (!hold) return;

        _hasPath = false;
        _waitTimer = 0f;
    }

    // 供 EnemyBrain 直接指定落脚点（比如战术位置），覆盖随机游荡
    public void MoveTo(Vector3 destination)
    {
        if (!TryGetPathStart(out Vector3 start)) return;

        if (!NavMesh.CalculatePath(start, destination, NavMesh.AllAreas, _path))
        {
            return;
        }

        _cornerIndex = 1;
        _hasPath = _path.corners.Length > 1;
        _waitTimer = 0f;
        _repathTimer = 0f;
        ResetProgress();
    }

    private void PickNewDestination()
    {
        if (!TryGetPathStart(out Vector3 start))
        {
            // 连吸附都失败（脚下离网格太远，比如掉出地图）：等一会儿再试
            _waitTimer = WaitTime;
            return;
        }

        for (int i = 0; i < SampleTries; i++)
        {
            if (!TryPickPoint(out Vector3 goal)) continue;
            if (FlatDistance(transform.position, goal) < MinTravelDistance) continue;

            if (NavMesh.CalculatePath(start, goal, NavMesh.AllAreas, _path) &&
                _path.status == NavMeshPathStatus.PathComplete)
            {
                _cornerIndex = 1;   // 第 0 个拐点是脚下，跳过
                _hasPath = _path.corners.Length > 1;
                if (_hasPath)
                {
                    _repathTimer = 0f;
                    ResetProgress();
                    return;
                }
            }
        }

        // 采样全失败（地图太小或被围死）就原地等一会儿再试，不要空转刷日志
        _waitTimer = WaitTime;
    }

    private bool TryPickPoint(out Vector3 point)
    {
        point = Vector3.zero;

        if (Scope == WanderScope.Local)
        {
            Vector2 random = Random.insideUnitCircle * SampleRadius;
            Vector3 candidate = transform.position + new Vector3(random.x, 0f, random.y);

            // 把随机点吸附到网格上；落在墙里或网格外会返回 false
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, SampleRadius * 0.5f, NavMesh.AllAreas))
            {
                return false;
            }

            point = hit.position;
            return true;
        }

        return TryPickWholeMapPoint(out point);
    }

    private bool TryPickWholeMapPoint(out Vector3 point)
    {
        point = Vector3.zero;

        EnsureTriangulationCached();
        if (_totalArea <= 0f) return false;

        // 必须按面积加权挑三角形，不能直接随机挑顶点：
        // 烘焙时地图边缘与墙脚会细分出大量小三角形，顶点密度远高于开阔地，
        // 随机取顶点会让敌人几乎总是往边界跑
        int triangle = FindTriangleByArea(Random.Range(0f, _totalArea));

        Vector3 a = _triangulation.vertices[_triangulation.indices[triangle * 3]];
        Vector3 b = _triangulation.vertices[_triangulation.indices[triangle * 3 + 1]];
        Vector3 c = _triangulation.vertices[_triangulation.indices[triangle * 3 + 2]];

        // 在三角形内部均匀取点（重心坐标）。落到外面时翻折回来，保证仍在三角形内
        float u = Random.value;
        float v = Random.value;
        if (u + v > 1f)
        {
            u = 1f - u;
            v = 1f - v;
        }

        point = a + (b - a) * u + (c - a) * v;
        return true;
    }

    // 三角化有开销，但地图是静态的，算一次缓存住就够用
    private void EnsureTriangulationCached()
    {
        if (_cumulativeAreas != null) return;

        _triangulation = NavMesh.CalculateTriangulation();

        int count = _triangulation.indices.Length / 3;
        _cumulativeAreas = new float[count];
        _totalArea = 0f;

        for (int i = 0; i < count; i++)
        {
            Vector3 a = _triangulation.vertices[_triangulation.indices[i * 3]];
            Vector3 b = _triangulation.vertices[_triangulation.indices[i * 3 + 1]];
            Vector3 c = _triangulation.vertices[_triangulation.indices[i * 3 + 2]];

            _totalArea += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            _cumulativeAreas[i] = _totalArea;
        }
    }

    // 在累加面积数组里二分查找，命中概率与三角形面积成正比
    private int FindTriangleByArea(float value)
    {
        int low = 0;
        int high = _cumulativeAreas.Length - 1;

        while (low < high)
        {
            int mid = (low + high) / 2;
            if (_cumulativeAreas[mid] < value) low = mid + 1;
            else high = mid;
        }

        return low;
    }

    private void RepathToCurrentTarget()
    {
        if (_cornerIndex >= _path.corners.Length) return;

        Vector3 goal = _path.corners[_path.corners.Length - 1];
        if (_scratchPath == null) _scratchPath = new NavMeshPath();

        if (!TryGetPathStart(out Vector3 start) ||
            !NavMesh.CalculatePath(start, goal, NavMesh.AllAreas, _scratchPath))
        {
            // 重算失败时 CalculatePath 已经把 _scratchPath 清空了，状态必须一起清，
            // 否则下一帧会在空路径上取拐点
            _hasPath = false;
            return;
        }

        // 只认完整路径：不完整说明这个目标目前根本走不到，继续跟着它跑纯属白跑
        if (_scratchPath.status != NavMeshPathStatus.PathComplete)
        {
            _hasPath = false;
            return;
        }

        // 关键迟滞：新路径必须明显更短才允许替换。
        // 没有它会怎样：这处坡道/平台地形上存在两条几乎等长的路线（下坡绕平台 / 上坡跨平台），
        // 敌人恰好在两条路线的"分水岭"附近时，每 0.8 秒重算都会选到另一条 → 立刻掉头 →
        // 走到另一侧的分水岭 → 又换回来。实测 30 秒掉头 45 次、路程 60.7m、净位移 0m、
        // 而且全程满速、目标不变、离网格 0.00 —— 所有"卡住"判据都抓不到（它压根没卡，是在跑圈）
        float newLength = PathLength(_scratchPath.corners);
        if (newLength > RemainingTravel() * PathImprovementRatio)
        {
            return;   // 两条路差不多长：保持原路线，绝不因为一丁点差值就掉头
        }

        // 注意：NavMeshPath 没有 Clear()，CalculatePath 会直接覆盖传入的路径对象
        NavMesh.CalculatePath(start, goal, NavMesh.AllAreas, _path);
        _cornerIndex = 1;
        _hasPath = _path.corners.Length > 1;
    }

    private static float PathLength(Vector3[] corners)
    {
        float sum = 0f;
        for (int i = 1; i < corners.Length; i++)
        {
            Vector3 from = corners[i - 1];
            Vector3 to = corners[i];
            from.y = 0f;
            to.y = 0f;
            sum += Vector3.Distance(from, to);
        }

        return sum;
    }

    private void TurnTowards(Vector3 point, float delta)
    {
        Vector3 flat = point - transform.position;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.0001f) return;

        // 只取水平朝向并锁成 Euler(0, yaw, 0)：直接赋四元数会带俯仰分量，胶囊会歪
        float targetYaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        float yaw = Mathf.MoveTowardsAngle(transform.eulerAngles.y, targetYaw, TurnSpeed * delta);
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    private void ApplyGravity()
    {
        // CharacterController 不带重力，每帧补一段向下速度，否则走坡会飘起来
        _verticalSpeed = _controller.isGrounded
            ? -GroundStickSpeed
            : _verticalSpeed - Gravity * TimeManager.WorldDeltaTime;
    }

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
        if (Scope == WanderScope.Local)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(transform.position, SampleRadius);
        }

        // 当前挡路的面（法线朝外）：排查卡坡角时看这根红线指向哪
        if (_wallTouch)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawLine(transform.position + Vector3.up,
                transform.position + Vector3.up + _wallNormal * 1.5f);
        }

        if (!_hasPath) return;

        Gizmos.color = Color.green;
        for (int i = 1; i < _path.corners.Length; i++)
        {
            Gizmos.DrawLine(_path.corners[i - 1], _path.corners[i]);
            Gizmos.DrawWireSphere(_path.corners[i], 0.2f);
        }
    }
}
