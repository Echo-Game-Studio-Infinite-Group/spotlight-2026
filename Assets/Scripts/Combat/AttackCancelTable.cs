using System;
using UnityEngine;

// 一条取消规则四要素（设计 §4.2）：来源（招式+段相位+相对时间窗）、目标、所需输入意图、附加条件
// 「没有开窗口的位置就没有取消」——连斩收尾不可取消 = 收尾段恢复窗不开窗口，无需禁止规则
[Serializable]
public class CancelRule
{
    [Tooltip("来源招式；空 = 通配（任意招式——饿狼式泛取消的归口。默认开放范围待 D1 拍板）")]
    public AttackDefinition SourceAttack;

    [Tooltip("来源段相位（Startup/Active/Recovery/Any）")]
    public AttackPhaseKind SourcePhase = AttackPhaseKind.Recovery;

    [Tooltip("来源段号（多段招式的第几段，从 0 起）；-1 = 任意段。「连斩前段可取消、收尾段无窗口」靠它表达")]
    public int SourceSegment = -1;

    [Tooltip("窗口起止（秒，相对该段相位起点）")]
    public float WindowStart = 0f;
    public float WindowEnd = 999f;

    [Tooltip("取消至目标招式")]
    public AttackDefinition TargetAttack;

    [Tooltip("所需输入意图（仲裁结果）")]
    public InputIntent RequiredIntent = InputIntent.Attack;

    [Tooltip("附加条件：目标激活所需最低速度（×地速阈值）")]
    public float MinSpeedRatio = 0f;
}

// 唯一取消规则配置（设计 §4.2「一张取消表说所有取消」）——涌现玩法的归口载体：
// 特例取消与泛取消统一为窗口匹配，新组合玩法只改本表不改代码
[CreateAssetMenu(fileName = "AttackCancelTable", menuName = "超高速行者/Combat/AttackCancelTable")]
public class AttackCancelTable : ScriptableObject
{
    public CancelRule[] Rules = Array.Empty<CancelRule>();

    /// <summary>
    /// 窗口匹配查询：特例规则（指定来源）先于通配规则（来源为空）——特例天然压过泛取消。
    /// 只回答「取消到谁」；激活可行性（冷却/能量/速度门槛）由 PlayerCombat.CanActivate 验证
    /// </summary>
    public AttackDefinition Query(AttackDefinition current, int segmentIndex, AttackPhaseKind phase,
        float timeInPhase, InputIntent intent, float speedRatio)
    {
        if (current == null || intent == InputIntent.None || intent == InputIntent.TimeStop) return null;

        AttackDefinition wildcardHit = null;
        for (int i = 0; i < Rules.Length; i++)
        {
            CancelRule rule = Rules[i];
            if (rule.TargetAttack == null || rule.RequiredIntent != intent) continue;

            if (rule.SourceAttack == null)
            {
                if (wildcardHit == null && RuleMatches(rule, segmentIndex, phase, timeInPhase, speedRatio))
                {
                    wildcardHit = rule.TargetAttack;
                }
                continue;
            }
            if (rule.SourceAttack != current) continue;
            if (RuleMatches(rule, segmentIndex, phase, timeInPhase, speedRatio)) return rule.TargetAttack;
        }
        return wildcardHit;
    }

    private static bool RuleMatches(CancelRule rule, int segmentIndex, AttackPhaseKind phase, float timeInPhase, float speedRatio)
    {
        if (rule.SourceSegment >= 0 && rule.SourceSegment != segmentIndex) return false;
        if (rule.SourcePhase != AttackPhaseKind.Any && rule.SourcePhase != phase) return false;
        if (timeInPhase < rule.WindowStart || timeInPhase > rule.WindowEnd) return false;
        return speedRatio >= rule.MinSpeedRatio;
    }
}
