using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 敌人侧战斗装配回归（D3–D5 闭环验收：一个敌人可攻击/可被击杀）：
// 感知决策（攻击距离出招 / 仇恨半径外待机 / 攻击间隔）、判定链（敌人 Hitbox → 结算 → 玩家扣血）、
// 被击杀停摆（断肢简化版：关阻挡停运动）、重开复位（复活/回出生点/满血）、时停冻结（世界层语义）
// 时序纪律：batchmode 帧率狂奔（万级 FPS）下墙钟秒与固定步相位不定——一律 WaitForFixedUpdate 按步推进；
// SetUp 把玩家放在仇恨范围外，各测试自移入遭遇位，保证「第一招」发生在测试体内、时序账可算
public class EnemyCombatTests
{
    private GameObject _player;
    private PlayerMotor _motor;
    private MovementParams _params;
    private HealthComponent _playerHealth;
    private GameObject _enemy;
    private EnemyCombat _enemyCombat;
    private EnemyMotor _enemyMotor;
    private HealthComponent _enemyHealth;
    private CharacterController _enemyController;
    private GameObject _resolverGo;
    private GameObject _timeGo;
    private GameObject _ground;

    private int _started;
    private readonly List<float> _attackTimes = new(); // 失败诊断：每招的世界时刻（断言消息里带出）

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        // UTF 在此工程复用 fixture 实例（域不重载）——计数器必须在 SetUp 归零，否则跨测试累积
        _started = 0;
        _attackTimes.Clear();

        _timeGo = new GameObject("TimeManager_EnemyTests");
        _timeGo.AddComponent<TimeManager>();

        _ground = GameObject.CreatePrimitive(PrimitiveType.Plane); // 敌人 Motor 有重力，站地防止 y 漂移干扰水平感知
        _ground.transform.localScale = new Vector3(50f, 1f, 50f);

        _resolverGo = new GameObject("Resolver");
        _resolverGo.AddComponent<DamageResolver>();

        // 玩家：EnemyCombat 感知按 PlayerMotor 定位（PlayerCombatTests 同款装配纪律）
        _params = ScriptableObject.CreateInstance<MovementParams>();
        _player = new GameObject("EnemyTestPlayer");
        _player.SetActive(false);
        CharacterController playerCc = _player.AddComponent<CharacterController>();
        playerCc.radius = _params.CapsuleBaseRadius;
        playerCc.height = _params.CapsuleBaseHeight;
        playerCc.center = new Vector3(0f, playerCc.height * 0.5f, 0f);
        _motor = _player.AddComponent<PlayerMotor>();
        _motor.SetParams(_params);
        _playerHealth = _player.AddComponent<HealthComponent>();
        _playerHealth.MaxHealth = 100f;
        _playerHealth.Layer = TimeLayer.Player;
        BoxCollider hurtCol = _player.AddComponent<BoxCollider>();
        hurtCol.center = new Vector3(0f, 1f, 0f);
        hurtCol.size = new Vector3(0.8f, 1.8f, 0.8f);
        _player.AddComponent<Hurtbox>();
        _player.SetActive(true);
        _player.transform.position = new Vector3(0f, 0.05f, 40f); // 仇恨范围外待机（AggroRange 15）——遭遇由各测试自控

        _enemy = BuildEnemy("Enemy", Vector3.zero);
        _enemyCombat.AttackStarted += _ =>
        {
            _started++;
            _attackTimes.Add(TimeManager.WorldTime);
        };

