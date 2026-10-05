# 动态动作音频接口规范

本文档定义动作代码与动态音频系统之间的稳定边界。动作代码只负责判断“当前是什么动作、处于什么阶段、需要怎样停止”，不直接操作 Wwise、PCM、Emitter 或音频资产。

## 1. 职责边界

动作代码负责：

- 判断动作开始、持续、停止和取消。
- 上报稳定的 `ActionId`。
- 提供速度、接触强度、方向和表面等控制参数。
- 在特殊情况下指定停止模式。

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

一个 `MonoBehaviour` 可以同时上报多个动作。例如同一个玩家组件可以同时上报 `slide` 和 `wall_slide`，但同一帧每个动作应只有一个最终状态。

接口在主线程调用。不要在这里解析音频文件、创建 Wwise 对象或执行重 DSP。

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
| `ActionId` | 非空字符串 | 稳定逻辑 ID，例如 `slide`、`wall_slide` |
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
ActionId: slide
Definition: Slide AudioActionDefinition
PlayEvent: Play_SlideSinePlugin
SourceRelativePath: Audio/Source/slide_tackle_2.wav
```

动作代码只上报：

```csharp
DynamicAudioActionRequest.Start("slide");
```

音频系统通过 `WwiseActionBindings.Find("slide")` 找到定义、事件和源文件。

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

不要每帧重复发送 `Start`。如果 channel 已经存在，`Start` 会被当作一次更新处理，但推荐明确遵守生命周期。

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

它读取现有 `PlayerMotor` 的公开状态，上报：

```text
slide
wall_slide
```

因此移动、战斗、Character、动画和模型代码不需要为了这两个动作新增音频依赖。

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
