using System;
using UnityEngine;

namespace GameJam.Flow
{
    [Serializable]
    public sealed class RoomPoolEntry
    {
        [InspectorName("启用")] public bool Enabled = true;
        [InspectorName("差分")] public RoomVariantDefinition Variant;
        [InspectorName("基础权重"), Min(0f)] public float Weight = 1f;
    }
}
