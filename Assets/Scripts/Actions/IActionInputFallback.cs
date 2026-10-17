namespace GameJam.Actions
{
    // 只在真实输入没有匹配意图时放宽禁止修饰键；必须按住的组合条件仍读真实输入。
    public interface IActionInputFallback
    {
        ActionInputButtons GetIgnoredForbiddenButtons(ActionInputEdge edge);
    }
}
