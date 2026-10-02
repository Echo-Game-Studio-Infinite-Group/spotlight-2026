using System;
using System.Collections.Generic;
using UnityEngine;

// 玩家动作状态机（战斗系统底层接口设计 §4.3）：持有当前攻击实例、推进段与相位、执行取消与激活
// 不做通用 HFSM / 技能宿主 / 挂载槽（框架 4.2 明文）——新招式 = 新 AttackDefinition SO + 取消表加行
//
// 每固定步流程（设计 §4.3 骨架）：
//   PhaseAdvance → PeekIntent（不消费）→ CancelTable.Query → CanActivate → Commit
//   任一步失败零副作用：不扣费 / 不吞输入 / 不打断原动作（框架 4.2 验收项）
//
// 执行序 -50：输入采样(-90)之后、移动(0)之前——招式的 MotionIntent 命令驱动本 tick 位移；
// 命中结算(+50)在移动之后由 DamageResolver 完成，本类不碰物理查询
[DefaultExecutionOrder(-50)]
[RequireComponent(typeof(PlayerMotor))]
public class PlayerCombat : MonoBehaviour, IParryReceiver, IMotorLandingClient
{
    [Header("招式资产（留空 = 该意图不映射、不会被消费）")]
    [Tooltip("常态普攻（速度 < 0.8x 阈值）")]
    public AttackDefinition NormalAttack;
    [Tooltip("高速普攻（速度 ≥ 0.8x 阈值，扫掠 + 速度伤害 + 落地清动量）")]
    public AttackDefinition FastAttack;
    [Tooltip("闪斩：parry/断肢后的派生技，伤害约为普攻 3 倍")]
    public AttackDefinition FlashSlash;
    [Tooltip("连斩（P2）：耗能高、清速、滞空多段")]
    public AttackDefinition Rashomon;
    [Tooltip("推斩（P1）：耗能随速度递减、可 parry、击退")]
    public AttackDefinition PushSlash;
    [Tooltip("冲刺一段/二段/三段（同键续接，段数计数在本类）")]
    public AttackDefinition DashStage1, DashStage2, DashStage3;

    [Header("取消与判定")]
    public AttackCancelTable CancelTable;
    [Tooltip("伤害判定框组件（挂在玩家上，形状由各招式段数据驱动）")]
    public Hitbox DamageHitbox;
    [Tooltip("parry 判定框组件（与伤害框分开）")]
    public Hitbox ParryHitbox;

    [Header("高速形态门槛（×地速阈值；策划案 0.8x 起高速普攻）")]
    public float FastAttackSpeedRatio = 0.8f;

    [Header("Parry 反馈（策划案：0.8s 无敌；近战来源世界层时缓 5%–10%，作用层待 D1）")]
    public float ParryInvulnSec = 0.8f;
    [Tooltip("闪斩派生窗口（时长待策划确认，设计 §4.2）")]
    public float ParryDeriveWindowSec = 0.5f;
    public float ParrySlowScale = 0.08f;
    public float ParrySlowDurationSec = 0.35f;
    public TimeLayer ParrySlowLayer = TimeLayer.World;

    [Header("冲刺三段（链窗口超时则段数归零）")]
    public float DashChainWindowSec = 0.9f;

    private PlayerMotor _motor;
    private IEnergyAccount _energy;      // 能量账户缝：当前由 PlayerMotor 实现，外迁 VectorEnergy 后无感切换
    private InputSampler _sampler;
    private HealthComponent _health;

    private static int _instanceCounter; // 攻击实例号（去重键）：每次 Commit 递增

    private AttackDefinition _def;
    private int _segment;
    private AttackPhaseKind _phase = AttackPhaseKind.Startup;
    private float _timeInPhase;
    private int _instanceId = -1;
    private float _speedSnapshot;
    private float _speedRatioSnapshot;
    private bool _isParryDeriveAttack;

    private float _deriveTimer;          // parry 派生窗口剩余
    private int _dashStage;
    private float _lastDashTime = -999f;
    private readonly Dictionary<AttackDefinition, float> _cooldowns = new Dictionary<AttackDefinition, float>();

    #region 对外读数（Motor 落地查询与 DebugHUD 用）

    public bool IsAttacking => _def != null;
    public AttackDefinition CurrentDefinition => _def;
    public int CurrentSegmentIndex => _segment;
    public AttackPhaseKind CurrentPhase => _phase;
    public float TimeInPhase => _timeInPhase;
    public float DeriveWindowRemaining => _deriveTimer;
    public float SpeedRatioSnapshot => _speedRatioSnapshot;

