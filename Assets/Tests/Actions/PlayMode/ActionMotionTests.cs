using System.Reflection;
using GameJam.Actions;
using NUnit.Framework;
using UnityEngine;

public sealed class ActionMotionTests
{
    private GameObject _root, _obstacle;
    private PlayerMotor _motor;
    private MovementParams _parameters;
    [SetUp]
    public void SetUp()
    {
        _parameters = ScriptableObject.CreateInstance<MovementParams>();
        _parameters.Gravity = 20f; _parameters.GroundFriction = 0f; _parameters.AirControl = 0f;
        _parameters.CapsuleBaseHeight = _parameters.CapsuleFastHeight = 2f;
        _parameters.CapsuleBaseRadius = _parameters.CapsuleMinRadius = .3f;
        _root = new GameObject("ActionMotionPlayer"); _root.SetActive(false);
        _root.transform.position = new Vector3(5000f, 20f, 5000f);
        _root.AddComponent<CharacterController>(); _motor = _root.AddComponent<PlayerMotor>();
        _motor.SetParams(_parameters); _root.SetActive(true);
    }
    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(_root); if (_obstacle != null) Object.DestroyImmediate(_obstacle);
        Object.DestroyImmediate(_parameters);
    }
    private void SeedVertical(float value)
    {
        Vector3 velocity = _motor.Velocity; velocity.y = value;
        typeof(PlayerMotor).GetField("_velocity", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_motor, velocity);
    }
    private void Step(int count = 1)
    { for (int i = 0; i < count; i++) _motor.Simulate(default, 1f / 60f, 0f); }
    private void Obstacle(Vector3 position, Vector3 size)
    {
        _obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _obstacle.transform.position = position; _obstacle.transform.localScale = size; Physics.SyncTransforms();
    }
    [TestCase(8f)]
    [TestCase(-3f)]
    public void AirMotionTurnsVerticalInertiaIntoDiveAndEndRestoresGravity(float initialY)
    {
        _motor.SetHorizontalSpeed(20f); SeedVertical(initialY);
        var motion = new ActionMotionSettings { ForwardImpulse = 0f, DiveAngle = 45f, DiveResponse = 60f, MaxDiveSpeed = 12f };
        _motor.BeginMotion(motion, 1); Step(6);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(20f).Within(.001f));
        Assert.That(_motor.Velocity.y, Is.EqualTo(-12f).Within(.1f));
        _motor.EndMotion(1); float y = _motor.Velocity.y; Step();
        Assert.That(_motor.Velocity.y, Is.EqualTo(y - 20f / 60f).Within(.001f));
    }
    [Test]
    public void RepeatedCancelsHaveBoundedBoostAndOldOwnerCannotStopNewMotion()
    {
        _motor.SetHorizontalSpeed(10f);
        var motion = new ActionMotionSettings { ForwardImpulse = 3f, BoostSpeedLimit = 15f, DiveResponse = 60f };
        for (int i = 1; i <= 20; i++) _motor.BeginMotion(motion, i);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(15f).Within(.001f));
        _motor.EndMotion(19); SeedVertical(5f); Step(6); Assert.Less(_motor.Velocity.y, 0f);
        _motor.SetHorizontalSpeed(40f); _motor.BeginMotion(motion, 21);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(40f).Within(.001f));
    }
    [Test]
    public void LandingStopsDiveAndWalkingOffEdgeEntersDiveAgain()
    {
        Obstacle(_root.transform.position + Vector3.down * .5f, new Vector3(10f, 1f, 2f));
        _motor.SetHorizontalSpeed(10f); _motor.BeginMotion(new ActionMotionSettings { ForwardImpulse = 0f, DiveResponse = 60f }, 1);
        Step(); Assert.IsTrue(_motor.IsGrounded);
        Step(); Assert.IsTrue(_motor.IsGrounded); Assert.Greater(_motor.Velocity.y, -1f);
        Step(15); Assert.IsFalse(_motor.IsGrounded); Assert.Less(_motor.Velocity.y, -1f);
    }
    [Test]
    public void DiveUsesControllerCollisionAndDoesNotAttachToWall()
    {
        Obstacle(_root.transform.position + Vector3.forward * 2f, new Vector3(50f, 50f, 1f));
        _motor.SetHorizontalSpeed(60f); _motor.BeginMotion(new ActionMotionSettings { ForwardImpulse = 0f, DiveResponse = 60f }, 1);
        Step(10);
        Assert.Less(_root.transform.position.z, 5001.5f);
        Assert.IsFalse(_motor.IsWallSliding); Assert.That(_motor.HorizontalSpeed, Is.LessThan(.01f));
    }
    [Test]
    public void DiveReleasesWallAndBypassesProtectedVerticalFriction()
    {
        _root.transform.rotation = Quaternion.Euler(0f, 20f, 0f);
        _parameters.Gravity = 0f; _parameters.WallGraceTime = 1f; _parameters.WallVerticalFriction = 100f;
        Obstacle(_root.transform.position + Vector3.right * .9f, new Vector3(1f, 100f, 100f));
        _motor.SetHorizontalSpeed(10f); SeedVertical(8f);
        for (int i = 0; i < 10 && !_motor.IsWallSliding; i++) Step();
        Assert.IsTrue(_motor.IsWallSliding);
        _motor.BeginMotion(new ActionMotionSettings { ForwardImpulse = 0f, DiveResponse = 60f }, 1);
        Assert.IsFalse(_motor.IsWallSliding); Step(6);
        Assert.IsFalse(_motor.IsWallSliding); Assert.Less(_motor.Velocity.y, -1f);
    }
}
