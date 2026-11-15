# Parry 功能交接文档

## 1. 任务目标

从外部策划案 `C:/Users/94880/Downloads/策划案v1.11.md` 提取 Parry 需求，检查 Unity 现有战斗实现，并完成以下主链路：

```text
玩家 Parry 输入
    -> PlayerInputReader
    -> InputBuffer.Action.Parry
    -> PlayerCombat
    -> CombatComponent
    -> 受击时 IParryReceiver
```

本文档只记录 Parry 相关内容，不替代策划案，也不修改策划案原文。

## 2. 策划案需求摘要

### 普通 Parry

- 敌人攻击从前摇开始，到收招结束，属于可 Parry 判定窗口。
- 普通 Parry 使用蓝色或黄色提示。
- 普通 Parry 成功后，玩家获得约 `0.8 秒` 无敌。
- 普通 Parry 成功后触发全局时缓。
- 普通攻击可以互相取消，用于连续 Parry。

### 飞行道具 Parry

- 远程子弹的实际伤害范围小于 Parry 判定范围。
- 飞行道具 Parry 不触发额外时缓。
- 远程 Parry 可以使用独立的 Parry 特效。

### 劣势 Parry

- 使用红色提示。
- 不触发时缓。
- 玩家速度清零并小幅后退。
- 可以中断敌人的攻击。

### 其他战斗约束

- 连斩自身没有 Parry 判定，但中途允许普通攻击取消。
- 大威力斩击的后摇不可取消。
- 轻型敌人死亡后，应失去碰撞、攻击和 Parry 判定。
- 推斩暂不实现。

## 3. 当前已完成改动

### `Assets/Scripts/Components/AttackDamageRequest.cs`

- 新增 `AttackSourceType`：`Melee`、`Projectile`。
- `AttackDamageRequest` 新增 `SourceType`。
- 保留旧构造函数，旧调用默认视为近战攻击。

### `Assets/Scripts/Components/IAttackDamageReceiver.cs`

- 新增接口：

```csharp
public interface IParryReceiver
{
    bool TryParry(AttackDamageRequest request);
}
```

### `Assets/Scripts/Components/CombatComponent.cs`

- `TryHit` 在调用 `ReceiveAttack` 前检查 `IParryReceiver.TryParry`。
- Parry 成功时跳过本次伤害以及 `Landed` / `SequenceLanded` 处理。

### `Assets/Scripts/Components/RangeComponent.cs`

- 远程攻击请求标记为 `AttackSourceType.Projectile`。
- 命中前通过父层级查找 `IParryReceiver`。
- Parry 成功时跳过远程伤害。

## 4. 尚未完成的实现任务

### 输入链路

- [ ] `Assets/Scripts/Core/InputBuffer.cs`
  - 增加 `Action.Parry`。
  - 增加 `ConsumeParry(float time)`。
- [ ] `Assets/Scripts/Actions/ActionInputButtons.cs`
  - 增加 `ActionInputButtons.Parry`。
- [ ] `Assets/Input/PlayerControls.inputactions`
  - 增加 Gameplay/Parry Button Action。
  - 增加默认按键绑定，建议先使用 `<Keyboard>/e`。
- [ ] `Assets/Scripts/Movement/PlayerInputReader.cs`
  - 缓存并注册 Parry InputAction。
  - performed 时写入 `ActionInputButtons.Parry`。
  - 非序列输入时推入 `InputBuffer.Action.Parry`。
  - 暴露 `ConsumeParry(float time)`。

### 玩家战斗接收

- [ ] `Assets/Scripts/Player/PlayerCombat.cs`
  - 实现 `IParryReceiver`。
  - 在 `TickAttack` 中优先消费 Parry 输入。
  - Parry 可以取消普通攻击，以支持连续 Parry。
  - 将 Parry Action 转发给 `CombatComponent`。

### `CombatComponent`

- [ ] 增加 Parry 参数：
  - Parry 窗口时长，建议初始值 `0.2 秒`。
  - Parry 成功无敌时长，建议初始值 `0.8 秒`。
  - 近战 Parry 时缓时长与缩放比例。
- [ ] 增加 Parry 窗口状态字段。
- [ ] 增加 `ReceiveAction(InputBuffer.Action action, float now)`。
- [ ] 增加 `TryBeginParry(float now)`。
- [ ] 增加 `IsParrying(float now)`。
- [ ] 实现 `TryParry(AttackDamageRequest request)`：
  - 不在窗口内时返回 `false`。
  - 在窗口内时消耗 Parry 窗口并返回 `true`。
  - 近战攻击触发时缓。
  - 远程攻击不触发时缓。
  - Parry 成功后设置玩家立即无敌。
- [ ] 重置攻击状态时清理 Parry 窗口。

### 生命与时间系统

- [ ] `Assets/Scripts/Components/HealthComponent.cs`
  - 增加“立即设置当前无敌窗口”的接口。
  - 不要只修改默认无敌时长；Parry 需要立即生效。
- [ ] 使用现有 `TimeManager` API 实现时缓，不直接散写 `Time.timeScale`。

## 5. 推荐接口形状

```csharp
public bool ReceiveAction(InputBuffer.Action action, float now)
{
    switch (action)
    {
        case InputBuffer.Action.Attack:
            return TryBeginAttack(now);
        case InputBuffer.Action.Parry:
            return TryBeginParry(now);
        default:
            return false;
    }
}
```

```csharp
public bool TryParry(AttackDamageRequest request)
{
    if (!IsParrying(TimeManager.UnscaledTime))
        return false;

    // 立即无敌；近战才触发时缓。
    _parryWindowEnd = float.NegativeInfinity;
    return true;
}
```

具体字段名称和 `TimeManager` 调用方式需要以当前工程 API 为准，禁止复制外部项目实现。

## 6. 实现顺序

1. 先完成 Input System Action 与 `InputBuffer`。
2. 再完成 `PlayerInputReader` 到 `PlayerCombat` 的转发。
3. 在 `CombatComponent` 增加 Parry 窗口和 `ReceiveAction`。
4. 增加立即无敌与近战/远程时缓差异。
5. 编译 Unity 工程，处理所有 C# 编译错误。
6. 在 TestScene 验证：近战 Parry、远程 Parry、Parry 失败受伤、连续 Parry。
7. 最后再补充红色劣势 Parry、击退、敌人攻击取消和表现层提示。

## 7. 验证清单

- [ ] `PlayerControls.inputactions` 是合法 JSON。
- [ ] Unity 2022.3.33f1c1 无编译错误。
- [ ] 按 E 可以进入 Parry 窗口。
- [ ] Parry 窗口外命中仍正常受伤。
- [ ] 近战命中 Parry 时不掉血并触发时缓。
- [ ] 远程命中 Parry 时不掉血且不触发时缓。
- [ ] Parry 成功后约 `0.8 秒` 内不会再次受伤。
- [ ] 普通攻击可以被 Parry 取消。
- [ ] 没有 Parry 接收者的目标仍按原逻辑受伤。
- [ ] 不修改 `Docs/` 之外的策划原文。

## 8. 当前风险

- 当前仓库只有 Parry 命中入口预留，尚未形成可操作的玩家 Parry 输入链路。
- `HealthComponent.SetInvulnerableTime(float)` 只修改默认值，不能直接用于 Parry 成功后的即时无敌。
- `TimeManager` 的具体时缓方法签名需要在实现时确认。
- 敌人攻击前摇、收招状态和红色劣势 Parry 尚未接入统一攻击状态机。
