using System.Collections.Generic;

namespace GameJam.Actions
{
    public static class ActionConditionEvaluator
    {
        public static bool Matches(IReadOnlyList<string> all, IReadOnlyList<string> any,
            IActionSequenceHost host, ActionExecutionState source, ActionInputRequest request)
        {
            if (all != null)
                foreach (string key in all)
                    if (string.IsNullOrWhiteSpace(key) || host == null || !host.CheckCondition(key, source, request))
                        return false;
            if (any == null || any.Count == 0) return true;
            foreach (string key in any)
                if (!string.IsNullOrWhiteSpace(key) && host != null && host.CheckCondition(key, source, request))
                    return true;
            return false;
        }
    }
}
