# 动态动作音频接口规范

本文档定义动作代码与动态音频系统之间的稳定边界。动作代码只负责判断“当前是什么动作、处于什么阶段、需要怎样停止”，不直接操作 Wwise、PCM、Emitter 或音频资产。

## 1. 职责边界

动作代码负责：

- 判断动作开始、持续、停止和取消。
- 上报稳定的 `ActionId`。
- 提供速度、接触强度、方向和表面等控制参数。
- 在特殊情况下指定停止模式。

普通一次性动作不实现本接口，应使用 `WwiseEventBridge`：

```csharp
WwiseEventBridge.Play("player_land", gameObject);
WwiseEventBridge.Play("hit_metal", gameObject);
```

本接口只用于需要 `Start / Update / Stop` 动态生命周期的连续动作。

音频系统负责：

- 根据 `ActionId` 查找 `WwiseActionBindings`。
- 管理动态动作 channel、voice 和 emitter。
- 渲染 ADSR、循环、颗粒、采样区间和 PCM。
- 把 PCM 交给 Wwise。
- 把控制参数转成 Wwise RTPC、Switch 或 Bus 参数。

## 2. 核心接口

```csharp
public interface IDynamicAudioActionSource
{
    void CollectDynamicAudioActions(
        List<DynamicAudioActionRequest> output);
}
```

一个 `MonoBehaviour` 可以同时上报多个动作。例如同一个玩家组件可以同时上报 `player_slide` 和 `wall_slide`，但同一帧每个动作应只有一个最终状态。

接口在主线程调用。不要在这里解析音频文件、创建 Wwise 对象或执行重 DSP。

动作源必须在启用时显式注册：

```csharp
private void OnEnable()
{
    WwiseAudioRegistry.Register(this);
}

private void OnDisable()
{
    WwiseAudioRegistry.Unregister(this);
}
```

`WwiseActionDriver` 只在 registry version 变化时刷新 source 列表，并且只消费与
当前 Player 同一 GameObject 或子物体上的 source。这样支持多个 Player 和运行时动态添加的动作源。

## 3. 请求结构

```csharp
public enum DynamicAudioActionPhase
{
    Start,
    Update,
    Stop
}

public enum DynamicAudioActionStopMode
{
    Release,
    Immediate,
    FinishCurrentLoop,
    PlayFullOnce
}

public struct DynamicAudioActionRequest
{
    public string ActionId;
    public int InstanceId;
    public DynamicAudioActionPhase Phase;
    public DynamicAudioActionStopMode StopMode;
    public float NormalizedSpeed;
    public float ContactIntensity;
    public float Direction;
    public string SurfaceId;
    public int Seed;
}
```

推荐使用静态工厂：

```csharp
DynamicAudioActionRequest.Start(...)
DynamicAudioActionRequest.Update(...)
DynamicAudioActionRequest.Stop(...)
```

## 4. 字段语义

| 字段 | 范围 | 说明 |
| --- | --- | --- |
| `ActionId` | 非空字符串 | 稳定逻辑 ID，例如 `player_slide`、`wall_slide` |
| `InstanceId` | 0 或唯一整数 | 0 表示同一 ActionId 单实例；非零用于并发或快速重触发 |
| `Phase` | 枚举 | 开始、更新或请求停止 |
| `StopMode` | 枚举 | 仅 `Phase == Stop` 时有意义 |
| `NormalizedSpeed` | `0..1` | 动作速度，用于 Wwise RTPC |
| `ContactIntensity` | `0..1` | 接触强度、贴墙程度等 |
| `Direction` | `-1..1` | 左右方向，`-1` 左，`1` 右 |
| `SurfaceId` | 字符串 | 材质或表面逻辑 ID，不在音频系统里枚举材质 |
| `Seed` | 整数 | `0` 表示随机；非零用于可复现测试 |

`ActionId` 不是音频文件名，也不是 `AudioClip.name`。它是动作代码和音频绑定之间的稳定协议。

## 5. ActionId 注册

每个动态动作必须在 `WwiseActionBindings` 中注册：

```text
ActionId: player_slide
Definition: PlayerSlideAudio AudioActionDefinition
PlayEvent: Play_SlideSinePlugin
SourceRelativePath: Audio/Source/slide_tackle_2.wav
```

动作代码只上报：

```csharp
DynamicAudioActionRequest.Start("player_slide");
```

音频系统通过 `WwiseActionBindings.Find("player_slide")` 找到定义、事件和源文件。

## 5.1 编辑器绑定流程

