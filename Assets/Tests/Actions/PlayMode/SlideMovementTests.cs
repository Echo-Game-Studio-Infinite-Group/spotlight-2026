using GameJam.Actions;
using NUnit.Framework;
using UnityEngine;

public sealed class SlideMovementTests
{
    private GameObject _root, _ground, _obstacle;
    private PlayerMotor _motor;
    private MovementParams _parameters;

    [SetUp]
    public void SetUp()
    {
        _parameters = ScriptableObject.CreateInstance<MovementParams>();
        _parameters.CapsuleBaseHeight = _parameters.CapsuleFastHeight = 2f;
        _parameters.CapsuleBaseRadius = _parameters.CapsuleMinRadius = .3f;
        _parameters.SlideCapsuleHeight = 1f;
        _parameters.SlideDecel = 6f;
        _root = new GameObject("SlideMovementPlayer"); _root.SetActive(false);
        _root.transform.position = new Vector3(7000f, 10f, 7000f);
        _root.AddComponent<CharacterController>();
        _motor = _root.AddComponent<PlayerMotor>(); _motor.SetParams(_parameters);
        _root.SetActive(true);
        _ground = new GameObject("SlideMovementGround");
        _ground.transform.position = _root.transform.position + Vector3.down * .5f;
        _ground.AddComponent<BoxCollider>().size = new Vector3(1000f, 1f, 1000f);
        Physics.SyncTransforms(); Step(2);
        Assert.IsTrue(_motor.IsGrounded);
        _motor.SetHorizontalSpeed(12f);
        Assert.IsTrue(_motor.TryExecute(MotorCommandKind.EnterSlide, 0f));
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(_root); Object.DestroyImmediate(_ground);
        if (_obstacle != null) Object.DestroyImmediate(_obstacle);
        Object.DestroyImmediate(_parameters);
    }

    private void Step(int count = 1, bool held = true)
    {
        for (int i = 0; i < count; i++)
            _motor.Simulate(new PlayerInputFrame { SlideHeld = held }, 1f / 60f, 0f);
    }

    private BoxCollider Ceiling(bool trigger = false)
    {
        _obstacle = new GameObject("SlideCeiling");
        _obstacle.transform.position = _root.transform.position + Vector3.up * 1.5f;
        BoxCollider collider = _obstacle.AddComponent<BoxCollider>();
        collider.size = new Vector3(10f, .4f, 300f);
        collider.isTrigger = trigger;
        Physics.SyncTransforms();
        return collider;
    }

    [Test]
    public void HeldSlideBelowOldExitThresholdContinuesUntilExactlyStopped()
    {
        Step(5);
        Assert.IsTrue(_motor.IsSliding);
        _motor.SetHorizontalSpeed(1f);
        Step();
        Assert.IsTrue(_motor.IsSliding, "低于旧的 2 m/s 门槛也不能提前起身");
        Assert.Greater(_motor.HorizontalSpeed, 0f);
        for (int i = 0; i < 100 && _motor.IsSliding; i++) Step();
        Assert.AreEqual(0f, _motor.HorizontalSpeed);
        Assert.IsFalse(_motor.IsSliding);
        Assert.That(_root.GetComponent<CharacterController>().height, Is.EqualTo(2f).Within(.001f));
    }

