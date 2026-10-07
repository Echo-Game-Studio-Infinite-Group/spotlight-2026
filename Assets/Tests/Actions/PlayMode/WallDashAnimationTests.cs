#if UNITY_EDITOR
using System.Collections.Generic;
using GameJam.Actions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class WallDashAnimationTests
{
    private readonly List<Object> _created = new List<Object>();
    private GameObject _root;
    private PlayerActionRunner _runner;
    private PlayerMotor _motor;
    private ActionAnimatorBridge _bridge;
    private ActionCatalog _catalog;
    private ActionDefinition _attack;
    private long _tick;

    [SetUp]
    public void SetUp()
    {
        TimeManager.ClearSlowMotion();
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        Assert.NotNull(prefab);
        _root = Object.Instantiate(prefab); _created.Add(_root);
        foreach (Camera camera in _root.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        _motor = _root.GetComponent<PlayerMotor>();
        _bridge = _root.GetComponent<ActionAnimatorBridge>();
        _runner = _root.GetComponent<PlayerActionRunner>();
        // 实例的 OnEnable 已连接原动作表，必须先释放，测试副本才会被新的执行器采用。
        _runner.Disconnect();
        _catalog = Object.Instantiate(_runner.Catalog); _created.Add(_catalog);
        _attack = Object.Instantiate(_catalog.Actions[0]); _created.Add(_attack);
        _attack.RequestVariants.Clear();
        _attack.CooldownGroup = "";
        _catalog.Actions[0] = _attack;
        _runner.Configure(_catalog, _root.GetComponent<PlayerInputReader>(), _motor,
            _root.GetComponent<PlayerCombat>(), _bridge);
        Assert.IsTrue(_runner.Connect());
        _tick = 0;
    }

    [TearDown]
    public void TearDown()
    {
        _runner?.Disconnect();
        foreach (Object value in _created) if (value != null) Object.DestroyImmediate(value);
        _created.Clear();
        TimeManager.ClearSlowMotion();
    }

    private void Tick(ActionInputButtons button = ActionInputButtons.None, float frames = 1f)
    {
        ActionInputEdge[] edges = button == ActionInputButtons.None ? null :
            new[] { new ActionInputEdge(button, ActionInputStep.Edge.Pressed, button, Vector2.zero) };
        _runner.SimulateTick(new ActionInputSample(_tick++, button, Vector2.zero, edges),
            default, frames / ActionSequencePlayer.FramesPerSecond);
    }

    private void ShortAttack()
    {
        ActionAnimationBinding binding = _attack.Timeline[0].Animation;
        binding.BlendFrames = 0;
        _attack.Timeline = new List<ActionSegment>
        {
            new ActionSegment { SegmentId = "startup", DurationFrames = 1, Animation = binding },
            new ActionSegment { SegmentId = "active", Phase = ActionPhase.Active, DurationFrames = 1, Animation = binding },
            new ActionSegment { SegmentId = "recovery", Phase = ActionPhase.Recovery, DurationFrames = 2, Animation = binding }
        };
        _attack.CancelWindows.Clear();
    }

    private void Ground()
    {
        _motor.Params.Gravity = 20f;
        GameObject ground = new GameObject("WallDashGround"); _created.Add(ground);
        ground.transform.position = _root.transform.position + Vector3.down * 0.5f;
        ground.AddComponent<BoxCollider>().size = new Vector3(100f, 1f, 100f);
        Physics.SyncTransforms(); Tick(); Tick();
        Assert.IsTrue(_motor.IsGrounded);
    }

    private void EnterWallDash(float wallSide)
    {
        MovementParams parameters = Object.Instantiate(_motor.Params); _created.Add(parameters);
        parameters.Gravity = 0f;
        parameters.CapsuleBaseRadius = parameters.CapsuleMinRadius = 0.3f;
        parameters.CapsuleBaseHeight = parameters.CapsuleFastHeight = 2f;
        _motor.SetParams(parameters);
        _motor.Teleport(new Vector3(4000f, 20f, 4000f));
        _root.transform.rotation = Quaternion.Euler(0f, wallSide * 20f, 0f);
        GameObject reference = new GameObject("WallDashCameraReference"); _created.Add(reference);
        _motor.SetMovementReference(reference.transform);
        GameObject wall = new GameObject("WallDashWall"); _created.Add(wall);
        wall.transform.position = new Vector3(4000f + wallSide * 0.9f, 50f, 4000f);
        wall.AddComponent<BoxCollider>().size = new Vector3(1f, 200f, 200f);
        Physics.SyncTransforms();
        _motor.SetHorizontalSpeed(10f);
        for (int i = 0; i < 10 && !_motor.IsWallSliding; i++) _motor.Simulate(default, 1f / 60f, 0f);
        Assert.IsTrue(_motor.IsWallSliding, "必须通过真实墙面碰撞进入划墙");
        _bridge.Sample(default, 0.2f);
        _bridge.Sample(default, 0.2f);
    }

    [TestCase(-1f, "WallDashLeft")]
    [TestCase(1f, "WallDashRight")]
    public void WallDashLoopsOnPlayerClockWithoutModelDriftOrCameraSideFlips(float wallSide, string stateName)
    {
        EnterWallDash(wallSide);
        Animator animator = _bridge.Animator;
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer." + stateName));
        Assert.IsFalse(animator.enabled);
        Assert.IsFalse(animator.applyRootMotion);
        Transform model = animator.transform;
        Vector3 position = model.localPosition;
        Quaternion rotation = model.localRotation;
        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        Vector3 firstHips = model.InverseTransformPoint(hips.position);
        float startTime = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
        GameObject reference = _created.Find(value => value is GameObject go && go.name == "WallDashCameraReference") as GameObject;
        reference.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
        for (int i = 0; i < 20; i++)
        {
            _bridge.Sample(default, 0.1f);
            Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer." + stateName));
            Vector3 hipsOffset = model.InverseTransformPoint(hips.position) - firstHips;
            hipsOffset.y = 0f;
            Assert.Less(hipsOffset.magnitude, _motor.Params.CapsuleBaseHeight * 0.5f, "模型骨架不能沿根曲线跑出碰撞体");
        }
        Assert.Greater(animator.GetCurrentAnimatorStateInfo(0).normalizedTime, startTime + 2f);
        Assert.That(Vector3.Distance(position, model.localPosition), Is.LessThan(0.001f));
        Assert.That(Quaternion.Angle(rotation, model.localRotation), Is.LessThan(0.001f));
        float pausedTime = animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
        _bridge.Sample(default, 0f);
        Assert.That(animator.GetCurrentAnimatorStateInfo(0).normalizedTime, Is.EqualTo(pausedTime).Within(0.00001f));
    }

    [TestCase(-1f, "Base Layer.Actions.PlayerAttack")]
    [TestCase(1f, "Base Layer.Actions.PlayerAttack")]
    [TestCase(-1f, "Base Layer.Actions.HighspeedAttackA")]
    [TestCase(1f, "Base Layer.Actions.HighspeedAttackA")]
    [TestCase(-1f, "Base Layer.Actions.HighspeedAttackB")]
    [TestCase(1f, "Base Layer.Actions.HighspeedAttackB")]
    public void WallDashCannotStealAnimatedActionsAndReturnsToCurrentWallSide(float wallSide, string actionPath)
    {
        EnterWallDash(wallSide);
        ActionDefinition action = _catalog.Actions.Find(candidate => candidate.Timeline.Exists(segment => segment.Animation.AnimatorState == actionPath));
        Assert.NotNull(action);
        var state = new ActionExecutionState(action, 100, 0, 0, 0, default);
        _bridge.EnterSegment(state, true);
        for (int i = 0; i < 10; i++)
        {
            _bridge.Sample(state, 1f / 60f);
            Assert.IsTrue(_bridge.Animator.GetCurrentAnimatorStateInfo(0).IsName(actionPath));
            Assert.IsTrue(_bridge.Animator.GetBool(ActionAnimatorBridge.PlayingParameter));
        }
        _bridge.EndAction(100, false);
        _bridge.Sample(default, 0.2f);
        _bridge.Sample(default, 0.2f);
        Assert.IsFalse(_bridge.Animator.GetBool(ActionAnimatorBridge.PlayingParameter));
        Assert.IsTrue(_bridge.Animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer." + (wallSide < 0f ? "WallDashLeft" : "WallDashRight")));
    }

    [TestCase(-1f)]
    [TestCase(1f)]
    public void WallDashWallJumpExitsLoopAndKeepsFacingProtection(float wallSide)
    {
        EnterWallDash(wallSide);
        Quaternion facing = _root.transform.rotation;
        Assert.IsTrue(_motor.TryExecute(MotorCommandKind.Jump, 0f));
        _motor.Simulate(default, 1f / 60f, 0f);
        Assert.IsFalse(_motor.IsWallSliding);
        Assert.AreEqual(1, _motor.WallJumpCount);
        _motor.Simulate(new PlayerInputFrame { Move = Vector2.down }, 1f / 60f, 0f);
        Assert.That(Quaternion.Angle(facing, _root.transform.rotation), Is.LessThan(0.001f));
        _bridge.Sample(default, 0.2f);
        _bridge.Sample(default, 0.2f);
        Assert.IsTrue(_bridge.Animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Jump"));
    }

    [TestCase(-1f)]
    [TestCase(1f)]
    public void WallDashRunnerCompletesAttackAndReturnsToWallLoop(float wallSide)
    {
        EnterWallDash(wallSide);
        ShortAttack();
        _motor.SetHorizontalSpeed(_motor.Params.GroundSpeedThreshold * 0.5f);
        Tick(ActionInputButtons.Attack);
        Assert.IsTrue(_runner.Player.IsRunning, _runner.Player.LastRejection);
        Tick(frames: 3f);
        Assert.IsFalse(_runner.Player.IsRunning);
        Assert.IsTrue(_motor.IsWallSliding);
        _bridge.Sample(default, 0.2f);
        _bridge.Sample(default, 0.2f);
        Assert.IsTrue(_bridge.Animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer." + (wallSide < 0f ? "WallDashLeft" : "WallDashRight")));
    }

    [TestCase(false, "Jump")]
    [TestCase(true, "Idle")]
    public void WallDashActionReturnUsesAirborneOrGroundedStateAfterLeavingWall(bool land, string expectedState)
    {
        EnterWallDash(1f);
        var state = new ActionExecutionState(_attack, 100, 0, 0, 0, default);
        _bridge.EnterSegment(state, true);
        if (land)
        {
            _motor.Teleport(new Vector3(5000f, 0f, 5000f));
            Ground();
        }
        else _motor.LaunchVertical(8f);
        Assert.IsFalse(_motor.IsWallSliding);
        _bridge.EndAction(100, false);
        _bridge.Sample(default, 0.2f);
        _bridge.Sample(default, 0.2f);
        Assert.IsTrue(_bridge.Animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer." + expectedState));
    }

    [Test]
    public void WallDashChangesSideWhenReenteringOppositeWall()
    {
        EnterWallDash(-1f);
        Assert.IsTrue(_bridge.Animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.WallDashLeft"));
        EnterWallDash(1f);
        Assert.IsTrue(_bridge.Animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.WallDashRight"));
    }
}
#endif
