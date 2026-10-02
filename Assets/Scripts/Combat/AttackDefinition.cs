using UnityEngine;

// 段相位（帧数据三窗口，设计 §4.1：时间结构先锁、动画后填）
public enum AttackPhaseKind { Startup, Active, Recovery, Any }

// 位移意图种类（执行全部走 Motor 命令——战斗不直接动 Transform，框架 4.1）
public enum MotionIntentKind { None, Advance, Hover, Dash }

// 一段攻击的帧数据（设计 §4.1 落地形状）。多段攻击 = Phases 多元素、段显式推进；
// 「冲刺三段」这类同键续接技 = 三个 SO（段数计数归状态机）——拆开才能进同一张取消表
[System.Serializable]
public class AttackPhase
{
    [Header("帧窗口（秒）")]
    public float StartupSec = 0.15f;
    public float ActiveSec = 0.10f;
    public float RecoverySec = 0.20f;

    [Header("伤害判定")]
    public bool HasDamage = true;
    public HitboxProfile DamageProfile = new HitboxProfile();
    [Tooltip("基础伤害；最终伤害 = 基础 × (1 + 速度伤害系数 × 起手速度比)")]
    public float BaseDamage = 10f;
    [Tooltip("速度伤害系数（高速普攻伤害随起手速度快照增加）。0 = 不随速度加成")]
    public float SpeedDamageScale = 0f;
    public DamageType DamageType = DamageType.Melee;
    [Tooltip("击退量级（方向由结算朝目标算；目标抗击退力折算归目标侧）")]
    public float Knockback = 2f;
    [Tooltip("命中 hit-stop 时长（秒）")]
    public float HitStopSec = 0.03f;

    [Header("Parry 判定（与伤害框分开配——子弹类「伤害判定小、受 parry 判定大」）")]
    public bool HasParry = false;
    public HitboxProfile ParryProfile = new HitboxProfile();

    [Header("位移意图")]
    public MotionIntentKind Motion = MotionIntentKind.None;
    [Tooltip("Advance/Dash 的位移速度（米/秒）：Startup+Active 期间每 tick 前移，撞墙经 Motor 扫掠截断")]
    public float MotionSpeed = 8f;

    [Tooltip("扫掠采集：覆盖上一 tick 刀根到当前的路径——高速普攻/冲刺不漏目标（框架 4.4）")]
    public bool Sweep = false;
}

// 一个 SO = 一个招式（设计 §4.1）。策划在此调帧数据；新招式 = 新资产 + 取消表加行，程序零改动
[CreateAssetMenu(fileName = "AttackDefinition", menuName = "超高速行者/Combat/AttackDefinition")]
public class AttackDefinition : ScriptableObject
{
    [Tooltip("显示名（DebugHUD / 取消表可读性）")]
    public string DisplayName = "新招式";

    [Tooltip("能量成本：一次性扣费，不随时间缩放（框架 4.3）")]
    public float EnergyCost = 0f;

    [Tooltip("冷却（秒，按玩家时间戳）")]
    public float CooldownSec = 0f;

    [Tooltip("起手清空水平速度（连斩「重置速度为 0」）")]
    public bool ClearSpeedOnStart = false;

    [Tooltip("激活最低速度（×地速阈值）；高速普攻变体 ≈0.8，常态变体 0")]
    public float MinSpeedRatio = 0f;

    [Tooltip("激活最高速度（×地速阈值）；无穷 = 无上限")]
    public float MaxSpeedRatio = float.PositiveInfinity;

    [Tooltip("高速普攻专用：落地时若无派生/取消则清空动量（速度经济学——逼迫玩家用招式链维持动量）")]
    public bool ClearMomentumOnLandIfUnderived = false;

    [TextArea, Tooltip("设计备注（出处/待定项；首版数值均为占位，内容待哈士奇核）")]
    public string DesignNote;

    [Tooltip("段数组：单段招式长度 1；多段（连斩/推斩）在此显式排布")]
    public AttackPhase[] Phases = { new AttackPhase() };
}
