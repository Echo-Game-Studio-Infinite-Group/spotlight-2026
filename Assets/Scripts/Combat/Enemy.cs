using UnityEngine;

// 敌人：血量/受击反馈继承自 Damageable，本类只负责「战斗属性 + 追击 + 攻击」这套行为。
// 追击用 Vector3.MoveTowards 的直线靠近，不接 NavMesh：本阶段是灰盒预研，
// 场景里没有烘焙导航网格，接 NavMeshAgent 只会得到一群原地不动的敌人。
//
// 刻意不加 [RequireComponent(typeof(CharacterController))]：
// 那是追击模式专用的位移载体，而它的胶囊以包围盒为基准体积可观，会改变敌人外观。
// 加了 RequireComponent 的话，只要挂上 Enemy 就会被强行塞一个 CharacterController，
// 「只想要一个能被打的靶子」就做不到了。需要追击时由装配工具显式添加。
[DisallowMultipleComponent]
public sealed class Enemy : Damageable, IDamageSource
{
    [Header("战斗属性")]
    [SerializeField, Min(0f)] private float _attackPower = 8f;
    [SerializeField, Min(0f)] private float _moveSpeed = 4f;
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
    private Player _player;
    private Vector3 _knockback;
    private float _nextAttackTime;
    private float _verticalSpeed;
    [SerializeField] private Hitbox _attackHitbox;
    [SerializeField] private AttackVFXManager _attackVfx;
    private bool _chaseConfig;                 // Awake 记录 Inspector 原值：OnDied 会把 _chasePlayer 置 false，复位按原配置恢复
    private Vector3 _spawnPosition;            // 出生点（重开复位回这里）
    private Quaternion _spawnRotation;

    public float AttackPower => _attackPower;
    /// <summary>IDamageSource 实现：Hitbox 从这里取伤害值。语义与 AttackPower 相同，名字对齐接口。</summary>
    public float AttackDamage => _attackPower;
    public float MoveSpeed => _moveSpeed;
    public float AttackRange => _attackRange;
    public bool IsChasing => _chasePlayer;

    public void ConfigureStats(float maxHealth, float attackPower, float moveSpeed)
    {
        SetMaxHealth(maxHealth);
        _attackPower = attackPower;
        _moveSpeed = moveSpeed;
    }

    public void SetChasePlayer(bool chase)
    {
        _chasePlayer = chase;
    }

    protected override void Awake()
    {
        base.Awake();
        _controller = GetComponent<CharacterController>();
        _chaseConfig = _chasePlayer;
        _spawnPosition = transform.position;
        _spawnRotation = transform.rotation;
        if (_attackHitbox == null) _attackHitbox = GetComponentInChildren<Hitbox>(true);
        if (_attackVfx == null) _attackVfx = GetComponentInChildren<AttackVFXManager>(true);
        if (_attackHitbox != null) _attackHitbox.Configure(CampType.Enemy, this, 0f);
    }

    private void Update()
    {
        if (!IsAlive)
        {
            return;
        }

        float dt = TimeManager.WorldDeltaTime;
        if (dt <= 0f) return;

        ApplyPhysics(dt);
        if (_chasePlayer) Chase(dt);
    }

    // ===== 动画事件入口 =====
    // 敌人的攻击判定同样走「动画事件开关 Hitbox」这套（与玩家的 Hitbox 同一个组件）。
    // 敌人当前用的动画控制器里还没有这些事件，缺了它就只靠 TryAttack 的代码结算 ——
    // 当前追击模式由 TryAttack 结算；动画入口供独立动画攻击使用。
    public void EnableHitbox()
    {
        if (_attackHitbox != null) _attackHitbox.EnableHitbox();
    }

    public void DisableHitbox()
    {
        if (_attackHitbox != null) _attackHitbox.DisableHitbox();
    }

