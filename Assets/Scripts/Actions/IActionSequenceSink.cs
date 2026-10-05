namespace GameJam.Actions
{
    public interface IActionSequenceSink
    {
        void OnActionStarted(ActionExecutionState state);
        void OnSegmentEntered(ActionExecutionState state);
        void OnFrameEvent(ActionExecutionState state, ActionFrameEvent frameEvent);
        void OnActionEnded(ActionExecutionState state, ActionExitReason reason);
        // 动画和移动通过只读状态接入，退出回调负责释放旧实例的判定与限制。
        void OnStateSampled(ActionExecutionState state);
    }
}
