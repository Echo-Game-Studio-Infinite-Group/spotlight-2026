using UnityEngine;

namespace GameJam.Flow
{
    [CreateAssetMenu(menuName = "超高速行者/流程/箱庭", fileName = "Room")]
    public sealed class RoomDefinition : ScriptableObject
    {
        [Tooltip("箱庭的唯一编号，用于配表引用；建议保持稳定，中文名称填写在显示名称中。")]
        [InspectorName("箱庭编号")] public string Id;
        [Tooltip("配表列表中显示的箱庭名称，可使用中文。")]
        [InspectorName("显示名称")] public string DisplayName;
        [Tooltip("箱庭共用的地图预制体，需要配置入口、出口、记录点及几何范围。")]
        [InspectorName("房间模板")] public FlowRoomTemplate Template;
        [Tooltip("本箱庭的全部差分，数量为 1～6；所属箱庭必须正确，混乱度区间不能重叠。")]
        [InspectorName("差分")] public RoomVariantDefinition[] Variants = new RoomVariantDefinition[0];

        public RoomVariantDefinition ResolveVariant(int chaos, RoomVariantDefinition preferred)
        {
            if (preferred != null && preferred.Room == this && preferred.Supports(chaos)) return preferred;
            foreach (RoomVariantDefinition variant in Variants)
                if (variant != null && variant.Supports(chaos)) return variant;
            return null;
        }
    }
}
