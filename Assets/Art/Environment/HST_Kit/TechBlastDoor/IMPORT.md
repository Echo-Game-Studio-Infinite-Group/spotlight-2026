# 科技感重型气密防爆门 — Unity 导入说明

本文自包含:不看其他文档也能照做。对象是一扇**四层 16 扇**错层伸缩开合的科技风重型防爆门
(斜向门 / 横向门 / 竖向门),Blender 侧已冻结规格(v4.5),本文告诉你怎么把它正确放进 Unity 工程。

- 配套控制器:`Assets/Scripts/Environment/TechBlastDoor/TechBlastDoorController.cs`(开合顺序、速度、缓动都在它身上调)
- 配套玩法脚本:`Assets/Scripts/Environment/TechBlastDoor/TechBlastDoorProximity.cs`(靠近自动开、走远自动关,见第 7 节)
- 本目录存放:`SM_TechBlastDoor_v1.fbx` + 各材质贴图 + 本说明

---

## 1. 文件落位(放哪)

| 内容 | 目标路径 |
| --- | --- |
| FBX 模型 | `Assets/Art/Environment/TechBlastDoor/SM_TechBlastDoor_v1.fbx` |
| 材质与贴图 | `Assets/Art/Environment/TechBlastDoor/Materials/` 与 `Textures/` |
| 开门控制器(已写好,勿动命名) | `Assets/Scripts/Environment/TechBlastDoor/TechBlastDoorController.cs` |
| 靠近自动开关脚本(已写好,勿动命名) | `Assets/Scripts/Environment/TechBlastDoor/TechBlastDoorProximity.cs` |
| 逐对象清单(尺寸/AABB/材质名核对用) | 随压缩包附 `SM_TechBlastDoor_v1.manifest.json` |

**FBX 版本指纹(v5.6,2026-10-15;与下表对不上就是拿错版本,先换包再导入):**

| 指纹项 | 值 |
| --- | --- |
| FBX md5 | `850295e0b54a02effac424870c6f387a` |
| 对象数 / 三角面数 | 561 对象 / 458,216 tri |
| 可动门扇 | **16 扇**(每层 4 扇 = 内/外 × 左右(或上下),见第 4 节) |
| 净开口 | **6.50 m 宽 × 6.00 m 高** |
| 整门(框)外形 | **10.60 m 宽 × 7.85 m 高 × 1.30 m 厚**(含外凸装饰件的合并包围盒 10.92 · 9.41 · 1.58;基础槽沉到 y −1.56) |

## 2. FBX 导入设置(Model 页,逐项)

选中 FBX,在 Inspector 的 Model 页:

| 设置项 | 值 | 原因 |
| --- | --- | --- |
| Scale Factor | 1 | 导出时已按 1 单位=1 米 写死(apply_unit_scale=True),不要再缩 |
| Convert Units | 勾选(默认) | 保证 Blender 米 → Unity 米 换算正确 |
| Bake Axis Conversion | 不勾(默认) | 保持与导出口径一致;勾了会改节点旋转,门扇位移对不上 |
| Import Blend Shapes / Import Visibility | 不勾 | 用不到 |
| Import Animation | 不勾 | 本 FBX **没有动画**,开合由控制器实时插值,不走路线 |
| Normals | Import,Smoothing Angle 60°(默认) | 法线随模型走 |
| Blend Shape Normals / Legacy Blend Shape Imports | 默认关 | 用不到 |
| Tangent Space | Calculate MikktsSpace(默认) | 法线贴图若后续要上,用默认即可 |
| Swap UVs / Generate Lightmap UVs | Generate Lightmap UVs **勾选** | 场景照明走烘焙(TA 管线),框体静态件需要 UV2。已知坑:个别微型件(螺栓/LED)可能被展开成空 UV2 —— 若发现个别小件烘焙后发黑,单独把该 FBX 的此项关掉,改用 Blender 侧预烤 UV2 的版本 |
| Materials 页 | **Material Creation Mode = Import Standard**,Location = External,Name = By Material Name,Search = Everywhere | ★ 让导入器**按 FBX 自己的材质索引**认领工程里同名的 `M_Door_*.mat`。**不要设成 None 再按 manifest 的材质列表下标指槽 —— 那是错的**(见第 5 节"子网格顺序坑") |

设置完点 **Apply**。

## 3. 坐标与单位(为什么模型是这么个朝向)

