using UnityEngine;

// 玩家攻击：消费输入 → 开攻击窗口 → 由**动画事件**开关 Hitbox → 结算伤害与打击感。
//
// 判定方式的演进（重要）：
//   最初用 SphereCast 每帧扫描，问题是要靠 _hitWindowStart 那类时间参数去「猜」判定时机，
//   而且会隔墙打到、会把玩家自己的碰撞体扫进来。
//   现在沿用参考实现 LittleAdventure 的做法：Hitbox 作为子物体挂在手上，默认关闭，
//   由动画事件 EnableHitbox / DisableHitbox 在挥砍帧精确开关 —— 判定时机完全由动画决定，
//   前摇不打人、后摇不打人，表现与判定天然一致。
[RequireComponent(typeof(CharacterController))]
[DisallowMultipleComponent]
public sealed class PlayerCombat : MonoBehaviour, IDamageSource
{
    [Header("攻击")]
    [SerializeField, Min(0f)] private float _attackDamage = 25f;
    // 攻击窗口 = 动画播放时长，由装配工具从 Attack.fbx 量出后写入。
    // 窗口关掉会让 IsAttacking 转假，状态机立刻退出攻击状态，动画就被掐断。
    [SerializeField, Min(0f)] private float _attackDuration = 1.6f;
    [SerializeField, Min(0f)] private float _attackCooldown = 0.35f;

    [Header("打击感")]
    [SerializeField, Min(0f)] private float _hitStopSeconds = 0.2f;
    [SerializeField, Range(0.05f, 1f)] private float _hitTimeScale = 0.75f;
    // 单纯 0.2s @ 0.75x 只比常速省 50ms，人眼几乎察觉不到。
    // 因此命中瞬间先来一段更强更短的冲击，再落回 _hitTimeScale 的尾巴。
    [SerializeField, Min(0f)] private float _impactSeconds = 0.09f;
    [SerializeField, Range(0.05f, 1f)] private float _impactTimeScale = 0.35f;

    [Header("攻击朝向辅助")]
    [SerializeField] private bool _faceNearestTargetOnAttack = true;
    [SerializeField, Min(0f)] private float _facingAssistRadius = 3.5f;
    [SerializeField] private LayerMask _facingAssistMask = ~0;

    [Header("Parry 派生")]
    // 策划案：parry/断肢后派生闪斩，伤害约为普攻 3 倍——闪斩独立动画未备，先以「同动画伤害放大」占位
    [SerializeField, Min(1f)] private float _deriveDamageMultiplier = 3f;

    [Header("引用")]
    [SerializeField] private Hitbox _hitbox;
    [SerializeField] private AttackVFXManager _vfx;
    [SerializeField] private bool _logHits;

    private PlayerInputReader _input;
    private PlayerMotor _motor;
    private float _attackRemaining;
    private float _readyAt = float.NegativeInfinity;
    private float _deriveUntil = float.NegativeInfinity;
    private bool _deriveNext;

    /// <summary>Hitbox 从这里取伤害值，因此攻击力只有一处真值。派生窗内的下一击为派生攻击（伤害放大）。</summary>
    public float AttackDamage => _attackDamage * (_deriveNext ? _deriveDamageMultiplier : 1f);
    public float AttackDuration => _attackDuration;
    public float AttackCooldown => _attackCooldown;
    public float HitStopSeconds => _hitStopSeconds;
    public float HitTimeScale => _hitTimeScale;
    public float ImpactSeconds => _impactSeconds;
    public float ImpactTimeScale => _impactTimeScale;
    public Hitbox Hitbox => _hitbox;

    // 判定射程由 Hitbox 的 Trigger Collider 决定，这里从它反推出来供诊断显示。
    // 用通用 Collider.bounds：判定盒是球还是盒都不影响这段计算。
    public float AttackRange
    {
        get
        {
            if (_hitbox == null) return 0f;
            Collider box = _hitbox.GetComponent<Collider>();
            if (box == null) return 0f;
            return Mathf.Max(0f,
                Vector3.Distance(transform.position, box.bounds.center) + box.bounds.extents.magnitude);
        }
    }

