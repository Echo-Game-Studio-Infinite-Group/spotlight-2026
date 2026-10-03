using System;

// 伤害类型（设计 §4.5）：免疫按类型掩码表达——"滑铲对投/对弹免疫但不等于全无敌"就落在这套位标志上
[Flags]
public enum DamageType
{
    None       = 0,
    Melee      = 1 << 0,  // 近战（parry 时缓只认近战来源）
    Projectile = 1 << 1,  // 飞行道具（伤害判定小、受 parry 判定大）
    Grab       = 1 << 2,  // 投技
    Impact     = 1 << 3,  // 撞击（高速撞墙/撞重型敌人的惩罚伤害）
    AOE        = 1 << 4,  // 范围伤害
    All        = Melee | Projectile | Grab | Impact | AOE,
}
