# 音频系统改动记录

> 状态说明：本文件按时间追加，保留每次改动当时的状态。正文中的“未 commit”
> 或“本次仍未 commit”只表示该条记录写入时的情况，不代表当前分支状态。

## 当前状态

- 当前分支：`feat/audio_wwise`
- 已合并：`7c688a1 Merge remote-tracking branch 'origin/dev' into feat/audio_wwise`
- 最近提交：`4db89fd chore(audio): update Wwise project and soundbank assets`
- 当前动态链路：
  ```text
  IDynamicAudioActionSource
  -> WwiseAudioRegistry
  -> WwiseActionDriver
  -> WwiseActionPcmRenderer
  -> Wwise Source Plugin
  -> DynamicAction Bus
  ```
- 一次性动作链路：
  ```text
  audio.<EventId> 帧事件
  -> PlayerActionRunner
  -> WwiseEventBridge
  -> WwiseEventBindings
  -> Wwise Event
  ```

## 历史基线和约束（初始阶段）

- 仓库：`spotlight-2026`
- 初始分支：`feat/audio`
- 基线提交：`11a460d246766561890e82e12ffb4a38e55d5f27`
- 初始阶段尚未 commit。
- 初始约束是不修改 `README.md`、`Scripts/Combat`、`Character`、动画、模型和美术资源。
- 初始版本未为动作系统新增时间字段；音频驱动自行记录动作时间。

## 架构修正

之前的首版把素材分类和文件路径当成了运行时契约，这是错误的。现已改为：

```text
GameAudioCatalog        项目自行决定保存在哪里
PlayerAudioProfile      动作语义到音频资产的映射
AudioCueDefinition      单次音效资产
AudioActionDefinition   单音效连续动作资产
AudioEnvelope           可序列化分段包络
GameAudioInstaller      在场景或启动流程中注入 Catalog / Profile
```

运行时代码不再：

- 从 `Resources` 固定路径加载音频。
- 硬编码金属、水、草地、岩石、科技等分类。
- 假设某个文件必须存在。

素材当前只暂存在：

```text
Assets/AudioCollection/
```

它不是运行时契约，也不参与自动加载；音频库位置和素材分配由项目后续决定。

## 单音效连续动作

`AudioActionDefinition` 已改为真正的单音效连续动作：

```text
一个 AudioClip
-> StartPosition01
-> LoopStart01 / LoopEnd01
-> ReleasePosition01
-> Start / Sustain / Release 包络
```

运行期状态机：

```text
Starting
-> Sustaining
-> Releasing
-> Finished
```

实现细节：

- 单个 `AudioSource` 播放同一个 `AudioClip`。
- Sustain 阶段手动维护循环区。
- 循环使用样本位置回绕，不依赖整段 `AudioSource.loop`。
- Release 可以继续当前样本位置，也可以选择跳到 Release 区。
- 动作持续任意时长。

## 包络处理

新增 `AudioEnvelope`：

- 可序列化 `EnvelopeKey[]`。
- 每个节点有归一化时间、值和到下一节点的曲线类型。
- 支持 Linear、Slow、Fast、S-Curve。
- 内置 FadeIn、Steady、FadeOut、Linear、ADSR 预设。

`AudioActionDefinition` 使用：

```text
StartEnvelope
SustainEnvelope
ReleaseEnvelope
PitchFollow
LowpassFollow
```

参数平滑仍使用单极点式响应，避免每帧直接写音高和滤波。

## 动作驱动

`ActionControlFrame` 提供：

```text
NormalizedSpeed
ContactIntensity
Direction
Release
ActionElapsed
SurfaceId
Seed
```

Start / Release 使用时间包络，Sustain 使用动作状态驱动：

- 速度映射音高。
- 速度映射低通截止频率。
- 接触强度映射音量和低通。
- 方向映射左右声像。
- 每次动作固定随机种子，产生音高、音量、滤波和循环相位差分。

## 基本触发

`PlayerAudioDriver` 是运行时挂载的独立桥接层，不修改战斗或角色代码：

- 滑铲和墙滑读取 `PlayerMotor` 公开状态。
- 攻击读取 `PlayerCombat.IsAttacking`。
- 命中读取 `Damageable.Damaged`。
- 受伤读取 `PlayerHealth.Health` 变化。
- 脚步使用脚底 Raycast；命中 `AudioSurface` 时使用其上配置的 `AudioCueDefinition`。
- 没有 `AudioSurface` 时使用 `PlayerAudioProfile.DefaultFootstep`。

## 滤波和音色

- `AudioActionVoice` 使用单个 `AudioLowPassFilter` 和 `AudioHighPassFilter`。
- 频率使用对数插值。
- 音量使用 dB 到线性 Gain。
- 每个动作定义可以独立配置起段、持续段、结束段和速度跟随曲线。

## 故障和特殊效果

- `StutterGateFilter`：门控切碎。
- `BitcrushFilter`：位深量化和采样保持。
- `AudioEchoFilter`：短延迟。
- `AudioDistortionFilter`：失真。
- 触发点：hitstop、受伤、受击、传送。

## 调试

- `AudioActionDefinition` 自定义 Inspector：
  - `Global Envelope` 面板。
  - Start / Sustain / Release / Pitch / Lowpass 五个页签。
  - 平滑曲线和彩色节点。
  - 单音效 Start / Loop / Release 区域条。
  - Start 和 Release 时长以毫秒显示。
- `Audition`：
  - “按住试听”按钮记录真实按住时间。
  - Inspector 显式处理 MouseDown / MouseUp，并用 EditorApplication.update 持续刷新。
  - 按住时直接播放隐藏 AudioSource，并实时计算 Start / Sustain 包络。
  - 松开后立即进入 Release 包络，Release 走完后自动停止。
  - 不调用 AudioClip.GetData，因此不依赖音频锁定或解压到内存。
  - WAV、MP3 等格式都可直接用当前导入设置试听。
  - 试听支持 Speed Ratio、Contact、音高和低通跟随预览。

### 编辑器试听无声的修复（2026-10-03）

现象：

- 按住试听时状态框一直是 `held 0 / gain 0`，且基本听不到声音。
- 更早的版本还会报
  `SoundManager.cpp(832) ... m_Sound->unlock(ptr1, ptr2, len1, len2) invalid parameter`
  并伴随 `AudioClip.GetData` 调用栈。

原因和修复：

1. `AudioClip.GetData` 在部分导入设置下会触发原生解锁错误。试听路径已彻底移除 `GetData`，
   改为实时控制隐藏 `AudioSource`，不再把音频锁进内存。
2. 试听没有推进编辑器音频时钟。现在每个 `EditorApplication.update` 都会反射调用
   `UnityEditor.AudioUtil.UpdateAudio()`，并调用 `QueuePlayerLoopUpdate()`。
3. 之前 `held 0 / gain 0` 是因为 Inspector 没有持续重绘，
   `EventType.MouseDown / MouseUp` 没有被正确消费。已改为显式处理并持续重绘。
4. 场景里没有 `AudioListener` 时，会自动给隐藏试听对象挂一个，避免编辑器里没有听者而不出声。

实测确认（Unity 2022.3.33f1，编辑器编辑模式）：

- `AudioSource.Play()` 后 `isPlaying` 在约 90 ms 变为 true。
- `AudioSource.timeSamples` 随 `AudioSettings.dspTime` 正常推进（0.5 s 时约 23552 采样）。
- `AudioListener.GetOutputData` 在编辑模式下始终返回 0，属于该 API 的预期行为，
  不能用它判断编辑器试听是否出声。
- 实际试听可听到 Start / Sustain / Release 包络变化。

### 长按无声：循环区落在素材静音段（2026-10-03）

现象：

- 按一下马上松开能听到声音，长按反而没声。

定位：

- 试听状态机本身正常：按住时 `Gain 1.00`、`volume 0.972`，播放头按 48 kHz 实时推进，
  循环回绕也生效。
- 问题在素材本身。当时绑定的 `footrub_long.wav`（6.37 s，48 kHz，32-bit float 立体声）
  只有 **0 ~ 0.9 s** 有内容，`0.9 s` 到结尾全是零采样。
- 而 `LoopStart01 / LoopEnd01` 是 `0.15 / 0.8`，换算成时间是 `0.96 s ~ 5.09 s`，
  整段都落在静音尾巴里。于是 Start 段出声，Sustain 循环的是静音。
- 结论：不是逻辑错误，是默认区域值和素材内容不匹配。

为避免再踩，新增：

- `AudioActionClipAnalysis`（编辑器侧）：把整段素材切成 20 ms 窗口统计 RMS，
  得到峰值位置、有声区间，以及任意归一化区间的平均能量。
  - 分块读取采样，长素材不会一次性占满内存。
  - `Streaming` 导入直接给出提示，不做读取。
- `AudioActionDefinition` Inspector：
  - “分析素材能量”：在区域条里画出能量波形，显示有声区间和峰值位置。
  - “自动设置循环区”：在有声区间内给出可循环中段，循环点尽量落在峰值之后，
    让起音瞬态只在 Start 段出现一次。
  - 循环区能量低于峰值 5% 时弹 HelpBox，直接说明“按住试听会没有持续声”。
  - 紫色循环手柄可以直接在区域条上拖动。
- 当前 `AudioActionDefinition.asset` 的循环区已按分析结果修正为 `0.064 ~ 0.123`
  （约 `0.41 s ~ 0.78 s`，区间能量 0.026），长按现在有持续声。

备注：这个素材真正有内容的只有约 0.6 秒，本质更接近一次性音效，不适合长时间循环。
滑铲持续音建议换成 `slide_loop.mp3` / `slide_loop_alt.mp3`。

### 长按声音特别大：循环接缝跳变 + 重复最响段（2026-10-03）

现象：

- 循环区修好以后长按有声音了，但听起来比短按响很多。

定位（实测数据）：

- 素材是“渐强再衰减”的形状，能量峰值在 `0.35 ~ 0.50 s`，`0.8 s` 之后接近静音。
  循环 `0.41 ~ 0.78 s` 等于把最响的一段反复播放，不再让它自然衰减，
  持续 RMS 0.0276 对比一次性 0.0238，只高约 1.3 dB。
- 真正刺耳的是接缝：循环终止采样 `0.0005 / 0.0023` 直接跳到起始采样
  `-0.0721 / -0.0960`，最大跳变 `0.143`（约为峰值 0.282 的 51%）。
  0.37 s 的循环相当于每秒约 2.7 次硬跳变，听感就是一个又响又毛的脉动。

修复：

- `AudioActionDefinition` 新增 `LoopSeamFadeSeconds`（默认 20 ms，`0~0.25 s`）。
  循环点两侧各做一次淡出/淡入，把采样跳变摊平成短斜坡。
- `AudioActionVoice`（运行时）和 `AudioActionPreviewPlayer`（试听）都应用这个接缝淡化，
  保证“试听听感”和“运行期听感”一致。
  - 运行时用独立的 `_sustainLevel` 做参数平滑，再把接缝淡化乘上去，
    避免平滑器把淡化吃掉。
