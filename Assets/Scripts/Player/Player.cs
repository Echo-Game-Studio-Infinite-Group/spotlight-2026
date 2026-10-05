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
        if (_health != null) { _health.Died += OnDied; _health.Revived += OnRevived; }
    }

    private void OnDisable()
    {
        if (_health != null) { _health.Died -= OnDied; _health.Revived -= OnRevived; }
    }

    private void OnDestroy()
    {
        if (_current == this) _current = null;
    }

    /// <summary>开局 / 重开：满血并清掉动作序列的进行中状态。由流程层调用。</summary>
    public void ResetForNewRun()
    {
        EnsureReferences();
        if (_health != null) _health.Reset();
        // 满血时 Reset 不会发 Revived，但动作系统仍需要无条件复位一次。
        if (_runner != null) { _runner.SetAlive(true); _runner.ResetActions(); }
    }

    private void OnDied(HealthComponent source)
    {
        if (_runner != null) _runner.SetAlive(false);
    }

    private void OnRevived(HealthComponent source)
    {
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
    }
}
