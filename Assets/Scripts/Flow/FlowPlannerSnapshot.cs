using System.Collections.Generic;

namespace GameJam.Flow
{
    public sealed class FlowPlannerSnapshot
    {
        internal uint RandomState;
        internal Dictionary<string, int> Visits;
        internal RoomVariantDefinition[] Cycle;
        internal LoopRuleDefinition Loop;
        internal int CycleIndex;
    }
}
