# ENV Kit 交付说明（feat/env-kit-handoff）

> 本分支 = 环境资产交付。**diff 里只有交付物**（自 `origin/dev` 切出），不带工程配置。
> 分批原子提交，合并点打 tag `envkit-v1`。
> **交付根 = `Assets/Art/Environment/HST_Kit/`——一个目录装下全部环境资产**：
> 网格 / 贴图 / 材质 / prefab / 编辑器工具 / 运行时脚本 / 场景 / 交付说明都在这里面。
> 全面口径与灰盒替换契约见外层文档《环境资产口径与地图接入》《地编Kit拆分与交付》§九。

## 根目录结构

| 子目录 | 内容 |
| --- | --- |
| `Models/HST_Prags` | 室内三场景 + 城市套件的源网格（FBX + 逐件 manifest） |
| `Models/HST_Kit_Std` | 标准件 FBX（一包 14 件，prefab 由烘焙器拆分） |
| `Models/Ruins/Kit_v3` | 废墟 kit v3 定稿 14 件（14,804 tri：断墙3/厂房5/化工6） |
| `Textures/HST_Prags` · `Textures/Ruins` | 贴图（HST_Prags 各套立面/舱壁/地坪；废墟 7 套 ambientCG） |
| `Materials/HST_Prags` | 材质 |
| `Prefabs/HST_Kit_Std` | **可摆成品件**（墙6/顶1/地2/摆件2/摆件组2/楼梯2，目录即类目，件表看同目录 IMPORT.md） |
| `Prefabs/HST_Kit` | 走廊 kit（3 件） |
| `TechBlastDoor` | 防爆门 v5.6 自包含单元（FBX/材质/贴图/manifest/IMPORT.md） |
| `Editor/HST` | 编辑器工具（烘焙器/目录棚拍/布光/验证/出图） |
| `Runtime/HST` · `Runtime/TechBlastDoor` | 要进包的运行时脚本（AgX 特性、摆门；门控制器、接近开门） |
| `Scenes/` | 室内三场景与光贴图、城市 LookDev、标准件目录/棚拍、走廊房间试验、防爆门试验场 |
| `ReflectionProbes` · `Skybox` | 场景光照依赖 |
| `Manifests/HST_Prags` | 逐件尺寸/材质槽 manifests |

## 批次（提交树自下而上）

1. HST_Prags 素材底料（GUID 依赖先行）
2. 室内 kit（标准件 prefab 库 + 三场景 + catalog/棚拍 + 工具）
3. 城市套件 LookDev 场景
4. 防爆门 v5.6
5. 废墟 kit v3 取件
6. 单根归拢（本提交）+ 交付说明

## 导入注意（踩过的坑，务必照做）

1. **防爆门发光**：材质 Emission 强度 **青 ×5.0 / 白 ×4.5**（基色 ≤1.0，亮度只靠 Intensity），漏乘暗 5 倍。
2. **防爆门基色图是线性字节**：贴图导入 **关 sRGB**。
3. **Unity 子网格顺序 ≠ Blender 材质槽顺序**：门 FBX 的材质按 FBX 材质索引认领，不要按槽号手接。
4. **废墟 v3**：FBX 7 个材质槽（混凝土/断口/砖/金属/波纹板/锈/地面），Unity 侧材质与 prefab 下一批接线；
   地面想压住空旷感用 `Ground107`，定稿展示地面用 `Concrete044A`。
5. **楼梯（下一批）**：`count=12 / rise=0.1667 / run=0.28 / width=3.0`，pivot=第一级踏步前缘中点、z=0 落地、
   沿 +X 上行（Unity 里即 -X）；灰盒替换契约见《环境资产口径与地图接入》§二。

## 下一批（在本分支上继续提交）

- 可破坏物件：能量方块 ×2 + 能量罐 ×1（FBX/材质/prefab）→ 顶掉旧板条箱角色
- 楼梯 ×3（见上）；灰盒替换编辑器工具
- 废墟 v3 的 Unity 材质接线与 prefab；城市六楼型 prefab 批