        Physics.SyncTransforms();
        yield return null; // 首帧初始化（Awake/OnEnable 注册表）
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        TimeManager.ResetAll();
        if (_enemy != null) Object.DestroyImmediate(_enemy);
        if (_player != null) Object.DestroyImmediate(_player);
        if (_resolverGo != null) Object.DestroyImmediate(_resolverGo);
        if (_ground != null) Object.DestroyImmediate(_ground);
        if (_timeGo != null) Object.DestroyImmediate(_timeGo);
        if (_params != null) Object.DestroyImmediate(_params);
        _enemy = null;
        _player = null;
        yield return null;
    }

    // —— 工具 ——

    // 固定步推进：按世界秒换算步数（batchmode 高帧率下墙钟秒与固定步相位不定，时序必须按步走）
    private static IEnumerator RunSteps(float worldSeconds)
    {
        int steps = Mathf.RoundToInt(worldSeconds / Time.fixedDeltaTime);
        for (int i = 0; i < steps; i++) yield return new WaitForFixedUpdate();
    }

    private void MovePlayerToEncounter() // 玩家移入敌人正面的攻击距离内（水平 1.2m < AttackRange 2.2）
    {
        _player.transform.position = new Vector3(0f, 0.05f, 1.2f);
        Physics.SyncTransforms();
    }

    private AttackDefinition NewEnemyAttack(float startup = 0.1f, float active = 0.1f, float recovery = 0.1f)
    {
        AttackDefinition def = ScriptableObject.CreateInstance<AttackDefinition>();
        def.Phases[0].StartupSec = startup;
        def.Phases[0].ActiveSec = active;
        def.Phases[0].RecoverySec = recovery;
        def.Phases[0].BaseDamage = 10f;
        def.Phases[0].Knockback = 0f;  // 玩家侧 PlayerMotor 未接击退实现，测伤害不打位移
        def.Phases[0].HitStopSec = 0f; // 不打 hit-stop，避免拖慢真实时间
        def.Phases[0].DamageProfile = new HitboxProfile
        {
            Shape = HitboxShape.Capsule,
            CapsuleRadius = 1.0f,
            CapsuleHeight = 2.0f,
            LocalOffset = new Vector3(0f, 1f, 0.6f),
        };
        return def;
    }

    private GameObject BuildEnemy(string name, Vector3 position)
    {
        GameObject enemy = new GameObject(name);
        enemy.transform.position = position; // 先定位再挂组件：EnemyCombat.Awake 要记录出生点
        CharacterController cc = enemy.AddComponent<CharacterController>();
        cc.center = new Vector3(0f, 0.9f, 0f);
        cc.height = 1.8f;
        cc.radius = 0.4f;
        _enemyMotor = enemy.AddComponent<EnemyMotor>();
        _enemyHealth = enemy.AddComponent<HealthComponent>();
        _enemyHealth.MaxHealth = 50f;
        _enemyHealth.Layer = TimeLayer.World;
        _enemyCombat = enemy.AddComponent<EnemyCombat>();
        _enemyCombat.Attack = NewEnemyAttack();
        _enemyCombat.AttackIntervalSec = 0.6f;
        _enemyCombat.AggroRange = 15f;
        _enemyCombat.AttackRange = 2.2f;
        Hitbox hitbox = enemy.AddComponent<Hitbox>();
        hitbox.Kind = HitboxKind.Damage;
        _enemyCombat.DamageHitbox = hitbox;
        BoxCollider hurtCol = enemy.AddComponent<BoxCollider>();
        hurtCol.center = new Vector3(0f, 0.95f, 0f);
        hurtCol.size = new Vector3(0.8f, 1.8f, 0.8f);
        enemy.AddComponent<Hurtbox>().Health = _enemyHealth;
        _enemyController = cc;
        return enemy;
    }

    // —— a. 判定全链：感知 → 出招 → 判定框 → 结算 → 玩家扣血（D3–D5 闭环验收主链）—— //
    [UnityTest]
    public IEnumerator Attack_ReachesPlayer_DamagesHim()
    {
        MovePlayerToEncounter();
        yield return RunSteps(0.5f); // 0.1 startup + 0.1 active 内必然命中，余量给感知与固定步

        Assert.That(_started, Is.GreaterThanOrEqualTo(1), "攻击距离内应出招");
        Assert.That(_playerHealth.CurrentHealth, Is.LessThan(100f), "敌人攻击应经结算扣玩家血");
    }

    // —— b. 攻击间隔：两次出招的最小时间差 = AttackIntervalSec（自出招起计、含招式时长）—— //
    [UnityTest]
    public IEnumerator Attack_RespectsInterval()
    {
        MovePlayerToEncounter();
        yield return RunSteps(1.0f); // 第一招 ~0.02s，第二招 ~0.62s（间隔 0.6），第三招 ~1.22s > 1.0

        Assert.That(_started, Is.EqualTo(2),
            $"出招世界时刻=[{string.Join(", ", _attackTimes)}]——应恰两次（窗 0.3 + 间隔 0.6）");
    }

    // —— c. 仇恨外待机：玩家超出 AggroRange 不追不打 —— //
    [UnityTest]
    public IEnumerator Perception_OutsideAggro_StaysIdle()
    {
        yield return RunSteps(0.5f); // SetUp 已把玩家放在 40m 外（Aggro 15m）

        Assert.That(_started, Is.EqualTo(0), "仇恨半径外不得出招");
    }

    // —— d. 被击杀停摆：断肢简化版——关阻挡、停攻击，尸体不参与逻辑 —— //
    [UnityTest]
    public IEnumerator Killed_StopsLocomotionAndCombat()
    {
        _enemyHealth.ApplyDamage(new DamageInfo { Type = DamageType.Melee, Damage = 999f, KnockbackDir = Vector3.forward });

        Assert.That(_enemyHealth.IsDead, Is.True, "前置：敌人已死");
        Assert.That(_enemyController.enabled, Is.False, "死亡应关阻挡碰撞（断肢简化版）");
        Assert.That(_enemyCombat.isActiveAndEnabled, Is.True, "对象保留在场（尸体不销毁——重开复位的基础）");

        _started = 0;
        MovePlayerToEncounter();
        yield return RunSteps(0.3f);
        Assert.That(_started, Is.EqualTo(0), "死后不得再出招");
    }

    // —— e. 重开复位：复活满血、回出生点、恢复阻挡（「重开无残留」敌人侧；纯同步语义，无帧推进）—— //
    [Test]
    public void ResetForRestart_RevivesToSpawnPoint()
    {
        _enemyHealth.ApplyDamage(new DamageInfo { Type = DamageType.Melee, Damage = 999f, KnockbackDir = Vector3.forward });
        _enemy.transform.position = new Vector3(5f, 0f, 5f); // 被击退/漂移后偏离出生点
        Physics.SyncTransforms();

        _enemyCombat.ResetForRestart();

        Assert.That(_enemyHealth.IsDead, Is.False, "复位应复活");
        Assert.That(_enemyHealth.CurrentHealth, Is.EqualTo(50f).Within(1e-3f), "复位应满血");
        Assert.That(_enemyController.enabled, Is.True, "复位应恢复阻挡碰撞");
        Assert.That(_enemy.transform.position, Is.EqualTo(Vector3.zero).Within(1e-2f), "复位应回出生点");
    }

    // —— f. 时停冻结（世界层语义）：WorldScale=0 期间敌人不出招不推进 —— //
    [UnityTest]
    public IEnumerator TimeStop_FreezesEnemy()
    {
        MovePlayerToEncounter();
        TimeScaleHandle stop = TimeManager.RegisterScale(TimeLayer.World, 0f, "test_timestop");
        float dtAfterStop = TimeManager.WorldDeltaTime; // 登记立即生效性（>0 = 登记没生效，instance 问题）

        yield return RunSteps(0.4f); // 24 个固定步（WaitForFixedUpdate 照走，敌人 dt=0 全程冻结）
        Assert.That(_started, Is.EqualTo(0),
            $"时停期间敌人不得出招（世界层冻结）；登记后 dt={dtAfterStop}，出招时刻=[{string.Join(", ", _attackTimes)}]");

        TimeManager.Release(stop);
        yield return RunSteps(0.5f);
        Assert.That(_started, Is.GreaterThanOrEqualTo(1), "时停解除后敌人恢复行动");
    }

    // —— g. 复位后无残留冷却：重开立即可战 —— //
    [UnityTest]
    public IEnumerator Reset_ClearsCooldown_EnemyCanFightAgain()
    {
        MovePlayerToEncounter();
        yield return RunSteps(0.35f); // 第一招 ~0.02s，招式窗 0.3s 走完
        Assert.That(_started, Is.EqualTo(1), "前置：已出招一次");

        _enemyCombat.ResetForRestart();
        _started = 0;
        yield return RunSteps(0.1f); // 复位清冷却，玩家仍在攻击距离内——下一个固定步即再出招

        Assert.That(_started, Is.EqualTo(1), "复位后冷却清零，应立即再次出招");
    }
}
