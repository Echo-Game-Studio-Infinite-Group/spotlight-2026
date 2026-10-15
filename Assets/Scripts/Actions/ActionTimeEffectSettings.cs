using System;
using UnityEngine;

namespace GameJam.Actions
{
    [Serializable]
    public sealed class ActionTimeEffectSettings
    {
        public bool UsesVectorConverter;
        [Range(0f, 1f)] public float GlobalRate = 1f;
        [Min(0f)] public float DurationSeconds;
    }
}
