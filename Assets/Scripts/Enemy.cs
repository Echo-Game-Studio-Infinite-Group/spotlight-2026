using UnityEngine;

// 敌人：自行维护血量与受击反馈，Hitbox 负责碰撞判定。
// 追击用直线靠近，不接 NavMesh：本阶段是灰盒预研，
// 场景里没有烘焙导航网格，接 NavMeshAgent 只会得到一群原地不动的敌人。
//
// 刻意不加 [RequireComponent(typeof(CharacterController))]：
// 那是追击模式专用的位移载体，而它的胶囊以包围盒为基准体积可观，会改变敌人外观。
// 加了 RequireComponent 的话，只要挂上 Enemy 就会被强行塞一个 CharacterController，
// 「只想要一个能被打的靶子」就做不到了。需要追击时由装配工具显式添加。
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
    [SerializeField, Min(0f)] private float _gravity = 25f;

    [Header("行为")]
    // 默认不追击：追击要靠 CharacterController 位移，而它的胶囊以包围盒为基准体积可观，
    // 不该在「只需要一个能被打的靶子」时强加给敌人。需要时在 Inspector 勾上。
    [SerializeField] private bool _chasePlayer;
    [SerializeField, Min(0f)] private float _sightRange = 14f;
    [SerializeField, Min(0f)] private float _attackRange = 2.2f;
    [SerializeField, Min(0f)] private float _attackCooldown = 1.1f;
    [SerializeField, Min(0f)] private float _facingResponse = 12f;

    [Header("受击反馈")]
    [SerializeField, Min(0f)] private float _knockbackSpeed = 6f;
    [SerializeField, Min(0f)] private float _knockbackDecay = 10f;

    private CharacterController _controller;
    private Vector3 _knockback;
    private float _nextAttackTime;
    private float _verticalSpeed;
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
    }

    private void Awake()
    {
        _health = _maxHealth;
        PreviousPosition = transform.position;
        PreviousRotation = transform.rotation;
        _controller = GetComponent<CharacterController>();
        _animation = GetComponentInChildren<EnemyAnimation>(true);
        _attackHitbox = GetComponentInChildren<Hitbox>(true);
        if (_attackHitbox != null) _attackHitbox.Configure(CampType.Enemy, this);
        if (_damagePopup != null) _damagePopup.gameObject.SetActive(false);
    }

    private void FixedUpdate()
    {
        PreviousPosition = transform.position;
        PreviousRotation = transform.rotation;
        Speed = 0f;
        if (!IsAlive) return;
        float dt = TimeManager.WorldFixedDeltaTime;
        if (dt <= 0f) return;
        ApplyPhysics(dt);
        if (_chasePlayer && !IsAttacking && (_animation == null || !_animation.IsHurting)) Chase(dt);
    }

    private void OnDisable() => FinishAttack();

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
            + $"控制器={(_controller != null && _controller.enabled)} 世界dt={TimeManager.WorldDeltaTime:0.#####}");
    }

    private void ApplyPhysics(float dt)
    {
        if (_controller == null || !_controller.enabled) return;

        _knockback = Vector3.MoveTowards(_knockback, Vector3.zero, _knockbackDecay * dt);

        if (_controller.isGrounded && _verticalSpeed < 0f) _verticalSpeed = -2f;
        _verticalSpeed -= _gravity * dt;

        Vector3 motion = _knockback + Vector3.up * _verticalSpeed;
        _controller.Move(motion * dt);
    }

    private void Chase(float dt)
    {
        PlayerMotor target = ResolvePlayer();
        if (target == null || !GameManager.Instance.Player.IsAlive) return;

        Vector3 toTarget = target.transform.position - transform.position;
        toTarget.y = 0f;
        float distance = toTarget.magnitude;
        if (distance > _sightRange) return;

        FaceTowards(toTarget, dt);

        if (distance - _attackRange > 0.001f)
        {
            // 进跑与退跑分开阈值，避免跟随移动玩家时在边界反复切换。
            _runningChase = _runningChase
                ? distance > Mathf.Max(_attackRange, _runDistance - _runHysteresis)
                : distance >= _runDistance;
            float desiredSpeed = _runningChase ? _runSpeed : _walkSpeed;
            Vector3 before = transform.position;
            MoveStep(toTarget.normalized * Mathf.Min(desiredSpeed * dt, distance - _attackRange));
            Vector3 displacement = transform.position - before;
            displacement.y = 0f;
            Speed = displacement.magnitude / dt;
            if (Mathf.Abs(Speed - desiredSpeed) < 0.001f) Speed = desiredSpeed;
            return;
        }

        TryAttack(target);
    }

    // 位移走 CharacterController.Move，与玩家一致：物理引擎只做碰撞，不接管运动
    private void MoveStep(Vector3 step)
    {
        if (_controller != null && _controller.enabled)
        {
            _controller.Move(step);
            return;
        }

        transform.position += step;
    }

    // 允许装配工具和测试显式注入位移载体。
    public void SetController(CharacterController controller)
    {
        _controller = controller;
    }

    private void FaceTowards(Vector3 toTarget, float dt)
    {
        if (toTarget.sqrMagnitude < 0.0001f) return;
        float targetYaw = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
        float yaw = Mathf.LerpAngle(transform.eulerAngles.y, targetYaw, Mathf.Clamp01(_facingResponse * dt));
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
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
            Speed = 0f;
            FinishAttack();
            GetComponent<GibComponent>()?.TrySlice(hitDirection);
            if (_animation != null) _animation.PlayDeath();
            if (_controller != null) _controller.enabled = false;
        }
        else if (_animation != null) _animation.PlayHurt();
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
        GetComponent<GibComponent>()?.ResetEffect();
        _health = _maxHealth;
        _invulnerableUntil = float.NegativeInfinity;
        DamagedCount = 0;
    }
    public void SetMaxHealth(float value) { _maxHealth = Mathf.Max(1f, value); ResetHealth(); }
    public void SetInvulnerableTime(float seconds) => _invulnerableTime = Mathf.Max(0f, seconds);

    public void ApplyKnockback(Vector3 direction, float strength)
    {
        Vector3 flat = new Vector3(direction.x, 0f, direction.z).normalized;
        _knockback = flat * (_knockbackSpeed * Mathf.Max(0f, strength));
    }
}
