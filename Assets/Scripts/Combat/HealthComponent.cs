using System;
using System.Collections.Generic;
using UnityEngine;

// 血量 / 无敌 / 霸体 / 死亡契约（设计 §4.6），玩家与敌人共用（框架分工表）
// 无敌 = 伤害类型掩码 + 到期时间戳：parry 0.8s 全类型、闪避全程（结束时 ClearImmunity）、
// 滑铲对投+对弹——「滑铲对投/弹免疫但不等于全无敌」就落在这套表达上
// 霸体与无敌是两个独立状态：霸体 = 受伤不硬直（事件 interrupted=false）但伤害照算
public class HealthComponent : MonoBehaviour
{
    [Tooltip("时间层：无敌/受伤计时用哪层时间（玩家=Player，敌人=World）")]
    public TimeLayer Layer = TimeLayer.World;

    [Tooltip("最大血量")]
    public float MaxHealth = 100f;

    [Tooltip("霸体：受击不硬直（事件 interrupted=false），伤害照算。冲刺稿「霸体但无无敌」落在这")]
    public bool SuperArmor;

    public float CurrentHealth { get; private set; }
    public bool IsDead { get; private set; }

    public float LayerNow => Layer == TimeLayer.Player ? TimeManager.PlayerTime : TimeManager.WorldTime;

    /// <summary>受伤事件 (信息, 剩余血量, 是否硬直打断)。音效/特效/受击动画订阅；订阅随绑定解除</summary>
    public event Action<DamageInfo, float, bool> Damaged;

    /// <summary>死亡事件：断肢（关阻挡碰撞）、掉血包、攻击配额归还都挂这里（设计 §4.6）</summary>
    public event Action<DamageInfo> Died;

    /// <summary>免疫拦截（滑铲挡弹等反馈用）</summary>
    public event Action<DamageInfo> ImmunityBlocked;

    /// <summary>治疗 (治疗量, 治疗后血量)：击杀回血（决策 #8——敌人也是一种资源）</summary>
    public event Action<float, float> Healed;

    private readonly Dictionary<DamageType, float> _immunityExpiry = new Dictionary<DamageType, float>();

    private void Awake()
    {
        CurrentHealth = MaxHealth;
    }

    private void OnValidate()
    {
        // 编辑器改 MaxHealth 时保持满血基准，避免调参时出现"当前血量 > 上限"的怪状态
        if (!Application.isPlaying) CurrentHealth = MaxHealth;
    }

    /// <summary>授予类型无敌（同类型叠加取更晚到期）。计时按所属层时间戳（框架 4.3：无敌属玩家/世界逻辑层）</summary>
    public void GrantImmunity(DamageType types, float durationSec)
    {
        float expiry = LayerNow + Mathf.Max(0f, durationSec);
        foreach (DamageType flag in Enum.GetValues(typeof(DamageType)))
        {
            if (flag == DamageType.None || flag == DamageType.All) continue;
            if ((types & flag) == 0) continue;
            _immunityExpiry.TryGetValue(flag, out float existing);
            if (expiry > existing) _immunityExpiry[flag] = expiry;
        }
    }

    /// <summary>显式解除类型无敌（闪避动作结束等；过期条目由查询惰性忽略，无需手动清）</summary>
    public void ClearImmunity(DamageType types)
    {
        foreach (DamageType flag in Enum.GetValues(typeof(DamageType)))
        {
            if (flag == DamageType.None || flag == DamageType.All) continue;
            if ((types & flag) != 0) _immunityExpiry.Remove(flag);
        }
    }

    public bool IsImmuneTo(DamageType type, float layerNow)
    {
        if (type == DamageType.None) return false;
        return _immunityExpiry.TryGetValue(type, out float expiry) && layerNow < expiry;
    }

    /// <summary>受伤入口（结算器调用）。无敌/死亡在此过滤；撞击惩罚等外部入口也走这里（类型=Impact，判定责任在 Motor）</summary>
    public void ApplyDamage(in DamageInfo info)
    {
        if (IsDead) return;
        if (IsImmuneTo(info.Type, LayerNow))
        {
            NotifyImmunityBlocked(in info);
            return;
        }
        CurrentHealth -= info.Damage;
        Damaged?.Invoke(info, CurrentHealth, !SuperArmor);
        if (CurrentHealth <= 0f)
        {
            CurrentHealth = 0f;
            IsDead = true;
            Died?.Invoke(info);
        }
    }

    /// <summary>
    /// 免疫拦截通知：结算器在预检分支（未进入 ApplyDamage）也要发反馈——滑铲挡弹类表现不依赖伤害入口
    /// </summary>
    public void NotifyImmunityBlocked(in DamageInfo info)
    {
        ImmunityBlocked?.Invoke(info);
    }

    public void Heal(float amount)
    {
        if (IsDead || amount <= 0f) return;
        CurrentHealth = Mathf.Min(MaxHealth, CurrentHealth + amount);
        Healed?.Invoke(amount, CurrentHealth);
    }

    /// <summary>重开/复活复位（「重开无残留」验收项）</summary>
    public void ResetForRestart()
    {
        CurrentHealth = MaxHealth;
        IsDead = false;
        _immunityExpiry.Clear();
        SuperArmor = false;
    }
}
