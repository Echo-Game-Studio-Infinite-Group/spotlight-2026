using UnityEngine;

// 动画事件接收器：挂在 Animator 所在的节点上，把攻击动画的动画事件转发给父节点的 PlayerCombat。
//
// 为什么需要这一层：Unity 的动画事件是按「动画器所在 GameObject」派发的。
// 本工程的 Animator 在子节点 PlayerDummy 上，而 PlayerCombat 挂在根节点 Player 上，
// 直接把 EnableHitbox 定义在 PlayerCombat 里，事件会报
// "AnimationEvent 'EnableHitbox' has no receiver!"，判定与特效在真实游玩时全部失效。
// （编辑器里手动调用能过，所以只看编辑器自检发现不了这个问题。）
[DisallowMultipleComponent]
public sealed class AttackAnimationEventReceiver : MonoBehaviour
{
    [SerializeField] private PlayerCombat _combat;
    [SerializeField] private Enemy _enemy;
    [SerializeField] private bool _logEvents;

    public void Configure(PlayerCombat combat, Enemy enemy)
    {
        _combat = combat;
        _enemy = enemy;
    }

    private void Awake() => Resolve();

    private void Resolve()
    {
        if (_combat == null) _combat = GetComponentInParent<PlayerCombat>();
        if (_enemy == null) _enemy = GetComponentInParent<Enemy>();
    }

    // ===== 动画事件入口（与参考实现 LittleAdventure 同名，动画事件直接对接）=====

    public void EnableHitbox()
    {
        Resolve();
        if (_logEvents) Debug.Log("[AttackEvent] EnableHitbox");
        if (_combat != null) _combat.EnableHitbox();
        else if (_enemy != null) _enemy.EnableHitbox();
    }

    public void DisableHitbox()
    {
        Resolve();
        if (_logEvents) Debug.Log("[AttackEvent] DisableHitbox");
        if (_combat != null) _combat.DisableHitbox();
        else if (_enemy != null) _enemy.DisableHitbox();
    }

    /// <summary>第 cnt 段攻击的特效，参数由动画事件里的 int 字段传入。</summary>
    public void UpdateAttack(int cnt = 1)
    {
        Resolve();
        if (_logEvents) Debug.Log($"[AttackEvent] UpdateAttack({cnt})");
        if (_combat != null) _combat.UpdateAttack(cnt);
        else if (_enemy != null) _enemy.UpdateAttack(cnt);
    }
}
