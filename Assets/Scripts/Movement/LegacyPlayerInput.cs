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

    // Shift 被奔跑共用，故“保持 Shift 但松开 W”也视为滑铲意图（策划案第 3 节）
    public bool SlideTrigger => RunPressed || (RunHeld && !Input.GetKey(KeyCode.W));
}
