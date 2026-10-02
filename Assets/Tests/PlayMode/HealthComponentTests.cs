using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 血量/无敌/霸体/死亡契约回归（设计 §4.6，框架 4.4）：
// 掩码语义（滑铲对投/弹免疫但吃近战）、限时无敌到期、显式解除、叠加取更晚、
// 霸体不硬直但伤害照算、死亡过滤与事件恰好一次、治疗夹紧与死亡无效、重开复位
// 直调 ApplyDamage 纯逻辑验证，不经物理采集——采集/结算链在 DamageResolverTests 覆盖
public class HealthComponentTests
{
    private GameObject _go;
    private HealthComponent _health;
    private int _damaged, _died, _blocked, _healed;
    private bool _lastInterrupted;

    private const float Tolerance = 1e-3f;

    [SetUp]
    public void SetUp()
    {
        _go = new GameObject("HealthTest");
        _health = _go.AddComponent<HealthComponent>();
        _health.MaxHealth = 100f;
        _damaged = _died = _blocked = _healed = 0;
        _lastInterrupted = false;

        _health.Damaged += (info, remaining, interrupted) => { _damaged++; _lastInterrupted = interrupted; };
        _health.Died += _ => _died++;
        _health.ImmunityBlocked += _ => _blocked++;
        _health.Healed += (amount, remaining) => _healed++;
    }

    [TearDown]
    public void TearDown()
    {
        if (_go != null) Object.DestroyImmediate(_go);
    }

    private static DamageInfo MakeInfo(DamageType type, float damage) => new DamageInfo
    {
        Type = type,
        Damage = damage,
        KnockbackDir = Vector3.forward,
    };

    // a. 掩码语义：滑铲型无敌（投+弹）挡投挡弹但吃近战——「对投/弹免疫 ≠ 全无敌」（策划案 §3）
    [Test]
    public void SlidingStyleMask_BlocksGrabAndProjectile_ButMeleeLands()
    {
        _health.GrantImmunity(DamageType.Grab | DamageType.Projectile, 1f);

        _health.ApplyDamage(MakeInfo(DamageType.Melee, 10f));
        Assert.That(_health.CurrentHealth, Is.EqualTo(90f).Within(Tolerance), "近战不在掩码内，伤害应照扣");
        Assert.That(_damaged, Is.EqualTo(1), "近战命中应触发受伤事件");
        Assert.That(_blocked, Is.EqualTo(0), "近战不应触发免疫拦截");

        _health.ApplyDamage(MakeInfo(DamageType.Grab, 10f));
        _health.ApplyDamage(MakeInfo(DamageType.Projectile, 10f));
        Assert.That(_health.CurrentHealth, Is.EqualTo(90f).Within(Tolerance), "投技/飞行道具在掩码内，不应扣血");
        Assert.That(_blocked, Is.EqualTo(2), "投技与飞行道具各应触发一次免疫拦截（滑铲挡弹类反馈）");
    }

    // b. 限时无敌到期：parry 0.8s 类无敌按所属层时间轴自然过期
    [UnityTest]
    public IEnumerator TimedImmunity_ExpiresOnItsOwnClock()
    {
        _health.GrantImmunity(DamageType.All, 0.05f);
        _health.ApplyDamage(MakeInfo(DamageType.Melee, 10f));
        Assert.That(_health.CurrentHealth, Is.EqualTo(100f).Within(Tolerance), "有效期内伤害应被拦");

        yield return new WaitForSecondsRealtime(0.15f); // 真实时间远超 0.05s 时长

        _health.ApplyDamage(MakeInfo(DamageType.Melee, 10f));
        Assert.That(_health.CurrentHealth, Is.EqualTo(90f).Within(Tolerance), "到期后无敌应失效，伤害生效");
    }

    // c. 显式解除（闪避动作结束时调）：只解指定类型，其余类型无敌保留
    [Test]
    public void ClearImmunity_RemovesOnlyRequestedTypes()
    {
        _health.GrantImmunity(DamageType.All, 10f);
        _health.ClearImmunity(DamageType.Melee);

        _health.ApplyDamage(MakeInfo(DamageType.Melee, 10f));
        Assert.That(_health.CurrentHealth, Is.EqualTo(90f).Within(Tolerance), "被显式解除了近战无敌，伤害应生效");

        _health.ApplyDamage(MakeInfo(DamageType.Grab, 10f));
        Assert.That(_health.CurrentHealth, Is.EqualTo(90f).Within(Tolerance), "未解除的类型无敌应保留");
    }

    // d. 同类型叠加取更晚到期：短授权不得缩短已存在的长无敌（parry 连续成功场景）
    [Test]
    public void GrantImmunity_ShorterDoesNotShortenLonger()
    {
        _health.GrantImmunity(DamageType.Melee, 10f);
        _health.GrantImmunity(DamageType.Melee, 0.01f);
        Assert.That(_health.IsImmuneTo(DamageType.Melee, _health.LayerNow), Is.True,
            "0.01s 的短授权不应把 10s 长无敌缩短到马上过期");
    }

    // e. 霸体：受伤不硬直（interrupted=false）但伤害照算——霸体与无敌是两个独立状态（设计 §4.6）
    [Test]
    public void SuperArmor_DamageLandsWithoutInterrupt()
    {
        _health.SuperArmor = true;
        _health.ApplyDamage(MakeInfo(DamageType.Melee, 10f));
        Assert.That(_health.CurrentHealth, Is.EqualTo(90f).Within(Tolerance), "霸体不免伤，伤害照算");
        Assert.That(_lastInterrupted, Is.False, "霸体受击不应硬直打断（interrupted=false）");

        _health.SuperArmor = false;
        _health.ApplyDamage(MakeInfo(DamageType.Melee, 10f));
        Assert.That(_lastInterrupted, Is.True, "非霸体受击应硬直打断（interrupted=true）");
    }

