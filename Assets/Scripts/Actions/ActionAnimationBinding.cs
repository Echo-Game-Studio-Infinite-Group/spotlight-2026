using System;
using UnityEngine;

namespace GameJam.Actions
{
    [Serializable]
    public sealed class ActionAnimationBinding
    {
        public string AnimatorState;
        public AnimationClip Clip;
        [Range(0f, 1f)] public float NormalizedStart;
        [Range(0f, 1f)] public float NormalizedEnd = 1f;
        [Min(0)] public int BlendFrames;
    }
}
