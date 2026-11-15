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
        [Tooltip("独立拼刀框；帧窗口随动作时间推进，空列表表示该招不能 Parry")]
        public System.Collections.Generic.List<ActionHitVolume> ParryVolumes = new System.Collections.Generic.List<ActionHitVolume>();
        public System.Collections.Generic.List<ActionHitVolume> HitVolumes = new System.Collections.Generic.List<ActionHitVolume>();
    }
}
