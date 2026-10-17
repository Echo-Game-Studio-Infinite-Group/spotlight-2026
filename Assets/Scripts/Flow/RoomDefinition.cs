using UnityEngine;

namespace GameJam.Flow
{
    [CreateAssetMenu(menuName = "超高速行者/流程/箱庭", fileName = "Room")]
    public sealed class RoomDefinition : ScriptableObject
    {
        [InspectorName("箱庭编号")] public string Id;
        [InspectorName("显示名称")] public string DisplayName;
        [InspectorName("房间模板")] public FlowRoomTemplate Template;
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