    [Test]
    public void ReleaseStandsWhileStillMovingAndRetainsMomentum()
    {
        Step(5);
        float speed = _motor.HorizontalSpeed;
        Step(held: false);
        Assert.IsFalse(_motor.IsSliding);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(speed - _parameters.SlideDecel / 60f).Within(.001f));
        Assert.That(_root.GetComponent<CharacterController>().height, Is.EqualTo(2f).Within(.001f));
    }

    [Test]
    public void ReleaseUnderCeilingStaysLowAndStandsAfterItClearsWithMomentum()
    {
        Ceiling(); Step(10, held: false);
        Assert.IsTrue(_motor.IsSliding);
        Assert.IsTrue(_motor.IsSlideHeadBlocked);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(12f).Within(.001f));
        Assert.That(_root.GetComponent<CharacterController>().height, Is.EqualTo(1f).Within(.001f));
        _obstacle.SetActive(false); Physics.SyncTransforms(); Step(held: false);
        Assert.IsFalse(_motor.IsSliding);
        Assert.IsFalse(_motor.IsSlideHeadBlocked);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(11.9f).Within(.001f));
    }

    [Test]
    public void ReachingZeroStandsEvenIfSlideKeyIsStillHeld()
    {
        for (int i = 0; i < 200 && _motor.IsSliding; i++) Step(held: true);
        Assert.AreEqual(0f, _motor.HorizontalSpeed);
        Assert.IsFalse(_motor.IsSliding);
    }

    [Test]
    public void CeilingPreservesSpeedAndLeavingItRestoresSlideFriction()
    {
        Ceiling();
        Step(120);
        Assert.IsTrue(_motor.IsSlideHeadBlocked);
        Assert.IsTrue(_motor.IsSliding);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(12f).Within(.0001f));
        _obstacle.SetActive(false); Physics.SyncTransforms();
        Step();
        Assert.IsFalse(_motor.IsSlideHeadBlocked);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(11.9f).Within(.001f));
    }

    [Test]
    public void StoppedPlayerCannotStandUntilCeilingClears()
    {
        Ceiling();
        _motor.SetHorizontalSpeed(0f); Step(10);
        Assert.AreEqual(0f, _motor.HorizontalSpeed);
        Assert.IsTrue(_motor.IsSliding);
        Assert.That(_root.GetComponent<CharacterController>().height, Is.EqualTo(1f).Within(.001f));
        _obstacle.SetActive(false); Physics.SyncTransforms(); Step();
        Assert.IsFalse(_motor.IsSliding);
        Assert.IsFalse(_motor.IsSlideHeadBlocked);
    }

    [Test]
    public void TriggerAboveHeadDoesNotGrantFrictionExemption()
    {
        Ceiling(true); Step(10);
        Assert.IsFalse(_motor.IsSlideHeadBlocked);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(11f).Within(.001f));
    }

    [Test]
    public void NearbySideWallDoesNotGrantFrictionExemption()
    {
        _obstacle = new GameObject("SlideSideWall");
        _obstacle.transform.position = _root.transform.position + Vector3.right * .5f + Vector3.up;
        _obstacle.AddComponent<BoxCollider>().size = new Vector3(.3f, 2f, 300f);
        Physics.SyncTransforms(); Step(10);
        Assert.IsFalse(_motor.IsSlideHeadBlocked);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(11f).Within(.001f));
    }

    [Test]
    public void OwnHeadColliderDoesNotGrantFrictionExemption()
    {
        GameObject ownHead = new GameObject("OwnHead"); ownHead.transform.SetParent(_root.transform, false);
        ownHead.transform.localPosition = Vector3.up * 1.4f;
        ownHead.AddComponent<BoxCollider>().size = Vector3.one * .2f;
        Physics.SyncTransforms(); Step(10);
        Assert.IsFalse(_motor.IsSlideHeadBlocked);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(11f).Within(.001f));
    }

    [Test]
    public void JumpStillCancelsMovingSlideWhenThereIsRoom()
    {
        Assert.IsTrue(_motor.TryExecute(MotorCommandKind.Jump, 0f)); Step();
        Assert.IsFalse(_motor.IsSliding);
        Assert.AreEqual(1, _motor.JumpCount);
        Assert.Greater(_motor.Velocity.y, 0f);
        Assert.Greater(_motor.HorizontalSpeed, 0f);
    }

    [Test]
    public void CeilingRejectsJumpWithoutCancellingSlideOrReducingSpeed()
    {
        Ceiling();
        Assert.IsFalse(_motor.TryExecute(MotorCommandKind.Jump, 0f)); Step(10);
        Assert.IsTrue(_motor.IsSliding);
        Assert.AreEqual(0, _motor.JumpCount);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(12f).Within(.001f));
    }
}
