using System;
using System.Collections.Generic;

namespace GameJam.Flow
{
    public sealed class RoomSelectionPlanner
    {
        private readonly RunFlowConfig _config;
        private readonly Dictionary<string, int> _visits = new Dictionary<string, int>();
        private readonly List<RoomVariantDefinition> _cycle = new List<RoomVariantDefinition>();
        private LoopRuleDefinition _loop;
        public FlowRandom Random { get; }
        public string LastError { get; private set; }
        public IReadOnlyList<RoomVariantDefinition> Cycle => _cycle;
        public int CycleIndex { get; private set; }
        public string LoopId => _loop != null ? _loop.Id : string.Empty;

        public RoomSelectionPlanner(RunFlowConfig config, int seed) { _config = config; Random = new FlowRandom(seed); }
        public int VisitCount(RoomVariantDefinition variant) => variant != null && _visits.TryGetValue(variant.Id, out int count) ? count : 0;
        public void RegisterEntry(RoomVariantDefinition variant)
        {
            if (variant != null) _visits[variant.Id] = VisitCount(variant) + 1;
        }
        public double EffectiveWeight(RoomPoolEntry entry)
        {
            int count = VisitCount(entry.Variant);
            return entry.Weight * Math.Pow(_config.VisitWeightMultiplier, _config.DecayOnlyOnce ? Math.Min(1, count) : count);
        }

        // 规划只读取游标；实际进入后才推进，预加载失败或恢复不会吞掉循环槽位。
        public RoomVariantDefinition Plan(int chaos, Predicate<RoomVariantDefinition> compatible = null)
        {
            LastError = string.Empty;
            if (_config.EnableSpecialRoutes && chaos == _config.ExceptionalChaos)
            { ClearLoop(); return Draw(new[] { _config.ExceptionalPool }, chaos, compatible); }
            if (chaos < _config.MinimumChaos)
            { ClearLoop(); return _config.EnableSpecialRoutes ? Draw(new[] { _config.NegativePool }, chaos, compatible) : Fail("负数路线未启用"); }
            if (chaos >= _config.MaximumChaos) { ClearLoop(); return Accept(_config.FinalRoom, chaos, compatible); }
            LoopRuleDefinition wanted = null;
            foreach (LoopRuleDefinition rule in _config.Loops)
                if (rule != null && rule.Supports(chaos)) { wanted = rule; break; }
            if (wanted == null) { ClearLoop(); return Draw(_config.NormalPools, chaos, compatible); }
            if (_loop != wanted)
            {
                ClearLoop();
                uint before = Random.State;
                int count = wanted.NeutralCounts[Random.NextIndex(wanted.NeutralCounts.Length)];
                var used = new HashSet<RoomDefinition>();
                for (int i = 0; i <= count; i++)
                {
                    RoomVariantDefinition picked = Draw(new[] { i == count ? wanted.ChaosPool : wanted.NeutralPool }, chaos,
                        variant => wanted.AllowRepeatedRooms || !used.Contains(variant.Room));
                    if (picked == null) { _cycle.Clear(); Random.State = before; return null; }
                    _cycle.Add(picked); used.Add(picked.Room);
                }
                for (int i = _cycle.Count - 1; i > 0; i--)
                { int j = Random.NextIndex(i + 1); RoomVariantDefinition value = _cycle[i]; _cycle[i] = _cycle[j]; _cycle[j] = value; }
                _loop = wanted;
            }
            RoomVariantDefinition slot = _cycle[CycleIndex];
            RoomVariantDefinition resolved = slot.Room.ResolveVariant(chaos, slot);
            return Accept(resolved, chaos, compatible);
        }

        public void CommitSuccessor()
        {
            if (_loop != null && _cycle.Count > 0) CycleIndex = (CycleIndex + 1) % _cycle.Count;
        }
        public FlowPlannerSnapshot Capture() => new FlowPlannerSnapshot {
            RandomState = Random.State, Visits = new Dictionary<string, int>(_visits), Cycle = _cycle.ToArray(), Loop = _loop, CycleIndex = CycleIndex };
        public void Restore(FlowPlannerSnapshot snapshot)
        {
            Random.State = snapshot.RandomState;
            _visits.Clear(); foreach (var pair in snapshot.Visits) _visits.Add(pair.Key, pair.Value);
            _cycle.Clear(); _cycle.AddRange(snapshot.Cycle); _loop = snapshot.Loop; CycleIndex = snapshot.CycleIndex;
        }
        public string RouteKey(int chaos)
        {
            if (_config.EnableSpecialRoutes && chaos == _config.ExceptionalChaos) return "special";
            if (chaos < _config.MinimumChaos) return "negative";
            if (chaos >= _config.MaximumChaos) return "final";
            foreach (LoopRuleDefinition rule in _config.Loops) if (rule.Supports(chaos)) return "loop:" + rule.Id;
            return "normal";
        }
        private RoomVariantDefinition Accept(RoomVariantDefinition variant, int chaos, Predicate<RoomVariantDefinition> compatible)
        {
            if (variant == null || variant.Room == null || !variant.Supports(chaos)) return Fail("没有符合当前混乱度的差分");
            return compatible == null || compatible(variant) ? variant : Fail("计划房间没有合法的空间连接");
        }
        private RoomVariantDefinition Draw(RoomPoolDefinition[] pools, int chaos, Predicate<RoomVariantDefinition> compatible)
        {
            var choices = new List<RoomPoolEntry>();
            var logs = new List<double>();
            double maximum = double.NegativeInfinity;
            foreach (RoomPoolDefinition pool in pools)
            {
                if (pool == null) continue;
                foreach (RoomPoolEntry entry in pool.Entries)
                {
                    if (entry == null || !entry.Enabled || entry.Weight <= 0f || float.IsNaN(entry.Weight) || float.IsInfinity(entry.Weight) ||
                        entry.Variant == null || entry.Variant.Room == null || !entry.Variant.Supports(chaos) ||
                        (compatible != null && !compatible(entry.Variant))) continue;
                    int count = VisitCount(entry.Variant);
                    double log = Math.Log(entry.Weight) + (_config.DecayOnlyOnce ? Math.Min(1, count) : count) * Math.Log(_config.VisitWeightMultiplier);
                    choices.Add(entry); logs.Add(log); maximum = Math.Max(maximum, log);
                }
            }
            if (choices.Count == 0) return Fail("地图池没有符合混乱度和连接条件的条目");
            double total = 0;
            foreach (double log in logs) total += Math.Exp(log - maximum);
            double roll = Random.NextUnit() * total;
            for (int i = 0; i < choices.Count; i++)
            { roll -= Math.Exp(logs[i] - maximum); if (roll < 0) return choices[i].Variant; }
            return choices[choices.Count - 1].Variant;
        }
        private void ClearLoop() { _loop = null; _cycle.Clear(); CycleIndex = 0; }
        private RoomVariantDefinition Fail(string message) { LastError = message; return null; }
    }
}