    public float CurrentSpeedRatio
    {
        get
        {
            if (_motor == null || _motor.Params == null || _motor.Params.GroundSpeedThreshold <= 0f) return 0f;
            return _motor.HorizontalSpeed / _motor.Params.GroundSpeedThreshold;
        }
    }

    #endregion

    #region 实例事件（音效/特效/动画/镜头订阅——逻辑立即生效，表现不推迟逻辑）

    /// <summary>招式起手（含取消后的新招式）。订阅方须在销毁时退订</summary>
    public event Action<AttackDefinition> AttackStarted;

    /// <summary>招式自然收尾（走完全部段）</summary>
    public event Action<AttackDefinition> AttackEnded;

    /// <summary>招式被取消：(原招式, 新招式)——取消表是否「转得动」的观测点</summary>
    public event Action<AttackDefinition, AttackDefinition> AttackCancelled;

    /// <summary>parry 成功（无敌/时缓/派生窗口已在本回调前生效）</summary>
    public event Action<ParryInfo> ParrySucceeded;

    #endregion

    private void Awake()
    {
        _motor = GetComponent<PlayerMotor>();
        // 能量账户统一由 VectorEnergy 持有（实现 IEnergyAccount）；PlayerMotor 的旧账户已废弃，不再回退
        _energy = GetComponent<VectorEnergy>();
        _sampler = GetComponent<InputSampler>();
        _health = GetComponent<HealthComponent>();
    }

    private void OnEnable()
    {
        _motor.LandingClient = this;
    }

    private void OnDisable()
    {
        if (_motor != null && _motor.LandingClient == this) _motor.LandingClient = null;
        ForceStopHitboxes(); // 对象失效时释放 Motor 悬挂与活跃判定框，不留残留
    }

    private void FixedUpdate()
    {
        if (TimeManager.IsPaused) return; // 暂停不出招（框架 4.3 验收项）；时停是持续效果、不占攻击状态位

        float dt = TimeManager.PlayerDeltaTime;
        AdvanceTimers(dt);
        AdvancePhase(dt);
        ApplyMotion(dt);

        InputSnapshot snapshot = _sampler != null ? _sampler.Snapshot : InputSnapshot.Empty;
        InputIntent intent = _sampler != null ? _sampler.Buffer.PeekIntent(snapshot) : InputIntent.None;
        AttackDefinition target = ResolveTarget(intent);
        if (target == null || !CanActivate(target)) return; // 零副作用路径
        Commit(target, intent);
    }

    #region 相位推进

    private void AdvanceTimers(float dt)
    {
        if (_deriveTimer > 0f) _deriveTimer -= dt;
    }

    private float CurrentPhaseDuration
    {
        get
        {
            AttackPhase phase = _def.Phases[_segment];
            switch (_phase)
            {
                case AttackPhaseKind.Startup: return phase.StartupSec;
                case AttackPhaseKind.Active: return phase.ActiveSec;
                default: return phase.RecoverySec;
            }
        }
    }

    private void AdvancePhase(float dt)
    {
        if (!IsAttacking) return;
        _timeInPhase += dt;
        float duration = CurrentPhaseDuration;
        int guard = 64; // 防御：全零窗口的垃圾数据不挂死循环
        while (_timeInPhase >= duration && guard-- > 0)
        {
            _timeInPhase -= duration;
            if (!EnterNextPhase()) return;
            duration = CurrentPhaseDuration;
        }
    }

    // 相位推进：Startup→Active（激活判定框）→Recovery（关闭判定框）→下一段 Startup / 收招。
    // 「连斩收尾不可取消」不需要禁止规则——收尾段恢复窗不开窗口即成立（设计 §4.2）
    private bool EnterNextPhase()
    {
        switch (_phase)
        {
            case AttackPhaseKind.Startup:
                _phase = AttackPhaseKind.Active;
                ActivateHitboxes();
                return true;

            case AttackPhaseKind.Active:
                DeactivateHitboxes();
                _phase = AttackPhaseKind.Recovery;
                return true;

            default:
                DeactivateHitboxes(); // 防御：段间 parry 框也不该留活
                if (_segment + 1 < _def.Phases.Length)
                {
                    _segment++;
                    _phase = AttackPhaseKind.Startup;
                    ApplySegmentMotionStart(_segment);
                    return true;
                }
                EndAttack();
                return false;
        }
    }

    private void EndAttack()
    {
        ForceStopHitboxes();
        AttackDefinition ended = _def;
        _def = null;
        _segment = 0;
        _timeInPhase = 0f;
        AttackEnded?.Invoke(ended);
    }

    #endregion

    #region 意图解析与提交

