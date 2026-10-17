using UnityEngine;

// 与 Animator 同节点接收动画事件；Enemy 只负责决策、血量与移动。
[RequireComponent(typeof(Animator))]
public sealed class EnemyAnimation : MonoBehaviour
{
    private Enemy _enemy;
    private Animator _animator;
    private bool _enteredAttack;
    // 直接读状态，不另存等待标记，避免标签漏配或过渡被打断后永久锁住 AI。
    public bool IsHurting => _animator != null && _animator.isActiveAndEnabled
        && _animator.runtimeAnimatorController != null
        && (_animator.GetCurrentAnimatorStateInfo(0).IsName("Hurt")
            || (_animator.IsInTransition(0) && _animator.GetNextAnimatorStateInfo(0).IsName("Hurt")));
    [SerializeField, Min(0f)] private float _speedDampTime = 0.1f;
    private Vector3 _modelOffset;
    private Quaternion _modelRotation;
    public bool CanAttack => _animator != null && _animator.isActiveAndEnabled
        && _animator.runtimeAnimatorController != null && !IsHurting && !_animator.IsInTransition(0)
        && !_animator.GetCurrentAnimatorStateInfo(0).IsTag("Attack");

    /// <summary>受击/死亡定格是否已经生效（已收到片段的 FreezeFrame 事件、动画停在定格帧上）。</summary>
    public bool IsFrozen => _holdActive && _holdFrozen;

    [Header("受击顿帧")]
    // 顿帧按「表现时长」而不是动画时长来配：这是打击感参数，不是动画数据，
    // 所以不写进 EnemyTest.controller 的 Clip，换动画资产也不影响手感。
    // 时长要盖住玩家那一次命中的顿帧尾巴（PlayerCombat 预制体上是 0.09s@0.35x + 0.2s@0.75x），
    // 否则玩家侧的慢动作还在，敌人的冻结已经结束，动画会在尾巴里又走起来。
    // 注意：冻结点是片段自己 K 的 FreezeFrame 事件，所以「冻结时长」必须长到能等到那个事件，
    // 否则窗口先到期，动画根本走不到定格帧就恢复正常速度了。
    [SerializeField, Min(0f), Tooltip("受伤（没死）时的冻结窗口。要 ≥ Hurt 片段上 FreezeFrame 事件的时间（默认 0.2s）。0 = 关掉。")]
    private float _hurtHitStopSeconds = 0.3f;

    [SerializeField, Range(0f, 1f), Tooltip("受伤冻结时的世界时间倍率。1 = 只冻敌人动画，不动世界时间")]
    private float _hurtHitStopRate = 1f;

    [SerializeField, Min(0f), Tooltip("击杀一击的冻结时长。要 ≥ Die 片段上 FreezeFrame 事件的时间（默认约 2.267s = 末段落地重击）。")]
    private float _deathHitStopSeconds = 2.3f;

    [SerializeField, Range(0f, 1f), Tooltip("击杀冻结时的世界时间倍率。1 = 只冻敌人动画，不动世界时间")]
    private float _deathHitStopRate = 1f;

    // 敌人的顿帧来源标识：与玩家侧那套各自计时，互不影响。
    private readonly object _hitStopOwner = new object();

    private bool _holdActive;
    private bool _holdFrozen;
    private float _holdRemaining;

    private void Awake()
    {
        _enemy = GetComponentInParent<Enemy>();
        _animator = GetComponent<Animator>();
        _animator.applyRootMotion = false;
        _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        // UnscaledTime：受击与死亡表现不该被时停/减速拖慢，冻帧由下面 Update 自己判断。
        _animator.updateMode = AnimatorUpdateMode.UnscaledTime;
        if (_enemy != null)
        {
            _modelOffset = _enemy.transform.InverseTransformPoint(transform.position);
            _modelRotation = Quaternion.Inverse(_enemy.transform.rotation) * transform.rotation;
        }
    }

    private void Update()
    {
        if (_enemy == null) return;
        // updateMode 是 UnscaledTime，动画器不会理 Time.timeScale，所以顿帧必须显式把速度压到 0 停住；
        // 不这么做的话世界在慢动作、敌人却照常播完受击/死亡动画，顿帧的「卡住」读感会整个漏掉。
        //
        // 自己挂牌期间必须屏蔽全局 InHitStop：玩家的顿帧是当帧生效的，若这里也认它，
        // 动画会先被玩家顿帧冻一下、再被片段上的 FreezeFrame 事件冻一次——看起来就是「多顿了一次」。
        // 挂牌期间冻在哪一帧只由片段事件决定，别的来源一律不插手。
        _animator.speed = _holdActive ? ResolveHoldSpeed()
            : (TimeManager.InHitStop ? 0f : NormalSpeed());
        _animator.SetFloat("Hp", _enemy.Health);
        _animator.SetFloat("MaxHp", _enemy.MaxHealth);
        _animator.SetFloat("Speed", _enemy.Speed, _speedDampTime, TimeManager.WorldDeltaTime);
        _animator.SetFloat("Injured", _enemy.IsInjured ? 1f : 0f);
        _animator.SetBool("Dead", !_enemy.IsAlive);
    }