    // f. 死亡过滤：死后不再吃伤害/治疗，Died 事件恰好一次
    [Test]
    public void Death_FiltersEverythingAfterwards()
    {
        _health.ApplyDamage(MakeInfo(DamageType.Melee, 999f));
        Assert.That(_health.IsDead, Is.True);
        Assert.That(_died, Is.EqualTo(1), "致死一击应触发一次死亡事件");
        Assert.That(_health.CurrentHealth, Is.EqualTo(0f).Within(Tolerance), "死亡血量应夹紧到 0 不为负");

        _health.ApplyDamage(MakeInfo(DamageType.Melee, 10f));
        _health.Heal(50f);
        Assert.That(_died, Is.EqualTo(1), "死后不得重复死亡事件");
        Assert.That(_damaged, Is.EqualTo(1), "死后不再触发受伤事件");
        Assert.That(_healed, Is.EqualTo(0), "死亡后治疗无效（医疗机器人血包不复活）");
        Assert.That(_health.CurrentHealth, Is.EqualTo(0f).Within(Tolerance), "死后血量不再变动");
    }

    // g. 治疗夹紧与事件：不超上限，非正量忽略
    [Test]
    public void Heal_ClampsToMaxAndFiresEvent()
    {
        _health.ApplyDamage(MakeInfo(DamageType.Melee, 30f));
        _health.Heal(50f);
        Assert.That(_health.CurrentHealth, Is.EqualTo(100f).Within(Tolerance), "治疗不得超出上限");
        Assert.That(_healed, Is.EqualTo(1), "有效治疗应触发事件");

        _health.Heal(0f);
        _health.Heal(-5f);
        Assert.That(_healed, Is.EqualTo(1), "零/负治疗量应被忽略（不触发事件）");
    }

    // h. 重开复位（框架 4.5「重开无残留」）：复活、清无敌、清霸体
    [Test]
    public void ResetForRestart_RevivesAndClearsImmunity()
    {
        _health.ApplyDamage(MakeInfo(DamageType.Melee, 999f));
        _health.ResetForRestart();
        Assert.That(_health.IsDead, Is.False, "复位后应复活");
        Assert.That(_health.CurrentHealth, Is.EqualTo(100f).Within(Tolerance), "复位后应满血");

        _health.GrantImmunity(DamageType.All, 10f);
        _health.SuperArmor = true;
        _health.ResetForRestart();
        _health.ApplyDamage(MakeInfo(DamageType.Melee, 10f));
        Assert.That(_health.CurrentHealth, Is.EqualTo(90f).Within(Tolerance),
            "复位应清掉无敌（否则本伤害被拦出 100）——重开不得残留无敌与霸体");
        Assert.That(_lastInterrupted, Is.True, "复位应清掉霸体（否则 interrupted=false）");
    }

    // i. Hurtbox 静态形态免疫与 Health 动态无敌的组合判定（设计 §4.4：静态常驻 + 动态授予两条通道）
    [Test]
    public void Hurtbox_StaticImmunity_CombinesWithDynamic()
    {
        BoxCollider collider = _go.AddComponent<BoxCollider>();
        collider.center = new Vector3(0f, 1f, 0f);
        Hurtbox hurtbox = _go.AddComponent<Hurtbox>();
        hurtbox.ImmuneTypes = DamageType.Grab;

        float now = _health.LayerNow;
        Assert.That(hurtbox.IsImmuneTo(DamageType.Grab, now), Is.True, "静态形态免疫（对投免疫的体型）应生效");
        Assert.That(hurtbox.IsImmuneTo(DamageType.Melee, now), Is.False, "未配静态免疫的类型不应被拦");

        _health.GrantImmunity(DamageType.Melee, 1f);
        Assert.That(hurtbox.IsImmuneTo(DamageType.Melee, now), Is.True, "Health 动态无敌应叠加进组合判定");
    }

    // j. 敌人无敌按世界时间轴：世界层被压低时到期同步变慢（框架 4.3 表格：敌人逻辑走世界层——
    //     世界时缓/时停期间敌人无敌不白过，解冻后照剩余时长走完）
    [UnityTest]
    public IEnumerator EnemyImmunity_AdvancesOnWorldLayerClock()
    {
        GameObject timeGo = new GameObject("TimeManager_HealthTest");
        timeGo.AddComponent<TimeManager>();
        try
        {
            _health.Layer = TimeLayer.World; // 敌人侧约定：世界层
            yield return new WaitForSecondsRealtime(0.05f);

            TimeScaleHandle handle = TimeManager.RegisterScale(TimeLayer.World, 0.05f, "test_j");
            yield return new WaitForSecondsRealtime(0.05f);

            _health.GrantImmunity(DamageType.All, 0.3f); // 世界时间轴 0.3s
            yield return new WaitForSecondsRealtime(0.35f); // 真实 0.35s，世界时间只走了 ~0.02s

            Assert.That(_health.IsImmuneTo(DamageType.Melee, _health.LayerNow), Is.True,
                "世界层被压到 5% 时，敌人 0.3s 无敌在真实 0.35s 后仍未到期（按世界时钟走）");

            TimeManager.Release(handle);
            yield return new WaitForSecondsRealtime(0.35f); // 恢复流速，补完剩余窗口
            Assert.That(_health.IsImmuneTo(DamageType.Melee, _health.LayerNow), Is.False,
                "世界时间恢复流速后无敌应到期");
        }
        finally
        {
            TimeManager.ResetAll();
            // DestroyImmediate：同步清 _instance，避免残留单例让下一个测试的 TimeManager 自毁
            Object.DestroyImmediate(timeGo);
        }
    }
}