    // 意图 → 候选招式：攻击中只经取消表换招；起手走直接映射。
    // 闪避/折返/加速/高跳属移动与技能层，不占攻击状态位（框架 4.2）
    private AttackDefinition ResolveTarget(InputIntent intent)
    {
        if (intent == InputIntent.None || intent == InputIntent.TimeStop) return null;

        if (IsAttacking)
        {
            return CancelTable != null
                ? CancelTable.Query(_def, _segment, _phase, _timeInPhase, intent, CurrentSpeedRatio)
                : null;
        }

        switch (intent)
        {
            case InputIntent.Attack:
                // parry/断肢派生窗口内优先闪斩；否则按速度选常态/高速变体
                if (_deriveTimer > 0f && FlashSlash != null) return FlashSlash;
                return CurrentSpeedRatio >= FastAttackSpeedRatio && FastAttack != null ? FastAttack : NormalAttack;
            case InputIntent.Dash:
                return NextDashStage();
            case InputIntent.Rashomon:
                return Rashomon;
            case InputIntent.PushSlash:
                return PushSlash;
            default:
                return null;
        }
    }

    // 可行性验证（只读，零副作用）：冷却按玩家时间戳、速度门槛按即时速度比、能量只查余额
    private bool CanActivate(AttackDefinition def)
    {
        if (def == null || def.Phases == null || def.Phases.Length == 0) return false;
        if (_cooldowns.TryGetValue(def, out float lastUse)
            && TimeManager.PlayerTime - lastUse < def.CooldownSec) return false;
        float ratio = CurrentSpeedRatio;
        if (ratio < def.MinSpeedRatio || ratio > def.MaxSpeedRatio) return false;
        if (def.EnergyCost > 0f && (_energy == null || _energy.CurrentEnergy < def.EnergyCost)) return false;
        return true;
    }

    // 统一提交（设计 §4.3）：扣费 → 消费输入 → 记冷却 → 切动作 → Motor 命令。
    // 可行性已由 CanActivate 验证；消费输入放在扣费之后——失败路径根本走不到这里
    private void Commit(AttackDefinition def, InputIntent intent)
    {
        if (def.EnergyCost > 0f && !_energy.TrySpend(def.EnergyCost))
        {
            // 理论不可达（CanActivate 刚查过余额）；保留断言防并发路径悄悄吞招
            Debug.LogError($"[PlayerCombat] 提交阶段能量扣费失败（{def.name}）", this);
            return;
        }
        if (_sampler != null) _sampler.Buffer.ConsumeFor(intent, _sampler.Snapshot);
        _cooldowns[def] = TimeManager.PlayerTime;

        bool cancelledPrevious = IsAttacking;
        AttackDefinition previous = _def;
        ForceStopHitboxes();

        _def = def;
        _segment = 0;
        _phase = AttackPhaseKind.Startup;
        _timeInPhase = 0f;
        _instanceId = ++_instanceCounter;
        _speedSnapshot = _motor.HorizontalSpeed;   // 起手速度快照：高速伤害命中时不得重取（框架 4.2）
        _speedRatioSnapshot = CurrentSpeedRatio;
        _isParryDeriveAttack = def == FlashSlash;
        if (def == FlashSlash) _deriveTimer = 0f;  // 派生窗口已消费

        if (def.ClearSpeedOnStart) _motor.SetHorizontalSpeed(Vector3.zero);
        ApplySegmentMotionStart(0);
        AdvanceDashCounter(def);

        if (cancelledPrevious) AttackCancelled?.Invoke(previous, def);
        AttackStarted?.Invoke(def);
    }

    #endregion

    #region 判定框与位移意图

    private void ActivateHitboxes()
    {
        AttackPhase p = _def.Phases[_segment];
        if (DamageHitbox != null && p.HasDamage && p.DamageProfile != null)
        {
            DamageHitbox.Activate(new HitboxActivation
            {
                Attacker = gameObject,
                InstanceId = _instanceId,
                PhaseIndex = _segment,
                Profile = p.DamageProfile,
                Sweep = p.Sweep,
                BaseDamage = p.BaseDamage,
                SpeedDamageScale = p.SpeedDamageScale,
                DamageType = p.DamageType,
                Knockback = p.Knockback,
                HitStopSec = p.HitStopSec,
                SpeedSnapshot = _speedSnapshot,
                SpeedRatioSnapshot = _speedRatioSnapshot,
                FromParryDerive = _isParryDeriveAttack,
            });
        }
        if (ParryHitbox != null && p.HasParry && p.ParryProfile != null)
        {
            ParryHitbox.Activate(new HitboxActivation
            {
                Attacker = gameObject,
                InstanceId = _instanceId,
                PhaseIndex = _segment,
                Profile = p.ParryProfile,
                DamageType = DamageType.Melee, // parry 框的"来源类型"按近战语义供配对方向使用
            });
        }
    }

