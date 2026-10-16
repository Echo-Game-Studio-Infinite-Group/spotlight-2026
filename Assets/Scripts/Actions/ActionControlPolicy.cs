using System;

namespace GameJam.Actions
{
    [Serializable]
    public sealed class ActionControlPolicy
    {
        public bool AllowMove = true;
        public bool AllowTurn = true;
        public bool AllowJump = true;
        public bool AllowSlide = true;
        public bool AllowSprint = true;
        public float GravityMultiplier = 1f;
        public string MovementCommand;
        public float CommandValue;
    }
}
