using System.Collections.Generic;
using GameJam.Flow;
using UnityEngine;

namespace GameJam.FlowEditor
{
    public static class FlowConfigAssets
    {
        public static List<Object> Collect(RunFlowConfig config)
        {
            var result = new List<Object>();
            var pending = new Queue<Object>();
            if (config != null) pending.Enqueue(config);
            while (pending.Count > 0)
            {
                Object item = pending.Dequeue();
                if (item == null || result.Contains(item)) continue;
                result.Add(item);
                if (item is RunFlowConfig flow)
                {
                    pending.Enqueue(flow.InitialRoom); pending.Enqueue(flow.FinalRoom);
                    foreach (RoomPoolDefinition pool in flow.NormalPools) pending.Enqueue(pool);
                    foreach (LoopRuleDefinition loop in flow.Loops) pending.Enqueue(loop);
                    if (flow.EnableSpecialRoutes) { pending.Enqueue(flow.NegativePool); pending.Enqueue(flow.ExceptionalPool); }
                }
                else if (item is RoomDefinition room) foreach (RoomVariantDefinition variant in room.Variants) pending.Enqueue(variant);
                else if (item is RoomVariantDefinition variant) pending.Enqueue(variant.Room);
                else if (item is RoomPoolDefinition pool) foreach (RoomPoolEntry entry in pool.Entries) if (entry != null) pending.Enqueue(entry.Variant);
                else if (item is LoopRuleDefinition loop) { pending.Enqueue(loop.NeutralPool); pending.Enqueue(loop.ChaosPool); }
            }
            return result;
        }
    }
}