    private void DeactivateHitboxes()
    {
        if (DamageHitbox != null && DamageHitbox.IsActive) DamageHitbox.Deactivate();
        if (ParryHitbox != null && ParryHitbox.IsActive) ParryHitbox.Deactivate();
    }

    private void ForceStopHitboxes()
    {
        DeactivateHitboxes();
        _motor.SetGravitySuspended(false); // 滞空悬挂随攻击结束/取消一并释放
    }

    // 段起手的位移意图（执行全部走 Motor 命令）：滞空 = 悬挂重力并清竖直速度
    private void ApplySegmentMotionStart(int segment)
    {
        AttackPhase p = _def.Phases[segment];
        if (p.Motion == MotionIntentKind.Hover)
        {
            _motor.SetGravitySuspended(true);
            _motor.SetVerticalSpeed(0f);
        }
    }

    // 每 tick 位移意图：Advance/Dash 在 Startup+Active 期间沿面朝前移，
    // 撞墙截断由 CharacterController 扫掠天然保证（§4.7 场景一）
    private void ApplyMotion(float dt)
    {
        if (!IsAttacking) return;
        AttackPhase p = _def.Phases[_segment];
        if (_phase == AttackPhaseKind.Recovery) return;
        if ((p.Motion == MotionIntentKind.Advance || p.Motion == MotionIntentKind.Dash) && p.MotionSpeed > 0f)
        {
            _motor.RequestMove(transform.forward, p.MotionSpeed * dt);
        }
    }

    #endregion

    #region 冲刺三段与 parry 接收

    private AttackDefinition NextDashStage()
    {
        if (TimeManager.PlayerTime - _lastDashTime > DashChainWindowSec) _dashStage = 0;
        switch (_dashStage)
        {
            case 0: return DashStage1;
            case 1: return DashStage2;
            case 2: return DashStage3;
            default: return null; // 三段后等链超时归零（第三段长恢复窗即"强制收招"）
        }
    }

    private void AdvanceDashCounter(AttackDefinition def)
    {
        if (def == DashStage1) _dashStage = 1;
        else if (def == DashStage2) _dashStage = 2;
        else if (def == DashStage3) _dashStage = 3;
        else _dashStage = 0; // 非冲刺招式打断链

        if (def == DashStage1 || def == DashStage2 || def == DashStage3)
        {
            _lastDashTime = TimeManager.PlayerTime;
        }
    }

    // parry 反馈链（设计 §4.5 ①；结算器配对成功后回调）：
    // 0.8s 全类型无敌 + 近战来源世界层时缓（解除计时 unscaled，不自锁）+ 闪斩派生窗口 + 事件
    public void OnParrySuccess(in ParryInfo info)
    {
        if (_health != null) _health.GrantImmunity(DamageType.All, ParryInvulnSec);
        if (info.SourceType == DamageType.Melee && ParrySlowScale < 1f)
        {
            TimeManager.RegisterTimedScale(ParrySlowLayer, ParrySlowScale, ParrySlowDurationSec, "parry_slow");
        }
        _deriveTimer = ParryDeriveWindowSec;
        ParrySucceeded?.Invoke(info);
    }

    // IMotorLandingClient（§4.7）：高速普攻落地且派生窗口已过 → 清空动量（速度经济学）
    public bool ShouldClearMomentumOnLanding()
    {
        return IsAttacking
            && _def.ClearMomentumOnLandIfUnderived
            && _deriveTimer <= 0f;
    }

    #endregion

    #region 复位与调试

    /// <summary>整局重开复位（「重开无残留」验收项）：中止攻击、清冷却/派生/冲刺链</summary>
    public void ResetForRestart()
    {
        ForceStopHitboxes();
        _def = null;
        _deriveTimer = 0f;
        _dashStage = 0;
        _lastDashTime = -999f;
        _cooldowns.Clear();
    }

    // 调试：当前相位下各意图能取消到谁（DebugHUD 显示——取消表是否「转得动」必须肉眼可验，设计 §五）
    public string DescribeCancelOptions()
    {
        if (!IsAttacking || CancelTable == null) return string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (InputIntent intent in Enum.GetValues(typeof(InputIntent)))
        {
            if (intent == InputIntent.None || intent == InputIntent.TimeStop) continue;
            AttackDefinition target = CancelTable.Query(_def, _segment, _phase, _timeInPhase, intent, CurrentSpeedRatio);
            if (target != null) sb.Append(intent).Append("→").Append(target.name).Append("  ");
        }
        return sb.ToString();
    }

    #endregion
}
