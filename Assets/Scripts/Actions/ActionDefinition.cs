using System.Collections.Generic;
using UnityEngine;

namespace GameJam.Actions
{
    [CreateAssetMenu(fileName = "Action", menuName = "超高速行者/动作序列/动作配置")]
    public sealed class ActionDefinition : ScriptableObject
    {
        public string ActionId;
        public string DisplayName;
        [TextArea] public string Description;
        public List<ActionSegment> Timeline = new List<ActionSegment>();
        public List<ActionCancelWindow> CancelWindows = new List<ActionCancelWindow>();
        public ActionInputPolicy Input = new ActionInputPolicy();
        public List<string> StartConditions = new List<string>();
        [Min(0f)] public float EnergyCost;
        [Min(0)] public int CooldownFrames;

        public string Label => string.IsNullOrEmpty(DisplayName) ? name : DisplayName;
        public int TotalFrames
        {
            get
            {
                long total = 0;
                if (Timeline != null)
                    foreach (ActionSegment segment in Timeline)
                        if (segment != null) total += Mathf.Max(0, segment.DurationFrames);
                return (int)System.Math.Min(total, int.MaxValue);
            }
        }

        public bool TryResolve(ActionFrameAnchor anchor, out int frame)
        {
            frame = 0;
            if (anchor == null || !System.Enum.IsDefined(typeof(ActionFrameAnchor.Boundary), anchor.RelativeTo)) return false;
            long value;
            if (anchor.RelativeTo == ActionFrameAnchor.Boundary.ActionStart) value = 0;
            else if (anchor.RelativeTo == ActionFrameAnchor.Boundary.ActionEnd) value = TotalFrames;
            else
            {
                value = 0;
                bool found = false;
                if (Timeline != null)
                    foreach (ActionSegment segment in Timeline)
                    {
                        if (segment == null) continue;
                        if (segment.SegmentId == anchor.SegmentId)
                        {
                            if (anchor.RelativeTo == ActionFrameAnchor.Boundary.SegmentEnd)
                                value += segment.DurationFrames;
                            found = true;
                            break;
                        }
                        value += segment.DurationFrames;
                    }
                if (!found) return false;
            }
            value += anchor.OffsetFrames;
            if (value < 0 || value > TotalFrames) return false;
            frame = (int)value;
            return true;
        }

        public bool TryGetWindowRange(ActionCancelWindow window, out int start, out int end)
        {
            start = end = 0;
            return window != null && TryResolve(window.Start, out start) &&
                TryResolve(window.End, out end) && start < end;
        }
    }
}