    // 动作与 Animator 使用同一玩家速率；输入缓冲和冷却仍使用未缩放时间。
    public bool IsAttacking => _attackRemaining > 0f;

    public void Configure(float damage, float duration, float hitStopSeconds, float hitTimeScale)
    {
        _attackDamage = Mathf.Max(0f, damage);
        _attackDuration = Mathf.Max(0f, duration);
        _hitStopSeconds = Mathf.Max(0f, hitStopSeconds);
        _hitTimeScale = Mathf.Clamp(hitTimeScale, 0.05f, 1f);
        SyncHitboxDamage();
    }

    public void SetReferences(Hitbox hitbox, AttackVFXManager vfx)
    {
        _hitbox = hitbox;
        _vfx = vfx;
        SyncHitboxDamage();
    }

    // 攻击窗口时长跟随动画片段：片段多长，窗口就多长，动画才不会被掐断。
    public void SetAttackDuration(float seconds) => _attackDuration = Mathf.Max(0.05f, seconds);

    private void Awake()
    {
        EnsureReferences();
        SyncHitboxDamage();
    }

    // 惰性解析：编辑模式装配与运行时初始化都需要解析引用。
    // 判定盒与剑由 SwordBinder 在运行时创建，创建时机可能晚于本组件的 Awake，
    // 所以引用为空时必须能重新找到，不能只在 Awake 里缓存一次。
    private void EnsureReferences()
    {
        if (_motor == null) _motor = GetComponent<PlayerMotor>();
        if (_input == null) _input = GetComponent<PlayerInputReader>();
        if (_hitbox == null) _hitbox = GetComponentInChildren<Hitbox>(true);
        if (_vfx == null) _vfx = GetComponentInChildren<AttackVFXManager>(true);
    }

    private void SyncHitboxDamage()
    {
        // 运行时判定盒可能晚于本组件创建，拿到引用后重新绑定。
        if (_hitbox != null) _hitbox.SetDamageSource(this);
    }

    private void FixedUpdate()
    {
        if (IsAttacking)
        {
            _attackRemaining = Mathf.Max(0f, _attackRemaining - TimeManager.PlayerFixedDeltaTime);
            if (!IsAttacking) DisableHitbox();
        }
        if (_input == null || !_input.GameplayEnabled) return;
        TickAttack(TimeManager.UnscaledTime);
    }

    // 一帧战斗逻辑：消费输入 + 开窗口。判定交给动画事件驱动的 Hitbox，这里不再做物理扫描。
    public void TickAttack(float now)
    {
        // 攻击窗口内不重复响应输入：挥砍播完之前按左键无效，动画才不会被新攻击打断。
        // 输入仍留在 InputBuffer 里，窗口结束的那一帧会被消费，形成自然的连击节奏。
        if (IsAttacking) return;
        if (_input != null && _input.ConsumeAttack(now))
        {
            // 派生窗内的下一击 = 派生攻击（闪斩占位）：伤害经 AttackDamage 放大，Hitbox 每击现读自动生效
            _deriveNext = now < _deriveUntil;
            BeginAttack(now);
        }
    }

    /// <summary>
    /// 打开 parry 派生窗口（PlayerParry 在格挡成功时调用）：窗内下一次攻击为派生攻击
    /// </summary>
    public void OpenDeriveWindow(float seconds) => _deriveUntil = TimeManager.UnscaledTime + Mathf.Max(0f, seconds);

    /// <summary>派生窗口是否开启（调试 HUD / 测试读数）</summary>
    public bool IsDeriveWindowOpen => TimeManager.UnscaledTime < _deriveUntil;

    public bool BeginAttack(float now)
    {
        if (!isActiveAndEnabled || IsAttacking || now < _readyAt) return false;
        EnsureReferences();
        // 开窗前重新绑定伤害来源，避免初始化顺序问题导致 Hitbox 拿不到攻击力
        SyncHitboxDamage();
        _readyAt = now + _attackCooldown;
        _attackRemaining = _attackDuration;
        if (_faceNearestTargetOnAttack) FaceNearestTarget();
        return true;
    }

