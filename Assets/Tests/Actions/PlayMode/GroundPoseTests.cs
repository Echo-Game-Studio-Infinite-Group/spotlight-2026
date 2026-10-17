#if UNITY_EDITOR
using System.Collections.Generic;
using GameJam.Actions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class GroundPoseTests
{
    private readonly List<Object> _created = new List<Object>();
    private GameObject _root;
    private PlayerMotor _motor;
    private PlayerActionRunner _runner;
    private PlayerGroundPose _pose;
    private ActionAnimatorBridge _bridge;
    private long _tick;

    [SetUp]
    public void SetUp()
    {
        _root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab")); _created.Add(_root);
        foreach (Camera camera in _root.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        _root.GetComponent<PlayerRagdollHitStun>().enabled = false;
        _motor = _root.GetComponent<PlayerMotor>();
        MovementParams parameters = Object.Instantiate(_motor.Params); _created.Add(parameters);
        parameters.GroundFriction = parameters.SlideDecel = 0f; _motor.SetParams(parameters);
        _runner = _root.GetComponent<PlayerActionRunner>(); _pose = _root.GetComponent<PlayerGroundPose>();
        _bridge = _root.GetComponent<ActionAnimatorBridge>();
        _motor.Teleport(new Vector3(18000f, 30.5f, 18000f)); _root.transform.rotation = Quaternion.identity;
        _tick = 0;
    }
    [TearDown]
    public void TearDown()
    {
        _runner?.Disconnect();
        for (int i = _created.Count - 1; i >= 0; i--) Object.DestroyImmediate(_created[i]);
        _created.Clear(); TimeManager.ClearSlowMotion();
    }
    private void Ground(float bank = 0f, float incline = 0f)
    {
        GameObject floor = new GameObject("GroundPoseFloor"); _created.Add(floor);
        floor.transform.SetPositionAndRotation(new Vector3(18000f, 30f, 18000f), Quaternion.Euler(-incline, 0f, bank));
        BoxCollider collider = floor.AddComponent<BoxCollider>(); collider.center = Vector3.down * .5f; collider.size = new Vector3(100f, 1f, 100f);
        Physics.SyncTransforms(); for (int i = 0; i < 60; i++) Tick();
        Assert.IsTrue(_motor.IsGrounded);
    }
    private void Tick(bool slide = false)
    {
        ActionInputButtons buttons = slide ? ActionInputButtons.Slide : ActionInputButtons.None;
        _runner.SimulateTick(new ActionInputSample(_tick++, buttons), new PlayerInputFrame { SlideHeld = slide }, 1f / 60f);
    }
    [Test]
    public void ManualAnimationEvaluationRunsIkAndLeavesMotorUntouched()
    {
        Ground(); Assert.Greater(_pose.IkEvaluationCount, 0); Assert.IsFalse(_bridge.Animator.enabled);
        Vector3 position = _root.transform.position, velocity = _motor.Velocity;
        int before = _pose.IkEvaluationCount; _bridge.Sample(default, 0f);
        Assert.Greater(_pose.IkEvaluationCount, before);
        Assert.AreEqual(position, _root.transform.position); Assert.AreEqual(velocity, _motor.Velocity);
        Assert.Greater(_pose.LeftFootWeight, .1f); Assert.Greater(_pose.RightFootWeight, .1f);
    }
    [Test]
    public void BankedSurfaceGivesFeetDifferentHeightsAndAirborneClearsWeights()
    {
        Ground(20f);
        Assert.Greater(_pose.LeftFootWeight, .1f); Assert.Greater(_pose.RightFootWeight, .1f);
        Vector3 left = _pose.LeftFootTarget, right = _pose.RightFootTarget;
        Assert.Greater(Mathf.Abs(left.y - right.y), .025f);
        Assert.That(left.y - right.y, Is.EqualTo(Mathf.Tan(20f * Mathf.Deg2Rad) * (left.x - right.x)).Within(.025f));
        _motor.LaunchVertical(8f); Tick(); Assert.IsFalse(_motor.IsGrounded);
        Assert.AreEqual(0f, _pose.LeftFootWeight); Assert.AreEqual(0f, _pose.RightFootWeight);
    }
    [Test]
    public void SlideUsesBodyPoseWithoutPlantingFeetAndTeleportClearsVisualOffset()
    {
        Ground(); _motor.SetHorizontalSpeed(12f);
        Assert.IsTrue(_motor.TryExecute(MotorCommandKind.EnterSlide, 0f));
        Tick(true); Assert.AreEqual(0f, _pose.LeftFootWeight); Assert.AreEqual(0f, _pose.RightFootWeight);
        _motor.Teleport(new Vector3(18000f, 35f, 18000f));
        Assert.AreEqual(0f, _pose.VisualStepOffset);
    }

    [TestCase(20f)]
    [TestCase(40f)]
    [TestCase(-20f)]
    [TestCase(-40f)]
    public void SlideBodyFollowsRampWithoutChangingColliderOrientation(float angle)
    {
        Ground(incline: angle); _motor.SetHorizontalSpeed(12f);
        Assert.IsTrue(_motor.TryExecute(MotorCommandKind.EnterSlide, 0f));
        for (int i = 0; i < 60; i++) Tick(true);
        Assert.IsTrue(_motor.IsSliding); Assert.IsTrue(_motor.IsGrounded);
        Assert.That(_pose.SlideTiltAngle, Is.EqualTo(Mathf.Abs(angle)).Within(1f));
        Assert.That(Vector3.Angle(_root.transform.up, Vector3.up), Is.LessThan(.01f));
        Assert.AreEqual(0f, _pose.LeftFootWeight); Assert.AreEqual(0f, _pose.RightFootWeight);
    }
}
#endif
