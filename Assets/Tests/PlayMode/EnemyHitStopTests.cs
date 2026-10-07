#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor;

// 受击/死亡顿帧的表现回归：敌人动画器是 UnscaledTime（不受 Time.timeScale 影响），
// 冻帧必须显式把 speed 压到 0，这条不变量最容易在后续重构里被静默改坏。
//
// 另一个必须锁住的是「什么时候冻」：受伤/死亡状态要先切进去、混合要走完、还要啃掉片段的起手段，
// 过早压 0 会把动画锁在过渡中间态或静止的起手帧上，看起来就像没挨打。
public sealed class EnemyHitStopTests
{
    private GameObject _root;

    [TearDown]
    public void Cleanup()
    {
        // 顿帧按 UnscaledTime 自动到期，但用例之间不该互相残留世界速率。
        TimeManager.ClearSlowMotion();
        if (_root != null) Object.Destroy(_root);
        _root = null;
    }

    // 极简靶子：只要 Animator + EnemyAnimation + HealthComponent，不需要场景、导航与判定盒。
    private (Enemy enemy, EnemyAnimation animation) CreateFixture()
    {
        var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/Animations/EnemyTest.controller");
        Assert.NotNull(controller, "必须能加载 EnemyTest.controller");

        _root = new GameObject("EnemyHitStopFixture");
        _root.SetActive(false);
        var enemy = _root.AddComponent<Enemy>();
        enemy.ConfigureStats(120f, 0f, 0f);
        // 默认 0.6s 无敌帧会把同一用例里的第二次伤害整段挡掉，先关掉。
        enemy.SetInvulnerableTime(0f);

        var model = new GameObject("Model");
        model.transform.SetParent(_root.transform, false);
        var animator = model.AddComponent<Animator>();
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        // 定格帧靠片段上的 FreezeFrame 动画事件触发，所以这里必须放行事件。
        animator.fireEvents = true;
        var animation = model.AddComponent<EnemyAnimation>();

        _root.SetActive(true);
        animator.Rebind();
        animator.Update(0f);
        return (enemy, animation);
    }

    // 轮询到条件成立，避免把「事件多久才到」写成脆弱的时间断言。
    private static IEnumerator WaitUntil(System.Func<bool> condition, float timeout, string message)
    {
        float deadline = Time.realtimeSinceStartup + timeout;
        while (!condition())
        {
            if (Time.realtimeSinceStartup > deadline) Assert.Fail(message);
            yield return null;
        }
    }

