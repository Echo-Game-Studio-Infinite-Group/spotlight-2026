using GameJam.Actions;
using UnityEngine;

// 最终还是决定将玩家从GameManager拆为单独单列
[DefaultExecutionOrder(-90)]
[DisallowMultipleComponent]
public sealed class Player : MonoBehaviour
{
    private static Player _current;

    /// <summary>当前场景的玩家；场景里没有玩家时返回 null。</summary>
    public static Player Current
    {
        get
        {
            if (_current == null) _current = FindObjectOfType<Player>();
            return _current;
        }
    }

    // 引用不进序列化：这两个组件必须在玩家根节点上，GetComponent 就能查到。
    // 摆进 Inspector 只会给"拖错对象 / 漏拖"留机会，而它们本来也没有第二个合法选择。
    private PlayerMotor _motor;
    private HealthComponent _health;

    private PlayerActionRunner _runner;
    private PlayerAnimation _animation;
    // 受击特效归 PlayerVFXManager 管（特效对象挂在 Player/Vfx 下），这里只负责喊一声。
    private PlayerVFXManager _vfx;
    [SerializeField, Min(0f)] private float _bigHurtDamageThreshold = 30f;

    public PlayerMotor Motor { get { EnsureReferences(); return _motor; } }
    public HealthComponent Health { get { EnsureReferences(); return _health; } }

    private void Awake()
    {
        _current = this;
        EnsureReferences();
        if (_motor == null) Debug.LogError("[Player] 缺少 PlayerMotor，外部读不到玩家位置", this);
        if (_health == null) Debug.LogError("[Player] 缺少 HealthComponent，玩家不会掉血", this);
    }

    private void OnEnable()
    {
        EnsureReferences();
        // 血量与动作系统的耦合点放在实体自己的组装层，不让 HealthComponent 反向认识动作系统。
        if (_health != null) { _health.Damaged += OnDamaged; _health.Died += OnDied; _health.Revived += OnRevived; }
    }

    private void OnDisable()
    {
        if (_health != null) { _health.Damaged -= OnDamaged; _health.Died -= OnDied; _health.Revived -= OnRevived; }
    }

    private void OnDestroy()
    {
        if (_current == this) _current = null;
    }

    /// <summary>开局 / 重开：满血并清掉动作序列的进行中状态。由流程层调用。</summary>
    public void ResetForNewRun()
    {
        EnsureReferences();
        _animation?.CancelHurt();
        if (_health != null) _health.Reset();
        // 满血时 Reset 不会发 Revived，但动作系统仍需要无条件复位一次。
        if (_runner != null) { _runner.SetAlive(true); _runner.ResetActions(); }
    }

    private void OnDamaged(HealthComponent source, float applied, Vector3 point, Vector3 direction)
    {
        if (applied <= 0f) return;
        _animation?.PlayHurt(applied > _bigHurtDamageThreshold);
        // 受击特效：作者放的粒子（如果有）+ 程序化受击火花，都在 PlayerVFXManager 里
        _vfx?.PlayHurt(point, direction, applied);
    }

    private void OnDied(HealthComponent source)
    {
        _animation?.CancelHurt();
        if (_runner != null) _runner.SetAlive(false);
    }

    private void OnRevived(HealthComponent source)
    {
        _animation?.CancelHurt();
        if (_runner == null) return;
        _runner.SetAlive(true);
        _runner.ResetActions();
    }

    // 惰性解析：引用为空时必须能重新找到，不能只在 Awake 里缓存一次（同 PlayerCombat.EnsureReferences）。
    private void EnsureReferences()
    {
        if (_motor == null) _motor = GetComponent<PlayerMotor>();
        if (_health == null) _health = GetComponent<HealthComponent>();
        if (_runner == null) _runner = GetComponent<PlayerActionRunner>();
        if (_animation == null) _animation = GetComponentInChildren<PlayerAnimation>();
        if (_vfx == null) _vfx = GetComponentInChildren<PlayerVFXManager>();
    }
}
