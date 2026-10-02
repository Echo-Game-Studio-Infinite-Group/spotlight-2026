using System.Linq;
using NUnit.Framework;
using UnityEngine;

// 命中结算语义回归（设计 §4.5/§4.6，框架验收清单）：
// 基础伤害与速度加成、去重（多 Collider 单次命中 / 重复 tick）、无敌掩码、
// parry 先于伤害（配对成功换来的无敌本 tick 就能挡伤害）、击退命令、扫掠高速不漏
// 场景纯代码搭建（MovementBhopTests 同款纪律）；驱动走 resolver.ProcessTick() 直调，确定性强
public class DamageResolverTests
{
    private GameObject _attacker;
    private GameObject _target;
    private GameObject _resolverGo;
    private DamageResolver _resolver;
    private Hitbox _attackBox;
    private Hurtbox _targetHurt;
    private HealthComponent _targetHealth;

    [SetUp]
    public void SetUp()
    {
        _resolverGo = new GameObject("Resolver");
        _resolver = _resolverGo.AddComponent<DamageResolver>();

        _attacker = new GameObject("Attacker");
        _attackBox = _attacker.AddComponent<Hitbox>();
        _attackBox.Kind = HitboxKind.Damage;

        _target = new GameObject("Target");
        BoxCollider main = _target.AddComponent<BoxCollider>();
        main.center = new Vector3(0f, 1f, 0f);
        main.size = new Vector3(0.8f, 1.8f, 0.8f);
        _targetHealth = _target.AddComponent<HealthComponent>();
        _targetHealth.MaxHealth = 100f;
        _target.transform.position = new Vector3(1.0f, 0f, 0.5f); // 攻击框偏移 (0,1,0.6) 覆盖半径 ~1，重叠
        _targetHurt = _target.AddComponent<Hurtbox>();

        Physics.SyncTransforms();
    }

    [TearDown]
    public void TearDown()
    {
        TimeManager.ResetAll(); // hit-stop 测试登记的冻结不清理会漏给下一个测试的 dt
        // DestroyImmediate：注册表是静态的，延迟销毁会把已死组件漏给下一个测试的 ProcessTick
        if (_attackBox != null) _attackBox.Deactivate();
        Object.DestroyImmediate(_attacker);
        Object.DestroyImmediate(_target);
        Object.DestroyImmediate(_resolverGo);
    }

    private HitboxActivation MakeActivation(float damage = 10f, float speedScale = 0f, float ratio = 0f,
        float knockback = 3f, bool sweep = false, float hitStop = 0f)
    {
        return new HitboxActivation
        {
            Attacker = _attacker,
            InstanceId = 1,
            PhaseIndex = 0,
            Profile = new HitboxProfile
            {
                Shape = HitboxShape.Capsule,
                CapsuleRadius = 1.0f,
                CapsuleHeight = 2.0f,
                LocalOffset = new Vector3(0f, 1f, 0.6f),
            },
            BaseDamage = damage,
            SpeedDamageScale = speedScale,
            SpeedRatioSnapshot = ratio,
            DamageType = DamageType.Melee,
            Knockback = knockback,
            HitStopSec = hitStop, // 默认 0：其余测试不打 hit-stop，避免拖慢真实时间
            Sweep = sweep,
        };
    }

    // a. 基础伤害：重叠即扣血
    [Test]
    public void Damage_Overlap_AppliesBaseDamage()
    {
        _attackBox.Activate(MakeActivation(damage: 10f));
        _resolver.ProcessTick();
        Assert.That(_targetHealth.CurrentHealth, Is.EqualTo(90f).Within(1e-3f), "重叠命中应扣基础伤害");
    }

    // b. 速度伤害：最终伤害 = 基础 × (1 + 系数 × 起手速度比)，快照在激活时锁定
    [Test]
    public void Damage_ScalesWithSpeedSnapshot()
    {
        _attackBox.Activate(MakeActivation(damage: 10f, speedScale: 1f, ratio: 2f));
        _resolver.ProcessTick();
        Assert.That(_targetHealth.CurrentHealth, Is.EqualTo(70f).Within(1e-3f), "10 × (1 + 1×2) = 30 伤害");
    }

