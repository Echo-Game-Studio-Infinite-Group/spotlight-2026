using System.Collections.Generic;
using GameJam.Actions;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class TerrainMovementTests
{
    private readonly List<Object> _created = new List<Object>();
    private PlayerMotor _motor;
    private MovementParams _parameters;
    private Vector3 _origin;
    private int _impacts;
    private float _time;

    [SetUp]
    public void SetUp()
    {
        _origin = new Vector3(16000f, 20f, 16000f);
        _parameters = ScriptableObject.CreateInstance<MovementParams>(); _created.Add(_parameters);
        _parameters.GroundFriction = _parameters.RunAccel = _parameters.SlideDecel = 0f;
        GameObject root = Create("TerrainPlayer", _origin); root.SetActive(false);
        CharacterController controller = root.AddComponent<CharacterController>();
        controller.skinWidth = .03f; controller.stepOffset = .3f; controller.slopeLimit = 45f;
        _motor = root.AddComponent<PlayerMotor>(); _motor.SetParams(_parameters);
        root.SetActive(true);
        _motor.WallImpact += _ => _impacts++;
        _impacts = 0; _time = 0f;
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = _created.Count - 1; i >= 0; i--) Object.DestroyImmediate(_created[i]);
        _created.Clear();
    }

    private GameObject Create(string name, Vector3 position)
    {
        var value = new GameObject(name); value.transform.position = position; _created.Add(value); return value;
    }

    private void Box(string name, Vector3 center, Vector3 size, bool trigger = false)
    {
        BoxCollider collider = Create(name, _origin + center).AddComponent<BoxCollider>();
        collider.size = size; collider.isTrigger = trigger;
    }

    private void Stairs(bool descending, float rise = .2f, float tread = .6f, int steps = 12)
    {
        Box("Approach", new Vector3(0f, -.5f, -5f), new Vector3(8f, 1f, 12f));
        for (int i = 0; i < steps; i++)
        {
            float top = (descending ? -1 : 1) * (i + 1) * rise;
            Box("Step" + i, new Vector3(0f, top - 2f, 1f + (i + .5f) * tread), new Vector3(8f, 4f, tread));
        }
        Physics.SyncTransforms(); Step(); Step(); Assert.IsTrue(_motor.IsGrounded);
    }

    private void Step(float dt = 1f / 60f, bool jump = false)
    {
        _time += dt;
        _motor.Simulate(new PlayerInputFrame { SlideHeld = _motor.IsSliding, JumpPressed = jump, JumpTime = _time }, dt, _time);
    }

    [TestCase(.01f, 6f)]
    [TestCase(.05f, 15f)]
    [TestCase(.1f, 30f)]
    [TestCase(.1f, 15f)]
    [TestCase(.1f, 6f)]
    public void FallingNearTheFloorAtStairEntryDoesNotTriggerWallImpact(float height, float speed)
    {
        _motor.GetComponent<CharacterController>().skinWidth = .08f;
        Stairs(false);
        _motor.Teleport(_origin + Vector3.forward * .65f + Vector3.up * height);
        _motor.SetHorizontalSpeed(speed);
        for (int i = 0; i < Mathf.CeilToInt(6f / speed * 60f); i++)
        {
            Step();
            Assert.AreEqual(0, _impacts, "近地落到楼梯入口不能算撞墙，tick=" + i);
            Assert.That(_motor.HorizontalSpeed, Is.EqualTo(speed).Within(.03f));
        }
        Assert.Greater(_motor.transform.position.y, _origin.y + 1.5f);
    }

    [TestCase(.01f, .29f, .72f, 30f)]
    [TestCase(.05f, .29f, .72f, 30f)]
    [TestCase(.05f, .29f, .72f, 15f)]
    public void LowStairRiserDuringLandingDoesNotEmitRagdollImpact(float height, float rise, float approach, float speed)
    {
        _motor.GetComponent<CharacterController>().skinWidth = .08f;
        Stairs(false, rise);
        _motor.Teleport(_origin + Vector3.forward * approach + Vector3.up * height);
        _motor.SetHorizontalSpeed(speed);
        for (int i = 0; i < 6; i++)
        {
            Step();
            Assert.AreEqual(0, _impacts, "可跨越台阶不应触发布娃娃撞击，tick=" + i);
            Assert.That(_motor.HorizontalSpeed, Is.EqualTo(speed).Within(.03f));
        }
    }

    [TestCase(6f, false)]
    [TestCase(15f, false)]
    [TestCase(30f, false)]
    [TestCase(6f, true)]
    [TestCase(30f, true)]
    public void AscendingStairsKeepGroundAndSpeedWithoutWallImpact(float speed, bool slide)
    {
        Stairs(false); _motor.SetHorizontalSpeed(speed);
        if (slide) Assert.IsTrue(_motor.TryExecute(MotorCommandKind.EnterSlide, 0f));
        for (int i = 0; i < Mathf.CeilToInt(6f / speed * 60f); i++)
        {
            Step(); Assert.IsTrue(_motor.IsGrounded, "上阶不应脱地，tick=" + i);
            Assert.That(_motor.HorizontalSpeed, Is.EqualTo(speed).Within(.03f), "台阶不应裁速，tick=" + i);
        }
        Assert.AreEqual(0, _impacts);
        Assert.Greater(_motor.transform.position.y, _origin.y + 1.5f);
        if (slide) Assert.IsTrue(_motor.IsSliding);
    }

    [TestCase(6f, false, 1f)]
    [TestCase(15f, false, 1f)]
    [TestCase(30f, false, 1f)]
    [TestCase(6f, true, 1f)]
    [TestCase(30f, true, .25f)]
    public void DescendingStairsStayGroundedAndDoNotRefreshLandingWindow(float speed, bool slide, float rate)
    {
        Stairs(true); _motor.SetHorizontalSpeed(speed);
        if (slide) Assert.IsTrue(_motor.TryExecute(MotorCommandKind.EnterSlide, 0f));
        for (int i = 0; i < Mathf.CeilToInt(6f / speed * 60f / rate); i++)
        {
            Step(rate / 60f); Assert.IsTrue(_motor.IsGrounded, "下阶不应脱地，tick=" + i);
            if (_time > .3f) Assert.AreEqual(0f, _motor.FrictionWindowRemaining, "连续下阶不能刷新落地窗口");
        }
        Assert.Less(_motor.transform.position.y, _origin.y - 1.5f);
        Assert.AreEqual(0, _impacts);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(speed).Within(.03f));
    }

