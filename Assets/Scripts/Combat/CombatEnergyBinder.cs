using System.Collections.Generic;
using UnityEngine;

// 战斗经济绑定（dev 地板适配版）：订阅场景里 Damageable 的受击/死亡事件 → 能量返还
// 「敌人即资源」（决策 #8）：命中/击杀两档返还，数值在 EnergyParams
// 依赖方向单向：本类订阅 dev 的 Damageable 事件并调 VectorEnergy.Grant，双方组件都不需要知道本类存在
// 注册制：Start 收集一次 + 公开 Register 供动态刷怪接入（订阅随绑定解除，无泄漏）
public class CombatEnergyBinder : MonoBehaviour
{
    [Tooltip("能量账户（留空自动找场景中唯一账户）")]
    [SerializeField] private VectorEnergy _energy;

    [Tooltip("返还数值来源（命中/击杀两档）")]
    [SerializeField] private EnergyParams _params;

    private readonly List<Damageable> _watched = new List<Damageable>();

    /// <summary>注入引用（装配工具与测试用；不注入则 Start 时自动找能量账户）</summary>
    public void SetSources(VectorEnergy energy, EnergyParams parameters)
    {
        _energy = energy;
        _params = parameters;
    }

    private void Start()
    {
        if (_energy == null) _energy = FindFirstObjectByType<VectorEnergy>();
        if (_energy == null || _params == null) return;

        // 存量敌人一次收齐；后续动态刷的由刷怪方调 Register
        foreach (Damageable damageable in FindObjectsByType<Damageable>(FindObjectsSortMode.None))
        {
            Register(damageable);
        }
    }

    private void OnDestroy()
    {
        for (int i = 0; i < _watched.Count; i++)
        {
            if (_watched[i] == null) continue;
            _watched[i].Damaged -= OnDamaged;
            _watched[i].Died -= OnDied;
        }
    }

    /// <summary>登记一个可受击实体（动态刷怪时调用）；幂等</summary>
    public void Register(Damageable damageable)
    {
        if (damageable == null || _watched.Contains(damageable)) return;
        _watched.Add(damageable);
        damageable.Damaged += OnDamaged;
        damageable.Died += OnDied;
    }

    private void OnDamaged(Damageable target, float applied)
    {
        if (applied <= 0f) return; // 无敌帧挡下的命中不返还（没有实际伤害就没有收益）
        if (!target.IsAlive) return; // 致死一击同时触发 Damaged 与 Died——只返 Kill 档，不叠 Hit
        if (_energy != null && _params != null) _energy.Grant(_params.HitEnergyGain);
    }

    private void OnDied(Damageable target)
    {
        if (_energy != null && _params != null) _energy.Grant(_params.KillEnergyGain);
    }
}