    // c. 去重：同攻击实例同段，多 tick / 多 Collider 只扣一次
    [Test]
    public void Damage_Deduped_AcrossTicksAndColliders()
    {
        // 第二个受击 Collider（同一 Hurtbox）——「多 Collider 敌人单次命中」
        BoxCollider extra = _target.AddComponent<BoxCollider>();
        extra.center = new Vector3(0f, 1f, 0f);
        extra.size = new Vector3(0.6f, 1.8f, 0.6f);
        Physics.SyncTransforms();

        _attackBox.Activate(MakeActivation(damage: 10f));
        _resolver.ProcessTick();
        _resolver.ProcessTick();
        Assert.That(_targetHealth.CurrentHealth, Is.EqualTo(90f).Within(1e-3f),
            "同实例同段对同目标只结算一次（多 Collider / 多 tick 均去重）");
    }

    // d. 换段重新合法：PhaseIndex 变化后同目标可再次命中
    [Test]
    public void Damage_NewPhase_CanHitAgain()
    {
        _attackBox.Activate(MakeActivation(damage: 10f));
        _resolver.ProcessTick();
        Assert.That(_targetHealth.CurrentHealth, Is.EqualTo(90f).Within(1e-3f));

        HitboxActivation next = MakeActivation(damage: 10f);
        next.PhaseIndex = 1;
        _attackBox.Activate(next);
        _resolver.ProcessTick();
        Assert.That(_targetHealth.CurrentHealth, Is.EqualTo(80f).Within(1e-3f), "换段后去重键失效，可再次命中");
    }

    // e. 无敌掩码：近战无敌期间不扣血且发免疫拦截事件
    [Test]
    public void Immunity_BlocksAndNotifies()
    {
        int blocked = 0;
        _targetHealth.ImmunityBlocked += _ => blocked++;

        _targetHealth.GrantImmunity(DamageType.Melee, 1f);
        _attackBox.Activate(MakeActivation(damage: 10f));
        _resolver.ProcessTick();

        Assert.That(_targetHealth.CurrentHealth, Is.EqualTo(100f).Within(1e-3f), "无敌期间不扣血");
        Assert.That(blocked, Is.EqualTo(1), "应发出一次免疫拦截事件（滑铲挡弹类反馈）");
    }

    // f. 击退命令：向目标侧 IKnockbackReceiver 发冲量（方向 = 攻击者→目标，水平）
    [Test]
    public void Knockback_ForwardedToReceiver()
    {
        var recorder = _target.AddComponent<KnockbackRecorder>();
        // Hurtbox 在 Awake 缓存接收方，补一次注册表刷新：重建受击框
        Object.DestroyImmediate(_targetHurt);
        _targetHurt = _target.AddComponent<Hurtbox>();
        Physics.SyncTransforms();

        _attackBox.Activate(MakeActivation(damage: 1f, knockback: 3f));
        _resolver.ProcessTick();

        Assert.That(recorder.LastImpulse.magnitude, Is.EqualTo(3f).Within(1e-2f), "击退量级应等于配置值");
        Assert.That(recorder.LastImpulse.x, Is.GreaterThan(0.5f), "击退方向应从攻击者指向目标（+x）");
    }

