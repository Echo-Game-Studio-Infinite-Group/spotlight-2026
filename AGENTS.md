# AGENTS.md — 本仓库的 Agent 工作规范

> 所有在本仓库工作的编码 Agent（含子 Agent）必须先读本文档。

## 项目背景

聚光灯 GameJam 2026（TapTap）参赛项目「超高速行者（暂定）」的技术预研仓库。游戏类型：高速 3D ACT（类幽灵行者/泰坦陨落手感 + 忍龙/doom 的战斗爽快感）。核心机制：跑跳（bhop）攒速度 → 高速强化攻防 → 速度转能量 → 能量换技能。

**本仓库当前阶段的目标**：技术预研 Demo——验证移动手感层（风险登记表最高项）的算法可行性，产出可直接并入正式工程的代码骨架。

## ⚠️ 目录约定（先看这条）

**本仓库根目录就是 Unity 工程根目录**——`Assets/`、`Packages/`、`ProjectSettings/` 直接位于仓库根下，不存在 `demo/unity/` 这类嵌套。

用 Unity Hub 打开时，选的就是**本仓库根目录**。

## 必读文档（按需）

| 文档 | 内容 |
| --- | --- |
| `Docs/策划案v1.03.md` | 策划案，移动/战斗机制的**唯一需求来源**（第 1-4 节是移动系统） |
| `Docs/aigc/算法级技术栈与可复用轮子清单.md` | 算法公式表（第一节是 Quake 移动公式）、技术选型结论 |
| `Docs/aigc/技术选型与工程判断.md` | 选型理由、红线、风险 |
| `Docs/aigc/ACT行业最佳实践工作流.md` | 工作流与迭代循环 |
| `Docs/aigc/超高速行者_三组分工与21天排期.md` | 排期与分工 |

## 环境

- **Unity：2022.3.33f1c1（版本冻结，禁止升级）**，编辑器位于 `D:\Program Files\Unity\Hub\Editor\2022.3.33f1\Editor\Unity.exe`
- **渲染管线：URP 14.0.11**（`Assets/Settings/URP-Asset.asset` + `URP-Renderer.asset`）
- dotnet SDK（`Tools/movement-sim` 控制台仿真用，目标框架 net8.0）
- Git；提交信息用中文，一句话说清改动

## 目录结构

```
Assets/                    # Unity 资产（仓库根 = 工程根）
  Scripts/                 # 运行时代码（GameJam.Runtime 程序集）
    Movement/              # 两套移动机制 + 参数
    Camera/                # Cinemachine 机位与速度感后处理
    Core/                  # 全局单例与整局流程、时间分层、输入缓冲
    Character/             # 玩家实体、能量与时间效果
    Debug/                 # 调试 HUD
  Editor/                  # 编辑器工具（不进玩家包）
  Tests/PlayMode/          # PlayMode 测试（GameJam.Tests.PlayMode 程序集）
  Scenes/                  # BootScene（启动）+ TestScene（移动 / 战斗联调）
  Prefabs/Maps/            # 地图预制体（MapTestField）
  Settings/                # ScriptableObject 参数 + URP 管线资产
  Shaders/                 # 速度感特效的自研 shader
  Art/                     # 美术资产（Models / Materials / Images）
Docs/                      # 项目文档（**不要修改**，除非任务明确要求）
  aigc/                    # 调研与分析稿
Tools/movement-sim/        # 纯 C# 移动数学仿真（dotnet run 可跑，产出 results/）
Tools/model-export/        # 模型导出与贴图检查脚本（Blender 侧，非 Unity）
AGENTS.md                  # 本文件
README.md                  # 仓库总览（面向接收开发者，说人话）
```

## 代码规范（Unity / C#）

1. **逻辑帧 60Hz**：所有移动/判定公式按 tick 结算，`Fixed Timestep = 1/60`
2. **数据驱动**：策划案数值全是占位符——一切可调参数进 `ScriptableObject` 或 `Inspector` 字段，禁止硬编码魔法数字（仿真程序里的参数扫描除外）
3. **运动学控制器**：位置与速度手动积分，物理引擎只做碰撞查询（`CharacterController.Move`），禁止用刚体力模拟手感
4. **输入用 Input System 1.7.0 Action Map**（用户已批准迁移），统一通过 `PlayerInputReader` 输出快照，不在运动和相机代码中直接读设备按键
5. **时间分层**：游戏逻辑读 `TimeManager` 提供的缩放时间；UI/相机走 unscaled——新代码不得直接 `Time.timeScale` 散写
6. 命名：公开成员 PascalCase、私有字段 `_camelCase`；一个类一个文件
7. 注释用中文，只写"为什么/约束"，不写"这行在干什么"
8. **禁止引入任何第三方包/插件**，除非任务明确要求
9. **许可证红线**：Quake/Source 源码是 GPL/受限许可——公式可以照写（数学不受版权保护），代码不能拷贝。一切参考实现按"读懂后重写"处理

## 移动与相机（重要）

