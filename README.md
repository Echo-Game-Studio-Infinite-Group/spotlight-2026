# taptap-game-jem · 超高速行者（暂定）

聚光灯 GameJam 2026（TapTap）参赛项目的工作仓库：项目文档 + 技术预研 Demo。

## 目录

```
docs/            # 项目文档
  第一次见面会纪要.md          # 会议与群聊信息汇总
  超高速行者（暂定） v1.03.md   # 策划案（机制需求唯一来源）
  聚光灯GameJam 2026 信息.md    # 比赛情报
  技术选型与工程判断.md         # 技术选型分析
  算法级技术栈与可复用轮子清单.md # 算法 + 开源轮子（已联网核验）
  ACT行业最佳实践工作流.md      # 行业工作流 + 组织结构
demo/
  movement-sim/  # 移动系统数学仿真（dotnet 8 控制台，验证 bhop 算法）
  unity/         # Unity 2022.3.33f1 预研工程（移动手感层脚本骨架）
AGENTS.md        # Agent/协作者工作规范（必读）
```

## 快速开始

### 跑移动仿真

```bash
cd demo/movement-sim
dotnet run
```

输出：跑-跳循环的速度增长曲线、参数扫描、0.2s 落地窗口宽容度分析。结果文件在 `results/`。

### 打开 Unity 工程

1. Unity Hub → 用 **2022.3.33f1** 打开 `demo/unity`（本机路径 `F:\Unity\2022.3.33f1`，注意该编辑器为中国版 c1，团队正式开工前需对齐版本）
2. 首次打开会生成 Library/（已被 .gitignore 忽略）
3. 菜单「超高速行者 → 生成灰盒场景」一键生成测试场景（地面/蹬墙走廊/斜坡/限高门 + 挂好 PlayerMotor 的玩家）
4. **F3** 开关调试 HUD：速度（阈值倍数）/ 能量 / 模式（WindowPump / VerbatimQuake）/ 着地与免摩擦窗口状态
5. MovementParams 里 `Pump` 字段可切换移动模型：**WindowPump（默认，修正模型）** vs VerbatimQuake（策划案字面公式，仿真与引擎测试均已证明零增长，仅作对照）

### 跑引擎侧交叉验证测试（PlayMode）

```bash
"F:/Unity/2022.3.33f1/Editor/Unity.exe" -batchmode -runTests \
  -projectPath "F:/VSCode/taptap-game-jem/demo/unity" \
  -testPlatform PlayMode -testResults results.xml -logFile test.log
```

注意**不能带 `-quit`**（带会在 TestRunner 启动前退出）。或在编辑器里 Window → General → Test Runner。三个测试断言：WindowPump 30 循环速度 ≈6× 阈值、VerbatimQuake 恒为 1×（零增长）、软上限与能量饱和——与 `demo/movement-sim` 的数学结论逐条对应。

## 环境约定

- Unity **2022.3.33f1**（版本冻结）
- 安装 Unity 注意：新版 Hub 会自动跳转团结引擎（国服版），用旧版 Hub 或官网直装国际版
- 逻辑帧 60Hz；数值全走 ScriptableObject
