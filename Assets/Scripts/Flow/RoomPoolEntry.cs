using System;
using UnityEngine;

namespace GameJam.Flow
{
    [Serializable]
    public sealed class RoomPoolEntry
    {
        [Tooltip("关闭后保留此条目，但不参与房间抽取。")]
        [InspectorName("启用")] public bool Enabled = true;
        [Tooltip("该条目指向的房间差分；还需满足差分的混乱度范围才参与抽取。")]
        [InspectorName("差分")] public RoomVariantDefinition Variant;
        [Tooltip("相对抽取权重，启用条目必须为有限正数；实际概率还受访问衰减及当前混乱度影响。")]
        [InspectorName("基础权重"), Min(0f)] public float Weight = 1f;
    }
}