`Assets/Prefabs/character.prefab` 是玩家预制体，运动与相机反馈组件挂在它的根节点；场景使用 character 实例。`PlayerMotor` 是唯一运动控制器，状态为地面、空中、划墙，滑铲是独立姿态。`MovementContacts` 处理碰撞，`MovementMath` 提供公式，参数统一在 `MovementParams`。旧 `CharacterMovement` 已移除。

输入绑定在 `Assets/Input/PlayerControls.inputactions`；移动读取 `PlayerInputFrame`，测试通过 `IPlayerInput` 或 `Simulate` 注入。固定逻辑使用 `TimeManager.PlayerFixedDeltaTime`。

相机使用 Cinemachine 2.10.7：`PlayerCameraRig` 控制 CameraTarget，`SpeedCameraFeedback` 控制速度反馈，不直接改渲染相机 Transform。`DebugHUD` 和重生统一依赖 `PlayerMotor`。说明见 `Docs/Movement代码简述.md`。

## 编辑器工具（菜单「超高速行者」）

| 菜单项 | 作用 |
| --- | --- |
| 装配 URP 与测试场景 | 幂等重建管线资产/材质转换/场景组件接线；**会自动退出 Play 模式**后执行 |
| 地图布局工具 | 按 Ground 实际尺寸重排 Boundary 与 CircleRail，参数可调 |
| 检查装配结果 | 打印管线资产/落盘状态/场景接线的自检日志，排障用 |

## 验收标准（预研 Demo）

- `Tools/movement-sim`：`dotnet run` 直接可跑，输出速度增长曲线与参数扫描结果，结论写入 `results/`
- Unity 工程：用 2022.3.33f1 打开无编译错误；`Assets/Scenes/TestScene.unity` 按播放能跑走/跳/冲刺/滑铲（灰盒验证，不需要动画）
- PlayMode 移动、输入与相机测试全过（当前 27 项，Window → General → Test Runner → PlayMode）

## 已知坑

- **移动/重组工程目录时，`Packages/` 必须跟着走**：Unity 打开一个只有 `Assets/` + `ProjectSettings/` 而没有 `Packages/` 的目录时，会**自动生成一份默认 manifest**（只有 `com.unity.modules.*`）。URP 与 test-framework 依赖会静默消失，表现为 `RadialRedshiftFeature`/`SpeedEffectsRig` 报一堆 `CS0246`（找不到 `ScriptableRendererFeature`/`RTHandle`/`Volume` 等），紧接着 Unity 抛出 `Internal build system error ... backend process is still running`。
  - **注意：`Internal build system error` 是表象不是病根**——它只是 Bee 后端在编译失败后卡住。先看它上面的 `CS` 错误。
  - 修复：把 `com.unity.render-pipelines.universal` 与 `com.unity.test-framework` 补回 `Packages/manifest.json`，关掉 Unity，必要时删 `Library/Bee`（纯缓存）后重开。
  - 排查入口：`Library/ScriptAssemblies` 若为空说明编译从未成功；`Library/PackageCache` 里没有 `com.unity.render-pipelines.*` 说明包没装上。
- **`.gitignore` 有两条模板规则会误伤源码**（父目录被排除时 Git 无法用 `!` 救子文件，所以必须反排除目录本身）：
  - dotnet 段的 `[Dd]ebug/` 会吞掉 Unity 里任意名为 Debug 的源码目录 → 已加 `!Assets/**/Debug/`。**本项目已因此丢过一次 `DebugHUD.cs`**，新建其他 `Debug` 目录后务必确认能被提交。
  - IDE 段的 `*.csproj` 会吞掉独立 dotnet 工具的项目文件 → 已加 `!Tools/**/*.csproj`。Unity 自动生成的 .csproj 仍应忽略。
  - 改完 `.gitignore` 请用 `git add --dry-run <路径>` 验证（`git check-ignore -v` 会把否定规则也打印出来，容易误判）。
- **播放模式下不能用 `EditorSceneManager.OpenScene`**：编辑器工具必须先退出 Play 模式，或走 `EditorApplication.isPlaying = false` + 回调重试。
- **改 Ground 尺寸后必须重排地图**：围墙与环道是按地面尺寸算出来的，不重排会与地面脱节。用「地图布局工具」。
- **改序列化字段后必须 `EditorUtility.SetDirty`**：漏掉时内存值正确但不会落盘，且紧随的 `ImportAsset(ForceUpdate)` 会把空值读回来——自检务必校验磁盘文件而非内存对象。
- **`EditorWindow.OnGUI` 里别做重 IO**：`PrefabUtility.LoadPrefabContents` 会整个加载预制体，放在 OnGUI 里每帧执行会把编辑器拖卡，要缓存。

## 禁止事项

- 不修改 `Docs/` 下的项目文档（`aigc/` 下的分析稿同理），除非任务明确要求
- 不升级 Unity 版本、不碰 Unity ProjectSettings（`ProjectVersion.txt`、Package 版本、渲染管线相关设置除外——管线切换是已批准的决策）
- 不删除 `.gitignore` 覆盖的内容（Library/ 等是本地产物）
- Git 提交不含 Unity 的 Library/Temp/Logs 目录与大二进制
