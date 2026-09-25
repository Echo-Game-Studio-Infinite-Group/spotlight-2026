# AGENTS.md — 本仓库的 Agent 工作规范

> 所有在本仓库工作的编码 Agent（含子 Agent）必须先读本文档。

## 项目背景

聚光灯 GameJam 2026（TapTap）参赛项目「超高速行者（暂定）」的技术预研仓库。游戏类型：高速 3D ACT（类幽灵行者/泰坦陨落手感 + 忍龙/doom 的战斗爽快感）。核心机制：跑跳（bhop）攒速度 → 高速强化攻防 → 速度转能量 → 能量换技能。

**本仓库当前阶段的目标**：技术预研 Demo——验证移动手感层（风险登记表最高项）的算法可行性，产出可直接并入正式工程的代码骨架。

## 必读文档（按需）

| 文档 | 内容 |
| --- | --- |
| `docs/超高速行者（暂定） v1.03.md` | 策划案，移动/战斗机制的**唯一需求来源**（第 1-4 节是移动系统） |
| `docs/算法级技术栈与可复用轮子清单.md` | 算法公式表（第一节是 Quake 移动公式）、技术选型结论 |
| `docs/技术选型与工程判断.md` | 选型理由、红线、风险 |
| `docs/ACT行业最佳实践工作流.md` | 工作流与迭代循环 |

## 环境

- **Unity：2022.3.33f1（版本冻结，禁止升级）**，编辑器位于 `F:\Unity\2022.3.33f1\Editor\Unity.exe`
- dotnet SDK 8.0（`demo/movement-sim` 控制台仿真用）
- Git；提交信息用中文，一句话说清改动

## 目录结构

```
docs/                # 项目文档（事实记录 + 分析稿，**不要修改**除非任务明确要求）
demo/
  movement-sim/      # 纯 C# 移动数学仿真（dotnet run 可跑，产出 results/）
  unity/             # Unity 2022.3.33f1 工程（预研脚本骨架）
    Assets/_Project/ # 所有自定义代码与资产，禁止在 Assets 根目录散放文件
AGENTS.md            # 本文件
README.md            # 仓库总览
```

## 代码规范（Unity / C#）

1. **逻辑帧 60Hz**：所有移动/判定公式按 tick 结算，`Fixed Timestep = 1/60`（工程内已有 Editor 脚本强制）
2. **数据驱动**：策划案数值全是占位符——一切可调参数进 `ScriptableObject` 或 `Inspector` 字段，禁止硬编码魔法数字（仿真程序里的参数扫描除外）
3. **运动学控制器**：位置与速度手动积分，物理引擎只做碰撞查询（`CharacterController.Move`），禁止用刚体力模拟手感
4. **输入用旧版 Input Manager**（`Input.GetKey` 直读），不引入新 Input System
5. **时间分层**：游戏逻辑读 `TimeManager` 提供的缩放时间；UI/相机走 unscaled——新代码不得直接 `Time.timeScale` 散写
6. 命名：公开成员 PascalCase、私有字段 `_camelCase`；一个类一个文件
7. 注释用中文，只写"为什么/约束"，不写"这行在干什么"
8. **禁止引入任何第三方包/插件**，除非任务明确要求
9. **许可证红线**：Quake/Source 源码是 GPL/受限许可——公式可以照写（数学不受版权保护），代码不能拷贝。一切参考实现按"读懂后重写"处理

## 验收标准（预研 Demo）

- `demo/movement-sim`：`dotnet run` 直接可跑，输出速度增长曲线与参数扫描结果，结论写入 `results/`
- `demo/unity`：用 2022.3.33f1 打开无编译错误；场景里挂上 `PlayerMotor` 能跑走/跳/蹲滑（灰盒验证，不需要动画）

## 禁止事项

- 不修改 `docs/` 下的事实记录文档
- 不升级 Unity 版本、不换渲染管线、不碰 Unity ProjectSettings（`ProjectVersion.txt` 与 Package 版本除外）
- 不删除 `.gitignore` 覆盖的内容（Library/ 等是本地产物）
- Git 提交不含 Unity 的 Library/Temp/Logs 目录与大二进制
