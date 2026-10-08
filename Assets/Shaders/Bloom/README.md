# Bloom

独立 Renderer Feature：`BloomRenderFeature`。由使用者手动添加到当前 URP Renderer；本目录不自动挂载或修改场景。

迁移源：`F:\SceneProject\Unity\Classic-Sponza\Assets\OurFunction\Bloom`。Shader 原样保留，C# 只声明颜色输入、适配 URP 14 的相机颜色目标访问，并在结束时恢复相机颜色和深度附件；亮度阈值、柔和拐点、金字塔下采样/上采样和叠加算法保持不变。

## 原 Sponza 已保存的配置

以下来自 `Assets/ClassicSponza/Settings/URP/UniversalRenderPipelineAsset_Renderer.asset` 中的 `BloomRenderFeature`，与新建 Feature 的源码默认值不同。要复现旧配置，请按此表手动设置。

| 字段 | 原 Renderer 保存值 |
| --- | --- |
| Feature active | 关闭（m_Active = 0） |
| settings.isEnabled | 开启 |
| renderPassEvent | 550 / BeforeRenderingPostProcessing |
| threshold | 1 |
| softKnee | 1 |
| intensity | 2.454 |
| scatter | 0.861 |
| tint | 白色（RGBA 1, 1, 1, 1） |
| iterations | 4 |
| downsample | 3 |
| minSize | 32 |

旧资产的 Feature 开关当时为关闭，以上参数不会自动应用。新仓库的新建默认值为 event 549、threshold 1.25、softKnee 0.5、intensity 0.25、scatter 0.65、downsample 2，其余见代码。不要把“新建默认值”误当作已调好 Renderer 的保存值。

原源码默认 event 551、原 Renderer 保存值 550；迁入 URP 14 后默认设为 549，仅作执行顺序兼容：GTAO 可选独立合成 547 → Outline 548 → Bloom 549 → URP 内置后处理 550，避免在色调映射后才计算泛光。原保存值在上表保留用于追溯，当前工程推荐使用 549；数学参数未改。Feature 只作用于 Game 类型的 Base Camera；Scene View 不执行。用于查看 HDR 高亮扩散时保留管线 HDR。
## 当前场景的保守默认值（2026-10-06）

旧场景强度 2.454 在当前白盒里过亮，不再直接套用。经当前 Main Camera 的 D3D11 三帧对照，改为 threshold 1.25、softKnee 0.5、intensity 0.25、scatter 0.65；新建 Feature 也使用这些值。当前 Renderer 的 downsample 3 保持，其他数学过程不变。关闭/强度0.25的画面均值约0.61236/0.61528，增幅约0.48%，保留明暗边界。旧值上表仅供来源追溯，不是当前建议值。
