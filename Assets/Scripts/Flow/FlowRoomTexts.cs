using System.Collections.Generic;

namespace GameJam.Flow
{
    public sealed class FlowRoomTexts
    {
        private readonly FlowTextPoint[] _points;
        public FlowRoomTexts(FlowRoomTemplate template, RoomVariantDefinition variant)
        {
            _points = template.GetComponentsInChildren<FlowTextPoint>(true);
            var bindings = new Dictionary<string, FlowTextBinding>();
            foreach (FlowTextBinding binding in variant.Texts ?? new FlowTextBinding[0])
                if (binding != null && !string.IsNullOrWhiteSpace(binding.PointId) && !bindings.ContainsKey(binding.PointId))
                    bindings.Add(binding.PointId, binding);
            var seen = new HashSet<string>();
            foreach (FlowTextPoint point in _points)
            {
                FlowTextBinding binding = null;
                if (!string.IsNullOrWhiteSpace(point.Id) && seen.Add(point.Id)) bindings.TryGetValue(point.Id, out binding);
                point.Bind(binding);
            }
        }
        public void SetCurrent(bool current) { foreach (FlowTextPoint point in _points) point.SetCurrent(current); }
    }
}
