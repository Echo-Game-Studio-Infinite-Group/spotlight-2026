# Wwise 一次性事件桥接规范

`WwiseEventBridge` 负责一次性动作和普通游戏事件。动态连续动作继续使用
`IDynamicAudioActionSource` 和 `WwiseActionDriver`，两条链路不要混用。

## 1. 创建绑定资产

打开菜单：

```text
超高速行者/音频/创建 Wwise 事件绑定资产
```

工具会创建或选中：

```text
Assets/Resources/Audio/WwiseEventBindings.asset
```

资产中的每条 Entry：

```text
EventId
WwiseEventName
Bank
```

当前分支已经实际配置的绑定：

```text
player_jump -> Jump_Normal (Bank: OneTimeAction)
```

后续一次性事件的接入示例：

```text
player_land  -> Play_PlayerLand
footstep     -> Play_Footstep
hit_metal    -> Play_MetalHit
draw_sword   -> Play_DrawSword
sword_swing  -> Play_SwordSwing
```

这些名称不是当前 Wwise 工程中已经全部存在的 Event。需要先在 Wwise Authoring
中创建 Event，再写入 `WwiseEventBindings.asset`。

`EventId` 是 gameplay 使用的逻辑 ID。`WwiseEventName` 是 Wwise Event 名称。
`Bank` 可选，填写后会先加载对应 Bank 再 Post Event。

## 2. 播放入口

动作、战斗、移动代码只调用：

```csharp
WwiseEventBridge.Play("player_jump", gameObject);
WwiseEventBridge.Play("player_land", gameObject);
WwiseEventBridge.Play("hit_metal", gameObject);
WwiseEventBridge.Play("draw_sword", gameObject);
```

动作序列帧事件的 `EventKey` 使用 `audio.` 前缀，例如：

```text
audio.player_jump
```

`PlayerActionRunner` 会去掉 `audio.`，再以 `player_jump` 调用
`WwiseEventBridge.Play(...)`。

需要停止时：

```csharp
WwiseEventBridge.Stop("sword_swing", gameObject);
```

可指定淡出时间：

```csharp
WwiseEventBridge.Stop("sword_swing", gameObject, 80);
```

## 3. Switch 和 RTPC

脚步或材质路由：

```csharp
WwiseEventBridge.SetSwitch("Surface", "grass", gameObject);
WwiseEventBridge.Play("footstep", gameObject);
```

动作参数：

```csharp
WwiseEventBridge.SetRTPC("ActionSpeed", speed01, gameObject);
WwiseEventBridge.SetRTPC("Contact", contact01, gameObject);
```

这里的 RTPC 和 Switch 名称只是调用示例。对应参数必须先在 Wwise Authoring
中创建；`WwiseEventBridge` 只负责发送值，不会自动创建 Wwise 工程对象。

参数映射建议：

```text
ActionSpeed -> Pitch / Lowpass
Contact     -> Volume / Effect
Direction   -> Pan
Surface     -> Switch
```

具体曲线在 Wwise 的 Actor-Mixer、Bus 或 Effect 中配置。

## 4. 与动态动作的区别

一次性动作：

```text
动作代码
-> WwiseEventBridge.Play(EventId)
-> 普通 Wwise Event
```

动态连续动作：

```text
动作代码
-> IDynamicAudioActionSource
-> DynamicAudioActionRequest
-> WwiseActionDriver
-> PCM Source Plugin
```

普通 Hit、Land、Jump、Footstep、DrawSword 不应进入动态 PCM 系统。

## 5. 约定

- `EventId` 使用 `名词_语义` 或动作系统的稳定 ID。
- 不在 gameplay 代码里直接调用 `AkUnitySoundEngine.PostEvent`。
- 不在 gameplay 代码里硬编码 Wwise Event、Switch、RTPC 字符串。
- Wwise Event 替换时只修改 `WwiseEventBindings.asset`。
- 同一逻辑事件需要多个变体时，在 Wwise Event 内使用 Random Container。
