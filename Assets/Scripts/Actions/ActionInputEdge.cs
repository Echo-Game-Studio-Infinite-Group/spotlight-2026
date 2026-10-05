using UnityEngine;

namespace GameJam.Actions
{
    public readonly struct ActionInputEdge
    {
        public readonly ActionInputButtons Button;
        public readonly ActionInputStep.Edge Trigger;
        public readonly ActionInputButtons HeldAtEvent;
        public readonly Vector2 DirectionAtEvent;

        public ActionInputEdge(ActionInputButtons button, ActionInputStep.Edge trigger,
            ActionInputButtons heldAtEvent, Vector2 directionAtEvent = default)
        {
            Button = button;
            Trigger = trigger;
            HeldAtEvent = heldAtEvent;
            DirectionAtEvent = directionAtEvent;
        }
    }
}
