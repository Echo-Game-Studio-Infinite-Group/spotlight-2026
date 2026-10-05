# 动作序列与配表工具

Unity 菜单 **超高速行者 → 动作序列配表**。模块位于 `Assets/Scripts/Actions` 与 `Assets/Editor/Actions`，当前已将玩家攻击、跳跃与滑铲起手接入 `Player.prefab`。详细接线、配置和执行顺序见 [动作序列与动画攻击接入说明](../../Docs/动作序列与动画攻击接入说明.md)。未新增第三方依赖。

首次使用点「打开 / 创建示例」，或双击 `Assets/Settings/ActionSequences/Examples/ExampleActions.asset`。示例动作关系参考 Git 历史中的 1.03，帧数是演示占位；再次打开示例不会覆盖已经改过的配置。

## 配置与操作

- 「动作配表」编辑阶段子段、帧事件、动画区间、控制命令、取消窗口和预输入。发生阶段可以连续配置多个子段；整个动作也允许再次进入发生阶段，例如连斩末斩的准备段。
- 每个子段至少 1 帧。无需某阶段时省略对应子段。帧事件位于段内 `[0, DurationFrames)`；时长末端的事件放到下一段第 0 帧。
- 取消窗口的起终点可参照子段开始/结束或动作开始/结束，再加有符号偏移。窗口使用左闭右开区间；多窗口权限取并集，各自检查条件。
- 子段 ID 和窗口 ID 自动生成且保持稳定。调整长度和顺序后锚点跟随子段；删除被引用的子段会产生校验错误，不会悄悄改绑。
- 「取消链有向图」显示每个窗口到每个目标的独立有向边，支持重叠窗口、双向边和循环。点击边标签后点「编辑这个窗口」，可定位来源配置。橙色是条件边，红色是无效区间；图展示配置允许关系，并非当前上下文下实际可执行的关系。
- 图中拖动节点，拖动空白平移，滚轮缩放；支持相关边筛选、自动布局、适配视图和 SVG 导出。布局保存到当前项目的本机编辑器偏好，不影响运行时配置。
- 「独立执行预览」用模拟能量、条件和语义输入测试执行器。载入预览模型后，克隆 Animator 同步采样骨骼；不操作当前场景角色。支持单帧、时缓、顿帧、直接请求、取消边预览和通知日志。配置发生变化时重置预览。
- 「动作集与检查」检查重复 ID、失效锚点、非法事件、缺失目标、矛盾按键与动画区间。窗口重叠和取消环合法。
- 编辑支持 Unity Undo，点「保存」落盘；移除动作集引用不会删除动作资产。

## 输入与预输入

`ActionInputPolicy.Steps` 表示有序输入。每步指定一个语义键、按下/松开/长按方式，以及必须和禁止按住的修饰键；每步最大间隔、长按阈值和预输入均用 **60Hz 非缩放采样帧**。

普通组合键配置为一个步骤及修饰键，例如推斩为 Attack 按下、必须按住 Skill、禁止按住 Forward。顺序输入配置多个步骤；长按仅用于最后一步，每次按住会话最多识别一次，松开后重新武装。松开沿支持最短/最长按住帧数，最大值 0 表示不限。

一个输入事件只归属一个识别结果：先比较输入优先级，再比较序列长度，同分按动作集顺序选择。输入资格用于区分闪斩/普攻、常态/高速等语义；能量和冷却在执行阶段检查，技能失败不会回退为普通攻击。序列步骤被识别后保留为该动作请求，不复用已占用的前缀；第一版不自动延迟短指令来等待更长前缀，需要用修饰键或独立输入避免此类歧义。

预输入时长由**目标动作**决定。0 仅尝试立即执行，失败即丢弃，其余值 N 的有效区间为 `[CreatedTick, CreatedTick + N)`。同组按配置保留最新、最早或有限队列。原动作被取消时默认清掉与其实例关联的其他请求，可显式开启跨取消保留；自然结束仍允许缓冲动作接续。顿帧可按目标配置暂停过期，普通时缓不会自动延长过期。

