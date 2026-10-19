#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class EnemyTopSlideTests
{
    private readonly List<GameObject> _created = new List<GameObject>();
    private readonly Vector3 _origin = new Vector3(19000f, 20f, 19000f);
    private MovementParams _parameters;
    private PlayerMotor _motor;
    private GameObject _enemy;
    private const float Step = 1f / 60f;

    [SetUp]
    public void SetUp()
    {
        _parameters = Object.Instantiate(AssetDatabase.LoadAssetAtPath<MovementParams>("Assets/Settings/MovementParams.asset"));
        var floor = Create("TopSlideFloor", _origin + Vector3.down * .5f);
        floor.AddComponent<BoxCollider>().size = new Vector3(20f, 1f, 20f);
        _enemy = Create("TopSlideEnemy", _origin + Vector3.up * .9855607f);
        _enemy.layer = LayerMask.NameToLayer("Enemy"); _enemy.tag = "NoWallInteraction";
        var body = _enemy.AddComponent<CharacterController>();
        body.height = 1.9121397f; body.radius = .38242793f; body.center = Vector3.zero; body.skinWidth = .03f;
        _enemy.AddComponent<EnemyTopSlide>();
        var player = Create("TopSlidePlayer", _origin + Vector3.up * 2.6f);
        player.SetActive(false); player.layer = LayerMask.NameToLayer("Player");
        player.AddComponent<CharacterController>().skinWidth = .08f;
        _motor = player.AddComponent<PlayerMotor>(); _motor.SetParams(_parameters);
        player.SetActive(true); _motor.enabled = false;
        Physics.SyncTransforms();
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = _created.Count - 1; i >= 0; i--) if (_created[i] != null) Object.DestroyImmediate(_created[i]);
        _created.Clear(); Object.DestroyImmediate(_parameters);
    }

    private GameObject Create(string name, Vector3 position)
    {
        var root = new GameObject(name); root.transform.position = position; _created.Add(root); return root;
    }

    private void Tick(int count)
    {
        for (int i = 0; i < count; i++) _motor.Simulate(default, Step, i * Step);
    }

    [TestCase(0f)]
    [TestCase(.2f)]
    [TestCase(.35f)]
    [TestCase(.55f)]
    [TestCase(.72f)]
    public void FallingOntoTopSlidesToFloorWithoutAirControl(float offset)
    {
        _motor.Teleport(_origin + new Vector3(offset, 2.6f, 0f));
        int impacts = 0; _motor.WallImpact += _ => impacts++;
        Tick(240);
        Assert.IsTrue(_motor.IsGrounded, "踩中胶囊顶部后必须脱离并落到真实地面，position=" + (_motor.transform.position - _origin).ToString("F4") + " velocity=" + _motor.Velocity + " flags=" + _motor.GetComponent<CharacterController>().collisionFlags);
        Assert.Less(_motor.transform.position.y - _origin.y, .2f);
        Assert.IsFalse(_motor.IsWallSliding);
        Assert.AreEqual(0, impacts);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BlockedDirectionFindsSideWithoutCrossingWall(bool corner)
    {
        var wall = Create("TopSlideWall", _origin + new Vector3(.75f, 3f, 0f)).AddComponent<BoxCollider>();
        wall.size = new Vector3(.5f, 6f, 20f);
        BoxCollider second = null;
        if (corner)
        {
            second = Create("TopSlideCorner", _origin + new Vector3(0f, 3f, .75f)).AddComponent<BoxCollider>();
            second.size = new Vector3(20f, 6f, .5f);
        }
        Physics.SyncTransforms();
        var player = _motor.GetComponent<CharacterController>();
        for (int i = 0; i < 300; i++)
        {
            Tick(1);
            Assert.LessOrEqual(player.bounds.max.x, wall.bounds.min.x + player.skinWidth + .02f);
            if (second != null) Assert.LessOrEqual(player.bounds.max.z, second.bounds.min.z + player.skinWidth + .02f);
        }
        Assert.IsTrue(_motor.IsGrounded, "靠墙时也应找到侧向出口");
        Assert.Less(_motor.transform.position.y - _origin.y, .2f);
    }

    [Test]
    public void EnemyWithoutComponentKeepsExistingCollision()
    {
        Object.DestroyImmediate(_enemy.GetComponent<EnemyTopSlide>());
        Tick(180);
        Assert.Greater(_motor.transform.position.y - _origin.y, 1.8f);
        Assert.IsFalse(_motor.IsGrounded, "未接入的敌人应保留原行为，便于本次只试近战");
    }

    [Test]
    public void RisingPlayerIsNotPushedFromAbove()
    {
        var body = _motor.GetComponent<CharacterController>();
        Assert.IsFalse(_enemy.GetComponent<EnemyTopSlide>().TryGetSlideVelocity(body, Vector3.up, Vector3.up * 4f, out _));
    }

    [Test]
    public void PausedMovementDoesNotSlide()
    {
        _motor.Teleport(_origin + Vector3.up * 2.0216f);
        Vector3 before = _motor.transform.position;
        for (int i = 0; i < 30; i++) _motor.Simulate(default, 0f, i * Step);
        Assert.AreEqual(before, _motor.transform.position);
    }

    [Test]
    public void MeleeAndRangedPrefabsHaveTopSlide()
    {
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/EnemyTest.prefab").GetComponent<EnemyTopSlide>());
        Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/EnemyTestRange.prefab").GetComponent<EnemyTopSlide>());
        Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/EnemyHeavy.prefab").GetComponent<EnemyTopSlide>());
    }
}
#endif