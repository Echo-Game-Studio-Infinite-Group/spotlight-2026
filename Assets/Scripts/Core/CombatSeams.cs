using UnityEngine;

// 战斗 ↔ 移动/能量层的接缝目录（战斗系统底层接口设计 §4.7 命令契约）：
// 战斗不直接改对方速度/位移（"Motor 唯一负责速度、胶囊、位移"，框架 4.1），
// 全部跨层交互走这里的小接口——接口形状待 3C 评审，本文件是契约的唯一落点

/// <summary>
/// 能量账户缝：PlayerCombat 扣费的唯一入口。当前实现方是 PlayerMotor（能量暂存移动层），
/// 后续能量外迁到 Character/VectorEnergy 时由其接管，战斗侧无感（框架 4.3）
/// </summary>
public interface IEnergyAccount
{
    float CurrentEnergy { get; }

    /// <summary>一次性扣费（不随时间缩放，框架 4.3）；不足则失败且零副作用</summary>
    bool TrySpend(float amount);
}

/// <summary>
/// 机动技能命令缝（能力层 → Motor）：加速/高跳/折返类技能不直接改 Motor 状态，经本接口请求。
/// 签名由移动侧维护；能力侧只引用，不得在别处定义本接口
/// </summary>
public interface IMotorCommand
{
    /// <summary>沿当前朝向把水平速度设为指定模长（加速技能）</summary>
    void SetHorizontalSpeed(float speed);

    /// <summary>竖直起跳（高跳技能）：走 Motor 内部起跳簿记（状态机切换与跳跃计数）</summary>
    void LaunchVertical(float upSpeed);

    /// <summary>水平折返（折返技能）：水平速度与朝向同时 180° 翻转，保速度模长</summary>
    void ReverseHorizontal();
}

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