## 接入契约

`ActionSequencePlayer` 是纯 C# 类，不读取 Unity 时间、设备、Animator、Motor 或资源账户。玩家已由 `PlayerActionRunner` 接入；其他角色接入时需要：

1. 从 `PlayerInputReader` 提供的统一快照构造 `ActionInputSample`。`Tick` 严格递增，按下/松开事件按实际顺序排列；事件携带当时的按住键与输入方向，避免按下后改变方向使请求变招。方向语义键由快照适配器生成，不在执行器读 WASD。
2. 每个采样 tick 调用 `Tick(sample, TimeManager.PlayerFixedDeltaTime * ActionSequencePlayer.FramesPerSecond, inHitStop)`。顿帧标记由接入层根据顿帧来源提供；不能用总时间倍率代替，因为时缓也会降低倍率。动作时间缩放与输入采样时钟分开。UI 暂停时清理待执行输入并停止 gameplay 采样，保留当前动作进度，不在恢复时补发旧输入。
3. 实现 `IActionSequenceHost`：解释条件键、检查目标是否合法、在 `TryCommit` 统一扣费及消费派生资格。失败必须无副作用，成功后执行器才退出旧动作；动态推斩耗能在这里计算。一次性耗能不乘时间缩放。
4. 实现 `IActionSequenceSink`：动作进入、子段进入、帧事件、退出、只读状态采样。移动指令交给唯一的 `PlayerMotor`；退出回调关闭该实例的判定并释放限制。动画适配器使用状态中的实例号、子段绑定及 `AnimationNormalizedTime`，避免动画机自行决定取消。
5. 已由外部识别的请求可通过 `Queue(target, inputTick, direction)` 注入，仍会检查窗口、进入条件、冷却和 Host 提交。此入口不重新识别输入资格。

同帧先仲裁取消，再发送旧动作帧事件。批量推进会逐个检查整数帧边界，不会跳过短取消窗口或帧事件。冷却使用累计玩家帧，跨动作继续推进。阶段与攻击命中段分开，实际玩家按 `InstanceId × HitGroup × Enemy` 去重。

玩家适配器已接入骨骼命中查询、能量账户、Motor 和 Animator 手动求值。右键时停仲裁、并行动作通道和推斩动态耗能仍保留扩展入口；沙盒使用固定 `EnergyCost`，不模拟这些玩法公式。

## 验证

```powershell
& ./Tools/ActionSequences/validate.ps1 -Stage Compile
& ./Tools/ActionSequences/validate.ps1 -Stage Install -UseRepositoryPackages
& ./Tools/ActionSequences/validate.ps1 -Stage EditMode
& ./Tools/ActionSequences/validate.ps1 -Stage PlayMode
& ./Tools/ActionSequences/validate.ps1 -Stage Examples
& ./Tools/ActionSequences/validate.ps1 -Stage All -UseRepositoryPackages
```

Compile 使用已安装的 dotnet SDK 和冻结版 Unity 参考程序集检查全部运行时代码和编辑器代码。其他阶段在 `Temp/PlayerActionValidation` 副本运行 Unity，复制 Assets、Packages、ProjectSettings 和现有包缓存，不修改当前场景和工程设置；结果 XML 与日志位于副本。`UseRepositoryPackages` 只在副本使用 dev 已提交的依赖。Install 在副本增量装配攻击；Examples 生成独立示例和 SVG。仓库另附 `Tools/ActionSequences/example-cancel-chain.svg`。

原独立模块保留 30 项 EditMode 测试；玩家接入新增动画、命中窗口、取消、资源、生命周期与实际 Input System / FixedUpdate 测试。最新结果见详细接入说明和隔离工程 XML。集成版本不再支持把 Actions 源码排除后运行 BaselinePlayMode。
