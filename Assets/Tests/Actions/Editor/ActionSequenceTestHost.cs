using System.Collections.Generic;
using GameJam.Actions;

public sealed class ActionSequenceTestHost : IActionSequenceHost, IActionSequenceSink
{
    public float Energy = 200;
    public bool CommitSucceeds = true;
    public readonly HashSet<string> Conditions = new HashSet<string>();
    public readonly List<string> Segments = new List<string>();
    public readonly List<string> Events = new List<string>();
    public readonly List<ActionExitReason> Exits = new List<ActionExitReason>();
    public readonly List<long> Instances = new List<long>();

    public bool CheckCondition(string key, ActionExecutionState source, ActionInputRequest request) => Conditions.Contains(key);
    public bool CanStart(ActionExecutionState source, ActionInputRequest request, out string reason)
    {
        reason = Energy >= request.Target.EnergyCost ? null : "能量不足";
        return reason == null;
    }
    public bool TryCommit(ActionExecutionState source, ActionInputRequest request)
    {
        if (!CommitSucceeds || Energy < request.Target.EnergyCost) return false;
        Energy -= request.Target.EnergyCost;
        return true;
    }
    public void OnActionStarted(ActionExecutionState state) => Instances.Add(state.InstanceId);
    public void OnSegmentEntered(ActionExecutionState state) => Segments.Add(state.Segment.SegmentId);
    public void OnFrameEvent(ActionExecutionState state, ActionFrameEvent frameEvent) => Events.Add(frameEvent.EventKey + "@" + state.ActionFrame);
    public void OnActionEnded(ActionExecutionState state, ActionExitReason reason) => Exits.Add(reason);
    public void OnStateSampled(ActionExecutionState state) { }
}
