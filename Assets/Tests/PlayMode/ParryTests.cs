#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

// Parry 链路的回归测试。
//
// 断言的是整条链路，而不是某个内部字段：
//   敌人命中 → CombatComponent.TryHit → IParryReceiver（PlayerCombat）→ 窗口判定 → 掉不掉血 / 慢不慢 / 有无敌
// 远端那一半走 RangeComponent.ReportHit，因为那里是**另一条**入口（子弹不经 Hitbox）；
// 两条入口任意一条漏接，parry 都会「时灵时不灵」，只测一条是查不出来的。
public sealed class ParryTests
{
    private readonly List<GameObject> _created = new List<GameObject>();
    private GameObject _player;
    private CharacterController _playerBody;
    private HealthComponent _playerHealth;
    private CombatComponent _playerCombat;
    private CombatComponent _attacker;
    private RangeComponent _ranged;

    [SetUp]
    public void SetUp()
    {
        // TimeManager 全是静态入口：没有实例时 GlobalRate 读不到自己登记的效果，时缓断言会假绿。
        var clock = new GameObject("ParryTestClock"); _created.Add(clock);
        clock.AddComponent<TimeManager>();
        TimeManager.ClearSlowMotion();

        _player = new GameObject("ParryTestPlayer"); _created.Add(_player);
        _playerHealth = _player.AddComponent<HealthComponent>();
        // 默认 0.6s 无敌会把「窗口外照样受伤」测成不受伤，这里必须先关掉。
        _playerHealth.SetInvulnerableTime(0f);
        _playerCombat = _player.AddComponent<CombatComponent>();
        _playerCombat.Camp = CampType.Player;
        // PlayerCombat 要求 CharacterController，AddComponent 会自动补上；判定盒留空，走 parry 分支即可。
        _player.AddComponent<PlayerCombat>();
        _playerBody = _player.GetComponent<CharacterController>();

        var attacker = new GameObject("ParryTestAttacker"); _created.Add(attacker);
        _attacker = attacker.AddComponent<CombatComponent>();
        _attacker.Camp = CampType.Enemy;
        _attacker.Damage = 25f;
        _ranged = attacker.AddComponent<RangeComponent>();
        _ranged.Camp = CampType.Enemy;
        Physics.SyncTransforms();
    }

    [TearDown]
    public void TearDown()
    {
        TimeManager.ClearSlowMotion();
        TimeManager.SetPaused(false);
        for (int i = _created.Count - 1; i >= 0; i--)
            if (_created[i] != null) Object.DestroyImmediate(_created[i]);
        _created.Clear();
    }

    private AttackDamageResult Hit(AttackSourceType source)
        => _attacker.TryHit(_playerBody, _playerHealth,
            new AttackDamageRequest(25f, _player.transform.position, Vector3.forward, 0L, 0,
                source, _attacker.ParryKind));

    private bool OpenParry(float now) => _playerCombat.ReceiveAction(InputBuffer.Action.Parry, now);

    [Test]
    public void ParryWindow_OpensAndExpiresAfterConfiguredSeconds()
    {
        float now = TimeManager.UnscaledTime;
        Assert.IsTrue(OpenParry(now), "按下 parry 应当开窗");
        Assert.IsTrue(_playerCombat.IsParrying(now), "开窗后当帧必须可判定");
        Assert.IsFalse(_playerCombat.IsParrying(now + _playerCombat.ParryWindowSeconds + 0.01f),
            "窗口结束后不应再判定成功");
    }

    [Test]
    public void ParryWindow_CannotBeRefreshedWhileOpen()
    {
        float now = TimeManager.UnscaledTime;
        Assert.IsTrue(OpenParry(now));
        // 窗口内再按一次不延长窗口：否则按住不放就能刷出无限判定。
        Assert.IsFalse(OpenParry(now + 0.15f), "窗口开着时不应再开新窗口");
        Assert.IsFalse(_playerCombat.IsParrying(now + _playerCombat.ParryWindowSeconds + 0.01f),
            "第二次按键不能把窗口往后推");
    }

    [Test]
    public void Parry_HasOneSecondCooldown()
    {
        float now = TimeManager.UnscaledTime;
        Assert.IsTrue(OpenParry(now), "第一次 parry 应当成功");
        Assert.IsFalse(_playerCombat.IsParryReady(now + 0.2f), "冷却期内不该就绪");
        Assert.IsFalse(OpenParry(now + 0.2f), "冷却期内再按不能开窗");
        Assert.IsFalse(OpenParry(now + 0.9f), "冷却结束前一帧也不能开窗");
        Assert.IsTrue(_playerCombat.IsParryReady(now + 1.01f), "1 秒后应当就绪");
        Assert.IsTrue(OpenParry(now + 1.01f), "冷却走完后应当能再次 parry");
        Assert.GreaterOrEqual(_playerCombat.ParryCooldownSeconds, 1f - 0.001f);
    }

    [Test]
    public void HitOutsideWindow_StillDamages()
    {
        float before = _playerHealth.Health;
        AttackDamageResult result = Hit(AttackSourceType.Melee);
        Assert.Greater(result.AppliedDamage, 0f, "没开 parry 窗口时必须按原逻辑受伤");
        Assert.Less(_playerHealth.Health, before);
    }

