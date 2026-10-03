using System;
using UnityEngine;

// 敌人攻击驱动（D3–D5 首个战斗闭环·敌人侧装配）：PlayerCombat 的同构轻量版——
// 复用 AttackDefinition SO + 相位推进（Startup→Active→Recovery）+ Hitbox 激活链；
// 砍掉取消表 / 输入仲裁 / 能量扣费 / parry 接收（敌人不招架——parry 配对的接收方只有玩家侧）
// 兼任灰盒感知决策（追击/停/攻击的最简距离规则）；寻路 AI（leo）落地后替换这段感知，
// TryStartAttack() 是留给 AI 的直调入口——AI 只发「打」的命令，相位与判定仍归本类
// 时间：全部走 TimeLayer.World（敌人属世界层——时停时敌人不动，框架 4.3）
[DefaultExecutionOrder(-50)] // 与 PlayerCombat 同位：攻击命令先行，EnemyMotor（0）后执行
[RequireComponent(typeof(EnemyMotor))]
public class EnemyCombat : MonoBehaviour
{
    [Header("攻击配置")]
    [Tooltip("攻击招式（与玩家同一种 SO：帧窗口/伤害/位移意图全在资产里调）")]
    public AttackDefinition Attack;

    [Tooltip("伤害判定框（挂在敌人上，形状由招式段数据驱动）")]
    public Hitbox DamageHitbox;

    [Header("灰盒感知（AI 落地后本段退役）")]
    [Tooltip("仇恨半径：玩家进入即直线追击")]
    public float AggroRange = 15f;

    [Tooltip("攻击距离：小于此值尝试出招（水平距离）")]
    public float AttackRange = 2.2f;

    [Tooltip("追击移动速度（米/秒）")]
    public float MoveSpeed = 3.5f;

    [Tooltip("两次出招的最小时间差（自出招起计、含招式时长；按世界层时间戳。与 Attack.CooldownSec 取大，防任一处忘配就无间隔）")]
    public float AttackIntervalSec = 1.5f;

    private EnemyMotor _motor;
    private HealthComponent _health;
    private Transform _player;           // 感知缓存：首次用时找一次，玩家不存在则保持空（待机）

    private AttackDefinition _def;
    private int _segment;
    private AttackPhaseKind _phase = AttackPhaseKind.Startup;
    private float _timeInPhase;
    private float _lastAttackTime = -999f;

    private static int _instanceCounter; // 攻击实例号（去重键）：每次出招递增

    private Vector3 _spawnPosition;      // 出生点（重开复位用）
    private Quaternion _spawnRotation;

    #region 对外读数（调试 HUD / AI 感知用）

    public bool IsAttacking => _def != null;
    public AttackPhaseKind CurrentPhase => _phase;

    /// <summary>出招事件（音效/特效/动画订阅）。与 PlayerCombat.AttackStarted 对齐，表现层可统一挂</summary>
    public event Action<AttackDefinition> AttackStarted;

    /// <summary>收招事件（走完全部段）</summary>
    public event Action<AttackDefinition> AttackEnded;

    #endregion

    private void Awake()
    {
        _motor = GetComponent<EnemyMotor>();
        _health = GetComponentInParent<HealthComponent>();
        _spawnPosition = transform.position; // 出生点以实例化时的初始位形为准（重开回这里）
        _spawnRotation = transform.rotation;
        if (_health != null) _health.Died += OnDied;
    }

    private void OnDestroy()
    {
        if (_health != null) _health.Died -= OnDied; // 订阅随绑定解除（Health 可能先销毁，判空防漏）
    }

    private void FixedUpdate()
    {
        if (TimeManager.IsPaused) return;
        float dt = TimeManager.WorldDeltaTime;
        if (dt <= 0f) return; // 时停（世界层 0）：敌人整体冻结——感知与出招判定也不得发生，只挡暂停会漏这条
        if (_health != null && _health.IsDead) return;

        AdvancePhase(dt);

        if (IsAttacking)
        {
            _motor.ClearMoveTarget(); // 攻击中不追击：位移只走招式的 Motion 意图
            ApplyMotion(dt);
        }
        else
        {
            PerceiveAndDecide(dt);
        }
    }

    #region 灰盒感知决策（AI 落地后整段替换）

    private void PerceiveAndDecide(float dt)
    {
        if (_player == null)
        {
            PlayerMotor playerMotor = FindFirstObjectByType<PlayerMotor>();
            if (playerMotor == null) return; // 场景无玩家（测试场景裁剪）：保持待机
            _player = playerMotor.transform;
        }

        float distSq = (_player.position - transform.position).sqrMagnitude;
        float attackRangeSq = AttackRange * AttackRange;

        if (distSq <= attackRangeSq)
        {
            _motor.ClearMoveTarget();
            if (TimeManager.WorldTime - _lastAttackTime >= EffectiveCooldown()) TryStartAttack();
        }
        else if (distSq <= AggroRange * AggroRange)
        {
            _motor.SetMoveTarget(_player, MoveSpeed);
        }
        else
        {
            _motor.ClearMoveTarget();
        }
    }

    private float EffectiveCooldown()
    {
        // 间隔配置优先：敌人攻击 SO 常不配冷却（CooldownSec 默认 0），两者取大避免任一处忘配就无间隔
        return Mathf.Max(AttackIntervalSec, Attack != null ? Attack.CooldownSec : 0f);
    }

