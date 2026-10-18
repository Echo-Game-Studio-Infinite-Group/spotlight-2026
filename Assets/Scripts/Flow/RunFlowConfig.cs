using UnityEngine;

namespace GameJam.Flow
{
    [CreateAssetMenu(menuName = "超高速行者/流程/全局流程", fileName = "RunFlow")]
    public sealed class RunFlowConfig : ScriptableObject
    {
        [Header("整局与混乱度")]
        [Tooltip("同一套配表和同一个随机种子可重现相同抽房结果；重开整局会重置随机状态。")]
        [InspectorName("固定随机种子")] public int Seed = 2026;
        [Tooltip("整局开始时的混乱度，必须处于普通混乱度上下限之间。")]
        [InspectorName("起始混乱度")] public int InitialChaos;
        [Tooltip("普通选房允许的最低混乱度；低于此值时需要启用特殊路线并配置负数房间池。")]
        [InspectorName("普通混乱度下限")] public int MinimumChaos;
        [Tooltip("混乱度达到此值即优先选择最终房差分。")]
        [InspectorName("普通混乱度上限")] public int MaximumChaos = 100;
        [Tooltip("死亡恢复时扣除的混乱度；非致死裂隙掉落不扣混乱度。")]
        [InspectorName("死亡扣除混乱度"), Min(0)] public int DeathPenalty = 10;
        [Tooltip("每次实际进入差分后，抽取权重乘以此值。例如 0.25 表示降至原来的四分之一，预加载不计入。")]
        [InspectorName("每次进入后的权重倍率"), Range(0.001f, 1f)] public float VisitWeightMultiplier = 0.25f;
        [Tooltip("勾选后，每个差分在一局中最多衰减一次；否则每次实际进入都会继续衰减。")]
        [InspectorName("每局只衰减一次")] public bool DecayOnlyOnce;
        [Header("房间与抽取")]
        [Tooltip("整局开始时加载的差分，通常为教程箱庭。")]
        [InspectorName("教程差分")] public RoomVariantDefinition InitialRoom;
        [Tooltip("混乱度达到普通上限时进入的终点差分。")]
        [InspectorName("最终房差分")] public RoomVariantDefinition FinalRoom;
        [Tooltip("没有命中循环规则时，从这些地图池中的有效差分按权重抽取。")]
        [InspectorName("普通选房池")] public RoomPoolDefinition[] NormalPools = new RoomPoolDefinition[0];
        [Tooltip("按混乱度区间选择循环规则；各规则的区间不能重叠。")]
        [InspectorName("循环规则")] public LoopRuleDefinition[] Loops = new LoopRuleDefinition[0];
        [Header("特殊路线")]
        [Tooltip("开启后允许低于普通下限及命中特殊混乱度值时使用独立地图池；需要先制作并登记对应差分。")]
        [InspectorName("启用特殊路线")] public bool EnableSpecialRoutes;
        [Tooltip("混乱度低于普通下限时使用的地图池，其差分必须覆盖对应混乱度。")]
        [InspectorName("负数房间池")] public RoomPoolDefinition NegativePool;
        [Tooltip("混乱度等于特殊混乱度值时使用的地图池，默认特殊值为 999。")]
        [InspectorName("特殊房间池")] public RoomPoolDefinition ExceptionalPool;
        [Tooltip("精确命中此数值时进入特殊房间池，优先于普通终点判定；仅在特殊路线开启时有效。")]
        [InspectorName("特殊混乱度值")] public int ExceptionalChaos = 999;
        [Header("失败与恢复")]
        [Tooltip("落入房间裂隙时扣除的生命值；此伤害不受战斗无敌帧保护。")]
        [InspectorName("裂隙伤害"), Min(0f)] public float FallDamage = 20f;
        [Tooltip("勾选后死亡复活恢复满血；未勾选时恢复记录点保存的生命值。")]
        [InspectorName("复活恢复满血")] public bool RestoreHealth = true;
        [Tooltip("记录点恢复时是否清空玩家能量。")]
        [InspectorName("复活清空能量")] public bool ResetEnergy = true;
        [Tooltip("记录点恢复时是否将视角朝向重置为记录点朝向。")]
        [InspectorName("复活重置视角朝向")] public bool ResetViewOnRecovery = true;
        [Header("房间连接")]
        [Tooltip("两个箱庭之间连接段的起始长度，单位为米；空间冲突时会尝试延长。")]
        [InspectorName("最短连接段长度"), Min(0.1f)] public float ConnectionLength = 8f;
        [Tooltip("每次空间装配失败后，连接段长度乘以此倍率，必须大于 1。")]
        [InspectorName("连接段延长倍率"), Min(1.1f)] public float ConnectionGrowth = 2f;
        [Tooltip("最多尝试多少种连接段长度；全部失败后保持保护门关闭，可重试装配。")]
        [InspectorName("空间装配尝试次数"), Min(1)] public int PlacementAttempts = 6;
        [Tooltip("检查后继房间与已保留房间是否重叠时使用的安全间距，单位为米。")]
        [InspectorName("房间安全间距"), Min(0f)] public float RoomClearance = 0.2f;
        [Tooltip("动态连接段地板的厚度，单位为米。")]
        [InspectorName("连接段地板厚度"), Min(0.05f)] public float FloorThickness = 0.3f;
        [Tooltip("动态连接段两侧护栏的厚度，单位为米。")]
        [InspectorName("连接段护栏厚度"), Min(0.05f)] public float RailThickness = 0.2f;
        [Tooltip("动态连接段护栏的高度，单位为米。")]
        [InspectorName("连接段护栏高度"), Min(0.1f)] public float RailHeight = 2f;
        [Tooltip("动态连接段保护门板的厚度，单位为米。")]
        [InspectorName("门板厚度"), Min(0.05f)] public float GateThickness = 0.2f;
        [Tooltip("玩家穿过后继入口后继续前进此距离，才确认进入新房间，单位为米。")]
        [InspectorName("过门后安全距离"), Min(0.1f)] public float EntryCommitDistance = 1f;
        [Tooltip("动态连接段地板、护栏和门板使用的材质。")]
        [InspectorName("连接段材质")] public Material ConnectionMaterial;
    }
}