    // g. parry 先于伤害：配对成功换来的无敌在同一 tick 挡掉被招架攻击的伤害
    [Test]
    public void Parry_PairsBeforeDamage_GrantSameTickImmunity()
    {
        // 玩家（招架方）：parry 框 + 血量 + 记录器（配对成功时授无敌——PlayerCombat 同款链路）
        GameObject player = new GameObject("ParryPlayer");
        try
        {
            Hitbox parryBox = player.AddComponent<Hitbox>();
            parryBox.Kind = HitboxKind.Parry;
            HealthComponent playerHealth = player.AddComponent<HealthComponent>();
            playerHealth.MaxHealth = 100f;
            playerHealth.Layer = TimeLayer.Player;
            var recorder = player.AddComponent<ParryRecorder>();
            recorder.HealthToProtect = playerHealth;
            BoxCollider playerCollider = player.AddComponent<BoxCollider>();
            playerCollider.center = new Vector3(0f, 1f, 0f);
            playerCollider.size = new Vector3(0.8f, 1.8f, 0.8f);
            Hurtbox playerHurt = player.AddComponent<Hurtbox>();
            player.transform.position = Vector3.zero;
            Physics.SyncTransforms();

            parryBox.Activate(new HitboxActivation
            {
                Attacker = player,
                InstanceId = 9,
                PhaseIndex = 0,
                Profile = new HitboxProfile
                {
                    CapsuleRadius = 1.2f, CapsuleHeight = 2.2f,
                    LocalOffset = new Vector3(0f, 1f, 0.5f),
                },
            });

            // 敌方攻击框既罩住玩家的 parry 框也罩住玩家受击框
            _attackBox.transform.position = player.transform.position + new Vector3(0.6f, 0f, 0f);
            Physics.SyncTransforms();
            HitboxActivation incoming = MakeActivation(damage: 50f);
            incoming.InstanceId = 2;
            _attackBox.Activate(incoming);

            _resolver.ProcessTick();

            Assert.That(recorder.Calls, Is.EqualTo(1), "parry 配对应恰好发生一次（配对去重）");
            Assert.That(playerHealth.CurrentHealth, Is.EqualTo(100f).Within(1e-3f),
                "parry 换来的无敌应在本 tick 挡掉来袭伤害（先 parry 后伤害）");
        }
        finally
        {
            Object.DestroyImmediate(player);
        }
    }

    // h. 扫掠：一 tick 位移跨过整个目标（首尾都不重叠）仍命中——高速不漏
    [Test]
    public void Sweep_HighSpeedDisplacement_DoesNotMiss()
    {
        _attacker.transform.position = new Vector3(-5f, 0f, 0.5f);
        Physics.SyncTransforms();
        _attackBox.Activate(MakeActivation(damage: 10f, sweep: true));
        _resolver.ProcessTick(); // 建立扫掠起点（首拍无命中）

        _attacker.transform.position = new Vector3(5f, 0f, 0.5f); // 一 tick 跨 10 米穿过目标
        Physics.SyncTransforms();
        _resolver.ProcessTick();

        Assert.That(_targetHealth.CurrentHealth, Is.EqualTo(90f).Within(1e-3f),
            "扫掠框应覆盖路径，高速穿越不漏目标");
    }

    // i. 死亡目标不结算：采集后扣血前的 IsDead 过滤（「死亡事件恰好一次」的保证）
    [Test]
    public void DeadTarget_NotResolved()
    {
        _targetHealth.ApplyDamage(new DamageInfo
        {
            Type = DamageType.Melee, Damage = 999f, KnockbackDir = Vector3.forward,
        });
        Assert.That(_targetHealth.IsDead, Is.True, "前置：目标已死亡");
        int damaged = 0;
        _targetHealth.Damaged += (_, _, _) => damaged++;

        _attackBox.Activate(MakeActivation(damage: 10f));
        _resolver.ProcessTick();

        Assert.That(damaged, Is.EqualTo(0), "已死目标不得再触发受伤");
        Assert.That(_targetHealth.CurrentHealth, Is.EqualTo(0f).Within(1e-3f), "血量保持 0 不再变动");
    }

    // j. 无敌中仍可 parry：先 parry 后无敌的顺序铁律反向情形——
    //    招架方已有无敌时配对照样发生（「无敌不能阻止继续 parry」，设计原则 4）
    [Test]
    public void Parry_StillPairs_WhenParrierAlreadyInvulnerable()
    {
        GameObject player = new GameObject("ParryPlayer");
        try
        {
            Hitbox parryBox = player.AddComponent<Hitbox>();
            parryBox.Kind = HitboxKind.Parry;
            HealthComponent playerHealth = player.AddComponent<HealthComponent>();
            playerHealth.MaxHealth = 100f;
            playerHealth.Layer = TimeLayer.Player;
            playerHealth.GrantImmunity(DamageType.All, 1f); // 前置无敌：闪避期间继续 parry 的场景
            var recorder = player.AddComponent<ParryRecorder>();
            recorder.HealthToProtect = playerHealth;
            BoxCollider playerCollider = player.AddComponent<BoxCollider>();
            playerCollider.center = new Vector3(0f, 1f, 0f);
            playerCollider.size = new Vector3(0.8f, 1.8f, 0.8f);
            player.AddComponent<Hurtbox>();
            player.transform.position = Vector3.zero;
            Physics.SyncTransforms();

            parryBox.Activate(new HitboxActivation
            {
                Attacker = player,
                InstanceId = 9,
                PhaseIndex = 0,
                Profile = new HitboxProfile
                {
                    CapsuleRadius = 1.2f, CapsuleHeight = 2.2f,
                    LocalOffset = new Vector3(0f, 1f, 0.5f),
                },
            });

            _attackBox.transform.position = player.transform.position + new Vector3(0.6f, 0f, 0f);
            Physics.SyncTransforms();
            HitboxActivation incoming = MakeActivation(damage: 50f);
            incoming.InstanceId = 2;
            _attackBox.Activate(incoming);

            _resolver.ProcessTick();

            Assert.That(recorder.Calls, Is.EqualTo(1), "已有无敌不得阻止 parry 配对（先 parry 后无敌的反向保护）");
            Assert.That(playerHealth.CurrentHealth, Is.EqualTo(100f).Within(1e-3f),
                "前置无敌应照常挡掉来袭伤害");
        }
        finally
        {
            Object.DestroyImmediate(player);
        }
    }

