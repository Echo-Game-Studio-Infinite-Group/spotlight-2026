using UnityEngine;

namespace GameJam.Flow
{
    public sealed class FlowContentGroup : MonoBehaviour
    {
        [InspectorName("内容组编号")] public string Id;
        [InspectorName("仅当前房间启用逻辑")] public bool GameplayOnly;
    }
}
