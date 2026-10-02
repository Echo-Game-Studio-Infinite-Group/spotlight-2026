# Movement 代码简述

移动已统一为一套 `PlayerMotor`，使用 Input System 1.7.0 的 Action Map 输入和 Cinemachine 2.10.7 相机。Unity 版本保持 2022.3.33f1c1。

## 操作与入口

打开 `Assets/Scenes/TestScene.unity` 播放。WASD 移动、Shift + W 奔跑、Ctrl 滑铲、Space 跳跃/蹬墙、鼠标转视角、Esc 切换鼠标锁定与 Gameplay 输入、F3 切换 HUD。跳跃按下只执行一次，不是按住自动连跳。

- 改键位：[PlayerControls.inputactions](../Assets/Input/PlayerControls.inputactions)。Gameplay 包含移动、视角、奔跑、跳跃、滑铲，以及战斗预留动作；Debug 包含面板和鼠标锁定开关。
- 调移动手感：[MovementParams.asset](../Assets/Settings/MovementParams.asset)。
- 修复场景接线：菜单「超高速行者 → 装配 character 与 Cinemachine」。装配会保存测试场景。

## 代码分工

| 文件 | 职责 |
| --- | --- |
| [PlayerInputReader.cs](../Assets/Scripts/Movement/PlayerInputReader.cs) | 实例化 Action Asset、缓存连续输入和按下沿、管理 Gameplay/Debug；动作引用初始化时缓存 |
| [PlayerInputFrame.cs](../Assets/Scripts/Movement/PlayerInputFrame.cs) / [IPlayerInput.cs](../Assets/Scripts/Movement/IPlayerInput.cs) | Motor 的输入快照与可替换输入接口，测试无需模拟键盘即可驱动移动 |
| [PlayerMotor.cs](../Assets/Scripts/Movement/PlayerMotor.cs) | 状态转换、地面/空中/划墙结算、滑铲姿态、胶囊尺寸、能量、重生复位 |
| [MovementMath.cs](../Assets/Scripts/Movement/MovementMath.cs) | 加速、碰撞裁剪、入墙角、墙面切线和蹬出速度公式 |
| [MovementContacts.cs](../Assets/Scripts/Movement/MovementContacts.cs) | 分段 CharacterController.Move、墙面碰撞采集与短距离探测、起身空间检测 |
| [MovementParams.cs](../Assets/Scripts/Movement/MovementParams.cs) | ScriptableObject 参数定义；[PumpMode.cs](../Assets/Scripts/Movement/PumpMode.cs) 保留泵油/字面 Quake 对照 |
| [TimeManager.cs](../Assets/Scripts/Core/TimeManager.cs) | 分离渲染帧时间、玩家固定步长和未缩放时间 |
| [PlayerCameraRig.cs](../Assets/Scripts/Camera/PlayerCameraRig.cs) | 根据 Look 旋转 CameraTarget，处理滑铲目标高度和重生镜头通知 |
| [SpeedCameraFeedback.cs](../Assets/Scripts/Camera/SpeedCameraFeedback.cs) | 速度 FOV、划墙倾斜、URP 与自研速度后处理 |
| [PlayerRespawn.cs](../Assets/Scripts/Movement/PlayerRespawn.cs) | 掉落后调用 Motor.Teleport，重置运动并通知镜头 |

旧 `CharacterMovement`、`LegacyPlayerInput`、手写跟随的 `CameraController`、灰盒 `CapsuleVisualSync` 和旧相机探针均已移除。运动、相机输入和相机反馈直接挂在 `Assets/Prefabs/character.prefab` 根节点；场景仅保留一个 character 控制实例。

## 移动结算

状态只有 `Grounded`、`Airborne`、`WallSlide`。滑铲是独立姿态，允许滑出平台后维持低姿态。

```text
FixedUpdate：读取输入快照及 PlayerFixedDeltaTime
  → 检查接触有效性、处理状态转换
  → 地面加速/摩擦、空中控制、划墙衰减或滑铲
  → 消费跳跃/蹬墙请求
  → 高速转向、水平速度上限、重力、胶囊尺寸
  → 分段 Move，裁剪撞墙分量，撞顶清掉上升速度
  → 落地开窗/合格入墙开窗
  → 能量按玩家时间秒累计
```

固定步长由 `TimeManager.PlayerFixedDeltaTime` 提供，等于固定帧间隔 × PlayerScale × HitStopScale。输入预缓冲走未缩放时间；落地/墙面窗口和冷却走玩家逻辑时间。玩家时间为 0 时不移动、不增加能量。

