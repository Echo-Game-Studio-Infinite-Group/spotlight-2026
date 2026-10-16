using System;
using UnityEngine;

namespace GameJam.Actions
{
    [Serializable]
    public sealed class ActionMotionSettings
    {
        public bool Enabled;
        public ActionFrameAnchor Start = new ActionFrameAnchor();
        public ActionFrameAnchor End = new ActionFrameAnchor { RelativeTo = ActionFrameAnchor.Boundary.ActionEnd };
        [Min(0f)] public float ForwardImpulse = 3f;
        [Min(0f)] public float BoostSpeedLimit = 15f;
        public bool DiveInAir = true;
        [Range(0f, 85f)] public float DiveAngle = 35f;
        [Min(0f)] public float MaxDiveSpeed = 25f;
        [Min(0f)] public float DiveResponse = 25f;
        public bool ClearMomentumOnCompletion;

        public bool IsActive(ActionDefinition action, double frame)
            => Enabled && action.TryResolve(Start, out int start) && action.TryResolve(End, out int end) && frame >= start && frame < end;
    }
}
