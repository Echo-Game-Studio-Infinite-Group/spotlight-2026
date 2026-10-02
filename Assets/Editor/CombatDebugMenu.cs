using UnityEditor;
using UnityEngine;

// 判定框可视化开关（设计 §五：判定框可视化是调手感标配）：EditorPrefs 持久化
// 绘制在 Hitbox / Hurtbox 的 OnDrawGizmos——伤害框红 / parry 框蓝 / 受击框绿 / 扫掠路径黄
public static class CombatDebugMenu
{
    private const string PrefKey = "超高速行者/DrawHitboxes";

    [InitializeOnLoadMethod]
    private static void LoadPref()
    {
        CombatDebug.DrawHitboxes = EditorPrefs.GetBool(PrefKey, false);
    }

    [MenuItem("超高速行者/战斗/切换判定框可视化")]
    private static void Toggle()
    {
        CombatDebug.DrawHitboxes = !CombatDebug.DrawHitboxes;
        EditorPrefs.SetBool(PrefKey, CombatDebug.DrawHitboxes);
        Debug.Log($"[CombatDebug] 判定框可视化 = {CombatDebug.DrawHitboxes}（伤害红 / parry 蓝 / 受击绿 / 扫掠黄）");
    }
}