- **1 Blender 单位 = 1 米**。净开口 6.50 m 宽 × 6.00 m 高,整门总宽 10.60 m、总高 7.85 m、厚 1.30 m。
- 导出轴向 `axis_forward='-Z', axis_up='Y'`。导入 Unity 后的实测坐标合同(2026-10-06,Unity 2022.3.33f1):

  ```
  unity = (-x, z, -y)      # Blender 点 (x,y,z) → Unity 点 (-x, z, -y)
  ```

- 结论:**门正面在 Unity 里朝 +Z**,地面 = Unity y=0,原点 = 洞口水平中心、地面处。
  场景里摆门时,想让人从哪边进门,就把门的 +Z 朝向哪边。
- 验收尺寸:把 FBX 拖进场景后,给根节点看 Bounds,或对照 manifest.json 里逐对象的
  `aabb_min_unity / aabb_max_unity`(已按上式换算成 Unity 坐标)。

## 4. 门扇命名与位移量表(开合动画用)

十六个**可动门扇**是独立 GameObject,名字固定(对象名 = 门扇节点名 = 控制器自动查找的键)。
★ 每扇门的**装饰从属件已经并进该门扇自身**(封边/面板/锁盒/滑座板/标识/灯带都在里面),所以一扇门只对应一个节点,
  开门时不会留下任何装饰件 —— 移动这个节点就够了。
★ v5.6 是**错层伸缩多扇**:每层拆成内/外两半扇(内扇携中缝阶梯齿,外扇与内扇异深度搭接),
  所以内扇比外扇退得远。★ v5.5 起**两层装饰门都是纯左右平移**(原来带的 1.10 m 抬升已归零),
  且近侧装饰门行程加长 —— 内扇半宽加厚到 80 mm 后要退得更远:外扇 ±1.96 / 内扇 ±3.90。

| 门扇 GameObject 名 | 所属层 | 开合方式 | Blender 位移 (x,y,z) | **Unity 位移 (-x,z,-y)** |
| --- | --- | --- | --- | --- |
| `GEO-OL_out_l` | ① 外层装饰门(近侧) | 左外半扇**纯左右平移**退入框柱 | (-1.960, 0.00, 0.00) | **(+1.96, 0, 0)** |
| `GEO-OL_out_r` | ① 外层装饰门(近侧) | 右外半扇**纯左右平移**退入框柱 | (+1.960, 0.00, 0.00) | **(-1.96, 0, 0)** |
| `GEO-OL_in_l` | ① 外层装饰门(近侧) | 左内半扇(携中缝齿)**纯左右平移** | (-3.900, 0.00, 0.00) | **(+3.90, 0, 0)** |
| `GEO-OL_in_r` | ① 外层装饰门(近侧) | 右内半扇(携中缝齿)**纯左右平移** | (+3.900, 0.00, 0.00) | **(-3.90, 0, 0)** |
| `GEO-ID_horiz_out_l` | ② 横向真门 | 左外半扇纯水平左移 | (-1.655, 0.00, 0.00) | **(+1.655, 0, 0)** |
| `GEO-ID_horiz_out_r` | ② 横向真门 | 右外半扇纯水平右移 | (+1.655, 0.00, 0.00) | **(-1.655, 0, 0)** |
| `GEO-ID_horiz_in_l` | ② 横向真门 | 左内半扇(携中缝齿)纯水平左移 | (-3.280, 0.00, 0.00) | **(+3.28, 0, 0)** |
| `GEO-ID_horiz_in_r` | ② 横向真门 | 右内半扇(携中缝齿)纯水平右移 | (+3.280, 0.00, 0.00) | **(-3.28, 0, 0)** |
| `GEO-ID_vert_up_out` | ③ 竖向真门 | 上外半扇上移入框顶腔 | (0.00, 0.00, +1.5425) | **(0, +1.5425, 0)** |
| `GEO-ID_vert_up_in` | ③ 竖向真门 | 上内半扇(携中缝齿)上移入框顶腔 | (0.00, 0.00, +3.0300) | **(0, +3.0300, 0)** |
| `GEO-ID_vert_dn_out` | ③ 竖向真门 | 下外半扇下沉入地面基础槽 | (0.00, 0.00, -1.5025) | **(0, -1.5025, 0)** |
| `GEO-ID_vert_dn_in` | ③ 竖向真门 | 下内半扇(携中缝齿)下沉入基础槽 | (0.00, 0.00, -2.9900) | **(0, -2.9900, 0)** |
| `GEO-OD_out_l` | ④ 外层装饰门(远侧) | 左外半扇**纯左右平移**(与近侧镜像) | (-1.655, 0.00, 0.00) | **(+1.655, 0, 0)** |
| `GEO-OD_out_r` | ④ 外层装饰门(远侧) | 右外半扇**纯左右平移** | (+1.655, 0.00, 0.00) | **(-1.655, 0, 0)** |
| `GEO-OD_in_l` | ④ 外层装饰门(远侧) | 左内半扇(携中缝齿,在最外后带) | (-3.280, 0.00, 0.00) | **(+3.28, 0, 0)** |
| `GEO-OD_in_r` | ④ 外层装饰门(远侧) | 右内半扇(携中缝齿,在最外后带) | (+3.280, 0.00, 0.00) | **(-3.28, 0, 0)** |