    #endregion

    #region 出招与相位推进（与 PlayerCombat 同构，砍掉取消/扣费）

    /// <summary>
    /// 尝试出招（AI 直调入口）。攻击中/冷却中静默失败；成功 = 锁向玩家 + 相位从头推进
    /// </summary>
    public bool TryStartAttack()
    {
        if (IsAttacking || Attack == null || Attack.Phases == null || Attack.Phases.Length == 0) return false;

        _motor.FaceTowards(_player != null ? _player.position : transform.position + transform.forward);
        _def = Attack;
        _segment = 0;
        _phase = AttackPhaseKind.Startup;
        _timeInPhase = 0f;
        _lastAttackTime = TimeManager.WorldTime;
        AttackStarted?.Invoke(_def);
        return true;
    }

    // 相位推进：Startup→Active（激活判定框）→Recovery（关闭判定框）→下一段 / 收招
    private void AdvancePhase(float dt)
    {
        if (!IsAttacking) return;
        _timeInPhase += dt;

        float duration = CurrentPhaseDuration;
        int guard = 64; // 防御：全零窗口的垃圾数据不挂死循环（与 PlayerCombat 同款）
        while (_timeInPhase >= duration && guard-- > 0)
        {
            _timeInPhase -= duration;
            if (!EnterNextPhase()) return;
            duration = CurrentPhaseDuration;
        }
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

    private bool EnterNextPhase()
    {
        switch (_phase)
        {
            case AttackPhaseKind.Startup:
                _phase = AttackPhaseKind.Active;
                ActivateHitbox();
                return true;

            case AttackPhaseKind.Active:
                DeactivateHitbox();
                _phase = AttackPhaseKind.Recovery;
                return true;

            default:
                DeactivateHitbox(); // 防御：段间判定框不该留活
                if (_segment + 1 < _def.Phases.Length)
                {
                    _segment++;
                    _phase = AttackPhaseKind.Startup;
                    return true;
                }
                EndAttack();
                return false;
        }
    }

    private void EndAttack()
    {
        DeactivateHitbox();
        AttackDefinition ended = _def;
        _def = null;
        _segment = 0;
        _timeInPhase = 0f;
        AttackEnded?.Invoke(ended);
    }

    private void ActivateHitbox()
    {
        AttackPhase p = _def.Phases[_segment];
        if (DamageHitbox == null || !p.HasDamage || p.DamageProfile == null) return;
        DamageHitbox.Activate(new HitboxActivation
        {
            Attacker = gameObject,
            InstanceId = ++_instanceCounter,
            PhaseIndex = _segment,
            Profile = p.DamageProfile,
            Sweep = p.Sweep,
            BaseDamage = p.BaseDamage,
            SpeedDamageScale = p.SpeedDamageScale, // 敌人攻击通常配 0：速度加成是玩家的速度经济学
            DamageType = p.DamageType,
            Knockback = p.Knockback,
            HitStopSec = p.HitStopSec,
        });
    }

    private void DeactivateHitbox()
    {
        if (DamageHitbox != null && DamageHitbox.IsActive) DamageHitbox.Deactivate();
    }

    // 攻击中的位移意图（Advance/Dash）：与 PlayerCombat.ApplyMotion 同语义，撞墙经 Motor 扫掠截断
    private void ApplyMotion(float dt)
    {
        AttackPhase p = _def.Phases[_segment];
        if (_phase == AttackPhaseKind.Recovery) return;
        if ((p.Motion == MotionIntentKind.Advance || p.Motion == MotionIntentKind.Dash) && p.MotionSpeed > 0f)
        {
            _motor.RequestMove(transform.forward, p.MotionSpeed * dt);
        }
    }

    #endregion

    #region 死亡与复位

    // 死亡断肢简化版（设计 §4.6）：关阻挡碰撞停运动，尸体留场；掉落/配额归还由表现层另挂 Health.Died
    private void OnDied(DamageInfo info)
    {
        ForceStop();
        _motor.DisableLocomotion();
    }

    private void OnDisable() => ForceStop(); // 对象失效时判定框不留活（与 PlayerCombat 同款防御）

    private void ForceStop()
    {
        DeactivateHitbox();
        _def = null;
        _segment = 0;
        _timeInPhase = 0f;
    }

    /// <summary>
    /// 显式设定出生点（重开复位回这里）。Instantiate 流定位先于 Awake 无需调；
    /// 「new GameObject + 组装 + 后设位」的编辑器组装流必须调，否则 Awake 记到默认零点
    /// </summary>
    public void SetSpawnPoint(Vector3 position, Quaternion rotation)
    {
        _spawnPosition = position;
        _spawnRotation = rotation;
    }

    /// <summary>
    /// 重开复位（「重开无残留」验收项）：复活、回出生点、恢复阻挡。恢复策略参数化归重试灰盒，本方法只做全恢复
    /// </summary>
    public void ResetForRestart()
    {
        ForceStop();
        _lastAttackTime = -999f;
        _player = null; // 重新感知（玩家可能重开换位）
        if (_health != null) _health.ResetForRestart();
        _motor.EnableLocomotion();
        _motor.Teleport(_spawnPosition, _spawnRotation);
    }

    #endregion
}
