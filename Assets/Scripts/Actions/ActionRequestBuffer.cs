using System;
using System.Collections.Generic;

namespace GameJam.Actions
{
    public sealed class ActionRequestBuffer
    {
        private struct Entry
        {
            public ActionInputRequest Request;
            public long ExpiresAt;
        }
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly int _capacity;
        public int Count => _entries.Count;
        public ActionInputRequest this[int index] => _entries[index].Request;

        public ActionRequestBuffer(int capacity) => _capacity = Math.Max(1, capacity);

        public bool Push(ActionInputRequest request)
        {
            if (request.Target == null || request.Target.Input == null) return false;
            ActionInputPolicy policy = request.Target.Input;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                ActionInputRequest old = _entries[i].Request;
                if (old.TriggerId == request.TriggerId) return false;
                if (!string.IsNullOrEmpty(policy.BufferGroup) && old.Target.Input.BufferGroup == policy.BufferGroup)
                {
                    if (policy.ReplacePolicy == ActionInputPolicy.Replacement.EarliestInGroup) return false;
                    if (policy.ReplacePolicy == ActionInputPolicy.Replacement.LatestInGroup) _entries.RemoveAt(i);
                }
            }
            if (_entries.Count >= _capacity) _entries.RemoveAt(0);
            _entries.Add(new Entry { Request = request,
                ExpiresAt = request.CreatedTick + Math.Max(1, policy.PreInputFrames) });
            return true;
        }

        public void Prune(long tick, long frozenTicks = 0)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                if (frozenTicks > 0 && entry.Request.Target != null &&
                    entry.Request.Target.Input.FreezeExpiryDuringHitStop)
                {
                    entry.ExpiresAt += Math.Max(0, Math.Min(frozenTicks, tick - entry.Request.CreatedTick));
                    _entries[i] = entry;
                }
                if (entry.Request.Target == null || tick >= entry.ExpiresAt) _entries.RemoveAt(i);
            }
        }

        public void DiscardSource(long instanceId, bool cancelled)
        {
            if (!cancelled) return;
            _entries.RemoveAll(entry => entry.Request.SourceInstanceId == instanceId &&
                !entry.Request.Target.Input.KeepOnSourceCancel);
        }

        public void RemoveAt(int index) => _entries.RemoveAt(index);
        public void DiscardImmediate(long tick)
            => _entries.RemoveAll(entry => entry.Request.CreatedTick <= tick && entry.Request.Target.Input.PreInputFrames == 0);
        public void Clear() => _entries.Clear();
    }
}
