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

#if UNITY_EDITOR
    [Test]
    public void ImportedAttackEventsStayInsideAttackWindow()
    {
        AnimationClip clip = null;
        foreach (Object asset in UnityEditor.AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/fbx/Attack.fbx"))
            if (asset is AnimationClip candidate && !candidate.name.StartsWith("__preview__")) clip = candidate;
        Assert.IsNotNull(clip);
        AnimationEvent[] events = clip.events;
        Assert.AreEqual(3, events.Length);
        Assert.AreEqual("UpdateAttack", events[0].functionName);
        Assert.AreEqual("EnableHitbox", events[1].functionName);
        Assert.AreEqual("DisableHitbox", events[2].functionName);
        Assert.Greater(events[1].time, events[0].time);
        Assert.Greater(events[2].time, events[1].time);
        PlayerCombat prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab")
            .GetComponent<PlayerCombat>();
        Assert.Less(events[2].time, Mathf.Min(clip.length, prefab.AttackDuration));
        Assert.AreEqual(0.336f, events[1].time, 0.02f, "导入后的开判定事件应位于挥砍前段");
    }
#endif

    [UnityTest]
    public IEnumerator SlowMotionKeepsAttackWindowInPlayerTime()
    {
        PlayerCombat combat = SpawnPlayer(out _, out _);
        combat.SetAttackDuration(0.4f);
        TimeManager.SlowMotion(1f, 0.1f);
        combat.BeginAttack(TimeManager.UnscaledTime);
        yield return new WaitForSecondsRealtime(0.5f);
        Assert.IsTrue(combat.IsAttacking, "慢动作期间不应按真实时间提前结束动作");
        TimeManager.ClearSlowMotion();
        yield return new WaitForSecondsRealtime(0.5f);
        Assert.IsFalse(combat.IsAttacking);
    }

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
        Enemy enemy = SpawnEnemy(out Damageable damageable);

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
        Enemy enemy = SpawnEnemy(out Damageable damageable);
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
        Enemy enemy = SpawnEnemy(out Damageable damageable);

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
        Enemy enemy = SpawnEnemy(out Damageable damageable);
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

    private Enemy SpawnEnemy(out Damageable damageable)
    {
        GameObject host = new GameObject("TestEnemy");
        host.SetActive(false);
        Enemy found = host.AddComponent<Enemy>();
        found.ConfigureStats(120f, 8f, 4f);
        GameObject popupHost = new GameObject("TestDamagePopup");
        popupHost.transform.SetParent(host.transform, false);
        popupHost.SetActive(false);
        found.SetDamagePopup(popupHost.AddComponent<DamagePopup>());
        // 受击盒：判定靠碰撞体，没有它永远打不中
        BoxCollider hitBox = host.AddComponent<BoxCollider>();
        hitBox.size = new Vector3(1f, 2f, 1f);
        hitBox.center = new Vector3(0f, 1f, 0f);
        host.SetActive(true);
        _spawned.Add(host);
        damageable = found;
        return found;
    }

    [UnityTest]
    public IEnumerator MultipleCollidersTakeOneHitPerSwing()
    {
        PlayerCombat combat = SpawnPlayer(out _, out Hitbox hitbox);
        Enemy enemy = SpawnEnemy(out Damageable target);
        target.SetInvulnerableTime(0f);
        enemy.gameObject.AddComponent<BoxCollider>();
        enemy.transform.position = new Vector3(0f, 0f, 1.4f);
        Assert.IsNull(enemy.GetComponent<Rigidbody>(), "必须覆盖真实场景中的静态敌人");
        combat.BeginAttack(TimeManager.UnscaledTime);
        combat.EnableHitbox();
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(target.MaxHealth - combat.AttackDamage, target.Health);

        hitbox.EnableHitbox();
        enemy.transform.position += Vector3.right * 10f;
        yield return new WaitForFixedUpdate();
        enemy.transform.position -= Vector3.right * 10f;
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(1, target.DamagedCount, "同一刀离开再进入也不能重复扣血");

        combat.DisableHitbox();
        yield return new WaitForFixedUpdate();
        combat.EnableHitbox();
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(2, target.DamagedCount, "下一段挥砍应重新允许命中");
    }

    [UnityTest]
    public IEnumerator AttackExpiryAndDisableCloseHitbox()
    {
        PlayerCombat combat = SpawnPlayer(out _, out Hitbox hitbox);
        combat.SetAttackDuration(0.1f);
        combat.BeginAttack(TimeManager.UnscaledTime);
        combat.EnableHitbox();
        yield return new WaitForSeconds(0.2f);
        Assert.IsFalse(combat.IsAttacking);
        Assert.IsFalse(hitbox.GetComponent<Collider>().enabled, "漏掉动画收招事件时也必须关闭");
        combat.ResetAttackState();
        combat.BeginAttack(TimeManager.UnscaledTime);
        combat.EnableHitbox();
        combat.enabled = false;
        Assert.IsFalse(hitbox.GetComponent<Collider>().enabled);
        Assert.IsFalse(combat.IsAttacking);
    }

    [UnityTest]
    public IEnumerator EnemyHitboxDamagesGameManagerPlayer()
    {
        PlayerCombat player = SpawnPlayer(out _, out _);
        player.gameObject.AddComponent<Player>();
        GameManager manager = GameManager.Instance;
        _spawned.Add(manager.gameObject);
        manager.Player.Reset();
        Enemy enemy = SpawnEnemy(out _);
        GameObject weapon = new GameObject("EnemyWeapon");
        weapon.transform.SetParent(enemy.transform, false);
        weapon.transform.position = player.transform.position;
        BoxCollider box = weapon.AddComponent<BoxCollider>();
        box.size = Vector3.one * 3f;
        Hitbox hitbox = weapon.AddComponent<Hitbox>();
        hitbox.Configure(CampType.Enemy, enemy, 0f);
        hitbox.EnableHitbox();
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(manager.Player.MaxHealth - enemy.AttackPower, manager.Player.Health);
        Assert.AreEqual(0f, manager.Player.TakeDamage(10f), "玩家无敌帧应由同一份数据维护");
        manager.Player.Reset();
        Assert.IsFalse(manager.Player.IsInvulnerable);
        Assert.AreEqual(manager.Player.MaxHealth, manager.Player.Health);
    }

    [UnityTest]
    public IEnumerator ImpactRecoversToTailSpeedBeforeSlowMotionEnds()
    {
        PlayerCombat combat = SpawnPlayer(out _, out _);
        combat.OnLandedHit(null, Vector3.zero, Vector3.forward, 1f);
        Assert.That(TimeManager.PlayerRate, Is.EqualTo(combat.ImpactTimeScale).Within(0.001f));
        yield return new WaitForSecondsRealtime(0.12f);
        Assert.That(TimeManager.PlayerRate, Is.EqualTo(combat.HitTimeScale).Within(0.001f),
            "短冲击结束后必须恢复尾部速度，不能把强减速延长至整个命中窗口");
        yield return new WaitForSecondsRealtime(0.12f);
        Assert.That(TimeManager.PlayerRate, Is.EqualTo(1f).Within(0.001f));
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
        motor = null;
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
        hitbox.Configure(CampType.Player, combat, 1f);

        host.SetActive(true);
        combat.SetReferences(hitbox, null);
        _spawned.Add(host);
        return combat;
    }
}
