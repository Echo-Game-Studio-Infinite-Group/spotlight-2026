using UnityEngine;

public static class BuildInfo
{
    // 编辑器和勾选 Development Build 的包保留调试界面。
    public static bool IsDebugBuild() => Debug.isDebugBuild;
}
