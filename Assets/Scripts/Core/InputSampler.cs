using UnityEngine;

// 输入采样层（框架 4.1：Update 统一采样、FixedUpdate 消费快照与缓冲）
// 职责边界：只做采样与缓冲持有，不做仲裁决策——PeekIntent/ConsumeFor 由动作层（PlayerCombat）调用
// 执行序 -90：在 TimeManager(-100) 之后、PlayerCombat(-50) 之前完成本帧采样
[DefaultExecutionOrder(-90)]
public class InputSampler : MonoBehaviour
{
    [SerializeField] private InputBufferConfig _config = InputBufferConfig.Default;

    public InputBuffer Buffer { get; private set; }

    /// <summary>最近一次 Update 采样的按住状态（FixedUpdate 读到的可能是"上一渲染帧"的状态，属预期语义）</summary>
    public InputSnapshot Snapshot { get; private set; }

    private void Awake()
    {
        Buffer = new InputBuffer(_config);
    }

    private void Update()
    {
        Snapshot = new InputSnapshot
        {
            Horizontal = Input.GetAxisRaw("Horizontal"),
            Vertical = Input.GetAxisRaw("Vertical"),
            WHeld = Input.GetKey(KeyCode.W),
            SHeld = Input.GetKey(KeyCode.S),
            AHeld = Input.GetKey(KeyCode.A),
            DHeld = Input.GetKey(KeyCode.D),
            ShiftHeld = Input.GetKey(KeyCode.LeftShift),
            CtrlHeld = Input.GetKey(KeyCode.LeftControl),
            QHeld = Input.GetKey(KeyCode.Q),
            Mouse0Held = Input.GetKey(KeyCode.Mouse0),
            Mouse1Held = Input.GetKey(KeyCode.Mouse1),
            JumpHeld = Input.GetKey(KeyCode.Space),
        };

        // 触发键按下沿进缓冲（只在 Update 捕获：FixedUpdate 可能漏检无固定步帧上的按键）
        if (Input.GetKeyDown(KeyCode.Mouse0)) Buffer.Push(KeyCode.Mouse0);
        if (Input.GetKeyDown(KeyCode.Mouse1)) Buffer.Push(KeyCode.Mouse1);
        if (Input.GetKeyDown(KeyCode.Space)) Buffer.Push(KeyCode.Space);
        if (Input.GetKeyDown(KeyCode.Q)) Buffer.Push(KeyCode.Q);
        if (Input.GetKeyDown(KeyCode.LeftShift)) Buffer.Push(KeyCode.LeftShift);
    }

    /// <summary>清空旧输入：暂停进入/重开复位时由流程层调用（框架 4.1）</summary>
    public void ClearBuffered()
    {
        if (Buffer != null) Buffer.Clear();
    }

    /// <summary>
    /// 测试注入快照：绕过 Update 直读引擎输入（PlayMode 测试无法发真实按键）。
    /// 注入后由调用方自行禁用本组件，防止下一帧 Update 用空输入覆盖
    /// </summary>
    public void InjectSnapshot(in InputSnapshot snapshot)
    {
        Snapshot = snapshot;
    }
}
