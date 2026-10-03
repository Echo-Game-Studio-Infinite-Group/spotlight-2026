using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// parry 与派生窗口回归（D3–D5 验收项，trigger 方案版）：
// 窗口语义（开窗/一窗一击/过期拒绝/冷却）、效果链（格挡不结算伤害、世界时缓）、
// 派生窗口（IsDeriveWindowOpen 过期、伤害乘数经 _deriveNext 放大）、
// 敌盒 trigger 链路（窗口内敌盒撞玩家=格挡且血量不动；未开窗=正常扣血）
public class ParryTests
{
    private GameObject _player;
    private PlayerParry _parry;
    private GameObject _enemyGo;
    private Hitbox _enemyBox;
    private GameObject _timeGo;
    private GameObject _gmGo;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        _timeGo = new GameObject("TimeManager_ParryTests");
        _timeGo.AddComponent<TimeManager>();

        _gmGo = new GameObject("GameManager_ParryTests");
        GameManager gm = _gmGo.AddComponent<GameManager>();

        _player = new GameObject("ParryPlayer");
        _player.AddComponent<Player>();                      // Hitbox.StrikeEnemyCamp 按它认玩家
        _player.AddComponent<BoxCollider>();                 // 玩家受击形状（非 trigger：敌盒 trigger 撞它即触发）
        _parry = _player.AddComponent<PlayerParry>();

        _enemyGo = new GameObject("EnemyBox");
        BoxCollider col = _enemyGo.AddComponent<BoxCollider>();
        col.isTrigger = true;
        _enemyGo.AddComponent<StubDamageSource>();
        _enemyBox = _enemyGo.AddComponent<Hitbox>();
        _enemyBox.Configure(CampType.Enemy, _enemyGo.GetComponent<StubDamageSource>(), 0f);

