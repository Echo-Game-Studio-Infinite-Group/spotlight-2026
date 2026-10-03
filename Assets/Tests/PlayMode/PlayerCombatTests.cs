using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 玩家动作状态机回归（设计 §4.3 / §4.2，框架 4.2 验收项）：
// 激活失败零副作用（能量不足/冷却/速度门槛 → 不扣费、不吞输入、不打断原动作——失败路径任一步都不留痕迹）、
// 统一提交链（扣费→消费输入→切动作→事件）、常态/高速变体选择、parry 派生窗口优先闪斩、
// 冲刺三段链窗口、parry 反馈链（无敌/时缓/事件）、重开复位
// 装配：真实 PlayerMotor + 注入 MovementParams + 无设备采样的 InputSampler（快照与缓冲手动喂），
// 判定框留空——本文件只锁状态机语义，采集/结算在 DamageResolverTests 覆盖
public class PlayerCombatTests
{
    private GameObject _player;
    private PlayerMotor _motor;
    private PlayerCombat _combat;
    private InputSampler _sampler;
    private MovementParams _params;
    private EnergyParams _energyParams;
    private VectorEnergy _energy;
    private GameObject _timeGo;

    private int _started, _ended, _cancelled, _parried;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        _params = ScriptableObject.CreateInstance<MovementParams>();
        // 地速阈值用默认 10：测试里 SetHorizontalSpeed 直接按倍数控制 CurrentSpeedRatio

        _player = new GameObject("CombatTestPlayer");
        _player.SetActive(false); // 先注入参数再激活，保证 Awake 读到注入值（MovementBhopTests 同款纪律）
        CharacterController controller = _player.AddComponent<CharacterController>();
        controller.radius = _params.CapsuleBaseRadius;
        controller.height = _params.CapsuleBaseHeight;
        controller.center = new Vector3(0f, controller.height * 0.5f, 0f);

        _motor = _player.AddComponent<PlayerMotor>();
        _motor.SetParams(_params);
        _energyParams = ScriptableObject.CreateInstance<EnergyParams>();
        _energy = _player.AddComponent<VectorEnergy>();
        _energy.SetParams(_energyParams, _params); // 唯一能量账户：PlayerCombat 扣费经接口走这里
        _combat = _player.AddComponent<PlayerCombat>();
        _sampler = _player.AddComponent<InputSampler>();
        _player.AddComponent<HealthComponent>(); // OnParrySuccess 授无敌的目标
        _player.SetActive(true);

        // InputSampler 无设备采样：按下沿与按住态快照全部由测试手动喂（Buffer.Push / InjectSnapshot）

        _started = _ended = _cancelled = _parried = 0;
        _combat.AttackStarted += _ => _started++;
        _combat.AttackEnded += _ => _ended++;
        _combat.AttackCancelled += (_, _) => _cancelled++;
        _combat.ParrySucceeded += _ => _parried++;