    // ===== 动画事件入口（与参考实现同名，动画事件可直接对接）=====

    /// <summary>挥砍起手：由 Attack 动画的动画事件调用。</summary>
    public void EnableHitbox()
    {
        if (!isActiveAndEnabled || !IsAttacking) return;
        EnsureReferences();
        if (_hitbox != null) _hitbox.EnableHitbox();
        else Debug.LogError("[PlayerCombat] 没有判定盒，挥砍不会造成伤害");
    }

    /// <summary>收招：由 Attack 动画的动画事件调用。</summary>
    public void DisableHitbox()
    {
        EnsureReferences();
        if (_hitbox != null) _hitbox.DisableHitbox();
    }

    /// <summary>攻击特效：第 cnt 段。由动画事件调用，与参考实现 UpdateAttack(int) 同名。</summary>
    public void UpdateAttack(int cnt = 1)
    {
        EnsureReferences();
        if (_vfx != null) _vfx.UpdateAttack(cnt);
        else Debug.LogWarning($"[PlayerCombat] 没有配置攻击特效管理器，第 {cnt} 段特效未播放");
    }

    // 复位攻击状态：清掉冷却与窗口。
    // 连续两次自检/测试之间必须调用，否则上一轮的冷却会让 BeginAttack 失败。
    public void ResetAttackState()
    {
        _readyAt = float.NegativeInfinity;
        _attackRemaining = 0f;
        DisableHitbox();
    }

    private void OnDisable() => ResetAttackState();

    // 攻击朝向辅助：把玩家水平转向辅助半径内最近的受击目标。
    // 没有它时挥砍方向永远是 transform.forward，而敌人会绕到玩家侧后方，出现「贴身空刀」。
    private void FaceNearestTarget()
    {
        Collider[] nearby = Physics.OverlapSphere(transform.position, _facingAssistRadius,
            _facingAssistMask, QueryTriggerInteraction.Collide);
        if (nearby.Length == 0) return;

        Damageable nearest = null;
        float nearestDistance = float.MaxValue;
        for (int i = 0; i < nearby.Length; i++)
        {
            Damageable candidate = nearby[i].GetComponentInParent<Damageable>();
            if (candidate == null || !candidate.IsAlive || candidate.gameObject == gameObject) continue;
            if (Mathf.Abs(candidate.transform.position.y - transform.position.y) > 2f) continue;
            float distance = MovementMath.Horizontal(candidate.transform.position - transform.position).sqrMagnitude;
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearest = candidate;
        }

        if (nearest == null) return;
        Vector3 toTarget = MovementMath.Horizontal(nearest.transform.position - transform.position);
        if (toTarget.sqrMagnitude < 0.0001f) return;
        // 直接写 rotation 即可：Hitbox 是碰撞体，跟手骨走，不依赖「立刻读 transform.forward」
        transform.rotation = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
    }

    // ===== 打击感 =====
    // 由 Hitbox 在成功造成伤害后回调（伤害被无敌帧挡下时不会走到这里）
    public void OnLandedHit(Damageable target, Vector3 point, Vector3 direction, float applied)
    {
        if (applied <= 0f) return;
        if (_logHits)
        {
            Debug.Log($"[PlayerCombat] 命中 {target.name} 伤害 {applied:0.#}，剩余 {target.Health:0.#}/{target.MaxHealth:0.#}");
        }

        // 短冲击与尾部减速分别计时，不能连续请求 SlowMotion，否则强减速会被延长到尾部结束。
        // 两层倍率相乘，因此换算冲击层倍率，使最终倍率仍为配置的冲击速度。
        if (_impactSeconds > 0f)
        {
            float tailScale = _hitStopSeconds > 0f ? _hitTimeScale : 1f;
            TimeManager.HitStop(_impactSeconds, Mathf.Clamp01(_impactTimeScale / tailScale));
        }
        TimeManager.SlowMotion(_hitStopSeconds, _hitTimeScale);
    }
}