    [UnityTest]
    public IEnumerator HurtAndDeath_FreezeOnAnimationEvent_ThenResume()
    {
        var fixture = CreateFixture();
        yield return null;
        Animator animator = fixture.animation.GetComponent<Animator>();

        // 没挨打时必须有动画速度，否则等于默认就被冻住了。
        Assert.Greater(animator.speed, 0f, "未受击时动画器不该是冻结的");

        // ---- 受伤：先当帧登记顿帧（动作系统与输入缓冲要读到），动画等片段上的 FreezeFrame 事件 ----
        fixture.enemy.TakeDamage(1f, _root.transform.position, Vector3.forward);
        yield return null;
        // InHitStop 表示「顿帧窗口开着」，不表示「动画已经冻住」——两者刻意解耦。
        Assert.IsTrue(TimeManager.InHitStop, "登记顿帧后当帧就该对 InHitStop 可见");
        Assert.AreEqual(1f, TimeManager.WorldRate, .001f, "只冻敌人动画，不压世界时间");
        Assert.AreEqual(1f, TimeManager.PlayerRate, .001f, "只冻敌人动画，不压玩家时间");
        Assert.IsFalse(fixture.animation.IsFrozen, "登记顿帧的当帧不该已经冻住，FreezeFrame 事件还没到");

        yield return WaitUntil(() => fixture.animation.IsFrozen, .3f, "Hurt 上的 FreezeFrame 事件必须能触到定格");
        Assert.IsFalse(animator.IsInTransition(0), "先在过渡中间态冻住 = 姿势没到位，等于没挨打");
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Hurt"), "必须切进 Hurt 才冻");
        // 定格帧由片段上的动画事件决定（Hurt.anim 现已 K 在 0.2s），所以不该是片头。
        Assert.Greater(animator.GetCurrentAnimatorStateInfo(0).normalizedTime, 0.1f, "不该冻在 Hurt 片头");
        Assert.IsTrue(fixture.animation.IsHurting, "冻帧期间 AI 必须仍被受伤锁挡住");

        // ---- 恢复：窗口到期自动解冻，不依赖任何显式 Release ----
        yield return WaitUntil(() => animator.speed > 0f, 1f, "顿帧到期必须自动解冻");
        Assert.IsFalse(fixture.animation.IsFrozen, "解冻后不能留在定格状态");

        // ---- 击杀 ----
        float deathWindow = (float)typeof(EnemyAnimation)
            .GetField("_deathHitStopSeconds", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(fixture.animation);
        // Die 是「稍慢播放」的状态：事件在片段里的时刻要除以状态速度才是真实经过的时间。
        var controller = (UnityEditor.Animations.AnimatorController)animator.runtimeAnimatorController;
        var die = controller.layers[0].stateMachine.states.Single(s => s.state.name == "Die").state;
        float eventRealTime = 2.2666667f / die.speed;

        fixture.enemy.SetInvulnerableTime(0f);
        fixture.enemy.TakeDamage(fixture.enemy.MaxHealth * 10f, _root.transform.position, Vector3.forward);
        yield return WaitUntil(() => fixture.animation.IsFrozen, eventRealTime + .4f, "Die 上的 FreezeFrame 事件必须能触到定格");
        Assert.IsFalse(animator.IsInTransition(0), "Die 也必须在过渡结束后才冻");
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Die"), "致死一击必须切进 Die");
        Assert.Greater(animator.GetCurrentAnimatorStateInfo(0).normalizedTime, .1f, "不该冻在 Die 片头");

        // 窗口剩余多久就该继续冻多久。
        float remainBeforeResume = deathWindow - eventRealTime - .05f;
        if (remainBeforeResume > .3f)
        {
            yield return new WaitForSeconds(remainBeforeResume * .5f);
            Assert.AreEqual(0f, animator.speed, "死亡顿帧窗口内必须一直冻着");
        }
        yield return WaitUntil(() => animator.speed > 0f, deathWindow + 1f, "死亡顿帧到期必须自动恢复");
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Die"), "解冻后仍停在 Die");
    }

    // 回归：敌人自己挂牌期间，玩家侧的顿帧不能提前把它的动画冻住。
    // 玩家顿帧是当帧生效的；如果这条路径没被屏蔽，动画会先被它冻一下，
    // 再被片段上的 FreezeFrame 事件冻一次——表现上就是「多顿了一次，而且很早」。
    [UnityTest]
    public IEnumerator PlayerHitStop_DoesNotPreFreezeEnemy_BeforeFreezeFrameEvent()
    {
        var fixture = CreateFixture();
        yield return null;
        Animator animator = fixture.animation.GetComponent<Animator>();

        // 模拟玩家砍中：玩家侧顿帧当帧就把 InHitStop 打开，敌人自己也同时挂上牌。
        TimeManager.SlowMotion(.09f, .35f);
        fixture.enemy.TakeDamage(1f, _root.transform.position, Vector3.forward);
        yield return null;
        Assert.IsTrue(TimeManager.InHitStop, "此时顿帧窗口确实是开着的");
        Assert.Greater(animator.speed, 0f,
            "挂牌期间不该被玩家顿帧提前冻住：冻在哪一帧只由 FreezeFrame 事件决定");

        // 事件到达后照样要冻住（屏蔽的是「提前」，不是「不冻」）。
        yield return WaitUntil(() => fixture.animation.IsFrozen, .3f, "FreezeFrame 事件仍必须能触到定格");
        Assert.AreEqual(0f, animator.speed, "事件到达后必须冻住");
    }

    [UnityTest]
    public IEnumerator FreezeFor_ZeroDuration_DoesNotFreeze()
    {
        var fixture = CreateFixture();
        yield return null;
        Animator animator = fixture.animation.GetComponent<Animator>();

        fixture.animation.FreezeFor(0f, 1f);
        yield return null;
        Assert.Greater(animator.speed, 0f, "0 秒顿帧不该冻住任何东西");
        Assert.IsFalse(TimeManager.InHitStop);
        Assert.IsFalse(fixture.animation.IsFrozen);
    }
}
#endif
