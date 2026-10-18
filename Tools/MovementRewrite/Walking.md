# 七方向行走与高速转向

地面正常移动时，A、D、W+A、W+D、S、A+S、S+D 在低速下进入七方向行走；W 单独按下继续奔跑。角色朝向镜头的水平前方，位移使用镜头相对方向；斜向归一化，不叠加速度。低速 W+S 抵消前后输入，仍可用 A/D 侧移。

已有速度严格超过 `MovementParams.LowSpeedThreshold`（默认 6 × 1 = 6 m/s；等于时仍属低速）时，原四组输入继续使用现有高速运动：角色转向位移方向，保留速度大小并播放 Running。现有地面摩擦、落地免摩擦窗口和 bhop 保留，不附加行走限速或回收摩擦。速度自然降到低速区间后才切入行走。高速 S、A+S、S+D 保持原刹车、锁朝向和跳跃返还规则；这次 S 持续按住时，即使已刹停也不会变成后退，必须先松开，再在低速重新按下。

`Assets/Settings/MovementParams.asset`：

| 字段 | 初值 | 含义 |
| --- | --- | --- |
| WalkSpeed | 3 m/s | 七个方向共用的独立行走目标速度，Inspector 限制不超过现有低速上限 |
| WalkAcceleration | 12 m/s² | 接近行走目标速度的加减速度 |
| WalkMaxAnimationRate | 2 | 按实际速度调整步频时的最大倍率 |

动画源目录：`Assets/CLazyRunnerActionAnimPack/Animations/P1_CLazyMovement/Mvm_8way_Jog/`。

| 输入 | 片段 | Blend Tree 方向 |
| --- | --- | --- |
| A | CLazy@Mvm_Jog_Front_L.FBX | (-1, 0) |
| D | CLazy@Mvm_Jog_Front_R.FBX | (1, 0) |
| W+A | CLazy@Mvm_Jog_Front_L45.FBX | (-1, 1) 归一化 |
| W+D | CLazy@Mvm_Jog_Front_R45.FBX | (1, 1) 归一化 |
| S | CLazy@Mvm_Jog_Back.FBX | (0, -1) |
| A+S | CLazy@Mvm_Jog_Back_L45.FBX | (-1, -1) 归一化 |
| S+D | CLazy@Mvm_Jog_Back_R45.FBX | (1, -1) 归一化 |

七个源片段均为 Humanoid 循环，30 FPS，长度约 0.933 秒。使用原地版本；Animator 不驱动位移。`Walk` 状态使用二维方向混合树，`WalkX`/`WalkZ` 由角色局部实际水平速度计算，`WalkRate` 由实际速度与 WalkSpeed 的比值计算。

S 仍通过独立的输入绑定进入快照，由 Motor 统一区分低速后退与高速刹车。动作输入复用同一方向解析，低速 S+跳跃使用移动跳跃及后退方向，不会误判为原地跳。低速后退不新增回收速度；前一次高速刹车尚未到期的缓存仍按原规则结算。空中、滑铲、滑铲起身姿态、划墙不进入地面行走，S 在这些状态的行为保留。攻击与受击继续由现有动作和受击状态控制。

菜单「超高速行者/动作序列/接入七方向行走」可增量装配，奔跑和动作装配入口也会检查行走配置。装配会将旧四方向树增量更新为七方向，不重建 Actions、受击或滑墙状态。F3 可观察当前是否处于行走。