    // 攻击特效：与玩家 AttackVFXManager 同名事件，动画事件可直接同时挂
    public void UpdateAttack(int cnt = 1)
    {
        if (_attackVfx != null) _attackVfx.UpdateAttack(cnt);
    }

    // 供装配工具写入敌人自己的受击盒与特效
    public void ConfigureAttackHitbox(Hitbox hitbox, AttackVFXManager vfx)
    {
        _attackHitbox = hitbox;
        _attackVfx = vfx;
    }

    // 实机联调用：把敌人的决策输入一次性打出来，便于定位「为什么不追/不攻击」。
    // 由 MCP 或调试器触发，正常玩法不调用。
    public void DebugDumpState()
    {
        Player target = ResolvePlayer();
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
        Player target = ResolvePlayer();
        if (target == null) return;

        Vector3 toTarget = target.transform.position - transform.position;
        toTarget.y = 0f;
        float distance = toTarget.magnitude;
        if (distance > _sightRange) return;

        FaceTowards(toTarget, dt);

        if (distance > _attackRange)
        {
            Vector3 step = toTarget.normalized * (_moveSpeed * dt);
            MoveStep(step);
            return;
        }

        TryAttack(target, distance);
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

    // 测试环境可能没有 CharacterController（[RequireComponent] 会在运行时补一个，
    // 但 Awake 之前拿不到），显式留一个注入点
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

    private void TryAttack(Player target, float distance)
    {
        if (TimeManager.UnscaledTime < _nextAttackTime) return;
        // 水平距离已在 Chase 里算过，这里再查一次高度差，避免站在玩家头顶隔着两层平台开打
        if (Mathf.Abs(target.transform.position.y - transform.position.y) > _attackRange) return;

        PlayerData health = GameManager.Instance.Player;
        if (health == null || !health.IsAlive) return;

        _nextAttackTime = TimeManager.UnscaledTime + _attackCooldown;
        float applied = health.TakeDamage(_attackPower);
        // 被打中也要有反馈：与玩家命中敌人共用同一套震屏通道
        if (applied > 0f) CameraShaker.Shake(0.35f);
    }

    private Player ResolvePlayer()
    {
        if (_player != null) return _player;
        // 不经过 GameManager.Instance：那个 getter 在找不到实例时会 new 一个空壳并覆盖真实实例，
        // 玩家的注册随之丢失（实测表现为「敌人永远找不到玩家、站着不动」）。
        // 这里只在缓存的注册引用有效时用它，否则退化为直接查场景。
        GameManager manager = FindObjectOfType<GameManager>();
        if (manager != null && manager.Player.Target != null) _player = manager.Player.Target;
        if (_player == null) _player = FindObjectOfType<Player>();
        return _player;
    }

    protected override void OnDamaged(float applied)
    {
        // 受击硬直由击退体现，不改速度：速度是属性，不该被单次命中污染
    }

    protected override void OnDied()
    {
        DisableHitbox();
        Debug.Log($"[Enemy] {name} 已被击破");
        // 先停掉行为再淡出，避免死亡后还在追着玩家打
        _chasePlayer = false;
        if (_controller != null) _controller.enabled = false;
    }

    /// <summary>
    /// 重开复位（死亡重试灰盒）：回满血、按 Inspector 原值恢复追击、恢复阻挡、清击退、回出生点——OnDied 的逆操作
    /// </summary>
    public void ResetForRestart()
    {
        SetMaxHealth(MaxHealth); // 基类语义：设上限同时回满血
        _chasePlayer = _chaseConfig;
        _knockback = Vector3.zero;
        _verticalSpeed = 0f;
        if (_controller != null) _controller.enabled = true;
        transform.SetPositionAndRotation(_spawnPosition, _spawnRotation);
    }

    public void ApplyKnockback(Vector3 direction, float strength)
    {
        Vector3 flat = new Vector3(direction.x, 0f, direction.z).normalized;
        _knockback = flat * (_knockbackSpeed * Mathf.Max(0f, strength));
    }
}
