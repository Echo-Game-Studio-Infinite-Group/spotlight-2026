namespace MovementSim;

// <summary>
// 移动算法参数。全部为占位基线值（任务建议值），实装时必须数据驱动（ScriptableObject / Inspector 字段）。
// 速度单位 = 地速阈值倍数（策划案 §4 将地速阈值归一化为 1）。
// 约束：TickRate 必须与 Unity Fixed Timestep（1/60）一致，否则所有按秒标定的系数全部失效。
// </summary>
public sealed record MovementParams
{
    public int TickRate { get; init; } = 60;
    public double Dt => 1.0 / TickRate;

    public double WishSpeed { get; init; } = 1.0;          // 奔跑 wishspeed（Quake accelerate 的速度预算）
    public double Accel { get; init; } = 10.0;             // 地面加速系数 (1/s)
    public double FrictionMu { get; init; } = 6.0;         // 摩擦系数 (1/s)，仅速度>阈值且不在免摩擦窗口时生效
    public double GroundThreshold { get; init; } = 1.0;    // 地速阈值（归一化基准）
    public double WalkTarget { get; init; } = 0.5;         // 行走目标速度（策划案 §1：固定较小值）
    public double WalkApproachRate { get; init; } = 2.0;   // 行走趋近速率 (1/s)，占位
    public double Gravity { get; init; } = 20.0;           // 重力，占位（手感优先，非真实 9.8）
    public double JumpVel { get; init; } = 7.0;            // 起跳垂直速度；滞空时长 ≈ 2*JumpVel/Gravity ≈ 0.7s
    public double FrictionFreeWindow { get; init; } = 0.2; // 落地免摩擦窗口 (s)，兔子跳加速的唯一来源（策划案 §2）
    public double EnergyCap { get; init; } = 200.0;        // 矢量转换器能量上限（策划案 §4）
    public double SlideDecay { get; init; } = 3.0;         // 滑铲速度指数衰减系数 (1/s)，占位
    public double SlideEnterSpeed { get; init; } = 0.8;    // 滑铲进入速度（策划案 §3：约 0.8x 阈值）
    public double SlideExitSpeed { get; init; } = 0.3;     // 滑铲结束速度，占位

    // 泵油模式说明（本仿真最重要的模型决策，详见 results/report.md 第 1 节）：
    // 字面 Quake accelerate 的投影上限在直线上（wishdir 与速度同向）把地速钉死在 wishspeed，
    // 而摩擦只在速度>阈值时生效、窗口只是免摩擦——字面模型下跑-跳循环只能保速、永远零增长，
    // 无法复现策划案 §1「奔跑则将速度不断增加」+ §2「落地 0.2s 免摩擦 → 不断加速」的因果链。
    // WindowPump 仅在免摩擦窗口内去掉投影上限，让摩擦（而非投影）充当唯一地面上限——模型唯一偏差。
    public PumpMode Pump { get; init; } = PumpMode.WindowPump;
}

public enum PumpMode
{
    VerbatimQuake, // 任务公式的字面模型：加速始终受投影上限约束（仿真结论：只保速、零增长）
    WindowPump,    // 修正模型：免摩擦窗口内泵加速不受投影上限约束
}

public enum Locomotion
{
    None, // 本 tick 无移动输入（滑铲/起跳等占用）
    Walk, // 行走：速度趋近 WalkTarget
    Run,  // 奔跑：Quake accelerate 持续加速
}

// <summary>
// 纯算法仿真器：无 IO、无随机、无引擎依赖。结算粒度与 Unity PlayerMotor 的 tick 结算一一对应，便于移植。
// 结算顺序约束：摩擦 → 加速 → 起跳/重力 → 落地判定 → 能量/位移；顺序改变会改变平衡点，移植时必须保持。
// </summary>
public sealed class MovementSimulator
{
    private readonly MovementParams _p;

    public MovementSimulator(MovementParams p)
    {
        _p = p;
        // 初始视为已接地很久：助跑阶段不得享受免摩擦窗口
        GroundTicks = int.MaxValue / 2;
    }

    public double Speed { get; private set; }      // 水平速度（阈值倍数）
    public double Height { get; private set; }     // 离地高度
    public double VerticalVel { get; private set; }
    public bool Grounded { get; private set; } = true;
    public int GroundTicks { get; private set; }   // 落地以来完成的地面 tick 数（落地瞬间清零）
    public double Energy { get; private set; }
    public long TickIndex { get; private set; }    // 已结算 tick 总数
    public double Distance { get; private set; }   // 水平位移（阈值·秒，换算米需另加尺度参数）
    public bool Sliding { get; private set; }

