# Movement 代码简述

本文按当前代码整理，入口在 `Assets/Scripts/Movement/`。阅读顺序建议：`MovementParams` → `IPlayerInput` / `LegacyPlayerInput` → `PlayerMotor`，最后看备选的 `CharacterMovement`。

**先注意场景配置：两套移动实现必须二选一。仓库约定默认使用 `PlayerMotor`，但当前 `TestScene.unity` 中两者的 `m_Enabled` 都是 1，会共同驱动同一个 `CharacterController`。** 使用主方案时应关闭 `CharacterMovement`；本文仅记录现状，未修改场景。

## 1. 文件分工

| 文件 | 作用 |
| --- | --- |
| [PlayerMotor.cs](../Assets/Scripts/Movement/PlayerMotor.cs) | 主移动逻辑：地面加速、兔子跳、转向、滑铲、蹬墙、胶囊变形、能量积累 |
| [MovementParams.cs](../Assets/Scripts/Movement/MovementParams.cs) | 主方案的 ScriptableObject 参数定义，包含 `PumpMode` 枚举 |
| [IPlayerInput.cs](../Assets/Scripts/Movement/IPlayerInput.cs) | 输入接口，允许测试或其他输入源注入 |
| [LegacyPlayerInput.cs](../Assets/Scripts/Movement/LegacyPlayerInput.cs) | 旧版 Input Manager 的键盘输入实现 |
| [CharacterMovement.cs](../Assets/Scripts/Movement/CharacterMovement.cs) | 独立备选方案：相机相对移动、冲刺、兔子跳；参数直接放在组件 Inspector |
| [PlayerRespawn.cs](../Assets/Scripts/Movement/PlayerRespawn.cs) | 低于 `_killY`（默认 -10）回到初始位置，并调用 `PlayerMotor.ResetState()` |
| [CapsuleVisualSync.cs](../Assets/Scripts/Movement/CapsuleVisualSync.cs) | 在 `LateUpdate` 将可见胶囊的位置、尺寸同步到碰撞胶囊 |

两套控制器都自己维护速度，再调用 `CharacterController.Move` 移动，不使用刚体力。

## 2. 主方案每帧怎么跑

`Awake` 获取控制器、初始化输入；没有绑定参数资产时会报错并停用。`Update` 获取主相机，并缓存跳跃按下沿，避免固定帧漏掉按键。

`FixedUpdate` 按下面顺序结算：

```text
读取 TimeManager.PlayerDeltaTime，推进内部玩家时钟
  → 接收跳跃缓存，计算相机水平朝向下的输入方向
  → 判断是否进入滑铲
  → 滑铲中：减速、尝试起身或跳出
    否则：地面摩擦与加速 / 空中加速，再处理跳跃和蹬墙
  → 高速转向 → 限制水平速度 → 重力 → 更新胶囊尺寸
  → CharacterController.Move
  → 更新落地状态，首次落地开启免摩擦窗口
  → 按超出地速阈值的水平速度累加能量
```

移动前用的是上一轮的着地状态；本轮 `Move` 后确认落地，后续 tick 才使用新开的窗口。

## 3. 主要机制怎么实现

| 机制 | 方法 / 实际行为 |
| --- | --- |
| 行走、奔跑 | `ComputeWishDir` 按相机 yaw 转换输入方向；左 Shift 决定使用奔跑还是行走目标速度，主方案没有限定必须同时按 W |
| 地面摩擦 | `ApplyGroundFriction` 仅在速度高于当前行走/奔跑门槛、且不在免摩擦窗口时减速；没有单独的松键刹停逻辑，因此低速松键仍可能滑行 |
| 兔子跳加速 | `ApplyGroundAcceleration` 在 `WindowPump` 模式的落地窗口内取消加速投影上限；地面先加速再起跳，跳跃保留奔跑水平动量 |
| 跳跃预输入 | `HandleJump` 消费最近一次 Space 按下，缓冲默认 0.12 秒；不是按住 Space 自动连跳。非奔跑且有方向输入时，水平速度会被设为该方向的行走速度 |
| 空中移动 | `ApplyAirAcceleration` 默认关闭（`AirControl = 0`），但高速转向仍然可以生效 |
| 高速转向 | `ApplySpeedSteering` 在有前向输入、速度达到门槛且未滑铲时，以受限角速度转向相机水平前方，保持水平速度大小 |
| 滑铲 | 达到速度门槛且在地面时，按左 Shift，或保持左 Shift 且未按 W，均可触发；`UpdateSlide` 线性减速，松 Shift、低速或跳跃可尝试退出，头顶受阻则保持低姿态 |
| 蹬墙 | `OnControllerColliderHit` 记录空中碰撞法线；`HandleWallJump` 检查跳跃缓冲、入墙角度和冷却，使用反射速度加法线冲量，并设置向上速度 |
| 高速缩身 | `UpdateCapsuleBySpeed` 随水平速度缩小控制器高度和半径；滑铲时另用滑铲高度，胶囊底部对齐角色原点 |
| 能量 | 每 tick 增加 `max(0, 水平速度 - 地速阈值) × 系数`，到 `EnergyMax` 封顶；这部分代码只有积累，没有技能消费 |

