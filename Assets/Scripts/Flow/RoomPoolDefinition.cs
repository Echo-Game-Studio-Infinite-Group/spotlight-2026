using UnityEngine;

namespace GameJam.Flow
{
    [CreateAssetMenu(menuName = "超高速行者/流程/地图池", fileName = "Pool")]
    public sealed class RoomPoolDefinition : ScriptableObject
    {
        [Tooltip("地图池的唯一编号，用于配表及表格导入匹配。")]
        [InspectorName("地图池编号")] public string Id;
        [Tooltip("变化房池需要勾选，条目的稳定装置增量必须大于 0；此选项本身不会在进入房间时直接增加混乱度。")]
        [InspectorName("会增加混乱度")] public bool ChangesChaos;
        [Tooltip("可抽取的差分及其基础权重。相同差分不能在同一地图池中重复登记。")]
        [InspectorName("条目")] public RoomPoolEntry[] Entries = new RoomPoolEntry[0];
    }
}
