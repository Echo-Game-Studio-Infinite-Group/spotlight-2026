using System;
using System.Collections.Generic;
using System.Linq;
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
    private bool _mobilityCatalogIsolated;

    [TestCase(-1f, 0f, false, "Front_L")]
    [TestCase(1f, 0f, false, "Front_R")]
    [TestCase(-1f, 1f, false, "Front_L45")]
    [TestCase(1f, 1f, false, "Front_R45")]
    [TestCase(0f, -1f, false, "Back")]
    [TestCase(-1f, -1f, false, "Back_L45")]
    [TestCase(1f, -1f, false, "Back_R45")]
    [TestCase(-1f, 0f, true, "Front_L")]
    [TestCase(1f, 0f, true, "Front_R")]
    [TestCase(-1f, 1f, true, "Front_L45")]
    [TestCase(1f, 1f, true, "Front_R45")]
    public void WalkDirectionsUseJogAtLowSpeedAndRunningWithHighMomentum(float x, float y, bool highSpeed, string suffix)
    {
        Ground();
        _motor.Params.GroundFriction = 0f; _motor.Params.Pump = PumpMode.VerbatimQuake;
        var reference = new GameObject("DirectionAnimationReference"); _created.Add(reference);
        _motor.SetMovementReference(reference.transform);
        _motor.SetHorizontalSpeed(highSpeed ? 12f : 0f);
        Vector2 direction = new Vector2(x, y);
        for (int i = 0; i < 60; i++) Tick(direction: direction);
        PlayerAnimation animation = _root.GetComponentInChildren<PlayerAnimation>();
        Assert.AreEqual(highSpeed ? PlayerAnimation.MotionStateMove : PlayerAnimation.MotionStateWalk, animation.CurrentMotionState);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(highSpeed ? 12f : _motor.Params.WalkSpeed).Within(.002f));
        string path = highSpeed ? PlayerLocomotionAnimatorSetup.RunClipPath : PlayerWalkAnimatorSetup.Folder + "CLazy@Mvm_Jog_" + suffix + ".FBX";
        Assert.IsTrue(animation.Animator.GetCurrentAnimatorClipInfo(0).Any(value => AssetDatabase.GetAssetPath(value.clip) == path && value.weight > .99f), path);
        Assert.IsFalse(animation.Animator.applyRootMotion);
    }

    [Test]
    public void WalkSetupAndExistingInstallersKeepOneTreeAndPreserveAuthoredStates()
    {
        string name = "WalkAuthoringTest_" + Guid.NewGuid().ToString("N");
        _folder = "Assets/" + name; AssetDatabase.CreateFolder("Assets", name);
        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(_folder + "/walk.controller");
        AnimatorStateMachine machine = controller.layers[0].stateMachine;
        AnimatorState authored = machine.AddState("AuthoredState"); authored.speed = 1.7f;
        PlayerWalkAnimatorSetup.Ensure(controller);
        AnimatorState walk = machine.states.Single(value => value.state.name == "Walk").state;
        BlendTree tree = (BlendTree)walk.motion;
        tree.children = tree.children.Take(4).ToArray();
        Assert.IsFalse(PlayerWalkAnimatorSetup.HasWalk(controller));
        PlayerWalkAnimatorSetup.Ensure(controller);
        PlayerLocomotionAnimatorSetup.EnsureRun(controller);
        ActionPlayerSetup.EnsureAnimator(controller, _catalog);
        AssetDatabase.SaveAssets();
        Assert.IsTrue(PlayerWalkAnimatorSetup.HasWalk(controller));
        Assert.IsTrue(PlayerLocomotionAnimatorSetup.HasRun(controller));
        Assert.AreSame(tree, walk.motion);
        Assert.AreEqual(1, AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(controller)).OfType<BlendTree>().Count());
        Assert.AreEqual(1.7f, authored.speed);
        Assert.AreEqual(7, tree.children.Length);
    }

    [TestCase(-1f, 0f, -1f, 1f, "Front_L", "Front_L45")]
    [TestCase(-1f, 1f, 1f, 1f, "Front_L45", "Front_R45")]
    [TestCase(1f, 1f, 1f, 0f, "Front_R45", "Front_R")]
    [TestCase(1f, 0f, 1f, -1f, "Front_R", "Back_R45")]
    [TestCase(1f, -1f, 0f, -1f, "Back_R45", "Back")]
    [TestCase(0f, -1f, -1f, -1f, "Back", "Back_L45")]
    [TestCase(-1f, -1f, -1f, 0f, "Back_L45", "Front_L")]
    public void WalkDirectionChangesBlendRealClipsWithoutRestartingOrDelayingMovement(
        float fromX, float fromZ, float toX, float toZ, string fromClip, string toClip)
    {
        PrepareWalkBlend(new Vector2(fromX, fromZ));
        Animator animator = _bridge.Animator;
        float phase = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
        Vector2 target = new Vector2(toX, toZ).normalized;
        Tick(direction: target);
        Vector2 blended = new Vector2(animator.GetFloat(PlayerAnimation.WalkXParameter), animator.GetFloat(PlayerAnimation.WalkZParameter));
        Assert.Greater(Vector2.Distance(blended, target), .01f, "方向动画不能一帧硬切到目标");
        Assert.That(Vector3.Angle(MovementMath.Horizontal(_motor.Velocity), new Vector3(target.x, 0f, target.y)), Is.LessThan(.01f));
        AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(0);
        Assert.IsTrue(clips.Any(value => value.clip.name == "Mvm_Jog_" + fromClip && value.weight > .01f && value.weight < .99f));
        Assert.IsTrue(clips.Any(value => value.clip.name == "Mvm_Jog_" + toClip && value.weight > .01f && value.weight < .99f));
        Assert.That(animator.GetCurrentAnimatorStateInfo(0).normalizedTime, Is.InRange(phase, phase + .1f));
        for (int i = 0; i < 60; i++) Tick(direction: target);
        Assert.IsTrue(animator.GetCurrentAnimatorClipInfo(0).Any(value => value.clip.name == "Mvm_Jog_" + toClip && value.weight > .99f));
    }

    [Test]
    public void WalkBlendDoesNotAdvanceDuringZeroTimeBoundarySamples()
    {
        PrepareWalkBlend(Vector2.left);
        Animator animator = _bridge.Animator;
        Vector2 before = new Vector2(animator.GetFloat(PlayerAnimation.WalkXParameter), animator.GetFloat(PlayerAnimation.WalkZParameter));
        float rate = animator.GetFloat(PlayerAnimation.WalkRateParameter);
        _motor.SetHorizontalSpeed(_motor.Params.WalkSpeed * 2f);
        _motor.Simulate(new PlayerInputFrame { Move = new Vector2(-1f, 1f) }, 1f / 60f, TimeManager.UnscaledTime);
        for (int i = 0; i < 10; i++) _bridge.Sample(default, 0f);
        Assert.AreEqual(before.x, animator.GetFloat(PlayerAnimation.WalkXParameter));
        Assert.AreEqual(before.y, animator.GetFloat(PlayerAnimation.WalkZParameter));
        Assert.AreEqual(rate, animator.GetFloat(PlayerAnimation.WalkRateParameter));
        _bridge.Sample(default, 1f / 60f);
        Assert.Greater(animator.GetFloat(PlayerAnimation.WalkZParameter), before.y);
        Assert.That(animator.GetFloat(PlayerAnimation.WalkRateParameter), Is.InRange(rate,
            _motor.HorizontalSpeed / _motor.Params.WalkSpeed));
        Assert.Less(animator.GetFloat(PlayerAnimation.WalkRateParameter), _motor.HorizontalSpeed / _motor.Params.WalkSpeed);
    }

    [Test]
    public void EnteringWalkAgainUsesCurrentDirectionInsteadOfPreviousWalk()
    {
        PrepareWalkBlend(Vector2.left);
        Tick();
        Vector2 target = new Vector2(1f, -1f).normalized;
        Tick(direction: target);
        Assert.That(_bridge.Animator.GetFloat(PlayerAnimation.WalkXParameter), Is.EqualTo(target.x).Within(.001f));
        Assert.That(_bridge.Animator.GetFloat(PlayerAnimation.WalkZParameter), Is.EqualTo(target.y).Within(.001f));
    }

    private void PrepareWalkBlend(Vector2 direction)
    {
        Ground();
        _motor.Params.GroundFriction = 0f;
        _motor.Params.Pump = PumpMode.VerbatimQuake;
        var reference = new GameObject("WalkBlendReference"); _created.Add(reference);
        _motor.SetMovementReference(reference.transform);
        _root.transform.rotation = Quaternion.identity;
        _motor.SetHorizontalSpeed(0f);
        for (int i = 0; i < 60; i++) Tick(direction: direction);
        Assert.AreEqual(PlayerAnimation.MotionStateWalk, _root.GetComponentInChildren<PlayerAnimation>().CurrentMotionState);
    }

    [TestCase(-1f, "Back_L45")]
    [TestCase(0f, "Back")]
    [TestCase(1f, "Back_R45")]
    public void RawLowSpeedSUsesTheCorrespondingBackwardJog(float x, string suffix)
    {
        Ground(); _motor.Params.GroundFriction = 0f;
        var reference = new GameObject("BackwardAnimationReference"); _created.Add(reference);
        _motor.SetMovementReference(reference.transform);
        for (int i = 0; i < 60; i++)
            _runner.SimulateTick(new ActionInputSample(_tick++, ActionInputButtons.Brake, new Vector2(x, 0f)),
                new PlayerInputFrame { BrakeHeld = true, Move = new Vector2(x, 0f) }, 1f / 60f);
        PlayerAnimation animation = _root.GetComponentInChildren<PlayerAnimation>();
        Assert.AreEqual(PlayerAnimation.MotionStateWalk, animation.CurrentMotionState);
        Assert.IsFalse(_motor.IsBraking);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(_motor.Params.WalkSpeed).Within(.002f));
        string path = PlayerWalkAnimatorSetup.Folder + "CLazy@Mvm_Jog_" + suffix + ".FBX";
        Assert.IsTrue(animation.Animator.GetCurrentAnimatorClipInfo(0).Any(value => AssetDatabase.GetAssetPath(value.clip) == path && value.weight > .99f), path);
    }

    [Test]
    public void LowSpeedSJumpUsesTheBackwardMovingJumpInsteadOfTheStationaryJump()
    {
        Ground(); MobilityEnergy(); _motor.Params.GroundFriction = 0f;
        var reference = new GameObject("BackwardJumpReference"); _created.Add(reference);
        _motor.SetMovementReference(reference.transform);
        for (int i = 0; i < 30; i++)
            _runner.SimulateTick(new ActionInputSample(_tick++, ActionInputButtons.Brake),
                new PlayerInputFrame { BrakeHeld = true }, 1f / 60f);
        ActionInputButtons held = ActionInputButtons.Brake | ActionInputButtons.Jump;
        var edge = new ActionInputEdge(ActionInputButtons.Jump, ActionInputStep.Edge.Pressed, held);
        string jumpAction = null;
        Vector2 jumpDirection = default;
        _runner.FrameEvent += (state, frameEvent) =>
        {
            if (frameEvent.EventKey != "motor.command" || frameEvent.MotorCommand != MotorCommandKind.Jump) return;
            jumpAction = state.Action.ActionId; jumpDirection = state.Input.Direction;
        };
        _runner.SimulateTick(new ActionInputSample(_tick++, held, default, new[] { edge }),
            new PlayerInputFrame { BrakeHeld = true, JumpPressed = true, JumpTime = TimeManager.UnscaledTime }, 1f / 60f);
        Assert.AreEqual("player_jump", jumpAction);
        Assert.AreEqual(Vector2.down, jumpDirection);
        Assert.AreEqual(1, _motor.JumpCount);
        Assert.Less(_motor.Velocity.z, 0f);
    }

    [Test]
    public void DeathClearsStoredRecoveryAndHeldSCannotAccumulateUntilAlive()
    {
        Ground(); _motor.Params.GroundFriction = 0f; _motor.SetHorizontalSpeed(12f);
        _runner.SimulateTick(new ActionInputSample(_tick++, ActionInputButtons.Brake),
            new PlayerInputFrame { BrakeHeld = true }, 10f / 60f);
        Assert.Greater(_motor.BrakeRecoverySpeed, 0f);
        _motor.BufferBrakeRecoveryJump(.2f);
        _runner.SetAlive(false);
        Assert.AreEqual(0f, _motor.BrakeJumpRecoverySpeed);
        _runner.SimulateTick(new ActionInputSample(_tick++, ActionInputButtons.Brake),
            new PlayerInputFrame { BrakeHeld = true }, 1f / 60f);
        Assert.IsFalse(_motor.IsBraking);
        Assert.AreEqual(0f, _motor.BrakeJumpRecoverySpeed);
        _runner.SetAlive(true);
        Tick(ActionInputButtons.Jump);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(8f).Within(.002f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ActionOwnedJumpReturnsBrakeSpeedExactlyOnce(bool highJump)
    {
        Ground(); MobilityEnergy();
        _motor.Params.GroundFriction = 0f;
        _motor.SetHorizontalSpeed(12f);
        _runner.SimulateTick(new ActionInputSample(_tick++, ActionInputButtons.Brake),
            new PlayerInputFrame { BrakeHeld = true }, 10f / 60f);
        Assert.That(_motor.BrakeRecoverySpeed, Is.EqualTo(4f).Within(.002f));
        ActionInputButtons held = ActionInputButtons.Brake | ActionInputButtons.Jump |
            (highJump ? ActionInputButtons.Skill : ActionInputButtons.None);
        var edge = new ActionInputEdge(ActionInputButtons.Jump, ActionInputStep.Edge.Pressed, held);
        _runner.SimulateTick(new ActionInputSample(_tick++, held, default, new[] { edge }),
            new PlayerInputFrame { BrakeHeld = true, JumpPressed = true, JumpTime = TimeManager.UnscaledTime }, 1f / 60f);
        Assert.AreEqual(1, _motor.JumpCount);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(12f).Within(.002f));
        Assert.AreEqual(0f, _motor.BrakeRecoverySpeed);
        for (int i = 0; i < 10; i++)
            _runner.SimulateTick(new ActionInputSample(_tick++, ActionInputButtons.Brake),
                new PlayerInputFrame { BrakeHeld = true }, 1f / 60f);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(12f).Within(.002f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GroundBrakeRejectsSprintAndEnergyDashBeforeCommit(bool energyDash)
    {
        Ground(); ActionTestEnergy energy = MobilityEnergy(100f);
        _motor.Params.GroundFriction = 0f; _motor.SetHorizontalSpeed(12f);
        ActionInputButtons held = ActionInputButtons.Brake | ActionInputButtons.Sprint | ActionInputButtons.Forward |
            (energyDash ? ActionInputButtons.Skill : ActionInputButtons.None);
        var edge = new ActionInputEdge(ActionInputButtons.Sprint, ActionInputStep.Edge.Pressed, held, Vector2.up);
        _runner.SimulateTick(new ActionInputSample(_tick++, held, Vector2.up, new[] { edge }),
            new PlayerInputFrame { Move = Vector2.up, BrakeHeld = true, SprintHeld = true }, 1f / 60f);
        Assert.IsFalse(_runner.Player.IsRunning);
        Assert.AreEqual(100f, energy.Balance);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(11.6f).Within(.002f));
        Assert.Greater(_motor.BrakeRecoverySpeed, 0f);
    }

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
        // 集成夹具固定算法参照值，覆盖 40/55/120 m/s 惯性，不依赖正式配表的当前调参。
        MovementParams parameters = Object.Instantiate(_motor.Params); _created.Add(parameters);
        parameters.GroundSpeedThreshold = 10f; parameters.RunAccel = 10f; parameters.MaxSpeed = 120f;
        _motor.SetParams(parameters);
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
        _mobilityCatalogIsolated = false;
        IsolateMobilityCatalog();
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
        float snapshotMultiplier = _attack.Combat.SpeedDamage.Evaluate(10f / _motor.Params.GroundSpeedThreshold);
        Assert.That(target.Health, Is.EqualTo(1000f - _attack.Combat.Damage * snapshotMultiplier).Within(.001f));
    }

    [Test]
    public void InstalledHighspeedStatesSampleDistinctPosesAndIndependentMotionTimes()
    {
        ActionDefinition a = _catalog.Actions.Find(action => action.ActionId == "player_highspeed_a");
        ActionDefinition b = _catalog.Actions.Find(action => action.ActionId == "player_highspeed_b");
        Assert.NotNull(a); Assert.NotNull(b);
        Assert.IsFalse(a.Timeline[0].Animation.Clip.isLooping); Assert.IsFalse(b.Timeline[0].Animation.Clip.isLooping);
        _bridge.EnterSegment(new ActionExecutionState(a, 120, 0, 6, 0, default), true);
        _bridge.Sample(new ActionExecutionState(a, 120, 0, 6, 0, default), 0f);
        Transform hand = _bridge.Animator.GetBoneTransform(HumanBodyBones.RightHand);
        Quaternion first = hand.rotation;
        int activeStart = a.Timeline[0].DurationFrames;
        int activeSample = activeStart + a.Timeline[1].DurationFrames / 2;
        _bridge.EnterSegment(new ActionExecutionState(a, 120, 1, activeSample, activeStart, default), true);
        _bridge.Sample(new ActionExecutionState(a, 120, 1, activeSample, activeStart, default), 0f);
        Assert.Greater(Quaternion.Angle(first, hand.rotation), .1f);
        float sourceTime = _bridge.Animator.GetFloat(a.Timeline[0].Animation.TimeParameter);
        _bridge.EnterSegment(new ActionExecutionState(b, 121, 0, 5, 0, default), true);
        _bridge.Sample(new ActionExecutionState(b, 121, 0, 5, 0, default), 0f);
        Assert.AreNotEqual(a.Timeline[0].Animation.TimeParameter, b.Timeline[0].Animation.TimeParameter);
        ActionAnimationBinding binding = b.Timeline[0].Animation;
        float expectedTime = Mathf.Lerp(binding.NormalizedStart, binding.NormalizedEnd, 5f / b.Timeline[0].DurationFrames);
        Assert.That(_bridge.Animator.GetFloat(binding.TimeParameter), Is.EqualTo(expectedTime).Within(.0001f));
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
    public void ForwardRunKeepsGroundingAndSpeedLimitWithoutShiftWithOrWithoutAttack(bool attack, float rate)
    {
        Ground();
        // 起跑前度过真实落地窗口，后续平地奔跑不应再次授予免摩擦资格。
        for (int i = 0; i < ActionSequencePlayer.FramesPerSecond; i++) Tick();
        Assert.IsFalse(_motor.InFrictionWindow);
        float deltaTime = rate / ActionSequencePlayer.FramesPerSecond;
        ActionInputButtons held = ActionInputButtons.Forward;
        if (attack) held |= ActionInputButtons.Attack;
        // 按玩家时间覆盖完整收招与加速；慢速下相同 tick 数不足以结束攻击前摇。
        int ticks = Mathf.CeilToInt(4 * ActionSequencePlayer.FramesPerSecond / rate);
        for (int i = 0; i < ticks; i++)
        {
            ActionInputEdge[] edges = attack && i == 0
                ? new[] { new ActionInputEdge(ActionInputButtons.Attack, ActionInputStep.Edge.Pressed, held, Vector2.up) } : null;
            _runner.SimulateTick(new ActionInputSample(_tick++, held, Vector2.up, edges),
                new PlayerInputFrame { Move = Vector2.up }, deltaTime);
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
        _attack.Combat.ParryVolumes.Clear();
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
        Physics.SyncTransforms();
        commandMotor.Simulate(default, 1f / 60f, TimeManager.UnscaledTime);
        commandMotor.Simulate(default, 1f / 60f, TimeManager.UnscaledTime);
        Assert.IsTrue(commandMotor.IsGrounded);
        PlayerInputFrame input = new PlayerInputFrame { Move = Vector2.up, SprintHeld = true, JumpPressed = true, JumpTime = TimeManager.UnscaledTime };
        _motor.Simulate(input, 1f / 60f, TimeManager.UnscaledTime);
        input.JumpPressed = false;
        Assert.IsTrue(commandMotor.TryExecute(MotorCommandKind.Jump, 0f));
        commandMotor.Simulate(input, 1f / 60f, TimeManager.UnscaledTime);
        Assert.That(Vector3.Distance(_motor.Velocity, commandMotor.Velocity), Is.LessThan(0.0001f),
            "原始输入=" + _motor.Velocity + "/" + _motor.State + "/跳数" + _motor.JumpCount +
            "，命令=" + commandMotor.Velocity + "/" + commandMotor.State + "/跳数" + commandMotor.JumpCount);
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

    [Test]
    public void WallDashSetupPreservesManualStatesAndActionBindingsAcrossRepeatedInstall()
    {
        _folder = "Assets/__WallDashTests_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", _folder.Substring(7));
        string path = _folder + "/Controller.controller";
        Assert.IsTrue(AssetDatabase.CopyAsset(ActionPlayerSetup.ControllerPath, path));
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        AnimatorStateMachine root = controller.layers[0].stateMachine;
        AnimatorState manual = root.AddState("ManualState");
        Motion manualMotion = root.defaultState.motion;
        manual.motion = manualMotion;
        foreach (ChildAnimatorState child in root.states)
            if (child.state.name == "WallDashLeft" || child.state.name == "WallDashRight") root.RemoveState(child.state);
        PlayerLocomotionAnimatorSetup.EnsureWallDash(controller);
        int stateCount = ActionAnimatorAuthoring.GetStates(controller).Count;
        int transitionCount = root.anyStateTransitions.Length;
        PlayerLocomotionAnimatorSetup.EnsureWallDash(controller);
        ActionPlayerSetup.EnsureAnimator(controller, _catalog);
        Assert.IsTrue(PlayerLocomotionAnimatorSetup.HasWallDash(controller));
        Assert.AreEqual(stateCount, ActionAnimatorAuthoring.GetStates(controller).Count);
        Assert.AreEqual(transitionCount, root.anyStateTransitions.Length);
        Assert.AreSame(manualMotion, manual.motion);
        Assert.IsEmpty(ActionAnimatorAuthoring.Validate(_catalog, controller));
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        Assert.IsTrue(PlayerLocomotionAnimatorSetup.HasWallDash(AssetDatabase.LoadAssetAtPath<AnimatorController>(path)));
    }
    private TimeStopController StartGlobalTimeStop(ActionTestEnergy energy = null)
    {
        var clock = new GameObject("GlobalTimeStopIntegrationClock"); _created.Add(clock);
        Invoke(clock.AddComponent<TimeManager>(), "Awake");
        var parameters = ScriptableObject.CreateInstance<EnergyParams>(); _created.Add(parameters);
        parameters.TimeStopCostPerTick = energy == null ? 0f : 1f;
        var controller = _root.GetComponent<TimeStopController>();
        Assert.NotNull(controller, "玩家预制体必须落盘独立时停组件");
        Invoke(controller, "OnEnable");
        controller.Configure(parameters, _root.GetComponent<PlayerInputReader>(), energy ?? new ActionTestEnergy { Balance = 100f });
        controller.BeginTimeStop();
        Assert.IsTrue(controller.IsTimeStopping);
        return controller;
    }

    [Test]
    public void GlobalTimeStopKeepsActionMotorAndManualAnimationOnOneClock()
    {
        var energy = new ActionTestEnergy { Balance = 100f };
        TimeStopController timeStop = StartGlobalTimeStop(energy);
        ActionInputButtons held = ActionInputButtons.Attack | ActionInputButtons.Skill;
        var edge = new ActionInputEdge(ActionInputButtons.Attack, ActionInputStep.Edge.Pressed, held, default);
        _runner.SimulateTick(new ActionInputSample(_tick++, held, default, new[] { edge }), default, .2f / 60f);
        Assert.AreSame(_attack, _runner.Player.State.Action);
        Assert.That(_runner.Player.State.FrameProgress, Is.EqualTo(.2d).Within(.000001));
        Assert.That(_runner.LastSimulatedSeconds, Is.EqualTo(.2f / 60f).Within(.000001));
        Assert.AreEqual(1f, _bridge.Animator.speed);
        Assert.IsFalse(_bridge.Animator.enabled);
        Assert.That(energy.Balance, Is.EqualTo(99.8f).Within(.0001f));
        for (int i = 0; i < 10; i++) _runner.SimulateTick(new ActionInputSample(_tick++, held), default, .2f / 60f);
        Assert.That(_runner.Player.State.FrameProgress, Is.EqualTo(2.2d).Within(.000001));
        Assert.That(_bridge.Animator.GetFloat(ActionAnimatorBridge.TimeParameter),
            Is.EqualTo(_runner.Player.State.AnimationNormalizedTime).Within(.000001));
        long instance = _runner.Player.State.InstanceId;
        timeStop.EndTimeStop();
        _runner.SimulateTick(new ActionInputSample(_tick++), default, 1f / 60f);
        Assert.AreEqual(instance, _runner.Player.State.InstanceId);
        Assert.That(_runner.Player.State.FrameProgress, Is.EqualTo(3.2d).Within(.000001));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ActualComboIntentWinsDuringTimeStopEvenWhenResourcesAreInsufficient(bool affordable)
    {
        StartGlobalTimeStop();
        var combo = ScriptableObject.CreateInstance<ActionDefinition>(); _created.Add(combo);
        combo.ActionId = "time_stop_combo_test";
        combo.EnergyCost = affordable ? 0f : 10000f;
        combo.Input.Priority = -100;
        combo.Input.PreInputFrames = 8;
        combo.Input.Steps.Add(new ActionInputStep { Button = ActionInputButtons.Attack, RequireHeld = ActionInputButtons.Skill });
        combo.Timeline.Add(new ActionSegment { DurationFrames = 10 });
        _catalog.Actions.Add(combo);
        ActionInputButtons held = ActionInputButtons.Attack | ActionInputButtons.Skill;
        var edge = new ActionInputEdge(ActionInputButtons.Attack, ActionInputStep.Edge.Pressed, held, default);
        _runner.SimulateTick(new ActionInputSample(_tick++, held, default, new[] { edge }), default, .2f / 60f);
        if (affordable) Assert.AreSame(combo, _runner.Player.State.Action);
        else { Assert.IsFalse(_runner.Player.IsRunning); Assert.AreEqual(1, _runner.Player.PendingCount); }
    }

    [Test]
    public void GlobalTimeStopPreservesSuccessfulHitDedupAndClearsHitboxOnInterrupt()
    {
        StartGlobalTimeStop();
        ShortAttack();
        Enemy target = Target(true);
        Assert.IsTrue(_runner.Player.Queue(_attack, _tick));
        for (int i = 0; i < 11; i++)
            _runner.SimulateTick(new ActionInputSample(_tick++), default, .2f / 60f);
        Assert.AreEqual(1, target.DamagedCount);
        Assert.IsFalse(_combat.Hitbox.SequenceWindowOpen);
        _runner.Interrupt();
        Assert.IsFalse(_runner.Player.IsRunning);
        Assert.IsFalse(_combat.Hitbox.SequenceWindowOpen);
    }

    [Test]
    public void TeleportReleasesTimeStopAndPendingActionInput()
    {
        StartGlobalTimeStop();
        Assert.IsTrue(_runner.Player.Queue(_attack, _tick));
        _motor.Teleport(_root.transform.position + Vector3.right);
        Assert.AreEqual(1f, TimeManager.GlobalRate);
        Assert.AreEqual(1f, TimeManager.PlayerRate);
        Assert.AreEqual(0, _runner.Player.PendingCount);
    }

    [Test]
    public void GlobalTimeStopKeepsHighspeedABCancelOnAuthoredActionFrames()
    {
        StartGlobalTimeStop();
        ActionDefinition a = _catalog.Actions.Find(action => action.ActionId == "player_highspeed_a");
        ActionDefinition b = _catalog.Actions.Find(action => action.ActionId == "player_highspeed_b");
        _attack.RequestVariants.AddRange(new[] { a, b, _attack });
        _motor.SetHorizontalSpeed(20f); _motor.LaunchVertical(8f);
        ActionInputButtons held = ActionInputButtons.Attack | ActionInputButtons.Skill;
        var press = new ActionInputEdge(ActionInputButtons.Attack, ActionInputStep.Edge.Pressed, held, default);
        _runner.SimulateTick(new ActionInputSample(_tick++, held, default, new[] { press }), default, .2f / 60f);
        for (int i = 1; i < 150; i++)
            _runner.SimulateTick(new ActionInputSample(_tick++, ActionInputButtons.Skill), default, .2f / 60f);
        Assert.AreSame(a, _runner.Player.State.Action);
        Assert.That(_runner.Player.State.FrameProgress, Is.EqualTo(30d).Within(.00001));
        long instance = _runner.Player.State.InstanceId;
        float speed = _motor.HorizontalSpeed;
        _runner.SimulateTick(new ActionInputSample(_tick++, held, default, new[] { press }), default, .2f / 60f);
        Assert.AreSame(b, _runner.Player.State.Action);
        Assert.Greater(_runner.Player.State.InstanceId, instance);
        Assert.That(_runner.Player.State.FrameProgress, Is.EqualTo(.2d).Within(.00001));
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(speed).Within(.001f));
        Assert.AreEqual(1f, _bridge.Animator.speed);
        Assert.IsFalse(_bridge.Animator.applyRootMotion);
    }

    private ActionDefinition MobilityAction(string id)
    {
        IsolateMobilityCatalog();
        int index = _catalog.Actions.FindIndex(action => action.ActionId == id);
        Assert.GreaterOrEqual(index, 0, "先装配高跳与冲刺配表");
        return _catalog.Actions[index];
    }
    private void IsolateMobilityCatalog()
    {
        if (_mobilityCatalogIsolated) return;
        var actions = new Dictionary<string, ActionDefinition>();
        for (int i = 0; i < _catalog.Actions.Count; i++)
        {
            ActionDefinition clone = i == 0 ? _attack : Object.Instantiate(_catalog.Actions[i]);
            if (i != 0) _created.Add(clone);
            _catalog.Actions[i] = clone; actions[clone.ActionId] = clone;
        }
        foreach (ActionDefinition source in _catalog.Actions)
        {
            foreach (ActionCancelWindow window in source.CancelWindows)
                for (int i = 0; i < window.Targets.Count; i++)
                    if (actions.TryGetValue(window.Targets[i].ActionId, out ActionDefinition target)) window.Targets[i] = target;
            for (int i = 0; i < source.RequestVariants.Count; i++)
                if (actions.TryGetValue(source.RequestVariants[i].ActionId, out ActionDefinition variant)) source.RequestVariants[i] = variant;
        }
        _mobilityCatalogIsolated = true;
    }
    private ActionTestEnergy MobilityEnergy(float balance = 200f)
    {
        IsolateMobilityCatalog();
        var energy = new ActionTestEnergy { Balance = balance };
        _runner.SetEnergyAccount(energy);
        return energy;
    }
    private void SkillTick(ActionInputButtons trigger, ActionInputButtons held = ActionInputButtons.Skill | ActionInputButtons.Jump,
        Vector2 direction = default, float frames = 1f, bool hitStop = false)
    {
        var edge = new ActionInputEdge(trigger, ActionInputStep.Edge.Pressed, held, direction);
        _runner.SimulateTick(new ActionInputSample(_tick++, held, direction, new[] { edge }),
            new PlayerInputFrame { Move = direction, JumpPressed = trigger == ActionInputButtons.Jump, JumpTime = TimeManager.UnscaledTime },
            frames / 60f, hitStop);
    }

    [Test]
    public void HighJumpHasFourTimesHeightAndNextOrdinaryJumpReturnsToBaseHeight()
    {
        Ground(); MobilityEnergy();
        float origin = _root.transform.position.y;
        SkillTick(ActionInputButtons.Jump);
        float highPeak = _root.transform.position.y;
        for (int i = 0; i < 180 && !_motor.IsGrounded; i++) { Tick(); highPeak = Mathf.Max(highPeak, _root.transform.position.y); }
        Assert.IsTrue(_motor.IsGrounded);
        Tick(ActionInputButtons.Jump);
        float ordinaryPeak = _root.transform.position.y;
        for (int i = 0; i < 120 && !_motor.IsGrounded; i++) { Tick(); ordinaryPeak = Mathf.Max(ordinaryPeak, _root.transform.position.y); }
        Assert.IsTrue(_motor.IsGrounded);
        Assert.That((highPeak - origin) / (ordinaryPeak - origin), Is.EqualTo(4f).Within(.15f));
        Assert.That(ordinaryPeak - origin, Is.EqualTo(_motor.Params.JumpSpeed * _motor.Params.JumpSpeed / (2f * _motor.Params.Gravity)).Within(.15f));
    }

    [TestCase(0f)]
    [TestCase(19f)]
    public void UnaffordableHighJumpDoesNotFallBackToOrdinaryJump(float balance)
    {
        Ground(); ActionTestEnergy energy = MobilityEnergy(balance);
        SkillTick(ActionInputButtons.Jump);
        for (int i = 0; i < 10; i++) Tick();
        Assert.AreEqual(0, _motor.JumpCount); Assert.AreEqual(balance, energy.Balance);
    }

    [Test]
    public void HighJumpCannotRelaunchInAirOrSpendEnergyForRejectedRequest()
    {
        Ground(); ActionTestEnergy energy = MobilityEnergy(100f);
        SkillTick(ActionInputButtons.Jump);
        for (int i = 0; i < 10; i++) SkillTick(ActionInputButtons.Jump);
        Assert.AreEqual(1, _motor.JumpCount); Assert.AreEqual(80f, energy.Balance);
        Assert.IsFalse(_motor.TryExecute(MotorCommandKind.HighJump, 4f));
    }

    [Test]
    public void AirborneHighJumpPreinputExecutesOnlyAfterLanding()
    {
        Ground(); ActionTestEnergy energy = MobilityEnergy(100f);
        Tick(ActionInputButtons.Jump);
        int safety = 0;
        while ((_motor.Velocity.y >= 0f || _root.transform.position.y > .3f) && safety++ < 100) Tick();
        Assert.IsFalse(_motor.IsGrounded);
        SkillTick(ActionInputButtons.Jump);
        Assert.AreEqual(1, _motor.JumpCount); Assert.AreEqual(100f, energy.Balance);
        for (int i = 0; i < 8 && _motor.JumpCount < 2; i++) Tick();
        Assert.AreEqual(2, _motor.JumpCount); Assert.AreEqual(80f, energy.Balance);
        Assert.Greater(_motor.Velocity.y, _motor.Params.JumpSpeed);
    }

    [Test]
    public void HighJumpLocksLowAttackUntilSixActorFramesButKeepsMoving()
    {
        Ground(); MobilityEnergy(); _motor.SetHorizontalSpeed(5f);
        SkillTick(ActionInputButtons.Jump);
        Vector3 position = _root.transform.position;
        Tick(ActionInputButtons.Attack);
        while (_runner.Player.State.FrameProgress < 5d)
        {
            Assert.AreEqual("player_high_jump", _runner.Player.State.Action.ActionId); Tick();
        }
        Assert.AreEqual("player_high_jump", _runner.Player.State.Action.ActionId);
        Assert.Greater(_root.transform.position.y, position.y);
        Assert.Greater(_root.transform.position.z, position.z);
        Tick(); Assert.AreSame(_attack, _runner.Player.State.Action);
    }

    [Test]
    public void HighJumpCannotCancelLowAttackStartupAndUsesExistingRecoveryWindow()
    {
        Ground(); MobilityEnergy(); ShortAttack(); _attack.Timeline[0].DurationFrames = 4;
        Tick(ActionInputButtons.Attack);
        SkillTick(ActionInputButtons.Jump, direction: Vector2.up);
        Assert.AreSame(_attack, _runner.Player.State.Action); Assert.AreEqual(0, _motor.JumpCount);
        Tick(); Tick(); Assert.AreSame(_attack, _runner.Player.State.Action);
        Tick(); Tick();
        Assert.AreEqual("player_high_jump", _runner.Player.State.Action.ActionId); Assert.AreEqual(1, _motor.JumpCount);
    }

    [Test]
    public void HighJumpCannotStandThroughLowCeilingOrSpendItsCost()
    {
        Ground(); ActionTestEnergy energy = MobilityEnergy(100f);
        _motor.SetHorizontalSpeed(_motor.Params.GroundSpeedThreshold);
        Assert.IsTrue(_motor.TryExecute(MotorCommandKind.EnterSlide, 0f));
        var ceiling = new GameObject("MobilityLowCeiling"); _created.Add(ceiling);
        ceiling.transform.position = _root.transform.position + Vector3.up * 1.4f;
        ceiling.AddComponent<BoxCollider>().size = new Vector3(10f, .4f, 10f); Physics.SyncTransforms();
        SkillTick(ActionInputButtons.Jump, direction: Vector2.up);
        Assert.AreEqual(0, _motor.JumpCount); Assert.AreEqual(100f, energy.Balance); Assert.IsTrue(_motor.IsSliding);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void JumpUpCompletionUsesConfiguredHoldPoseOrDefaultAirborneState(bool holdPose)
    {
        Ground(); MobilityEnergy();
        MobilityAction("player_high_jump").AirborneAnimation.HoldJumpUpPose = holdPose;
        SkillTick(ActionInputButtons.Jump);
        for (int i = 0; i < 51; i++) Tick();
        Assert.IsFalse(_runner.Player.IsRunning); Assert.IsFalse(_motor.IsGrounded);
        Assert.AreEqual(holdPose, _bridge.IsHoldingAirbornePose);
        Transform hips = _bridge.Animator.transform.Find("mixamorig:Hips"); Assert.NotNull(hips);
        for (int i = 0; i < 5; i++) Tick();
        Vector3 pose = hips.localPosition; Quaternion rotation = hips.localRotation;
        for (int i = 0; i < 8; i++) Tick();
        if (holdPose)
        {
            Assert.That(Vector3.Distance(pose, hips.localPosition), Is.LessThan(.001f));
            Assert.That(Quaternion.Angle(rotation, hips.localRotation), Is.LessThan(.1f));
        }
        else
        {
            Assert.IsFalse(_bridge.Animator.GetBool(ActionAnimatorBridge.PlayingParameter));
            Assert.IsTrue(_bridge.Animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Jump"));
        }
        for (int i = 0; i < 120 && !_motor.IsGrounded; i++) Tick();
        Assert.IsTrue(_motor.IsGrounded); Assert.IsFalse(_bridge.IsHoldingAirbornePose);
    }

    [TestCase(ActionInputButtons.Skill)]
    [TestCase(ActionInputButtons.Sprint)]
    public void DashAcceptsEitherChordOrderAndFiresOnlyOnceWhileHeld(ActionInputButtons trigger)
    {
        Ground(); ActionTestEnergy energy = MobilityEnergy(100f);
        SkillTick(trigger, ActionInputButtons.Skill | ActionInputButtons.Sprint, Vector2.up);
        Assert.AreEqual("player_dash", _runner.Player.State.Action.ActionId);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(_motor.Params.GroundSpeedThreshold * 4f).Within(.001f));
        long instance = _runner.Player.State.InstanceId;
        for (int i = 0; i < 8; i++) _runner.SimulateTick(new ActionInputSample(_tick++, ActionInputButtons.Skill | ActionInputButtons.Sprint, Vector2.up),
            new PlayerInputFrame { Move = Vector2.up }, 1f / 60f);
        Assert.AreEqual(instance, _runner.Player.State.InstanceId); Assert.AreEqual(50f, energy.Balance);
    }

    [TestCase(25f, 40f)]
    [TestCase(55f, 55f)]
    public void AirDashBoostsToFourTimesThresholdAndPreservesHigherMomentumAndVerticalVelocity(float initialSpeed, float expectedSpeed)
    {
        MobilityEnergy(); _motor.SetHorizontalSpeed(initialSpeed); _motor.LaunchVertical(8f);
        SkillTick(ActionInputButtons.Skill, ActionInputButtons.Skill | ActionInputButtons.Sprint, Vector2.up);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(expectedSpeed).Within(.001f));
        Assert.That(_motor.Velocity.y, Is.EqualTo(8f - _motor.Params.Gravity / 60f).Within(.001f));
    }

    [Test]
    public void DashUsesAuthoredSpeedMultiplierAndCurrentGroundThreshold()
    {
        Ground(); MobilityEnergy();
        MovementParams parameters = Object.Instantiate(_motor.Params); _created.Add(parameters);
        parameters.GroundSpeedThreshold = 13f; _motor.SetParams(parameters);
        MobilityAction("player_dash").Timeline[0].Events[0].Value = 2.5f;
        SkillTick(ActionInputButtons.Skill, ActionInputButtons.Skill | ActionInputButtons.Sprint, Vector2.up);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(32.5f).Within(.001f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SimultaneousDashAndHighJumpChooseOnlyHighJumpRegardlessOfEdgeOrder(bool jumpFirst)
    {
        Ground(); ActionTestEnergy energy = MobilityEnergy(100f);
        ActionInputButtons held = ActionInputButtons.Skill | ActionInputButtons.Sprint | ActionInputButtons.Jump;
        var dash = new ActionInputEdge(ActionInputButtons.Skill, ActionInputStep.Edge.Pressed, held, Vector2.up);
        var jump = new ActionInputEdge(ActionInputButtons.Jump, ActionInputStep.Edge.Pressed, held, Vector2.up);
        _runner.SimulateTick(new ActionInputSample(_tick++, held, Vector2.up, jumpFirst ? new[] { jump, dash } : new[] { dash, jump }),
            new PlayerInputFrame { Move = Vector2.up, JumpPressed = true }, 1f / 60f);
        Assert.AreEqual("player_high_jump", _runner.Player.State.Action.ActionId); Assert.AreEqual(80f, energy.Balance);
        for (int i = 0; i < 10; i++) Tick(); Assert.AreEqual(80f, energy.Balance);
    }

    [Test]
    public void MobilityCostReadsEnergyParamsAtCommitAndSuccessfulSkillEndsTimeStop()
    {
        Ground(); ActionDefinition high = MobilityAction("player_high_jump");
        EnergyParams parameters = Object.Instantiate(high.EnergyParameters); _created.Add(parameters);
        parameters.HighJumpCost = 35f; high.EnergyParameters = parameters;
        ActionTestEnergy energy = MobilityEnergy(100f);
        TimeStopController slow = StartGlobalTimeStop(energy);
        SkillTick(ActionInputButtons.Jump);
        Assert.IsFalse(slow.IsTimeStopping); Assert.AreEqual(65f, energy.Balance);
    }

    [Test]
    public void DashGlobalTimeSourceComposesWithHitStopAndReleasesOnReset()
    {
        Ground(); MobilityEnergy(); TimeManager.ClearSlowMotion();
        SkillTick(ActionInputButtons.Skill, ActionInputButtons.Skill | ActionInputButtons.Sprint, Vector2.up);
        Assert.That(TimeManager.GlobalRate, Is.EqualTo(.8f));
        TimeManager.HitStop(1f, .05f);
        Assert.That(TimeManager.PlayerRate, Is.EqualTo(.05f));
        _runner.ResetActions(); Assert.That(TimeManager.GlobalRate, Is.EqualTo(1f));
        Assert.That(TimeManager.PlayerRate, Is.EqualTo(.05f)); TimeManager.ClearSlowMotion();
    }

    [Test]
    public void MobilityInstallIsIdempotentAndKeepsAuthoredHeightAndRecoveryFrames()
    {
        _folder = "Assets/__MobilityTests_" + Guid.NewGuid().ToString("N");
        AnimationClip jumpUp = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/T_Jump.anim");
        AnimationClip dash = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animations/Player/T_Dash.anim");
        EnergyParams energy = AssetDatabase.LoadAssetAtPath<EnergyParams>("Assets/Settings/EnergyParams.asset");
        IsolateMobilityCatalog();
        _catalog.Actions.RemoveAll(action => action.ActionId == "player_high_jump" || action.ActionId == "player_dash" || action.ActionId == "player_sprint");
        foreach (ActionDefinition action in _catalog.Actions)
            foreach (ActionCancelWindow window in action.CancelWindows)
                window.Targets.RemoveAll(target => target.ActionId == "player_high_jump" || target.ActionId == "player_dash" || target.ActionId == "player_sprint");
        PlayerMobilitySetup.EnsureActions(_catalog, energy, jumpUp, dash, _folder);
        ActionDefinition high = _catalog.Actions.Find(action => action.ActionId == "player_high_jump" && AssetDatabase.GetAssetPath(action).StartsWith(_folder));
        ActionDefinition sprint = _catalog.Actions.Find(action => action.ActionId == "player_dash" && AssetDatabase.GetAssetPath(action).StartsWith(_folder));
        ActionDefinition ordinary = _catalog.Actions.Find(action => action.ActionId == "player_sprint" && AssetDatabase.GetAssetPath(action).StartsWith(_folder));
        Assert.NotNull(high);
        Assert.NotNull(sprint); Assert.AreEqual(4f, sprint.Timeline[0].Events[0].Value);
        Assert.NotNull(ordinary); Assert.AreEqual(2f, ordinary.Timeline[0].Events[0].Value);
        high.Timeline[0].Events[0].Value = 5f;
        sprint.Timeline[0].Events[0].Value = 3f;
        high.Timeline[0].DurationFrames = 8;
        ordinary.Timeline[0].Events[0].Value = 1.5f;
        ordinary.CooldownFrames = 17;
        int count = _catalog.Actions.Count;
        PlayerMobilitySetup.EnsureActions(_catalog, energy, jumpUp, dash, _folder);
        Assert.AreEqual(count, _catalog.Actions.Count);
        Assert.AreEqual(5f, high.Timeline[0].Events[0].Value); Assert.AreEqual(8, high.Timeline[0].DurationFrames);
        Assert.AreEqual(3f, sprint.Timeline[0].Events[0].Value);
        Assert.AreEqual(1.5f, ordinary.Timeline[0].Events[0].Value); Assert.AreEqual(17, ordinary.CooldownFrames);
        Assert.IsTrue(high.TryResolve(high.LandingCompletionStart, out int landingFrame)); Assert.AreEqual(8, landingFrame);
    }

    [Test]
    public void GroundAnimationStopsOnForwardReleaseWhileMomentumStillMovesThePlayer()
    {
        Ground();
        Tick(ActionInputButtons.Forward, Vector2.up);
        PlayerAnimation animation = _root.GetComponentInChildren<PlayerAnimation>();
        Assert.AreEqual(PlayerAnimation.MotionStateMove, animation.CurrentMotionState);
        Vector3 before = _root.transform.position;
        Tick();
        Assert.IsFalse(_motor.HasForwardInput);
        Assert.Greater(_motor.HorizontalSpeed, 0f);
        Assert.Greater(Vector3.Distance(before, _root.transform.position), 0f);
        Assert.AreEqual(PlayerAnimation.MotionStateIdle, animation.CurrentMotionState);
        Tick(ActionInputButtons.Right, Vector2.right);
        Assert.AreEqual(PlayerAnimation.MotionStateWalk, animation.CurrentMotionState);
    }

    [Test]
    public void ForwardBhopKeepsMomentumAndWindowPumpWithoutShift()
    {
        Ground();
        _motor.SetHorizontalSpeed(20f);
        Tick(ActionInputButtons.Jump, Vector2.up);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(20f + 10f * 10f / 60f).Within(.001f));
        float firstJumpSpeed = _motor.HorizontalSpeed;
        Assert.IsFalse(_motor.IsGrounded);
        for (int i = 0; i < 90 && !_motor.IsGrounded; i++) Tick(ActionInputButtons.None, Vector2.up);
        Assert.IsTrue(_motor.IsGrounded);
        Assert.IsTrue(_motor.InFrictionWindow);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(firstJumpSpeed).Within(.001f));
        Tick(ActionInputButtons.Jump, Vector2.up);
        Assert.AreEqual(2, _motor.JumpCount);
        Assert.Greater(_motor.HorizontalSpeed, firstJumpSpeed);
    }

    [TestCase(0f)]
    [TestCase(25f)]
    [TestCase(119f)]
    public void OrdinarySprintAddsTwiceLowThresholdOnceWithoutEnergyOrVerticalImpulse(float initialSpeed)
    {
        ActionTestEnergy energy = MobilityEnergy(0f);
        _motor.SetHorizontalSpeed(initialSpeed); _motor.LaunchVertical(8f);
        float rate = TimeManager.GlobalRate;
        int commands = 0;
        _runner.FrameEvent += (state, frameEvent) => { if (frameEvent.MotorCommand == MotorCommandKind.SprintBoost) commands++; };
        SkillTick(ActionInputButtons.Sprint, ActionInputButtons.Sprint);
        Assert.AreEqual("player_sprint", _runner.Player.State.Action.ActionId);
        float expected = Mathf.Min(initialSpeed + 2f * _motor.Params.LowSpeedThreshold, _motor.Params.MaxSpeed);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(expected).Within(.001f));
        Assert.That(_motor.Velocity.y, Is.EqualTo(8f - 20f / 60f).Within(.001f));
        Assert.AreEqual(0f, energy.Balance); Assert.AreEqual(rate, TimeManager.GlobalRate);
        for (int i = 0; i < 34; i++) _runner.SimulateTick(new ActionInputSample(_tick++, ActionInputButtons.Sprint), default, 1f / 60f);
        Assert.AreEqual(1, commands);
        Assert.IsFalse(_runner.Player.IsRunning);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(expected).Within(.001f));
    }

    [Test]
    public void OrdinarySprintAndCombatShareCurrentLowSpeedThresholdAndAuthoredMultiplier()
    {
        MobilityEnergy();
        MovementParams parameters = Object.Instantiate(_motor.Params); _created.Add(parameters);
        parameters.GroundSpeedThreshold = 13f; parameters.LowSpeedRatio = .6f; _motor.SetParams(parameters);
        MobilityAction("player_sprint").Timeline[0].Events[0].Value = 2.5f;
        _motor.SetHorizontalSpeed(parameters.LowSpeedThreshold);
        Assert.IsTrue(_runner.CheckCondition("speed_low", default, default));
        Assert.IsFalse(_runner.CheckCondition("speed_high", default, default));
        SkillTick(ActionInputButtons.Sprint, ActionInputButtons.Sprint);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(7.8f + 19.5f).Within(.001f));
        Assert.IsTrue(_runner.CheckCondition("speed_high", default, default));
    }

    [TestCase(false, 0f)]
    [TestCase(true, 0f)]
    [TestCase(false, 100f)]
    [TestCase(true, 100f)]
    public void SameTickShiftAndSkillNeverAlsoGiveFreeSprintRegardlessOfEventOrder(bool shiftFirst, float balance)
    {
        Ground(); ActionTestEnergy energy = MobilityEnergy(balance);
        ActionInputButtons held = ActionInputButtons.Sprint | ActionInputButtons.Skill;
        var shift = new ActionInputEdge(ActionInputButtons.Sprint, ActionInputStep.Edge.Pressed, shiftFirst ? ActionInputButtons.Sprint : held, default);
        var skill = new ActionInputEdge(ActionInputButtons.Skill, ActionInputStep.Edge.Pressed, shiftFirst ? held : ActionInputButtons.Skill, default);
        int ordinary = 0;
        _runner.FrameEvent += (state, frameEvent) => { if (frameEvent.MotorCommand == MotorCommandKind.SprintBoost) ordinary++; };
        _runner.SimulateTick(new ActionInputSample(_tick++, held, default, shiftFirst ? new[] { shift, skill } : new[] { skill, shift }), default, 1f / 60f);
        Assert.AreEqual(0, ordinary);
        Assert.AreEqual(balance >= 50f, _runner.Player.IsRunning);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(balance >= 50f ? 40f : 0f).Within(.001f));
        Assert.AreEqual(balance >= 50f ? balance - 50f : balance, energy.Balance);
    }

    [Test]
    public void OrdinarySprintCanUpgradeIntoEnergyDashOnTheNextTick()
    {
        Ground(); ActionTestEnergy energy = MobilityEnergy(100f);
        SkillTick(ActionInputButtons.Sprint, ActionInputButtons.Sprint);
        Assert.AreEqual("player_sprint", _runner.Player.State.Action.ActionId);
        SkillTick(ActionInputButtons.Skill, ActionInputButtons.Sprint | ActionInputButtons.Skill);
        Assert.AreEqual("player_dash", _runner.Player.State.Action.ActionId);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(40f).Within(.001f));
        Assert.AreEqual(50f, energy.Balance);
    }

    [Test]
    public void OrdinarySprintCannotCancelLowAttackStartupButUsesItsRecoveryWindow()
    {
        Ground(); ShortAttack();
        Tick(ActionInputButtons.Attack, frames: .25f);
        SkillTick(ActionInputButtons.Sprint, ActionInputButtons.Sprint, Vector2.up, .25f);
        Assert.AreSame(_attack, _runner.Player.State.Action);
        Tick(frames: 2f);
        Assert.AreEqual("player_sprint", _runner.Player.State.Action.ActionId);
    }

    [Test]
    public void RunBindingMigrationPreservesActionStatesAndIsIdempotent()
    {
        _folder = "Assets/__RunBindingTests_" + Guid.NewGuid().ToString("N");
        AssetDatabase.CreateFolder("Assets", _folder.Substring(7));
        string path = _folder + "/Player.controller";
        Assert.IsTrue(AssetDatabase.CopyAsset(ActionPlayerSetup.ControllerPath, path));
        AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        int states = controller.layers[0].stateMachine.stateMachines[0].stateMachine.states.Length;
        PlayerLocomotionAnimatorSetup.EnsureRun(controller);
        PlayerLocomotionAnimatorSetup.EnsureRun(controller);
        Assert.IsTrue(PlayerLocomotionAnimatorSetup.HasRun(controller));
        Assert.AreEqual(states, controller.layers[0].stateMachine.stateMachines[0].stateMachine.states.Length);
        Assert.IsTrue(PlayerLocomotionAnimatorSetup.HasWallDash(controller));
    }
    [Test]
    public void ParryCancelsRealRunnerBeforeDamageAndBuffersFreshStopInput()
    {
        var clock = new GameObject("ParryRunnerClock"); _created.Add(clock); Invoke(clock.AddComponent<TimeManager>(), "Awake");
        Ground(); ShortAttack();
        _attack.Combat.Damage = 37f; _attack.Combat.ScaleDamageWithSpeed = false;
        Enemy enemy = Target(); enemy.GetComponent<BoxCollider>().size = Vector3.one * .2f;
        enemy.transform.position = _root.transform.position + Vector3.forward * 1.5f;
        CombatComponent source = enemy.GetComponent<CombatComponent>();
        Assert.IsTrue(source.TryBeginAttack(TimeManager.WorldTime));
        int hits = 0, parries = 0;
        _combat.SequenceHitLanded += (target, request, result) => hits++;
        _combat.ParrySucceeded += contacts => parries++;
        Tick(ActionInputButtons.Attack);
        Assert.AreEqual(1, parries); Assert.AreEqual(0, hits);
        Assert.AreEqual(enemy.MaxHealth - 37f, enemy.Health);
        Assert.AreEqual(1, enemy.DamagedCount);
        Assert.IsFalse(_runner.Player.IsRunning); Assert.IsFalse(source.IsAttacking(TimeManager.WorldTime));
        Assert.IsFalse(_combat.Hitbox.SequenceWindowOpen);
        float pose = _bridge.Animator.GetFloat("ActionTime");
        Tick(ActionInputButtons.Attack, frames: 0f);
        Assert.AreEqual(pose, _bridge.Animator.GetFloat("ActionTime"));
        Assert.IsFalse(_runner.Player.IsRunning); Assert.AreEqual(1, _runner.Player.PendingCount);
        TimeManager.ClearSlowMotion(); Tick();
        Assert.IsTrue(_runner.Player.IsRunning, "时停期间的新输入恢复后应正常提交");
        Assert.AreEqual(1, parries);
    }

    [Test]
    public void DisadvantageParryClearsMomentumAndAddsConfiguredBackwardImpulse()
    {
        var clock = new GameObject("ParryKnockbackClock"); _created.Add(clock); Invoke(clock.AddComponent<TimeManager>(), "Awake");
        Ground(); ShortAttack(); _motor.Params.GroundFriction = 0f; _motor.SetHorizontalSpeed(5f);
        Enemy enemy = Target(); enemy.GetComponent<BoxCollider>().size = Vector3.one * .2f;
        enemy.transform.position = _root.transform.position + Vector3.forward * 1.5f;
        CombatComponent source = enemy.GetComponent<CombatComponent>(); source.ParryKind = AttackParryKind.Disadvantage;
        Assert.IsTrue(source.TryBeginAttack(TimeManager.WorldTime)); Tick(ActionInputButtons.Attack);
        Assert.IsFalse(_runner.Player.IsRunning);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(3f).Within(.001f));
        Assert.Less(Vector3.Dot(_motor.Velocity, _root.transform.forward), 0f);
    }

}
