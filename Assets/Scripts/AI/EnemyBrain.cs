using UnityEngine;
using UnityEngine.Events;

// 敌人决策层：把 Patrol / Alerted / Engage 串成一条链（上下文包 §5.2 点名的第一个类）
// 架构红线（上下文包 §5.3）：协调层只发号施令，绝不直接改单个敌人的位置/朝向——位移一律委托给 EnemyNavWander
// 约束（AGENTS.md 第 3 条）：不接刚体，位移仍由 CharacterController 手动积分（发生在 EnemyNavWander 内）
// 约束（AGENTS.md 第 5 条）：敌人属世界时间层，本类所有计时走 TimeManager.WorldDeltaTime
// 约束（上下文包 §5.2）：实现 IAlertReceiver；禁用/销毁必须归还开火配额并注销警觉监听
// 与 EnemyPatrol 的关系：本类依赖 EnemyNavWander（巡逻靠 NavMesh 绕障），挂 EnemyPatrol 的敌人不适用
public enum EnemyState
{
    Patrol,     // 游荡：不分散，各自乱走——「成群出现」的压迫感靠这一段，提前摊平就没味道了
    Alerted,    // 警觉：开始分散，并去找一个有视野、离玩家够远的战术位
    Engage      // 交战：守住战术位，拿到开火配额才射击（策划案 8.(2)：同场地同时发射 ≤3）
}

[RequireComponent(typeof(EnemyNavWander))]
public class EnemyBrain : MonoBehaviour, IAlertReceiver
{
    [Header("感知")]
    public float SightRange = 14f;              // 更远一律当作看不见
    public float EyeHeight = 1.5f;              // 视线起点/终点高度，与 TacticalSpotSelector 保持一致
    public LayerMask SightBlockingLayers = ~0;
    public float LoseTargetDelay = 2.5f;        // 连续看不见这么久才算丢失目标，避免对射时反复切状态
    public float AlertMemory = 8f;              // 被同伴叫醒后的警戒时长：还没看见也会先朝那边搜
    public float TargetSearchInterval = 1f;     // 没有刷怪器时轮询玩家的间隔，别每帧全场景找

    [Header("就位")]
    public float ArriveDistance = 2.5f;         // 离战术位多近算就位
    public float RepositionInterval = 2f;       // 就位/搜索的重新评估间隔

    [Header("开火")]
    public float FireInterval = 0.8f;
    public float MaxHoldTime = 2f;              // 配额最长持有时间：到点主动归还，杜绝名额泄漏
    public float AimHeight = 1.2f;              // 瞄点高度（胸口），策划案 8.(2) 瞄的是发射瞬间的位置
    public bool FireWithoutScheduler = false;   // 仅预研场没有配额管理器时打开；正式关卡必须走配额

    [Header("引用（留空则运行时查找）")]
    [SerializeField] private Transform _target;
    [SerializeField] private EnemyNavWander _wander;
    [SerializeField] private EnemySeparation _separation;
    [SerializeField] private TacticalSpotSelector _spotSelector;

    // 开火动作由战斗模块接（Scripts/Combat/）：AI 只喊「该开火了」并给出瞄点，不碰投射物怎么生成。
    // 参数是发射瞬间的瞄准点——投射物飞行期间玩家会跑掉，这是策划案要的「几乎打不中高速玩家」
    public UnityEvent<Vector3> OnFire = new UnityEvent<Vector3>();

    private EnemyState _state = EnemyState.Patrol;
    private Vector3 _tacticalSpot;
    private Vector3 _lastSeenPosition;
    private bool _hasSpot;
    private bool _registered;
    private float _unseenTimer;
    private float _alertTimer;
    private float _repositionTimer;
    private float _fireTimer;
    private float _holdTimer;
    private float _targetSearchTimer;

    public EnemyState State => _state;
    public Transform Target => _target;