- `AudioActionClipAnalysis` 新增峰值幅度和 `SeamStep01()`：
  计算循环起点首采样与终点末采样的最大差值。
- Inspector 在接缝跳变超过峰值 12% 时给出警告，并提示改用本身可无缝循环的素材。

结论：以上两点都是“素材本身不适合循环”造成的正常后果，工具只能削弱爆音和脉动，
不能把一次性素材变成可无缝循环的持续音。滑铲持续段请用 `slide_loop.mp3` /
`slide_loop_alt.mp3` 这类为循环制作的素材。

### 长按跳过 Start 段（2026-10-03）

现象：

- 长按试听时起音直接进持续段，StartPosition→LoopStart 这段起音被跳过。

原因：

- `AudioActionVoice.UpdateStart` 和 `AudioActionPreviewPlayer.TickStarting` 都用
  `if (播放头已到 LoopStart || u >= 1f)` 作为进入持续段的条件。
- `u` 是 `_stateTime / StartDuration`，`StartDuration` 默认只有 0.12 s，
  所以起段在 0.12 s 就被强行截断，直接跳到 LoopStart。
- 结果：起段实际只播了素材最前面的 0.12 s。之前 `LoopStart01` 靠后时听不出来，
  循环点一提前就非常明显。

修复：

- 起段相位长度改为**完全由区域决定**：从 `StartPosition01` 一直播到 `LoopStart01`，
  播放头到达 `LoopStart01` 才进入持续段。
- `StartDuration` 语义收窄为“起段包络的爬升时间”，不再作为相位的硬性上限，
  字段加了 Tooltip 说明。
- 运行时和试听两处同步修改，保证听感一致。
- Inspector 的 Start 标签改为显示真实起段长度与包络时间，例如
  `Start 1.36s · 包络 120ms`，避免再被 `StartDuration` 误导。
- `AudioActionClipAnalysis.TrySuggestLoopRegion` 相应调整：既然起段会完整播完，
  循环点就应落在起音瞬态“之后”（峰值后能量首次跌破 60% 的位置），
  否则每轮循环都会重新触发瞬态，听感像机关枪。

### 松手不进 Release（2026-10-03）

现象：

- 按住试听后松手，声音没有进入 Release 收尾，状态一直停在按住。

原因：

- 试听按钮只判断 `Event.current.type == EventType.MouseUp`，没有接管 `GUIUtility.hotControl`。
- 一旦按住过程中鼠标移出按钮区域（或窗口没把 MouseUp 派发到 Inspector），
  这个事件就收不到，`_previewHeld` 一直是 true，`AudioActionPreviewPlayer.Release()` 永远不执行。
- 状态机本身没问题，实测 `Begin → Releasing → Finished` 正常：
  `Releasing · 100 ms · Gain 0.30` → `Releasing · 200 ms · Gain 0.04` → 停止。

修复：

- 试听按钮改用标准 IMGUI 热控件模式：
  MouseDown 且命中按钮时 `GUIUtility.hotControl = controlId`，
  MouseUp 只要 `hotControl` 还是自己就一定收到，鼠标移出按钮也能正常松手。
- 增加兜底：`_previewHeld` 为真但 `hotControl` 已不在自己手上时，直接结束按住，
  避免卡在持续段循环下去。
- `MouseLeaveWindow` 也会结束按住。

### 区域条图例（2026-10-03）

区域条原来只有颜色没有说明，容易看不懂。现在加了图例，五段分别是：

| 颜色 | 含义 |
| --- | --- |
| 黄色 `裁掉` | `0 → StartPosition01`，播放起点之前被裁掉的头部 |
| 蓝色 `Start` | `StartPosition01 → LoopStart01`，起段相位，会完整播完 |
| 紫色 `Loop` | `LoopStart01 → LoopEnd01`，持续段循环区 |
| 橙色 `间隙` | `LoopEnd01 → ReleasePosition01`，循环末尾到 Release 点之间的空档；只有开启 `SeekToReleaseRegion` 且 Release 跳到该点时才会用到 |
| 绿色 `Release` | `ReleasePosition01 → 1`，Release 区 |

### 重做为标准 ADSR + 采样循环点模型（2026-10-03）

上一版把两件事混在了一起，不符合通用音色处理的做法：

- 把 Sustain 做成了“循环相位上的曲线”，而 Sustain 在标准里是**电平**，不是时长。
- 把“起段”做成了从 StartPosition 播到 LoopStart 的独立相位，
  而标准采样器里循环只是“播放头越过 loop_end 就回跳 loop_start”，没有独立起段相位。
- `StartDuration` 同时承担“包络爬升时间”和“起段相位长度”两种含义。

参照 SFZ 规范（sfzformat.com）核对后的标准语义：

- `ampeg_attack` / `ampeg_decay` / `ampeg_release`：**时间（秒）**。
- `ampeg_sustain`：**电平百分比**，按住期间保持不变，按住多久就保持多久。
- `loop_mode`：
  - `no_loop`：从起点播到素材结尾，或先收到松手。
  - `one_shot`：整段播完，忽略松手。
  - `loop_sustain`：**按住期间**在循环点之间循环；**松手后不再循环**。
  - `loop_continuous`：一直循环，包含 Release 阶段。

按这个模型重做：

- 新增 `AdsrEnvelope`（`Assets/Scripts/Audio/AdsrEnvelope.cs`）：
  `AttackSeconds / DecaySeconds / SustainLevel / ReleaseSeconds` + 三段曲线类型，
  提供 `EvaluateOn(按下后秒数)` 和 `EvaluateRelease(松手后秒数, 松手瞬间电平)`。
- `AudioActionDefinition` 移除 `StartEnvelope / SustainEnvelope / ReleaseEnvelope /
  StartDuration / ReleaseDuration / SustainFadeResponse`，改为一个 `Envelope`（ADSR）。
- 新增 `AudioActionLoopMode`（对齐 SFZ `loop_mode` 四态）。
- 播放头逻辑改成标准循环点行为：从 `StartPosition01` 前进，
  **越过 `LoopEnd01` 才回跳 `LoopStart01`**，所以首遍会把起音完整播完。
  `loop_sustain` 松手后停止回跳，播放头继续向素材结尾推进，这段就是天然的 Release 尾巴。
- 运行时 `ActionAudioVoice` 和试听 `AudioActionPreviewPlayer` 用同一套模型。
- 循环接缝淡化只在**循环区内部**生效；首遍还没走到 `LoopStart` 时不做淡化
  （此前会把整段起音压成 0 音量，已修复）。
- Inspector：
  - 页签改为 `ADSR (音量) / Pitch / Lowpass`，ADSR 面板按标准形状绘制
    （attack 斜坡 → decay → sustain 平台 → release 斜坡）并标注 ms / %。
  - 区域条图例改为 `裁掉 / 首遍 / Loop / Release 目标`，并画出 loop_end→loop_start 的回跳箭头。
  - 新增警告：`SeekToReleaseRegion` 打开但 Release 目标点到结尾几乎全是静音时会提示。

实测（编辑器内跑运行时的 `ActionAudioVoice`）：

- `Starting` 期间 `gain 1.00`，越过 `LoopEnd` 后 `state=Sustaining`、`loopEntered=True`。
- `RequestRelease()` 后 `state=Releasing`，`gain 1.00 → 0.30 → 0.00`，随后 `Finished`。

### 两个图支持拖动 + Release 目标点始终可见（2026-10-03）

- 区域条原来只有紫色 Loop 两端能拖，而且 Release 目标点只在开启
  `SeekToReleaseRegion` 时才画出来，所以默认看不到绿色那段。现在：
  - 白色 = `StartPosition01`，紫色 = `LoopStart01 / LoopEnd01`，绿色 = `ReleasePosition01`，
    四个手柄都能直接拖。
  - Release 目标点始终绘制；没开启 `SeekToReleaseRegion` 时画成半透明，
    表示“现在松手不会跳到这里”。
- ADSR 面板也支持拖动：
  - 黄点 = Attack，横向拖动改 `AttackSeconds`。
  - 橙/紫点 = Decay 末端，横向改 `DecaySeconds`，纵向改 `SustainLevel`。
  - 紫点 = Release 斜坡起点，**向左拖 = Release 更长**（斜坡终点固定在时间轴右端）。
  - 拖动过程中冻结时间轴，避免“改值 → 轴变化 → 手柄乱跑”的反馈回路。
- 试听按钮的 MouseUp 再加一层兜底：只要 `_previewHeld` 为真，
  任何 MouseUp 都会结束按住并进入 Release，不再依赖 `hotControl` 是否还在手上。

关于 Release 尾巴的行为（避免误解）：

- `loop_sustain` 模式下，松手后播放头停止回跳，继续向素材结尾推进，
  所以 Release 尾巴 = **LoopEnd01 到素材结尾**的那一段。若这段是静音，松手就只会听到 R 时间的淡出。
- 想要一段独立的收尾音，就打开 `SeekToReleaseRegion` 把 `ReleasePosition01` 指向尾音起点。

### 循环改成真正的交叠淡化（2026-10-03）

问题：

- 之前的实现只是“尾部接回开头”，接缝要么硬拼，要么靠每帧改 `AudioSource.volume` 做淡出淡入。
- `AudioSource.volume` 只在控制帧更新，20 ms 的淡化在 60 fps 下只有 1 个台阶，
  等于没淡，仍然是硬切。玩家听得到脉动。

参考 DSP Action：

- `GameSynth.DSP/XShareableGrooveReader.cs` 用**两个 reader** 同时播放，再做窗口交叠：
  `(num * blackman[fadeOutIndex++] + readers.Last().Tick(...) * blackman[fadeInIndex++]) / compensation_win[...]`，
  其中 `compensation_win[i] = blackman[i] + blackman[i+256]` 用来做归一化。
- 关键点是它在**采样级**做交叠，而不是每帧改音量。它的 `Oscillator` 才是硬回绕。

按这个思路重做：

- 新增 `ActionClipRenderer`：把循环区预渲染成一条已经做过交叠淡化的片段。
  交叠长度 `xf`，原长 `L = loopEnd - loopStart`，新片段长度 `P = L - xf`：
  - `j ∈ [0, xf)`：`x[loopEnd - xf + j] * (1 - w(j)) + x[loopStart + j] * w(j)`
  - `j ∈ [xf, P)`：`x[loopStart + j]`
  - `w` 为升余弦，权重对和恒为 1（等价于等幅交叠）。
  这样 `B[P-1] → B[0]` 天然连续，直接交给 `AudioSource.loop` 就是无缝循环，不依赖帧率。
- 起段仍然播原始素材：`PlayScheduled` 起点，`SetScheduledEndTime` 精确停在交叠区之前，
  同时把循环片段 `PlayScheduled` 到同一时刻，做到采样级交班。
- 松手：
  - `loop_sustain`：停掉循环片段，回到原始素材、从当前相位对应的采样继续向结尾播，
    这段就是 Release 尾巴。
  - `loop_continuous`：继续循环，只走 ADSR 的 R。
