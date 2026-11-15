# Parry 配置与验收

玩家左键发动动作；动作序列「攻击判定 → Parry 拼刀框」决定拼刀范围及帧区间。旧 E 独立计时防御已移除，普通攻击不会再消耗额外 Parry 冷却。

## 玩家动作表

- `Combat.Enabled` 启用战斗上下文。
- `Combat.ParryVolumes` 配置独立拼刀框；空列表表示该动作不能 Parry。它不会造成伤害，`HitGroup` 不参与拼刀去重。
- `AnchorPath / Center / Rotation / Size` 与伤害框使用同样的空间配置。帧起点包含，终点不包含；随动作缩放时间推进。
- `Start / End` 使用稳定的子段 ID 和边界锚点。三种现有普攻均配置为 `active` 起点至终点；低速普攻从第 22 帧才开启。
- 动作窗口中青色条为 Parry 区间，模型预览青色框为当前生效的 Parry 框，橙色框为伤害框。编辑后保存资产。

## 敌人、子弹与光束

敌人根节点 `CombatComponent` 中的「Parry（本组件作为攻击方）」配置来源类型、普通/劣势属性、是否可被 Parry，以及 `_parryTarget` 的挂点和形状。选中时 Scene 显示青色框；敌人的这个框不使用帧锚点，近战从前摇到收招、远程从蓄力到发射自动管理启停。空闲、死亡、中断和回收时关闭。

实际子弹 `Bullet.prefab` 使用独立放大框：局部尺寸 4，乘以根缩放 0.15 后为世界尺寸 0.6 米。每次 Launch 重建攻击身份；Parry 通过独立事件立即返回所属对象池，不依赖扣血事件。射击者后续被打断不会取消已经发出的其他子弹。

激光蓄力期间使用敌人身上的框，发射期间使用受环境遮挡截断的光束框；成功立即灭束并进入冷却。重型敌人切回近战时重新设置来源类型，提示颜色统一读取 CombatComponent 的普通/劣势属性。

## 成功反馈

| 位置 | 字段 | 当前默认值 |
| --- | --- | --- |
| 玩家 CombatComponent | `_parryStopSeconds` | 0.08 秒真实时间，0 关闭 |
| 玩家 CombatComponent | `_parryStopRate` | 0，完整时停 |
| 玩家 CombatComponent | `_parryInvulnerableSeconds` | 0.8 秒 |
| 玩家 PlayerCombat | `_disadvantageKnockback` | 3 米/秒后退，先清水平速度 |
| 敌人 CombatComponent | `_parryStaggerSeconds` | 0.3 秒世界时间的最短再起手间隔 |

时停使用 TimeManager 独立来源，输入、相机和成功火花走真实时间。玩家立即退出动作并关闭所有攻击框，保留接触姿态直到恢复，再混合回移动。敌人中断后进入 Hurt，不叠加普通受伤动画的额外冻帧。成功释放当前攻击组冷却，不重置其他技能冷却、耗能及高速 AB 交替记录；时停期间的新输入可缓冲。

## 结算约束

先收集同批框接触并按攻击身份去重，再关闭来袭来源，最后只通知一次玩家取消和反馈。伤害采样在拼刀之后；身体命中入口仅补查相同几何，背身、框外和无挥刀窗口仍受伤。扫掠同时考虑双方相对运动、旋转及玩家碰撞后的移动路径，并检查环境遮挡。

## 验证

在隔离工程运行 `Tools/ActionSequences/validate.ps1`，避免占用正在编辑的主工程：

```powershell
& Tools/ActionSequences/validate.ps1 -Stage EditMode -TestFilter 'Action;GameJam.FlowTests' -ValidationDirectory Tools/map-flow/obj/Validation
& Tools/ActionSequences/validate.ps1 -Stage PlayMode -TestFilter 'ParryTests;ProjectileHitTests;ActionPlayerPlayModeTests;SlowMotionFeedbackTests;HeavyLaserTests;GameJam.FlowTests' -ValidationDirectory Tools/map-flow/obj/Validation
```

测试记录保存在 `Temp/ParryRework20261018/`。核心用例覆盖实际 Bullet/敌人预制体、对象池复用、光束及蓄力中断、同批多目标、身体触发器优先级、框外与遮挡、双方高速相对扫掠、动作取消、连续输入与时停恢复。最终测试结果见该目录的 Summary.json。

最终结果（2026-10-18）：EditMode 178/178 通过；PlayMode 95/99 通过。4 项重型敌人导航、瞄准与正面碰撞旧失败已在 HEAD 原版本复现，最终失败集合与基线完全一致。Parry 专项全部通过；测试源码及资产已逐文件校验与工作区一致。
