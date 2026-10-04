using UnityEngine;
using UnityEngine.AI;

// 敌人：自行维护血量与受击反馈，Hitbox 负责碰撞判定。
// 追击与位移全部交给 NavMeshAgent（寻路、绕障、贴地、转向都是它的事），
// Enemy 只做决策（追不追、打不打）与战斗结算，不碰 Transform、不管重力。
// 2026-10-04 主程定：旧的直线追击位移系统废弃，原 CharacterController 那套随之移除。
//
// 刻意不加 [RequireComponent(typeof(NavMeshAgent))]：
// CombatSelfCheck 之类只想要「一个能被打的靶子」的地方不该被强行塞一个 agent，
// 那种场合没有 agent 也能正常工作（只是不追击）。需要追击时在 Inspector 挂上即可。
[DisallowMultipleComponent]
public sealed class Enemy : MonoBehaviour
{
    [Header("生命")]
    [SerializeField, Min(1f)] private float _maxHealth = 100f;
    [SerializeField, Min(0f)] private float _invulnerableTime = 0.45f;
    private float _health;
    private float _invulnerableUntil = float.NegativeInfinity;
    public float MaxHealth => _maxHealth;
    public float Health => _health;
    public bool IsAlive => _health > 0f;
    public float HealthRatio => _maxHealth > 0f ? _health / _maxHealth : 0f;
    public int DamagedCount { get; private set; }
    public float InvulnerableTime => _invulnerableTime;
    public bool IsInvulnerable => TimeManager.UnscaledTime < _invulnerableUntil;
    [Header("战斗属性")]
    [SerializeField, Min(0f)] private float _attackPower = 8f;
    [UnityEngine.Serialization.FormerlySerializedAs("_moveSpeed")]
    [SerializeField, Min(0f)] private float _walkSpeed = 2f;
    [SerializeField, Min(0f)] private float _runSpeed = 5f;
    [SerializeField, Min(0f)] private float _runDistance = 6f;
    [SerializeField, Min(0f)] private float _runHysteresis = 0.5f;
    [SerializeField, Range(0f, 1f)] private float _injuredThreshold = 0.3f;
    public float Speed { get; private set; }
    public float WalkSpeed => _walkSpeed;
    public float RunSpeed => _runSpeed;
    // 比较比例，避免 100 * 0.3f 的舍入把恰好 30 血误判为受伤。
    public bool IsInjured => HealthRatio < _injuredThreshold;
    public bool IsAttacking { get; private set; }
    internal Vector3 PreviousPosition { get; private set; }
    internal Quaternion PreviousRotation { get; private set; }
    private EnemyAnimation _animation;

    [Header("行为")]
    // 寻路与位移全部交给 NavMeshAgent（2026-10-04 主程定：旧的直线追击位移系统废弃）
    // Enemy 只保留决策与攻击判定，不再碰 Transform、不管重力
    // 刻意不用 [RequireComponent]：CombatSelfCheck 之类只想要"能被打的靶子"的地方
    // 不该被强行塞一个 agent，所以缺失时只跳过追击，不报错也不禁用
    [SerializeField] private NavMeshAgent _agent;

    // 默认不追击：需要时在 Inspector 勾上
    // 朝向由 NavMeshAgent.updateRotation 处理，Enemy 不再自己转
    [SerializeField] private bool _chasePlayer;
    [SerializeField, Min(0f)] private float _sightRange = 14f;
    [SerializeField, Min(0f)] private float _attackRange = 2.2f;
    [SerializeField, Min(0f)] private float _attackCooldown = 1.1f;

    private float _nextAttackTime;
    private Hitbox _attackHitbox;
    private bool _runningChase;

    public float AttackPower => _attackPower;
    public float AttackDamage => _attackPower;
    public float MoveSpeed => _walkSpeed;
    public float AttackRange => _attackRange;
    public bool IsChasing => _chasePlayer;

    public void ConfigureStats(float maxHealth, float attackPower, float moveSpeed)
    {
        SetMaxHealth(maxHealth);
        _attackPower = attackPower;
        _walkSpeed = Mathf.Max(0f, moveSpeed);
    }