- `LoopSeamFadeSeconds` 语义改为“交叠淡化长度”，内部会 clamp 到循环区长度的一半。
- 预渲染结果按 `(clip, loopStart, loopEnd, crossfade)` 缓存，不会每次触发都重算。
  素材为 `Streaming` 导入时无法读取采样，此时会退化为不循环（Inspector 状态栏会标注“无循环片段”）。

实测（编辑器内驱动真实代码路径）：

```
renderedLoop=True  loopClipSamples=55018 (= 55979 - 960 xfade)
0.4s..2.8s   A 播起段，B 停在 0 等待
3.20s        A 停止，B pos=3834        <- 采样级交班
4.00s        B pos=41722
4.40s        B pos=6160                <- 原生循环回绕
5.20s        B pos=44048
5.60s        B pos=8486                <- 再次回绕
```

另外验证了 Inspector 的按键胶水（用反射直接调 `StartAudition` / `EndAudition`）：

```
StartAudition -> held=True active=True 'Attack'
held 0.5~2.0s -> 'Sustain · ... · Gain 0.35'
EndAudition   -> 进入 Release，0.3 s 后 active=False
```

### Release 区间显示 + 交叠时间可调 + 去掉拖动（2026-10-03）

问题：

- 区域条上只画了一根 Release 目标线，没有画出“松手后到底会响哪一段”，
  看起来像是 Release 区间没生效。
- 交叠时间只能改 `LoopSeamFadeSeconds` 字段，看不出上限，也不知道实际生效多少。
- 拖动容易误操作，暂时去掉。

改动：

- Release 尾巴改为画成**区间**（绿色块），语义和实际发声一致：
  - 没开 `SeekToReleaseRegion`：尾巴 = `LoopEnd01 → 素材结尾`（松手后播放头继续往前）。
  - 开了 `SeekToReleaseRegion`：尾巴 = `ReleasePosition01 → 素材结尾`，同时保留目标点竖线。
  图例也改成 `裁掉 / 首遍 / Loop / Release 尾巴`。
- 新增“交叠时间”滑条：
  - 上限 = `min(250 ms, 循环区长度的一半)`，超过一半会把整个循环吃掉。
  - 旁边显示 `生效 xx ms · 上限 yy ms`，字段值和实际生效值不一致时能直接看出来。
  - 内部渲染时仍会再 clamp 一次，素材或循环区改小也不会出事。
- 去掉 ADSR 图和区域条的所有拖动，改为**鼠标悬停提示**：
  - ADSR 图：悬停节点或区间会说明是 Attack / Decay / Sustain / Release，以及当前值。
  - 区域条：悬停分界线提示对应字段（StartPosition01 / LoopStart01 / LoopEnd01 /
    ReleasePosition01），悬停色块提示这一段在什么时候发声。
  - 提示文字固定占一行，不随内容增删控件，避免 Inspector 布局跳动。
- 试听按钮的 hotControl + 兜底释放逻辑保持不变（那不是拖动）。

验证：

- 批量模式编译通过（`Assembly-CSharp-Editor.dll`，compile time 2349 ms，无 CS 错误）。

### 松手行为按相位区分 + 恢复默认值（2026-10-03）

按需求把松手逻辑改成按相位决定：

- 起段（Attack / 首遍，还没进循环）松手：
  `AudioActionDefinition.IntroReleaseMode` 可选：
  - `StopImmediately`：直接停。
  - `PlayFullOnce`：不循环，把这一遍音效完整播完再停。
- 持续段（已经进循环）松手：
  **先走完当前这一遍 loop，再进 Release**。
  实现方式：松手只置 `_releasePending`，继续播；检测到循环片段播放头回绕
  （`timeSamples` 变小）即认为这一遍走完，此时停循环、回到原始素材走 ADSR 的 R。
  这样 Release 不会切在半句上。
- `AudioActionDefinition.ResetToDefaults()` + Inspector 顶部“恢复默认值”按钮
  （带二次确认，保留 `Clip` 和 `Id`）。
- 起段交班时两个 AudioSource 可能有一瞬间都不在播放，
  预览和运行时都改成“静音持续 50 ms 才判定结束”，避免误判为播放完毕。

关于“ADSR 好像没用”的排查：

- 头less 验证 `AdsrEnvelope`（A=0.5 / D=0.3 / S=0.4 / R=1.0，线性）：
  ```
  on(0.00)=0.000 on(0.25)=0.500 on(0.50)=1.000
  on(0.65)=0.700 on(0.80)=0.400 on(3.00)=0.400
  rel(0.5|0.4)=0.200 rel(1.0|0.4)=0.000
  ```
  说明包络本身是对的：Attack 线性爬升、Decay 降到 SustainLevel、
  Sustain 按住多久保持多久、Release 从松手瞬间电平降到 0。
- 但当时工程里那条资产的 `ReleaseSeconds` 只有 **0.0707 s（71 ms）**，
  `SustainLevel` 0.345、`AttackSeconds` 1.0。71 ms 的 Release 几乎听不出来，
  所以感觉“没进 Release / ADSR 没用”。把 `ReleaseSeconds` 调到 0.4~0.8 s 就能明显听到收尾。
- 另外之前 Release 需要靠松手事件触发，如果没触发就永远停在循环里，
  R 段自然一次都不会响；现在持续段松手会等一遍循环结束后必定进入 Release。

其它验证（批量模式，`-executeMethod`）：

```
ADSR A=0.5 D=0.3 S=0.4 R=1.0:
  on(0.00)=0.000 on(0.25)=0.500 on(0.50)=1.000
  on(0.65)=0.700 on(0.80)=0.400 on(3.00)=0.400
  rel(0.0|0.4)=0.400 rel(0.5|0.4)=0.200 rel(1.0|0.4)=0.000
ResetToDefaults: loop=0.20..0.90 S=1.00 spatial=1.00 introRelease=PlayFullOnce
Crossfade loop: samples=23040 (expect 23040) firstErr=0.000000 lastErr=0.000000
Wrap detect: pending+wrap=True pending+noWrap=False notPending+wrap=False
```

### 起段松手失效的根因 + 区域条恢复拖动（2026-10-03）

问题：

- `IntroReleaseMode` 两种模式都无效，起段松手后还是会进循环。

根因（关键坑）：

- `AudioSource.PlayScheduled()` 一旦调用，`isPlaying` **立刻**变成 `true`，
  即使排程的播放时间还没到。
- 原来判断“是否已经进循环”用的是 `if (_sourceB.isPlaying) _loopEntered = true;`，
  于是第一帧 `_loopEntered` 就变成了 true，**起段分支永远进不去**，
  松手总是走“持续段”分支去等循环回绕。
- 修复：记录排程时间 `_loopStartDspTime`，
  用 `AudioSettings.dspTime >= _loopStartDspTime` 判断是否真的进了循环。

实测（驱动真实试用播放器，三组用例）：

```
--- PlayFullOnce @ intro ---
0.48s entered=False 'Sustain ...' A[pos=21152] B[pos=0]
--- Release at 0.48s, entered=False ---      <- 正确进入起段分支
0.98s..3.98s '完整播一遍' A 一路播到 183968，B play=False
4.97s active=False                          <- 播完即停，全程没进循环

--- StopImmediately @ intro ---
--- Release at 0.50s ---
1.00s active=False                          <- 立即停止

--- Sustain release ---
3.09s entered=True B[pos=3272]
--- Release at 3.09s, entered=True ---
3.59s 'Sustain ...' B[pos=27848]            <- 先把这一遍循环走完
4.09s 'Release · 298 ms · Gain 0.05' A[pos=47486]  <- 然后进 Release
4.58s active=False
```

另外：

- 采样回放区域条恢复拖动（白=StartPosition01，紫=LoopStart01 / LoopEnd01，
  绿=ReleasePosition01），同时保留鼠标悬停说明。ADSR 图暂时仍是悬停查看。

### 持续段 Release 复核 + 绿色重复 + 循环质感（2026-10-03）

1）持续段松手到底进没进 Release —— 用**工程里那条真实资产**实测（overdrive，3.88 s）：

```
loop=0.503..0.623 R=0.300s S=1.00 intro=StopImmediately seek=False
2.58s entered=True   B 开始循环
4.07s entered=True   B pos=650（回绕，一遍走完）
--- Release at 4.07s, entered=True ---
4.57s 'Release · 100 ms · Gain 0.30'   A[pos=93803]   <- 确实进了 Release
5.07s finished
```

结论：逻辑是通的。松手后**会先走完当前这一遍循环**（这里一遍 0.45 s），
所以听感上像是"还在一遍遍循环"，实际是在等这一遍结束。
另外这条资产的 `ReleaseSeconds` 只有 `0.0707 s`，就算进了 Release 也几乎听不出收尾；
建议调到 `0.4~0.8 s`。

2）绿区间和绿线为什么同时存在：

- 绿**区间** = Release 尾巴（松手后实际会发声的那一段）。
- 绿**线** = `ReleasePosition01` 的拖动手柄。
- 之前无论开不开 `SeekToReleaseRegion` 都画手柄，于是和一个从 `LoopEnd01` 开始的绿区间
  同时出现，看着像两个 Release。
- 现在：`SeekToReleaseRegion` 关闭时不画绿色手柄（该字段此时不影响发声），
  开启时手柄正好是绿区间的左边界，也去掉了多余的独立目标线。

3）循环质感：继续挖 DSP Action 源码后补的两项

- 参考 `GameSynth.DSP/XShareableGrooveReader.cs`（双 reader + 窗口交叠）与
  `GameSynth.Data.Patches.Modular/GranularVoice.cs`（颗粒级 fade-in/out 窗口 + 每颗变化），
  以及 `Oscillator.cs`（循环本身只是硬回绕，质感靠外层变化）。
- 新增 `LoopRegionRandom01`：每次触发把整个循环窗口随机平移一小段（长度不变），
  量化成 16 档以免预渲染缓存爆掉。实测四次触发：
  `loopStart = 93247 / 92691 / 93247 / 93525`，不再是同一段重复。
- 新增 `SustainDriftDb` + `SustainDriftCutoffHz`：持续段每 0.3 s 抽一个新目标并平滑过去，
  做慢速随机音量/低通漂移。实测音量在 `1.000 → 0.963 → 0.952 → 0.990 → 0.945` 之间呼吸，
  不再是一条死循环。

### 真正的原因：相位边界用错了（2026-10-03）

之前"起段 / 持续段"的分界用的是**有没有进循环**，而循环起点在素材上往往很靠后：

```
overdrive 3.88s，LoopEnd01 ≈ 0.81 -> 循环起点约 3.16s
```

也就是说：**一次正常时长的滑铲（1~2 秒）松手时，循环根本还没开始**，
于是永远落进"起段"分支；如果 `IntroReleaseMode = StopImmediately`，声音就被直接停掉，
听起来就是"松手不进 Release"。这跟 Release 逻辑本身无关。

