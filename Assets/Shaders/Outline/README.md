# Rendering Layer 描边与夜魂流动

## 配置

- Renderer 上启用 `DeferredOutlineFeature`，选择 **Rendering Layers**，指定 Group。这里筛选 `Renderer.renderingLayerMask`，不是 GameObject Layer。
- 当前使用第 7 位（数值 128）；Player 的 Alpha_Surface / Alpha_Joints 为原值 1 OR 128 = 129，保留原光照层。攻击粒子没有加入。
- 物体只勾这个 Rendering Layer 就能描边，不要求每物体挂 OutlineTarget。同一层共用一个样式、显隐和运动方向。
- Feature 按 Group 找活动 OutlineTarget 作为可选驱动；当前 Player 是唯一匹配 EmberGroup 的驱动。多个匹配时按实例 ID 固定选一个，也可在运行时调用 Feature.SetMotionSource(target) 明确指定有效预设的驱动；传 null 恢复自动选择。
- 无匹配驱动时仍按 Feature 的 Preset Override / Group 绘制，静止火焰向世界上方流动。若要不同对象独立控制，使用不同 Rendering Layer / Feature 配置。

## On / Off 与参数

Player 的 OutlineController 绑定现有 OutlineTarget。Inspector 的 Effect Enabled、On / Off / Toggle 控制原出现/消失过渡；公开 SetEnabled(bool)、TurnOn()、TurnOff()、Toggle() 可供 UnityEvent 调用。

宽度、火焰、抖动、频率、内外颜色及 Preset Override 继续在 OutlineTarget 设置，控制器不会覆盖；没有驱动时读取 Feature 预设。Loop In Demo 应保持关闭。驱动的显隐会控制这个 Rendering Layer 的整组轮廓。

## 运动输入

默认 TransformDelta 从目标根节点的世界位移采样完整速度向量，不读取镜头位移；支持固定物理步与高刷新率。Motion Smoothing Seconds 控制矢量平滑，Teleport Distance 默认 5 米，单帧越界重置（0 禁用）；也可在传送后调用 ResetMotion()。

外部控制器可每帧调用 `target.SubmitWorldVelocity(worldVelocity)`，会切至 ExternalWorldVelocity。必须传世界空间单位/秒；默认超过 0.15 秒没有新提交就以零速度平滑释放，NaN/Infinity 输入直接清理运动。`ClearExternalVelocity()` 回到 TransformDelta。接口不依赖游戏移动程序集。

## 绘制路径与边界

一次 DrawRenderers 按 RenderingLayerMask 筛选不透明/AlphaTest 队列，使用原材质 LightMode=DepthOnly pass，保留蒙皮和原材质 alpha clip。不使用 source override 材质、不按目标重复整场绘制。DepthOnly 输出私有深度，全屏转换为 coverage alpha 并比较场景深度，再通过当帧距离边距、扭曲和 Bezier 反速度火舌合成。没有历史帧图像缓存。

内层宽度稳定；原 tbnEqualSpacingPixels 转为屏幕距离上的统一宽度补偿，不再依赖顶点法线膨胀。事件顺序为描边 548、Bloom 549、URP 内置后处理 550。

这不保证单个 GPU draw：原方案每子网格两次 override 几何绘制，现方案每可见材质/子网格一次原 DepthOnly 绘制，能否合批由 Unity 决定；全屏边距与合成仍有独立 pass。当前两身体子网格的几何提交预期从四次降至两次，实际以 Frame Debugger 为准。

原材质必须有 LightMode=DepthOnly，缺失不会用替代材质伪造轮廓；驱动目标会输出一次缺 pass 警告。普通透明队列不参与。透明遮挡物不写深度时也无法像实体遮挡物一样挡住描边。

旧 DeferredOutlineSource Shader 和 outlineMaterialTemplate 字段保留兼容已有引用，但新 RenderingLayer/DepthOnly 路径不使用它们；无需创建或替换角色材质。