    // 免摩擦窗口判定：以"已完成的地面 tick 数"计，落地后前 window/Dt 个地面 tick 在窗口内
    public bool InFrictionFreeWindow => Grounded && GroundTicks * _p.Dt < _p.FrictionFreeWindow;

    // 仿真注入入口：仅供滑铲等需要指定初速的场景使用，正常流程禁止调用
    public void ForceGroundSpeed(double speed)
    {
        Speed = speed;
        Grounded = true;
    }

    // 输入按 tick 生效：jump 为本 tick 的起跳请求（滞空预输入缓冲由驱动层负责，策划案 §2）
    public void Tick(Locomotion loco, bool jump, bool slideHeld)
    {
        TickIndex++;
        if (Grounded)
        {
            bool inWindow = InFrictionFreeWindow;
            if (jump)
            {
                // 起跳消费整个 tick：不结算地面加速/摩擦；滑铲可被前跳取消（策划案 §3）
                VerticalVel = _p.JumpVel;
                Height += VerticalVel * _p.Dt;
                Grounded = false;
                Sliding = false;
            }
            else if (Sliding || (slideHeld && Speed >= _p.SlideEnterSpeed))
            {
                // 滑铲：指数快速衰减，替代该 tick 的摩擦与加速；衰减到结束速度后滑铲终止
                Speed = ApplySlideDecay(Speed, _p);
                Sliding = Speed >= _p.SlideExitSpeed;
            }
            else
            {
                Speed = ApplyFriction(Speed, _p, inWindow);
                switch (loco)
                {
                    case Locomotion.Walk:
                        Speed = ApplyWalk(Speed, _p);
                        break;
                    case Locomotion.Run:
                        Speed = ApplyGroundAccelerate(Speed, _p, inWindow);
                        break;
                    case Locomotion.None:
                        break;
                }
            }
            GroundTicks++;
        }
        else
        {
            // 滞空：水平速度不变（本次仿真不做空中转向）；半隐式欧拉积分，落地判 y<=0
            VerticalVel -= _p.Gravity * _p.Dt;
            Height += VerticalVel * _p.Dt;
            if (Height <= 0.0)
            {
                Height = 0.0;
                VerticalVel = 0.0;
                Grounded = true;
                GroundTicks = 0; // 落地即开启新的免摩擦窗口——兔子跳循环的关键
                Sliding = false;
            }
        }

        // 能量积累不影响速度（策划案 §4）；上限在累加后立即钳制
        Energy = Math.Min(_p.EnergyCap, Energy + Math.Max(0.0, Speed - _p.GroundThreshold) * _p.Dt);
        Distance += Speed * _p.Dt;
    }

    // 地面摩擦（Quake friction）：仅当速度 > 地速阈值 且 不在落地免摩擦窗口内
    public static double ApplyFriction(double speed, MovementParams p, bool inWindow)
        => inWindow || speed <= p.GroundThreshold
            ? speed
            : speed * Math.Max(0.0, 1.0 - p.FrictionMu * p.Dt);

    // 地面加速（Quake accelerate）：addspeed = wishspeed - dot(v, wishdir)，为正则
    // v += wishdir * min(accel*wishspeed*dt, addspeed)。直线仿真中 wishdir 与速度同向，
    // dot(v,wishdir)=|v|，投影上限因此把地速钉在 wishspeed。
    public static double ApplyGroundAccelerate(double speed, MovementParams p, bool inWindow)
    {
        double step = p.Accel * p.WishSpeed * p.Dt;
        if (p.Pump == PumpMode.WindowPump && inWindow)
        {
            return speed + step; // 泵加速：窗口内不受投影上限（模型相对任务公式的唯一偏差）
        }
        double addspeed = p.WishSpeed - speed;
        return addspeed > 0.0 ? speed + Math.Min(step, addspeed) : speed;
    }

    // 行走：速度以固定速率直线趋近 WalkTarget（策划案 §1：快速增加至固定较小值）
    public static double ApplyWalk(double speed, MovementParams p)
    {
        double step = p.WalkApproachRate * p.Dt;
        double diff = p.WalkTarget - speed;
        return Math.Abs(diff) <= step ? p.WalkTarget : speed + Math.Sign(diff) * step;
    }

    // 滑铲：指数快速衰减（策划案 §3：滑铲过程中速度快速衰减）
    public static double ApplySlideDecay(double speed, MovementParams p)
        => speed * Math.Exp(-p.SlideDecay * p.Dt);
}
