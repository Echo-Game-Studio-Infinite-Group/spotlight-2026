// 判定可视化全局开关（设计 §五：判定框可视化是行业标配，没有它调不了手感）
// 开关入口在编辑器菜单「超高速行者/战斗/切换判定框可视化」；绘制散布在各组件 OnDrawGizmos：
// 伤害框红 / parry 框蓝 / 受击框绿 / 扫掠路径黄
public static class CombatDebug
{
    public static bool DrawHitboxes;
}
