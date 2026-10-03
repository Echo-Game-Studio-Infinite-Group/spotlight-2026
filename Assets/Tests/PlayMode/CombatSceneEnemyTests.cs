using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 战斗链路集成测试。
//
// 判定方式已从「SphereCast 每帧扫描」改为参考实现 LittleAdventure 的
// 「Trigger 判定盒 + 动画事件开关」，因此触发器成为测试的主路径：
//   BeginAttack → EnableHitbox → 物理推进让触发器互相接触 → 结算伤害。
// 直接物理扫描的老断言已删除，避免测的是一个不再存在的实现。
public sealed class CombatSceneEnemyTests
{
    private readonly List<Object> _spawned = new List<Object>();

    [TearDown]
    public void TearDown()
    {
        TimeManager.ClearSlowMotion();
        foreach (Object created in _spawned)
        {
            if (created != null) Object.DestroyImmediate(created);
        }

        _spawned.Clear();
    }

    [UnityTest]
    public IEnumerator AttackHitboxDamagesEnemy()
    {
        PlayerCombat combat = SpawnPlayer(out PlayerMotor motor, out Hitbox playerHitbox);
        Enemy enemy = SpawnEnemy(out Enemy damageable);

        // 敌人摆在玩家正前方，进入判定盒范围
        Vector3 facing = combat.transform.forward;
        enemy.transform.position = combat.transform.position + facing * 1.4f;
        yield return null;

        float before = damageable.Health;
        Assert.IsTrue(combat.BeginAttack(TimeManager.UnscaledTime), "攻击应当能开启窗口");
        combat.EnableHitbox();
        // 触发器由物理推进才结算，等两个物理帧
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();

        Assert.Less(damageable.Health, before,
            $"判定盒未命中：敌人应扣血。玩家={combat.transform.position} 敌人={enemy.transform.position} 朝向={facing}");
        Assert.GreaterOrEqual(damageable.DamagedCount, 1, "受击次数应被记录");
    }

    [UnityTest]
    public IEnumerator HitboxIsDisabledAfterAttackEnds()
    {
        PlayerCombat combat = SpawnPlayer(out _, out Hitbox playerHitbox);
        Assert.IsNotNull(playerHitbox, "PlayerCombat 必须引用判定盒");

        // 判定盒默认必须是关的：否则敌人一进场就会被持续打到
        Collider box = playerHitbox.GetComponent<Collider>();
        Assert.IsNotNull(box);
        Assert.IsFalse(box.enabled, "判定盒默认应关闭");

        combat.BeginAttack(TimeManager.UnscaledTime);
        combat.EnableHitbox();
        Assert.IsTrue(playerHitbox.GetComponent<Collider>().enabled, "挥砍起手后判定盒应打开");

        combat.DisableHitbox();
        Assert.IsFalse(playerHitbox.GetComponent<Collider>().enabled, "收招后判定盒应关闭");

        combat.ResetAttackState();
        Assert.IsFalse(playerHitbox.GetComponent<Collider>().enabled, "复位攻击状态后判定盒应关闭");

        // 虽然没有需要等待的时序，但 [UnityTest] 必须是迭代器方法
        yield return null;
    }

    [UnityTest]
    public IEnumerator DamagePopupIsReusedNotCreated()
    {
        PlayerCombat combat = SpawnPlayer(out _, out _);
        Enemy enemy = SpawnEnemy(out Enemy damageable);
        enemy.transform.position = combat.transform.position + combat.transform.forward * 1.4f;
        yield return null;

        int poolBefore = Object.FindObjectsOfType<DamagePopup>(true).Length;
        Assert.Greater(poolBefore, 0, "跳字组件应在场景中预先存在（池化，不是运行时生成）");

        combat.BeginAttack(TimeManager.UnscaledTime);
        combat.EnableHitbox();
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();

        int poolAfter = Object.FindObjectsOfType<DamagePopup>(true).Length;
        Assert.AreEqual(poolBefore, poolAfter, "命中不应新建跳字对象 —— 应从池里复用");
    }

