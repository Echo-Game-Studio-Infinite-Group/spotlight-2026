using System;
using System.Collections;
using System.Linq;
using GameJam.Actions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class ChaosMechanicsTests
{
    private ChaosManager _chaos;
    private MapManager _map;
    private PlayerMotor _motor;
    private HealthComponent _health;
    private ChaosRunController _run;
    private const string Tutorial = "Whitebox_Tutorial";
    private const string Next = "Whitebox_Room_01";

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        TimeManager.SetPaused(false);
        if (MapManager.Exists) Object.Destroy(MapManager.Instance.gameObject);
        yield return null;
        yield return SceneManager.LoadSceneAsync("Boot", LoadSceneMode.Single);
        yield return Until(() => ChaosManager.Exists && MapManager.Exists &&
            MapManager.Instance.CurrentRoomSceneName == Tutorial && !MapManager.Instance.IsTransitioning &&
            MapManager.Instance.IsRoomReady(Next) && Object.FindObjectOfType<MapBootstrapper>().IsReady);
        _chaos = ChaosManager.Instance; _map = MapManager.Instance;
        _motor = Object.FindObjectOfType<PlayerMotor>(); _health = _motor.GetComponent<HealthComponent>();
        _run = _motor.GetComponent<ChaosRunController>();
        _health.SetInvulnerableTime(0f);
        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        TimeManager.SetPaused(false);
        TimeManager.ClearSlowMotion();
        if (MapManager.Exists) Object.Destroy(MapManager.Instance.gameObject);
        yield return null;
    }

    [Test]
    public void BootHasOneGlobalChaosAndConfiguredAuthoredRoomContent()
    {
        Assert.That(Object.FindObjectsOfType<ChaosManager>().Length, Is.EqualTo(1));
        Assert.That(_chaos.gameObject.scene.name, Is.EqualTo("WhiteboxGame"));
        Assert.That(_chaos.Value, Is.Zero);
        Assert.That(_chaos.Settings, Is.Not.Null);
        Assert.That(Object.FindObjectOfType<ChaosHUD>(), Is.Not.Null);
        Assert.That(_run.CurrentCheckpointId, Is.EqualTo(Tutorial + "/入口"));
        Assert.That(Components<ChaosLamp>(Tutorial).Length, Is.EqualTo(2));
        Assert.That(Variants(Tutorial).VariantCount, Is.EqualTo(3));
        Assert.That(Variants(Tutorial).CurrentVariantId, Is.EqualTo("联调A"));
    }

    [UnityTest]
    public IEnumerator RealAttackInputRunsExistingActionHitboxAndBreaksLampExactlyOnce()
    {
        ChaosLamp lamp = Components<ChaosLamp>(Tutorial).Single(item => item.Gain == 10);
        _motor.Teleport(lamp.transform.position - lamp.transform.forward * 1.25f);
        _motor.transform.rotation = lamp.transform.rotation;
        PlayerActionRunner runner = _motor.GetComponent<PlayerActionRunner>();
        runner.ResetActions();
        // 注入的是既有输入快照，依次经过动作序列、动画判定盒和 CombatComponent，而非直接调用装置受击。
        for (int tick = 0; tick < 100 && !lamp.IsBroken; tick++)
        {
            var edges = tick == 2 ? new[] { new ActionInputEdge(ActionInputButtons.Attack,
                ActionInputStep.Edge.Pressed, ActionInputButtons.Attack) } : null;
            runner.SimulateTick(new ActionInputSample(10000 + tick,
                tick == 2 ? ActionInputButtons.Attack : ActionInputButtons.None, default, edges), default, 1f / 60f);
        }
        Assert.That(lamp.IsBroken, Is.True, "真实普通攻击应能击碎装置");
        Assert.That(_chaos.Value, Is.EqualTo(10));
        Assert.That(lamp.ReceiveAttack(new AttackDamageRequest(25f, default, default, 0, 0)).AppliedDamage, Is.Zero);
        Assert.That(_chaos.Value, Is.EqualTo(10));
        // 恢复固定循环前重建执行器时序，不能让自动小帧号接到手工的 10000 帧之后。
        runner.Disconnect(); Assert.That(runner.Connect(), Is.True);
        yield return null;
    }

    [Test]
    public void LampGainFifteenAndPreloadedRoomRewardProtection()
    {
        var hit = new AttackDamageRequest(25f, default, default, 0, 0);
        ChaosLamp ahead = Components<ChaosLamp>(Next).First();
        Assert.That(ahead.ReceiveAttack(hit).AppliedDamage, Is.Zero);
        Assert.That(_chaos.Value, Is.Zero);
        ChaosLamp lamp = Components<ChaosLamp>(Tutorial).Single(item => item.Gain == 15);
        Assert.That(lamp.ReceiveAttack(hit).AppliedDamage, Is.Positive);
        Assert.That(_chaos.Value, Is.EqualTo(15));
    }

    [Test]
    public void NormalClampSpecialValuesAndLoopEligibilityMatchFinalDesign()
    {
        foreach (int value in new[] { 0, 30, 80, 100 })
        { _chaos.SetNormalValue(value); Assert.That(_chaos.IsInLoopRange, Is.True, "混乱度=" + value); }
        foreach (int value in new[] { 31, 79 })
        { _chaos.SetNormalValue(value); Assert.That(_chaos.IsInLoopRange, Is.False, "混乱度=" + value); }
        _chaos.SetNormalValue(200); Assert.That(_chaos.Value, Is.EqualTo(100));
        Assert.That(_chaos.NormalGoalReached, Is.True);
        _chaos.SetNormalValue(5); _chaos.ApplyDeathPenalty(); Assert.That(_chaos.Value, Is.Zero);
        Assert.That(_chaos.SetSpecialValue(-5), Is.True);
        Assert.That(_chaos.IsNegative, Is.True); Assert.That(_chaos.IsInLoopRange, Is.False);
        Assert.That(_chaos.SetSpecialValue(999), Is.True);
        Assert.That(_chaos.NormalGoalReached, Is.False); Assert.That(_chaos.IsInLoopRange, Is.False);
        _chaos.ApplyDeathPenalty(); Assert.That(_chaos.Value, Is.EqualTo(999));
        Assert.That(_chaos.SetSpecialValue(50), Is.False);
    }

    [UnityTest]
    public IEnumerator PreloadedSuccessorUpdatesBeforeEntryWhileCurrentRoomWaitsForReturn()
    {
        _chaos.SetNormalValue(40);
        Assert.That(Variants(Tutorial).CurrentVariantId, Is.EqualTo("联调A"));
        Assert.That(Variants(Next).CurrentVariantId, Is.EqualTo("联调B"));
        Assert.That(_map.TryEnterRoom(Tutorial, Next), Is.True);
        yield return Until(() => !_map.IsTransitioning && _map.CurrentRoomSceneName == Next);
        Assert.That(Variants(Next).CurrentVariantId, Is.EqualTo("联调B"));
        Assert.That(_chaos.Value, Is.EqualTo(40));
        Assert.That(_run.CurrentCheckpointId, Is.EqualTo(Next + "/入口"));
        Assert.That(Object.FindObjectsOfType<PlayerMotor>().Length, Is.EqualTo(1));
    }

    [UnityTest]
    public IEnumerator DeathReducesChaosThenRebuildsMatchingVariantAndRevivesSamePlayer()
    {
        int playerId = _motor.GetInstanceID(); int cameraId = Camera.main.GetInstanceID();
        _chaos.SetNormalValue(35); Variants(Tutorial).Refresh();
        Assert.That(Variants(Tutorial).CurrentVariantId, Is.EqualTo("联调B"));
        int returns = 0; _run.ReturnedToCheckpoint += died => { if (died) returns++; };
        _health.TakeDamage(10000f);
        _health.TakeDamage(10000f);
        yield return Until(() => returns == 1);
        Assert.That(_chaos.Value, Is.EqualTo(25));
        Assert.That(Variants(Tutorial).CurrentVariantId, Is.EqualTo("联调A"));
        Assert.That(_health.Health, Is.EqualTo(_health.MaxHealth));
        Assert.That(_motor.GetInstanceID(), Is.EqualTo(playerId)); Assert.That(Camera.main.GetInstanceID(), Is.EqualTo(cameraId));
        Assert.That(_motor.GetComponent<PlayerActionRunner>().IsConnected, Is.True);
        Assert.That(Vector3.Distance(_motor.transform.position, _motor.GetComponent<PlayerRespawn>().RespawnPosition), Is.LessThan(.2f));
        yield return null;
        Assert.That(returns, Is.EqualTo(1)); Assert.That(_chaos.Value, Is.EqualTo(25));
    }

    [UnityTest]
    public IEnumerator NonlethalFallLosesHealthButNotChaosAndRefreshesCurrentVariant()
    {
        _chaos.SetNormalValue(45);
        int returns = 0; bool countedAsDeath = true;
        _run.ReturnedToCheckpoint += died => { returns++; countedAsDeath = died; };
        DropBelowRoom();
        yield return Until(() => returns > 0);
        Assert.That(countedAsDeath, Is.False); Assert.That(returns, Is.EqualTo(1));
        Assert.That(_chaos.Value, Is.EqualTo(45));
        Assert.That(_health.Health, Is.EqualTo(_health.MaxHealth - _chaos.Settings.FallDamage));
        Assert.That(Variants(Tutorial).CurrentVariantId, Is.EqualTo("联调B"));
    }

    [UnityTest]
    public IEnumerator LethalFallAppliesOneDeathPenaltyAndNormalDeathNeverGoesNegative()
    {
        _chaos.SetNormalValue(5); _health.Reset(1f);
        int returns = 0; _run.ReturnedToCheckpoint += died => { if (died) returns++; };
        DropBelowRoom();
        yield return Until(() => returns > 0);
        Assert.That(returns, Is.EqualTo(1)); Assert.That(_chaos.Value, Is.Zero);
        Assert.That(_health.IsAlive, Is.True);
        yield return null; Assert.That(_chaos.Value, Is.Zero);
    }

    [UnityTest]
    public IEnumerator SameVariantIsRebuiltFromPrefabAtDeathRatherThanKeepingMutatedState()
    {
        ChaosRoomVariants variants = Variants(Tutorial);
        _chaos.SetNormalValue(20); variants.Refresh();
        GameObject previous = variants.CurrentContent;
        new GameObject("仅运行期的污染物件").transform.SetParent(previous.transform);
        _health.TakeDamage(10000f);
        yield return Until(() => _health.IsAlive && !_run.IsReturning);
        Assert.That(variants.CurrentVariantId, Is.EqualTo("联调A"));
        Assert.That(variants.CurrentContent, Is.Not.SameAs(previous));
        Assert.That(variants.CurrentContent.GetComponentsInChildren<Transform>(true).Any(item => item.name == "仅运行期的污染物件"), Is.False);
    }

    [UnityTest]
    public IEnumerator BrokenLampPersistsAcrossDeathAndRoomUnloadWithoutRewardFarming()
    {
        ChaosLamp lamp = Components<ChaosLamp>(Tutorial).Single(item => item.Gain == 15);
        lamp.ReceiveAttack(new AttackDamageRequest(25f, default, default, 0, 0));
        _health.TakeDamage(10000f);
        yield return Until(() => _health.IsAlive && !_run.IsReturning);
        Assert.That(_chaos.Value, Is.EqualTo(5)); Assert.That(lamp.IsBroken, Is.True);
        Assert.That(_map.TryEnterRoom(Tutorial, Next), Is.True);
        yield return Until(() => !_map.IsTransitioning);
        Assert.That(_map.TryEnterRoom(Next, Tutorial), Is.True);
        yield return Until(() => !_map.IsTransitioning && _map.IsRoomReady(Tutorial));
        yield return null;
        ChaosLamp restored = Components<ChaosLamp>(Tutorial).Single(item => item.Gain == 15);
        Assert.That(restored.IsBroken, Is.True);
        Assert.That(restored.ReceiveAttack(new AttackDamageRequest(25f, default, default, 0, 0)).AppliedDamage, Is.Zero);
        Assert.That(_chaos.Value, Is.EqualTo(5));
    }

    [Test]
    public void CheckpointPriorityAndExplicitOverrideAreSupported()
    {
        ChaosCheckpoint high = Checkpoint("后段", 5);
        ChaosCheckpoint low = Checkpoint("前段", 1);
        Assert.That(_run.TrySetCheckpoint(high), Is.True);
        Assert.That(_run.TrySetCheckpoint(low), Is.False);
        low.Configure("再次覆盖", 1, true);
        Assert.That(_run.TrySetCheckpoint(low), Is.True);
        Assert.That(_run.CurrentCheckpointId, Is.EqualTo(Tutorial + "/再次覆盖"));
        Assert.That(_motor.GetComponent<PlayerRespawn>().RespawnPosition, Is.EqualTo(low.transform.position));
    }

    [UnityTest]
    public IEnumerator ExplicitSpecialCheckpointAllowsNegativeDeathWithoutAddingSpecialMapRoute()
    {
        var point = Checkpoint("特殊记录点_仅测试", 10);
        point.Configure("特殊记录点_仅测试", 10, false, true);
        Assert.That(_run.TrySetCheckpoint(point), Is.True);
        _chaos.SetNormalValue(5); _health.TakeDamage(10000f);
        yield return Until(() => _health.IsAlive && !_run.IsReturning);
        Assert.That(_chaos.Value, Is.EqualTo(-5)); Assert.That(_map.CurrentRoomSceneName, Is.EqualTo(Tutorial));
    }

    [UnityTest]
    public IEnumerator RestartClearsChaosAndLampLedgerButDoesNotDuplicatePlayersOrManagers()
    {
        Components<ChaosLamp>(Tutorial).First().ReceiveAttack(new AttackDamageRequest(25f, default, default, 0, 0));
        int old = _chaos.GetInstanceID();
        GameManager.Instance.RestartLevel();
        yield return Until(() => ChaosManager.Exists && ChaosManager.Instance.GetInstanceID() != old &&
            !MapManager.Instance.IsTransitioning && MapManager.Instance.CurrentRoomSceneName == Tutorial &&
            MapManager.Instance.IsRoomReady(Next) && Object.FindObjectOfType<MapBootstrapper>().IsReady);
        Assert.That(ChaosManager.Instance.Value, Is.Zero);
        Assert.That(Components<ChaosLamp>(Tutorial).All(item => !item.IsBroken), Is.True);
        Assert.That(Object.FindObjectsOfType<ChaosManager>().Length, Is.EqualTo(1));
        Assert.That(Object.FindObjectsOfType<PlayerMotor>().Length, Is.EqualTo(1));
    }

    [UnityTest]
    public IEnumerator ExplicitReturnRefreshesVariantWithoutTakingHealthOrChaos()
    {
        _chaos.SetNormalValue(85);
        Assert.That(_run.RequestCheckpointReturn(), Is.True);
        yield return Until(() => !_run.IsReturning);
        Assert.That(Variants(Tutorial).CurrentVariantId, Is.EqualTo("联调C"));
        Assert.That(_chaos.Value, Is.EqualTo(85)); Assert.That(_health.Health, Is.EqualTo(_health.MaxHealth));
        _chaos.SetNormalValue(0);
        Assert.That(_run.RequestCheckpointReturn(), Is.True);
        yield return Until(() => !_run.IsReturning);
        Assert.That(Variants(Tutorial).CurrentVariantId, Is.EqualTo("联调A"));
    }

    [UnityTest]
    public IEnumerator ApproachingCheckpointUsesRealPhysicsTriggerWithoutInteractionKey()
    {
        var point = Checkpoint("自动触发", 5);
        point.transform.position += _motor.transform.forward * 2f;
        point.GetComponent<BoxCollider>().center = Vector3.up;
        point.GetComponent<BoxCollider>().size = new Vector3(2f, 3f, 2f);
        _motor.GetComponent<CharacterController>().Move(_motor.transform.forward * 1.8f);
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();
        Assert.That(_run.CurrentCheckpointId, Is.EqualTo(Tutorial + "/自动触发"));
    }

    [UnityTest]
    public IEnumerator PausedDeathWaitsForResumeAndAppliesPenaltyOnce()
    {
        _chaos.SetNormalValue(25);
        TimeManager.SetPaused(true);
        _health.TakeDamage(10000f);
        yield return null; yield return null;
        Assert.That(_chaos.Value, Is.EqualTo(25)); Assert.That(_run.IsReturning, Is.True);
        TimeManager.SetPaused(false);
        yield return Until(() => _health.IsAlive && !_run.IsReturning);
        Assert.That(_chaos.Value, Is.EqualTo(15));
    }

    [Test]
    public void InvalidOrOverlappingVariantConfigurationsAreRejected()
    {
        ChaosRoomVariants variants = Variants(Tutorial);
        Assert.Throws<ArgumentException>(() => variants.Configure(Array.Empty<ChaosRoomVariant>()));
        Assert.Throws<ArgumentException>(() => variants.Configure(new[]
        {
            new ChaosRoomVariant { Id = "A", MinimumChaos = 0, MaximumChaos = 30, ContentPrefab = variants.CurrentContent },
            new ChaosRoomVariant { Id = "B", MinimumChaos = 30, MaximumChaos = 100, ContentPrefab = variants.CurrentContent }
        }));
        Assert.That(variants.VariantCount, Is.EqualTo(3));
    }

    [Test]
    public void DebugToolsBorrowAndRestoreGameplayInputInsteadOfSwingingWhenClickingUi()
    {
        ChaosHUD hud = Object.FindObjectOfType<ChaosHUD>();
        PlayerInputReader input = _motor.GetComponent<PlayerInputReader>();
        var toggle = typeof(ChaosHUD).GetMethod("ToggleTools", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.That(input.GameplayEnabled, Is.True);
        toggle.Invoke(hud, null); Assert.That(input.GameplayEnabled, Is.False);
        toggle.Invoke(hud, null); Assert.That(input.GameplayEnabled, Is.True);
        toggle.Invoke(hud, null); hud.enabled = false;
        Assert.That(input.GameplayEnabled, Is.True);
    }

    [UnityTest]
    public IEnumerator OfficialChaosHudFollowsGlobalValueIncludingSpecialValues()
    {
        ChaosHUD hud = Object.FindObjectOfType<ChaosHUD>();
        Assert.That(hud, Is.Not.Null);
        Assert.That(hud.NixieDigitsReady, Is.True, "混乱度 HUD 应加载 Resources 中的 0 到 9 辉光管素材");

        _chaos.SetNormalValue(35);
        yield return Until(() => hud.DisplayedValue == 35);
        Assert.That(hud.DisplayedValue, Is.EqualTo(35));

        Assert.That(_chaos.SetSpecialValue(-5), Is.True);
        yield return null;
        Assert.That(hud.DisplayedValue, Is.EqualTo(-5));
    }

    private void DropBelowRoom()
    {
        var respawn = _motor.GetComponent<PlayerRespawn>();
        _motor.Teleport(new Vector3(respawn.RespawnPosition.x, respawn.KillHeight - 2f, respawn.RespawnPosition.z));
    }
    private ChaosCheckpoint Checkpoint(string id, int priority)
    {
        var root = new GameObject("测试记录点_" + id);
        SceneManager.MoveGameObjectToScene(root, SceneManager.GetSceneByName(Tutorial));
        root.transform.position = _motor.GetComponent<PlayerRespawn>().RespawnPosition;
        var checkpoint = root.AddComponent<ChaosCheckpoint>(); checkpoint.Configure(id, priority);
        return checkpoint;
    }
    private static ChaosRoomVariants Variants(string scene) => Components<ChaosRoomVariants>(scene).Single();
    private static T[] Components<T>(string scene) where T : Component => SceneManager.GetSceneByName(scene)
        .GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<T>(true)).ToArray();
    private static IEnumerator Until(Func<bool> condition)
    {
        float deadline = Time.realtimeSinceStartup + 40f;
        while (!condition())
        {
            if (Time.realtimeSinceStartup > deadline) Assert.Fail("等待混乱度联调条件超时");
            yield return null;
        }
    }
}
