using UnityEngine;

namespace GameJam.Flow
{
    [CreateAssetMenu(menuName = "超高速行者/流程/循环规则", fileName = "Loop")]
    public sealed class LoopRuleDefinition : ScriptableObject
    {
        [Tooltip("循环规则的稳定编号，用于规划器记录当前循环。")]
        [InspectorName("规则编号")] public string Id;
        [Tooltip("循环生效的最低混乱度，包含此值。")]
        [InspectorName("混乱度下限")] public int MinimumChaos;
        [Tooltip("循环生效的最高混乱度，包含此值；离开区间后退出当前循环。")]
        [InspectorName("混乱度上限")] public int MaximumChaos = 30;
        [Tooltip("循环中普通房的来源池，不能勾选会增加混乱度。")]
        [InspectorName("普通房来源池")] public RoomPoolDefinition NeutralPool;
        [Tooltip("循环中变化房的来源池，必须勾选会增加混乱度。")]
        [InspectorName("变化房来源池")] public RoomPoolDefinition ChaosPool;
        [Tooltip("候选值只能填 3 或 6；建立循环时随机选一个数量，再加一个变化房组成循环序列。")]
        [InspectorName("普通房数量候选")] public int[] NeutralCounts = { 3, 6 };
        [Tooltip("允许同一箱庭在本次循环序列中出现多次；关闭时必须有足够多符合条件的箱庭。")]
        [InspectorName("允许同一箱庭重复")] public bool AllowRepeatedRooms = true;
        public bool Supports(int chaos) => chaos >= MinimumChaos && chaos <= MaximumChaos;
    }
}
