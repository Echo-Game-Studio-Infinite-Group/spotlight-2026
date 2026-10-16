namespace GameJam.Actions
{
    // 可选接口保留纯通知接入；物理和骨骼采样须覆盖每段实际占用的时间。
    public interface IActionSequenceSimulationSink
    {
        void OnSimulationStep(ActionExecutionState from, ActionExecutionState to, double frames);
        void OnFrameBoundary(ActionExecutionState state);
        void OnIdleSimulation(double frames);
    }
}
