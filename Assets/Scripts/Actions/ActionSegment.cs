using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameJam.Actions
{
    [Serializable]
    public sealed class ActionSegment
    {
        public string SegmentId = Guid.NewGuid().ToString("N");
        public string DisplayName = "新子段";
        public ActionPhase Phase;
        [Min(1)] public int DurationFrames = 1;
        public List<ActionFrameEvent> Events = new List<ActionFrameEvent>();
        public ActionControlPolicy Control = new ActionControlPolicy();
        public ActionAnimationBinding Animation = new ActionAnimationBinding();
    }
}
