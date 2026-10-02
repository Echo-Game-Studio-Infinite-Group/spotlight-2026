using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class MovementControlTests
{
    private const float Tick = 1f / 60f;
    private GameObject _player, _ground;
    private PlayerMotor _motor;
    private MovementParams _params;
    private float _now;

    [SetUp]
    public void SetUp()
    {
        _params = ScriptableObject.CreateInstance<MovementParams>();
        _params.Gravity = 0f;
        _params.RunAccel = 0f;
        _params.AirControl = 0f;
        _params.CapsuleShrinkStartSpeed = 200f;
        _params.CapsuleShrinkEndSpeed = 300f;
        _player = new GameObject("MovementControlTest");
        _player.SetActive(false);
        _player.AddComponent<CharacterController>();
        _motor = _player.AddComponent<PlayerMotor>();
        _motor.SetParams(_params);
        _player.transform.position = Vector3.up * 100f;
        _player.SetActive(true);
        _motor.enabled = false;
        _now = 0f;
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(_player);
        if (_ground != null) Object.DestroyImmediate(_ground);
        Object.DestroyImmediate(_params);
    }

    [TestCase(1f, 0f)]
    [TestCase(-1f, 0f)]
    [TestCase(0f, -1f)]
    [TestCase(1f, 1f)]
    public void Turning_IsSmoothAndKeepsVelocityMagnitudeAndFacingTogether(float x, float y)
    {
        SetVelocity(new Vector3(0f, 3f, 20f));
        Vector2 move = new Vector2(x, y).normalized;
        Vector3 target = new Vector3(move.x, 0f, move.y);
        float originalAngle = Vector3.Angle(Vector3.forward, target);
        for (int tick = 0; tick < 40; tick++)
        {
            Step(move);
            Assert.That(_motor.HorizontalSpeed, Is.EqualTo(20f).Within(0.001f));
            Assert.That(_motor.Velocity.y, Is.EqualTo(3f).Within(0.001f));
            Assert.Less(Vector3.Angle(MovementMath.Horizontal(_motor.Velocity), _player.transform.forward), 0.05f);
            if (tick == 0)
                Assert.That(Vector3.Angle(_player.transform.forward, target), Is.GreaterThan(0f).And.LessThan(originalAngle));
        }
        Assert.Less(Vector3.Angle(_player.transform.forward, target), 0.2f);
        Quaternion facing = _player.transform.rotation;
        Vector3 velocity = _motor.Velocity;
        Step();
        Assert.AreEqual(facing, _player.transform.rotation);
        Assert.Less((_motor.Velocity - velocity).magnitude, 0.001f);
    }

    [TestCase(2f)]
    [TestCase(20f)]
    public void GroundFriction_ProtectsBhopWindowThenDecaysAllSpeedsToRest(float speed)
    {
        Land();
        SetVelocity(Vector3.forward * speed);
        Step();
        Assert.IsTrue(_motor.InFrictionWindow);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(speed).Within(0.001f));
        for (int i = 0; i < 120; i++) Step();
        Assert.IsFalse(_motor.InFrictionWindow);
        Assert.AreEqual(0f, _motor.HorizontalSpeed);
    }

    [Test]
    public void GroundFriction_AlsoActsWithMovementInput_AndZeroCoefficientPreservesSpeed()
    {
        Land();
        for (int i = 0; i < 20; i++) Step();
        SetVelocity(Vector3.forward * 2f);
        Step(Vector2.up);
        Assert.That(_motor.HorizontalSpeed, Is.LessThan(2f).And.GreaterThan(0f));
        _params.GroundFriction = 0f;
        SetVelocity(Vector3.forward * 0.02f);
        Step(Vector2.up);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(0.02f).Within(0.001f));
    }

    private void Land()
    {
        _ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _ground.transform.position = Vector3.down * 0.5f;
        _ground.transform.localScale = new Vector3(1000f, 1f, 1000f);
        _params.Gravity = 20f;
        _motor.Teleport(Vector3.up * 0.05f);
        for (int i = 0; i < 30 && !_motor.IsGrounded; i++) Step();
        Assert.IsTrue(_motor.IsGrounded);
    }

    private void Step(Vector2 move = default)
    {
        _now += Tick;
        _motor.Simulate(new PlayerInputFrame { Move = move }, Tick, _now);
    }

    private void SetVelocity(Vector3 velocity) => typeof(PlayerMotor)
        .GetField("_velocity", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_motor, velocity);
}
