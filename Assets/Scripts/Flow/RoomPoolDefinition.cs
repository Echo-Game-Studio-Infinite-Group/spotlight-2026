using UnityEngine;

namespace GameJam.Flow
{
    [CreateAssetMenu(menuName = "超高速行者/流程/地图池", fileName = "Pool")]
    public sealed class RoomPoolDefinition : ScriptableObject
    {
        [InspectorName("地图池编号")] public string Id;
        [InspectorName("会增加混乱度")] public bool ChangesChaos;
        [InspectorName("条目")] public RoomPoolEntry[] Entries = new RoomPoolEntry[0];
    }
}
