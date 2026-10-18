using GameJam.Actions;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class DirectionalWalkingTests
{
    private const float Step = 1f / 60f;
    private GameObject _root, _floor, _reference;
    private PlayerMotor _motor;
    private MovementParams _parameters;
    private float _time;

    [SetUp]
    public void SetUp()
    {
        _time = 0f;
        _parameters = ScriptableObject.CreateInstance<MovementParams>();
        _parameters.GroundFriction = _parameters.AirControl = 0f;
        _parameters.Pump = PumpMode.VerbatimQuake;
        _root = new GameObject("DirectionalWalker"); _root.SetActive(false);
        _root.transform.position = new Vector3(23000f, 20f, 23000f);
        _root.AddComponent<CharacterController>();
        _motor = _root.AddComponent<PlayerMotor>(); _motor.SetParams(_parameters);
        _root.SetActive(true);
        _reference = new GameObject("WalkCameraReference");
        _motor.SetMovementReference(_reference.transform);
        _floor = new GameObject("WalkFloor");
        _floor.transform.position = _root.transform.position + Vector3.down * .5f;
        _floor.AddComponent<BoxCollider>().size = new Vector3(1000f, 1f, 1000f);
        Physics.SyncTransforms(); Tick(); Tick();
        Assert.IsTrue(_motor.IsGrounded);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(_root); Object.DestroyImmediate(_floor);
        Object.DestroyImmediate(_reference); Object.DestroyImmediate(_parameters);
    }

    private void Tick(Vector2 move = default, bool brake = false, bool jump = false, bool slide = false)
    {
        _time += Step;
        _motor.Simulate(new PlayerInputFrame { Move = move, BrakeHeld = brake, JumpPressed = jump,
            JumpTime = _time, SlideHeld = slide }, Step, _time);
    }

    [TestCase(-1f, 0f)]
    [TestCase(1f, 0f)]
    [TestCase(-1f, 1f)]
    [TestCase(1f, 1f)]
    public void FourLowSpeedDirectionsWalkAtOneSpeedAndKeepForwardFacing(float x, float y)
    {
        Vector2 move = new Vector2(x, y);
        for (int i = 0; i < 60; i++) Tick(move);
        Assert.IsTrue(_motor.IsWalking);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(_parameters.WalkSpeed).Within(.001f));
        Assert.That(Vector3.Angle(_root.transform.forward, Vector3.forward), Is.LessThan(.01f));
        Assert.That(Vector3.Angle(MovementMath.Horizontal(_motor.Velocity), new Vector3(x, 0f, y)), Is.LessThan(.01f));
    }

    [TestCase(-1f, 0f)]
    [TestCase(1f, 0f)]
    [TestCase(-1f, 1f)]
    [TestCase(1f, 1f)]
    public void HighSpeedDirectionsPreserveSpeedAndFaceTheirMovement(float x, float y)
    {
        _motor.SetHorizontalSpeed(15f);
        Vector2 move = new Vector2(x, y);
        for (int i = 0; i < 60; i++) Tick(move);
        Assert.IsFalse(_motor.IsWalking);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(15f).Within(.002f));
        Assert.That(Vector3.Angle(_root.transform.forward, new Vector3(x, 0f, y)), Is.LessThan(.1f));
        Assert.That(Vector3.Angle(MovementMath.Horizontal(_motor.Velocity), _root.transform.forward), Is.LessThan(.01f));
    }

    [TestCase(-1f, 0f)]
    [TestCase(1f, 0f)]
    [TestCase(-1f, 1f)]
    [TestCase(1f, 1f)]
    public void WalkDirectionsRemainRelativeToCameraRatherThanBodyTurn(float x, float y)
    {
        _reference.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
        Vector2 move = new Vector2(x, y);
        for (int i = 0; i < 60; i++) Tick(move);
        Vector3 expected = _reference.transform.right * x + _reference.transform.forward * y;
        Assert.IsTrue(_motor.IsWalking);
        Assert.That(Vector3.Angle(_root.transform.forward, _reference.transform.forward), Is.LessThan(.1f));
        Assert.That(Vector3.Angle(MovementMath.Horizontal(_motor.Velocity), expected), Is.LessThan(.01f));
    }

    [Test]
    public void WAloneUsesRunSpeedAndChangingWalkSpeedDoesNotChangeRun()
    {
        _parameters.WalkSpeed = 1.5f;
        for (int i = 0; i < 60; i++) Tick(Vector2.up);
        Assert.IsFalse(_motor.IsWalking);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(_parameters.GroundSpeedThreshold).Within(.001f));
    }

    [Test]
    public void WalkSpeedCanBeAuthoredIndependently()
    {
        _parameters.WalkSpeed = 2f;
        for (int i = 0; i < 60; i++) Tick(Vector2.left);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(2f).Within(.001f));
        Assert.AreEqual(6f, _parameters.GroundSpeedThreshold);
    }

    [Test]
    public void ExistingGroundFrictionStillAppliesWithoutWalkSpeedClamp()
    {
        _parameters.FrictionExemptWindow = 0f; _parameters.GroundFriction = 2f;
        _motor.SetHorizontalSpeed(15f); Tick(Vector2.right);
        Assert.IsFalse(_motor.IsWalking);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(15f * (1f - 2f * Step)).Within(.001f));
        Assert.AreEqual(0f, _motor.BrakeRecoverySpeed);
    }

    [Test]
    public void HighSpeedSideInputRetainsExistingLandingPump()
    {
        _parameters.Pump = PumpMode.WindowPump;
        _motor.SetHorizontalSpeed(15f); Tick(Vector2.left);
        Assert.IsFalse(_motor.IsWalking);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(15f + _parameters.RunAccel * _parameters.GroundSpeedThreshold * Step).Within(.001f));
    }

    [Test]
    public void NaturallySlowingIntoLowSpeedSwitchesToWalkWithoutOscillation()
    {
        _parameters.FrictionExemptWindow = 0f; _parameters.GroundFriction = 2f;
        _parameters.RunAccel = 0f;
        _motor.SetHorizontalSpeed(15f);
        bool walked = false;
        for (int i = 0; i < 180; i++)
        {
            Tick(Vector2.right);
            if (_motor.IsWalking) walked = true;
            if (walked) Assert.IsTrue(_motor.IsWalking);
        }
        Assert.IsTrue(walked);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(_parameters.WalkSpeed).Within(.002f));
    }

    [TestCase(-1f, 0f)]
    [TestCase(1f, 0f)]
    [TestCase(-1f, 1f)]
    [TestCase(1f, 1f)]
    public void BrakeOverridesWalkingAndLocksFacing(float x, float y)
    {
        _motor.SetHorizontalSpeed(3f);
        _reference.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
        Tick(new Vector2(x, y), brake: true);
        Assert.IsFalse(_motor.IsWalking);
        Assert.IsFalse(_motor.HasMovementInput);
        Assert.IsTrue(_motor.IsBraking);
        Assert.That(Vector3.Angle(_root.transform.forward, Vector3.forward), Is.LessThan(.01f));
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(3f - _parameters.BrakeDeceleration * Step).Within(.001f));
    }

    [TestCase(-1f, 0f)]
    [TestCase(1f, 0f)]
    [TestCase(-1f, 1f)]
    [TestCase(1f, 1f)]
    public void WalkingJumpRetainsItsActualHorizontalDirection(float x, float y)
    {
        Vector2 move = new Vector2(x, y);
        for (int i = 0; i < 30; i++) Tick(move);
        Tick(move, jump: true);
        Assert.AreEqual(1, _motor.JumpCount);
        Assert.IsFalse(_motor.IsWalking);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(_parameters.WalkSpeed).Within(.001f));
        Assert.That(Vector3.Angle(MovementMath.Horizontal(_motor.Velocity), new Vector3(x, 0f, y)), Is.LessThan(.01f));
    }

    [Test]
    public void AirAndSlideDoNotEnterGroundWalking()
    {
        _motor.SetHorizontalSpeed(3f); _motor.LaunchVertical(8f); Tick(Vector2.right);
        Assert.IsFalse(_motor.IsWalking);
        _motor.Teleport(_floor.transform.position + Vector3.up * .5f); Tick(); Tick();
        _motor.SetHorizontalSpeed(12f);
        Assert.IsTrue(_motor.TryExecute(MotorCommandKind.EnterSlide, 0f));
        _motor.SetHorizontalSpeed(3f); Tick(Vector2.right, slide: true);
        Assert.IsTrue(_motor.IsSliding);
        Assert.IsFalse(_motor.IsWalking);
    }
}
