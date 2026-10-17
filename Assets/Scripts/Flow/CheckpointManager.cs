namespace GameJam.Flow
{
    public sealed class CheckpointManager
    {
        public FlowRoomInstance Room { get; private set; }
        public string Id { get; private set; }
        public int Priority { get; private set; }
        public bool AllowNegativeChaos { get; private set; }
        public float Health { get; private set; }
        public FlowPlannerSnapshot PlannerState { get; private set; }
        public RoomVariantDefinition NextVariant { get; private set; }
        public int NextChaos { get; private set; }
        public void SavePlan(RoomSelectionPlanner planner, RoomVariantDefinition next, int chaos)
        { PlannerState = planner.Capture(); NextVariant = next; NextChaos = chaos; }
        public bool TryCommit(FlowRoomInstance room, FlowCheckpoint checkpoint, float health)
        {
            if (Room == room && (checkpoint.Id == Id || (checkpoint.Priority < Priority && !checkpoint.AllowReactivation))) return false;
            if (Room != null) Room.IsCheckpointProtected = false;
            Room = room; Id = checkpoint.Id; Priority = checkpoint.Priority;
            AllowNegativeChaos = checkpoint.AllowNegativeChaos; Health = health;
            room.IsCheckpointProtected = true;
            return true;
        }
        public void Clear()
        {
            if (Room != null) Room.IsCheckpointProtected = false;
            Room = null; Id = null; Priority = 0; PlannerState = null; NextVariant = null;
        }
    }
}
