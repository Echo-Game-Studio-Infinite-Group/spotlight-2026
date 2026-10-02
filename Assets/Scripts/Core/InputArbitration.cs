using System;
using UnityEngine;

// 逻辑键：缓冲按下沿的语义载体（设备无关）。物理键绑定集中在 PlayerControls.inputactions，
// 本层及以上不见 KeyCode——改绑/换设备不动战斗代码
public enum LogicalButton { None, Attack, Skill, Jump, DodgeShift, Q }

// 组合修饰位（AND 语义）。单条规则表达不了"或"（闪避 = A/D 任一）：用两条同意图规则各写一半
[Flags]
public enum InputModifier
{
    None  = 0,
    Forward = 1 << 0, // W
    Back    = 1 << 1, // S
    Left    = 1 << 2, // A
    Right   = 1 << 3, // D
    Sprint  = 1 << 4, // Shift
    Slide   = 1 << 5, // Ctrl
    Dash    = 1 << 6, // Q
    Skill   = 1 << 7, // 右键
}

// 一条仲裁规则：触发键按下沿已入缓冲 且 全部修饰位按住 → 输出意图。数组序即优先级（特定组合在前，泛用在后）
[Serializable]
public struct ArbitrationRule
{
    public InputIntent Intent;
    public LogicalButton Trigger;
    public InputModifier Modifiers;
}

// 仲裁规则的默认表与快照→修饰位换算
public static class InputArbitration
{
    // 代码内置默认表：与首版行为逐条一致（连斩 > 冲刺 > 推斩 > 普攻 > 闪避 > 折返 > 加速 > 高跳）。
    // 优先级基线待哈士奇确认——确认后落 InputArbitrationTable 资产覆盖，不改代码
    public static readonly ArbitrationRule[] DefaultRules =
    {
        new ArbitrationRule { Intent = InputIntent.Rashomon,   Trigger = LogicalButton.Attack,     Modifiers = InputModifier.Skill | InputModifier.Forward },
        new ArbitrationRule { Intent = InputIntent.Dash,       Trigger = LogicalButton.Attack,     Modifiers = InputModifier.Dash },
        new ArbitrationRule { Intent = InputIntent.PushSlash,  Trigger = LogicalButton.Attack,     Modifiers = InputModifier.Skill },
        new ArbitrationRule { Intent = InputIntent.Attack,     Trigger = LogicalButton.Attack,     Modifiers = InputModifier.None },
        new ArbitrationRule { Intent = InputIntent.Dodge,      Trigger = LogicalButton.DodgeShift, Modifiers = InputModifier.Left },
        new ArbitrationRule { Intent = InputIntent.Dodge,      Trigger = LogicalButton.DodgeShift, Modifiers = InputModifier.Right },
        new ArbitrationRule { Intent = InputIntent.Turnaround, Trigger = LogicalButton.Skill,      Modifiers = InputModifier.Back },
        new ArbitrationRule { Intent = InputIntent.Accelerate, Trigger = LogicalButton.Skill,      Modifiers = InputModifier.Forward },
        new ArbitrationRule { Intent = InputIntent.HighJump,   Trigger = LogicalButton.Jump,       Modifiers = InputModifier.Skill },
    };

    // 按住态快照 → 修饰位（仲裁判定共用）
    public static InputModifier ReadModifiers(in InputSnapshot snapshot)
    {
        InputModifier held = InputModifier.None;
        if (snapshot.ForwardHeld) held |= InputModifier.Forward;
        if (snapshot.BackHeld) held |= InputModifier.Back;
        if (snapshot.LeftHeld) held |= InputModifier.Left;
        if (snapshot.RightHeld) held |= InputModifier.Right;
        if (snapshot.SprintHeld) held |= InputModifier.Sprint;
        if (snapshot.SlideHeld) held |= InputModifier.Slide;
        if (snapshot.DashHeld) held |= InputModifier.Dash;
        if (snapshot.SkillHeld) held |= InputModifier.Skill;
        return held;
    }
}

// 仲裁规则表（可选 SO）：留空 = 用代码内置默认表；赋值后优先级/组合在 Inspector 调，不改代码
[CreateAssetMenu(fileName = "InputArbitrationTable", menuName = "超高速行者/Core/InputArbitrationTable")]
public class InputArbitrationTable : ScriptableObject
{
    [Tooltip("规则数组：次序即优先级（特定组合在前，泛用在后）")]
    public ArbitrationRule[] Rules = Array.Empty<ArbitrationRule>();
}
