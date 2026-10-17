using UnityEngine;

namespace GameJam.Flow
{
    [CreateAssetMenu(menuName = "超高速行者/流程/循环规则", fileName = "Loop")]
    public sealed class LoopRuleDefinition : ScriptableObject
    {
        [InspectorName("规则编号")] public string Id;
        [InspectorName("混乱度下限")] public int MinimumChaos;
        [InspectorName("混乱度上限")] public int MaximumChaos = 30;
        [InspectorName("普通房来源池")] public RoomPoolDefinition NeutralPool;
        [InspectorName("变化房来源池")] public RoomPoolDefinition ChaosPool;
        [InspectorName("普通房数量候选")] public int[] NeutralCounts = { 3, 6 };
        [InspectorName("允许同一箱庭重复")] public bool AllowRepeatedRooms = true;
        public bool Supports(int chaos) => chaos >= MinimumChaos && chaos <= MaximumChaos;
    }
}
