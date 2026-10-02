using UnityEngine;

// 战斗输入持有器（框架 4.1）：缓冲与按住态快照的唯一持有点，FixedUpdate 由 PlayerCombat 消费
// 数据源 = PlayerInputReader（Input System 回调推按下沿、Update 刷新按住态）——本组件不直读任何设备 API，
// 因此 PlayMode 测试可经 Buffer.Push / InjectSnapshot 无设备注入
// 执行序 -90：战斗(-50)读到的是最近一次 Update 采样的快照
[DefaultExecutionOrder(-90)]
public class InputSampler : MonoBehaviour
{
    [SerializeField] private InputBufferConfig _config = InputBufferConfig.Default;

    [Tooltip("仲裁规则表 SO（留空 = 代码内置默认表；优先级基线待哈士奇确认后落资产）")]
    [SerializeField] private InputArbitrationTable _arbitration;

    public InputBuffer Buffer { get; private set; }

    /// <summary>按住态快照：PlayerInputReader 每 Update 写入；测试用 InjectSnapshot 覆盖</summary>
    public InputSnapshot Snapshot { get; private set; }

    private void Awake()
    {
        Buffer = new InputBuffer(_config, _arbitration);
    }

    /// <summary>清空缓冲：暂停进入/重开复位时由流程层调用（框架 4.1）</summary>
    public void ClearBuffered()
    {
        if (Buffer != null) Buffer.Clear();
    }

    /// <summary>
    /// 注入按住态快照：PlayerInputReader 每 Update 调用；测试绕过设备直接注入。
    /// 本组件无自动采样，注入值不会被覆盖
    /// </summary>
    public void InjectSnapshot(in InputSnapshot snapshot)
    {
        Snapshot = snapshot;
    }
}
