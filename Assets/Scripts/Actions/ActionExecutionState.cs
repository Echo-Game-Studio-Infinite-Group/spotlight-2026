using System;
using UnityEngine;

namespace GameJam.Actions
{
    public readonly struct ActionExecutionState
    {
        public readonly ActionDefinition Action;
        public readonly long InstanceId;
        public readonly int SegmentIndex;
        public readonly double FrameProgress;
        public readonly int SegmentStartFrame;
        public readonly ActionInputRequest Input;
        public int ActionFrame => (int)Math.Floor(FrameProgress);
        public ActionSegment Segment => Action != null && SegmentIndex >= 0 && SegmentIndex < Action.Timeline.Count
            ? Action.Timeline[SegmentIndex] : null;
        public double SegmentFrameProgress => FrameProgress - SegmentStartFrame;
        public int SegmentFrame => (int)Math.Floor(SegmentFrameProgress);
        public float AnimationNormalizedTime => Segment != null && Segment.Animation != null
            ? Mathf.Lerp(Segment.Animation.NormalizedStart, Segment.Animation.NormalizedEnd,
                Mathf.Clamp01((float)(SegmentFrameProgress / Segment.DurationFrames))) : 0f;

        public ActionExecutionState(ActionDefinition action, long instanceId, int segmentIndex,
            double frameProgress, int segmentStartFrame, ActionInputRequest input)
        {
            Action = action;
            InstanceId = instanceId;
            SegmentIndex = segmentIndex;
            FrameProgress = frameProgress;
            SegmentStartFrame = segmentStartFrame;
            Input = input;
        }
    }
}