修复：相位边界改用 **ADSR 的 Attack + Decay**：

- 松手时 `heldTime < AttackSeconds + DecaySeconds` → 起段行为
  （`StopImmediately` 直接停 / `PlayFullOnce` 完整播一遍）。
- 否则 → 持续段行为：
  - 已经在循环里：等这一遍 loop 走完再进 Release。
  - 还没进循环：没有"这一遍 loop"可等，直接从当前位置进 Release。

实测（当前资产 A=0.01 / D=0.05，相位边界 0.06s）：

```
用例0 短按 0.5s  -> 'Release · 0 ms · Gain 1.00'
用例1 短按 0.59s -> 'Release · 0 ms · Gain 1.00'
用例2 按 1.59s   -> 'Release · 0 ms · Gain 1.00'（未进循环，直接收尾）
用例3 按 4.09s   -> entered=True '持续段松手 · 等这一遍循环走完' -> 完成
```

注意联动：现在"起段"有多长完全由 `AttackSeconds + DecaySeconds` 决定。
当前 A=0.01 意味着起段只有 6 ms，几乎每次松手都会走持续段分支。
想让"短按 = 直接停 / 完整播一遍"生效，就把 `AttackSeconds`（必要时连同 Decay）
调到和实际短按时长相当，例如 0.3~0.6 s。

### 结构确认：启动 → 保持段循环 → 松手播放尾音（2026-10-03）

承认前面把结构理解偏了。正确的三段结构是：

```
启动      ：素材开头那一段，只播一次
保持段循环：在 LoopStart01..LoopEnd01 之间循环，按住多久循环多久
尾音      ：LoopEnd01 到素材结尾，松手时播一次
```

之前的错误：松手进入 Release 时，我把播放头放回了 `LoopStart01`——
于是等于**把循环区又播了一遍**，根本听不到尾音。

修复：松手（并走完当前这一遍循环）后，播放头落在 `LoopEnd01` 附近，
直接从这里往素材结尾播，这段就是尾音。

实测（overdrive，186231 采样）：

```
loop = 93488..121660     尾音区 = 121660..186231
2.57s Release（在循环中，B.pos=2388）-> 等这一遍循环走完
3.07s 'Release · 0 ms · Gain 1.00'  A[pos=121660]   <- 从 LoopEnd 开始播尾音
```

两点联动提醒：

- `ReleaseSeconds` 决定尾音"淡出多长"，也就决定了实际能听到多长的尾音。
  当前 0.3 s，而这条素材的尾音区有 1.35 s；想完整听到尾音就把 R 调到 0.8~1.2 s。
- 循环起点由 `LoopEnd01` 决定（播放头越过它才回跳），所以 `LoopEnd01` 越靠前，
  "启动段"越短、越早进入循环。当前 `LoopEnd01 ≈ 0.65`（约 2.5 s 才进循环），
  想让滑铲一开始就进入循环，把它往前提。

### Release 长度为什么"看起来被固定"（2026-10-03）

实测（同一份素材，只改 `ReleaseSeconds`）：

```
尾音区 = 90937..186231 = 1.99s
R=0.30s 松手后 0.50s
R=1.00s 松手后 1.19s
R=2.00s 松手后 2.69s
R=4.00s 松手后 2.69s   <- 和 R=2.0 完全一样
```

公式：`松手后总时长 = min(ReleaseSeconds, 尾音素材长度) + 等这一遍循环走完的时间`

- `R <= 尾音长度`：R 完全起作用（0.3 / 1.0 两行成比例）。
- `R > 尾音长度`：声音会被**素材结尾**截断，再加 R 也不会更长——这就是"长度被固定"的原因。
- 另外那 0.2~0.7 s 的固定附加量，是"走完当前这一遍循环"的等待时间。

所以要让尾音更长，只能改素材侧：

1. 把 `LoopEnd01` 往前提（尾音区 = `LoopEnd01 → 素材结尾`，越靠前尾音区越长）。
2. 或者换一段尾巴更长的素材。

Inspector 里新增了一行提示与警告：

```
尾音素材 1.99s · Release 4.00s · 实际 1.99s
[!] Release 4.00s 比尾音素材 1.99s 还长，多出来的部分没有声音，
    实际长度被限制在 1.99s。想把尾音做长：把 LoopEnd01 往前提……
```

### 最终定案：Release 时长 = 绿色区间长度（2026-10-03）

上一版把 Release 当成一个独立可调的秒数，才会出现"被截断 / 看起来固定"这些怪现象。
正确做法就是你说的：**Release 时长 = 尾音区间的长度**。

改动：

- `AdsrEnvelope` 删除 `ReleaseSeconds` 字段。
  `EvaluateRelease(timeSinceRelease, startLevel, duration)` 的 duration 由尾音区间给出。
  现在 ADSR 只剩 A / D / S 三个参数（S 为电平），R 由区域决定。
- 松手时按实际落点算时长：
  `releaseDuration = (素材总采样数 - 播放头位置) / 采样率`，
  也就是"从尾音起点到素材结尾"那一段的长度。
- ADSR 面板里的 R 段宽度直接画成尾音区间的长度，标签写成
  `R xxxx ms（尾音区）`，不再是独立输入。
- 试听状态栏显示 `Release · 已播 / 总长 ms`，可以直接看到总长就是尾音区间长度。
- 试听与运行时同步修改。

实测（overdrive，尾音区 = 73374..186231）：

```
尾音长度 = 2.35s
Release at 3.27s -> A[pos=73140]（尾音起点）
3.57s 'Release ·    0 / 2356 ms · Gain 1.00'
4.07s 'Release ·  497 / 2356 ms · Gain 0.49'
4.57s 'Release ·  997 / 2356 ms · Gain 0.19'
5.07s 'Release · 1495 / 2356 ms · Gain 0.05'
5.56s 'Release · 1991 / 2356 ms · Gain 0.00'
```

`2356 ms` 正好是尾音区间长度，且淡出形状由 `ReleaseCurve` 决定（默认 Fast，
所以前段掉得快；想均匀一点改成 Linear / Slow）。

另外：那 0.2~0.7 s 的附加时间是"走完当前这一遍循环"的等待，属于设计而非 bug。

### 三段边界定案：启动段 = Start → LoopStart（2026-10-03）

前两版边界都错：

- 用 ADSR 的 Attack+Decay 当边界 —— 当前 `A=0.01`，边界只有 6 ms，
  于是 `IntroReleaseMode` 的两个选项永远用不到。
- 更早用"有没有进循环"当边界，但那时**启动段跑到了 LoopEnd**（2.5 s），
  一次正常滑铲根本进不了循环，于是永远走"起段"分支被停掉。

正确的三段应该是：

```
启动段   ：StartPosition01 → LoopStart01   （只播一次，长度由 LoopStart01 决定）
保持段   ：LoopStart01 → LoopEnd01         （循环）
尾音     ：LoopEnd01 → 素材结尾             （松手时播一次，长度 = release 时长）
```

配套改动：

- `ActionClipRenderer` 的循环片段改成**入口为 x[LoopStart]**：
  `B[j] = x[LoopStart+j]`（前段），再在尾部做交叠，把 x[LoopEnd-xf..LoopEnd)
  淡入到 x[LoopStart..LoopStart+xf)。这样"启动段结束于 LoopStart → 循环从 LoopStart 开始"
  是连续的，实测首采样差值 `0.000000`。
- 启动段长度改为 `LoopStart01 - StartPosition01`（不再跑到 LoopEnd）。
- 相位边界改回"有没有进循环"，但这次启动段是短的，两个选项才有意义。
- 松手时在保持段：等这一遍循环走完（播放头回绕）→ 从 `LoopEnd01` 播尾音。
  release 时长 = 尾音区间长度。

实测（LoopStart01=0.1 / LoopEnd01=0.3）：

```
启动段=0..18623（0.39s） 循环=18623..55869 尾音=55869..186231（2.72s）
循环片段首采样与素材 LoopStart 处差值 = 0.000000

用例0 短按 0.2s + 立即停   -> 立刻停
用例1 短按 0.2s + 完整播一遍 -> '起段松手 · 完整播一遍'，播完 3.88s 整段
用例2 按住 1.5s 松手       -> '等这一遍循环走完' -> 尾音 2.72s
```

代价说明：入口定在 LoopStart 之后，接缝靠"尾部交叠"处理，
所以每一圈会重复一小段头部（长度 = `LoopSeamFadeSeconds`）。
默认 20 ms 基本听不出来；把它调大会更平滑但重复段更长，按素材取舍。

### 图例统一术语（2026-10-03）

区域条和实现本来就是"蓝色区间 = 启动段"，但图例写成了"首遍"，
读起来像另一套概念。统一成实际术语：

```
裁掉 = 0 → StartPosition01
启动 = StartPosition01 → LoopStart01     只播一次；松手行为"立即停/完整播一遍"就作用在这一段
保持段循环 = LoopStart01 → LoopEnd01      循环
尾音 = LoopEnd01 → 素材结尾               松手时播一次，长度 = release 时长
```

悬停提示也改成带秒数的区间说明，例如：
`启动段：0.000..0.100（0.39s，只播一次）`、
`尾音：0.300..1.000（2.72s，松手时播一次，长度就是 release 时长）`。

### 循环区处理：参考 GameSynth 的做法（2026-10-03）

重新翻了原 DSP Action / GameSynth 源码，和循环相关的主要有这些：

| 源码 | 做法 | 是否已采用 |
| --- | --- | --- |
| `GameSynth.DSP/XShareableGrooveReader.cs` | 双 reader + Blackman 窗口交叠，`compensation_win` 归一化 | 已采用（预渲染交叠循环 + AudioSource.loop） |
| `GameSynth.DSP/Oscillator.cs` | 循环本身只是回绕 + 线性插值 | 说明"质感靠外层变化" |
| `Modular/GranularPlayer.cs`、`GranularVoice.cs` | 颗粒参数：`Start` / `Duration` / **`Fading Duration`(0~1000ms)** / **`Fading Curve`(None/Linear/Slow/Fast/S-Curve)** / Loop / SnapToMarkers；每颗自带淡入淡出窗 | 本次新增的漂移/抖动属于这一类思路 |
| `Modular/Looper.cs` | 录一段再重复 + 衰减 | 与本需求无关 |
| `tsugiDSP/Correlation.cs` | 自相关（用于 LPC 音高检测），**没有**现成的循环点搜索 | 因此自己实现了 NCC 循环点搜索 |

新增"互相关找循环点"按钮（`AudioActionClipAnalysis.TryFindSeamlessLoopRegion`）：

- 在素材里搜一对循环点，让 `LoopEnd` 前的波形与 `LoopStart` 前的波形尽量一致。
- 评分为三项乘积：**电平匹配（RMS 接近）** × **接缝相对跳变（越小越好）** ×
  **波形相关加分项**；循环长度限制在 `0.25~1.2 s`，搜索量封顶 400×40 组合。
