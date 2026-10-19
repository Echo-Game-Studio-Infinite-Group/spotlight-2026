#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GameJam.Actions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class PlayerRagdollRecoveryTests
{
    private readonly List<Object> _created = new List<Object>();
    private readonly object _timeOwner = new object();
    private GameObject _root;
    private PlayerRagdollHitStun _hitStun;
    private PlayerMotor _motor;
    private CharacterController _controller;
    private PlayerActionRunner _runner;
    private PlayerInputReader _input;
    private Animator _animator;

    private sealed class HeldInput : IPlayerInput
    {
        public PlayerInputFrame ReadFrame() => new PlayerInputFrame
        {
            Move = Vector2.right, JumpPressed = true, JumpTime = TimeManager.UnscaledTime,
            SlidePressed = true, SlideHeld = true, SprintHeld = true
        };
        public void Clear() { }
    }

    [SetUp]
    public void SetUp()
    {
        if (Object.FindObjectOfType<TimeManager>() == null)
        {
            var clock = new GameObject("RagdollRecoveryClock");
            _created.Add(clock);
            clock.AddComponent<TimeManager>();
        }
        TimeManager.ClearSlowMotion();
        _root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab"));
        _created.Add(_root);
        foreach (Camera camera in _root.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        _motor = _root.GetComponent<PlayerMotor>();
        var parameters = Object.Instantiate(_motor.Params);
        _created.Add(parameters);
        _motor.SetParams(parameters);
        _motor.Teleport(new Vector3(17000f, 30f, 17000f));
        _controller = _root.GetComponent<CharacterController>();
        _runner = _root.GetComponent<PlayerActionRunner>();
        _input = _root.GetComponent<PlayerInputReader>();
        _animator = _root.GetComponent<Animator>();
        _hitStun = _root.GetComponent<PlayerRagdollHitStun>();
        var data = new SerializedObject(_hitStun);
        data.FindProperty("_getUpTargetSeconds").floatValue = 3f;
        data.ApplyModifiedPropertiesWithoutUndo();
        Physics.SyncTransforms();
        // 玩家预制体自带 TimeManager，须在它 Awake 接管静态入口后设置暂停。
        TimeManager.SetPaused(true);
    }

    [TearDown]
    public void TearDown()
    {
        TimeManager.Release(TimeManager.TimeLayer.Player, _timeOwner);
        _hitStun?.ExitHitStun();
        _runner?.Disconnect();
        for (int i = _created.Count - 1; i >= 0; i--)
            if (_created[i] != null) Object.DestroyImmediate(_created[i]);
        _created.Clear();
        TimeManager.SetPaused(false);
        TimeManager.ClearSlowMotion();
    }

    private GameObject CreateFloor(float top)
    {
        var ground = new GameObject("RagdollRecoveryFloor");
        _created.Add(ground);
        ground.transform.position = new Vector3(_root.transform.position.x, top - .5f, _root.transform.position.z);
        ground.AddComponent<BoxCollider>().size = new Vector3(100f, 1f, 100f);
        Physics.SyncTransforms();
        return ground;
    }

    private bool GettingUp => (bool)typeof(PlayerRagdollHitStun)
        .GetField("_inGetUp", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_hitStun);

    private void TryGetUp() => typeof(PlayerRagdollHitStun)
        .GetMethod("BeginGetUp", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_hitStun, null);

    private void BeginGetUp(bool keepGround = false)
    {
        _hitStun.EnterHitStun();
        Assert.IsTrue(_hitStun.IsActive);
        Assert.IsFalse(_controller.enabled);
        Assert.IsFalse(_motor.enabled);
        float bottom = float.PositiveInfinity;
        foreach (Rigidbody body in _root.GetComponent<Animator>().GetBoneTransform(HumanBodyBones.Hips)
            .GetComponentsInChildren<Rigidbody>())
            bottom = Mathf.Min(bottom, body.GetComponent<Collider>().bounds.min.y);
        GameObject floor = CreateFloor(bottom - .02f);
        // 只跳过布娃娃等待时间；仍需真实支撑面才能进入生产代码的起身阶段。
        TryGetUp();
        Assert.IsTrue(GettingUp);
        Assert.IsTrue(_hitStun.IsActive);
        int layer = _animator.GetLayerIndex("GetUp");
        Assert.GreaterOrEqual(layer, 0);
        Assert.AreEqual(Animator.StringToHash("GetUp"), _animator.GetCurrentAnimatorStateInfo(layer).shortNameHash);
        // 验收起身后支撑面消失时仍有重力，不能借空中开始起身绕过落地条件。
        if (!keepGround) Object.DestroyImmediate(floor);
    }

    private void FixedSteps(int count)
    {
        MethodInfo step = typeof(PlayerMotor).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        for (int i = 0; i < count; i++) step.Invoke(_motor, null);
    }

    [UnityTest]
    public IEnumerator AirborneRagdollKeepsFallingAfterTimerWithoutStartingGetUp()
    {
        _hitStun.EnterHitStun();
        Transform hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
        Vector3 start = hips.position;
        TimeManager.SetPaused(false);
        float deadline = Time.time + 1.2f;
        while (Time.time < deadline) yield return new WaitForFixedUpdate();
        Assert.Less(hips.position.y, start.y - .02f, "等待落地期间骨骼必须继续受重力驱动");
        Assert.IsTrue(_hitStun.IsActive);
        Assert.IsFalse(GettingUp);
        Assert.IsFalse(_controller.enabled);
        Assert.IsFalse(_motor.enabled);
        Assert.IsFalse(_input.GameplayEnabled);
        Assert.IsFalse(_runner.IsConnected);
        Assert.IsFalse(_animator.enabled);
        foreach (Rigidbody body in hips.GetComponentsInChildren<Rigidbody>()) Assert.IsFalse(body.isKinematic);
    }

    [UnityTest]
    public IEnumerator RagdollWaitsForLandingThenGetUpRemainsSupportedUntilAnimationEnds()
    {
        BoxCollider floor = CreateFloor(_root.transform.position.y - 2f).GetComponent<BoxCollider>();
        _hitStun.EnterHitStun();
        typeof(PlayerRagdollHitStun).GetField("_ragdollEndAt", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(_hitStun, Time.time);
        TryGetUp();
        Assert.IsFalse(GettingUp, "地板距离不到原来的 3 米探地范围也不能提前起身");
        TimeManager.SetPaused(false);
        for (int i = 0; i < 120 && !GettingUp; i++) yield return new WaitForFixedUpdate();
        Assert.IsTrue(GettingUp, "布娃娃接触地面后应自动开始起身");
        for (int i = 0; i < 6 && !_motor.IsGrounded; i++) yield return new WaitForFixedUpdate();
        Assert.IsTrue(_motor.IsGrounded);
        Assert.IsTrue(_hitStun.IsActive);
        Assert.That(_root.transform.position.y, Is.EqualTo(floor.bounds.max.y).Within(_controller.skinWidth + .01f));
        float landedY = _root.transform.position.y;
        for (int i = 0; i < 10; i++) yield return new WaitForFixedUpdate();
        Assert.That(_root.transform.position.y, Is.EqualTo(landedY).Within(.005f));
        Assert.IsFalse(_input.GameplayEnabled);
    }

    [TestCase(1f)]
    [TestCase(.25f)]
    [TestCase(0f)]
    public void RecoveryGravityUsesPlayerClockAndPause(float playerRate)
    {
        BeginGetUp();
        Assert.IsTrue(_motor.enabled);
        Vector3 start = _root.transform.position;
        FixedSteps(6);
        Assert.AreEqual(start, _root.transform.position, "暂停时不能推进起身重力");
        Assert.AreEqual(Vector3.zero, _motor.Velocity);
        TimeManager.SetPaused(false);
        TimeManager.Apply(TimeManager.TimeLayer.Player, playerRate, owner: _timeOwner);
        float dt = TimeManager.PlayerFixedDeltaTime;
        FixedSteps(6);
        Assert.That(_motor.Velocity.y, Is.EqualTo(-_motor.Params.Gravity * dt * 6f).Within(.0001f));
        Assert.That(start.y - _root.transform.position.y,
            Is.EqualTo(_motor.Params.Gravity * dt * dt * 21f).Within(.005f));
    }

    [Test]
    public void GetUpRejectsHeldMovementJumpSlideAndTurn()
    {
        _motor.Params.SlideSpeedRatio = 0f;
        BeginGetUp(keepGround: true);
        Assert.IsTrue(_motor.enabled);
        TimeManager.SetPaused(false);
        FixedSteps(2);
        Assert.IsTrue(_motor.IsGrounded);
        Assert.IsTrue(_motor.CanExecute(MotorCommandKind.Jump));
        Assert.IsTrue(_motor.CanExecute(MotorCommandKind.EnterSlide));
        _motor.SetInput(new HeldInput());
        Vector3 start = _root.transform.position;
        Quaternion rotation = _root.transform.rotation;
        FixedSteps(6);
        Assert.AreEqual(0f, _motor.HorizontalSpeed);
        Assert.AreEqual(0, _motor.JumpCount);
        Assert.IsFalse(_motor.IsSliding);
        Assert.AreEqual(rotation, _root.transform.rotation);
        Assert.AreEqual(start.x, _root.transform.position.x);
        Assert.AreEqual(start.z, _root.transform.position.z);
        Assert.That(_root.transform.position.y, Is.EqualTo(start.y).Within(.005f));
        Assert.IsTrue(_motor.IsGrounded);
    }

    [Test]
    public void FinishingRecoveryReturnsControlWithoutClearingFallVelocity()
    {
        BeginGetUp();
        Assert.IsTrue(_motor.enabled);
        TimeManager.SetPaused(false);
        FixedSteps(6);
        float speedY = _motor.Velocity.y;
        Assert.Less(speedY, 0f);
        _hitStun.ExitHitStun();
        Assert.IsFalse(_hitStun.IsActive);
        Assert.IsTrue(_controller.enabled);
        Assert.IsTrue(_motor.enabled);
        Assert.IsTrue(_input.GameplayEnabled);
        Assert.IsTrue(_runner.IsConnected);
        Assert.AreEqual(speedY, _motor.Velocity.y);
        _runner.SimulateTick(new ActionInputSample(0), default, 1f / 60f);
        Assert.That(_motor.Velocity.y, Is.EqualTo(speedY - _motor.Params.Gravity / 60f).Within(.0001f));
    }

    [Test]
    public void MissingGroundDoesNotFreezeBonesOrMoveRootToRagdoll()
    {
        Vector3 start = _root.transform.position;
        _hitStun.EnterHitStun();
        var hips = (Transform)typeof(PlayerRagdollHitStun)
            .GetField("_hips", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_hitStun);
        hips.position += Vector3.down * 5f;
        Physics.SyncTransforms();
        TryGetUp();
        Assert.IsFalse(GettingUp);
        Assert.AreEqual(start, _root.transform.position, "未落地时不能提前对齐起身位置");
        Assert.IsFalse(_animator.enabled);
        Assert.IsFalse(hips.GetComponent<Rigidbody>().isKinematic);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void TriggersAndExcludedLayersDoNotCountAsGround(bool trigger)
    {
        _hitStun.EnterHitStun();
        float bottom = float.PositiveInfinity;
        foreach (Rigidbody body in _animator.GetBoneTransform(HumanBodyBones.Hips).GetComponentsInChildren<Rigidbody>())
            bottom = Mathf.Min(bottom, body.GetComponent<Collider>().bounds.min.y);
        GameObject floor = CreateFloor(bottom - .02f);
        if (trigger) floor.GetComponent<Collider>().isTrigger = true;
        else _motor.Params.CollisionMask = 0;
        Physics.SyncTransforms();
        TryGetUp();
        Assert.IsFalse(GettingUp);
        Assert.IsFalse(_animator.enabled);
    }

    [TestCase(30f, true)]
    [TestCase(80f, false)]
    [TestCase(90f, false)]
    public void OnlyWalkableSlopesAllowGetUp(float angle, bool expected)
    {
        _hitStun.EnterHitStun();
        Rigidbody hips = _animator.GetBoneTransform(HumanBodyBones.Hips).GetComponent<Rigidbody>();
        // 固定查询一个真实骨骼形状，排除其他肢体位于斜面另一侧的干扰。
        FieldInfo bodiesField = typeof(PlayerRagdollHitStun).GetField("_ragdollBodies", BindingFlags.Instance | BindingFlags.NonPublic);
        var bodies = (Rigidbody[])bodiesField.GetValue(_hitStun);
        bodiesField.SetValue(_hitStun, new[] { hips });
        Vector3 normal = Quaternion.Euler(0f, 0f, angle) * Vector3.up;
        Collider bone = hips.GetComponent<Collider>();
        Vector3 contact = bone.ClosestPoint(bone.bounds.center - normal * 100f);
        GameObject floor = CreateFloor(0f);
        floor.transform.rotation = Quaternion.Euler(0f, 0f, angle);
        floor.transform.position = contact - normal * .505f;
        Physics.SyncTransforms();
        try { TryGetUp(); }
        finally { bodiesField.SetValue(_hitStun, bodies); }
        Assert.AreEqual(expected, GettingUp, "竖墙和超出最大可行走角度的斜面不能算落地");
    }

    [Test]
    public void RecoveryPreservesAnAlreadyDisabledMotor()
    {
        _motor.enabled = false;
        BeginGetUp();
        Assert.IsFalse(_motor.enabled);
        _hitStun.ExitHitStun();
        Assert.IsFalse(_motor.enabled);
    }
}
#endif
