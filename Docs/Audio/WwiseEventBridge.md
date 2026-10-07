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

示例：

```text
player_land  -> Play_PlayerLand
player_jump  -> Play_PlayerJump
footstep     -> Play_Footstep
hit_metal    -> Play_MetalHit
draw_sword   -> Play_DrawSword
sword_swing  -> Play_SwordSwing
```

`EventId` 是 gameplay 使用的逻辑 ID。`WwiseEventName` 是 Wwise Event 名称。
`Bank` 可选，填写后会先加载对应 Bank 再 Post Event。

## 2. 播放入口

动作、战斗、移动代码只调用：

```csharp
WwiseEventBridge.Play("player_land", gameObject);
WwiseEventBridge.Play("hit_metal", gameObject);
WwiseEventBridge.Play("draw_sword", gameObject);
```

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