兔子跳的关键区别是：普通加速受 `目标速度 - dot(水平速度, 输入方向)` 限制；窗口泵油直接增加 `输入方向 × RunAccel × 目标速度 × dt`。因此默认模式能通过反复落地、起跳涨速。切成 `VerbatimQuake` 后，直线跑跳在该模型下主要保速，无法靠窗口继续突破目标速度。

## 4. 调参看哪里

主方案修改 [Assets/Settings/MovementParams.asset](../Assets/Settings/MovementParams.asset)，并确认它绑定到 `PlayerMotor._params`。以下是当前资产值；转向两项尚未写入该资产，表中列的是代码初始化值，应在 Inspector 核对。

| 想调整的手感 | 参数与当前值 |
| --- | --- |
| 行走、奔跑基准 | `WalkSpeed = 3`，`GroundSpeedThreshold = 10` |
| 加速、超速衰减 | `RunAccel = 10`，`GroundFriction = 6` |
| 兔子跳宽容度 | `Pump = WindowPump`，`FrictionExemptWindow = 0.2s`，`JumpBufferWindow = 0.12s` |
| 跳高、滞空 | `JumpSpeed = 8`，`Gravity = 20`；空中加速 `AirControl = 0` |
| 高速转弯 | `SpeedSteerTurnRate = 540°/s`，`SteerMinSpeedRatio = 0.5`（代码初始化值） |
| 最大速度 | `MaxSpeed = 120`；实现是直接钳制水平速度，虽然注释称为“软上限” |
| 滑铲 | `SlideSpeedRatio = 0.8`（当前入铲门槛为 8），`SlideDecel = 15`，`SlideEndSpeed = 2`，`SlideCapsuleHeight = 1` |
| 蹬墙 | `WallJumpMinAngle = 30°`，`WallJumpReflectRatio = 0.8`，法线/向上冲量分别为 3 / 8，冷却 0.3 秒 |
| 胶囊变形 | 速度 10～25 时，高度 2 → 1.7，半径 0.5 → 0.3；由各 `Capsule*` 参数独立控制 |
| 能量积累 | `EnergyMax = 200`，`EnergyPerTickPerExcessSpeed = 1`；按 tick 累加，不乘 dt |

## 5. 备选方案有什么不同

`CharacterMovement` 不读取 `MovementParams`，也不经过 `IPlayerInput`。它在 `Update` 采样输入，再由公开方法 `Simulate` 用累积器按 1/60 秒执行 `Tick`，每帧最多接纳 0.1 秒时间。

它要求 **Shift + 前向输入** 才算冲刺，左右 Shift 都支持。地面落地窗口可以继续加速，空中冲刺也会持续加速（默认系数 8），有输入时直接更新移动方向；没有主方案的滑铲、蹬墙、能量和高速缩身，也没有最大速度钳制。碰撞方面，它会分段移动、撞顶清掉向上速度、撞墙去掉朝墙内的水平分量。

切换实现时还要检查外围依赖：`DebugHUD` 读取 `PlayerMotor`；`PlayerRespawn` 只重置 `PlayerMotor`；`CameraController` 的冲刺特效订阅的是 `CharacterMovement.SprintChanged`，尚未接到主方案。仅切换组件开关不会自动切换这些数据源。

## 6. 阅读和验证时注意

- **时间步长存在接线差异**：`PlayerMotor` 在 `FixedUpdate` 使用 `TimeManager.PlayerDeltaTime`，但有 `TimeManager` 实例时，该值在 `Update` 中按渲染帧计算。不能仅凭 `FixedUpdate` 就认定当前 dt 严格为 1/60；备选方案则使用 `Time.deltaTime` 和自己的固定步长，未接玩家分层时间。
- **滑铲触发不完全是按下沿**：保持左 Shift 且没有按 W 会持续返回 true；Shift 按下沿也没有像跳跃那样在 `Update` 缓存。
- **主方案报告的是内部速度**：`HorizontalSpeed` 不是实际位移测速；普通撞墙时没有像备选方案那样移除朝墙内的速度分量。
- **输入缓冲是分开的**：跳跃缓冲直接写在 `PlayerMotor` 内，`Core/InputBuffer.cs` 的战斗组合键缓冲没有接入这条移动流程。

现有 [MovementBhopTests.cs](../Assets/Tests/PlayMode/MovementBhopTests.cs) 有三个 PlayMode 测试，覆盖窗口泵油增长、字面 Quake 模型不增长，以及速度上限和能量饱和；没有覆盖完整的滑铲、蹬墙或两套组件切换。测试通过 `SetInput` / `SetParams` 在角色激活前注入输入和参数。

纯数学对照在 [Tools/movement-sim](../Tools/movement-sim)，仓库根目录可执行 `dotnet run --project Tools/movement-sim`；已有结果见 [report.md](../Tools/movement-sim/results/report.md)。部分源码注释还保留旧的 `demo/movement-sim` 路径，应以 `Tools/` 下实际目录为准。

本文基于源码、参数资产和场景序列化文件静态核对，本次未运行 Unity 或重新执行测试。