    private void LateUpdate()
    {
        // 只插值模型，碰撞体和攻击判定仍跟随固定逻辑帧，不改物理位置。
        if (_enemy != null && transform != _enemy.transform)
        {
            float alpha = Mathf.Clamp01((Time.unscaledTime - Time.fixedUnscaledTime) / Time.fixedUnscaledDeltaTime);
            Quaternion rotation = Quaternion.Slerp(_enemy.PreviousRotation, _enemy.transform.rotation, alpha);
            Vector3 position = Vector3.Lerp(_enemy.PreviousPosition, _enemy.transform.position, alpha);
            transform.SetPositionAndRotation(position + rotation * Vector3.Scale(_modelOffset, _enemy.transform.lossyScale),
                rotation * _modelRotation);
        }
        if (_enemy == null || !_enemy.IsAttacking) return;
        AnimatorStateInfo state = _animator.GetCurrentAnimatorStateInfo(0);
        if (state.IsTag("Attack")) _enteredAttack = true;
        // 状态被打断或末帧事件未送达时也要关闭判定，不能把敌人锁在攻击里。
        if (_enteredAttack && ((!state.IsTag("Attack") && !_animator.IsInTransition(0))
            || (state.IsTag("Attack") && state.normalizedTime >= 1f))) FinishAttack();
    }

    public void PlayAttack()
    {
        _enteredAttack = false;
        _animator.SetTrigger("Attack");
    }
    public void PlayDeath()
    {
        _animator.ResetTrigger("Hurt");
        _animator.ResetTrigger("Attack");
        _animator.SetBool("Dead", true);
        BeginHold(_deathHitStopSeconds, _deathHitStopRate);
    }

    public void PlayHurt()
    {
        if (_animator.runtimeAnimatorController == null || !_animator.isActiveAndEnabled) return;
        FinishAttack();
        _animator.ResetTrigger("Attack");
        _animator.SetTrigger("Hurt");
        BeginHold(_hurtHitStopSeconds, _hurtHitStopRate);
    }

    // 冻住自己的受击/死亡动画；与玩家侧顿帧互不覆盖（各按各的时长恢复）。
    // 只登记 Player 层：顿帧若连 World 一起压，Enemy.FixedUpdate 拿到的 WorldFixedDeltaTime 会趋近 0，
    // AI 的位置积分跟着一起冻结，恢复瞬间会和 LateUpdate 的模型插值位置脱节。
    public void FreezeFor(float seconds, float rate) => BeginHold(seconds, rate);

    // 挂牌：登记顿帧窗口（保证 InHitStop 立刻为真，玩家侧动作系统当帧就吃到顿帧），
    // 动画不在这里停，等片段上的 FreezeFrame 事件到位才停。
    private void BeginHold(float seconds, float rate)
    {
        if (seconds <= 0f) return;
        TimeManager.HitStop(seconds, rate, _hitStopOwner);
        _holdActive = true;
        _holdFrozen = false;
        _holdRemaining = seconds;
    }

    /// <summary>
    /// 定格帧的动画事件入口。在 Hurt / Die 片段上 K 的帧决定冻在哪一帧，
    /// 与 Animator 同节点（动画事件只发给 Animator 所在物体的组件）。
    /// </summary>
    public void FreezeFrame()
    {
        if (!_holdActive || _holdFrozen) return;
        _holdFrozen = true;
    }

    // 定格过程里的动画速度：未挂牌走正常速率；收到 FreezeFrame 事件后压 0；顿帧窗口到期恢复正常。
    private float ResolveHoldSpeed()
    {
        if (_holdRemaining <= 0f) { _holdActive = false; return NormalSpeed(); }
        _holdRemaining -= Time.unscaledDeltaTime;

        // 只有片段的 FreezeFrame 事件能让它停住：事件排在哪一帧就冻在哪一帧。
        return _holdFrozen ? 0f : NormalSpeed();
    }

    private float NormalSpeed() => TimeManager.WorldRate * TimeManager.GameRate;

    public void ResetAfterDeath()
    {
        if (_animator == null || _animator.runtimeAnimatorController == null) return;
        // Die 没有出口；复用敌人时必须恢复默认状态，不能只清掉 Dead 参数。
        _enteredAttack = false;
        _animator.Rebind();
        if (_animator.isActiveAndEnabled) _animator.Update(0f);
    }

    public void EnableHitbox() { if (_enemy != null) _enemy.EnableHitbox(); }
    public void DisableHitbox() { if (_enemy != null) _enemy.DisableHitbox(); }
    public void FinishAttack() { if (_enemy != null) _enemy.FinishAttack(); }
    private void OnDisable()
    {
        // 回收进池时别把定格牌子留着：对象下次复用会在受击姿势上冻一帧。
        _holdActive = false;
        _holdFrozen = false;
        _holdRemaining = 0f;
        FinishAttack();
    }
}
