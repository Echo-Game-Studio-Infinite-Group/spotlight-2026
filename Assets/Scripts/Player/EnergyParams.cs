using UnityEngine;

// 矢量转换器（能量账户）的全部可调数值
[CreateAssetMenu(fileName = "EnergyParams", menuName = "超高速行者/EnergyParams")]
public class EnergyParams : ScriptableObject
{
    [Header("能量账户")]
    [Tooltip("能量上限")]
    public float MaxEnergy = 200f;

    [Tooltip("每 tick 能量增量 = max(0, 水平速度/地速阈值 - 1) × 本系数。速度做了归一化，故改地速阈值不会改变积能曲线")]
    public float EnergyPerTickPerExcessSpeed = 1f;

    [Header("机动技能耗能")]
    [Tooltip("加速：右键 + W（+Shift），快速加速至地速阈值")]
    public float AccelerateCost = 50f;

    [Tooltip("高跳：右键 + 空格，跳跃高度约为常态两倍")]
    public float HighJumpCost = 20f;

    [Tooltip("折返：S + 右键，速度值不变、方向反向")]
    public float ReverseCost = 50f;

    [Header("时停耗能")]
    [Tooltip("长按右键期间每 tick 消耗；满能量 200 / 本值 = 可维持的 tick 数（按 60tick/秒 约 3.33 秒）")]
    public float TimeStopCostPerTick = 1f;
}
