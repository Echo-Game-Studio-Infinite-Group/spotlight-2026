using System;
using System.Collections.Generic;
using System.Reflection;
using GameJam.Actions;
using GameJam.Actions.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class ActionPlayerIntegrationTests
{
    private readonly List<Object> _created = new List<Object>();
    private GameObject _root;
    private PlayerActionRunner _runner;
    private PlayerMotor _motor;
    private PlayerCombat _combat;
    private ActionAnimatorBridge _bridge;
    private ActionCatalog _catalog;
    private ActionDefinition _attack;
    private long _tick;
    private string _folder;

    [SetUp]
    public void SetUp()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ActionPlayerSetup.PlayerPath);
        Assert.NotNull(prefab);
        _root = Object.Instantiate(prefab);
        _created.Add(_root);
        _root.transform.position = new Vector3(2000f, 0f, 2000f);
        foreach (Camera camera in _root.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        _motor = _root.GetComponent<PlayerMotor>();
        Invoke(_root.GetComponent<PlayerInputReader>(), "Awake");
        Invoke(_motor, "Awake");
        _combat = _root.GetComponent<PlayerCombat>();
        Invoke(_combat, "Awake");
        Invoke(_combat, "OnEnable");
        _bridge = _root.GetComponent<ActionAnimatorBridge>();
        _runner = _root.GetComponent<PlayerActionRunner>();
        Assert.NotNull(_runner, "先运行 ActionPlayerSetup.Install，再执行接入测试");
        _catalog = Object.Instantiate(_runner.Catalog); _created.Add(_catalog);
        _attack = Object.Instantiate(_catalog.Actions[0]); _created.Add(_attack);
        _attack.RequestVariants.Clear();
        _attack.CooldownGroup = "";
        _catalog.Actions[0] = _attack;
        _runner.Configure(_catalog, _root.GetComponent<PlayerInputReader>(), _motor, _combat, _bridge);
        Assert.IsTrue(_runner.Connect());
        _tick = 0;
    }
    [TearDown]
    public void TearDown()
    {
        _runner?.Disconnect();
        foreach (Object value in _created) if (value != null) Object.DestroyImmediate(value);
        _created.Clear();
        if (!string.IsNullOrEmpty(_folder)) AssetDatabase.DeleteAsset(_folder);
        _folder = null;
    }
    private static void Invoke(object target, string name) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(target, null);
    private void Tick(ActionInputButtons button = ActionInputButtons.None, Vector2 direction = default, float frames = 1f)
    {
        ActionInputEdge[] edges = button == ActionInputButtons.None ? null : new[] { new ActionInputEdge(button, ActionInputStep.Edge.Pressed, button, direction) };
        _runner.SimulateTick(new ActionInputSample(_tick++, button, direction, edges), new PlayerInputFrame { Move = direction, JumpPressed = button == ActionInputButtons.Jump, JumpTime = TimeManager.UnscaledTime }, frames / 60f);
    }
    private Enemy Target(bool multipleColliders = false)
    {
        GameObject host = new GameObject("ActionTarget"); _created.Add(host);
        host.transform.position = _root.transform.position + Vector3.up;
        host.AddComponent<BoxCollider>().size = Vector3.one * 20f;
        if (multipleColliders)
        {
            GameObject child = new GameObject("AdditionalCollider"); child.transform.SetParent(host.transform, false);
            child.AddComponent<BoxCollider>().size = Vector3.one * 20f;
        }
        Enemy target = host.AddComponent<Enemy>(); target.ConfigureStats(1000f, 0f, 0f); target.SetInvulnerableTime(0f);
        Invoke(target, "OnEnable");
        return target;
    }
    private void ShortAttack()
    {
        ActionAnimationBinding binding = _attack.Timeline[0].Animation;
        binding.BlendFrames = 0;
        _attack.Timeline = new List<ActionSegment>
        {
            new ActionSegment { SegmentId = "startup", DurationFrames = 1, Animation = binding, Control = new ActionControlPolicy { AllowJump = false } },
            new ActionSegment { SegmentId = "active", Phase = ActionPhase.Active, DurationFrames = 1, Animation = binding,
                Events = new List<ActionFrameEvent> { new ActionFrameEvent { EventKey = "combat.hitbox.open", HitGroup = 1 } }, Control = new ActionControlPolicy { AllowJump = false } },
            new ActionSegment { SegmentId = "recovery", Phase = ActionPhase.Recovery, DurationFrames = 2, Animation = binding,
                Events = new List<ActionFrameEvent> { new ActionFrameEvent { EventKey = "combat.hitbox.close" } }, Control = new ActionControlPolicy { AllowJump = false } }
        };
        _attack.CancelWindows[0].Start.SegmentId = "recovery";
    }
    private void Ground()
    {
        GameObject ground = new GameObject("ActionGround"); _created.Add(ground);
        ground.transform.position = _root.transform.position + Vector3.down * 0.5f;
        ground.AddComponent<BoxCollider>().size = new Vector3(100f, 1f, 100f);
        Physics.SyncTransforms(); Tick(); Tick();
        Assert.IsTrue(_motor.IsGrounded);
    }

    [Test]
    public void ConfiguredVolumesDoNotHitThroughWallAndCanRetryAfterWallRemoved()
    {
        Enemy target = Target();
        target.transform.position = _root.transform.position + new Vector3(0f, 1f, 2f);
        target.GetComponent<BoxCollider>().size = Vector3.one * .2f;
        _attack.Combat.HitVolumes.Add(new ActionHitVolume { Center = new Vector3(0f, 1f, 2f), Size = Vector3.one,
            Start = new ActionFrameAnchor { RelativeTo = ActionFrameAnchor.Boundary.ActionStart } });
        var wall = new GameObject("AttackObstruction"); _created.Add(wall);
        wall.transform.position = _root.transform.position + new Vector3(0f, 1f, 1f);
        wall.AddComponent<BoxCollider>().size = new Vector3(10f, 10f, .2f);
        var state = new ActionExecutionState(_attack, 100, 0, 0, 0, default);
        _combat.BeginSequenceAction(state); _combat.SampleSequenceBoundary(state);
        Assert.AreEqual(0, target.DamagedCount);
        Object.DestroyImmediate(wall); _combat.SampleSequenceBoundary(state);
        Assert.AreEqual(1, target.DamagedCount);
    }

    [Test]
    public void HealthOnlyTargetUsesUnifiedSettlementAndSingleFeedbackEvent()
    {
        var host = new GameObject("HealthOnlyTarget"); _created.Add(host);
        host.transform.position = _root.transform.position + Vector3.up;
        host.AddComponent<BoxCollider>().size = Vector3.one * 20f;
        HealthComponent health = host.AddComponent<HealthComponent>(); health.SetMaxHealth(1000f); health.SetInvulnerableTime(0f);
        int landed = 0, sequence = 0, notifications = 0;
        _root.GetComponent<CombatComponent>().Landed += (target, point, direction, damage) => landed++;
        _combat.SequenceHitLanded += (target, request, result) => { Assert.AreSame(health, target); sequence++; };
        _combat.Hitbox.Hit += (box, collider, target) => notifications++;
        var state = new ActionExecutionState(_attack, 100, 0, 0, 0, default);
        _combat.BeginSequenceAction(state);
        _combat.HandleSequenceEvent(state, new ActionFrameEvent { EventKey = "combat.hitbox.open", HitGroup = 1 });
        _combat.SampleSequenceHitbox(100); _combat.SampleSequenceHitbox(100);
        Assert.That(health.Health, Is.EqualTo(1000f - _attack.Combat.Damage));
        Assert.AreEqual(1, landed); Assert.AreEqual(1, sequence); Assert.AreEqual(1, notifications);
    }

    [Test]
    public void InvulnerableTargetIsNotMarkedHitUntilDamageSucceeds()
    {
        Enemy target = Target(true);
        HealthComponent health = target.GetComponent<HealthComponent>(); health.SetInvulnerableTime(100f);
        health.TakeDamage(1f); int initial = target.DamagedCount;
        var state = new ActionExecutionState(_attack, 100, 0, 0, 0, default);
        _combat.BeginSequenceAction(state);
        _combat.HandleSequenceEvent(state, new ActionFrameEvent { EventKey = "combat.hitbox.open", HitGroup = 1 });
        _combat.SampleSequenceHitbox(100); Assert.AreEqual(initial, target.DamagedCount);
        health.Reset(); health.SetInvulnerableTime(0f);
        _combat.SampleSequenceHitbox(100); _combat.SampleSequenceHitbox(100);
        Assert.AreEqual(initial + 1, target.DamagedCount);
    }

    [Test]
    public void SameCampIsFilteredAndStaleSamplesCannotAffectNewInstance()
    {
        Enemy target = Target(true);
        CombatComponent targetCombat = target.GetComponent<CombatComponent>(); targetCombat.Camp = CampType.Player;
        var old = new ActionExecutionState(_attack, 100, 0, 0, 0, default);
        _combat.BeginSequenceAction(old);
        _combat.HandleSequenceEvent(old, new ActionFrameEvent { EventKey = "combat.hitbox.open", HitGroup = 1 });
        _combat.SampleSequenceHitbox(100); Assert.AreEqual(0, target.DamagedCount);
        var next = new ActionExecutionState(_attack, 101, 0, 0, 0, default);
        _combat.BeginSequenceAction(next); targetCombat.Camp = CampType.Enemy;
        _combat.HandleSequenceEvent(next, new ActionFrameEvent { EventKey = "combat.hitbox.open", HitGroup = 1 });
        _combat.HandleSequenceEvent(old, new ActionFrameEvent { EventKey = "combat.hitbox.close" });
        _combat.EndSequenceAction(100); _combat.SampleSequenceHitbox(100);
        Assert.AreEqual(0, target.DamagedCount); Assert.IsTrue(_combat.Hitbox.SequenceWindowOpen);
        _combat.SampleSequenceHitbox(101); Assert.AreEqual(1, target.DamagedCount);
    }

    [Test]
    public void ConfiguredVolumesSweepPastThinEnemyAndShareGroupDamageSnapshot()
    {
        Enemy target = Target(true);
        target.transform.position = _root.transform.position + new Vector3(0f, 1f, 2f);
        foreach (BoxCollider collider in target.GetComponentsInChildren<BoxCollider>()) collider.size = Vector3.one * .1f;
        _attack.Combat.ScaleDamageWithSpeed = true; _attack.Combat.SpeedDamage = AnimationCurve.Linear(0f, 1f, 2f, 2f);
        _attack.Combat.HitVolumes.Add(new ActionHitVolume { Center = Vector3.up, Size = Vector3.one * .2f,
            Start = new ActionFrameAnchor { RelativeTo = ActionFrameAnchor.Boundary.ActionStart } });
        _attack.Combat.HitVolumes.Add(new ActionHitVolume { Center = Vector3.up, Size = Vector3.one * .3f,
            Start = new ActionFrameAnchor { RelativeTo = ActionFrameAnchor.Boundary.ActionStart } });
        _motor.SetHorizontalSpeed(10f);
        var start = new ActionExecutionState(_attack, 100, 0, 0, 0, default);
        _combat.BeginSequenceAction(start); _combat.SampleSequenceBoundary(start);
        Assert.AreEqual(0, target.DamagedCount);
        _motor.SetHorizontalSpeed(100f);
        _root.transform.position += Vector3.forward * 4f;
        var end = new ActionExecutionState(_attack, 100, 0, 1, 0, default);
        _combat.SampleSequenceHitbox(start, end, new[] { _root.transform.position });
        Assert.AreEqual(1, target.DamagedCount);
        Assert.That(target.Health, Is.EqualTo(1000f - _attack.Combat.Damage * 1.5f).Within(.001f));
    }

    [Test]
    public void InstalledHighspeedStatesSampleDistinctPosesAndIndependentMotionTimes()
    {
        ActionDefinition a = _catalog.Actions.Find(action => action.ActionId == "player_highspeed_a");
        ActionDefinition b = _catalog.Actions.Find(action => action.ActionId == "player_highspeed_b");
        Assert.NotNull(a); Assert.NotNull(b); Assert.AreEqual(76, a.TotalFrames); Assert.AreEqual(68, b.TotalFrames);
        Assert.IsFalse(a.Timeline[0].Animation.Clip.isLooping); Assert.IsFalse(b.Timeline[0].Animation.Clip.isLooping);
        _bridge.EnterSegment(new ActionExecutionState(a, 120, 0, 6, 0, default), true);
        _bridge.Sample(new ActionExecutionState(a, 120, 0, 6, 0, default), 0f);
        Transform hand = _bridge.Animator.GetBoneTransform(HumanBodyBones.RightHand);
        Quaternion first = hand.rotation;
        _bridge.EnterSegment(new ActionExecutionState(a, 120, 1, 22, 12, default), true);
        _bridge.Sample(new ActionExecutionState(a, 120, 1, 22, 12, default), 0f);
        Assert.Greater(Quaternion.Angle(first, hand.rotation), .1f);
        float sourceTime = _bridge.Animator.GetFloat(a.Timeline[0].Animation.TimeParameter);
        _bridge.EnterSegment(new ActionExecutionState(b, 121, 0, 5, 0, default), true);
        _bridge.Sample(new ActionExecutionState(b, 121, 0, 5, 0, default), 0f);
        Assert.AreNotEqual(a.Timeline[0].Animation.TimeParameter, b.Timeline[0].Animation.TimeParameter);
        Assert.That(_bridge.Animator.GetFloat(b.Timeline[0].Animation.TimeParameter), Is.EqualTo(5f / 68f).Within(.0001f));
        Assert.That(_bridge.Animator.GetFloat(a.Timeline[0].Animation.TimeParameter), Is.EqualTo(sourceTime).Within(.0001f));
    }

    [Test]
    public void LeftClickResolvesHighspeedAThenBCancelAndLosingSpeedBlocksNextCancel()
    {
        ActionDefinition a = _catalog.Actions.Find(action => action.ActionId == "player_highspeed_a");
        ActionDefinition b = _catalog.Actions.Find(action => action.ActionId == "player_highspeed_b");
        _attack.RequestVariants.AddRange(new[] { a, b, _attack });
        _motor.SetHorizontalSpeed(20f); _motor.LaunchVertical(8f);
        Tick(ActionInputButtons.Attack, frames: 30f);
        Assert.AreSame(a, _runner.Player.State.Action); Assert.Less(_motor.Velocity.y, 0f);
        long instance = _runner.Player.State.InstanceId;
        float speed = _motor.HorizontalSpeed;
        Tick(ActionInputButtons.Attack);
        Assert.AreSame(b, _runner.Player.State.Action); Assert.Greater(_runner.Player.State.InstanceId, instance);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(speed).Within(.001f));
        Tick(frames: 27f); _motor.SetHorizontalSpeed(2f); Tick(ActionInputButtons.Attack);
        Assert.AreSame(b, _runner.Player.State.Action);
        Assert.IsFalse(_bridge.Animator.applyRootMotion);
    }

    [Test]
    public void AirCompletionWaitsForLandingAndTeleportDiscardsPendingMomentumStop()
    {
        ShortAttack(); _attack.Motion.ClearMomentumOnCompletion = true;
        _motor.SetHorizontalSpeed(5f); _motor.LaunchVertical(8f);
        Tick(ActionInputButtons.Attack, frames: 4f);
        Assert.Greater(_motor.HorizontalSpeed, 0f);
        _motor.Teleport(new Vector3(2100f, 0f, 2100f)); _motor.SetHorizontalSpeed(5f);
        Ground(); Assert.Greater(_motor.HorizontalSpeed, 0f, "传送后不能继承上一招的待落地清速");
        _motor.LaunchVertical(8f); _motor.SetHorizontalSpeed(5f); Tick(ActionInputButtons.Attack, frames: 4f);
        for (int i = 0; i < 120 && !_motor.IsGrounded; i++) Tick();
        Assert.IsTrue(_motor.IsGrounded); Assert.That(_motor.HorizontalSpeed, Is.Zero);
    }

    [Test]
    public void CancelClearsSweepHistoryAndNewInstanceMayHitAgain()
    {
        Enemy target = Target(); target.GetComponent<BoxCollider>().size = Vector3.one * .1f;
        target.transform.position = _root.transform.position + new Vector3(0f, 1f, 2f);
        _attack.Combat.HitVolumes.Add(new ActionHitVolume { Center = Vector3.up, Size = Vector3.one * .2f,
            Start = new ActionFrameAnchor { RelativeTo = ActionFrameAnchor.Boundary.ActionStart } });
        var start = new ActionExecutionState(_attack, 100, 0, 0, 0, default);
        _combat.BeginSequenceAction(start); _combat.SampleSequenceBoundary(start); _combat.EndSequenceAction(100);
        _root.transform.position += Vector3.forward * 4f;
        var next = new ActionExecutionState(_attack, 101, 0, 0, 0, default);
        _combat.BeginSequenceAction(next); _combat.SampleSequenceBoundary(next);
        Assert.AreEqual(0, target.DamagedCount, "两刀切换不能产生跨越空间的扫掠");
        _root.transform.position -= Vector3.forward * 2f;
        _combat.SampleSequenceBoundary(next); Assert.AreEqual(1, target.DamagedCount);
        _combat.EndSequenceAction(101);
        next = new ActionExecutionState(_attack, 102, 0, 0, 0, default);
        _combat.BeginSequenceAction(next); _combat.SampleSequenceBoundary(next); Assert.AreEqual(2, target.DamagedCount);
    }

    [Test]
    public void NaturalCompletionClearsGroundSpeedButSuccessfulSameFrameFollowupPreservesIt()
    {
        Ground(); ShortAttack(); _motor.SetHorizontalSpeed(5f);
        _attack.Motion.ClearMomentumOnCompletion = true;
        Tick(ActionInputButtons.Attack, frames: 4f);
        Assert.That(_motor.HorizontalSpeed, Is.Zero);
        _runner.ResetActions(); _motor.SetHorizontalSpeed(5f);
        ActionDefinition follow = _catalog.Actions.Find(action => action.ActionId == "player_jump");
        _runner.Player.Queue(_attack, 0); _runner.Player.Queue(follow, 0);
        _runner.SimulateTick(new ActionInputSample(0), default, 4f / 60f);
        Assert.AreSame(follow, _runner.Player.State.Action);
        Assert.Greater(_motor.HorizontalSpeed, 0f);
    }

    [Test]
    public void DisabledAnimatorIsEvaluatedManuallyAndSamplesDifferentHumanoidPoses()
    {
        _attack.Timeline = new List<ActionSegment> { new ActionSegment { DurationFrames = 60, Animation = _attack.Timeline[0].Animation } };
        _attack.Timeline[0].Animation.NormalizedStart = 0f; _attack.Timeline[0].Animation.NormalizedEnd = 1f; _attack.Timeline[0].Animation.BlendFrames = 0;
        _bridge.EnterSegment(new ActionExecutionState(_attack, 42, 0, 0, 0, default));
        Transform hand = _bridge.Animator.GetBoneTransform(HumanBodyBones.RightHand);
        _bridge.Sample(new ActionExecutionState(_attack, 42, 0, 15, 0, default), 0f); Vector3 first = hand.localPosition;
        Quaternion rotation = hand.localRotation;
        _bridge.Sample(new ActionExecutionState(_attack, 42, 0, 45, 0, default), 0f);
        Assert.IsFalse(_bridge.Animator.enabled);
        Assert.That(_bridge.Animator.GetFloat("ActionTime"), Is.EqualTo(0.75f).Within(0.00001f));
        Assert.IsTrue(Vector3.Distance(first, hand.localPosition) > 0.0001f || Quaternion.Angle(rotation, hand.localRotation) > 0.01f);
    }
    [Test]
    public void NewInstanceRestartsSameStateAndAdjacentStartupSegmentsKeepTheirRange()
    {
        ActionAnimationBinding binding = _attack.Timeline[0].Animation;
        binding.NormalizedStart = 0f; binding.NormalizedEnd = 0.5f; binding.BlendFrames = 0;
        _attack.Timeline = new List<ActionSegment> { new ActionSegment { DurationFrames = 10, Animation = binding },
            new ActionSegment { DurationFrames = 10, Animation = new ActionAnimationBinding { AnimatorState = binding.AnimatorState, Clip = binding.Clip, NormalizedStart = 0.5f, NormalizedEnd = 1f } } };
        _bridge.EnterSegment(new ActionExecutionState(_attack, 1, 0, 0, 0, default));
        _bridge.EnterSegment(new ActionExecutionState(_attack, 1, 1, 10, 10, default));
        Assert.That(_bridge.Animator.GetFloat("ActionTime"), Is.EqualTo(0.5f));
        _bridge.EnterSegment(new ActionExecutionState(_attack, 2, 0, 0, 0, default));
        Assert.That(_bridge.Animator.GetFloat("ActionTime"), Is.Zero);
    }
    [Test]
    public void HitGroupsDeduplicateCollidersAndReopenedWindowsButAllowNewGroupAndInstance()
    {
        Enemy target = Target(true);
        var state = new ActionExecutionState(_attack, 10, 0, 0, 0, default);
        _combat.BeginSequenceAction(state);
        _combat.HandleSequenceEvent(state, new ActionFrameEvent { EventKey = "combat.hitbox.open", HitGroup = 1 });
        _combat.SampleSequenceHitbox(10); _combat.SampleSequenceHitbox(10);
        Assert.AreEqual(1, target.DamagedCount);
        _combat.HandleSequenceEvent(state, new ActionFrameEvent { EventKey = "combat.hitbox.close" });
        _combat.HandleSequenceEvent(state, new ActionFrameEvent { EventKey = "combat.hitbox.open", HitGroup = 1 });
        _combat.SampleSequenceHitbox(10); Assert.AreEqual(1, target.DamagedCount);
        _combat.HandleSequenceEvent(state, new ActionFrameEvent { EventKey = "combat.hitbox.open", HitGroup = 2 });
        _combat.SampleSequenceHitbox(10); Assert.AreEqual(2, target.DamagedCount);
        _combat.EndSequenceAction(10);
        state = new ActionExecutionState(_attack, 11, 0, 0, 0, default);
        _combat.BeginSequenceAction(state);
        _combat.HandleSequenceEvent(state, new ActionFrameEvent { EventKey = "combat.hitbox.open", HitGroup = 1 });
        _combat.SampleSequenceHitbox(11); Assert.AreEqual(3, target.DamagedCount);
    }
    [Test]
    public void LegacyAnimationEventsCannotCloseOrFinishSequenceAttack()
    {
        var state = new ActionExecutionState(_attack, 10, 0, 0, 0, default);
        _combat.BeginSequenceAction(state);
        _combat.HandleSequenceEvent(state, new ActionFrameEvent { EventKey = "combat.hitbox.open", HitGroup = 1 });
        _combat.DisableHitbox(); _combat.FinishAttack();
        Assert.IsTrue(_combat.IsAttacking); Assert.IsTrue(_combat.Hitbox.SequenceWindowOpen);
        Assert.IsFalse(_combat.BeginAttack(TimeManager.UnscaledTime));
        _combat.EndSequenceAction(9); Assert.IsTrue(_combat.IsAttacking);
        _combat.EndSequenceAction(10); Assert.IsFalse(_combat.Hitbox.SequenceWindowOpen);
    }
    [Test]
    public void SingleTickCrossingOpenAndCloseStillSamplesTheOneFrameWindow()
    {
        ShortAttack(); Enemy target = Target(true);
        Tick(ActionInputButtons.Attack, frames: 4f);
        Assert.AreEqual(1, target.DamagedCount);
        Assert.IsFalse(_combat.Hitbox.SequenceWindowOpen);
        Assert.IsFalse(_runner.Player.IsRunning);
        Assert.That(_runner.LastSimulatedSeconds, Is.EqualTo(4f / 60f).Within(0.000001f));
    }
    [Test]
    public void JumpPreinputCancelsRecoveryAndUsesMotorCommandOnce()
    {
        Ground(); ShortAttack(); Tick(ActionInputButtons.Attack);
        Tick(ActionInputButtons.Jump, Vector2.up);
        Assert.AreEqual("player_jump", _runner.Player.State.Action.ActionId);
        Assert.IsFalse(_combat.Hitbox.SequenceWindowOpen);
        Tick();
        Assert.AreEqual(1, _motor.JumpCount);
        Assert.Greater(_motor.Velocity.y, 0f);
        Tick(); Assert.AreEqual(1, _motor.JumpCount);
    }
    [Test]
    public void StationaryJumpCannotBypassRecoveryCancelWindowThroughRawInput()
    {
        Ground(); ShortAttack(); Tick(ActionInputButtons.Attack);
        Tick(ActionInputButtons.Jump);
        Assert.AreSame(_attack, _runner.Player.State.Action);
        Assert.AreEqual(0, _motor.JumpCount);
    }
    [Test]
    public void FractionalFramesAndPauseKeepAnimationAndLogicOnSameClock()
    {
        Tick(ActionInputButtons.Attack, frames: 0.25f);
        Tick(frames: 0.25f); Tick(frames: 0.25f); Tick(frames: 0.25f);
        Assert.That(_runner.Player.State.FrameProgress, Is.EqualTo(1d).Within(0.000001));
        float time = _bridge.Animator.GetFloat("ActionTime");
        Tick(frames: 0f);
        Assert.That(_bridge.Animator.GetFloat("ActionTime"), Is.EqualTo(time));
        Assert.That(_runner.LastSimulatedSeconds, Is.Zero);
    }
    [Test]
    public void DeathInterruptsWindowsAndDisconnectRestoresLegacyOwnership()
    {
        ShortAttack(); Tick(ActionInputButtons.Attack);
        Assert.IsTrue(_combat.Hitbox.SequenceWindowOpen);
        _runner.SetAlive(false);
        Assert.IsFalse(_runner.Player.IsRunning); Assert.IsFalse(_combat.Hitbox.SequenceWindowOpen);
        Tick(ActionInputButtons.Attack); Assert.IsFalse(_runner.Player.IsRunning);
        _runner.Disconnect(); Assert.IsFalse(_motor.IsExternallyDriven); Assert.IsFalse(_combat.IsSequenceDriven); Assert.IsTrue(_bridge.Animator.enabled);
    }
    [Test]
    public void ResourceFailurePreservesSourceAndSuccessfulCancelChargesExactlyOnce()
    {
        _runner.Disconnect();
        var energy = new ActionTestEnergy { Balance = 5f };
        ActionDefinition target = Object.Instantiate(_attack); _created.Add(target);
        target.ActionId = "costly_attack"; target.EnergyCost = 10f; target.Input.Steps.Clear(); _catalog.Actions.Add(target);
        _attack.CancelWindows = new List<ActionCancelWindow> { new ActionCancelWindow { Start = new ActionFrameAnchor { RelativeTo = ActionFrameAnchor.Boundary.ActionStart }, Targets = new List<ActionDefinition> { target } } };
        _runner.Configure(_catalog, _root.GetComponent<PlayerInputReader>(), _motor, _combat, _bridge);
        _runner.SetEnergyAccount(energy);
        Assert.IsTrue(_runner.Connect());
        Tick(ActionInputButtons.Attack); _runner.Player.Queue(target, _tick); Tick();
        Assert.AreSame(_attack, _runner.Player.State.Action); Assert.AreEqual(5f, energy.Balance);
        energy.Balance = 10f; Tick();
        Assert.AreSame(target, _runner.Player.State.Action); Assert.AreEqual(0f, energy.Balance);
        Tick(); Assert.AreEqual(0f, energy.Balance);
    }
    [Test]
    public void MotorOwnershipPreventsDoubleSimulationAndZeroGravityPreservesVerticalSpeed()
    {
        _motor.LaunchVertical(10f);
        Vector3 position = _root.transform.position;
        Invoke(_motor, "FixedUpdate"); Assert.AreEqual(position, _root.transform.position);
        _motor.SetActionControl(new ActionControlPolicy { AllowTurn = false, GravityMultiplier = 0f });
        Quaternion facing = _root.transform.rotation;
        _motor.Simulate(new PlayerInputFrame { Move = Vector2.right }, 1f / 60f, TimeManager.UnscaledTime);
        Assert.That(_motor.Velocity.y, Is.EqualTo(10f).Within(0.00001f));
        Assert.That(Quaternion.Angle(facing, _root.transform.rotation), Is.LessThan(0.001f));
        Assert.Greater(_root.transform.position.y, position.y);
    }
    [TestCase(false, 1f)]
    [TestCase(true, 1f)]
    [TestCase(false, 0.35f)]
    [TestCase(true, 0.35f)]
    [TestCase(false, 0.05f)]
    [TestCase(true, 0.05f)]
    public void SprintKeepsGroundingAndSpeedLimitWithOrWithoutAttack(bool attack, float rate)
    {
        Ground();
        // 起跑前度过真实落地窗口，后续平地奔跑不应再次授予免摩擦资格。
        for (int i = 0; i < ActionSequencePlayer.FramesPerSecond; i++) Tick();
        Assert.IsFalse(_motor.InFrictionWindow);
        float deltaTime = rate / ActionSequencePlayer.FramesPerSecond;
        ActionInputButtons held = ActionInputButtons.Sprint | ActionInputButtons.Forward;
        if (attack) held |= ActionInputButtons.Attack;
        for (int i = 0; i < 4 * ActionSequencePlayer.FramesPerSecond; i++)
        {
            ActionInputEdge[] edges = attack && i == 0
                ? new[] { new ActionInputEdge(ActionInputButtons.Attack, ActionInputStep.Edge.Pressed, held, Vector2.up) } : null;
            _runner.SimulateTick(new ActionInputSample(_tick++, held, Vector2.up, edges),
                new PlayerInputFrame { Move = Vector2.up, SprintHeld = true }, deltaTime);
            if (attack && i == 0) Assert.AreSame(_attack, _runner.Player.State.Action);
            Assert.IsTrue(_motor.IsGrounded, "平地奔跑不应因动作分步失去接地，tick=" + i);
            Assert.IsFalse(_motor.InFrictionWindow, "动作分步不应被当作再次落地，tick=" + i);
            Assert.That(_motor.HorizontalSpeed, Is.LessThanOrEqualTo(_motor.Params.GroundSpeedThreshold + 0.001f));
            Assert.That(_runner.LastSimulatedSeconds, Is.EqualTo(deltaTime).Within(0.000001f));
        }
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(_motor.Params.GroundSpeedThreshold).Within(0.001f));
    }
    [Test]
    public void CrossfadeUsesSeparateMotionTimesAndKeepsSourcePoseProgress()
    {
        _runner.Disconnect();
        _folder = "Assets/__ActionPlayerTests_" + Guid.NewGuid().ToString("N"); AssetDatabase.CreateFolder("Assets", _folder.Substring(7));
        string path = _folder + "/Controller.controller"; Assert.IsTrue(AssetDatabase.CopyAsset(ActionPlayerSetup.ControllerPath, path));
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        ActionAnimationBinding sourceBinding = _attack.Timeline[0].Animation;
        sourceBinding.NormalizedStart = 0f; sourceBinding.NormalizedEnd = 1f; sourceBinding.BlendFrames = 0;
        _attack.Timeline = new List<ActionSegment> { new ActionSegment { DurationFrames = 60, Animation = sourceBinding } };
        _attack.CancelWindows.Clear();
        ActionDefinition target = Object.Instantiate(_attack); _created.Add(target); target.ActionId = "second_attack";
        target.Timeline[0].Animation.AnimatorState = "Base Layer.Actions.SecondAttack";
        target.Timeline[0].Animation.NormalizedStart = 0.1f; target.Timeline[0].Animation.BlendFrames = 6;
        target.Input.Steps.Clear(); _catalog.Actions.Add(target);
        Assert.IsFalse(ActionCatalogValidator.Validate(_catalog).Exists(issue => issue.Level == ActionValidationIssue.Severity.Error));
        ActionPlayerSetup.EnsureAnimator(controller, _catalog);
        _bridge.Animator.runtimeAnimatorController = controller;
        Assert.IsTrue(_runner.Connect());
        _bridge.EnterSegment(new ActionExecutionState(_attack, 1, 0, 0, 0, default));
        _bridge.Sample(new ActionExecutionState(_attack, 1, 0, 42, 0, default), 0f);
        _bridge.EnterSegment(new ActionExecutionState(target, 2, 0, 0, 0, default));
        Assert.That(_bridge.Animator.GetFloat(sourceBinding.TimeParameter), Is.EqualTo(0.7f).Within(0.00001f));
        Assert.That(_bridge.Animator.GetFloat(target.Timeline[0].Animation.TimeParameter), Is.EqualTo(0.1f).Within(0.00001f));
        _bridge.Sample(new ActionExecutionState(target, 2, 0, 1, 0, default), 1f / 60f);
        Assert.IsTrue(_bridge.Animator.IsInTransition(0));
    }
    [Test]
    public void JumpCommandPreservesGroundAccelerationAndBhopOrderOfLegacySnapshot()
    {
        Ground();
        GameObject comparison = Object.Instantiate(_root); _created.Add(comparison);
        comparison.transform.position = _root.transform.position + Vector3.right * 30f;
        PlayerMotor commandMotor = comparison.GetComponent<PlayerMotor>();
        Invoke(comparison.GetComponent<PlayerInputReader>(), "Awake"); Invoke(commandMotor, "Awake");
        commandMotor.Simulate(default, 1f / 60f, TimeManager.UnscaledTime);
        commandMotor.Simulate(default, 1f / 60f, TimeManager.UnscaledTime);
        Assert.IsTrue(commandMotor.IsGrounded);
        PlayerInputFrame input = new PlayerInputFrame { Move = Vector2.up, SprintHeld = true, JumpPressed = true, JumpTime = TimeManager.UnscaledTime };
        _motor.Simulate(input, 1f / 60f, TimeManager.UnscaledTime);
        input.JumpPressed = false;
        Assert.IsTrue(commandMotor.TryExecute(MotorCommandKind.Jump, 0f));
        commandMotor.Simulate(input, 1f / 60f, TimeManager.UnscaledTime);
        Assert.That(Vector3.Distance(_motor.Velocity, commandMotor.Velocity), Is.LessThan(0.0001f));
        Assert.AreEqual(_motor.JumpCount, commandMotor.JumpCount);
    }
    [Test]
    public void PreviewCloneSamplesAnimationWithoutChangingScenePlayer()
    {
        float before = _bridge.Animator.GetFloat("ActionTime");
        using (var preview = new ActionModelPreview())
        {
            preview.Load(AssetDatabase.LoadAssetAtPath<GameObject>(ActionPlayerSetup.PlayerPath), AssetDatabase.LoadAssetAtPath<AnimatorController>(ActionPlayerSetup.ControllerPath));
            Assert.IsTrue(preview.Loaded);
            preview.Scrub(_attack, _attack.TotalFrames * 0.1f);
            Transform hand = preview.Animator.GetBoneTransform(HumanBodyBones.RightHand);
            Quaternion rotation = hand.localRotation;
            preview.Scrub(_attack, _attack.TotalFrames * 0.6f);
            Assert.Greater(Quaternion.Angle(rotation, hand.localRotation), 0.01f);
        }
        Assert.AreEqual(before, _bridge.Animator.GetFloat("ActionTime"));
        Assert.IsFalse(_runner.Player.IsRunning);
    }
    [Test]
    public void SetupIsIncrementalAndAnimationValidatorDetectsWrongClipAndMotionTime()
    {
        _folder = "Assets/__ActionPlayerTests_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", _folder.Substring(7));
        string path = _folder + "/Controller.controller";
        Assert.IsTrue(AssetDatabase.CopyAsset(ActionPlayerSetup.ControllerPath, path));
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        ActionPlayerSetup.EnsureAnimator(controller, _catalog);
        int states = ActionAnimatorAuthoring.GetStates(controller).Count;
        ActionPlayerSetup.EnsureAnimator(controller, _catalog); ActionAnimatorAuthoring.Invalidate();
        Assert.AreEqual(states, ActionAnimatorAuthoring.GetStates(controller).Count);
        Assert.IsEmpty(ActionAnimatorAuthoring.Validate(_catalog, controller));
        foreach (ActionAnimatorAuthoring.StateInfo info in ActionAnimatorAuthoring.GetStates(controller))
            if (info.Path == ActionPlayerSetup.AttackStatePath) info.State.timeParameterActive = false;
        Assert.IsTrue(ActionAnimatorAuthoring.Validate(_catalog, controller).Exists(issue => issue.Message.Contains("Motion Time")));
        ActionCatalog first = ActionPlayerSetup.CreateCatalog(_attack.Timeline[0].Animation.Clip, _combat, _motor.Params, _folder);
        first.Actions[0].Input.PreInputFrames = 19;
        ActionCatalog second = ActionPlayerSetup.CreateCatalog(_attack.Timeline[0].Animation.Clip, _combat, _motor.Params, _folder);
        Assert.AreSame(first, second); Assert.AreEqual(19, second.Actions[0].Input.PreInputFrames);
    }
}
