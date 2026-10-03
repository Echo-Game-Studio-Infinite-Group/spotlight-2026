using UnityEngine;

// 战斗 ↔ 移动/能量层的接缝目录（战斗系统底层接口设计 §4.7 命令契约）：
// 战斗不直接改对方速度/位移（"Motor 唯一负责速度、胶囊、位移"，框架 4.1）
// IEnergyAccount 与 IMotorCommand 的定义随 PR#5 落在实现侧文件
// （Character/VectorEnergy.cs 与 Character/MobilityAbilities.cs）——此处不重复定义

/// <summary>击退命令接收方（结算③：向目标 Motor/投射物发冲量命令）。实现方自行折算抗击退力</summary>
public interface IKnockbackReceiver
{
    void OnKnockback(Vector3 impulse);
}

/// <summary>
/// Motor 落地时的战斗查询（§4.7：高速普攻落地未派生则清空动量——判定责任在 Motor，决策在战斗侧）
/// </summary>
public interface IMotorLandingClient
{
    bool ShouldClearMomentumOnLanding();
}

/// <summary>parry 成功的接收方：结算器配对成功后回调。玩家侧由 PlayerCombat 实现</summary>
public interface IParryReceiver
{
    void OnParrySuccess(in ParryInfo info);
}

/// <summary>parry 配对结果（结算器 → 接收方）。速度经济学与演出都挂在这次回调上</summary>
public struct ParryInfo
{
    public GameObject Parrier;          // 招架者
    public GameObject SourceAttacker;   // 被招架的攻击来源
    public DamageType SourceType;       // 近战才触发世界层时缓（飞行道具无额外效果，策划案）
    public int SourceInstanceId;        // 被招架的攻击实例
    public Vector3 ApproxPoint;         // 近似配对点（表现用）
}
