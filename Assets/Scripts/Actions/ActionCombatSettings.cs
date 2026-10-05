using System;
using UnityEngine;

namespace GameJam.Actions
{
    [Serializable]
    public sealed class ActionCombatSettings
    {
        public bool Enabled;
        [Min(0f)] public float Damage = 25f;
        public LayerMask HitMask = ~0;
    }
}
