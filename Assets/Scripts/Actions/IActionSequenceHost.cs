namespace GameJam.Actions
{
    public interface IActionSequenceHost
    {
        bool CheckCondition(string conditionKey, ActionExecutionState source, ActionInputRequest request);
        bool CanStart(ActionExecutionState source, ActionInputRequest request, out string reason);
        // 接入方必须保证失败无副作用，成功后才能退出原动作；动态耗能也在此统一计算。
        bool TryCommit(ActionExecutionState source, ActionInputRequest request);
    }
}