`Simulate(frame, dt, inputTime)` 支持直接注入模拟；`SetInput` 和 `SetParams` 支持激活前配置。重生统一调用 `Teleport`，同时清空输入、速度、能量、窗口、墙面资格和计数。

## 地面与滑铲

奔跑方向按最终渲染相机的 yaw 计算。地面普通加速受速度投影上限限制；`WindowPump` 模式在落地后的 0.2 秒窗口内免摩擦并允许突破投影上限，因此反复跑跳能增长速度。`VerbatimQuake` 仅作对照。

按前进时，高速运动会保留水平速度大小、逐渐转向相机朝向；蹬墙后短暂禁止这项转向，保证能够离墙。空中额外加速默认关闭。保留原地面摩擦门槛，因此低速松开移动键仍可能滑行。

滑铲默认需速度达到 8，Ctrl 按下进入，速度线性衰减；松开 Ctrl、速度过低或按跳可尝试起身。头顶空间不足时保持低姿态；起身成功且着地时才能跳跃。高速胶囊恢复尺寸也先检查空间。

## 划墙与墙面 bhop

夹角取**碰撞修正前的水平速度与墙面**：0° 平行，90° 正撞。只有朝墙运动、空中碰到近竖直面且角度 ≥30°，才进入划墙；数值边界允许 0.01° 法线误差。地板、天花板、普通坡面不会授予资格。

进入后将入墙水平动量转为沿墙方向，保持速度大小，避免直接投影造成大角度撞墙瞬间丢速。正撞且无沿墙输入时只下滑，短窗口内仍可利用入墙动量反向蹬出；窗口过期后不会继续储存这份动量。

| 参数 | 当前基线 |
| --- | --- |
| WallMinApproachAngle | 30° |
| WallGraceTime | 0.15 秒，免衰减及蹬墙增速窗口 |
| WallFriction | 3/秒，窗口后速度乘 exp(-摩擦 × dt) |
| WallGravityScale / WallMaxFallSpeed | 0.4 / 6 |
| WallJumpBoost | 窗口内成功蹬出一次 +1.5 水平速度 |
| WallJumpAngle / WallJumpUpImpulse | 相对墙面 45° / 向上速度 8 |
| WallJumpCooldown / WallRearmDistance | 0.2 秒 / 离墙 0.15 |
| MaxSpeed | 120，直接钳制水平速度 |

窗口内蹬墙保速并加速；窗口外仍能蹬出，但使用已衰减速度且无奖励。每次连续接触只开启一次窗口。接缝或相邻共面碰撞体不会刷新窗口；离墙后再以合格角度接触，可以继续 bhop。同墙需要真实分离及冷却；每 tick 最多跳一次。

当前能量按实际 Motor 水平速度超出地速阈值的部分累加，默认系数 60/秒，上限 200；墙面缓存的正撞动量不产生能量。技能消费尚未实现。

## Cinemachine 场景结构

```text
character（Assets/Prefabs/character.prefab 的实例）
  根组件：CharacterController、PlayerInputReader、PlayerMotor、HUD、Respawn
          PlayerCameraRig、SpeedCameraFeedback
  原模型/骨骼/动画：保留，Animator Root Motion 关闭
  CameraTarget：独立旋转，滑铲时降低高度
  CharacterCamera：CinemachineVirtualCamera + 3rdPersonFollow
Main Camera：CinemachineBrain、URP 相机数据
Global Volume：共享速度后处理
TimeManager
```

Brain 使用 LateUpdate 并忽略全局时间缩放。3rdPersonFollow 处理距离、阻尼和碰撞避障；忽略 Player 标签，避免角色挡住自身镜头。自有组件不再写 Main Camera 的 Transform 或 FOV。

## 验证入口

Unity Test Runner → PlayMode。测试覆盖原有三项地面 bhop、墙面角度和衰减、一次性奖励/重新接触、正撞、撞顶、限高滑铲、输入短按与 Map 开关、玩家时间冻结以及 Cinemachine 场景跟随。

自动化脚本：[validate.ps1](../Tools/MovementRewrite/validate.ps1)。`-Stage Setup` 在 `Temp/MovementRewriteValidation` 副本装配场景；`-Stage Tests` 在副本运行 PlayMode。日志和 `PlayMode.xml` 同样位于该目录，不关闭正在使用的工程编辑器。该脚本不会自动回写副本场景。

本次独立副本验证：Unity 编译通过，27 项 PlayMode 测试全部通过。自动测试验证行为和接线；实际镜头观感与手感仍需在 Game 窗口调参。

纯数学地面模型仍在 [Tools/movement-sim](../Tools/movement-sim)，墙面行为以本次 Unity 碰撞测试为准。
