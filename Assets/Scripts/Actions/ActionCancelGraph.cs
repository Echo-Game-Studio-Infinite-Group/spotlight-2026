using System.Collections.Generic;

namespace GameJam.Actions
{
    public static class ActionCancelGraph
    {
        // 每个窗口保留一条独立边，重叠窗口的条件不会被合并丢失。
        public static List<ActionCancelEdge> Build(ActionCatalog catalog)
        {
            var edges = new List<ActionCancelEdge>();
            if (catalog?.Actions == null) return edges;
            var visited = new HashSet<ActionDefinition>();
            foreach (ActionDefinition source in catalog.Actions)
            {
                if (source == null || !visited.Add(source) || source.CancelWindows == null) continue;
                for (int i = 0; i < source.CancelWindows.Count; i++)
                {
                    ActionCancelWindow window = source.CancelWindows[i];
                    if (window?.Targets == null) continue;
                    bool valid = source.TryGetWindowRange(window, out int start, out int end);
                    var targets = new HashSet<ActionDefinition>();
                    foreach (ActionDefinition target in window.Targets)
                        if (target != null && targets.Add(target)) edges.Add(new ActionCancelEdge(source, target, i, start, end, valid));
                }
            }
            return edges;
        }
    }
}
