# 废墟 kit v3(定稿保留集 · 14 件) — 导入说明(IMPORT)

批次:`jem-assets_ruins-kit-v3_20261015` · 生成器:`scripts/gen_ruins_kit_v2.py`(固定 seed 可复现)
+ 裁件打包 `scripts/pack_ruins_kit_v3.py` · 场景 `blends/environment/SM_Ruins_Kit_v2_Cull14.blend`
工程:Unity 2022.3 URP · 1 单位=1m · 静态网格 · **共 14 件 / 14,804 tri**

## 零、这一轮跟前几轮的关系(必读)

v1(12 件, 老线)与 v2(29 件, 全场件)都在 `exports/` 里留着。本轮按 **2026-10-15 人工过目筛选**,
只保留用户点名的 14 件(断墙 3 + 厂房 5 + 化工 6), 其余 **15 件全部移出交付集**,
并**补齐贴图**(v2 包缺 `Textures/`, 本包已含 7 组)。

**移出的 15 件**(没有销毁, 整包在 `exports/models/_superseded_ruins_kit_v2_20261015/`, 可随时取回):

| 组 | 移出的件 |
|---|---|
| 断墙 | `SM_Wall_B2`、`SM_Wall_Window2` |
| 厂房 | `SM_WH_SlabBig` |
| 大跨 | `SM_Mega_*` 全 7 件(WallStories / CornerBlown / Pancake / Gable / Collapse / DebrisField / RubbleMound) |
| 瓦砾·砖·钢筋 | `SM_Rubble2_L`、`SM_Rubble2_M`、`SM_Rubble2_S`、`SM_Bricks2`、`SM_Rebar2` |

> **这条线砍掉的后果(先知道再用)**:移出的 15 件里包含**全部瓦砾堆、散砖与独立钢筋簇**——
> 也就是说这批交付件里**没有任何碎屑/散砖/钢筋**元素, "废墟感"只来自 14 件自身的断口与锈蚀;
> 另外 `SM_WH_SlabBig` 是**唯一可上板/坡道件**, 现在没有 bhop 上板件。
> 需要这些元素时: 用 v1 的 `SM_Ruin_*`(瓦砾/散砖/钢筋/坡道齐) 或从归档目录取回 v2 对应件。

## 一、件清单(轴心=吸附点, z=0 落地, 缩放 1.0;尺寸/tri/槽均实测自定稿原件)

**材质槽序(全部件统一)**:0 混凝土 / 1 断口(更暗更糙, 覆在碎裂面上)/ 2 砖 / 3 金属 /
4 波纹板 / 5 锈 / 6 地面。对应 Unity 材质:`M_Ruin_Concrete`(Concrete044A)、
`M_Ruin_Fracture`(Concrete039)、`M_Ruin_Brick`(Bricks097)、`M_Ruin_Metal`(Metal053C)、
`M_Ruin_Corrugated`(CorrugatedSteel009)、`M_Ruin_Rust`(Metal062C) —— 全部 ambientCG **CC0**。