        yield return null; // 至少跑一个固定步，让各组件完成首帧初始化
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        TimeManager.ResetAll(); // parry 时缓测试登记过来源——不清理会漏给其他测试
        if (_timeGo != null) Object.Destroy(_timeGo);
        if (_player != null) Object.Destroy(_player);
        if (_params != null) Object.Destroy(_params);
        if (_energyParams != null) Object.Destroy(_energyParams);
        _player = null;
        _params = null;
        yield return null;
    }

    // —— 工具 —— //

    private static AttackDefinition NewDef(float energyCost = 0f, float cooldown = 0f,
        float minRatio = 0f, float maxRatio = float.PositiveInfinity)
    {
        AttackDefinition def = ScriptableObject.CreateInstance<AttackDefinition>();
        def.EnergyCost = energyCost;
        def.CooldownSec = cooldown;
        def.MinSpeedRatio = minRatio;
        def.MaxSpeedRatio = maxRatio;
        return def;
    }

    // 缩短帧窗口（秒）：默认三窗合计 0.45s 太拖，冲刺链测试需要紧凑时序
    private static void SetPhaseWindows(AttackDefinition def, float startup, float active, float recovery)
    {
        AttackPhase phase = def.Phases[0];
        phase.StartupSec = startup;
        phase.ActiveSec = active;
        phase.RecoverySec = recovery;
    }

    private void PressAttack()
    {
        _sampler.InjectSnapshot(new InputSnapshot { AttackHeld = true });
        _sampler.Buffer.Push(LogicalButton.Attack);
    }

    private void PressDash()
    {
        _sampler.InjectSnapshot(new InputSnapshot { DashHeld = true, AttackHeld = true });
        _sampler.Buffer.Push(LogicalButton.Attack);
    }

    private static IEnumerator RunFor(float seconds) =>
        new WaitForSecondsRealtime(seconds); // 60Hz 默认固定步下真实秒与逻辑秒一致

    // —— a. 激活失败零副作用：能量不足 → 不扣费、不吞输入、不打断待机 —— //
    [UnityTest]
    public IEnumerator EnergyTooLow_ZeroSideEffect_ButInputStaysBuffered()
    {
        AttackDefinition def = NewDef(energyCost: 50f);
        _combat.NormalAttack = def;
        _energy.SetForDebug(10f);

        PressAttack();
        yield return RunFor(0.02f);

        Assert.That(_combat.IsAttacking, Is.False, "能量不足不得出招");
        Assert.That(_energy.CurrentEnergy, Is.EqualTo(10f).Within(1e-3f), "失败路径不得扣费");
        Assert.That(_sampler.Buffer.HasBuffered(LogicalButton.Attack), Is.True,
            "失败路径不得吞输入——按下沿保留在缓冲（框架 4.1：暂不可执行时保留到过期）");

        // 同一条目在能量补足后立即可用：验证「保留」而非「作废」
        _energy.SetForDebug(100f);
        yield return RunFor(0.02f);
        Assert.That(_combat.IsAttacking, Is.True, "能量补足后被保留的输入应能出招");
        Assert.That(_energy.CurrentEnergy, Is.EqualTo(50f).Within(1e-3f), "成功提交才扣费");
        Object.Destroy(def);
    }

    // —— b. 冷却中零副作用：冷却按玩家时间戳，未到不得出招 —— //
    [UnityTest]
    public IEnumerator Cooldown_BlocksActivation_ZeroSideEffect()
    {
        AttackDefinition def = NewDef(cooldown: 10f);
        _combat.NormalAttack = def;

        PressAttack();
        yield return RunFor(0.03f);
        Assert.That(_combat.IsAttacking, Is.True, "首次出招不受冷却限制");

        yield return RunFor(0.5f); // 走完 0.45s 全相位收招
        Assert.That(_combat.IsAttacking, Is.False);

        PressAttack();
        yield return RunFor(0.03f);
        Assert.That(_combat.IsAttacking, Is.False, "冷却未到不得再次出招");
        Assert.That(_sampler.Buffer.HasBuffered(LogicalButton.Attack), Is.True, "冷却拦截不得吞输入");
        Object.Destroy(def);
    }

    // —— c. 速度门槛零副作用：Min/MaxSpeedRatio 都不满足时不出招不扣费 —— //
    [UnityTest]
    public IEnumerator SpeedGates_BlockActivation_BothWays()
    {
        AttackDefinition def = NewDef(minRatio: 2f, maxRatio: 1.5f); // 故意无解区间
        _combat.NormalAttack = def;

        _motor.SetHorizontalSpeed(new Vector3(10f, 0f, 0f)); // ratio 1.0
        PressAttack();
        yield return RunFor(0.03f);
        Assert.That(_combat.IsAttacking, Is.False, "速度比 1.0 落在 [2, 1.5] 之外不得出招");
        Assert.That(_sampler.Buffer.HasBuffered(LogicalButton.Attack), Is.True, "门槛拦截不得吞输入");

        _motor.SetHorizontalSpeed(new Vector3(25f, 0f, 0f)); // ratio 2.5 超上限
        yield return RunFor(0.03f);
        Assert.That(_combat.IsAttacking, Is.False, "超上限速度同样不得出招（常态/高速变体互斥的基础）");
        Object.Destroy(def);
    }

    // —— d. 统一提交链：扣费 + 消费输入 + 切动作 + 起手事件 —— //
    [UnityTest]
    public IEnumerator Commit_SpendsEnergy_ConsumesInput_SwitchesState_FiresEvent()
    {
        AttackDefinition def = NewDef(energyCost: 20f);
        _combat.NormalAttack = def;
        _energy.SetForDebug(100f);

        PressAttack();
        yield return RunFor(0.03f);

        Assert.That(_combat.IsAttacking, Is.True, "应进入攻击状态");
        Assert.That(_combat.CurrentDefinition, Is.EqualTo(def), "当前招式应为被映射的定义");
        Assert.That(_energy.CurrentEnergy, Is.EqualTo(80f).Within(1e-3f), "提交时应一次性扣费");
        Assert.That(_sampler.Buffer.HasBuffered(LogicalButton.Attack), Is.False, "提交时应消费触发键");
        Assert.That(_started, Is.EqualTo(1), "起手应恰好触发一次事件");

        yield return RunFor(0.5f); // 收招
        Assert.That(_ended, Is.EqualTo(1), "自然收尾应触发 AttackEnded");
        Object.Destroy(def);
    }

    // —— e. 取消链：取消表命中后换招并发取消事件 —— //
    [UnityTest]
    public IEnumerator CancelTableHit_SwitchesToFiresCancelEvent()
    {
        AttackDefinition a = NewDef();
        AttackDefinition b = NewDef();
        SetPhaseWindows(a, 0.1f, 0.1f, 0.2f);
        _combat.NormalAttack = a;

        AttackCancelTable table = ScriptableObject.CreateInstance<AttackCancelTable>();
        table.Rules = new[]
        {
            new CancelRule
            {
                SourceAttack = a, SourcePhase = AttackPhaseKind.Recovery,
                TargetAttack = b, RequiredIntent = InputIntent.Attack,
            },
        };
        _combat.CancelTable = table;

        PressAttack();
        yield return RunFor(0.03f);
        Assert.That(_combat.CurrentDefinition, Is.EqualTo(a));

        yield return RunFor(0.25f); // 跨过 Startup+Active，进入 Recovery（0.2s 收招窗内）
        Assert.That(_combat.CurrentPhase, Is.EqualTo(AttackPhaseKind.Recovery), "前置：应已推进到恢复窗");

        PressAttack(); // 取消输入
        yield return RunFor(0.03f);

        Assert.That(_combat.CurrentDefinition, Is.EqualTo(b), "取消表命中应切到目标招式");
        Assert.That(_cancelled, Is.EqualTo(1), "取消应触发 AttackCancelled(原, 新) 事件");
        Object.Destroy(a);
        Object.Destroy(b);
        Object.Destroy(table);
    }

    // —— f. 变体选择：速度比 ≥ 0.8 选高速变体，否则常态（策划案 §5） —— //
    [UnityTest]
    public IEnumerator AttackVariant_SelectedBySpeedRatio()
    {
        AttackDefinition normal = NewDef();
        AttackDefinition fast = NewDef();
        _combat.NormalAttack = normal;
        _combat.FastAttack = fast;

        _motor.SetHorizontalSpeed(new Vector3(5f, 0f, 0f)); // ratio 0.5 < 0.8
        PressAttack();
        yield return RunFor(0.03f);
        Assert.That(_combat.CurrentDefinition, Is.EqualTo(normal), "低速起手应为常态普攻");

        yield return RunFor(0.5f); // 收招
        _motor.SetHorizontalSpeed(new Vector3(8f, 0f, 0f)); // ratio 0.8 恰在门槛（策划案 0.8x 起高速）
        PressAttack();
        yield return RunFor(0.03f);
        Assert.That(_combat.CurrentDefinition, Is.EqualTo(fast), "速度比达门槛（含等于）应为高速普攻");
        Object.Destroy(normal);
        Object.Destroy(fast);
    }

    // —— g. parry 派生窗口：窗口内普攻优先派生闪斩 —— //
    [UnityTest]
    public IEnumerator ParryDeriveWindow_PrioritizesFlashSlash()
    {
        AttackDefinition flash = NewDef();
        AttackDefinition normal = NewDef();
        _combat.FlashSlash = flash;
        _combat.NormalAttack = normal;

        // Projectile 来源：不进时缓分支（时缓链在独立测试覆盖，本测不建 TimeManager）
        _combat.OnParrySuccess(new ParryInfo { SourceType = DamageType.Projectile, SourceInstanceId = 1 });
        Assert.That(_combat.DeriveWindowRemaining, Is.GreaterThan(0f), "parry 成功应授予派生窗口");

        PressAttack();
        yield return RunFor(0.03f);
        Assert.That(_combat.CurrentDefinition, Is.EqualTo(flash), "派生窗口内左键应优先闪斩");
        Assert.That(_parried, Is.EqualTo(1), "parry 反馈应恰好触发一次事件");
        Object.Destroy(flash);
        Object.Destroy(normal);
    }

    // —— h. 冲刺三段链：窗口内依次推进，三段后不再续接，超窗归零（设计 §4.3） —— //
    [UnityTest]
    public IEnumerator DashChain_ThreeStages_ThenResetAfterWindow()
    {
        AttackDefinition d1 = NewDef(), d2 = NewDef(), d3 = NewDef();
        SetPhaseWindows(d1, 0.05f, 0.05f, 0.1f);
        SetPhaseWindows(d2, 0.05f, 0.05f, 0.1f);
        SetPhaseWindows(d3, 0.05f, 0.05f, 0.1f);
        _combat.DashStage1 = d1;
        _combat.DashStage2 = d2;
        _combat.DashStage3 = d3;
        _combat.DashChainWindowSec = 0.5f; // 收紧链窗口，缩短测试时长（语义与默认一致）

        PressDash();
        yield return RunFor(0.03f);
        Assert.That(_combat.CurrentDefinition, Is.EqualTo(d1), "第一段冲刺");

        yield return RunFor(0.22f); // 收招（0.2s 全相位）后仍在链窗口内
        PressDash();
        yield return RunFor(0.03f);
        Assert.That(_combat.CurrentDefinition, Is.EqualTo(d2), "链窗口内续接第二段");

        yield return RunFor(0.22f);
        PressDash();
        yield return RunFor(0.03f);
        Assert.That(_combat.CurrentDefinition, Is.EqualTo(d3), "链窗口内续接第三段");

        yield return RunFor(0.22f);
        PressDash();
        yield return RunFor(0.03f);
        Assert.That(_combat.IsAttacking, Is.False, "三段后（未超窗）不再映射第四段——强制收招语义");

        yield return RunFor(0.4f); // 链窗口超时归零
        PressDash();
        yield return RunFor(0.03f);
        Assert.That(_combat.CurrentDefinition, Is.EqualTo(d1), "超窗后段数归零，从第一段重来");
        Object.Destroy(d1);
        Object.Destroy(d2);
        Object.Destroy(d3);
    }

    // —— i. parry 反馈链（近战来源）：无敌授予 + 作用层时缓 + 到期恢复 —— //
    [UnityTest]
    public IEnumerator ParryFeedback_MeleeGrantsInvulnAndLayerSlow()
    {
        _timeGo = new GameObject("TimeManager_CombatTest");
        _timeGo.AddComponent<TimeManager>();
        yield return RunFor(0.05f);

        _combat.OnParrySuccess(new ParryInfo { SourceType = DamageType.Melee, SourceInstanceId = 1 });

        HealthComponent health = _player.GetComponent<HealthComponent>();
        Assert.That(health.IsImmuneTo(DamageType.Melee, health.LayerNow), Is.True,
            "parry 成功应立即授予全类型无敌（0.8s，策划案）");

        // 层 dt 只在 FixedUpdate 重算——等至少一个固定步后缩放读数才可见
        yield return RunFor(0.05f);

        // 作用层读字段断言：D1 若改 parry 时缓作用层，只改 ParrySlowLayer 字段值，本断言自动跟随
        bool slowOnWorld = _combat.ParrySlowLayer == TimeLayer.World;
        float slowed = slowOnWorld ? TimeManager.WorldDeltaTime : TimeManager.PlayerDeltaTime;
        float untouched = slowOnWorld ? TimeManager.PlayerDeltaTime : TimeManager.WorldDeltaTime;
        Assert.That(slowed, Is.EqualTo(Time.fixedDeltaTime * _combat.ParrySlowScale).Within(1e-4f),
            "近战来源 parry 应把作用层压到 ParrySlowScale");
        Assert.That(untouched, Is.EqualTo(Time.fixedDeltaTime).Within(1e-4f), "另一层不受 parry 时缓影响");

        yield return RunFor(_combat.ParrySlowDurationSec + 0.15f); // 超过时缓时长（unscaled 时钟）
        float after = slowOnWorld ? TimeManager.WorldDeltaTime : TimeManager.PlayerDeltaTime;
        Assert.That(after, Is.EqualTo(Time.fixedDeltaTime).Within(1e-4f), "时缓到期应自动解除（不自锁）");
    }

    // —— j. parry 反馈链（飞行道具来源）：不触发时缓 —— //
    [UnityTest]
    public IEnumerator ParryFeedback_ProjectileDoesNotSlowTime()
    {
        _timeGo = new GameObject("TimeManager_CombatTest");
        _timeGo.AddComponent<TimeManager>();
        yield return RunFor(0.05f);

        _combat.OnParrySuccess(new ParryInfo { SourceType = DamageType.Projectile, SourceInstanceId = 1 });
        yield return RunFor(0.05f);

        Assert.That(TimeManager.WorldDeltaTime, Is.EqualTo(Time.fixedDeltaTime).Within(1e-4f),
            "飞行道具来源不得触发时缓（策划案：仅近战来源）");
        Assert.That(TimeManager.PlayerDeltaTime, Is.EqualTo(Time.fixedDeltaTime).Within(1e-4f),
            "玩家层同样不得被影响");
    }

    // —— k. 重开复位：中止攻击、清冷却（「重开无残留」验收项） —— //
    [UnityTest]
    public IEnumerator ResetForRestart_ClearsStateAndCooldown()
    {
        AttackDefinition def = NewDef(cooldown: 10f);
        _combat.NormalAttack = def;

        PressAttack();
        yield return RunFor(0.03f);
        Assert.That(_combat.IsAttacking, Is.True);

        _combat.ResetForRestart();
        Assert.That(_combat.IsAttacking, Is.False, "复位应中止进行中的攻击");

        yield return RunFor(0.1f);
        PressAttack(); // 冷却被清——应能立即再出
        yield return RunFor(0.03f);
        Assert.That(_combat.IsAttacking, Is.True, "复位应清空冷却，否则本出招被 10s 冷却拦截");
        Object.Destroy(def);
    }

    // —— l. 暂停不出招：区别于时停（D1 待决），暂停=全层置零是框架 4.3 锁死语义——
    //     暂停中不仲裁不提交；缓冲条目保留（清空旧输入归流程层 InputSampler.ClearBuffered 职责）
    [UnityTest]
    public IEnumerator Paused_DoesNotStartAttack_ButBufferStays()
    {
        _timeGo = new GameObject("TimeManager_CombatTest");
        _timeGo.AddComponent<TimeManager>();
        yield return RunFor(0.05f);

        AttackDefinition def = NewDef();
        _combat.NormalAttack = def;
        TimeManager.SetPaused(true);

        PressAttack();
        yield return RunFor(0.03f);
        Assert.That(_combat.IsAttacking, Is.False, "暂停中不得出招（框架 4.3：暂停不积能、不耗能、不出招）");
        Assert.That(_sampler.Buffer.HasBuffered(LogicalButton.Attack), Is.True,
            "暂停中不得吞输入——缓冲清空是流程层职责，动作层只挡不消费");

        TimeManager.SetPaused(false);
        yield return RunFor(0.03f);
        Assert.That(_combat.IsAttacking, Is.True, "恢复后窗口内被保留的输入应能出招");
        Object.Destroy(def);
    }

    // —— m. 派生窗口过期：窗口自然流失后派生机会消失，普攻回退常态变体 —— //
    [UnityTest]
    public IEnumerator DeriveWindowExpired_FallsBackToNormalAttack()
    {
        AttackDefinition flash = NewDef();
        AttackDefinition normal = NewDef();
        _combat.FlashSlash = flash;
        _combat.NormalAttack = normal;
        _combat.ParryDeriveWindowSec = 0.15f; // 收短窗口缩短测试时长（语义与默认一致，数值待策划核）

        _combat.OnParrySuccess(new ParryInfo { SourceType = DamageType.Projectile, SourceInstanceId = 1 });
        Assert.That(_combat.DeriveWindowRemaining, Is.GreaterThan(0f), "前置：窗口已授予");

        yield return RunFor(0.25f); // 超窗（玩家时间轴流逝）
        Assert.That(_combat.DeriveWindowRemaining, Is.LessThanOrEqualTo(0f), "前置：窗口已过期");

        PressAttack();
        yield return RunFor(0.03f);
        Assert.That(_combat.CurrentDefinition, Is.EqualTo(normal), "窗口过期后左键应回退常态普攻（派生机会消失）");
        Object.Destroy(flash);
        Object.Destroy(normal);
    }

    // —— n. 落地清动量决策三态（§4.7 IMotorLandingClient）——
    //     只锁战斗侧决策；Motor 执行落地动量清零归移动侧联调
    [UnityTest]
    public IEnumerator ClearMomentumOnLanding_RequiresAttackFlagAndNoDeriveWindow()
    {
        AttackDefinition fast = NewDef();
        fast.ClearMomentumOnLandIfUnderived = true;
        _combat.NormalAttack = fast;

        Assert.That(_combat.ShouldClearMomentumOnLanding(), Is.False, "非攻击中落地不应清动量");

        PressAttack();
        yield return RunFor(0.03f);
        Assert.That(_combat.ShouldClearMomentumOnLanding(), Is.True,
            "高速普攻攻击中且无派生 → 落地清空动量（速度经济学：逼迫用招式链维持动量）");

        _combat.OnParrySuccess(new ParryInfo { SourceType = DamageType.Projectile, SourceInstanceId = 1 });
        Assert.That(_combat.ShouldClearMomentumOnLanding(), Is.False,
            "派生窗口内落地不清（动量留给闪斩派生链）");
        Object.Destroy(fast);
    }

    // —— o. 多段攻击段推进（设计 §4.1：多段 = 一个 SO 内多段、段显式推进——连斩/推斩的结构基础）—— //
    //     段号递增、收尾段无取消窗口即不可取消（「没有开窗口的位置就没有取消」）、走完自然收尾
    //     时序纪律：收尾段按键的条目须死于收招前——否则「保留输入」语义会在收招后自动出招（竞态）
    [UnityTest]
    public IEnumerator MultiSegment_AdvancesThroughSegments_TailNotCancellable()
    {
        AttackDefinition def = NewDef();
        AttackPhase phase0 = new AttackPhase { StartupSec = 0.05f, ActiveSec = 0.05f, RecoverySec = 0.1f };
        AttackPhase phase1 = new AttackPhase { StartupSec = 0.05f, ActiveSec = 0.05f, RecoverySec = 0.25f }; // 收尾长恢复窗
        def.Phases = new[] { phase0, phase1 };
        _combat.NormalAttack = def;

        AttackDefinition cancelTarget = NewDef();
        AttackCancelTable table = ScriptableObject.CreateInstance<AttackCancelTable>();
        table.Rules = new[] // 只给段 0 的恢复窗开取消——段 1（收尾段）无窗口
        {
            new CancelRule
            {
                SourceAttack = def, SourceSegment = 0,
                SourcePhase = AttackPhaseKind.Recovery,
                TargetAttack = cancelTarget, RequiredIntent = InputIntent.Attack,
            },
        };
        _combat.CancelTable = table;

        PressAttack();
        yield return RunFor(0.03f);
        Assert.That(_combat.CurrentSegmentIndex, Is.EqualTo(0), "起手在段 0");

        yield return RunFor(0.25f); // 段 0 全相位 0.2s 走完（含提交延迟余量）→ 推进段 1
        Assert.That(_combat.CurrentSegmentIndex, Is.EqualTo(1), "段 0 走完应显式推进到段 1");
        Assert.That(_combat.IsAttacking, Is.True, "段间推进不打断攻击");

        yield return RunFor(0.07f); // 段 1 走完 Startup+Active，刚进收尾段 Recovery
        Assert.That(_combat.CurrentPhase, Is.EqualTo(AttackPhaseKind.Recovery), "前置：已进入收尾段恢复窗");

        PressAttack(); // 收尾段按键——取消表无窗口（条目死于收招前 ≥6 帧，不触发收招后自动出招）
        yield return RunFor(0.03f);
        Assert.That(_combat.CurrentDefinition, Is.EqualTo(def), "收尾段无取消窗口：不得切换招式");
        Assert.That(_started, Is.EqualTo(1), "收尾段按键不产生新起手");

        yield return RunFor(0.3f); // 收尾段走完（且残留条目已过期）→ 自然收招
        Assert.That(_combat.IsAttacking, Is.False, "全部段走完应自然收尾");
        Assert.That(_ended, Is.EqualTo(1), "收尾应触发一次 AttackEnded");
        Assert.That(_sampler.Buffer.HasBuffered(LogicalButton.Attack), Is.False, "残留按下沿应已自然过期");
        Object.Destroy(def);
        Object.Destroy(cancelTarget);
        Object.Destroy(table);
    }

    // —— p. 冷却到期恢复：按玩家时间戳计时（待机也恢复，不依赖动作 Tick——框架 4.2）—— //
    //     时序纪律：冷却中按键的条目须死于冷却到期前——否则到期瞬间「保留输入」会自动出招（竞态）；
    //     顺带锁能量等值边界：余额恰等于费用时可出招（TrySpend 用 < 严格小于）
    [UnityTest]
    public IEnumerator Cooldown_RecoversOnTimestamp_EvenWhileIdle()
    {
        AttackDefinition def = NewDef(cooldown: 0.4f);
        SetPhaseWindows(def, 0.05f, 0.05f, 0.1f); // 全相位 0.2s < 冷却 0.4s
        _combat.NormalAttack = def;

        PressAttack();
        yield return RunFor(0.03f);
        Assert.That(_combat.IsAttacking, Is.True, "首次出招不受冷却限制");
        float commitAt = Time.time; // 冷却零点（PlayerTime 退化 Time.time）

        yield return RunFor(0.16f); // 仍在 Recovery（全程 0.2s）
        PressAttack(); // 攻击中按键（取消表空 → 零副作用保留）；条目 0.12s 后过期，远早于冷却到期
        yield return RunFor(0.15f); // 收招（0.2s）+ 条目过期
        Assert.That(_combat.IsAttacking, Is.False, "冷却中且条目已过期——不得出招");
        Assert.That(_sampler.Buffer.HasBuffered(LogicalButton.Attack), Is.False, "前置：冷却中的按键已被窗口自然清理");
        Assert.That(Time.time - commitAt, Is.LessThan(0.4f), "前置：冷却尚未到期");

        yield return RunFor(0.2f); // 冷却到期（全程待机——无动作在跑，恢复只靠时间戳）
        Assert.That(_combat.IsAttacking, Is.False, "前置：待机中，无自动出招");
        PressAttack();
        yield return RunFor(0.03f);
        Assert.That(_combat.IsAttacking, Is.True, "冷却按时间戳恢复——待机也计时（框架 4.2）");
        Object.Destroy(def);

        // 能量等值边界：余额 == 费用（50）时应能出招并扣至 0
        AttackDefinition costly = NewDef(energyCost: 50f);
        _combat.ResetForRestart();
        _combat.NormalAttack = costly;
        _energy.SetForDebug(50f);
        PressAttack();
        yield return RunFor(0.03f);
        Assert.That(_combat.IsAttacking, Is.True, "余额恰等于费用时应可出招");
        Assert.That(_energy.CurrentEnergy, Is.EqualTo(0f).Within(1e-3f), "出招后扣至 0");
        Object.Destroy(costly);
    }
}
