using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class WallMovementTests
{
    private readonly List<GameObject> _objects = new List<GameObject>();
    private MovementParams _params;
    private PlayerMotor _motor;
    private float _now;
    private const float Tick = 1f / 60f;

    [SetUp]
    public void SetUp()
    {
        _params = ScriptableObject.CreateInstance<MovementParams>();
        _params.Gravity = 0f;
        _params.WallMaxApproachAngle = 45f;
        _params.WallGraceTime = 0.15f;
        _params.CapsuleShrinkStartSpeed = 200f;
        _params.CapsuleShrinkEndSpeed = 300f;
        GameObject player = new GameObject("TestPlayer");
        _objects.Add(player);
        player.SetActive(false);
        player.AddComponent<CharacterController>();
        _motor = player.AddComponent<PlayerMotor>();
        _motor.SetParams(_params);
        player.transform.position = new Vector3(0f, 3f, 0f);
        player.SetActive(true);
        _motor.enabled = false;
        Wall(new Vector3(0f, 3f, 2f), new Vector3(100f, 20f, 1f));
        _now = 0f;
    }
    [TearDown]
    public void TearDown()
    {
        foreach (GameObject go in _objects) Object.DestroyImmediate(go);
        _objects.Clear();
        Object.DestroyImmediate(_params);
    }

    [TestCase(10f, true)]
    [TestCase(30f, true)]
    [TestCase(44f, true)]
    [TestCase(45f, false)]
    [TestCase(60f, false)]
    [TestCase(90f, false)]
    public void RealWall_ApproachAngleControlsEntry(float angle, bool expected)
    {
        Enter(angle);
        Assert.AreEqual(expected, _motor.IsWallSliding);
        if (expected) Assert.That(_motor.WallApproachAngle, Is.EqualTo(angle).Within(0.02f));
    }

    [Test]
    public void GraceJump_PreservesSpeedAndAwardsExactlyOneBoost()
    {
        Enter(30f);
        float speed = _motor.HorizontalSpeed;
        Step(jump: true);
        Assert.AreEqual(1, _motor.WallJumpCount);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(speed + _params.WallJumpBoost).Within(0.02f));
        Assert.Less(_motor.Velocity.z, 0f);
        Step(jump: true);
        Assert.AreEqual(1, _motor.WallJumpCount);
    }

    [Test]
    public void ZeroFallLimit_StaysInWallSlideAcrossManyTicks()
    {
        _params.Gravity = 20f;
        _params.WallMaxFallSpeed = 0f;
        Enter(30f);
        Assert.IsTrue(_motor.IsWallSliding);
        float height = _motor.transform.position.y;
        for (int i = 0; i < 120; i++)
        {
            Step();
            Assert.IsTrue(_motor.IsWallSliding, $"第 {i + 1} 帧丢失贴墙状态");
            Assert.That(_motor.transform.position.y, Is.EqualTo(height).Within(0.01f));
        }
    }

    [Test]
    public void BriefLostContact_DoesNotDropWallJump()
    {
        _params.Gravity = 20f;
        _params.WallMaxFallSpeed = 0f;
        Enter(30f);
        Assert.IsTrue(_motor.IsWallSliding);
        _objects[1].GetComponent<Collider>().enabled = false;
        Step();
        Assert.IsTrue(_motor.IsWallSliding);
        _objects[1].GetComponent<Collider>().enabled = true;
        Physics.SyncTransforms();
        Step(jump: true);
        Assert.AreEqual(1, _motor.WallJumpCount);
    }

    [Test]
    public void LostWallBeyondContactGrace_ExitsWallSlide()
    {
        Enter(30f);
        Assert.IsTrue(_motor.IsWallSliding);
        _objects[1].GetComponent<Collider>().enabled = false;
        for (int i = 0; i < 10; i++) Step();
        Assert.IsFalse(_motor.IsWallSliding);
    }

    [Test]
    public void WallContact_ExpiresGraceAndDecaysWithoutRefreshing()
    {
        Enter(30f);
        float speed = _motor.HorizontalSpeed;
        for (int i = 0; i < 25; i++) Step();
        Assert.IsTrue(_motor.IsWallSliding);
        Assert.AreEqual(0f, _motor.WallWindowRemaining);
        Assert.That(_motor.HorizontalSpeed, Is.LessThan(speed * 0.6f));
        float beforeJump = _motor.HorizontalSpeed;
        Step(jump: true);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(beforeJump * Mathf.Exp(-_params.WallFriction * Tick)).Within(0.02f));
    }

    [Test]
    public void HeadOn_DoesNotAttachOrGrantWallJump()
    {
        Enter(90f);
        Assert.IsFalse(_motor.IsWallSliding);
        Step(jump: true);
        Assert.AreEqual(0, _motor.WallJumpCount);
    }

    [Test]
    public void TooSteep_ImmediateJumpDoesNotCountAsWallJump()
    {
        Enter(60f);
        Step(jump: true);
        Assert.AreEqual(0, _motor.WallJumpCount);
    }

    [Test]
    public void LeavingWall_ClearsJumpPermission()
    {
        Enter(30f);
        _motor.transform.position += Vector3.back * 2f;
        Physics.SyncTransforms();
        Step(jump: true);
        Assert.IsFalse(_motor.IsWallSliding);
        Assert.AreEqual(0, _motor.WallJumpCount);
    }

    [Test]
    public void RecontactSameWall_RequiresSeparationThenCanBhopAgain()
    {
        Enter(30f);
        Step(jump: true);
        for (int i = 0; i < 20; i++) Step();
        float speed = _motor.HorizontalSpeed;
        SetVelocity(new Vector3(Mathf.Cos(30f * Mathf.Deg2Rad), 0f,
            Mathf.Sin(30f * Mathf.Deg2Rad)) * speed);
        for (int i = 0; i < 80 && !_motor.IsWallSliding; i++) Step();
        Assert.IsTrue(_motor.IsWallSliding);
        Step(jump: true);
        Assert.AreEqual(2, _motor.WallJumpCount);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(speed + _params.WallJumpBoost).Within(0.03f));
    }

    [Test]
    public void WallJump_WithForwardInputStillSeparatesFromWall()
    {
        Enter(30f);
        Step(jump: true, move: Vector2.up);
        Assert.Less(_motor.Velocity.z, 0f);
        Assert.AreEqual(1, _motor.WallJumpCount);
    }

    [Test]
    public void WallJump_KeepsOutwardVelocityUntilSeparated()
    {
        _params.WallJumpAngle = 1f;
        Enter(30f);
        Step(jump: true, move: Vector2.up);
        Assert.AreEqual(1, _motor.WallJumpCount);
        for (int i = 0; i < 20; i++)
        {
            Step(move: Vector2.up);
            Assert.Less(_motor.Velocity.z, 0f, $"第 {i + 1} 帧过早回头；z={_motor.transform.position.z}");
        }
        Assert.AreEqual(1, _motor.WallJumpCount);
    }

    [Test]
    public void JumpOnContactTick_UsesBufferedPress()
    {
        _motor.transform.position = new Vector3(0f, 3f, 0.9f);
        Physics.SyncTransforms();
        SetVelocity(new Vector3(17f, 0f, 10f));
        Step(jump: true);
        Assert.AreEqual(1, _motor.WallJumpCount);
    }

    [Test]
    public void Freeze_DoesNotMoveOrGenerateEnergy_ExpiredJumpIsNotReplayed()
    {
        Enter(30f);
        Vector3 position = _motor.transform.position;
        float energy = _motor.Energy;
        _motor.Simulate(new PlayerInputFrame { JumpPressed = true, JumpTime = _now }, 0f, _now);
        _now += 1f;
        _motor.Simulate(default, 0f, _now);
        Assert.AreEqual(position, _motor.transform.position);
        Assert.AreEqual(energy, _motor.Energy);
        Step();
        Assert.AreEqual(0, _motor.WallJumpCount);
    }

    [Test]
    public void Ceiling_IsNotAWall_AndStopsUpwardVelocity()
    {
        Wall(new Vector3(0f, 6f, 0f), new Vector3(10f, 1f, 10f));
        SetVelocity(Vector3.up * 20f);
        for (int i = 0; i < 10; i++) Step();
        Assert.IsFalse(_motor.IsWallSliding);
        Assert.AreEqual(0f, _motor.Velocity.y);
    }

    [Test]
    public void Teleport_ClearsAllMotionAndContactState()
    {
        Enter(30f);
        _motor.Teleport(Vector3.up * 10f);
        Assert.AreEqual(Vector3.zero, _motor.Velocity);
        Assert.AreEqual(0f, _motor.Energy);
        Assert.AreEqual(0f, _motor.WallWindowRemaining);
        Assert.AreEqual(MovementState.Airborne, _motor.State);
        Step(jump: true);
        Assert.AreEqual(0, _motor.JumpCount);
    }

    [Test]
    public void JumpReward_RespectsSpeedCap()
    {
        _params.MaxSpeed = 20f;
        Enter(30f);
        Step(jump: true);
        Assert.That(_motor.HorizontalSpeed, Is.LessThanOrEqualTo(20.001f));
    }

    [Test]
    public void WallSeam_DoesNotRefreshWindowAcrossAdjacentColliders()
    {
        Object.DestroyImmediate(_objects[1]);
        _objects.RemoveAt(1);
        Wall(new Vector3(-24f, 3f, 2f), new Vector3(50f, 20f, 1f));
        Wall(new Vector3(26f, 3f, 2f), new Vector3(50f, 20f, 1f));
        Enter(30f);
        for (int i = 0; i < 30; i++) Step();
        Assert.IsTrue(_motor.IsWallSliding);
        Assert.Greater(_motor.transform.position.x, 1f);
        Assert.AreEqual(0f, _motor.WallWindowRemaining);
    }

    [Test]
    public void Slide_UnderLowCeilingCannotStand_ThenCanJumpWhenClear()
    {
        _params.Gravity = 20f;
        Wall(new Vector3(0f, -0.5f, 0f), new Vector3(100f, 1f, 100f));
        _motor.Teleport(new Vector3(0f, 0.05f, -5f));
        for (int i = 0; i < 10; i++) Step();
        Assert.IsTrue(_motor.IsGrounded);
        SetVelocity(Vector3.right * 10f);
        _now += Tick;
        _motor.Simulate(new PlayerInputFrame { SlidePressed = true, SlideHeld = true }, Tick, _now);
        Assert.IsTrue(_motor.IsSliding);
        Wall(new Vector3(_motor.transform.position.x, 1.65f, -5f), new Vector3(10f, 0.5f, 4f));
        Step(jump: true);
        Assert.IsTrue(_motor.IsSliding);
        Object.DestroyImmediate(_objects[_objects.Count - 1]);
        _objects.RemoveAt(_objects.Count - 1);
        Physics.SyncTransforms();
        Step(jump: true);
        Assert.IsFalse(_motor.IsSliding);
        Assert.Greater(_motor.Velocity.y, 0f);
    }

    private void Enter(float angle)
    {
        float radians = angle * Mathf.Deg2Rad;
        SetVelocity(new Vector3(Mathf.Cos(radians), 0f, Mathf.Sin(radians)) * 20f);
        for (int i = 0; i < 60 && !_motor.IsWallSliding; i++) Step();
    }
    private void Step(bool jump = false, Vector2 move = default)
    {
        _now += Tick;
        _motor.Simulate(new PlayerInputFrame { JumpPressed = jump, JumpTime = _now, Move = move }, Tick, _now);
    }
    private void SetVelocity(Vector3 velocity) => typeof(PlayerMotor).GetField("_velocity", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_motor, velocity);
    private void Wall(Vector3 position, Vector3 scale)
    {
        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _objects.Add(wall);
        wall.transform.position = position;
        wall.transform.localScale = scale;
        Physics.SyncTransforms();
    }
}
