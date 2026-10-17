# GTAO（Classic-Sponza 原实现）

来源：`F:/SceneProject/Unity/Classic-Sponza/Assets/OurFunction/GTAO`。Compute 和 Composite shader 均字节原样复制；GTAO、双边滤波、时域、Multi-Bounce/SSDO 算法及 C# 默认视觉值不改。C# 适配 URP 14 颜色 RTHandle、颜色/深度附件恢复与 Composite 注入顺序；debug blit 在 Execute 取目标，避免在 AddRenderPasses 访问尚未分配的 RTHandle。

没有自动安装、自动修改 Volume 或自动挂载 Feature。用户手动添加 GTAORendererFeature，并将同目录 GTAOComputeShader.compute 拖到 Compute Shader 字段。按需要使用独立 `ClassicSponza GTAO.asset` VolumeProfile：只包含原 GTAO 组件，不含旧场景 Tonemapping，不绑定任何场景。

## 参数来源与时机

旧 Renderer 中 GTAO：isEnabled=true，event=220，useForwardNormals=true（旧 fallback；实际模式由 UniversalRenderer 检测）。旧 Composite/Debug event=548；URP14 迁移统一改为 547，使它在 Outline 548、Bloom 549 和内置 PostProcessPass 550 之前完成。这只是注入顺序兼容，不改变算法参数。主要视觉调参存在旧 `ClassicSponza/Scenes/Sponza/Global Volume Profile.asset`；本目录独立 Profile 精确保留该 AO 子资产所有值和 override 标记：intensity=2.86、directLightingStrength=0.096、radius=1.18、downsamplingFactor=1、Multi-Bounce=true/0.593、SSDO=true/2.65 等。

注意：temporalEnabled 存值为 true，但其 override=false；组件默认 false，因此该 Profile 没有启用时域。迁移没有擅自打开它，也没有把未 override 的值强制生效。

目标 Unity 2022.3.33f1c1 / URP 14.0.11。Deferred 非 native 路径在事件 211 复制 GBuffer 深度，220 计算 AO 并发布 _ScreenSpaceOcclusionTexture，早于 Deferred Lighting。Feature 自己请求 Depth；Forward fallback 另请求 Normal。当前 Renderer 原有 Feature 列表没有挂载此模块。不要与另一 SSAO/GTAO 同时发布同一个全局 AO 纹理。构建时须保留 Compute 引用及 Hidden/OurFunction/GTAOComposite shader。
URP14 附件兼容：首帧时域 history blit/debug blit 后恢复相机颜色+深度附件；Composite 同时绑定相机颜色与深度且保留深度，避免后续内置 Pass 的附件状态缓存与实际状态不一致。
