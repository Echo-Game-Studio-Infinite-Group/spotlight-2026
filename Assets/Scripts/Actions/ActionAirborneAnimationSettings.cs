using System;

namespace GameJam.Actions
{
    [Serializable]
    public sealed class ActionAirborneAnimationSettings
    {
        public bool HoldJumpUpPose;
        public ActionAnimationBinding Airborne = new ActionAnimationBinding();
        public ActionAnimationBinding Descending = new ActionAnimationBinding();
        public ActionAnimationBinding Landing = new ActionAnimationBinding();
    }
}
