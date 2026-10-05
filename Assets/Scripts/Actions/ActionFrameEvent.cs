using System;

namespace GameJam.Actions
{
    [Serializable]
    public sealed class ActionFrameEvent
    {
        public int Frame;
        public string EventKey;
        public int HitGroup;
        public float Value;
    }
}
