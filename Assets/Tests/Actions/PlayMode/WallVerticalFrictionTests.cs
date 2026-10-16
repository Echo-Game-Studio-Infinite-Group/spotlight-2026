using System.Reflection;
using GameJam.Actions;
using NUnit.Framework;
using UnityEngine;

public sealed class WallVerticalFrictionTests
{
    private GameObject _root;
    private GameObject _wall;
    private PlayerMotor _motor;
    private MovementParams _parameters;

    [SetUp]
    public void SetUp()
    {
        _parameters = ScriptableObject.CreateInstance<MovementParams>();
        _parameters.Gravity = 0f;
        _parameters.WallGraceTime = 0.25f;
        _parameters.WallVerticalFriction = 3f;
        _parameters.WallVerticalStopSpeed = 0.01f;
        _parameters.WallFriction = 10f;
        _parameters.WallGravityScale = 0.6f;
        _parameters.WallMaxFallSpeed = 100f;
        _parameters.CapsuleBaseRadius = _parameters.CapsuleMinRadius = 0.3f;
        _parameters.CapsuleBaseHeight = _parameters.CapsuleFastHeight = 2f;
        _root = new GameObject("WallVerticalFrictionPlayer");
        // 先接好参数再激活，保证运行时 Awake 使用完整配置。
        _root.SetActive(false);
        _root.transform.position = new Vector3(4000f, 20f, 4000f);
        _root.transform.rotation = Quaternion.Euler(0f, 20f, 0f);
        _root.AddComponent<CharacterController>();
        _motor = _root.AddComponent<PlayerMotor>();
        _motor.SetParams(_parameters);
        _root.SetActive(true);
        _wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _wall.name = "WallVerticalFrictionWall";
        _wall.transform.position = new Vector3(4000.9f, 50f, 4000f);
        _wall.transform.localScale = new Vector3(1f, 200f, 200f);
        Physics.SyncTransforms();
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(_root);
        Object.DestroyImmediate(_wall);
        Object.DestroyImmediate(_parameters);
    }

    private void EnterWall(float verticalSpeed)
    {
        _motor.SetHorizontalSpeed(10f);
        // 只注入初始惯性；上墙和后续窗口状态必须由真实碰撞产生。
        Vector3 velocity = _motor.Velocity;
        velocity.y = verticalSpeed;
        typeof(PlayerMotor).GetField("_velocity", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_motor, velocity);
        for (int i = 0; i < 10 && !_motor.IsWallSliding; i++)
            _motor.Simulate(default, 1f / 60f, 0f);
        Assert.IsTrue(_motor.IsWallSliding, "测试角色必须通过实际墙面碰撞进入划墙，位置=" + _root.transform.position + "，速度=" + _motor.Velocity);
        Assert.That(_motor.Velocity.y, Is.EqualTo(verticalSpeed).Within(0.00001f));
        _parameters.Gravity = 20f;
    }

    [TestCase(9f)]
    [TestCase(-9f)]
    public void ProtectedWindowDampsBothVerticalDirectionsAndPreservesHorizontalSpeed(float verticalSpeed)
    {
        EnterWall(verticalSpeed);
        float horizontalSpeed = _motor.HorizontalSpeed;
        for (int i = 0; i < 6; i++) _motor.Simulate(default, 1f / 60f, 0f);
        Assert.IsTrue(_motor.IsWallSliding);
        Assert.That(_motor.Velocity.y, Is.EqualTo(verticalSpeed * Mathf.Exp(-0.3f)).Within(0.0001f));
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(horizontalSpeed).Within(0.0001f));
    }

    [TestCase(1f)]
    [TestCase(0.35f)]
    [TestCase(0.05f)]
    public void EqualPlayerTimeGivesEqualDampingAcrossScaledAndSplitSteps(float rate)
    {
        EnterWall(9f);
        float remaining = 0.1f;
        while (remaining > 0f)
        {
            float step = Mathf.Min(remaining, rate / 60f);
            _motor.Simulate(default, step, 0f);
            remaining -= step;
        }
        Assert.IsTrue(_motor.IsWallSliding);
        Assert.That(_motor.Velocity.y, Is.EqualTo(9f * Mathf.Exp(-0.3f)).Within(0.0002f));
    }

    [Test]
    public void CrossingWindowOnlyDampsProtectedTimeAndThenRestoresGravity()
    {
        _parameters.WallGraceTime = 0.05f;
        EnterWall(9f);
        _motor.Simulate(default, 0.2f, 0f);
        float expected = 9f * Mathf.Exp(-3f * 0.05f) - 20f * 0.6f * 0.15f;
        Assert.IsTrue(_motor.IsWallSliding);
        Assert.That(_motor.Velocity.y, Is.EqualTo(expected).Within(0.0001f));
        _motor.Simulate(default, 0.1f, 0f);
        Assert.IsTrue(_motor.IsWallSliding);
        Assert.That(_motor.Velocity.y, Is.EqualTo(expected - 20f * 0.6f * 0.1f).Within(0.0001f));
    }

    [Test]
    public void ZeroCoefficientPreservesVerticalInertiaEvenBelowStopThreshold()
    {
        _parameters.WallVerticalFriction = 0f;
        EnterWall(0.005f);
        _motor.Simulate(default, 0.1f, 0f);
        Assert.That(_motor.Velocity.y, Is.EqualTo(0.005f).Within(0.000001f));
    }

    [TestCase(0.02f)]
    [TestCase(-0.02f)]
    public void SmallDampedVerticalVelocityStopsWithoutChangingDirection(float verticalSpeed)
    {
        _parameters.WallVerticalFriction = 10f;
        EnterWall(verticalSpeed);
        _motor.Simulate(default, 0.1f, 0f);
        Assert.AreEqual(0f, _motor.Velocity.y);
        Assert.IsTrue(_motor.IsWallSliding);
    }

    [Test]
    public void WallJumpCommandKeepsItsFullUpwardImpulse()
    {
        EnterWall(-9f);
        Assert.IsTrue(_motor.TryExecute(MotorCommandKind.Jump, 0f));
        _motor.Simulate(default, 1f / 60f, 0f);
        Assert.AreEqual(MovementState.Airborne, _motor.State);
        Assert.AreEqual(1, _motor.WallJumpCount);
        Assert.That(_motor.Velocity.y, Is.EqualTo(_parameters.WallJumpUpImpulse - _parameters.Gravity / 60f).Within(0.0001f));
    }

    [Test]
    public void FreeAirMovementDoesNotReceiveWallVerticalDamping()
    {
        _parameters.Gravity = 20f;
        _motor.LaunchVertical(9f);
        _motor.Simulate(default, 0.1f, 0f);
        Assert.AreEqual(MovementState.Airborne, _motor.State);
        Assert.That(_motor.Velocity.y, Is.EqualTo(7f).Within(0.0001f));
    }
}
