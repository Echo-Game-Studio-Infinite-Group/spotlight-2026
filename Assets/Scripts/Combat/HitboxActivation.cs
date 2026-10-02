using UnityEngine;

// Hitbox 激活上下文：一次「某招式某段生效窗」的全部判定参数（状态机/敌人 AI 写入）
// 起手速度以快照形式随激活带入——高速伤害「命中时不得重取」的保证在此成立（框架 4.2）
public struct HitboxActivation
{
    public GameObject Attacker;
    public int InstanceId;           // 攻击实例（去重键）
    public int PhaseIndex;           // 段号（去重键）
    public HitboxProfile Profile;
    public bool Sweep;
    public float BaseDamage;
    public float SpeedDamageScale;   // 伤害 = 基础 × (1 + 系数 × 起手速度比)
    public DamageType DamageType;
    public float Knockback;
    public float HitStopSec;
    public float SpeedSnapshot;      // 起手绝对速度（表现层）
    public float SpeedRatioSnapshot; // 起手速度比
    public bool FromParryDerive;
}