- 实测中发现的关键点：**噪声型素材上波形相关几乎无效**
  （`slide_tackle_1` 的相关分只有 `0.01`，会挑出接近整段长的循环）。
  所以把相关降级为加分项，电平匹配与接缝跳变做主判据。

实测对比（`slide_tackle_1`，2.92 s，峰值 0.804）：

```
手工循环 0.385..0.664   接缝跳变 0.0927（峰值的 11.5%）
互相关找到 0.254..0.357  接缝跳变 0.0331（4.1%）score 0.62 长度 0.30s
搜索耗时 46ms
```

### 颗粒持续模式（Granular）（2026-10-03）

按 GameSynth `GranularPlayer` / `GranularVoice` 的思路新增第三种持续方式，
用来**从根上消掉接缝**。

做法（和我们已有的"预渲染 + AudioSource.loop"架构保持一致，因此仍是采样级精度）：

1. 在循环区里铺一串颗粒，每颗：
   - 读取起点在循环区内随机（`GrainRandomStart01`）；
   - 自带淡入淡出窗（`GrainSeconds` 决定长度，窗占 45%，形状由 `GrainFadeCurve` 决定）；
   - 相邻颗粒间隔 `GrainSpacingSeconds`，小于颗粒长度即重叠。
2. 把颗粒重叠相加成一条固定长度 buffer；**颗粒尾巴越过末尾时回写到开头**，
   所以 buffer 首尾其实是同一颗颗粒的两半 —— 交给 `AudioSource.loop` 就没有接缝。
3. 重叠相加会抬高电平，按峰值归一化到 0.95，避免削顶。
4. buffer 按 `(clip, 循环点, 颗粒长度, 间隔, 随机量, 曲线, seed)` 缓存；
   每次触发用不同 seed，所以每一遍滑铲的颗粒排布都不一样。

新增字段：

```
GrainSeconds       颗粒时长（默认 0.18s）
GrainSpacingSeconds 相邻间隔（默认 0.07s → 重叠 0.11s）
GrainRandomStart01 读取起点随机范围（默认 1.0）
GrainFadeCurve     颗粒窗曲线（默认 SCurve）
LoopMode = Granular 时启用
```

实测（`slide_tackle_1`，循环 74855..97971）：

```
交叠循环: samples=20428  接缝跳变=0.187195
颗粒循环: samples=67200  接缝跳变=0.005723   <- 小 33 倍
  （1.40s，颗粒 180ms / 间隔 70ms）
不同种子是否不同: 是（不同）
颗粒缓冲 RMS 波动 0.067；首 20ms RMS 0.1651 / 末 20ms 0.1610  <- 首尾电平连续
```

接缝跳变从 0.187 掉到 0.0057，而且首尾电平几乎一致，说明循环点处既没有波形跳变、
也没有电平突变 —— 对这类噪声型摩擦/滑铲素材比强行找一个"能循环的点"更合适。

松手行为：颗粒模式与 `SustainLoop` 一致 —— 走完当前一遍 buffer 后，
从 `LoopEnd01` 播尾音区；`LoopMode` 设为 `ContinuousLoop` 时才会连尾音一起循环。

#### 颗粒模式的"电子味/失真"修复（同一天）

现象：颗粒模式接缝跳变小了，但听起来有金属/电子味的失真。

查证：

- Comb filter（Wikipedia）："a comb filter is a filter implemented by
  **adding a delayed version of a signal to itself**"。
  颗粒重叠如果把**同一段素材**以不同偏移量相加，就正好是这个结构 —— 产生梳状滤波，
  听感就是金属/电子味、发闷或发尖。
- Granular synthesis（Wikipedia）：颗粒 "may play at **different speeds, phases,
  volume, and frequency**"。也就是说，颗粒之间本来就应该有速度/频率差异，
  这样叠加时才不会形成固定的相位关系。

修复：

- 新增 `GrainTuneCents`（默认 25 音分，0~100）：
  每颗颗粒随机失谐一点点，并用分数步长做线性插值读取，
  把重叠颗粒之间的相位关系打散，梳状滤波随之消失。
- 实测（同一 seed，只改失谐）：
  ```
  失谐  0 音分: 接缝跳变=0.132940  小滞后自相关=0.090
  失谐 25 音分: 接缝跳变=0.037299  小滞后自相关=0.093
  ```
  接缝跳变降到约 1/3.6。

另外两处顺带修正：

- 颗粒 buffer 长度目标从 1.4s 拉到 **2.5s**，颗粒排布的重复周期更长，
  不容易听出"这段又来了"。
- 之前每次触发都用新 seed，会为每条 buffer 新建一份 AudioClip 并缓存，
  长时间游戏会不断吃内存。现在把 seed 量化成 **8 个变体**，
  并给缓存加了 64 条上限（超出整体清空），内存有界。

如果还觉得有金属味，优先调这三个：把 `GrainTuneCents` 加到 50~80、
把 `GrainSpacingSeconds` 调大（减少重叠量）、把 `GrainRandomStart01` 保持 1。

#### 颗粒模式四点修正（同一天）

1）`GrainTuneCents` 上限 `100 → 500` 音分。
   实测高处确实更能压掉电子音/异常高频（相位打散更彻底）。
   注意 ±500 音分接近 ±5 个半音，颗粒之间会有明显音高差，
   这是拿"稳定音高"换"没有金属味"，按素材取舍。

2）`GrainSpacingSeconds` 远小于 `GrainSeconds` 时听感更好（重叠多、更绵密）。
   当前工程里是 `180ms / 20ms`，重叠 160ms、颗粒率 50 Hz。

3）**sustain 响度比其它段高** —— 原因是旧实现只按峰值归一化。
   重叠越多，重叠相加把电平抬得越高，峰值归一化管不住 RMS，于是 sustain 明显更响。
   改成**按 RMS 对齐**：把颗粒缓冲的 RMS 压到与循环区素材自身 RMS 一致，
   再检查峰值不削顶。实测：

   ```
   素材循环区 RMS=0.1524
   颗粒缓冲   RMS=0.1609（≈ +0.5 dB）峰值=0.8055
   ```

   这样启动段 / 保持段 / 尾音三段响度是连续的，重叠量怎么改都不会再让 sustain 凸出来。

4）**loop 次数异常增多** —— 找到原因：
   颗粒 buffer 长度是 2.5 s 量级（颗粒多、铺得长），
   而松手后的"等这一遍走完"原来等的是**整个 buffer 周期**，
   于是松手后还会循环好几秒才进尾音，次数自然比交叠模式（0.7 s 周期）多得多。

   修正：**颗粒模式松手直接进尾音**，不等 buffer 周期。
   理由是颗粒本身是密集纹理，切在任意位置都不存在"半句"问题，
   而刚性循环才有必要凑周期边界。

   实测：`2.00s 松手 -> 'Release · 0 ms'`，立即开始尾音。

### 参数不会自动保存（2026-10-03）

`AudioActionDefinition` 是 ScriptableObject：在 Inspector 改字段只会把对象标记为 dirty，
**不会立刻写盘**。要落盘必须 `Ctrl+S`（File > Save Project），
或者在 Unity 提示"save changes"时选择保存。

检查磁盘时发现工程里那条资产当时的状态是：

```
AudioActionDefinition.asset  LastWriteTime = 20:29:35
LoopStart01: 0.5068763   LoopEnd01: 0.65815336   LoopMode: 2
（文件里没有 GrainSeconds / GrainSpacingSeconds / GrainTuneCents / GrainRandomStart01）
```

也就是说当时调的循环点、颗粒参数、失谐全都只在内存里，没有写进文件。

为避免误丢，Inspector 顶部加了"保存资产"按钮：
`EditorUtility.SetDirty(definition)` + `AssetDatabase.SaveAssetIfDirty(definition)`，
等价于只保存这一条资产（不用等 Ctrl+S 存整个工程）。

另外注意：离线渲染出来的交叠/颗粒循环片段是**运行时缓存**，不写进资产，
每次改参数或换素材都会按新参数重建，不需要、也不应该保存。

## 调试覆盖层

- `AudioDebugOverlay` 显示：
  - 当前音乐。
  - 速度比。
  - SFX 数量。
  - 连续动作状态。
  - 动作时间。
  - 归一化速度。
  - 当前低通频率。
  - Gain / Pitch Follow / Lowpass Follow 实时条形值。
- 使用现有 `PlayerInputReader.ToggleHUD` 事件切换，和 F3 HUD 同步。

## 创建音频库

编辑器菜单：

```text
超高速行者/音频/创建音频库和玩家映射
```

会弹出保存位置选择框，由项目自行决定 `GameAudioCatalog` 和 `PlayerAudioProfile` 放在哪里。

## 验证

- Unity 2022.3.33f1 脚本编译：通过。
- PlayMode 音频过滤测试：`7/7` 通过。
- 覆盖：
  - 曲线映射。
  - 对数频率插值。
  - 包络端点。
  - 单音效动作区域修正。
  - 随机区间。
  - 无素材 Cue 的降级。
  - 创建并停止单音效连续动作。

完整 PlayMode 套件仍有分支原有的场景/预制体装配失败，例如动画绑定缺失、场景玩家相机未装配、测试 Player 缺少 `MovementParams`。这些不是本次音频系统的编译错误。

## 未做

- 未 commit。
- 未决定最终音频库保存位置。
- 未把暂存素材自动分配到 Catalog/Profile；需要项目根据自己的分类和动作语义手动绑定。

---

## 2026-10-04 修正

### 保存资产按钮重叠

Toolbar 会吃掉整行宽度，两个按钮被压在右边导致重叠。
改成 Toolbar 独占一行，按钮另起一行右对齐（`GUILayout.FlexibleSpace()`）。

### 新增"Loop 段音量"滑槽

`AudioActionDefinition.SustainGainDb`，范围 `-24 ~ +6 dB`，默认 0。

- 作用范围：**只有保持段循环期间**（`_loopEntered && !_released`）。
  启动段、尾音、Release 阶段都不受影响。
- 位置：Inspector"采样回放区域"面板里，和"交叠时间"并排；
  默认 Inspector 里也能直接改。
- 实测 `-12 dB`：保持段音量 `0.2441`，未减益时 `0.9717`，
  比值 `0.2512` 正好等于 -12 dB。

### 颗粒模式下起段松手选项失效

原因：把"颗粒模式 → 直接进尾音"的短路判断写在了**启动段判断之前**，
所以颗粒模式下不论在不在启动段，都会跳过 `StopImmediately / PlayFullOnce`。

修正后的判断顺序：

```
未进循环（启动段）      -> IntroReleaseMode：StopImmediately / PlayFullOnce
已进循环 + 颗粒模式      -> 直接进尾音（不等 buffer 周期）
已进循环 + 刚性循环      -> 等这一遍 loop 走完再进尾音
```

实测（启动段 0..0.29s，`LoopMode=Granular`）：

```
用例0 短按 0.15s + 立即停    -> 立即停
用例1 短按 0.15s + 完整播一遍 -> '起段松手 · 完整播一遍'，播完整段
用例2 保持段 -12dB            -> 保持段音量 0.2441
```

