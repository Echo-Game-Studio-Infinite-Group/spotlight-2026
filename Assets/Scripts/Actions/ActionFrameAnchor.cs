using System;

namespace GameJam.Actions
{
    [Serializable]
    public sealed class ActionFrameAnchor
    {
        public enum Boundary { SegmentStart, SegmentEnd, ActionStart, ActionEnd }
        public string SegmentId;
        public Boundary RelativeTo;
        public int OffsetFrames;
    }
}
