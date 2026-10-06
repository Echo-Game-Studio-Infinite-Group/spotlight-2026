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
        public bool ScaleDamageWithSpeed;
        public AnimationCurve SpeedDamage = AnimationCurve.Linear(0f, 1f, 2f, 2f);
        public System.Collections.Generic.List<ActionHitVolume> HitVolumes = new System.Collections.Generic.List<ActionHitVolume>();
    }
}