### 滑铲音效最省事绑定方式：直连（2026-10-04）

排查现状：

- 按 GUID 搜过全部 9 个场景，**没有任何场景放了 `GameAudioInstaller`**。
- 工程里也**没有 `PlayerAudioProfile` / `GameAudioCatalog` 资产**（只有几条 `AudioActionDefinition`）。
- 结论：`AudioSystem.Profile` 一直是 null，`PlayerAudioDriver.Update` 第一行就 return，
  所以滑铲从来没被接上。

驱动侧本来就不用管：`AudioSystem.FindPlayer()` 会在 0.5 s 后自动给 `PlayerMotor`
所在物体挂上 `PlayerAudioDriver`，而它读的是 `PlayerMotor.IsSliding`
（上升沿 `PlayAction`，下降沿 `handle.Stop()` → 进 Release）。

所以缺的只是"声音资产怎么传进去"。新增**直连**路径，不需要 Catalog / Profile / 场景组件：

- `PlayerAudioDriver` 增加两个可直接拖的字段：
  ```
  Slide Action       <- 拖 AudioActionDefinition
  Wall Slide Action  <- 可选
  ```
- 没有 `PlayerAudioProfile` 时，驱动内部建一条运行时兜底 Profile，
  只填这两个直连动作；其余音效（脚步/战斗等）保持为空、不发声，不会报错。
- `Update` 的守卫从"必须有 Profile"改成"有 Profile 或者有直连绑定"。

验证：

```
无 AudioSystem 时 Profile=null
AudioSystem 无 Profile 时，兜底 Profile 的 SlideAction 绑定正确=True
资产 Clip=slide_tackle_1  动作声源池=4
用该资产直接 PlayAction 成功=True（handle=ok）
```

使用步骤（一次即可）：

1. 选中场景里的 Player（或 Player 预制体）。
2. Add Component → `PlayerAudioDriver`（若还没有）。
3. 把 `AudioActionDefinition` 资产拖到 **Slide Action**。
4. Play，滑铲即可听到 启动 → 保持段循环 → 松手尾音。

以后要接完整音效（脚步材质、战斗、音乐）时，再走
`超高速行者/音频/创建音频库和玩家映射` 生成 Catalog + Profile，
并在场景里放一个 `GameAudioInstaller`；有 Profile 时直连字段会被 Profile 覆盖。

---

## 2026-10-06

### 修 bug：颗粒模式缓存爆炸（每次滑铲重渲染 27ms）

现象：颗粒模式下几乎每次触发都要重新渲染循环 buffer（约 25~27ms，等于 1.6 帧卡顿）。

根因：`ActionClipRenderer` 的缓存键包含 `(素材, LoopStart, LoopEnd, 交叠, 颗粒长度,
间隔, 随机起点, 失谐, 曲线, seed)`，其中 seed 量化成 8 档。而
`ApplyLoopRegionJitter()`（`LoopRegionRandom01`，默认 0.05）会**每次触发**把整个
循环窗口平移，量化成约 17 档。于是单个定义最多
`17 档区域 × 8 seed = 136` 条缓存，超过 64 条上限后 `Cache.Clear()` 被反复触发，
缓存基本失效。

修复：**颗粒模式不再做区域抖动**（`ActionAudioVoice.ApplyLoopRegionJitter` 与
`AudioActionPreviewPlayer.ApplyLoopRegionJitter` 各加一行早退）。

理由：颗粒渲染本身已经在循环区内随机取每颗的读取起点（`GrainRandomStart01`），
再平移整个窗口几乎不增加听感差异，却把缓存键炸了 17 倍。

实测（24 次触发，8 个 seed 变体）：

```
循环窗口固定：loopStart=66048 loopEnd=93568
24 次触发 → 渲染 7 次（共 172ms，平均 24.5ms），缓存命中 17 次
```

即每个定义最多渲染 8 次，之后永久命中；开销不再随触发次数增长。

### 顺带：颗粒渲染本身的性能优化（已改，未单独提交）

首次渲染 91ms → 27ms（约 3.4 倍），改了三处：

1. **颗粒窗预计算成表** —— 原来每个采样都调一次 `AudioCurveUtility.Map`，
   SCurve 内含 `Mathf.Sin`，一轮 100 多万次。
2. **去掉逐采样的整数取模与 `WrapIntoLoop()` 调用** —— 读指针改增量、
   写指针越界归零、源帧越界只减一次（`tuneRatio` 恒在 1 附近，不会跨多圈）。
3. **素材采样解码缓存**（`SourceCache`）—— 不再每次渲染都 `GetData` 整条素材；
   与渲染缓存一起在超过上限时清空，避免长期占内存。

注意：这仍然只是"把每次渲染变便宜"，**没有做预热**。如果要彻底消除前几次滑铲的
一次性卡顿（每个定义前 8 次），需要按定义分帧预热 —— 这一项按你的要求暂不做。

### 清理：只保留动态连续动作音效（2026-10-06）

目标：这一套只负责"动态连续动作音效"，其余（一次性音效、脚步材质、战斗音效、
主题曲、风声、故障效果）后续用 Wwise 重做。

**删除的代码（9 个文件，含 meta）**

```
Assets/Scripts/Audio/AudioSfxVoice.cs        一次性音效播放器 / 对象池成员
Assets/Scripts/Audio/AudioGlitchFilters.cs   Stutter / Bitcrush，只服务 SFX 故障效果
Assets/Scripts/Audio/MusicDirector.cs        主题曲交叉淡化
Assets/Scripts/Audio/AudioCueDefinition.cs   一次性音效资产定义
Assets/Scripts/Audio/AudioSurface.cs         脚步材质路由
Assets/Scripts/Audio/GameAudioCatalog.cs     语义 Id 库 + Music/SpeedLayer
Assets/Scripts/Audio/GameAudioInstaller.cs   把 Catalog/Profile 注入 AudioSystem
Assets/Scripts/Audio/PlayerAudioProfile.cs   玩家音效映射（改为只用直连字段）
Assets/Editor/AudioCatalogWizard.cs          生成 Catalog/Profile 的菜单
```

**保留并瘦身**

- `AudioSystem.cs`：258 → 145 行。去掉 SFX 池、音乐、风声、glitch、`Configure`；
  只留动作声源池、`PlayAction`、`TickActions`、`FindPlayer`（自动挂
  `PlayerAudioDriver`）、`ToggleDebug`。
- `PlayerAudioDriver.cs`：230 → 122 行。只留滑铲 / 墙滑；
  去掉攻击、脚步、跳跃、命中、hitstop glitch、传送 glitch、地面材质射线。
  `ComputeFrame` 不再需要 `AudioCueDefinition` 参数（`SurfaceId` 暂时留空字符串，
  给后续 Wwise 材质路由留位置）。
- `GameAudio.cs`：只剩 `IsReady` 和 `PlayAction`。
- `AudioDebugOverlay.cs`：去掉 Music / Active SFX / Speed ratio 三行，
  保留 "Continuous actions" 整块（状态、动作时间、速度、低通、以及 Gain /
  Pitch Follow / Lowpass Follow 三条实时条形）。
- `Assets/Tests/PlayMode/AudioSystemTests.cs`：删掉 `AudioCueDefinition` 用例，
  以及 `AudioSystem_StartsAndStopsSingleClipAction` 里对
  `PlayerAudioProfile` / `GameAudioCatalog` / `Configure` 的依赖。

**完全没动**

- 连续动作核心：`AudioActionDefinition`、`AdsrEnvelope`、`AudioEnvelope`、
  `AudioActionTypes`、`ActionClipRenderer`、`AudioActionVoice`、`AudioActionHandle`。
- 编辑器工具：`AudioActionDefinitionEditor`、`AudioActionPreviewPlayer`、
  `AudioActionClipAnalysis`。
- **所有音频素材**（`Assets/AudioCollection/` 一个文件都没删），
  Wwise 阶段继续复用。
- `PlayerAudioDriver` 的字段名 `_slideAction` / `_wallSlideAction` 保持原样，
  所以 `Player.prefab` 上已绑定的 `Slide.asset` / `WallSlide.asset` 不受影响。

**验证**

- 全仓库已无对上述 9 个类型的引用；场景 / prefab / 资产里也没有指向它们 GUID 的
  悬空引用（逐个 GUID 搜过）。
- 批量模式编译：无 CS 错误。`GameJam.Runtime.dll` 重建后 185344 → 172032 字节，
  用二进制检索确认 9 个类型已从程序集消失，5 个核心类型仍在。

## 2026-10-06：Wwise 原生 Source Plugin 纵向切片

环境：

```text
Wwise Authoring / SDK / Unity Integration: 2024.1.17.9170
Unity: 2022.3.33f1
Visual Studio Build Tools 2022: MSVC v143
```

项目内新增原生插件工程：

```text
spotlight-2026_WwiseProject/Plugins/SpotlightActionSource/
  Runtime/                          Sound Engine 源插件
  Authoring/                        Wwise Authoring 插件和 XML
  Dynamic/                          动态插件 DLL 注册
  Tests/                            离线 DSP 对比程序
  CMakeLists.txt
```

插件身份：

```text
CompanyID: 0
PluginID: 4242
ClassID: 278003714
容器名: SpotlightActionSource
Wwise 名称: Wwise Spotlight Action Source
```

### 最小 Source Plugin

- 已实现 `IAkSourcePlugin` 和 `IAkPluginParam`。
- 初始验证版输出正弦 PCM，现已替换成真实 WAV 采样读取和动作状态机。
- 插件不依赖 MFC，Authoring DLL 和 Runtime DLL 都只依赖 `KERNEL32.dll`。
- 已通过 `ak.wwise.core.object.create` 和 `pluginInfo.json` 验证注册、Bank
  引用与 Bus 路由。

踩坑：

- Wwise XML 中的空 `UserInterface` 元素必须写成 `<UserInterface ... />`。
  截图中的 `<UserInterface ...></UserInterface>` 会让 WwiseConsole 在加载插件时卡住。
- 自定义 Source 插件需要同时安装 Authoring DLL/XML 和 Runtime DLL，缺一不可。

### 离线 DSP 核心

新增 `Runtime/Dsp/SpotlightActionDsp.*`：

- 标准 ADSR：A/D/R 为时间，S 为电平。
- 启动段、保持段、尾音的采样区间模型。
- 交叠淡化循环。
- 颗粒循环、随机起点、随机失谐、颗粒窗、RMS 响度对齐、峰值保护。
- 与 C# 相同公式的对比测试程序。

自动对比脚本：

```text
Tools/audio/build_spotlight_action_source.ps1
Tools/audio/run_dsp_parity.ps1
```

实测对比结果：

