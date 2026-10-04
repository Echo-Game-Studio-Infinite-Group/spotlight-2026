using System;
using System.Collections.Generic;

namespace GameJam.Actions
{
    public sealed class ActionInputRecognizer
    {
        private struct Occurrence
        {
            public long Id;
            public long Tick;
            public long HeldFrames;
            public ActionInputEdge Edge;
        }
        private readonly ActionCatalog _catalog;
        private readonly List<Occurrence> _history = new List<Occurrence>();
        private readonly HashSet<long> _reserved = new HashSet<long>();
        private readonly Dictionary<ActionInputButtons, long> _holdStarts = new Dictionary<ActionInputButtons, long>();
        private readonly HashSet<ActionInputButtons> _claimedHolds = new HashSet<ActionInputButtons>();
        private long _nextId;

        public ActionInputRecognizer(ActionCatalog catalog) => _catalog = catalog;

        public void Observe(ActionInputSample sample, ActionExecutionState source,
            IActionSequenceHost host, List<ActionInputRequest> output)
        {
            int firstNew = _history.Count;
            if (sample.Edges != null)
                foreach (ActionInputEdge edge in sample.Edges)
                {
                    if (!IsSingleButton(edge.Button) || edge.Trigger == ActionInputStep.Edge.Held) continue;
                    long heldFrames = _holdStarts.TryGetValue(edge.Button, out long began) ? sample.Tick - began : 0;
                    if (edge.Trigger == ActionInputStep.Edge.Pressed) _holdStarts[edge.Button] = sample.Tick;
                    _history.Add(new Occurrence { Id = ++_nextId, Tick = sample.Tick, HeldFrames = heldFrames, Edge = edge });
                    if (edge.Trigger == ActionInputStep.Edge.Released)
                    {
                        _holdStarts.Remove(edge.Button);
                        _claimedHolds.Remove(edge.Button);
                    }
                }
            foreach (ActionInputButtons button in Enum.GetValues(typeof(ActionInputButtons)))
            {
                if (!IsSingleButton(button)) continue;
                if ((sample.Held & button) == 0)
                {
                    _holdStarts.Remove(button);
                    _claimedHolds.Remove(button);
                    continue;
                }
                if (!_holdStarts.TryGetValue(button, out long began)) _holdStarts[button] = began = sample.Tick;
                if (_claimedHolds.Contains(button) || !HasHoldPattern(button)) continue;
                _history.Add(new Occurrence { Id = ++_nextId, Tick = sample.Tick, HeldFrames = sample.Tick - began,
                    Edge = new ActionInputEdge(button, ActionInputStep.Edge.Held, sample.Held, sample.Move) });
            }

            for (int index = firstNew; index < _history.Count; index++)
            {
                Occurrence trigger = _history[index];
                if (_reserved.Contains(trigger.Id)) continue;
                ActionDefinition best = null;
                List<long> bestIds = null;
                if (_catalog != null && _catalog.Actions != null)
                    foreach (ActionDefinition action in _catalog.Actions)
                    {
                        if (action == null || action.Input == null || !TryMatch(action.Input, index, out List<long> ids)) continue;
                        var request = new ActionInputRequest(action, trigger.Id, sample.Tick, source.InstanceId, trigger.Edge.DirectionAtEvent);
                        if (!ActionConditionEvaluator.Matches(action.Input.RequireAll, action.Input.RequireAny, host, source, request)) continue;
                        if (best != null && (best.Input.Priority > action.Input.Priority ||
                            best.Input.Priority == action.Input.Priority && best.Input.Steps.Count >= action.Input.Steps.Count)) continue;
                        best = action;
                        bestIds = ids;
                    }
                if (best == null) continue;
                foreach (long id in bestIds) _reserved.Add(id);
                if (trigger.Edge.Trigger == ActionInputStep.Edge.Held) _claimedHolds.Add(trigger.Edge.Button);
                output.Add(new ActionInputRequest(best, trigger.Id, sample.Tick, source.InstanceId, trigger.Edge.DirectionAtEvent));
            }
            int capacity = _catalog != null ? Math.Max(1, _catalog.InputHistoryCapacity) : 1;
            while (_history.Count > capacity)
            {
                _reserved.Remove(_history[0].Id);
                _history.RemoveAt(0);
            }
        }

        private bool TryMatch(ActionInputPolicy policy, int triggerIndex, out List<long> ids)
        {
            ids = null;
            if (policy.Steps == null || policy.Steps.Count == 0) return false;
            int index = triggerIndex;
            long nextTick = _history[index].Tick;
            var matched = new List<long>(policy.Steps.Count);
            for (int stepIndex = policy.Steps.Count - 1; stepIndex >= 0; stepIndex--)
            {
                ActionInputStep step = policy.Steps[stepIndex];
                bool found = false;
                while (index >= 0)
                {
                    Occurrence occurrence = _history[index];
                    if (nextTick - occurrence.Tick > policy.MaxStepGapFrames) return false;
                    if (!_reserved.Contains(occurrence.Id) && Matches(step, occurrence))
                    {
                        matched.Add(occurrence.Id);
                        nextTick = occurrence.Tick;
                        index--;
                        found = true;
                        break;
                    }
                    // 最后一步必须就是当前触发事件，不能借另一条新事件重复识别旧序列。
                    if (stepIndex == policy.Steps.Count - 1) return false;
                    index--;
                }
                if (!found) return false;
            }
            ids = matched;
            return true;
        }

        private static bool Matches(ActionInputStep step, Occurrence occurrence)
        {
            if (step == null || step.Button != occurrence.Edge.Button || step.Trigger != occurrence.Edge.Trigger) return false;
            return (occurrence.Edge.HeldAtEvent & step.RequireHeld) == step.RequireHeld &&
                (occurrence.Edge.HeldAtEvent & step.ForbidHeld) == 0 && occurrence.HeldFrames >= step.MinHoldFrames &&
                (step.MaxHoldFrames == 0 || occurrence.HeldFrames <= step.MaxHoldFrames);
        }

        private bool HasHoldPattern(ActionInputButtons button)
        {
            if (_catalog == null || _catalog.Actions == null) return false;
            foreach (ActionDefinition action in _catalog.Actions)
            {
                List<ActionInputStep> steps = action != null && action.Input != null ? action.Input.Steps : null;
                if (steps != null && steps.Count > 0 && steps[steps.Count - 1] != null &&
                    steps[steps.Count - 1].Button == button && steps[steps.Count - 1].Trigger == ActionInputStep.Edge.Held) return true;
            }
            return false;
        }

        public static bool IsSingleButton(ActionInputButtons button)
        {
            int bits = (int)button;
            return bits > 0 && (bits & (bits - 1)) == 0;
        }

        public void Clear()
        {
            _history.Clear();
            _reserved.Clear();
            _holdStarts.Clear();
            _claimedHolds.Clear();
        }
    }
}