说明:
- "Unity 位移"一列已按第 3 节合同换算(取两位小数),并且就是控制器 Inspector 里的默认值 —— **不用手改**。
- 语义是"门根节点局部坐标系下的位移"(米),门根在场景里旋转缩放时依然成立。
- 其余对象是静态件:框体 `GEO-FR_*`、五金 `GEO-HW_*`、灯带标识 `GEO-LT_*`,不动。

## 5. 材质(URP)

FBX 自带材质槽名,按下表手工建 URP Lit 材质并拖到 FBX 的对应槽上
(FBX 的 Materials 页设为 None 后,在 Project 里选中 FBX → Materials 页逐槽指定):

| 材质槽名 | 外观 | Base Color | 建议做法 |
| --- | --- | --- | --- |
| `M_Door_Metal_Dark` | 深蓝黑结构金属(框体/基底/深腔) | `#20242B` | Metallic 0.35,Smoothness ~0.56 |
| `M_Door_Metal_Mid` | 中灰金属(结构盖板/横向门) | `#565C66` | Metallic 0.70,Smoothness ~0.58 |
| `M_Door_Metal_Light` | 浅瓷白面板(大面板/竖向门/亮面件;瓷面感,近非金属) | `#C2C7CE` | Metallic ~0,Smoothness ~0.50 |
| `M_Door_Steel_Trim` | 拉丝不锈钢(边框/五金/螺栓) | `#8F969E` | Metallic 1,Smoothness ~0.78 |
| `M_Door_Dark_Rubber` | 哑光黑橡胶(密封/防滑/齿面) | `#0B0C0E` | Metallic 0,Smoothness ~0.14 |
| `M_Door_Hazard_Stripe` | 琥珀黑 45° 警示斜纹(琥珀 `#E9AE1E` / 黑 `#17181A`,条宽≈54mm) | — | 用随包贴图,不透明 |
| `M_Door_Emit_Cyan` | 青色自发光(灯带) | `#29C7FF` | 见下方"自发光" |
| `M_Door_Emit_White` | 白色自发光(状态屏背光) | `#FFF8EE` | 见下方"自发光" |
| `M_Door_Glass_Dark` | 深色玻璃(状态屏) | `#0A141A` | Surface Type Transparent,Alpha ~0.35,Metallic 0,Smoothness 高(~0.94) |
| `M_Door_Perf_Dark` | 穿孔板孔底/暗腔 | `#0E1114` | 纯深色,不透明 |
| `M_Door_Paint_Blue` | 蓝标/小色块 | `#2A7BFF` | 纯色,Metallic ~0.15 |

**随包基色图 + 法线图(v5.6 起,Unity 观感的关键)**:FBX 带不走程序化节点(噪波/凹凸/Wave 斜纹)——
只挂 Image Texture 才带得走。所以导出前已把每个材质**烘平成能直接喂 URP 的形态**,贴图在 `Textures/`:

| 贴图 | 挂到 | 导入设置(**必设,设错整体偏暗**) |
| --- | --- | --- |
| `T_Door_metal_dark / metal_mid / metal_light / steel / perf / rubber / paint_blue _BC_unity.png` | Base Map | Texture Type = Default,**sRGB 取消勾选** |
| `T_Door_hazard_BC_unity.png` | Base Map(警示材质) | 同上。45° 斜纹已烘进图,**UV 用模型自带的世界投影 UV**(tile 2 m),不要再改 tiling/offset |
| `T_Door_Panel_N.png` | Normal Map | Texture Type = **Normal map**(OpenGL 约定,不要翻 G) |
| `T_Door_Panel_MS_URP.png` | (备选)Metallic Map | R = 金属、A = 光滑。本门用逐材质标量即可,不必挂 |

