using System.Collections.Generic;
using GameJam.Actions;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class WallSurfaceTests
{
    private readonly List<GameObject> _created = new List<GameObject>();
    private readonly List<WallContact> _impacts = new List<WallContact>();
    private PlayerMotor _motor;
    private MovementParams _parameters;
    private BoxCollider _wall;
    private const float Step = 1f / 60f;

    [SetUp]
    public void SetUp()
    {
        _impacts.Clear();
        _parameters = ScriptableObject.CreateInstance<MovementParams>();
        _parameters.Gravity = _parameters.AirControl = 0f;
        _parameters.WallMaxApproachAngle = 70f;
        _parameters.WallGraceTime = 10f;
        _parameters.WallVerticalFriction = _parameters.WallFriction = 0f;
        _parameters.CapsuleBaseRadius = _parameters.CapsuleMinRadius = .3f;
        _parameters.CapsuleBaseHeight = _parameters.CapsuleFastHeight = 2f;
        GameObject player = Create("WallSurfacePlayer", new Vector3(14000f, 20f, 14000f));
        player.SetActive(false);
        player.AddComponent<CharacterController>();
        _motor = player.AddComponent<PlayerMotor>();
        _motor.SetParams(_parameters);
        player.SetActive(true);
        _motor.WallImpact += _impacts.Add;
        _wall = Wall(player.transform.position + new Vector3(.9f, 30f, 0f), new Vector3(1f, 100f, 200f));
        Physics.SyncTransforms();
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = _created.Count - 1; i >= 0; i--)
            if (_created[i] != null) Object.DestroyImmediate(_created[i]);
        _created.Clear();
        Object.DestroyImmediate(_parameters);
    }

    private GameObject Create(string name, Vector3 position)
    {
        var value = new GameObject(name);
        _created.Add(value);
        value.transform.position = position;
        return value;
    }

    private BoxCollider Wall(Vector3 position, Vector3 size)
    {
        var collider = Create("WallSurface", position).AddComponent<BoxCollider>();
        collider.size = size;
        return collider;
    }

    private void StartMoving(float angle = 20f)
    {
        _motor.transform.rotation = Quaternion.Euler(0f, angle, 0f);
        _motor.SetHorizontalSpeed(20f);
    }

    private void Tick() => _motor.Simulate(default, Step, 0f);

    private void EnterWall()
    {
        StartMoving();
        for (int i = 0; i < 15 && !_motor.IsWallSliding; i++) Tick();
        Assert.IsTrue(_motor.IsWallSliding, "必须通过真实碰撞进入滑墙，而不是注入状态");
    }

    [TestCase(false, 20f, false, true)]
    [TestCase(true, 20f, false, false)]
    [TestCase(false, 69f, false, true)]
    [TestCase(true, 69f, false, false)]
    [TestCase(false, 71f, true, false)]
    [TestCase(true, 71f, true, false)]
    [TestCase(false, 90f, true, false)]
    [TestCase(true, 90f, true, false)]
    public void SurfaceTagOnlyChangesSlidingNotImpactThreshold(bool blocked, float angle, bool impact, bool sliding)
    {
        _wall.tag = blocked ? "NoWallSlide" : "Untagged";
        StartMoving(angle);
        for (int i = 0; i < 20; i++) Tick();
        Assert.AreEqual(sliding, _motor.IsWallSliding);
        Assert.AreEqual(impact, _impacts.Count > 0);
        Assert.LessOrEqual(_motor.transform.position.x,
            _wall.bounds.min.x - _motor.GetComponent<CharacterController>().radius + .03f);
        if (impact)
        {
            Assert.GreaterOrEqual(_impacts[0].ApproachAngle, 70f);
            Assert.Greater(_impacts[0].IncomingVelocity.magnitude, 19f, "撞击事件必须保留裁剪前速度供瘫倒消费");
        }
    }

    [Test]
    public void TaggedParentBlocksChildColliderWithoutChangingEnemyLayer()
    {
        GameObject parent = Create("NonSlideParent", Vector3.zero);
        parent.tag = "NoWallSlide";
        _wall.transform.SetParent(parent.transform, true);
        _wall.gameObject.layer = LayerMask.NameToLayer("Enemy");
        StartMoving();
        for (int i = 0; i < 20; i++) Tick();
        Assert.IsFalse(_motor.IsWallSliding);
        Assert.AreEqual(0, _impacts.Count);
        Assert.AreEqual(LayerMask.NameToLayer("Enemy"), _wall.gameObject.layer);
        Assert.IsFalse(_motor.CanExecute(MotorCommandKind.Jump));
    }

    [TestCase("NoWallSlide")]
    [TestCase("NoWallInteraction")]
    public void NewlyTaggedSupportEndsSlidingImmediatelyWithoutImpact(string tag)
    {
        EnterWall();
        _wall.tag = tag;
        Tick();
        Assert.IsFalse(_motor.IsWallSliding, "不可滑墙不能继续享受贴墙宽限");
        Assert.AreEqual(0, _impacts.Count);
    }

    [TestCase("Untagged", true)]
    [TestCase("NoWallSlide", false)]
    [TestCase("NoWallInteraction", false)]
    public void CoplanarSeamRespectsNextSurfaceTag(string tag, bool sliding)
    {
        Vector3 origin = _motor.transform.position;
        _wall.transform.position = origin + new Vector3(.9f, 30f, -9.25f);
        _wall.size = new Vector3(1f, 100f, 21.5f);
        BoxCollider next = Wall(origin + new Vector3(.9f, 30f, 11.5f), new Vector3(1f, 100f, 20f));
        next.tag = tag;
        Physics.SyncTransforms();
        EnterWall();
        for (int i = 0; i < 30; i++) Tick();
        Assert.Greater(_motor.transform.position.z, origin.z + 2f, "必须实际经过两面同法线墙的接缝");
        Assert.AreEqual(sliding, _motor.IsWallSliding);
        Assert.AreEqual(0, _impacts.Count);
    }

    [TestCase(20f)]
    [TestCase(69f)]
    [TestCase(71f)]
    [TestCase(90f)]
    public void LightEnemyBlocksMovementWithoutWallSlideOrImpact(float angle)
    {
        _wall.tag = "NoWallInteraction";
        _wall.gameObject.layer = LayerMask.NameToLayer("Enemy");
        Physics.SyncTransforms();
        StartMoving(angle);
        for (int i = 0; i < 20; i++) Tick();
        Assert.IsFalse(_motor.IsWallSliding);
        Assert.AreEqual(0, _impacts.Count);
        Assert.IsFalse(_motor.CanExecute(MotorCommandKind.Jump));
        Assert.LessOrEqual(_motor.transform.position.x,
            _wall.bounds.min.x - _motor.GetComponent<CharacterController>().radius + .03f);
    }

    [TestCase(20f)]
    [TestCase(90f)]
    public void LightEnemyParentOverridesChildNonSlideTag(float angle)
    {
        GameObject parent = Create("LightEnemyRoot", Vector3.zero);
        parent.tag = "NoWallInteraction";
        _wall.transform.SetParent(parent.transform, true);
        _wall.tag = "NoWallSlide";
        _wall.gameObject.layer = LayerMask.NameToLayer("Enemy");
        Physics.SyncTransforms();
        StartMoving(angle);
        for (int i = 0; i < 20; i++) Tick();
        Assert.IsFalse(_motor.IsWallSliding);
        Assert.AreEqual(0, _impacts.Count);
        Assert.AreEqual(LayerMask.NameToLayer("Enemy"), _wall.gameObject.layer);
    }

    [Test]
    public void SlideFrontGuardBlocksLightEnemyWithoutReportingWallImpact()
    {
        _wall.tag = "NoWallInteraction";
        _motor.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
        Physics.SyncTransforms();
        var contacts = new MovementContacts(_motor.GetComponent<CharacterController>(), _parameters);
        Vector3 origin = _motor.transform.position;
        Vector3 velocity = Vector3.right * 20f;
        contacts.Move(ref velocity, Step, sliding: true, slideForwardExtent: 1f);
        Assert.IsFalse(contacts.HasWall);
        Assert.IsTrue(contacts.HasNonSlideWall);
        Assert.IsFalse(contacts.TryGetWallImpact(70f, out _));
        Assert.Less(velocity.x, .001f);
        Assert.LessOrEqual(_motor.transform.position.x, origin.x + .01f);
    }

    [Test]
    public void LightEnemySideContactDoesNotHideRealFrontWallImpact()
    {
        Vector3 origin = _motor.transform.position;
        _wall.tag = "NoWallInteraction";
        Wall(origin + new Vector3(0f, 30f, 3f), new Vector3(200f, 100f, 1f));
        Physics.SyncTransforms();
        StartMoving();
        for (int i = 0; i < 25; i++) Tick();
        Assert.IsFalse(_motor.IsWallSliding);
        Assert.Greater(_impacts.Count, 0);
        Assert.Greater(Vector3.Dot(_impacts[0].Normal, Vector3.back), .99f);
    }
}