#if UNITY_EDITOR
    [TestCase(3f, false)]
    [TestCase(6f, false)]
    [TestCase(15f, false)]
    [TestCase(30f, false)]
    [TestCase(6f, true)]
    [TestCase(15f, true)]
    [TestCase(30f, true)]
    public void FullPlayerPrefabClimbsStairsWithoutWallImpact(float speed, bool slide)
    {
        GameObject prior = _motor.gameObject; _created.Remove(prior); Object.DestroyImmediate(prior);
        GameObject player = Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab"));
        _created.Add(player);
        foreach (Camera camera in player.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        player.GetComponent<GameJam.Actions.PlayerActionRunner>().enabled = false;
        player.GetComponent<PlayerRagdollHitStun>().enabled = false;
        _motor = player.GetComponent<PlayerMotor>(); _motor.SetParams(_parameters); _motor.Teleport(_origin);
        _motor.transform.rotation = Quaternion.identity;
        _motor.WallImpact += _ => _impacts++;
        Stairs(false); _motor.SetHorizontalSpeed(speed);
        if (slide) Assert.IsTrue(_motor.TryExecute(MotorCommandKind.EnterSlide, 0f));
        for (int i = 0; i < Mathf.CeilToInt(6f / speed * 60f); i++)
        {
            Step();
            string trace = "完整玩家上楼，tick=" + i + ", position=" + (player.transform.position - _origin);
            Assert.AreEqual(0, _impacts, trace);
            Assert.IsTrue(_motor.IsGrounded, trace);
            Assert.That(_motor.HorizontalSpeed, Is.EqualTo(speed).Within(.03f), trace);
        }
        Assert.Greater(player.transform.position.y, _origin.y + 1.5f);
    }

    [TestCase(6f, false)]
    [TestCase(15f, false)]
    [TestCase(25f, false)]
    [TestCase(30f, false)]
    [TestCase(6f, true)]
    [TestCase(15f, true)]
    [TestCase(30f, true)]
    public void ProductionSkinWidthClimbsStairsWithoutWallImpact(float speed, bool slide)
    {
        var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        _motor.GetComponent<CharacterController>().skinWidth = prefab.GetComponent<CharacterController>().skinWidth;
        Stairs(false); _motor.SetHorizontalSpeed(speed);
        if (slide) Assert.IsTrue(_motor.TryExecute(MotorCommandKind.EnterSlide, 0f));
        for (int i = 0; i < Mathf.CeilToInt(6f / speed * 60f); i++)
        {
            Step();
            Assert.AreEqual(0, _impacts, "生产胶囊不能把台阶判作撞墙，tick=" + i);
            Assert.IsTrue(_motor.IsGrounded, "生产胶囊上阶不能脱地，tick=" + i);
            Assert.That(_motor.HorizontalSpeed, Is.EqualTo(speed).Within(.03f), "生产胶囊上阶不能裁速，tick=" + i);
        }
        Assert.Greater(_motor.transform.position.y, _origin.y + 1.5f);
    }
#endif

    [TestCase(20f, false)]
    [TestCase(40f, false)]
    [TestCase(20f, true)]
    [TestCase(40f, true)]
    [TestCase(-20f, false)]
    [TestCase(-40f, true)]
    public void WalkableSlopesKeepGroundAndHorizontalSpeed(float angle, bool slide)
    {
        Box("Approach", new Vector3(0f, -.5f, -5f), new Vector3(8f, 1f, 12f));
        GameObject slope = Create("Slope", _origin + new Vector3(0f, -.25f, 1f));
        slope.transform.rotation = Quaternion.Euler(-angle, 0f, 0f);
        BoxCollider collider = slope.AddComponent<BoxCollider>();
        collider.size = new Vector3(8f, .5f, 20f); collider.center = new Vector3(0f, -.25f, 10f);
        // 旋转绕坡面起点，避免测试夹具自己在接缝处制造台阶。
        slope.transform.position = _origin + Vector3.forward;
        Physics.SyncTransforms(); Step(); Step(); _motor.SetHorizontalSpeed(15f);
        if (slide) Assert.IsTrue(_motor.TryExecute(MotorCommandKind.EnterSlide, 0f));
        for (int i = 0; i < 30; i++)
        {
            Step(); Assert.IsTrue(_motor.IsGrounded, "坡面连续接地，tick=" + i);
            Assert.That(_motor.HorizontalSpeed, Is.EqualTo(15f).Within(.03f));
        }
        Assert.AreEqual(0, _impacts);
    }

    [Test]
    public void JumpOnStairsIsNotSnappedBackAndPauseDoesNotMove()
    {
        Stairs(false); _motor.SetHorizontalSpeed(6f); Step(); Step();
        Vector3 before = _motor.transform.position;
        Step(0f); Assert.AreEqual(before, _motor.transform.position);
        Step(jump: true); Assert.IsFalse(_motor.IsGrounded); Assert.Greater(_motor.Velocity.y, 0f);
        Step(); Assert.IsFalse(_motor.IsGrounded); Assert.Greater(_motor.transform.position.y, before.y);
    }

    [Test]
    public void LargeDropEntersAirAndHighObstacleStillReportsWallImpact()
    {
        Stairs(false, .6f); _motor.SetHorizontalSpeed(15f);
        for (int i = 0; i < 30; i++) Step();
        Assert.Greater(_impacts, 0); Assert.Less(_motor.transform.position.z - _origin.z, 1f);
        _motor.Teleport(_origin); _motor.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
        Physics.SyncTransforms(); Step(); Step(); _motor.SetHorizontalSpeed(15f);
        for (int i = 0; i < 70; i++) Step();
        Assert.IsFalse(_motor.IsGrounded); Assert.Less(_motor.transform.position.y, _origin.y - .5f);
    }

    [TestCase(30f)]
    [TestCase(-30f)]
    public void DiagonalAscentKeepsMomentum(float yaw)
    {
        Stairs(false); _motor.transform.rotation = Quaternion.Euler(0f, yaw, 0f); _motor.SetHorizontalSpeed(15f);
        for (int i = 0; i < 24; i++) { Step(); Assert.IsTrue(_motor.IsGrounded); }
        Assert.AreEqual(0, _impacts); Assert.That(_motor.HorizontalSpeed, Is.EqualTo(15f).Within(.03f));
        Assert.Greater(_motor.transform.position.y, _origin.y + 1f);
    }

    [Test]
    public void LowRoofPreventsStepWithoutPenetratingCeiling()
    {
        Stairs(false); Box("LowRoof", new Vector3(0f, 1.82f, 4f), new Vector3(8f, .2f, 10f));
        Physics.SyncTransforms(); _motor.SetHorizontalSpeed(6f);
        for (int i = 0; i < 40; i++) Step();
        Assert.Less(_motor.HorizontalSpeed, .1f);
        Assert.Less(_motor.transform.position.y, _origin.y + .05f);
        Assert.Less(_motor.transform.position.z, _origin.z + 1f);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SteepRampCannotBeTreatedAsWalkableGroundOrStep(bool slide)
    {
        Box("Approach", new Vector3(0f, -.5f, -5f), new Vector3(8f, 1f, 12f));
        GameObject slope = Create("SteepSlope", _origin + Vector3.forward);
        slope.transform.rotation = Quaternion.Euler(-65f, 0f, 0f);
        BoxCollider collider = slope.AddComponent<BoxCollider>(); collider.size = new Vector3(8f, .5f, 20f);
        collider.center = new Vector3(0f, -.25f, 10f);
        Physics.SyncTransforms(); Step(); Step(); _motor.SetHorizontalSpeed(15f);
        if (slide) Assert.IsTrue(_motor.TryExecute(MotorCommandKind.EnterSlide, 0f));
        for (int i = 0; i < 40; i++) Step();
        Assert.Less(_motor.transform.position.y, _origin.y + .15f);
        Assert.Less(_motor.transform.position.z, _origin.z + 1.1f);
        Assert.Less(_motor.HorizontalSpeed, .1f);
    }

    [Test]
    public void CameraStepOffsetSmoothsPhysicalStepAndTeleportClearsIt()
    {
        Stairs(false);
        Transform target = Create("TerrainCameraTarget", Vector3.zero).transform;
        target.SetParent(_motor.transform, false); target.localPosition = Vector3.up * 1.2f;
        PlayerCameraRig rig = _motor.gameObject.AddComponent<PlayerCameraRig>(); rig.enabled = false;
        rig.Configure(_motor, null, target, null); rig.enabled = true;
        _motor.SetHorizontalSpeed(6f);
        bool stepped = false;
        for (int i = 0; i < 15; i++)
        {
            float previousY = target.position.y; Step();
            if (_motor.GroundStepHeight > .1f)
            {
                stepped = true;
                Assert.That(target.position.y, Is.EqualTo(previousY).Within(.01f), "胶囊跨阶时镜头目标不能同步跳高");
                rig.UpdateHeight(1f / 60f);
                Assert.That(target.position.y - previousY, Is.InRange(0f, .1f));
                break;
            }
            rig.UpdateHeight(1f / 60f);
        }
        Assert.IsTrue(stepped);
        _motor.Teleport(_origin + Vector3.up * 5f);
        Assert.That(target.localPosition.y, Is.EqualTo(1.2f).Within(.001f));
    }

#if UNITY_EDITOR
    [TestCase(false, false, 6f, 0f)]
    [TestCase(false, true, 30f, 0f)]
    [TestCase(true, false, 30f, 0f)]
    [TestCase(true, true, 15f, 0f)]
    [TestCase(false, false, 15f, 15f)]
    [TestCase(false, true, 30f, -15f)]
    [TestCase(true, false, 15f, 15f)]
    [TestCase(true, true, 30f, -15f)]
    public void ActualWhiteboxStairsRemainTraversable(bool descending, bool slide, float speed, float yaw)
    {
        var player = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        _motor.GetComponent<CharacterController>().skinWidth = player.GetComponent<CharacterController>().skinWidth;
        GameObject prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Maps/TeamWhitebox/Prefabs/Stairs (5).prefab");
        GameObject stairs = Object.Instantiate(prefab); _created.Add(stairs);
        stairs.transform.SetPositionAndRotation(_origin + new Vector3(-6f, 0f, 1f), Quaternion.Euler(0f, 180f, 0f));
        Box("Approach", new Vector3(0f, -.5f, -5f), new Vector3(8f, 1f, 12f));
        if (descending)
        {
            _motor.Teleport(_origin + new Vector3(0f, 6.454f, 26.1f));
        }
        _motor.transform.rotation = Quaternion.Euler(0f, (descending ? 180f : 0f) + yaw, 0f);
        Physics.SyncTransforms(); for (int i = 0; i < 5; i++) Step();
        Assert.IsTrue(_motor.IsGrounded); _motor.SetHorizontalSpeed(speed);
        if (slide) Assert.IsTrue(_motor.TryExecute(MotorCommandKind.EnterSlide, 0f));
        for (int i = 0; i < Mathf.RoundToInt(20f / speed * 60f); i++)
        {
            Step(); Assert.IsTrue(_motor.IsGrounded, "真实楼梯连续接地，tick=" + i);
            Assert.That(_motor.HorizontalSpeed, Is.EqualTo(speed).Within(.03f));
        }
        Assert.AreEqual(0, _impacts);
        Assert.That(_motor.transform.position.y - _origin.y, descending ? Is.LessThan(2f) : Is.GreaterThan(4f));
    }
#endif
}
