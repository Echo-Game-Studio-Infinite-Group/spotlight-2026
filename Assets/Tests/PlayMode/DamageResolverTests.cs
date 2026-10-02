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
        // DestroyImmediate：注册表是静态的，延迟销毁会把已死组件漏给下一个测试的 ProcessTick
        if (_attackBox != null) _attackBox.Deactivate();
        Object.DestroyImmediate(_attacker);
        Object.DestroyImmediate(_target);
        Object.DestroyImmediate(_resolverGo);
    }

    private HitboxActivation MakeActivation(float damage = 10f, float speedScale = 0f, float ratio = 0f,
        float knockback = 3f, bool sweep = false)
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
            HitStopSec = 0f, // 测试不打 hit-stop，避免拖慢真实时间
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