```text
adsr_hold_diff    max_abs=1.19209e-07  rms_diff=7.45389e-09
adsr_release_diff max_abs=0            rms_diff=0
crossfade_diff    max_abs=0.000107378  rms_diff=8.8363e-07

granular native    frames=120960 rms=0.365207 peak=0.95 seam=0.0137348
granular reference frames=120960 rms=0.355503 peak=0.95 seam=0.0158321
granular delta     frames=0 rms=0.00970472 peak=5.96046e-08 seam=-0.00209731
```

### Wwise 工程对象

已创建：

```text
Bus:        Master Audio Bus/DynamicAction
ActorMixer: DynamicAction
Sound:      SlideSinePlugin
Source:     SpotlightActionSource
Event:      Play_SlideSinePlugin
Event:      Stop_SlideSinePlugin
GameParam:  ActionSpeed
RTPC:       ActionSpeed -> SpotlightActionSource.PitchSemitones (0..+2 st)
Bank:       DynamicAction
```

生成结果：

```text
Assets/StreamingAssets/Audio/GeneratedSoundBanks/Windows/DynamicAction.bnk
Assets/StreamingAssets/Audio/GeneratedSoundBanks/Windows/Init.bnk
Assets/StreamingAssets/Audio/GeneratedSoundBanks/Windows/PluginInfo.json
```

### Unity 绑定

- `WwiseActionBindings`：把 `AudioActionDefinition` 映射到 Wwise Event / RTPC。
- `WwiseActionDriver`：由 `AudioSystem` 自动挂到玩家，不修改预制体字段。
- 自动加载 `DynamicAction` Bank。
- 滑铲上升沿发送 `Play_SlideSinePlugin`。
- 滑铲持续期间给 `ActionSpeed` 写速度。
- 滑铲下降沿发送 `Stop_SlideSinePlugin`。
- 发送 Play Event 前，通过 `SendPluginCustomGameData` 把真实 WAV 字节发给插件。

这次只给 `PlayerAudioDriver` 增加了公开只读属性 `SlideAction`，
没有改动战斗、动画、Character、模型或现有 `_slideAction` 字段名。

### 验证

PlayMode 烟测日志：

```text
WwiseSourcePluginTests: engine initialized
WwiseSourcePluginTests: DynamicAction loaded, pluginRegistered=True
WwiseSourcePluginTests: custom WAV bytes 1587880
WwiseSourcePluginTests: play posted 1
WwiseSourcePluginTests: stop posted 2
```

说明：

- Wwise 初始化成功。
- `DynamicAction` Bank 加载成功。
- 自定义 Source 插件已注册。
- Unity 发送的真实 `slide_tackle_2.wav` 媒体字节数为 1,587,880，插件成功接收。
- Play / Stop Event 都已成功返回 playing ID。

### 真实采样和动作状态机

- Runtime 插件通过 `GetPluginCustomGameData` 接收 WAV，不要求把媒体嵌入 Bank。
- 支持 Wave PCM 8/16/24/32-bit 和 IEEE float 32/64-bit。
- 立体声素材在插件内下混为单声道，再交给 Wwise Bus、效果器和空间化。
- 播放状态机：
  ```text
  StartPosition01 -> LoopStart01
  LoopStart01     -> LoopEnd01
  LoopEnd01       -> 文件结尾
  ```
- 保持段支持 Sustain Loop、Continuous Loop 和 Granular。
- 包络使用标准 ADSR，Release 时长等于尾音区间长度。
- Slide 资产的参数已写入插件 XML 默认值：
  ```text
  StartPosition01=0
  LoopStart01=0.1394892
  LoopEnd01=0.26719058
  LoopMode=Granular
  GrainSeconds=0.18
  GrainSpacingSeconds=0.02
  GrainRandomStart01=1
  GrainTuneCents=341
  SustainGainDb=-3.9
  ADSR=(0.01, 0.05, 1.0)
  ```

当前保留：

- Unity Inspector 仍是参数创作入口；当前滑铲参数通过插件 XML 默认值和绑定资产同步。
  后续可以继续做 `AudioActionDefinition -> Wwise preset` 的自动导出。
- Wwise 插件媒体导入能力也已实现，但当前运行路径优先使用 Unity
  `SendPluginCustomGameData` 发送的 WAV 字节。
- `SendPluginCustomGameData` 按 game object + plugin ID 存储。当前只有一个滑铲实例时
  没问题；后续同角色同时存在多个同类动态音效时，应拆分 plugin ID/事件，或切回插件媒体。
- 本轮未 commit，未修改 `README.md`、战斗、Character、动画和模型。
- 新增忽略规则：Wwise Launcher 的临时 zip / `logRunSetup.txt`，以及 Wwise 工程内的
  `GeneratedSoundBanks/`。Unity 实际运行时使用的 Bank 仍提交在
  `Assets/StreamingAssets/Audio/GeneratedSoundBanks/`。
- Unity 集成自动把 `WwiseGlobal` / `AkAudioListener` 写入 `TestScene`，替换了原来的
  Unity `AudioListener`；这是 Wwise 运行初始化所必需。

## 2026-10-06：远程干净副本上的 PCM 搬运重构

这次不再在旧脏工作区上继续叠加，而是重新从远程分支建立独立副本：

```text
远程分支: origin/feat/audio_wwise
基线提交: a5b1d23
工作副本: D:\TapTapGameJam26\spotlight-2026-remote
```

原工作区 `D:\TapTapGameJam26\spotlight-2026` 保留不动；新副本没有 commit，
并继续遵守不修改 `README.md`、战斗、Character、动画和模型的约束。

### 职责重新拆分

```text
Unity：
- 动作状态机输入
- AudioActionDefinition 参数
- ADSR
- 启动段 / 循环 / 颗粒 / 随机差分
- 全部 DSP 和 PCM 渲染

Wwise Source Plugin：
- 只从 ring buffer 读取 PCM
- 只把 PCM 交给 Wwise Bus / 效果器 / Master
- 不解析 WAV、不实现 ADSR、不实现循环和颗粒
```

### 不依赖 Unity 音频解码

工程启用了 Wwise 后，Unity 音频输出被关闭：

```text
ProjectSettings/AudioManager.asset
m_DisableAudio: 1
```

在这个状态下，`AudioClip.GetData()` 也会失败。因此新增：

```text
Assets/Scripts/Audio/WwiseActionPcmSource.cs
```

它直接解析 `StreamingAssets` 下的 WAV（PCM 8/16/24/32-bit、IEEE float
32/64-bit），并把采样交给 `WwiseActionPcmRenderer`。素材路径来自
`WwiseActionBindings.Entry.SourceRelativePath`，驱动逻辑里没有硬编码
具体音效库或材质分类。

同时，`ActionClipRenderer` 增加纯 `float[]` 接口：

```text
GetCrossfadeLoopPcm(...)
GetGranularLoopPcm(...)
```

AudioClip 预览路径继续保留原有接口；Wwise 路径不再创建 `AudioClip`。

### 验证结果

真实 `Slide.asset` + `slide_tackle_2.wav` 的批处理烟测通过：

```text
Wwise PCM bridge smoke test passed: playingId=1
```

测试使用：

```powershell
Unity.exe -batchmode -nographics `
  -projectPath "D:\TapTapGameJam26\spotlight-2026-remote" `
  -wwiseEnableWithNoGraphics `
  -executeMethod WwisePcmBridgeSmokeTest.Run `
  -quit `
  -logFile "...\Logs\pcm-bridge-slide-smoke-3.log"
