using System.Collections.Generic;
using UnityEngine;
namespace GameJam.Flow
{
    public static class LevelSelection
    {
        private static RunFlowConfig _config;
        private static RoomVariantDefinition _pending;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Clear() { _pending = null; _config = null; }
        public static List<RoomVariantDefinition> GetLevels(RunFlowConfig config)
        {
            var result = new List<RoomVariantDefinition>();
            if (config == null) return result;
            void Add(RoomVariantDefinition v) { if (v != null && v.Room != null && v.Room.Template != null && !result.Contains(v)) result.Add(v); }
            void Pool(RoomPoolDefinition pool)
            {
                if (pool == null) return;
                foreach (var entry in pool.Entries) if (entry != null && entry.Enabled && entry.Weight > 0) Add(entry.Variant);
            }
            Add(config.InitialRoom);
            foreach (var pool in config.NormalPools) Pool(pool);
            foreach (var loop in config.Loops) if (loop != null) { Pool(loop.NeutralPool); Pool(loop.ChaosPool); }
            if (config.EnableSpecialRoutes) { Pool(config.NegativePool); Pool(config.ExceptionalPool); }
            Add(config.FinalRoom); return result;
        }
        public static bool Select(RunFlowConfig config, RoomVariantDefinition variant)
        {
            if (!GetLevels(config).Contains(variant)) return false;
            _config = config; _pending = variant; return true;
        }
        public static RoomVariantDefinition Consume(RunFlowConfig config)
        {
            var result = _config == config ? _pending : null; Clear(); return result;
        }
    }
}
