# 楼梯、坡面与地形姿态

玩家预制体为 `Assets/Prefabs/Player.prefab`。物理通行只由 `PlayerMotor`、`MovementContacts` 和 `MovementGrounding` 处理；关闭 `PlayerGroundPose` 不影响跨阶和接地。所有移动查询使用玩家的缩放逻辑时间，`dt = 0` 不执行移动或贴地。

## 参数入口

`Assets/Settings/MovementParams.asset`：

| 字段 | 默认值 | 含义 |
| --- | --- | --- |
| MaxStepUp | 0.30 m | 胶囊可跨越的单次台面高差；仍需通过抬升、前移及目标空间检查 |
| MaxStepDown | 0.35 m | 连续地面移动时允许的向下贴地距离；空中下降不使用此范围吸附 |
| MaxGroundAngle | 45° | 可行走坡面上限，同时同步 CharacterController.slopeLimit |
| GroundTolerance | 0.03 m | 近地查询与几何安全余量；不是额外跨阶高度 |
| StepVisualSmoothTime | 0.08 s | 模型和镜头对离散阶差的平滑时间；0 关闭 |

运行时原生 `CharacterController.stepOffset` 置零，由统一跨阶逻辑执行；组件停用时还原预制体值。角色胶囊保持竖直。水平速度仍是速度上限、技能及能量的计算依据，坡面高度跟随不转成起跳冲量。正常地面摩擦和 SlideDecel 继续生效，上楼不额外扣除碰撞速度，下坡不额外增加加速度。

当前白盒 `Stairs (5).prefab` 的原始网格约有 40 级，阶高 0.161 m、踏面 0.632 m。实例缩放会改变实际阶高，超出 MaxStepUp 的楼梯需要调整关卡或参数。

## 支撑与碰撞

球扫查找脚下实体，用局部射线确认真实台面法线；中心、前沿及已确认接触点的射线补充同一 MeshCollider 内相邻台阶的识别。检测考虑当前胶囊半径和 Skin Width，因此高速变细与滑铲继续使用相同规则。Trigger 和自身碰撞体被过滤。

向台阶移动时，即使上一帧短暂离地，只要已回到脚下台面的近地范围，也先恢复支撑再处理跨阶；空中不会吸附前方更高的台面，上升中的跳跃仍保持空中状态。跨阶额外沿实际位移方向检查包含 Skin Width 余量的前沿水平台面，防止原生胶囊先被棱边卡住；坡面继续由原有支撑法线处理。

跨阶先验证抬升、前移和落点空间，再经 CharacterController.Move 执行。成功通过的台阶不生成墙面撞击；无法通过的墙、高台和陡坡仍然阻挡。滑铲前伸保护按连续台面调整查询形状，继续阻止身体前部穿入真实障碍。

下阶只在此前已获得地面支撑、没有主动上升且下一台面处于范围内时执行。连续上下楼不刷新落地免摩擦窗口；起跳、发射、悬崖和真正落地仍走原状态机。

## 表现与 IK

`PlayerGroundPose` 挂在玩家 Animator 同物体，参数在组件 Inspector 中调整：

| 字段 | 默认值 | 含义 |
| --- | --- | --- |
| Max Foot Correction | 0.30 m | 单脚允许的最大地形修正 |
| Max Pelvis Drop | 0.18 m | 骨盆最大下调量 |
| Swing Height | 0.20 m | 移动时抬脚降低约束的高度尺度 |
| Pose Response | 16 /s | 骨盆及滑铲倾斜的响应速度 |
| Max Slide Tilt | 45° | 滑铲身体的最大地形倾角 |
| Slide Ground Clearance | 0.02 m | 滑铲模型贴地的展示余量，不改变碰撞胶囊 |

基础层启用 IK Pass。站立和走跑使用原生 Humanoid 足部目标及有限骨盆补偿；滑铲使用身体倾斜与高度补偿，自定义足部目标权重归零，保留动画原有的 Foot IK 设置。跳跃、划墙、攻击、受伤和布娃娃/起身阶段不强制贴脚。

`ActionAnimatorBridge.Sample` 在手动 Animator.Update 前准备姿态；重复的零时间采样不额外推进平滑。自动播放时由组件 Update 准备。模型平滑跟随玩家逻辑时间，相机高度平滑使用 unscaled 时间。瞬移清空展示偏移。

F3 面板显示当前支撑坡角与本帧阶差；播放时选中玩家可查看支撑点及法线 Gizmo。

## 验证

在独立验证工程中运行，避免关闭正在使用的 Unity 编辑器：

```powershell
& Tools/ActionSequences/validate.ps1 -Stage PlayMode -TestFilter 'TerrainMovementTests;GroundPoseTests;SlideMovementTests;SlideWallCollisionTests;SlideAnimationTests;ActionMotionTests;PlayerRagdollRecoveryTests' -ValidationDirectory Tools/map-flow/obj/Validation
```

用例覆盖合成/真实网格楼梯、6/15/30 m/s、斜向上下阶、慢动作与暂停、坡面、滑铲、低顶、墙体、高台、悬崖、起跳、镜头平滑、IK 手动求值和起身重力。