    // k. 打击感链④：命中配置 HitStopSec 应触发全局 hit-stop（解除计时走 unscaled，不自锁在 TimeLayerTests 覆盖）
    [Test]
    public void HitStop_TriggeredByResolvedHit()
    {
        GameObject timeGo = new GameObject("TimeManager_HitStopTest");
        timeGo.AddComponent<TimeManager>();
        try
        {
            _attackBox.Activate(MakeActivation(damage: 10f, hitStop: 0.05f));
            _resolver.ProcessTick(); // [Test] 同步执行无帧推进——hit-stop 计时器不会走，断言的是触发瞬间

            Assert.That(TimeManager.InHitStop, Is.True, "命中应触发 hit-stop（打击感链④）");
        }
        finally
        {
            TimeManager.ResetAll();
            Object.DestroyImmediate(timeGo);
        }
    }

    // l. 攻击侧事件：HitResolved 携带已结算的 DamageInfo（连击数/刀口火花的订阅源），派生标记透传
    [Test]
    public void AttackerEvent_FiredWithResolvedInfo()
    {
        int calls = 0;
        DamageInfo received = default;
        _attackBox.HitResolved += info => { calls++; received = info; };

        HitboxActivation activation = MakeActivation(damage: 10f);
        activation.FromParryDerive = true;
        _attackBox.Activate(activation);
        _resolver.ProcessTick();

        Assert.That(calls, Is.EqualTo(1), "攻击侧应恰好收到一次命中事件");
        Assert.That(received.Damage, Is.EqualTo(10f).Within(1e-3f), "事件应携带最终伤害值");
        Assert.That(received.Attacker, Is.EqualTo(_attacker), "事件应携带攻击者引用");
        Assert.That(received.FromParryDerive, Is.True, "parry 派生标记应透传到事件（闪斩表现差异依据）");
    }

    // m. parry 可连续触发：同一 parry 框先后招架两个攻击实例，各配对一次
    //    （§六 parry 组「可连续触发」——闪斩/普攻互取消链的行为基础；配对去重按实例号不累计）
    [Test]
    public void Parry_ConsecutiveAttacks_EachInstancePairsOnce()
    {
        GameObject player = new GameObject("ParryPlayer");
        try
        {
            Hitbox parryBox = player.AddComponent<Hitbox>();
            parryBox.Kind = HitboxKind.Parry;
            HealthComponent playerHealth = player.AddComponent<HealthComponent>();
            playerHealth.MaxHealth = 100f;
            playerHealth.Layer = TimeLayer.Player;
            var recorder = player.AddComponent<ParryRecorder>();
            recorder.HealthToProtect = playerHealth;
            BoxCollider playerCollider = player.AddComponent<BoxCollider>();
            playerCollider.center = new Vector3(0f, 1f, 0f);
            playerCollider.size = new Vector3(0.8f, 1.8f, 0.8f);
            player.AddComponent<Hurtbox>();
            player.transform.position = Vector3.zero;
            _attackBox.transform.position = player.transform.position + new Vector3(0.6f, 0f, 0f);
            Physics.SyncTransforms();

            parryBox.Activate(new HitboxActivation
            {
                Attacker = player,
                InstanceId = 9,
                PhaseIndex = 0,
                Profile = new HitboxProfile
                {
                    CapsuleRadius = 1.2f, CapsuleHeight = 2.2f,
                    LocalOffset = new Vector3(0f, 1f, 0.5f),
                },
            });

            HitboxActivation first = MakeActivation(damage: 50f);
            first.InstanceId = 2;
            _attackBox.Activate(first);
            _resolver.ProcessTick();
            Assert.That(recorder.Calls, Is.EqualTo(1), "第一发应配对成功");

            HitboxActivation second = MakeActivation(damage: 50f);
            second.InstanceId = 3;
            _attackBox.Activate(second);
            _resolver.ProcessTick();
            Assert.That(recorder.Calls, Is.EqualTo(2),
                "新攻击实例应再次配对——parry 可连续触发（去重按实例号，不按次数）");
            Assert.That(playerHealth.CurrentHealth, Is.EqualTo(100f).Within(1e-3f),
                "两发伤害都应被 parry 无敌挡下");
        }
        finally
        {
            Object.DestroyImmediate(player);
        }
    }

