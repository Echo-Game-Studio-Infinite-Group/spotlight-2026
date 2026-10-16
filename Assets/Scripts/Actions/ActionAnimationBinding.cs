using System;
using UnityEngine;

namespace GameJam.Actions
{
    [Serializable]
    public sealed class ActionAnimationBinding
    {
        public string AnimatorState;
        [Min(0)] public int Layer;
        public AnimationClip Clip;
        [Range(0f, 1f)] public float NormalizedStart;
        [Range(0f, 1f)] public float NormalizedEnd = 1f;
        [Min(0)] public int BlendFrames;
        // 每个状态独立采样时间，取消混合时新动作不能把旧状态的姿态拉回起点。
        public string TimeParameter => "ActionTime::" + AnimatorState;
    }
}
