#if UNITY_EDITOR
using System.Collections.Generic;
using GameJam.Actions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class SlideWallCollisionTests
{
    private readonly List<Object> _created = new List<Object>();
    private readonly List<WallContact> _impacts = new List<WallContact>();
    private GameObject _root, _ground;
    private PlayerMotor _motor;
    private PlayerActionRunner _runner;
    private PlayerAnimation _animation;
    private PlayerRagdollHitStun _hitStun;
    private CharacterController _controller;
    private MovementParams _parameters;
    private BoxCollider _wall;
    private long _tick;

    [SetUp]
    public void SetUp()
    {
        TimeManager.ClearSlowMotion(); _tick = 0; _impacts.Clear();
        _root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab"));
        _created.Add(_root);
        foreach (Camera camera in _root.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        _hitStun = _root.GetComponent<PlayerRagdollHitStun>(); _hitStun.enabled = false;
        _motor = _root.GetComponent<PlayerMotor>();
        _parameters = Object.Instantiate(_motor.Params); _created.Add(_parameters); _motor.SetParams(_parameters);
        _parameters.MaxSpeed = 120f;
        _controller = _root.GetComponent<CharacterController>();
        _runner = _root.GetComponent<PlayerActionRunner>();
        _animation = _root.GetComponentInChildren<PlayerAnimation>();
        _motor.Teleport(new Vector3(11000f, 10f, 11000f)); _root.transform.rotation = Quaternion.identity;
        GameObject reference = Create("SlideWallReference", Vector3.zero);
        _motor.SetMovementReference(reference.transform);
        _ground = Create("SlideWallGround", _root.transform.position + Vector3.down * .5f);
        _ground.AddComponent<BoxCollider>().size = new Vector3(1000f, 1f, 1000f);
        Physics.SyncTransforms(); Tick(false); Tick(false);
        Assert.IsTrue(_motor.IsGrounded);
        _motor.WallImpact += contact => _impacts.Add(contact);
    }

    [TearDown]
    public void TearDown()
    {
        _runner?.Disconnect();
        for (int i = _created.Count - 1; i >= 0; i--)
            if (_created[i] != null) Object.DestroyImmediate(_created[i]);
        _created.Clear(); TimeManager.ClearSlowMotion();
    }

    private GameObject Create(string name, Vector3 position)
    {
        var value = new GameObject(name); _created.Add(value); value.transform.position = position; return value;
    }

    private void Tick(bool held = true, bool start = false, Vector2 move = default, float dt = 1f / 60f)
    {
        ActionInputButtons buttons = held ? ActionInputButtons.Slide : ActionInputButtons.None;
        ActionInputEdge[] edges = start ? new[] { new ActionInputEdge(ActionInputButtons.Slide, ActionInputStep.Edge.Pressed, buttons) } : null;
        _runner.SimulateTick(new ActionInputSample(_tick++, buttons, move, edges),
            new PlayerInputFrame { SlideHeld = held, Move = move }, dt);
    }

    private void Begin(float speed = 40f, bool sliding = true, bool airborne = false)
    {
        _motor.SetHorizontalSpeed(speed); Tick(false);
        if (sliding) { Tick(true, true); Assert.IsTrue(_motor.IsSliding); }
        if (!airborne) return;
        _ground.SetActive(false); _parameters.Gravity = 0f; Physics.SyncTransforms(); Tick(sliding);
        Assert.IsFalse(_motor.IsGrounded);
    }

    private BoxCollider Wall(float distance = 32f, float height = 20f, bool trigger = false)
    {
        GameObject wall = Create("SlideFrontWall", _root.transform.position + Vector3.forward * (distance + .5f)
            + Vector3.up * (height * .5f - .08f));
        BoxCollider collider = wall.AddComponent<BoxCollider>(); collider.size = new Vector3(1000f, height, 1f);
        collider.isTrigger = trigger; Physics.SyncTransforms(); return collider;
    }

    private void Collide(bool sliding = true, float dt = 1f / 60f)
    {
        for (int i = 0; i < 600 && _impacts.Count == 0; i++) Tick(sliding, dt: dt);
        Assert.AreEqual(1, _impacts.Count, "真实移动必须上报一次正撞接触");
    }

    private float ModelFront()
    {
        float front = float.NegativeInfinity;
        foreach (SkinnedMeshRenderer renderer in _root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var mesh = new Mesh();
            try
            {
                renderer.BakeMesh(mesh, false);
                foreach (Vector3 vertex in mesh.vertices) front = Mathf.Max(front, renderer.transform.TransformPoint(vertex).z);
            }
            finally { Object.DestroyImmediate(mesh); }
        }
        return front;
    }

    [TestCase(12f)]
    [TestCase(40f)]
    [TestCase(120f)]
    public void SlideStopsBeforeVisibleBodyEntersWallAndCanStand(float speed)
    {
        Begin(speed); _wall = Wall(Mathf.Max(6f, speed * .8f)); Collide();
        Assert.IsTrue(_impacts[0].WasSliding);
        Assert.Greater(_impacts[0].IncomingVelocity.z, 0f);
        Assert.Less(_motor.HorizontalSpeed, .001f);
        Assert.GreaterOrEqual(_wall.bounds.min.z - _root.transform.position.z, _parameters.SlideForwardExtent - .01f);
        Assert.LessOrEqual(ModelFront(), _wall.bounds.min.z + .02f);
        for (int i = 0; i < 90; i++)
        {
            Tick(false);
            if (i < 10 || i % 15 == 0) Assert.LessOrEqual(ModelFront(), _wall.bounds.min.z + .02f, "起身也不能穿墙");
        }
        Assert.IsFalse(_motor.IsSliding);
        Assert.IsFalse(_animation.IsSlideRecovering);
        Assert.That(_controller.height, Is.EqualTo(_parameters.CapsuleBaseHeight).Within(.001f));
        Assert.AreEqual(1, _impacts.Count);
    }

    [TestCase(MotorCommandKind.ExitSlide)]
    [TestCase(MotorCommandKind.Jump)]
    [TestCase(MotorCommandKind.HighJump)]
    public void NarrowSlideCanStandOrJumpWhileWidthRecoveryIsBlocked(MotorCommandKind command)
    {
        // 关闭前伸保护，单独覆盖旧的贴墙增宽死锁，避免保护距离掩盖站起判定错误。
        _parameters.SlideForwardExtent = 0f;
        Begin(); _wall = Wall(); Collide();
        Assert.IsTrue(_motor.IsSliding);
        Assert.That(_controller.radius, Is.EqualTo(_parameters.CapsuleMinRadius).Within(.001f));
        var contacts = new MovementContacts(_controller, _parameters);
        Assert.IsFalse(contacts.CanResize(_parameters.CapsuleBaseHeight, _parameters.CapsuleBaseRadius));
        Assert.IsTrue(_motor.CanExecute(command));
        Assert.IsTrue(_motor.TryExecute(command, command == MotorCommandKind.HighJump ? 4f : 0f));
        Tick();
        Assert.IsFalse(_motor.IsSliding);
        Assert.That(_controller.height, Is.EqualTo(_parameters.CapsuleBaseHeight).Within(.001f));
        Assert.That(_controller.radius, Is.EqualTo(_parameters.CapsuleMinRadius).Within(.001f));
        if (command != MotorCommandKind.ExitSlide) Assert.Greater(_motor.Velocity.y, 0f);
        else
        {
            for (int i = 0; i < 45; i++) Tick(false, move: Vector2.down);
            Assert.That(_controller.radius, Is.EqualTo(_parameters.CapsuleBaseRadius).Within(.001f));
        }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void GroundAirAndAirborneSlideAllReportIncomingImpact(bool sliding, bool airborne)
    {
        Begin(40f, sliding, airborne); _wall = Wall(sliding ? 32f : 6f); Collide(sliding);
        Assert.AreEqual(sliding, _impacts[0].WasSliding);
        Assert.That(_impacts[0].ApproachAngle, Is.EqualTo(90f).Within(.1f));
        Assert.Greater(_impacts[0].IncomingVelocity.z, 12f);
        Assert.Less(_motor.HorizontalSpeed, .001f);
    }

    [Test]
    public void GlancingSlideRetainsTangentialSpeedWithoutImpactOrWallRun()
    {
        _root.transform.rotation = Quaternion.Euler(0f, 70f, 0f); Begin(); _wall = Wall(4f);
        for (int i = 0; i < 120; i++) Tick();
        Assert.IsTrue(_motor.IsSliding);
        Assert.IsFalse(_motor.IsWallSliding);
        Assert.AreEqual(0, _impacts.Count);
        Assert.Greater(_motor.Velocity.x, 25f);
        Assert.That(_motor.Velocity.z, Is.EqualTo(0f).Within(.001f));
        Assert.GreaterOrEqual(_wall.bounds.min.z - _root.transform.position.z, _controller.radius - .01f);
    }

    [Test]
    public void ReleaseWhileMovingKeepsFrontProtectionThroughStandingRecovery()
    {
        Begin(); _wall = Wall(10f);
        for (int i = 0; i < 10; i++) Tick();
        Tick(false);
        Assert.IsFalse(_motor.IsSliding); Assert.IsTrue(_animation.IsSlideRecovering);
        Collide(false);
        Assert.IsFalse(_impacts[0].WasSliding, "松键退出后不再享受滑铲免伤");
        for (int i = 0; i < 80; i++)
        {
            Assert.LessOrEqual(ModelFront(), _wall.bounds.min.z + .02f);
            Tick(false);
        }
        Assert.IsFalse(_animation.IsSlideRecovering);
    }

    [Test]
    public void SlideCannotStartIfItsForwardPoseAlreadyOverlapsWall()
    {
        _motor.SetHorizontalSpeed(12f); Tick(false); _wall = Wall(.5f);
        Assert.IsFalse(_motor.CanExecute(MotorCommandKind.EnterSlide));
        Tick(true, true);
        Assert.IsFalse(_motor.IsSliding);
        for (int i = 0; i < 10; i++) Tick(false);
        Assert.LessOrEqual(ModelFront(), _wall.bounds.min.z + .02f);
    }

    [Test]
    public void FrontImpactAtCornerIsNotHiddenByGlancingSideContact()
    {
        _root.transform.rotation = Quaternion.Euler(0f, 19f, 0f); Begin(); _wall = Wall(1.19f);
        GameObject side = Create("SlideCornerSide", _root.transform.position + Vector3.right * 1.05f + Vector3.up * 4f);
        side.AddComponent<BoxCollider>().size = new Vector3(1f, 20f, 300f);
        Physics.SyncTransforms(); Tick();
        Assert.AreEqual(1, _impacts.Count);
        Assert.Less(_impacts[0].Normal.z, -.9f);
        Assert.GreaterOrEqual(_impacts[0].ApproachAngle, _parameters.WallMaxApproachAngle);
        Assert.IsFalse(_motor.IsWallSliding);
    }

    [Test]
    public void LowCeilingImpactStaysLowAndCanMoveBackThenStandAfterClearingRoof()
    {
        Begin();
        GameObject roof = Create("SlideTunnelRoof", _root.transform.position + Vector3.up * 1.35f);
        roof.AddComponent<BoxCollider>().size = new Vector3(200f, .3f, 300f);
        _wall = Wall(); Physics.SyncTransforms();
        _hitStun.enabled = true;
        float health = _root.GetComponent<HealthComponent>().Health;
        Collide();
        Assert.IsTrue(_motor.IsSliding); Assert.IsTrue(_motor.IsSlideHeadBlocked);
        Assert.IsFalse(_hitStun.IsActive); Assert.IsTrue(_runner.IsConnected);
        Assert.AreEqual(health, _root.GetComponent<HealthComponent>().Health);
        Vector3 stopped = _root.transform.position;
        for (int i = 0; i < 90; i++) Tick(false, move: Vector2.down);
        Assert.Less(_root.transform.position.z, stopped.z - 1f, "不能在低顶尽头永久锁死");
        Assert.IsTrue(_motor.IsSliding);
        roof.SetActive(false); Physics.SyncTransforms(); Tick(false);
        Assert.IsFalse(_motor.IsSliding);
        Assert.That(_controller.height, Is.EqualTo(_parameters.CapsuleBaseHeight).Within(.001f));
    }

    [Test]
    public void LowObstacleStillBlocksExtendedFeet()
    {
        Begin(); _wall = Wall(32f, .4f); Collide();
        Assert.LessOrEqual(ModelFront(), _wall.bounds.min.z + .02f);
        Assert.Less(_motor.HorizontalSpeed, .001f);
    }

    [Test]
    public void TriggerInFrontDoesNotBlockSlideOrReportImpact()
    {
        Begin(); _wall = Wall(4f, trigger: true);
        for (int i = 0; i < 30; i++) Tick();
        Assert.AreEqual(0, _impacts.Count);
        Assert.Greater(_root.transform.position.z, _wall.bounds.max.z + 1f);
        Assert.Greater(_motor.HorizontalSpeed, 30f);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RagdollConsumesGroundImpactAfterMotorSettlesAndSlideDoesNotTakeDamage(bool sliding)
    {
        Begin(40f, sliding); _wall = Wall(sliding ? 32f : 6f); _hitStun.enabled = true;
        HealthComponent health = _root.GetComponent<HealthComponent>(); float before = health.Health;
        Collide(sliding);
        Assert.IsTrue(_hitStun.IsActive);
        Assert.IsFalse(_controller.enabled); Assert.IsFalse(_motor.enabled); Assert.IsFalse(_runner.IsConnected);
        Assert.AreEqual(MovementState.Airborne, _motor.State, "布娃娃关闭 Motor 后的重置不能被旧模拟覆盖");
        if (sliding) Assert.AreEqual(before, health.Health);
        else Assert.Less(health.Health, before);
        _hitStun.ExitHitStun();
        Assert.IsTrue(_controller.enabled); Assert.IsTrue(_motor.enabled); Assert.IsTrue(_runner.IsConnected);
        Tick(false);
    }

    [TestCase(1f)]
    [TestCase(.2f)]
    public void SlideFrontProtectionStopsAtSamePlaceAcrossPlayerClockRates(float rate)
    {
        Begin(); _wall = Wall(8f); Collide(dt: rate / 60f);
        Assert.That(_wall.bounds.min.z - _root.transform.position.z,
            Is.EqualTo(_parameters.SlideForwardExtent + _controller.skinWidth).Within(.015f));
    }
}
#endif
