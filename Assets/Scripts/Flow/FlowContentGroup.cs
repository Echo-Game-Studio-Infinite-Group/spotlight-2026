using UnityEngine;

namespace GameJam.Flow
{
    public sealed class FlowContentGroup : MonoBehaviour
    {
        [Tooltip("供差分启用内容组列表引用的编号，必须与列表中的文字完全一致。")]
        [InspectorName("内容组编号")] public string Id;
        [Tooltip("仅当前房间启用，预加载时保持关闭。敌人和生成器应放在勾选此项的内容组中。")]
        [InspectorName("仅当前房间启用逻辑")] public bool GameplayOnly;
    }
}
