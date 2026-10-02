using UnityEngine;

// 生产输入实现：包旧版 Input Manager（Input.GetKey/GetAxisRaw）。
// 按下沿仍由 Motor 在其 Update 中查询本类属性时捕获（属性为即时查询），语义与重构前一致。
public sealed class LegacyPlayerInput : IPlayerInput
{
    public float Horizontal => Input.GetAxisRaw("Horizontal");
    public float Vertical => Input.GetAxisRaw("Vertical");
    public bool RunHeld => Input.GetKey(KeyCode.LeftShift);
    public bool RunPressed => Input.GetKeyDown(KeyCode.LeftShift);
    public bool JumpPressed => Input.GetKeyDown(KeyCode.Space);

    // 下蹲/滑铲归 Ctrl（策划案加工版口径；v1.03 原文用 Shift）——Ctrl 独立于 Shift，
    // 修掉“按住 Shift 松 W 即滑铲”的误触发
    public bool CrouchHeld => Input.GetKey(KeyCode.LeftControl);
    public bool SlideTrigger => Input.GetKeyDown(KeyCode.LeftControl);
}
