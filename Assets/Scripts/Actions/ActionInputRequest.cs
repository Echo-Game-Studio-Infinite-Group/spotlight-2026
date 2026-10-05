using UnityEngine;

namespace GameJam.Actions
{
    public readonly struct ActionInputRequest
    {
        public readonly ActionDefinition Target;
        public readonly long TriggerId;
        public readonly long CreatedTick;
        public readonly long SourceInstanceId;
        public readonly Vector2 Direction;

        public ActionInputRequest(ActionDefinition target, long triggerId, long createdTick,
            long sourceInstanceId = 0, Vector2 direction = default)
        {
            Target = target;
            TriggerId = triggerId;
            CreatedTick = createdTick;
            SourceInstanceId = sourceInstanceId;
            Direction = direction;
        }
    }
}