| 件 | 尺寸 W×D×H (m) | tri | 用到的槽 | 断口面 | 砖夹心 | 说明 |
|---|---|---|---|---|---|---|
| `SM_Wall_A2` | 2.00 × 0.62 × 2.53 | 670 | 0/1/2 | 11.4㎡ | **9.1㎡** | 断墙(高位断口):皮+砖芯+断口露骨料+墙脚碎落裙边;墙内有弯钩钢筋 |
| `SM_Wall_Corner2` | 2.00 × 1.29 × 2.51 | 798 | 0/1/5 | 0 | 0 | L 形转角墙(轴心=角点), **断口无砖** |
| `SM_Wall_Stub` | 2.00 × 0.60 × 1.07 | 720 | 0/1/5 | 3.8㎡ | 0 | 矮残墙(掩体/跳台), **断口无砖** |
| `SM_WH_WindowWall` | 2.00 × 0.59 × 3.00 | 822 | 0/1/2/3/5 | 4.0㎡ | **3.9㎡** | 格构钢窗墙(锈框+格条+砖裙墙, 玻璃全碎) |
| `SM_WH_CorrugatedWall` | 2.02 × 0.38 × 2.60 | 918 | 0/1/3/4/5 | 0 | 0 | 波纹板墙(L 形锐撕口×2+卷边+竖向锈带) |
| `SM_WH_CorrugatedSheet` | 1.13 × 1.83 × 0.98 | 460 | 4/5 | 0 | 0 | 倒地波纹板单张(24° 折角半埋) |
| `SM_WH_CraneBeam` | 1.99 × 0.65 × 0.65 | 628 | 0/1/3/5 | 0 | 0 | 行车梁段(工字钢+吊钩+斜撕断口) |
| `SM_WH_FrameColumn` | 0.68 × 0.60 × 2.83 | 354 | 0/1/3/5 | 0 | 0 | H 钢柱(顶端压弯 26°+锚栓+柱脚碎块) |
| `SM_CH_PipeRack` | 2.03 × 0.72 × 2.20 | 1364 | 0/3/5 | 0 | 0 | 管廊段(混凝土柱+三层管, 一根爆裂断法兰) |
| `SM_CH_TankVertical` | 1.63 × 1.80 × 2.36 | 1634 | 3/5 | 0 | 0 | 立式罐(侧壁撕裂大口+翻边+爬梯) |
| `SM_CH_TankHorizontal` | 2.42 × 1.24 × 1.65 | 1686 | 0/3/5 | 0 | 0 | 卧式罐(鞍座+人孔法兰) |
| `SM_CH_Drums` | 1.58 × 1.46 × 0.93 | 2044 | 3/5 | 0 | 0 | 油桶堆(2 立 1 倒 1 凹陷) |
| `SM_CH_LadderColumn` | 0.83 × 0.77 × 2.59 | 1688 | 3/5 | 0 | 0 | 塔器残柱(折断+环形爬梯) |
| `SM_CH_ValveStand` | 0.80 × 0.51 × 1.29 | 1018 | 3/5 | 0 | 0 | 阀门站(球阀+手轮+基墩) |

> 断口面/砖夹心是**实测面面积**(从网格按材质槽统计):这 14 件里**只有 2 件露砖夹心**
> (`SM_Wall_A2` 9.1㎡、`SM_WH_WindowWall` 3.9㎡);`Corner2`、`Stub` 断口上没有砖。
> 若后续要"每面断墙都露夹心", 需要改这两件, 或取回 `SM_Wall_B2`(砖夹心 6.5㎡)。

## 二、Unity 导入设置(同 v1/v2 口径)
1. Model:Scale Factor=1,不勾 Generate Colliders(碰撞手工铺,见第四节)。
2. 贴图:Color→sRGB;NormalGL→Normal map;Roughness/Metalness→Linear;
   URP Lit 的 Smoothness 走 MetallicAlpha(Roughness 反相塞 alpha)。
3. UV:按世界米烘(1 UV=1m),材质 Tiling 基准 Concrete≈0.8 / Fracture≈1.6 / Brick≈1.5 /
   Metal≈2.0 / Corrugated≈1.2 / Rust≈1.6(每米重复次数)。
4. **摆放 yaw 需 +180°**(FBX 往返把 Blender -Y 正面映射到 Unity -Z,与 v1 同口径)。
5. **子网格顺序别按名字猜**:Unity 的 submesh 次序不一定等于本表的槽号次序;进工程后按
   **每个子网格的三角面数**与上表"用到的槽"对拍确认(本仓 2026-10-14 踩过此坑)。

## 三、玩法动线(bhop)
- 墙件按 2m 网格拼接;窗洞净宽 ≥1.0m;可蹬面:墙两大地连续平面 ≥2m,仓库 H 钢柱翼缘面宽 0.25m。
- **本批没有瓦砾/散砖/钢筋, 也没有坡道件**(都在移出清单里):需要减速带用 v1 的 `SM_Ruin_Rubble_*`,
  需要可上板用 v1 的 `SM_Ruin_Slab_Ramp_v1.fbx`。

## 四、碰撞规范(60+ km/h)
- 可跑/可蹬面:手工 Box,向下加厚 ≥0.1m;件间缝补 Box;禁 <0.1m 薄片碰撞。
- 罐体:Capsule 或 Box 粗包;管廊:每层管一根 Box。

## 五、包内容与许可
- `Models/` 14 个 `SM_*_v3.fbx`;`Textures/` 7 组 25 张(Concrete044A / Concrete039 / Bricks097 /
  Metal053C / CorrugatedSteel009 / Metal062C / Ground107);本文件。
- 7 组贴图全部 ambientCG **CC0**(商用免署名), 登记见 `assets/textures/许可证登记.md`;
  几何全部程序化自建。Ground107 是**展示/路面地面**贴图(场景地面用, 不属于 14 件本体)。