    [UnityTest]
    public IEnumerator InvulnerabilityBlocksRepeatedHits()
    {
        SpawnPlayer(out _, out _);
        Enemy enemy = SpawnEnemy(out Enemy damageable);

        // 第一次命中生效
        float first = damageable.TakeDamage(10f, Vector3.zero, Vector3.forward);
        Assert.Greater(first, 0f, "首次命中应造成伤害");
        Assert.IsTrue(damageable.IsInvulnerable, "受击后应进入无敌帧");

        // 无敌帧内的第二次命中必须被忽略
        float second = damageable.TakeDamage(10f, Vector3.zero, Vector3.forward);
        Assert.AreEqual(0f, second, "无敌帧内不应再次受到伤害");

        // 等无敌帧结束后可以再次受击
        yield return new WaitForSeconds(damageable.InvulnerableTime + 0.1f);
        Assert.IsFalse(damageable.IsInvulnerable, "无敌帧到期后应恢复可受击");
        float third = damageable.TakeDamage(10f, Vector3.zero, Vector3.forward);
        Assert.Greater(third, 0f, "无敌帧结束后应能再次受到伤害");
    }

    [UnityTest]
    public IEnumerator HitAppliesSlowMotionThenRecovers()
    {
        PlayerCombat combat = SpawnPlayer(out _, out _);
        Enemy enemy = SpawnEnemy(out Enemy damageable);
        enemy.transform.position = combat.transform.position + combat.transform.forward * 1.4f;
        yield return null;

        Assert.IsFalse(TimeManager.InSlowMotion, "未命中前不应处于减速状态");
        float rateBefore = TimeManager.PlayerRate;

        combat.BeginAttack(TimeManager.UnscaledTime);
        combat.EnableHitbox();
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();

        Assert.IsTrue(TimeManager.InSlowMotion, "命中后应进入减速");
        // 两段减速里更强的一档会生效，因此只断言「确实被压过速」
        Assert.Less(TimeManager.PlayerRate, 1f, "减速期间玩家速率应小于 1");

        yield return new WaitForSeconds(combat.HitStopSeconds + combat.ImpactSeconds + 0.2f);
        Assert.IsFalse(TimeManager.InSlowMotion, "阈值时间后减速应自动结束");
        Assert.AreEqual(rateBefore, TimeManager.PlayerRate, 0.001f, "减速结束后速率应恢复原值");
    }

    private Enemy SpawnEnemy(out Enemy damageable)
    {
        GameObject host = new GameObject("TestEnemy");
        host.SetActive(false);
        var body = host.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        Enemy found = host.AddComponent<Enemy>();
        found.ConfigureStats(120f, 8f, 4f);
        var popupObject = new GameObject("DamagePopup");
        popupObject.transform.SetParent(host.transform, false);
        found.SetDamagePopup(popupObject.AddComponent<DamagePopup>());
        popupObject.SetActive(false);
        // 受击盒：判定靠碰撞体，没有它永远打不中
        BoxCollider hitBox = host.AddComponent<BoxCollider>();
        hitBox.size = new Vector3(1f, 2f, 1f);
        hitBox.center = new Vector3(0f, 1f, 0f);
        host.SetActive(true);
        _spawned.Add(host);
        damageable = found;
        return found;
    }

    private PlayerCombat SpawnPlayer(out PlayerMotor motor, out Hitbox hitbox)
    {
        if (Object.FindObjectOfType<TimeManager>() == null)
        {
            GameObject clock = new GameObject("TestTimeManager");
            clock.AddComponent<TimeManager>();
            _spawned.Add(clock);
        }

        GameObject host = new GameObject("TestPlayer");
        host.transform.position = Vector3.zero;
        host.transform.rotation = Quaternion.identity;
        host.SetActive(false);
        CharacterController controller = host.AddComponent<CharacterController>();
        controller.height = 1.8f;
        controller.radius = 0.35f;
        motor = host.AddComponent<PlayerMotor>();
        var parameters = ScriptableObject.CreateInstance<MovementParams>();
        motor.SetParams(parameters);
        motor.enabled = false;
        _spawned.Add(parameters);
        PlayerCombat combat = host.AddComponent<PlayerCombat>();

        // 判定盒挂在玩家子物体上，默认关闭，由 EnableHitbox 打开
        GameObject hitboxHost = new GameObject("PlayerHitbox");
        hitboxHost.transform.SetParent(host.transform, false);
        hitboxHost.transform.localPosition = new Vector3(0f, 0.9f, 1.2f);
        BoxCollider box = hitboxHost.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(1.2f, 1.2f, 1.6f);
        box.enabled = false;
        hitbox = hitboxHost.AddComponent<Hitbox>();
        hitbox.Configure(CampType.Player, combat);

        host.SetActive(true);
        combat.SetReferences(hitbox, null);
        _spawned.Add(host);
        return combat;
    }
}
