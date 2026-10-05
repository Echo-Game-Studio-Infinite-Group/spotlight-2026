using System;
using UnityEngine;

namespace GameJam.Actions
{
    [Serializable]
    public sealed class ActionInputStep
    {
        public enum Edge { Pressed, Released, Held }
        public ActionInputButtons Button = ActionInputButtons.Attack;
        public Edge Trigger;
        public ActionInputButtons RequireHeld;
        public ActionInputButtons ForbidHeld;
        [Min(0)] public int MinHoldFrames;
        [Min(0)] public int MaxHoldFrames;
    }
}