★ **为什么基色图要关 sRGB**:这批图里存的是**线性值**(导出器直接写线性缓冲,未做 sRGB 编码)。
  按默认 sRGB 导入会被**再解一次** → 整门比 Blender 暗好几倍(实测 4~13 倍)—— 这正是此前
  "Unity 端质感远不如 Blender"的根因。**有基色图时材质的 Base Color 要留白(1,1,1,1)**,
  颜色全由图承担,否则双重变暗。第 10 节的试验场工具已自动按上表设好,手工导入务必照做。

★ **致命坑:Unity 的子网格顺序 ≠ Blender 的材质槽顺序 —— 绝不能按 manifest 的材质列表下标指槽。**

实测(2026-10-15):`GEO-ID_horiz_in_l` 的 Blender 槽序是
`[Metal_Mid, Steel_Trim, Dark_Rubber, Perf_Dark, Metal_Dark, Emit_Cyan, Hazard]`,
但 **Unity 里导出的子网格顺序是 `[0, 2, 3, 4, 1, 6]`**(按"逐材质三角形数"两边对拍确定的)。
按下标指槽的后果:**近黑凹腔(Perf_Dark)被涂成中灰** —— 就是"黑色底变白"的样子;装饰门也是同样的错位。
**正解**:FBX 导入设置走 `Import Standard + External + By Material Name + Search Everywhere`,
让**导入器按 FBX 自己的材质索引认领**同名的 `M_Door_*.mat` —— 子网格↔材质完全由文件决定,不经我们的手。
试验场工具(`HstBlastDoorTestScene`)已按这套设好,并逐渲染器核对"材质都在本目录且在 manifest 声明集合内"
(实测 通过 561 / 异常 0)。

★ 配色为 v4.5 中庭口径(`SC_Atrium_Concept` 同源,十六进制值与 Blender 侧 `mat_door.py` 一致):
  浅瓷白面板 + 深蓝黑结构 + 青发光 + 琥珀警示。**所有颜色(含发光件基色)一律 ≤1.0,不许超 1**
  (Bloom 阈值即 1.0);要更亮只调 Emission Intensity,不拉高颜色。

**Metallic-Smoothness 工作流(URP 特有,必读)**:URP Lit 没有 Roughness 槽,
roughness 要**取反成 smoothness 塞进 Metallic 贴图的 alpha 通道**,再在材质里把
Smoothness Source 设为 Metallic Alpha。手涂平滑度时直接填 Smoothness 滑条(数值 = 1 − roughness)。

**自发光必做三步**:① Emission 勾选;② Emission Color 填上表青 `#29C7FF` / 白 `#FFF8EE`,
Intensity 青 ≈5、白 ≈4.5(与 Blender 侧同值;颜色本身保持 ≤1.0);
③ 点材质底部的 Apply。TA 的 URP-Renderer 自带性能 Bloom,**沿用场景现有的,不要给门自建任何
Post-process Volume / 调色卷**(团队纪律)。

## 6. 开合顺序与控制器用法

开门时序固定:**① 外层装饰门(近侧)先开 → 走到 50%(`bcdStartFraction` = 0.5)时 → ② 横向真门 + ③ 竖向真门 + ④ 外层装饰门(远侧) 三组同时起步**;
两组在时间轴上**重叠**(不是等外层走完再开内层)。门扇线速度统一为 `speedMetersPerSecond`:
冻结规格是 **1.0 m/s**(与 Blender GIF 出图同口径);试验场(第 10 节)按用户令调成 **1.5 m/s = 比 GIF 快 50%**;
关门是这条时间线的严格镜像(先一起关中间三组,最后关近侧装饰门)。
关门完全反序。这些都由控制器实现,不用动画剪辑、不用 Timeline。

```csharp
// 任意脚本里(控制器挂在门根节点上):
var door = doorRoot.GetComponent<GameJam.Environment.TechBlastDoorController>();
door.Open();                  // 开门
door.Close();                 // 关门
door.Toggle();                // 开关切换
door.SetProgress(0.5f);       // 直接摆到整体进度一半(过场/脚本对齐用)
bool canWalkThrough = door.IsPassable;   // 只有四层全开完才为 true,门未开完不许通行
float inner = door.GetLayerProgress(TechBlastDoorController.DoorLayer.Horizontal);  // 单层进度(分镜/音效用)
```