    // 供刷怪器 / 关卡脚本统一指定玩家，免得每只敌人各自满场景找
    public void SetTarget(Transform target)
    {
        _target = target;
    }

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        TryRegister();
    }

    private void OnDisable()
    {
        // 禁用也要还名额、注销监听：框架 4.5 明确要求「死亡 / 打断 / 禁用」三条路都归还，否则名额永久泄漏
        ReleaseFireSlot();
        Unregister();
    }

    private void OnDestroy()
    {
        // 销毁路径上 OnDisable 已经跑过一次，这里再兜一次，保证「死亡」这条线也显式归还
        ReleaseFireSlot();
        Unregister();
    }

    private void Update()
    {
        float delta = TimeManager.WorldDeltaTime;

        // 广播器的 Awake 顺序不保证，注册失败就每帧重试，直到挂上为止
        if (!_registered) TryRegister();

        RefreshTarget();
        if (_target == null) return;   // Unity 伪 null：目标被销毁后判空有效，但访问成员会抛异常，先撤

        bool visible = CanSeeTarget();
        if (visible) _lastSeenPosition = _target.position;

        if (_state == EnemyState.Patrol)
        {
            if (!visible) return;

            // 看见玩家：先广播叫醒同伴，再自己进入警觉。广播只通知，怎么反应是各自的事
            if (AlertBroadcaster.Instance != null) AlertBroadcaster.Instance.RaiseAlert(transform.position);
            EnterAlerted();
            return;
        }

        // 警觉之后：看不见的时间与警戒记忆一起倒计时，两个都耗完才回巡逻
        _unseenTimer = visible ? 0f : _unseenTimer + delta;
        _alertTimer = Mathf.Max(0f, _alertTimer - delta);

        if (_unseenTimer >= LoseTargetDelay && _alertTimer <= 0f)
        {
            EnterPatrol();
            return;
        }

        if (_state == EnemyState.Alerted) UpdateAlerted(delta);
        else UpdateEngage(delta);
    }

    // 被同伴叫醒的入口（IAlertReceiver）。sourcePosition 是"玩家最后被看到的地方"，
    // 看不见目标时就往那边搜——原地乱转不像在找人
    public void OnAlerted(Vector3 sourcePosition)
    {
        _lastSeenPosition = sourcePosition;
        _alertTimer = AlertMemory;

        if (_state == EnemyState.Patrol) EnterAlerted();
    }

    // —— 状态迁移 —— //

    private void EnterPatrol()
    {
        _state = EnemyState.Patrol;
        _hasSpot = false;
        _unseenTimer = 0f;
        _alertTimer = 0f;
        ReleaseFireSlot();

        // 巡逻阶段不分散：几台机器人自己先摊平，就没有「成群出现」了（上下文包 §5.1 的约定）
        if (_separation != null) _separation.enabled = false;
        if (_wander != null) _wander.HoldPosition(false);
    }

    private void EnterAlerted()
    {
        _state = EnemyState.Alerted;
        _repositionTimer = RepositionInterval;   // 立刻评估一次位置
        _unseenTimer = 0f;

        if (_separation != null) _separation.enabled = true;
        if (_wander != null) _wander.HoldPosition(false);
    }

    private void EnterEngage()
    {
        _state = EnemyState.Engage;
        _fireTimer = FireInterval;   // 就位后马上打第一发
        _holdTimer = 0f;
        _repositionTimer = 0f;

        // 守住位置：EnemyNavWander 否则会在到达后继续随机取下一个点
        if (_wander != null) _wander.HoldPosition(true);
    }

    // —— 状态行为 —— //

    private void UpdateAlerted(float delta)
    {
        _repositionTimer += delta;
        if (_repositionTimer >= RepositionInterval)
        {
            _repositionTimer = 0f;
            RepositionTowardsTarget();
        }

        if (_hasSpot && FlatDistance(transform.position, _tacticalSpot) <= ArriveDistance) EnterEngage();
    }

    // 选一个「看得见玩家、又离玩家够远」的点过去；选不出来（被节流、射线被挡、目标已丢失）
    // 就退回朝最后发现玩家的位置搜——站着不动比走错地方更糟
    private void RepositionTowardsTarget()
    {
        bool visible = CanSeeTarget();

        if (visible && _spotSelector != null &&
            _spotSelector.TryPickSpot(transform.position, _target.position, out Vector3 spot))
        {
            _tacticalSpot = spot;
        }
        else
        {
            _tacticalSpot = visible ? _target.position : _lastSeenPosition;
        }

        _hasSpot = true;
        if (_wander != null) _wander.MoveTo(_tacticalSpot);
    }

    private void UpdateEngage(float delta)
    {
        // 就位不是钉死：玩家一直在动，隔一段时间重新找位（风筝型敌人靠这个保持距离）
        _repositionTimer += delta;
        if (_repositionTimer >= RepositionInterval)
        {
            EnterAlerted();
            return;
        }

        // 开火前先看名额。没有配额管理器时默认也不开火——宁可不开，也不能违反同屏 ≤3 的硬性要求
        if (!HoldingFireSlot)
        {
            if (!TryAcquireFireSlot()) return;
            _holdTimer = 0f;
        }

        _holdTimer += delta;
        _fireTimer += delta;

        if (_fireTimer >= FireInterval)
        {
            _fireTimer = 0f;
            OnFire.Invoke(AimPoint());
        }

        // 配额不能长期占着，否则别人永远轮不上（框架 4.5：要「轮流开火」的节奏感）
        if (_holdTimer >= MaxHoldTime) ReleaseFireSlot();
    }

    // —— 配额 —— //

    private bool HoldingFireSlot =>
        AttackScheduler.Instance != null && AttackScheduler.Instance.IsHolding(this);

    private bool TryAcquireFireSlot()
    {
        if (AttackScheduler.Instance != null) return AttackScheduler.Instance.TryAcquire(this);
        return FireWithoutScheduler;
    }

    private void ReleaseFireSlot()
    {
        if (AttackScheduler.Instance == null) return;

        AttackScheduler.Instance.Release(this);
        _holdTimer = 0f;
    }

    // —— 感知与工具 —— //

    private Vector3 AimPoint()
    {
        return _target.position + Vector3.up * AimHeight;
    }

    private bool CanSeeTarget()
    {
        Vector3 eye = transform.position + Vector3.up * EyeHeight;
        Vector3 targetEye = _target.position + Vector3.up * EyeHeight;

        if ((targetEye - eye).sqrMagnitude > SightRange * SightRange) return false;

        return !Physics.Linecast(eye, targetEye, SightBlockingLayers);
    }

    // Unity 伪 null：目标被销毁后 _target == null 依然成立，但访问成员会抛异常，
    // 所以这里判空后重新找，而不是一直信任缓存
    private void RefreshTarget()
    {
        if (_target != null) return;

        _targetSearchTimer += TimeManager.WorldDeltaTime;
        if (_targetSearchTimer < TargetSearchInterval) return;
        _targetSearchTimer = 0f;

        // 预研场还没有刷怪器，直接认玩家；正式关卡应改用 SetTarget 显式注入
        PlayerMotor player = FindObjectOfType<PlayerMotor>();
        if (player != null) _target = player.transform;
    }

    private void ResolveReferences()
    {
        if (_wander == null) _wander = GetComponent<EnemyNavWander>();
        if (_separation == null) _separation = GetComponent<EnemySeparation>();
        if (_spotSelector == null) _spotSelector = GetComponent<TacticalSpotSelector>();
    }

    private void TryRegister()
    {
        if (_registered || AlertBroadcaster.Instance == null) return;

        AlertBroadcaster.Instance.Register(this, this);
        _registered = true;
    }

    private void Unregister()
    {
        if (!_registered) return;
        if (AlertBroadcaster.Instance != null) AlertBroadcaster.Instance.Unregister(this);
        _registered = false;
    }

    private static float FlatDistance(Vector3 from, Vector3 to)
    {
        from.y = 0f;
        to.y = 0f;
        return Vector3.Distance(from, to);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = _state == EnemyState.Patrol ? Color.green : Color.red;
        Gizmos.DrawWireSphere(transform.position, SightRange);

        if (!_hasSpot) return;

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(_tacticalSpot, 0.4f);
        Gizmos.DrawLine(transform.position, _tacticalSpot);
    }
}
