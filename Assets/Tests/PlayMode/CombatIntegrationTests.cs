using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 战斗×移动接缝联调（战斗系统 §六验收项「撞墙截断后只判到撞墙点」，框架 4.4）：
// 真实 CharacterController 撞墙 + 扫掠采集整链：墙前目标命中、墙后目标不命中。
// Motor 命令契约（§4.7 RequestMove 返回实际终点）× Hitbox 扫掠（§4.4）的端到端回归——
// 纯逻辑侧（去重 / 扫掠不漏）在 DamageResolverTests，本文件只验物理截断下的链路行为
public class CombatIntegrationTests
{
    private GameObject _ground, _wall, _player, _resolverGo, _targetBefore, _targetBehind;
    private MovementParams _params;
    private PlayerMotor _motor;
    private PlayerCombat _combat;
    private InputSampler _sampler;
    private Hitbox _attackBox;
    private HealthComponent _healthBeforeWall, _healthBehindWall;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        // 批处理渲染帧可达千级 fps：40 帧循环在一个 fixed tick 内跑完，首个采集拍时玩家已停在墙前，
        // 扫掠从未发生。钉死每帧 1/60s（MovementBhopTests 同款拼图）：1 渲染帧 = 1 fixed tick，扫掠逐拍覆盖全程
        Time.captureFramerate = 60;

        _ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        _ground.transform.localScale = new Vector3(2f, 1f, 2f); // Plane 默认 10x10 → 20x20m

        // 墙：薄立方体（非 trigger，物理阻挡），z ∈ [5.75, 6.25]
        _wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _wall.transform.position = new Vector3(0f, 1f, 6f);
        _wall.transform.localScale = new Vector3(8f, 2f, 0.5f);

        _params = ScriptableObject.CreateInstance<MovementParams>();

        _player = new GameObject("SweepPlayer");
        _player.SetActive(false); // 先注入参数再激活（同 MovementBhopTests / PlayerCombatTests 纪律）
        CharacterController controller = _player.AddComponent<CharacterController>();
        controller.radius = 0.5f;
        controller.height = 2f;
        controller.center = new Vector3(0f, 1f, 0f);
        _motor = _player.AddComponent<PlayerMotor>();
        _motor.SetParams(_params);
        _attackBox = _player.AddComponent<Hitbox>();
        _attackBox.Kind = HitboxKind.Damage;
        _combat = _player.AddComponent<PlayerCombat>(); // Motion 驱动链测试用；其余测试不喂输入即静默
        _sampler = _player.AddComponent<InputSampler>();
        _player.transform.position = new Vector3(0f, 0.1f, 0f);
        _player.SetActive(true);
        _sampler.enabled = false; // 停自动采样，快照由 InjectSnapshot 注入（PlayerCombatTests 同款）

        _healthBeforeWall = MakeTarget("TargetBeforeWall", 3f);
        _healthBehindWall = MakeTarget("TargetBehindWall", 7f); // 墙后 0.75m 起（Collider z∈[6.6,7.4]）

        _resolverGo = new GameObject("Resolver");
        _resolverGo.AddComponent<DamageResolver>();

