using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameJam.Actions
{
    // 将动作配表转换成通用盒查询；扫掠历史属于一次动作，不能跨取消保留。
    public sealed class ActionHitboxSampler
    {
        private readonly Dictionary<int, AttackVolumePose> _history = new Dictionary<int, AttackVolumePose>();
        private long _instance;
        public void Begin(long instance) { _history.Clear(); _instance = instance; }
        public void End(long instance) { if (_instance == instance) { _history.Clear(); _instance = 0; } }

        public void Sample(ActionExecutionState from, ActionExecutionState to, IReadOnlyList<Vector3> path,
            Transform root, Hitbox hitbox, Vector3 obstacleOffset, int environmentMask, Action<int> prepareDamage)
        {
            if (_instance != to.InstanceId || _instance == 0 || hitbox == null) return;
            SampleVolumes(from, to, path, root, to.Action.Combat.HitVolumes,
                (group, pose, previous, spacing, start, end) => { prepareDamage(group); hitbox.SampleBox(to.InstanceId, group, pose, pose.Root + obstacleOffset, environmentMask); });
            if (_instance == to.InstanceId) hitbox.SetConfiguredWindow(to.InstanceId,
                to.Action.Combat.HitVolumes.Exists(volume => volume.IsActive(to.Action, to.FrameProgress)));
        }

        public void SampleVolumes(ActionExecutionState from, ActionExecutionState to, IReadOnlyList<Vector3> path,
            Transform root, List<ActionHitVolume> volumes, Action<int, AttackVolumePose, AttackVolumePose, float, float, float> sample)
        {
            if (_instance != to.InstanceId || _instance == 0) return;
            long instance = _instance;
            Physics.SyncTransforms();
            for (int i = 0; i < volumes.Count; i++)
            {
                if (_instance != instance) return;
                ActionHitVolume volume = volumes[i];
                bool wasActive = volume.IsActive(to.Action, from.FrameProgress);
                bool active = volume.IsActive(to.Action, to.FrameProgress);

                if ((!wasActive && !active) || !volume.TryGetPose(root, out Vector3 center, out Vector3 extents, out Quaternion rotation))
                { _history.Remove(i); continue; }
                var current = new AttackVolumePose(center, extents, rotation, root.position);

                if (wasActive && _history.TryGetValue(i, out AttackVolumePose previous))
                {
                    AttackVolumePose begin = previous;
                    if (path != null && path.Count > 0)
                    {
                        float length = 0f; Vector3 point = previous.Root;
                        foreach (Vector3 next in path) { length += Vector3.Distance(point, next); point = next; }
                        float elapsed = 0f; float priorT = 0f; point = previous.Root;
                        for (int p = 0; p < path.Count; p++)
                        {
                            elapsed += Vector3.Distance(point, path[p]); point = path[p];
                            float t = length > 0f ? elapsed / length : (p + 1f) / path.Count;
                            var end = new AttackVolumePose(point + Vector3.Lerp(previous.Center - previous.Root, current.Center - current.Root, t),
                                Vector3.Lerp(previous.Extents, current.Extents, t), Quaternion.Slerp(previous.Rotation, current.Rotation, t), point);
                            Sweep(begin, end, volume, instance, sample, priorT, t); begin = end; priorT = t;
                            if (_instance != instance) return;
                        }
                    }
                    else Sweep(previous, current, volume, instance, sample, from.FrameProgress == to.FrameProgress ? 1f : 0f, 1f);
                }
                else sample(volume.HitGroup, current, current, volume.SweepSpacing, 1f, 1f);
                if (_instance != instance) return;
                if (active) _history[i] = current; else _history.Remove(i);
            }
        }

        private void Sweep(AttackVolumePose from, AttackVolumePose to, ActionHitVolume volume, long instance, Action<int, AttackVolumePose, AttackVolumePose, float, float, float> sample, float start = 0f, float end = 1f)
        {
            float radius = Mathf.Max(from.Extents.magnitude, to.Extents.magnitude);
            float travel = Vector3.Distance(from.Center, to.Center) + Quaternion.Angle(from.Rotation, to.Rotation) * Mathf.Deg2Rad * radius;
            float smallest = Mathf.Min(to.Extents.x, Mathf.Min(to.Extents.y, to.Extents.z));
            float spacing = Mathf.Max(.01f, Mathf.Min(volume.SweepSpacing, smallest));
            int steps = Mathf.Max(1, Mathf.CeilToInt(travel / spacing));
            for (int i = 1; i <= steps && _instance == instance; i++)
            {
                float t = i / (float)steps;
                var pose = new AttackVolumePose(Vector3.Lerp(from.Center, to.Center, t), Vector3.Lerp(from.Extents, to.Extents, t),
                    Quaternion.Slerp(from.Rotation, to.Rotation, t), Vector3.Lerp(from.Root, to.Root, t));
                sample(volume.HitGroup, pose, ParryGeometry.Lerp(from, to, (i - 1f) / steps), volume.SweepSpacing,
                    Mathf.Lerp(start, end, (i - 1f) / steps), Mathf.Lerp(start, end, t));
            }
        }
    }
}
