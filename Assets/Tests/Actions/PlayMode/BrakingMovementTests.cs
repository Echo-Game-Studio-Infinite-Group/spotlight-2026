using GameJam.Actions;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class BrakingMovementTests
{
    private const float Step = 1f / 60f;
    private GameObject _root, _ground, _reference, _wall;
    private PlayerMotor _motor;
    private MovementParams _parameters;
    private float _time;

    [SetUp]
    public void SetUp()
    {
        _time = 0f;
        _parameters = ScriptableObject.CreateInstance<MovementParams>();
        _parameters.GroundFriction = _parameters.AirControl = 0f;
        _parameters.CapsuleBaseHeight = _parameters.CapsuleFastHeight = 2f;
        _parameters.CapsuleBaseRadius = _parameters.CapsuleMinRadius = .3f;
        _root = new GameObject("BrakingPlayer"); _root.SetActive(false);
        _root.transform.position = new Vector3(21000f, 20f, 21000f);
        _root.AddComponent<CharacterController>();
        _motor = _root.AddComponent<PlayerMotor>(); _motor.SetParams(_parameters);
        _root.SetActive(true);
        _reference = new GameObject("BrakingCameraReference");
        _motor.SetMovementReference(_reference.transform);
        _ground = new GameObject("BrakingFloor");
        _ground.transform.position = _root.transform.position + Vector3.down * .5f;
        _ground.AddComponent<BoxCollider>().size = new Vector3(1000f, 1f, 1000f);
        Physics.SyncTransforms(); Tick(); Tick();
        Assert.IsTrue(_motor.IsGrounded);
        _motor.SetHorizontalSpeed(12f);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(_root); Object.DestroyImmediate(_ground);
        Object.DestroyImmediate(_reference);
        if (_wall != null) Object.DestroyImmediate(_wall);
        Object.DestroyImmediate(_parameters);
    }

    private void Tick(bool brake = false, Vector2 move = default, bool jump = false, bool slide = false, float dt = Step)
    {
        _time += dt;
        _motor.Simulate(new PlayerInputFrame { BrakeHeld = brake, Move = move, JumpPressed = jump,
            JumpTime = _time, SlideHeld = slide }, dt, _time);
    }

    private void Brake(int ticks = 10)
    {
        for (int i = 0; i < ticks; i++) Tick(brake: true);
        Assert.Greater(_motor.BrakeRecoverySpeed, 0f);
    }

    [TestCase(-1f, 0f)]
    [TestCase(1f, 0f)]
    [TestCase(0f, 1f)]
    [TestCase(1f, 1f)]
    public void BrakeLocksFacingAndMomentumWhenDirectionAndCameraChange(float x, float y)
    {
        Quaternion facing = _root.transform.rotation;
        for (int i = 0; i < 20; i++)
        {
            _reference.transform.rotation = Quaternion.Euler(0f, i * 9f, 0f);
            Tick(brake: true, move: new Vector2(x, y));
        }
        Assert.That(Quaternion.Angle(facing, _root.transform.rotation), Is.LessThan(.001f));
        Assert.That(_motor.Velocity.x, Is.EqualTo(0f).Within(.001f));
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(4f).Within(.002f));
    }

    [Test]
    public void BrakeReachesZeroWithoutReversingOrWAcceleratingAgain()
    {
        for (int i = 0; i < 90; i++) Tick(brake: true, move: Vector2.up);
        Assert.IsTrue(_motor.IsBraking);
        Assert.AreEqual(0f, _motor.HorizontalSpeed);
        Assert.That(_motor.BrakeRecoverySpeed, Is.EqualTo(12f).Within(.002f));
        Tick(brake: true, jump: true);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(12f).Within(.002f));
    }

    [Test]
    public void NormalFrictionLossIsNotRecoverable()
    {
        _parameters.FrictionExemptWindow = 0f;
        _parameters.GroundFriction = 2f;
        Tick(brake: true, dt: .1f);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(7.2f).Within(.002f));
        Assert.That(_motor.BrakeRecoverySpeed, Is.EqualTo(2.4f).Within(.002f));
        Tick(brake: true, jump: true);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(7.2f * (1f - 2f * Step) + 2.4f).Within(.002f));
    }

    [Test]
    public void HeldBrakeReturnsSpeedOnceAndDoesNotBrakeDuringJump()
    {
        Brake(); Tick(brake: true, jump: true);
        Assert.AreEqual(1, _motor.JumpCount);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(12f).Within(.002f));
        Assert.Greater(_motor.Velocity.y, 0f);
        Assert.IsFalse(_motor.IsBraking);
        for (int i = 0; i < 20; i++) Tick(brake: true);
        Assert.IsFalse(_motor.IsGrounded);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(12f).Within(.002f));
        Assert.AreEqual(0f, _motor.BrakeRecoverySpeed);
    }

    [TestCase(.69f, true)]
    [TestCase(.7f, true)]
    [TestCase(.71f, false)]
    public void ReleasedBrakeUsesSevenTenthsPlayerSecondWindow(float delay, bool recover)
    {
        Brake();
        float reduced = _motor.HorizontalSpeed;
        Tick(dt: delay);
        Tick(jump: true);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(recover ? 12f : reduced).Within(.002f));
        Assert.AreEqual(0f, _motor.BrakeRecoverySpeed);
    }

    [Test]
    public void ReleasingBrakeAllowsSteeringBeforeRecoveryJump()
    {
        _parameters.FrictionExemptWindow = 0f;
        Brake();
        _reference.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
        for (int i = 0; i < 15; i++) Tick(move: Vector2.up);
        Tick(move: Vector2.up, jump: true);
        Assert.Greater(_motor.Velocity.x, 11f);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(12f).Within(.002f));
    }

    [TestCase(.04f, true)]
    [TestCase(.2f, false)]
    public void QualifiedJumpPreinputSurvivesRecoveryWindowButNotItsOwnExpiry(float pendingTime, bool recover)
    {
        Brake(); Tick(dt: .69f);
        float reduced = _motor.HorizontalSpeed;
        _motor.BufferBrakeRecoveryJump(_parameters.JumpBufferWindow);
        Tick(dt: pendingTime);
        Assert.IsTrue(_motor.TryExecute(MotorCommandKind.Jump, 0f));
        Tick();
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(recover ? 12f : reduced).Within(.002f));
    }

    [Test]
    public void RecoveryObeysExistingMaximumSpeed()
    {
        Brake(); _motor.SetHorizontalSpeed(_parameters.MaxSpeed);
        Tick(jump: true);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(_parameters.MaxSpeed).Within(.002f));
    }

    [Test]
    public void RecoveryWindowDoesNotExpireFromUnscaledTimeDuringZeroDeltaSimulation()
    {
        Brake(); Tick(dt: .3f);
        float stored = _motor.BrakeRecoverySpeed;
        _motor.Simulate(default, 0f, _time + 100f);
        Assert.That(_motor.BrakeRecoverySpeed, Is.EqualTo(stored).Within(.001f));
        Tick(jump: true);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(12f).Within(.002f));
    }

    [Test]
    public void AirborneBrakeDoesNotChangeHorizontalSpeedOrGravity()
    {
        _motor.LaunchVertical(8f);
        float vertical = _motor.Velocity.y;
        Tick(brake: true);
        Assert.IsFalse(_motor.IsBraking);
        Assert.AreEqual(0f, _motor.BrakeRecoverySpeed);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(12f).Within(.002f));
        Assert.That(_motor.Velocity.y, Is.EqualTo(vertical - _parameters.Gravity * Step).Within(.001f));
    }

    [Test]
    public void SHeldFromHighSpeedAirRemainsBrakeAfterLandingAtLowSpeed()
    {
        _motor.LaunchVertical(8f); Tick(brake: true);
        Assert.IsFalse(_motor.IsBraking);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(12f).Within(.002f));
        _motor.SetHorizontalSpeed(3f);
        for (int i = 0; i < 90; i++) Tick(brake: true);
        Assert.IsTrue(_motor.IsGrounded);
        Assert.IsTrue(_motor.IsBraking);
        Assert.IsFalse(_motor.IsWalking);
        Assert.That(_motor.HorizontalSpeed, Is.Zero.Within(.002f));
        Assert.That(_motor.BrakeRecoverySpeed, Is.EqualTo(3f).Within(.002f));
    }

    [Test]
    public void SlideOnlyUsesSlideFrictionAndProducesNoRecovery()
    {
        _parameters.SlideDecel = 6f;
        Assert.IsTrue(_motor.TryExecute(MotorCommandKind.EnterSlide, 0f));
        Tick(brake: true, slide: true, dt: .1f);
        Assert.IsTrue(_motor.IsSliding);
        Assert.IsFalse(_motor.IsBraking);
        Assert.AreEqual(0f, _motor.BrakeRecoverySpeed);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(11.4f).Within(.002f));
    }

    [Test]
    public void SlideRecoveryPoseCannotStartBraking()
    {
        _motor.SetSlidePoseActive(true);
        Tick(brake: true);
        Assert.IsFalse(_motor.IsBraking);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(12f).Within(.002f));
        Assert.AreEqual(0f, _motor.BrakeRecoverySpeed);
    }

    [Test]
    public void HoldingSAfterLeavingGroundDoesNotExtendStoredSpeedForever()
    {
        Brake(); _ground.SetActive(false); Physics.SyncTransforms();
        for (int i = 0; i < 60; i++) Tick(brake: true);
        Assert.IsFalse(_motor.IsGrounded);
        Assert.IsFalse(_motor.IsBraking);
        Assert.AreEqual(0f, _motor.BrakeRecoverySpeed);
    }

    [Test]
    public void WallSlideCannotBrakeOrCollectWallFriction()
    {
        _ground.SetActive(false);
        _motor.Teleport(_root.transform.position + Vector3.up * 20f);
        _parameters.Gravity = _parameters.WallFriction = _parameters.WallVerticalFriction = 0f;
        _parameters.WallGraceTime = 10f;
        _wall = new GameObject("BrakingWall");
        _wall.transform.position = _root.transform.position + new Vector3(.9f, 0f, 0f);
        _wall.AddComponent<BoxCollider>().size = new Vector3(1f, 100f, 200f);
        _root.transform.rotation = Quaternion.Euler(0f, 20f, 0f);
        _motor.SetHorizontalSpeed(20f); Physics.SyncTransforms();
        for (int i = 0; i < 15 && !_motor.IsWallSliding; i++) Tick();
        Assert.IsTrue(_motor.IsWallSliding);
        float speed = _motor.HorizontalSpeed;
        for (int i = 0; i < 10; i++) Tick(brake: true, move: Vector2.left);
        Assert.IsTrue(_motor.IsWallSliding);
        Assert.IsFalse(_motor.IsBraking);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(speed).Within(.002f));
        Assert.AreEqual(0f, _motor.BrakeRecoverySpeed);
    }

    [Test]
    public void ActionPolicyCanDisableBrakeIndependentlyOfMoveAndTurn()
    {
        _motor.SetActionControl(new ActionControlPolicy { AllowBrake = false });
        Tick(brake: true);
        Assert.IsFalse(_motor.IsBraking);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(12f).Within(.002f));
        Assert.AreEqual(0f, _motor.BrakeRecoverySpeed);
    }

    [TestCase("teleport")]
    [TestCase("disable")]
    [TestCase("suppress")]
    [TestCase("stop")]
    [TestCase("command")]
    public void ExplicitResetsClearStoredAndReservedSpeed(string reset)
    {
        Brake(); _motor.BufferBrakeRecoveryJump(.12f);
        switch (reset)
        {
            case "teleport": _motor.Teleport(_root.transform.position); break;
            case "disable": _motor.enabled = false; _motor.enabled = true; break;
            case "suppress": _motor.InputSuppressed = true; _motor.InputSuppressed = false; break;
            case "stop": _motor.SetHorizontalSpeed(0f); break;
            case "command": _motor.TryExecute(MotorCommandKind.ClearHorizontal, 0f); break;
        }
        Assert.AreEqual(0f, _motor.BrakeRecoverySpeed);
        _motor.LaunchVertical(8f);
        Assert.LessOrEqual(_motor.HorizontalSpeed, 8.001f);
    }

    [Test]
    public void ActionSubstepsDoNotMultiplyBrakeOrRecovery()
    {
        Vector3 start = _root.transform.position;
        for (int i = 0; i < 12; i++) Tick(brake: true);
        float speed = _motor.HorizontalSpeed, stored = _motor.BrakeRecoverySpeed;
        _motor.Teleport(start); Tick(); Tick(); _motor.SetHorizontalSpeed(12f);
        for (int i = 0; i < 24; i++) Tick(brake: true, dt: Step / 2f);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(speed).Within(.002f));
        Assert.That(_motor.BrakeRecoverySpeed, Is.EqualTo(stored).Within(.002f));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StairBrakeOnlyRecoversBrakeLossAndKeepsGroundSupport(bool descending)
    {
        Vector3 origin = _root.transform.position;
        _ground.transform.position = origin + new Vector3(0f, -.5f, -5f);
        _ground.GetComponent<BoxCollider>().size = new Vector3(8f, 1f, 12f);
        _wall = new GameObject("BrakingStairs");
        for (int i = 0; i < 12; i++)
        {
            var step = new GameObject("BrakeStep" + i);
            step.transform.SetParent(_wall.transform);
            float top = (descending ? -1f : 1f) * (i + 1) * .2f;
            step.transform.position = origin + new Vector3(0f, top - 2f, 1f + (i + .5f) * .6f);
            step.AddComponent<BoxCollider>().size = new Vector3(8f, 4f, .6f);
        }
        int impacts = 0;
        _motor.WallImpact += _ => impacts++;
        Physics.SyncTransforms();
        for (int i = 0; i < 15; i++)
        {
            Tick(brake: true);
            Assert.IsTrue(_motor.IsGrounded, "刹车不能造成楼梯支撑闪断，tick=" + i);
        }
        Assert.AreEqual(0, impacts);
        Assert.That(_motor.HorizontalSpeed + _motor.BrakeRecoverySpeed, Is.EqualTo(12f).Within(.003f));
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(6f).Within(.003f));
        Assert.That(Mathf.Abs(_root.transform.position.y - origin.y), Is.GreaterThan(.15f));
    }
}
