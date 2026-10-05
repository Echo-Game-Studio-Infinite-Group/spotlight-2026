using UnityEditor;

/// <summary>
/// Legacy menu entry kept for muscle memory. The actual binding workflow is
/// now generic and lives in WwiseActionBindingWindow.
/// </summary>
public static class WwiseActionBindingSetup
{
    [MenuItem("超高速行者/音频/创建或更新 Wwise 动作绑定")]
    public static void Ensure()
    {
        WwiseActionBindingWindow.Open();
    }
}