    [Test]
    public void MeleeParry_BlocksDamage_AndSlowsGlobalTime()
    {
        float now = TimeManager.UnscaledTime;
        Assert.IsTrue(OpenParry(now));
        float before = _playerHealth.Health;

        AttackDamageResult result = Hit(AttackSourceType.Melee);

        Assert.AreEqual(0f, result.AppliedDamage, "parry 成功时不得扣血");
        Assert.AreEqual(before, _playerHealth.Health);
        Assert.Less(TimeManager.GlobalRate, 1f, "近战 parry 必须触发全局时缓");
        // 全局倍率必须真的传到玩家 / 世界的推进速率上，否则只是登记了个数字：
        // 「没慢放」这个 bug 就长在这条断链上。
        Assert.Less(TimeManager.PlayerRate, 1f, "玩家推进速率必须被压慢");
        Assert.Less(TimeManager.WorldRate, 1f, "世界推进速率必须被压慢");
        Assert.Less(TimeManager.PlayerFixedDeltaTime, Time.fixedUnscaledDeltaTime * 0.5f,
            "每个逻辑帧推进的量必须明显变小，不然画面上不会有慢放");
        Assert.IsFalse(_playerCombat.IsParrying(now), "一次开窗只能挡一下，成功后立即消耗");
    }

    [Test]
    public void ProjectileParry_BlocksDamage_WithoutSlowMotion()
    {
        _ranged.Damage = 8f;
        float now = TimeManager.UnscaledTime;
        Assert.IsTrue(OpenParry(now));
        float before = _playerHealth.Health;

        // 走真实远端入口：子弹命中 → RangeComponent.ReportHit（不经 Hitbox）。
        _ranged.ReportHit(_playerHealth, _player.transform.position, Vector3.forward);

        Assert.AreEqual(before, _playerHealth.Health, "远程 parry 成功时不得扣血");
        Assert.AreEqual(1f, TimeManager.GlobalRate, "飞行道具 parry 不触发时缓");
    }

    [Test]
    public void ProjectileWithoutParryWindow_StillDamages()
    {
        _ranged.Damage = 8f;
        float before = _playerHealth.Health;
        _ranged.ReportHit(_playerHealth, _player.transform.position, Vector3.forward);
        Assert.Less(_playerHealth.Health, before, "窗口外被子弹打中要照常掉血");
    }

    [Test]
    public void Parry_GrantsImmediateInvulnerability()
    {
        Assert.IsTrue(OpenParry(TimeManager.UnscaledTime));
        Hit(AttackSourceType.Melee);
        Assert.IsTrue(_playerHealth.IsInvulnerable, "parry 成功后必须立刻获得无敌");

        // 窗口已经消耗掉，这一下不是被 parry 挡的，只能是被无敌档下的。
        float before = _playerHealth.Health;
        AttackDamageResult followUp = Hit(AttackSourceType.Melee);
        Assert.AreEqual(0f, followUp.AppliedDamage, "parry 后的无敌窗口内不应再次受伤");
        Assert.AreEqual(before, _playerHealth.Health);
        Assert.GreaterOrEqual(_playerCombat.ParryInvulnerableSeconds, 0.8f - 0.001f);
    }

    [Test]
    public void DisadvantageParry_BlocksDamage_WithoutSlowMotion()
    {
        _attacker.ParryKind = AttackParryKind.Disadvantage;
        Assert.IsTrue(OpenParry(TimeManager.UnscaledTime));
        float before = _playerHealth.Health;

        AttackDamageResult result = Hit(AttackSourceType.Melee);

        Assert.AreEqual(0f, result.AppliedDamage, "劣势 parry 同样要挡下这一击");
        Assert.AreEqual(before, _playerHealth.Health);
        Assert.AreEqual(1f, TimeManager.GlobalRate, "劣势 parry 不触发时缓，只把玩家弹开");
    }

    [Test]
    public void Parry_CancelsOngoingAttackWindow()
    {
        float now = TimeManager.UnscaledTime;
        Assert.IsTrue(_playerCombat.ReceiveAction(InputBuffer.Action.Attack, now), "攻击应当能起手");
        Assert.IsTrue(_playerCombat.IsAttacking(now));
        Assert.IsTrue(OpenParry(now), "普攻后摇里必须能按出 parry");
        Assert.IsFalse(_playerCombat.IsAttacking(now), "parry 要能取消普攻，连续 parry 才接得起来");
    }

    [Test]
    public void TargetWithoutParryReceiver_StillTakesDamage()
    {
        var plain = new GameObject("ParryTestPlainTarget"); _created.Add(plain);
        var body = plain.AddComponent<CapsuleCollider>();
        HealthComponent health = plain.AddComponent<HealthComponent>();
        health.SetInvulnerableTime(0f);
        Physics.SyncTransforms();

        AttackDamageResult result = _attacker.TryHit(body, health,
            new AttackDamageRequest(25f, plain.transform.position, Vector3.forward, 0L, 0));

        Assert.Greater(result.AppliedDamage, 0f, "没有 IParryReceiver 的目标不能因为 parry 改造而免疫");
    }

    [Test]
    public void InputBuffer_ParryIsConsumedOnce()
    {
        var buffer = new InputBuffer();
        buffer.Push(InputBuffer.Action.Parry, 0f);
        Assert.IsTrue(buffer.ConsumeParry(0.05f));
        Assert.IsFalse(buffer.ConsumeParry(0.05f), "同一次按键不能被消费两次");

        buffer.Push(InputBuffer.Action.Parry, 0f);
        Assert.IsFalse(buffer.ConsumeParry(1f), "超过缓冲窗口的输入应当过期");
    }

    [Test]
    public void PlayerControls_ExposesParryBoundToE()
    {
        var asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>("Assets/Input/PlayerControls.inputactions");
        Assert.IsNotNull(asset, "找不到 PlayerControls.inputactions");
        InputAction parry = asset.FindAction("Gameplay/Parry");
        Assert.IsNotNull(parry, "Gameplay 里必须有 Parry 动作");
        Assert.IsTrue(parry.bindings.Count > 0, "Parry 必须有默认按键");
        Assert.AreEqual("<Keyboard>/e", parry.bindings[0].path, "按 E 才是文档约定的 parry 键");
    }
}
#endif
