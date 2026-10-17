using UnityEngine;

namespace GameJam.Flow
{
    [CreateAssetMenu(menuName = "超高速行者/流程/全局流程", fileName = "RunFlow")]
    public sealed class RunFlowConfig : ScriptableObject
    {
        [InspectorName("固定随机种子")] public int Seed = 2026;
        [InspectorName("起始混乱度")] public int InitialChaos;
        [InspectorName("普通混乱度下限")] public int MinimumChaos;
        [InspectorName("普通混乱度上限")] public int MaximumChaos = 100;
        [InspectorName("死亡扣除混乱度"), Min(0)] public int DeathPenalty = 10;
        [InspectorName("每次进入后的权重倍率"), Range(0.001f, 1f)] public float VisitWeightMultiplier = 0.25f;
        [InspectorName("每局只衰减一次")] public bool DecayOnlyOnce;
        [InspectorName("教程差分")] public RoomVariantDefinition InitialRoom;
        [InspectorName("最终房差分")] public RoomVariantDefinition FinalRoom;
        [InspectorName("普通选房池")] public RoomPoolDefinition[] NormalPools = new RoomPoolDefinition[0];
        [InspectorName("循环规则")] public LoopRuleDefinition[] Loops = new LoopRuleDefinition[0];
        [InspectorName("启用特殊路线")] public bool EnableSpecialRoutes;
        [InspectorName("负数房间池")] public RoomPoolDefinition NegativePool;
        [InspectorName("999房间池")] public RoomPoolDefinition ExceptionalPool;
        [InspectorName("特殊混乱度值")] public int ExceptionalChaos = 999;
        [InspectorName("裂隙伤害"), Min(0f)] public float FallDamage = 20f;
        [InspectorName("复活恢复满血")] public bool RestoreHealth = true;
        [InspectorName("复活清空能量")] public bool ResetEnergy = true;
        [InspectorName("复活重置视角朝向")] public bool ResetViewOnRecovery = true;
        [InspectorName("最短连接段长度"), Min(0.1f)] public float ConnectionLength = 8f;
        [InspectorName("连接段延长倍率"), Min(1.1f)] public float ConnectionGrowth = 2f;
        [InspectorName("空间装配尝试次数"), Min(1)] public int PlacementAttempts = 6;
        [InspectorName("房间安全间距"), Min(0f)] public float RoomClearance = 0.2f;
        [InspectorName("连接段地板厚度"), Min(0.05f)] public float FloorThickness = 0.3f;
        [InspectorName("连接段护栏厚度"), Min(0.05f)] public float RailThickness = 0.2f;
        [InspectorName("连接段护栏高度"), Min(0.1f)] public float RailHeight = 2f;
        [InspectorName("门板厚度"), Min(0.05f)] public float GateThickness = 0.2f;
        [InspectorName("过门后安全距离"), Min(0.1f)] public float EntryCommitDistance = 1f;
        [InspectorName("连接段材质")] public Material ConnectionMaterial;
    }
}