```

本次仍未 commit。

## 2026-10-06：动态动作源显式注册

- 新增 `WwiseAudioRegistry`。
- `PlayerDynamicAudioActionSource` 在 `OnEnable/OnDisable` 注册和注销。
- `WwiseActionDriver` 不再扫描全部 MonoBehaviour。
- Driver 只在 registry version 变化时刷新 source，并按层级过滤：
  ```text
  当前 Player GameObject
  或它的子物体
  ```
- 支持多 Player，以及运行时动态挂载新的动态动作源。

文档：

```text
Docs/Audio/DynamicAudioActionInterface.md
```

本次仍未 commit。

## 2026-10-06：Wwise Bootstrap 与原生 DSP 清理

- 新增 `WwiseAudioBootstrap`，只负责：
  - 给 Player 自动挂载 `WwiseActionDriver`
  - 创建 F3 `AudioDebugOverlay`
  - 绑定 `PlayerInputReader.ToggleHUD`
- 删除旧的 `AudioSystemBootstrap`，旧 `AudioSystem` 不再被运行时自动创建。
- `AudioDebugOverlay` 删除旧 `ActionAudioVoice` 展示，只显示 Wwise 动态通道。
- 删除原生 `SpotlightActionDsp` 静态库和测试 target。
- 删除：
  ```text
  Runtime/Dsp/SpotlightActionDsp.h
  Runtime/Dsp/SpotlightActionDsp.cpp
  Tests/SpotlightActionDspTests.cpp
  Assets/Editor/Audio/SpotlightActionDspReferenceExporter.cs
  Tools/audio/run_dsp_parity.ps1
  ```
- Runtime / Authoring 插件继续只使用 `PcmVoiceBridge`。

验证：

```text
Unity recompile: 0 error, 0 warning
SpotlightActionSource / Authoring / Runtime CMake build: success
```

本次仍未 commit。

## 2026-10-06：Wwise 一次性事件桥接

新增：

```text
Assets/Scripts/Audio/WwiseEventBindings.cs
Assets/Scripts/Audio/WwiseEventBridge.cs
Assets/Editor/Audio/WwiseEventBindingSetup.cs
```

用途：

- 一次性动作不进入动态 PCM 系统。
- gameplay 只调用逻辑 `EventId`。
- `WwiseEventBindings.asset` 负责映射 `AK.Wwise.Event`。
- 提供 Play / Stop / SetSwitch / SetRTPC 薄封装。

使用示例：

```csharp
WwiseEventBridge.Play("player_land", gameObject);
WwiseEventBridge.Play("hit_metal", gameObject);
WwiseEventBridge.SetSwitch("Surface", "grass", gameObject);
WwiseEventBridge.SetRTPC("ActionSpeed", speed01, gameObject);
```

绑定资产创建入口：

```text
超高速行者/音频/创建 Wwise 事件绑定资产
```

文档：

```text
Docs/Audio/WwiseEventBridge.md
```

本次仍未 commit。

## 2026-10-06：通用 Wwise 动作绑定管理器

新增 EditorWindow：

```text
超高速行者/音频/Wwise 动作绑定管理器
```

功能：

- 选择任意 `AudioActionDefinition`。
- 配置 `ActionId`、`Play Event`、`Stop Event`。
- 从 Clip 自动复制 WAV 到 `StreamingAssets/Audio/Source/Generated/`。
- 创建或更新 `WwiseActionBindings.Entry`。
- 检查重复 `ActionId`。
- 查看、选择和删除已有绑定。
- `AudioActionDefinition` Inspector 增加直接打开绑定窗口的入口。

旧的 `WwiseActionBindingSetup` 菜单不再写死 Slide / WallSlide，只打开通用窗口。

本次仍未 commit。

## 2026-10-06：移除 PlayerAudioDriver，动作源改为手动挂载

- 删除旧 Unity AudioSource 路径的 `PlayerAudioDriver`。
- `AudioSystem` 不再自动创建动作适配器，只负责自动挂载 `WwiseActionDriver`。
- `PlayerDynamicAudioActionSource` 改为由使用者手动挂到 Player。
- 新链路依赖：
  ```text
  PlayerMotor
  -> PlayerDynamicAudioActionSource
  -> IDynamicAudioActionSource
  -> WwiseActionDriver
  -> Wwise
  ```
- Player prefab 需要由使用者移除旧组件引用，并添加
  `PlayerDynamicAudioActionSource`。

本次仍未 commit。

## 2026-10-06：动态动作快速重触发与规则化 Adapter

问题：

- 新 driver 用 `ActionId` 复用 channel，动作停止进入 Release 后，新的 Start
  会被旧的 Release renderer 挡住，必须等尾音播完。
- `PlayerDynamicAudioActionSource` 仍然写死 slide / wall_slide。

修复：

- `DynamicAudioActionRequest` 增加 `InstanceId`。
- channel key 改为 `ActionId + InstanceId`。
- 同一实例新的 `Start` 会立即释放旧 renderer 并重新创建 voice。
- 快速连续触发可以使用不同 `InstanceId` 并存，不再等待旧 Release。
- `PlayerDynamicAudioActionSource` 改成规则表：
  ```text
  ActionId + ConditionKey + ContactIntensity
  ```
- 默认规则仍映射：
  ```text
  slide      <- sliding
  wall_slide <- wall_sliding
  ```
- ConditionKey 交给现有 `PlayerActionRunner.CheckCondition` 判断；新增条件
  不需要改 `WwiseActionDriver`。

日志：

```text
Logs/wwise-adapter-interface-2.log
```

本次仍未 commit。

## 2026-10-06：Slide / WallSlide Adapter 接入

- `WwiseActionDriver` 已改为按 `ActionId` 管理通用 channel 池。
- 新增 `PlayerDynamicAudioActionSource`，读取现有 `PlayerMotor` 状态并上报：
  ```text
  slide
  wall_slide
  ```
- `PlayerMotor`、战斗、Character、动画和模型无需新增音频依赖。
- `WwiseActionBindings` 通过 `ActionId` 查找 definition、Event 和源文件。
- `WwiseActionPcmRenderer` 增加 `RequestStop(...)`，支持：
  ```text
  Release
  Immediate
  FinishCurrentLoop
  PlayFullOnce
  ```
- 接口规范文档：
  ```text
  Docs/Audio/DynamicAudioActionInterface.md
  ```

烟测通过：

```text
Wwise action preview passed: Slide
Wwise action preview passed: WallSlide
Wwise action preview passed: UnboundAutoSource
Wwise action preview passed: AfterEngineReset
Wwise PCM bridge smoke test passed: playingId=1
```

日志：

```text
Logs/wwise-adapter-interface-2.log
```

本次仍未 commit。

## 2026-10-06：动态动作接口与曲线清理

新增动态动作上报契约：

```text
DynamicAudioActionPhase
DynamicAudioActionStopMode
DynamicAudioActionRequest
IDynamicAudioActionSource
```

动作代码只需上报：

```text
ActionId
Start / Update / Stop
StopMode
NormalizedSpeed
ContactIntensity
Direction
SurfaceId
Seed
```

音频侧不再需要理解具体动作枚举。`WwiseActionBindings` 增加 `ActionId` 和按
字符串查找，后续 `WwiseActionDriver` 可以切换成通用 channel 池。

同时删除没有进入 Wwise 输出链路的 `Pitch Follow`、`Lowpass Follow` 及其
配套参数、Inspector 标签页、调试表和旧的 `AudioEnvelope` 曲线类型。动作驱动
的 Pitch、Lowpass、Volume、Pan 后续统一在 Wwise RTPC / Bus 中处理。

本次仍未 commit。

## 2026-10-06：Play 模式后试听的最终清理

真实 Enter Play / Exit Play 回归确认：

- 退出 Play 后旧 Wwise 初始化器、监听器、编辑器 LateUpdate 和银行句柄可能残留。
- 仅复用初始化器不足以恢复音频线程渲染。

最终处理：

- 退出 Play 后第一次试听先执行一次
  `AkUnitySoundEngineInitialization.ResetSoundEngine()`。
- 重置后重新初始化 `AkInitializer`，强制启用编辑器 LateUpdate。
- 如果引擎被 suspend，调用 `WakeupFromSuspend()`。
- 预览不再依赖 `AkBankManager` 的旧引用计数，直接调用
  `AkUnitySoundEngine.LoadBank("DynamicAction")`。
- 试听 host 固定带自己的 `AkAudioListener`。
- 新增菜单 `超高速行者/音频/重置编辑器试听`，用于任何情况下手动重建
  Wwise 编辑器音频链路。
- 临时 PlayMode 回归脚本已删除，不留在工程菜单中。

最终烟测：

```text
Wwise action preview passed: Slide
Wwise action preview passed: WallSlide
Wwise action preview passed: UnboundAutoSource
Wwise action preview passed: AfterEngineReset
Wwise PCM bridge smoke test passed: playingId=1
```

日志：

```text
Logs/wwise-final-after-cleanup-2.log
```

本次仍未 commit。

## 2026-10-06：Play 模式后试听失效与 Release 变调修复

### Play 模式后试听失效

复现方式：

```text
先正常试听
-> 模拟进入 / 退出 Play 模式时 Wwise 强制重置
-> 再试听
```

复现结果：

```text
NullReferenceException
WwiseActionPreviewPlayer.EnsureEngine
```

原因：

- `AkInitializer` 仍保留旧的单例对象。
- 预览层没有复用该初始化器，而是新建 `AkInitializer`。
- Wwise 在 `Awake` 中立刻销毁重复组件，随后访问它导致空引用；银行和监听器状态也
  留在上一次 Play 模式。

修复：

- 预览优先复用 `AkInitializer.GetAkInitializerGameObject()`。
- 每次试听前重新执行 Wwise 初始化和编辑器监听器注册。
- Play 模式状态切换时主动停止旧预览。
- Tick 绑定改为无状态重绑，避免 Play 模式后残留 delegate 状态。
- 回归烟测加入 `AfterEngineReset`。

### Release 异常变调

原因：

- Wwise 工程仍残留旧 `ActionSpeed -> PitchSemitones` RTPC 和
  `PluginMediaSource`。
- 新 Source Plugin 已不包含 `PitchSemitones` 参数，因此这是无效旧结构。
- C# 端 `ReleasePitchSemitones` 默认范围为 `-1.2..1.2`，松手进尾音时会产生
  明显随机移调。

修复：

- Wwise 工程删除旧 `PluginMediaSource`、旧 RTPC 和 `ActionSpeed` Game Parameter。
- 重新生成并复制 `Init.bnk` / `DynamicAction.bnk`。
- `ReleasePitchSemitones` 默认改为 `0..0`，现有示例资产同步归零。
- 保留字段供特殊效果手动开启；普通动作不再在 Release 瞬间随机移调。
- `WwiseActionPcmRenderer` 增加 15 ms 播放速率平滑，避免状态切换时出现音高突跳。
- 删除旧的 `AudioActionPreviewPlayer` AudioSource 实现，Inspector 试听只保留
  Wwise PCM 路径，避免两套状态机并存。

回归烟测：

```text
Wwise action preview passed: Slide
Wwise action preview passed: WallSlide
Wwise action preview passed: UnboundAutoSource
Wwise action preview passed: AfterEngineReset
Wwise PCM bridge smoke test passed: playingId=1
```

日志：

```text
Logs/wwise-clean-final-2.log
```

本次仍未 commit。

## 2026-10-06：所有 AudioActionDefinition 自动波形与试听

新增：

```text
Assets/Editor/Audio/AudioActionSourceLocator.cs
```

现在 Inspector 不再要求每条资产先写进 `WwiseActionBindings`：

- 绑定表里的 `SourceRelativePath` 仍然可以作为显式覆盖。
- 没有绑定的定义会根据 `AudioClip` 的资产路径和 GUID，自动把 WAV 复制到：
  ```text
  Assets/StreamingAssets/Audio/Source/Generated/<GUID>_<文件名>.wav
  ```
- 路径稳定，同名素材不会互相覆盖。
- 波形分析和 Inspector 试听都通过该定位器工作。
- 试听没有绑定 Event 时使用通用的
  `Play_SlideSinePlugin`，只负责 PCM 搬运和试听，不代表运行时动作路由。

实时试听还补上了 Wwise 监听器：编辑器里没有 `AkAudioListener` 时，试听
host 会临时添加一个默认监听器，避免 Source Plugin 有 PCM 但没有监听输出。

自动验证新增了未绑定资产：

```text
Wwise action analysis passed: UnboundAutoSource (0.00..0.97)
Wwise action preview passed: UnboundAutoSource
```

日志：

```text
Logs/wwise-generic-preview-final-2.log
```

本次仍未 commit。

## 2026-10-06：Inspector 试听与 F3 面板接入 Wwise PCM 路径

问题现象：

```text
Inspector：当前导入设置不允许读取采样数据
Audition：无法播放
F3 Audio System：no active action voice
```

原因是 Wwise 接管输出后 `m_DisableAudio: 1`，旧的
`AudioClip.GetData()` / `AudioSource` 预览链路全部失效；运行期 F3 面板也仍然
只读取旧的 `ActionAudioVoice` 池，而实际声音已经由 `WwiseActionDriver` 播放。

本次修改：

- `AudioActionClipAnalysis` 支持直接分析交错 PCM。
- `AudioActionDefinitionEditor` 优先通过绑定资产的 `SourceRelativePath`
  读取 `StreamingAssets` WAV 进行波形和循环区分析。
- 新增 `WwiseActionPreviewPlayer`：Inspector 试听直接创建 Wwise PCM voice、
  推送 Unity 渲染的 PCM，再走 Wwise Event/Bus。
- `WwiseActionDriver` 改为滑铲与墙滑两个独立通道，各自使用独立 emitter，
  避免两个动作同时释放时互相覆盖 `SendPluginCustomGameData` 的 voice 信息。
- `AudioDebugOverlay` 现在同时显示 Wwise 通道的状态、动作时间、速度、
  Gain、Pitch Follow 和 Lowpass Follow。
- `PlayerAudioDriver` 暴露 `WallSlideAction`；Wwise 驱动存在时不再启动
  旧 AudioSource 动作池。
- `WwiseActionBindings` 增加 `WallSlide` 条目，源文件为
  `Audio/Source/hand_slide_very_short.wav`。

自动烟测结果：

```text
Wwise action analysis passed: Slide (0.00..0.51)
Wwise action preview passed: Slide
Wwise action analysis passed: WallSlide (0.00..0.69)
Wwise action preview passed: WallSlide
Wwise PCM bridge smoke test passed: playingId=1
```

日志：

```text
Logs/wwise-preview-debug-final-3.log
```

本次仍未 commit。
