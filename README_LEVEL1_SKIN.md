# Level1 换皮（工业硬表面）— 交接说明

基于 `dev@78512c2a` 切出的独立分支，存放 Level 1 换皮成果，供地编挑选可直接复制的资产。

## 怎么用
- 演示场景：`Assets/Scenes/Level1_Skin.unity`（由旧版 Level1 复制后加工，原 `Level1.unity` **未改动**，全部碰撞体数量一致：42 MeshCollider + 53 BoxCollider）。
- 生成物（约 1.1GB 的两套合并网格）**未入库**：
  - 解压附件 `Level1Skin_GeneratedMeshes.tar` 到 `Assets/Art/EnvSkins/`（含 `Facade/`、`Greeble/`），或
  - 用仓库内工具重放（确定性 seed）：`MapSkin.FacadeRebuild.Run` → `MapSkin.GreebleScatter.Run` → `MapSkin.DecalPlates.Run` → `MapSkin.SkinLookTune.Run`。
- 场景里新增对象全部挂在三个根下，可整体删除：`MAPSKIN_Facade` / `MAPSKIN_Greeble` / `MAPSKIN_Decal`；除它们之外只有 14 樘门实例（`DEC_Shared` 下的 `GATE_<区块>_<类型>`）是新加的。新几何**零碰撞体**。

## 可直接复制的资产
| 用途 | 路径 |
| --- | --- |
| 气闸门 kit（14 樘同款） | `Assets/Art/Environment/Gates/V2/GATE_KIT.fbx` + `Assets/Art/Images/Gates/V2/` |
| 工业功能副形 12 件（桥架/线槽/通风格栅/检修门/接线盒/立管/栏杆/爬梯/支架/灯具/标识/地漏） | `Assets/Art/Environment/Greeble/` + `Assets/Art/Images/Greeble/` |
| 甲板支撑塔（按板块尺寸的柱头） | `Assets/Art/Environment/Deck/Support/SUPPORTS.fbx` |
| 线条技法贴图 25 套（A_Ortho/B_Step/C_Island/D_Jog/E_Bulk × 黑/白/青/地坪） | `Assets/Art/Images/HST_Prags/` |
| 包板/功能件/贴纸材质 | `Assets/Art/Materials/MapSkin/{Facade,Greeble,Decal}/` |
| 白模倒角网格（929 件，45° 切角盒） | `Assets/Art/EnvSkins/BEVEL_*` + `BEVEL_Backup.asset` |

## 工具（Editor，均支持 `-scene`）
`MapSkin.FacadeRebuild`（模块化包板）、`MapSkin.GreebleScatter`（功能件装配）、`MapSkin.DecalPlates`（贴纸装饰板）、`MapSkin.SkinLookTune`（曝光/环境光/顶灯/包板色）、`MapSkin.SkinQA`（体积/三角/门洞净空/出生点体检）、`MapSkin.BevelGeometry`（全场景倒角）、`MapSkin.GateV2Apply`、`MapSkin.DeckSupports`、`MapSkin.RackV2Apply`。