        yield return null; // 至少一个固定步：落地建立、Resolver 首拍
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        Time.captureFramerate = 0;
        if (_attackBox != null) _attackBox.Deactivate();
        if (_player != null) Object.Destroy(_player);
        if (_targetBefore != null) Object.Destroy(_targetBefore);
        if (_targetBehind != null) Object.Destroy(_targetBehind);
        if (_resolverGo != null) Object.Destroy(_resolverGo);
        if (_wall != null) Object.Destroy(_wall);
        if (_ground != null) Object.Destroy(_ground);
        if (_params != null) Object.Destroy(_params);
        _player = null;
        _params = null;
        yield return null;
    }

    private HealthComponent MakeTarget(string name, float z)
    {
        GameObject go = new GameObject(name);
        BoxCollider collider = go.AddComponent<BoxCollider>();
        collider.center = new Vector3(0f, 1f, 0f);
        collider.size = new Vector3(0.8f, 2f, 0.8f);
        HealthComponent health = go.AddComponent<HealthComponent>();
        health.MaxHealth = 100f;
        go.AddComponent<Hurtbox>();
        go.transform.position = new Vector3(0f, 0f, z);
        return health;
    }

    // 撞墙截断：高速前移扫掠攻击撞墙后——墙前目标（扫掠路径上）命中，墙后目标不命中。
    // 攻击框半径 0.5 + 前偏移 0.2：贴墙时框体最多延伸穿墙 ~0.12m，远够不到墙后 6.6m 起的目标；
    // 若截断失效（位置未停在墙前），扫掠路径会覆盖到墙后目标——断言即挂
    [UnityTest]
    public IEnumerator SweepAttack_WallClip_TruncatesAtWall()
    {
        _attackBox.Activate(new HitboxActivation
        {
            Attacker = _player,
            InstanceId = 1,
            PhaseIndex = 0,
            Profile = new HitboxProfile
            {
                Shape = HitboxShape.Capsule,
                CapsuleRadius = 0.5f,
                CapsuleHeight = 1f,
                LocalOffset = new Vector3(0f, 1f, 0.2f),
            },
            BaseDamage = 10f,
            DamageType = DamageType.Melee,
            Knockback = 0f,
            HitStopSec = 0f, // 测试不打 hit-stop，避免拖慢真实时间
            Sweep = true,
        });

        Vector3 lastMove = Vector3.one;
        for (int i = 0; i < 40; i++) // 0.5m/帧 × 40 = 20m，远超 6m 墙距
        {
            lastMove = _motor.RequestMove(Vector3.forward, 0.5f);
            yield return null;
        }

        Assert.That(_player.transform.position.z, Is.LessThan(5.5f), "前置：玩家应被墙截停在墙前（物理成立）");
        Assert.That(lastMove.z, Is.InRange(0f, 0.4f),
            "顶墙后 RequestMove 应返回被截断的实际位移（§4.7 契约：返回实际终点；请求量为 0.5）");

        Assert.That(_healthBeforeWall.CurrentHealth, Is.EqualTo(90f).Within(1e-3f),
            "墙前目标（扫掠路径上）应被命中");
        Assert.That(_healthBehindWall.CurrentHealth, Is.EqualTo(100f).Within(1e-3f),
            "墙后目标不应命中——扫掠只判到撞墙点");
    }

    // Motion 驱动链（§4.7 场景一的自动驱动）：招式段数据 Motion=Dash 时，
    // PlayerCombat 每 tick 经 Motor 命令前移（战斗不直接动 Transform）——Startup+Active 前移、Recovery 停
    [UnityTest]
    public IEnumerator PlayerCombat_MotionIntent_DrivesMotorAdvance_DuringStartupActiveOnly()
    {
        AttackDefinition def = ScriptableObject.CreateInstance<AttackDefinition>();
        AttackPhase phase = def.Phases[0];
        phase.StartupSec = 0.1f;
        phase.ActiveSec = 0.1f;
        phase.RecoverySec = 0.1f;
        phase.Motion = MotionIntentKind.Dash;
        phase.MotionSpeed = 10f;
        _combat.NormalAttack = def;

        _sampler.InjectSnapshot(new InputSnapshot { Mouse0Held = true });
        _sampler.Buffer.Push(KeyCode.Mouse0);
        yield return new WaitForSecondsRealtime(0.25f); // Startup+Active 0.2s 前移约 2m，再留 Recovery 余量

        Assert.That(_player.transform.position.z, Is.GreaterThan(1.5f),
            "Motion=Dash 应经 Motor 命令链自动前移（§4.7：战斗通过命令请求位移）");

        float zAtRecovery = _player.transform.position.z;
        yield return new WaitForSecondsRealtime(0.1f);
        Assert.That(_player.transform.position.z, Is.EqualTo(zAtRecovery).Within(0.01f),
            "Recovery 相位应停止前移（位移意图只在 Startup+Active 期间生效）");
        Object.Destroy(def);
    }
}