    // n. 攻击者中途销毁：OnDisable 自移出注册表，结算器照常运转不炸不残留
    //    （Hitbox 注册表自管理是采集层职责——敌人死亡/回收时活跃判定框必须随对象退场）
    [Test]
    public void AttackerDestroyed_ActiveHitboxLeavesRegistry_Safely()
    {
        _attackBox.Activate(MakeActivation(damage: 10f));
        Assert.That(Hitbox.Registry.Contains(_attackBox), Is.True, "前置：激活框应在注册表");

        Object.DestroyImmediate(_attacker); // OnDisable 应自移除 + 停用
        _resolver.ProcessTick();

        Assert.That(Hitbox.Registry.Contains(_attackBox), Is.False, "销毁后应已移出注册表");
        Assert.That(_targetHealth.CurrentHealth, Is.EqualTo(100f).Within(1e-3f), "死框不得再结算伤害");
    }

    // o. 多攻击者同目标：两框各自去重、独立结算——多敌人围攻玩家的 resolver 底座
    [Test]
    public void MultipleAttackers_SameTarget_EachResolvesIndependently()
    {
        GameObject attacker2 = new GameObject("Attacker2");
        try
        {
            Hitbox box2 = attacker2.AddComponent<Hitbox>();
            box2.Kind = HitboxKind.Damage;
            attacker2.transform.position = new Vector3(2f, 0f, 0.5f); // 目标另一侧
            Physics.SyncTransforms();

            _attackBox.Activate(MakeActivation(damage: 10f));
            box2.Activate(new HitboxActivation
            {
                Attacker = attacker2,
                InstanceId = 7,
                PhaseIndex = 0,
                Profile = new HitboxProfile
                {
                    Shape = HitboxShape.Capsule,
                    CapsuleRadius = 1.0f,
                    CapsuleHeight = 2.0f,
                    LocalOffset = new Vector3(0f, 1f, 0f),
                },
                BaseDamage = 10f,
                DamageType = DamageType.Melee,
                Knockback = 0f,
                HitStopSec = 0f,
            });

            _resolver.ProcessTick();
            Assert.That(_targetHealth.CurrentHealth, Is.EqualTo(80f).Within(1e-3f),
                "两个攻击实例应各自结算一次（去重按实例，不同攻击者互不干扰）");

            _resolver.ProcessTick();
            Assert.That(_targetHealth.CurrentHealth, Is.EqualTo(80f).Within(1e-3f),
                "同实例多 tick 仍各只结算一次");
        }
        finally
        {
            Object.DestroyImmediate(attacker2);
        }
    }

    // 测试桩：parry 接收记录（PlayerCombat.OnParrySuccess 的等价链路——授无敌）
    private sealed class ParryRecorder : MonoBehaviour, IParryReceiver
    {
        public int Calls;
        public HealthComponent HealthToProtect;

        public void OnParrySuccess(in ParryInfo info)
        {
            Calls++;
            if (HealthToProtect != null) HealthToProtect.GrantImmunity(DamageType.All, 1f);
        }
    }

    private sealed class KnockbackRecorder : MonoBehaviour, IKnockbackReceiver
    {
        public Vector3 LastImpulse;

        public void OnKnockback(Vector3 impulse) => LastImpulse = impulse;
    }
}
