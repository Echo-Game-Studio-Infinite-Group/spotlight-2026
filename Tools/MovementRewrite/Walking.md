# 四方向行走与高速转向

地面正常移动时，A、D、W+A、W+D 在低速下进入四方向行走；W 单独按下继续奔跑。角色朝向镜头的水平前方，位移使用镜头的左、右或左右前 45° 方向；斜向归一化，不叠加速度。

已有速度超过 `MovementParams.LowSpeedThreshold`（默认 6 × 0.8 = 4.8 m/s）时，这四组输入继续使用现有高速运动：角色转向位移方向，保留速度大小并播放 Running。现有地面摩擦、落地免摩擦窗口和 bhop 保留，不附加行走限速或回收摩擦。速度自然降到低速区间后才切入行走。

`Assets/Settings/MovementParams.asset`：

| 字段 | 初值 | 含义 |
| --- | --- | --- |
| WalkSpeed | 3 m/s | 四个方向共用的行走目标速度，Inspector 限制不超过现有低速上限 |
| WalkAcceleration | 12 m/s² | 接近行走目标速度的加减速度 |
| WalkMaxAnimationRate | 2 | 按实际速度调整步频时的最大倍率 |

动画源目录：`Assets/CLazyRunnerActionAnimPack/Animations/P1_CLazyMovement/Mvm_8way_Jog/`。

| 输入 | 片段 | Blend Tree 方向 |
| --- | --- | --- |
| A | CLazy@Mvm_Jog_Front_L.FBX | (-1, 0) |
| D | CLazy@Mvm_Jog_Front_R.FBX | (1, 0) |
| W+A | CLazy@Mvm_Jog_Front_L45.FBX | (-1, 1) 归一化 |
| W+D | CLazy@Mvm_Jog_Front_R45.FBX | (1, 1) 归一化 |

四个源片段均为 Humanoid 循环，30 FPS，长度约 0.933 秒。使用原地版本；Animator 不驱动位移。`Walk` 状态使用二维方向混合树，`WalkX`/`WalkZ` 由角色局部实际水平速度计算，`WalkRate` 由实际速度与 WalkSpeed 的比值计算。

按住 S 时行走驱动与转向都让位于现有刹车规则。空中、滑铲、滑铲起身姿态、划墙不进入地面行走，起跳保留当时实际水平速度。攻击与受击继续由现有动作和受击状态控制。

菜单「超高速行者/动作序列/接入四方向行走」可增量装配，奔跑和动作装配入口也会检查行走配置。装配只添加/维护 Walk、三个浮点参数和对应过渡，不重建 Actions、受击或滑墙状态。F3 可观察当前是否处于行走。