Inspector 可调:每扇门的引用与位移向量(留空自动按名字找)、门扇线速度(米/秒;冻结规格 1.0,试验场里 1.5)、近侧装饰门走到几成时中间三组起步(`bcdStartFraction`,默认 0.5 = 开一半)、缓动曲线(默认 EaseInOut)。
**挂载时门扇必须处于关闭位**(FBX 刚实例化出来的位姿就是),关闭位在运行开始时捕获一次。

## 7. 通电:靠近自动开、走远自动关(两组件,零接线)

开合动画由 `TechBlastDoorController` 驱动,"靠近就开"由 `TechBlastDoorProximity` 接线。
**两个组件都挂在门根节点上**,不用建子物体、不用拖引用:

1. 拖 FBX 进场景 → 门根挂 `TechBlastDoorController`(门扇引用留空,自动按名字找);
2. 同一节点再挂 `TechBlastDoorProximity` —— 进 Play 后自动生成两个子物体:
   - `ProximityZone`:横跨门两侧的触发盒(默认宽 12 × 高 7 × 前后各伸 14 米)。
     玩家进入 → 开门;最后一人离开且过了 `closeDelaySeconds`(默认 1 秒)→ 关门;
     关门倒计时里再有人进来则取消关门(人还没走光永不关)。
   - `PassageBlock`:一块正好堵住门洞的碰撞盒(6.7 × 6.1 × 1.6 米,比 6.5×6.0 洞口略大一圈防贴边钻缝)。
3. 玩家预制体的 **Tag 设为 "Player"**(或改 Proximity 的 `triggerTag`)。

**碰撞口径(重要)**:开/关全程**只是动画** —— 十六扇门与框体都没有碰撞体,全门唯一的物理就是
`PassageBlock` 这块闸板:整体进度没到 `passableAtProgress`(默认 0.95,即"基本开完")时启用挡人,
到了就禁用放行。**别给门扇加 MeshCollider**(纯动画件,加了费性能,移动中的非凸 MeshCollider 还会刷警告)。

Inspector 可调(都有默认值,门根选中时 Scene 里会画出触发区=青线、通行闸=橙线):

| 参数 | 默认 | 怎么调 |
| --- | --- | --- |
| `triggerDepthEachSide` | 14 米/侧 | 玩家跑得快就加深。想"跑到门口门刚好开完":纵深 ≥ 最高跑速 × 门全开耗时(门速 1.5 下全程约 3.5 秒,如 10 m/s 的 bhop 段位给 35 米/侧) |
| `closeDelaySeconds` | 1 秒 | 最后一人离开后等多久开始关;0 = 立刻关 |
| `passableAtProgress` | 0.95 | 开到几成放行;1 = 四层全开完才放行 |
| `blockerCenter` / `blockerSize` | 堵住 6.5×6.0 洞口 | 洞口尺寸没改过就不用动 |

**Play 验收清单**(逐条勾):
- [ ] 从 +Z 侧走近:①装饰门先动 → 走到一半时 ②③④ 三组**同时**起步(两组时间轴重叠);
- [ ] 门没开完冲门,被 `PassageBlock` 挡住;开到 0.95 后能穿;
- [ ] 站在门洞里不动:门保持开,不夹人;
- [ ] 走远:中间三组先一起关,最后关①装饰门(严格镜像);
- [ ] 关门中途折返:门平滑反向重开(时间线插值,天然支持);
- [ ] 从 −Z 侧来同样成立;门根旋转 90° 放走廊拐角,门扇仍沿门自身朝向滑。

## 8. 常见问题

