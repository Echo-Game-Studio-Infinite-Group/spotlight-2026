using UnityEngine;

// 战斗经济绑定（D3–D5 闭环「能量收支接通」）：订阅玩家伤害框的命中事件 → 能量返还
// 「敌人即资源」（决策 #8）：命中/击杀两档返还，数值在 EnergyParams——Doom 式推进战斗经济，
// 逼玩家持续进攻而不是龟缩攒能量
// 依赖方向单向：本类订阅战斗事件并调 VectorEnergy.Grant，能量组件不反向依赖战斗
// 击杀判定不另订阅 Died：结算顺序里 ApplyDamage（内部触发 Died）先于 NotifyHit——
// 命中回调时读目标 IsDead 已是击杀后状态，一个订阅点同时覆盖两档
public class CombatEnergyBinder : MonoBehaviour
{
    [Tooltip("能量账户（留空自动找场景中唯一账户）")]
    [SerializeField] private VectorEnergy _energy;

    [Tooltip("返还数值来源（命中/击杀两档）")]
    [SerializeField] private EnergyParams _params;

    [Tooltip("玩家伤害判定框（命中事件的订阅源）")]
    [SerializeField] private Hitbox _playerDamageHitbox;

    /// <summary>
    /// 注入引用（装配工具与测试用；先注入再激活，Start 里据此订阅）
    /// </summary>
    public void SetSources(VectorEnergy energy, EnergyParams parameters, Hitbox playerDamageHitbox)
    {
        _energy = energy;
        _params = parameters;
        _playerDamageHitbox = playerDamageHitbox;
    }

    // 订阅放 Start 不放 OnEnable：编辑器装配流是「AddComponent（立即 OnEnable）→ 后注入引用」，
    // OnEnable 时引用还是空；Start 在进 Play 后首帧跑，注入已完成
    private void Start()
    {
        if (_energy == null) _energy = FindFirstObjectByType<VectorEnergy>();
        if (_playerDamageHitbox != null) _playerDamageHitbox.HitResolved += OnHitResolved;
    }

    private void OnDestroy()
    {
        if (_playerDamageHitbox != null) _playerDamageHitbox.HitResolved -= OnHitResolved; // 订阅随绑定解除
    }

    private void OnHitResolved(DamageInfo info)
    {
        if (_energy == null || _params == null) return;
        HealthComponent targetHealth = info.Target != null ? info.Target.Health : null;
        bool killed = targetHealth != null && targetHealth.IsDead;
        _energy.Grant(killed ? _params.KillEnergyGain : _params.HitEnergyGain);
    }
}