    public void SetChasePlayer(bool chase)
    {
        _chasePlayer = chase;
        if (!chase) StopMoving();
    }

    private void Awake()
    {
        _health = _maxHealth;
        if (_agent == null) _agent = GetComponent<NavMeshAgent>();
        _animation = GetComponentInChildren<EnemyAnimation>();
        _attackHitbox = GetComponentInChildren<Hitbox>(true);
        if (_attackHitbox != null)
        {
            _attackHitbox.Configure(CampType.Enemy, this);
            _attackHitbox.DisableHitbox();
        }
        PreviousPosition = transform.position;
        PreviousRotation = transform.rotation;
        if (_agent != null)
        {
            // 速度与停止距离都以 Enemy 的字段为准，策划只在一个地方填值
            _agent.speed = _walkSpeed;
            // 预留胶囊半径，避免 Agent 在攻击边界外减速停下，永远无法开打。
            _agent.stoppingDistance = Mathf.Max(0f, _attackRange - _agent.radius);
        }
        if (_damagePopup != null) _damagePopup.gameObject.SetActive(false);
    }

    private void FixedUpdate()
    {
        PreviousPosition = transform.position;
        PreviousRotation = transform.rotation;
        Speed = 0f;
        if (!IsAlive) { StopMoving(); return; }
        float dt = TimeManager.WorldFixedDeltaTime;
        if (dt <= 0f || !_chasePlayer || IsAttacking || (_animation != null && _animation.IsHurting))
        {
            StopMoving();
            return;
        }
        Chase();
    }

    private void OnDisable()
    {
        StopMoving();
        FinishAttack();
    }

    public void FinishAttack()
    {
        IsAttacking = false;
        DisableHitbox();
    }

    public void EnableHitbox()
    {
        if (IsAlive && IsAttacking && _attackHitbox != null) _attackHitbox.EnableHitbox();
    }

    public void DisableHitbox()
    {
        if (_attackHitbox != null) _attackHitbox.DisableHitbox();
    }

    // 供装配工具写入敌人自己的攻击判定盒
    public void ConfigureAttackHitbox(Hitbox hitbox)
    {
        _attackHitbox = hitbox;
    }

    // 实机联调用：把敌人的决策输入一次性打出来，便于定位「为什么不追/不攻击」。
    // 由 MCP 或调试器触发，正常玩法不调用。
    public void DebugDumpState()
    {
        PlayerMotor target = ResolvePlayer();
        float distance = target != null
            ? Vector3.Distance(new Vector3(transform.position.x, 0f, transform.position.z),
                new Vector3(target.transform.position.x, 0f, target.transform.position.z))
            : -1f;
        Debug.Log($"[Enemy] {name} 状态: 存活={IsAlive} 血量={Health}/{MaxHealth} 追击={_chasePlayer} "
            + $"玩家={(target != null ? target.name : "未找到")} 水平距离={distance:0.##} 攻击距离={_attackRange} "
            + $"视野={_sightRange} 下次攻击={_nextAttackTime:0.##} 当前={TimeManager.UnscaledTime:0.##} "
            + $"Agent={(_agent != null ? (_agent.isOnNavMesh ? "在网格上" : "不在网格上") : "未挂")} 世界dt={TimeManager.WorldDeltaTime:0.#####}");
    }

