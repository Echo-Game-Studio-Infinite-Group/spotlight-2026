using System.Collections.Generic;
using UnityEngine;

namespace GameJam.Flow
{
    public static class FlowPlacement
    {
        public static Bounds TransformBounds(Bounds local, Vector3 position, Quaternion rotation)
        {
            var result = new Bounds(position + rotation * local.center, Vector3.zero);
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        result.Encapsulate(position + rotation * (local.center + Vector3.Scale(local.extents, new Vector3(x, y, z))));
            return result;
        }

        public static bool TryFind(FlowRoomInstance source, FlowRoomTemplate template, IReadOnlyList<FlowRoomInstance> retained,
            RunFlowConfig config, out Pose pose)
        {
            pose = default;
            if (template == null || template.Entry == null || template.Exit == null) return false;
            if (source == null)
            {
                Quaternion startRotation = Quaternion.Inverse(template.Entry.localRotation);
                pose = new Pose(-(startRotation * template.Entry.localPosition), startRotation); return true;
            }
            Transform exit = source.Root.Exit;
            Quaternion rotation = exit.rotation * Quaternion.Inverse(template.Entry.localRotation);
            float length = config.ConnectionLength;
            for (int attempt = 0; attempt < config.PlacementAttempts; attempt++, length *= config.ConnectionGrowth)
            {
                Vector3 entryPosition = exit.position + exit.forward * length;
                Vector3 position = entryPosition - rotation * template.Entry.localPosition;
                Bounds targetBounds = TransformBounds(template.GeometryBounds, position, rotation);
                targetBounds.Expand(config.RoomClearance * 2f);
                Bounds corridor = new Bounds(exit.position + Vector3.up * config.RailHeight * .5f, Vector3.zero);
                corridor.Encapsulate(entryPosition + Vector3.up * config.RailHeight);
                corridor.Expand(new Vector3(Mathf.Min(source.Root.ExitSize.x, template.EntrySize.x), 0f,
                    Mathf.Min(source.Root.ExitSize.x, template.EntrySize.x)));
                bool available = true;
                foreach (FlowRoomInstance room in retained)
                {
                    if (!room.IsReady || room.IsUnloading) continue;
                    if (targetBounds.Intersects(room.WorldBounds) || (room != source && corridor.Intersects(room.WorldBounds)))
                    { available = false; break; }
                }
                if (available) { pose = new Pose(position, rotation); return true; }
            }
            return false;
        }

        public static bool CrossedEntry(FlowRoomTemplate target, Vector3 previous, Vector3 current, float distance)
        {
            Vector3 a = target.Entry.InverseTransformPoint(previous);
            Vector3 b = target.Entry.InverseTransformPoint(current);
            if (a.z >= distance || b.z < distance || b.z <= a.z) return false;
            Vector3 cross = Vector3.Lerp(a, b, (distance - a.z) / (b.z - a.z));
            return Mathf.Abs(cross.x) < target.EntrySize.x * .5f && cross.y >= -0.1f && cross.y <= target.EntrySize.y;
        }
    }
}