        Physics.SyncTransforms();
        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        // dev 版 TimeManager 无 ResetAll：时缓/窗口随各对象销毁自然消失
        if (_enemyGo != null) Object.DestroyImmediate(_enemyGo);
        if (_player != null) Object.DestroyImmediate(_player);
        if (_gmGo != null) Object.DestroyImmediate(_gmGo);
        if (_timeGo != null) Object.DestroyImmediate(_timeGo);
        yield return null;
    }

    private static IEnumerator RunSteps(float worldSeconds)
    {
        int steps = Mathf.RoundToInt(worldSeconds / Time.fixedDeltaTime);
        for (int i = 0; i < steps; i++) yield return new WaitForFixedUpdate();
    }

    // —— a. 一窗一击 + 时缓：格挡成功触发事件与世界层减速 —— //
    [UnityTest]
    public IEnumerator Parry_BlocksOnce_AndSlowsWorld()
    {
        int parried = 0;
        _parry.Parried += _ => parried++;

        Assert.That(_parry.TryOpenParry(), Is.True, "非冷却期应能开窗");
        Assert.That(_parry.TryParry(_enemyBox), Is.True, "窗口内应格挡");
        Assert.That(parried, Is.EqualTo(1), "格挡应恰好触发一次事件");
        Assert.That(TimeManager.InSlowMotion, Is.True, "格挡应触发世界时缓");

        Assert.That(_parry.TryParry(_enemyBox), Is.False, "一次窗口只格挡一击");
        yield return null;
    }

    // —— b. 窗口过期：没赶上就是没赶上 —— //
    [UnityTest]
    public IEnumerator Parry_WindowExpires_RejectsLate()
    {
        _parry.TryOpenParry();
        yield return new WaitForSecondsRealtime(0.3f); // > 默认窗口 0.25s（UnscaledTime 域，真实等待）

        Assert.That(_parry.WindowOpen, Is.False, "窗口应已过期");
        Assert.That(_parry.TryParry(_enemyBox), Is.False, "过期后不得格挡");
    }

    // —— c. 冷却：窗口结束后的冷却期内不能再开 —— //
    [UnityTest]
    public IEnumerator Parry_Cooldown_BlocksReopen()
    {
        _parry.TryOpenParry();
        _parry.TryParry(_enemyBox); // 消耗窗口
        yield return new WaitForSecondsRealtime(0.3f); // 窗口已被格挡关闭，冷却 0.8s 未到

        Assert.That(_parry.TryOpenParry(), Is.False, "冷却期内不得再开窗");
        yield return new WaitForSecondsRealtime(0.8f); // 冷却自窗口结束点（t0+0.25）起算 0.8s，累计须 >1.05s

        Assert.That(_parry.TryOpenParry(), Is.True, "冷却结束应可再开");
    }

    // —— d. 派生窗口语义：开窗后有效、到期关闭 —— //
    [UnityTest]
    public IEnumerator DeriveWindow_OpensAndExpires()
    {
        PlayerCombat combat = _player.AddComponent<PlayerCombat>(); // RequireComponent 会补 CharacterController
        _parry.TryOpenParry();
        _parry.TryParry(_enemyBox); // 内部惰性接线 combat.OpenDeriveWindow

        Assert.That(combat.IsDeriveWindowOpen, Is.True, "格挡后应开派生窗");
        yield return new WaitForSecondsRealtime(0.6f); // > 默认派生窗 0.5s（UnscaledTime 域）

        Assert.That(combat.IsDeriveWindowOpen, Is.False, "派生窗应已过期");
    }

    // —— e. 派生伤害乘数：_deriveNext 置位时 AttackDamage 放大（策划案：闪斩约为普攻 3 倍）—— //
    [Test]
    public void DeriveAttack_DamageMultiplied()
    {
        PlayerCombat combat = _player.AddComponent<PlayerCombat>();
        float normal = combat.AttackDamage;

        var field = typeof(PlayerCombat).GetField("_deriveNext",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field.SetValue(combat, true);

        Assert.That(combat.AttackDamage, Is.EqualTo(normal * 3f).Within(1e-3f), "派生攻击伤害应按乘数放大");
    }

    // —— f. 敌盒 trigger 链路：窗口内撞玩家=格挡且血量不动；未开窗=正常扣血 —— //
    [UnityTest]
    public IEnumerator EnemyBox_HitsPlayer_ParriedWhenWindowOpen()
    {
        PlayerData health = GameManager.Instance.Player;
        float before = health.Health;

        _enemyGo.transform.position = _player.transform.position; // 敌盒与玩家形状交叠
        Physics.SyncTransforms();
        _parry.TryOpenParry();
        _enemyBox.EnableHitbox();
        yield return RunSteps(0.1f); // 物理步进让 trigger 事件发出

        Assert.That(health.Health, Is.EqualTo(before).Within(1e-4f), "窗口内敌盒命中应被格挡，血量不动");

        // 未开窗的第二击：独立敌盒（Hitbox 有 DisallowMultipleComponent，不能同物体叠加）
        GameObject secondGo = new GameObject("EnemyBox2");
        try
        {
            BoxCollider col2 = secondGo.AddComponent<BoxCollider>();
            col2.isTrigger = true;
            secondGo.AddComponent<StubDamageSource>();
            Hitbox second = secondGo.AddComponent<Hitbox>();
            second.Configure(CampType.Enemy, secondGo.GetComponent<StubDamageSource>(), 0f);
            secondGo.transform.position = _player.transform.position;
            Physics.SyncTransforms();
            second.EnableHitbox();
            yield return RunSteps(0.1f);
        }
        finally
        {
            Object.DestroyImmediate(secondGo);
        }

        Assert.That(health.Health, Is.LessThan(before), "未开窗时敌盒命中应正常扣血");
    }

    // 测试桩：敌方伤害来源（Hitbox 结算前置条件）
    private sealed class StubDamageSource : MonoBehaviour, IDamageSource
    {
        public float AttackDamage => 10f;
    }
}