    private void Chase()
    {
        PlayerMotor target = ResolvePlayer();
        if (target == null || !GameManager.Instance.Player.IsAlive) { StopMoving(); return; }

        Vector3 toTarget = target.transform.position - transform.position;
        toTarget.y = 0f;
        float distance = toTarget.magnitude;

        if (distance > _sightRange) { StopMoving(); return; }

        if (distance - _attackRange > 0.001f)
        {
            // 进跑与退跑分开阈值，避免跟随移动玩家时在边界反复切换。
            _runningChase = _runningChase
                ? distance > Mathf.Max(_attackRange, _runDistance - _runHysteresis)
                : distance >= _runDistance;
            float desiredSpeed = _runningChase ? _runSpeed : _walkSpeed;
            if (AgentReady)
            {
                float rate = TimeManager.WorldRate;
                _agent.speed = desiredSpeed * rate;
                _agent.stoppingDistance = Mathf.Max(0f, _attackRange - _agent.radius);
                _agent.isStopped = false;
                _agent.SetDestination(target.transform.position);
                Speed = Vector3.ProjectOnPlane(_agent.velocity, Vector3.up).magnitude / rate;
            }
            return;
        }

        StopMoving();
        TryAttack(target);
    }

    private bool AgentReady => _agent != null && _agent.isActiveAndEnabled && _agent.isOnNavMesh;

    private void StopMoving()
    {
        Speed = 0f;
        if (!AgentReady) return;
        _agent.isStopped = true;
        _agent.velocity = Vector3.zero;
    }

    private void TryAttack(PlayerMotor target)
    {
        if (TimeManager.WorldTime < _nextAttackTime) return;
        // 水平距离已在 Chase 里算过，这里再查一次高度差，避免站在玩家头顶隔着两层平台开打
        if (Mathf.Abs(target.transform.position.y - transform.position.y) > _attackRange) return;

        PlayerData health = GameManager.Instance.Player;
        if (health == null || !health.IsAlive) return;

        // 没有动画或判定盒就不凭距离直接扣血。
        if (_animation == null || !_animation.CanAttack || _attackHitbox == null) return;
        _nextAttackTime = TimeManager.WorldTime + _attackCooldown;
        IsAttacking = true;
        _animation.PlayAttack();
    }

    private PlayerMotor ResolvePlayer()
    {
        return GameManager.Instance.Player.Target;
    }

    public float TakeDamage(float amount, Vector3 hitPoint, Vector3 hitDirection)
    {
        if (!IsAlive || amount <= 0f || IsInvulnerable) return 0f;
        float applied = Mathf.Min(amount, _health);
        _health -= applied;
        DamagedCount++;
        if (_damagePopup != null)
            _damagePopup.Show(applied, _damageTextColor, hitPoint + Vector3.up * 0.5f,
                transform, Camera.main, _numberLifetime, _riseSpeed);
        CameraShaker.Shake(_shakeAmplitude);
        _invulnerableUntil = TimeManager.UnscaledTime + _invulnerableTime;
        if (!IsAlive)
        {
            StopMoving();
            if (AgentReady) _agent.ResetPath();
            FinishAttack();
            GetComponent<GibComponent>()?.TrySlice(hitDirection);
            if (_animation != null) _animation.PlayDeath();
        }
        else if (_animation != null)
        {
            StopMoving();
            _animation.PlayHurt();
        }
        return applied;
    }

    [SerializeField] private DamagePopup _damagePopup;
    [SerializeField] private Color _damageTextColor = new Color(1f, 0.85f, 0.2f);
    [SerializeField] private float _numberLifetime = 1.2f;
    [SerializeField] private float _riseSpeed = 2.2f;
    [SerializeField] private float _shakeAmplitude = 0.6f;
    public DamagePopup DamagePopupTemplate => _damagePopup;
    public void SetDamagePopup(DamagePopup popup) => _damagePopup = popup;

    public void ResetHealth()
    {
        bool wasDead = !IsAlive;
        StopMoving();
        if (AgentReady) _agent.ResetPath();
        FinishAttack();
        _runningChase = false;
        _nextAttackTime = 0f;
        GetComponent<GibComponent>()?.ResetEffect();
        _health = _maxHealth;
        _invulnerableUntil = float.NegativeInfinity;
        DamagedCount = 0;
        if (wasDead && _animation != null) _animation.ResetAfterDeath();
    }
    public void SetMaxHealth(float value) { _maxHealth = Mathf.Max(1f, value); ResetHealth(); }
    public void SetInvulnerableTime(float seconds) => _invulnerableTime = Mathf.Max(0f, seconds);
}