打开：

```text
超高速行者/音频/Wwise 动作绑定管理器
```

也可以直接选中 `AudioActionDefinition`，在 Inspector 的
`Wwise 绑定 / Event / 源 WAV` 按钮打开。

操作：

1. 把一个 `AudioActionDefinition` 拖入窗口。
2. 填写 `ActionId`。
3. 填写 `Play Event` 和可选的 `Stop Event`。
4. 点击“从 Clip 自动生成源 WAV”。
5. 点击“创建 / 更新绑定”。

工具会：

- 把源 WAV 复制到 `Assets/StreamingAssets/Audio/Source/Generated/`。
- 创建或更新 `WwiseActionBindings.Entry`。
- 检查 `ActionId` 是否与其他定义冲突。
- 把绑定写回 `Assets/Resources/Audio/WwiseActionBindings.asset`。

`WwiseActionBindingSetup` 的旧菜单现在只负责打开这个通用窗口。

## 6. 生命周期规则

正常持续动作：

```text
第一帧：
Start

持续期间：
Update
Update
Update

松手：
Stop(Release)
```

最短合法序列：

```text
Start
Stop(Release)
```

`Update` 应该逐帧发送，或者至少在控制参数变化时发送。音频系统会缓存最后一次控制值。

不要每帧重复发送 `Start`。`Start` 表示一次新的触发，会强制重启同实例 channel。

同一个 `ActionId` 需要同时存在多个实例时，为每个触发分配不同的 `InstanceId`。快速重复触发也推荐使用新的 `InstanceId`，避免旧 Release 尾音占用新动作的 channel。

同一 `ActionId + InstanceId` 收到新的 `Start` 时，音频系统会立即释放旧实例并重新启动，不会等待旧 Release 播完。

## 7. 停止模式

```text
Release
```

按动作定义进入正常 Release 流程。连续动作默认使用这个模式。

```text
Immediate
```

立即停止。适用于动作被硬取消、角色死亡、传送、场景卸载等情况。

```text
FinishCurrentLoop
```

保持段等待当前这一遍循环走完，再进入 Release。适用于“松手后不要切在循环中间”的效果。

```text
PlayFullOnce
```

不循环，从当前位置继续完整播放到尾音结束。适用于启动段被打断或需要完整播放一遍的特殊动作。

## 8. 示例

动作代码实现：

```csharp
public sealed class MyActionAudioSource :
    MonoBehaviour,
    IDynamicAudioActionSource
{
    private readonly List<DynamicAudioActionRequest> _pending =
        new List<DynamicAudioActionRequest>();

    public void CollectDynamicAudioActions(
        List<DynamicAudioActionRequest> output)
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            output.Add(_pending[i]);
        }
        _pending.Clear();
    }
}
```

当前工程已有的过渡适配器是：

```text
Assets/Scripts/Audio/PlayerDynamicAudioActionSource.cs
```

该组件不再自动挂载。需要手动添加到 Player 对象，并确认 Player 对象上存在
`PlayerMotor` 和 `WwiseActionDriver`。

它使用规则表把动作条件 key 映射到 `ActionId`：

```text
player_slide <- sliding
wall_slide   <- wall_sliding
```

规则表位于组件的 Inspector 中，新增条件映射不需要改音频驱动。动作条件 key
由现有 `PlayerActionRunner` 统一判断；没有 ActionRunner 时会退回 Motor 的
基础状态判断。

## 9. 接入检查表

新增一个动态动作时需要确认：

- 动作代码能稳定提供唯一的 `ActionId`。
- 开始和停止沿只发送一次。
- 持续阶段至少提供 `NormalizedSpeed` 和 `ContactIntensity`。
- 特殊取消使用正确的 `DynamicAudioActionStopMode`。
- `WwiseActionBindings` 中存在对应 `ActionId`。
- Wwise Event、Bus、RTPC 和 Switch 已经配置。
- 动作结束时没有遗留 channel、voice 或 emitter。

## 10. Wwise 参数边界

Unity 只上报控制值，不在 PCM 渲染器里实现动作音色曲线。

推荐映射：

```text
NormalizedSpeed -> ActionSpeed RTPC -> Pitch / Lowpass / Volume
ContactIntensity -> Contact RTPC -> Volume / Effect
Direction -> Direction RTPC -> Pan
SurfaceId -> Surface Switch
```

音高、低通、EQ、压缩、失真和特殊效果统一在 Wwise Bus、Actor-Mixer 或 Effect 中处理。
