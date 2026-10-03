using UnityEngine;

// 矢量转换器的三个机动技能：加速 / 高跳 / 折返
// 技能只做三件事：收到"技能被触发"的通知 → 向能量账户提交扣费 → 向移动实现发命令
// 边界：速度与位移的唯一执行者是移动实现，本类不碰 Transform、不改速度字段
//
// 按键判定不在这里：游戏将统一由"按键预输入时间轴"管理组合键与时间窗，
// 本类只提供 TryTrigger 作为接入点，由那套系统在判定成立时调用。下面的 Update 只是占位，
// 等时间轴落地后删掉即可，其余逻辑不受影响。
//
// 待接：折返的"0.5s 内提高视角转换灵敏度"归相机侧；本类先不处理
public class MobilityAbilities : MonoBehaviour
{
    [Tooltip("技能触发参数")]
    [SerializeField] private EnergyParams _params;

    [Tooltip("物理数值：地速阈值、跳跃初速——技能要按物理量发命令，故与能量参数分开取")]
    [SerializeField] private MovementParams _movementParams;

    [SerializeField] private VectorEnergy _energy;

    // 用接口而不是具体类型：以后换移动实现（PlayerMotor / CharacterMovement）本类不用改
    [SerializeField] private MonoBehaviour _motorSource;

    // 跳跃高度翻倍对应初速 ×√2（h = v²/2g），不是数值设计，是物理
    private const float HighJumpSpeedFactor = 1.4142136f;

    private IMotorCommand _motor;

    private void Awake()
    {
        _motor = _motorSource as IMotorCommand;
        if (_motor == null)
        {
            Debug.LogError("[MobilityAbilities] _motorSource 未指定或未实现 IMotorCommand，机动技能已禁用", this);
            enabled = false;
        }
    }

    /// <summary>能量是否够放这个技能（给输入层做判定用，不扣费）。账户未装配时一律返回 false</summary>
    public bool CanAfford(MobilityAbility ability)
    {
        if (!isActiveAndEnabled || _motor == null || _params == null || _energy == null) return false;

        switch (ability)
        {
            case MobilityAbility.Accelerate: return _energy.CurrentEnergy >= _params.AccelerateCost;
            case MobilityAbility.HighJump: return _energy.CurrentEnergy >= _params.HighJumpCost;
            case MobilityAbility.Reverse: return _energy.CurrentEnergy >= _params.ReverseCost;
            default: return false;
        }
    }

    /// <summary>
    /// 技能触发入口：按键时间轴判定成立后调用。
    /// 本方法只负责扣费与发命令，不参与任何按键判定，因此可被将来的输入系统反复调用。
    /// </summary>
    /// <param name="ability">本次要释放的机动技能</param>
    /// <returns>真正生效返回 true；能量不足或执行端未就绪返回 false</returns>
    public bool TryTrigger(MobilityAbility ability)
    {
        if (!isActiveAndEnabled || _motor == null || _params == null || _movementParams == null || _energy == null) return false;

        switch (ability)
        {
            case MobilityAbility.Accelerate:
                if (!_energy.TrySpend(_params.AccelerateCost)) return false;
                _motor.SetHorizontalSpeed(_movementParams.GroundSpeedThreshold);
                return true;

            case MobilityAbility.HighJump:
                if (!_energy.TrySpend(_params.HighJumpCost)) return false;
                _motor.LaunchVertical(_movementParams.JumpSpeed * HighJumpSpeedFactor);
                return true;

            case MobilityAbility.Reverse:
                if (!_energy.TrySpend(_params.ReverseCost)) return false;
                _motor.ReverseHorizontal();
                return true;

            default:
                return false;
        }
    }

    // 空占位：按键判定一律不在这里做。
    // 本类只提供 TryTrigger / CanAfford 两个接入点，由输入层判定成立后调用；
    // 具体走哪条输入链路（PlayerInputReader / InputBuffer / 未来重做的输入系统）待定，
    // 所以这里不预留任何判定代码——避免和输入侧的时间窗规则并存成两套。
    private void Update()
    {
    }
}

/// <summary>机动技能种类。新增技能时在这里加，并在 TryTrigger 里补一个 case</summary>
public enum MobilityAbility
{
    Accelerate,   // 加速：右键 + W
    HighJump,     // 高跳：右键 + 空格
    Reverse,      // 折返：右键 + S
}

// 机动技能向移动实现发出的命令。PlayerMotor（或备选的 CharacterMovement）负责实现；
// 技能侧只认本接口，实现里怎么改速度是它自己的事。
public interface IMotorCommand
{
    /// <summary>把水平速度设成指定大小，方向保持当前水平朝向</summary>
    void SetHorizontalSpeed(float speed);

    /// <summary>以指定垂直初速起跳，水平分量不动</summary>
    void LaunchVertical(float upSpeed);

    /// <summary>水平速度反向、大小不变</summary>
    void ReverseHorizontal();
}
