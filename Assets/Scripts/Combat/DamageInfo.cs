using UnityEngine;

// 统一伤害信息（设计 §4.5）：从采集到结算到表现的单一事实载体，纯数据
public struct DamageInfo
{
    public GameObject Attacker;         // 攻击者
    public Hurtbox Target;              // 受击框
    public int AttackInstanceId;        // 攻击实例（每次 Commit 递增——去重键之一）
    public int PhaseIndex;              // 段号（换段后同目标可再命中——去重键之一）
    public DamageType Type;             // 近战/飞行道具/投技/撞击/AOE
    public float Damage;                // 最终伤害（已含速度加成）
    public Vector3 HitPoint;            // 命中点（特效挂点）
    public Vector3 KnockbackDir;        // 击退方向（水平）
    public float Knockback;             // 击退量级
    public float AttackerSpeedSnapshot; // 起手绝对速度快照（表现层用；命中时不得重取，框架 4.2）
    public float SpeedRatioSnapshot;    // 起手速度比（×地速阈值）
    public float HitStopSec;            // 结算触发的 hit-stop 时长
    public bool FromParryDerive;        // parry 派生标记（闪斩触发条件/表现差异）
}
