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
        // 禁止预先吸附地面，验收起身期间真实的下落和胶囊落地。
        data.FindProperty("_groundProbeDistance").floatValue = 0f;
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

    private void BeginGetUp()
    {
        _hitStun.EnterHitStun();
        Assert.IsTrue(_hitStun.IsActive);
        Assert.IsFalse(_controller.enabled);
        Assert.IsFalse(_motor.enabled);
        // 只跳过布娃娃等待时间；起身姿态、Animator 和碰撞切换仍执行实际生产代码。
        typeof(PlayerRagdollHitStun).GetMethod("BeginGetUp", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(_hitStun, null);
        Assert.IsTrue(_hitStun.IsActive);
        int layer = _animator.GetLayerIndex("GetUp");
        Assert.GreaterOrEqual(layer, 0);
        Assert.AreEqual(Animator.StringToHash("GetUp"), _animator.GetCurrentAnimatorStateInfo(layer).shortNameHash);
    }

    private void FixedSteps(int count)
    {
        MethodInfo step = typeof(PlayerMotor).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        for (int i = 0; i < count; i++) step.Invoke(_motor, null);
    }

    [UnityTest]
    public IEnumerator AirborneGetUpFallsWhileAnimationAndInputLockRemainActive()
    {
        BeginGetUp();
        Vector3 start = _root.transform.position;
        TimeManager.SetPaused(false);
        for (int i = 0; i < 6; i++) yield return new WaitForFixedUpdate();
        Assert.Less(_root.transform.position.y, start.y - .02f, "起身动画尚未结束就必须开始下落");
        Assert.Less(_motor.Velocity.y, 0f);
        Assert.IsTrue(_hitStun.IsActive);
        Assert.IsTrue(_controller.enabled);
        Assert.IsTrue(_motor.enabled);
        Assert.IsFalse(_input.GameplayEnabled);
        Assert.IsFalse(_runner.IsConnected);
        Assert.IsTrue(_animator.enabled);
        Assert.That(_root.transform.position.x, Is.EqualTo(start.x).Within(.001f));
        Assert.That(_root.transform.position.z, Is.EqualTo(start.z).Within(.001f));
    }

    [UnityTest]
    public IEnumerator GetUpLandsOnFloorAndRemainsSupportedUntilAnimationEnds()
    {
        var ground = new GameObject("RagdollRecoveryFloor");
        _created.Add(ground);
        ground.transform.position = _root.transform.position + Vector3.down * 1.5f;
        BoxCollider floor = ground.AddComponent<BoxCollider>();
        floor.size = new Vector3(100f, 1f, 100f);
        Physics.SyncTransforms();
        BeginGetUp();
        Assert.Greater(_root.transform.position.y - floor.bounds.max.y, .5f);
        TimeManager.SetPaused(false);
        for (int i = 0; i < 120 && !_motor.IsGrounded; i++) yield return new WaitForFixedUpdate();
        Assert.IsTrue(_motor.IsGrounded, "起身时仍须通过 CharacterController 碰撞落地");
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
        var ground = new GameObject("RagdollRecoveryInputFloor");
        _created.Add(ground);
        ground.transform.position = _root.transform.position + Vector3.down * .5f;
        ground.AddComponent<BoxCollider>().size = new Vector3(100f, 1f, 100f);
        _motor.Params.SlideSpeedRatio = 0f;
        Physics.SyncTransforms();
        BeginGetUp();
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
    public void GetUpKeepsCurrentRagdollHeightWhenGroundProbeMisses()
    {
        Vector3 start = _root.transform.position;
        _hitStun.EnterHitStun();
        var hips = (Transform)typeof(PlayerRagdollHitStun)
            .GetField("_hips", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_hitStun);
        hips.position += Vector3.down * 5f;
        typeof(PlayerRagdollHitStun).GetMethod("BeginGetUp", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(_hitStun, null);
        Assert.That(_root.transform.position.y, Is.EqualTo(start.y - 5f).Within(.005f),
            "探不到地面时应从布娃娃当前位置起身，不能回到碰撞前的根节点高度");
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