| 症状 | 原因与修法 |
| --- | --- |
| 门不会自动开 | 门根没挂 `TechBlastDoorProximity`;或玩家 Tag 不是 "Player"(要与 Proximity 的 `triggerTag` 一致);或触发区太浅、人还没进范围——Scene 里选中门根看青色框 |
| 人已经走了门还开着 | `closeDelaySeconds` 太大是正常的(默认走光 1 秒后才开始关);一直不开就是触发区里还留着带 Player Tag 的物体没走光 |
| 玩家能穿过没开的门 | `PassageBlock` 被手动禁用了,或门根被缩放导致闸板错位(闸板跟随门根,别单独动它) |
| 尺寸不对(门巨大/微小) | Import Settings 的 Convert Units 被关了,或 Scale Factor 不是 1。重设后用 manifest.json 的 Unity AABB 核对(净开口应 6.5 × 6.0 m) |
| 门朝向反了(正面该朝 +Z 却朝了 -Z) | 导出/导入轴向设置被改。临时修:门根旋转 y=180;根治:恢复导出参数(axis_forward='-Z', axis_up='Y'),并按合同 `unity=(-x,z,-y)` 重算位移表 |
| 门扇左右互换 | 同上,轴向合同失效;核对第 4 节表——Unity 里 +X 是观众(站在 +Z 侧)的左手边 |
| 某扇门不动 | 控制器红字"找不到门扇":FBX 里对象名和表里对不上(被改名/带了 .001 后缀)。改回标准名或手动拖 Transform 进槽位 |
| 门扇一开局就滑出框外 | 挂控制器时门扇不在关闭位。把 FBX 重新实例化(默认位姿=关闭位),或在场景里把门扇摆回关闭位再运行 |
| 灯带/状态屏不发光 | 材质 Emission 没勾或强度太低(要 HDR Intensity ≥ 2);确认场景用的是 TA 自带后处理,别自建 Volume |
| 烘焙后个别小件(螺栓/LED)发黑 | Generate Lightmap UVs 对微型件偶发空展开(已知坑)。对该 FBX 关掉此项,等 Blender 侧出预烤 UV2 版本替换 |
| 整体比例/位置要调 | 调**门根节点**,别动 FBX 子节点 —— 门扇关闭位是控制器运行时从子节点捕获的 |

## 9. 交付登记

- FBX:`SM_TechBlastDoor_v1.fbx`(由 `scripts/door/export_unity_door.py` 从五个 builder 汇总导出)
- 逐对象清单:`SM_TechBlastDoor_v1.manifest.json`(对象名/三角面数/Unity AABB/材质槽名,含 FBX 的 MD5)
- 脚本:`TechBlastDoorController.cs`(动画时序)+ `TechBlastDoorProximity.cs`(靠近开关与通行闸),均在 `Assets/Scripts/Environment/TechBlastDoor/`
- 更新版本时换后缀(如 `_v2`),防止队友端覆盖混乱。

## 10. 试验场(project-0 本地,门直接能跑):走近就开,亲身体验

本地沙箱工作树 `project-0` 里现成有一个**可跑可玩的试验场景**,不用自己搭:

| 项 | 值 |
| --- | --- |
| 场景 | `Assets/Scenes/SC_BlastDoor_Test.unity` |
| 建场工具(可重跑) | `Assets/Editor/HST/HstBlastDoorTestScene.cs`;菜单 `HST/爆门试验场 全流程`,或 `bash tools/unity-batch.sh HST.HstBlastDoorTestScene.BuildOnce` |
| 出图(可重跑) | 同一工具的 `ShotOnce`(闭 / 半开 / 全开 + 3/4 机位) |

**场景里有什么**:暗色地台 80×80 m(带碰撞)+ 纯白墙 18×10×1.6(洞口被门框脸盘完全遮住,不露缝)
+ 爆门原地站在原点(正面朝 +Z)+ 玩家(在 +Z 侧 16 m 处面朝门)+ 团队正式跟随相机
(Main Camera + Virtual Camera,已按 §7 接线);打光 = Flat 环境光 0.75 中性白 + 一盏主方向光 1.7 +
三盏 Realtime 点光(**与走廊/中庭定稿同口径**),另加一盏**实时反射探针**(URP 里没有天空盒/探针时,
metallic=1 的钢材会发黑,Blender 侧有面光宽高光所以不会),零 Volume 后处理。

**Play 起来能体验什么**:朝门走 → 进 10 m 触发区 → 门自动开(门速 **1.5 m/s = 比 Blender GIF 快 50%**)
→ 走远自动关。时间线上近侧装饰门走到一半时中间三道同时起步 —— 与 GIF 出图节奏逐帧一致,只是整体快 1.5 倍。

**为什么不在走廊里放**:走廊内宽 8 m / 净高 4.4 m,**装不下**这台门(框外 10.60 宽 × 7.85 高)。
所以试验场自带墙与地台,门按规格**原尺寸**进场(不缩放)。
