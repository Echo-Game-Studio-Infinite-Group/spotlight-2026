#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using GameJam.Actions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class PlayerHurtLegTests
{
    private readonly List<Object> _created = new List<Object>();
    private GameObject _root;
    private PlayerMotor _motor;
    private PlayerInputReader _input;
    private PlayerActionRunner _runner;
    private ActionAnimatorBridge _bridge;
    private PlayerAnimation _animation;
    private long _tick;

    [SetUp]
    public void SetUp()
    {
        _root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab"));
        _created.Add(_root);
        _motor = _root.GetComponent<PlayerMotor>();
        _input = _root.GetComponent<PlayerInputReader>();
        _runner = _root.GetComponent<PlayerActionRunner>();
        _bridge = _root.GetComponent<ActionAnimatorBridge>();
        _animation = _root.GetComponentInChildren<PlayerAnimation>();
        var parameters = Object.Instantiate(_motor.Params);
        _created.Add(parameters);
        _motor.SetParams(parameters);
        _motor.Teleport(new Vector3(7000f, 1f, 7000f));
        var ground = new GameObject("HurtLegTestGround");
        _created.Add(ground);
        ground.transform.position = new Vector3(7000f, -0.5f, 7000f);
        ground.AddComponent<BoxCollider>().size = new Vector3(200f, 1f, 200f);
        Physics.SyncTransforms();
        for (int i = 0; i < 120 && !_motor.IsGrounded; i++) Tick();
        Assert.IsTrue(_motor.IsGrounded);
    }

    [TearDown]
    public void TearDown()
    {
        _runner?.Disconnect();
        foreach (var item in _created) if (item != null) Object.DestroyImmediate(item);
        _created.Clear();
    }

    private void Tick(bool moving = false, bool sprint = false, ActionInputButtons button = ActionInputButtons.None, Vector2? direction = null)
    {
        Vector2 move = direction ?? (moving ? Vector2.up : Vector2.zero);
        // 注入回调缓存而不消费 ReadFrame，覆盖执行器接管 Motor 时的原始输入读取。
        typeof(PlayerInputReader).GetField("_moveValue", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_input, move);
        typeof(PlayerInputReader).GetField("_focused", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_input, true);
        var edges = button == ActionInputButtons.None ? null : new[]
        {
            new ActionInputEdge(button, ActionInputStep.Edge.Pressed, button, move)
        };
        _runner.SimulateTick(new ActionInputSample(_tick++, button, move, edges),
            new PlayerInputFrame { Move = move, SprintHeld = sprint }, 1f / 60f);
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public void MovingHurtOverridesLegBonesAndPreservesUpperBody(bool big, bool sprint)
    {
        var animator = _bridge.Animator;
        int layer = animator.GetLayerIndex(PlayerAnimation.HurtLegLayerName);
        Assert.Greater(layer, 0);
        for (int i = 0; i < 30; i++) Tick(true, sprint);
        Assert.AreEqual(0f, animator.GetLayerWeight(layer), "普通走跑不能覆盖腿部");
        _animation.PlayHurt(big);
        Assert.AreEqual(0f, animator.GetLayerWeight(layer), "硬直期间保持受伤原姿势");
        for (int i = 0; i < 25; i++) Tick(true, sprint);
        Assert.IsTrue(_animation.IsHurting, "移动不能取消上身受伤");
        Assert.AreEqual(1f, animator.GetLayerWeight(layer));
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName(big ? "HitBody" : "HitStomach"));

        var leg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        var chest = animator.GetBoneTransform(HumanBodyBones.Chest);
        Quaternion walkingLeg = leg.localRotation;
        Quaternion hurtChest = chest.localRotation;
        animator.SetLayerWeight(layer, 0f);
        animator.Update(0f);
        Assert.Greater(Quaternion.Angle(walkingLeg, leg.localRotation), 1f, "真实腿骨必须被行走层改变");
        Assert.Less(Quaternion.Angle(hurtChest, chest.localRotation), 0.1f, "腿部掩码不能覆盖上身");
        for (int i = 0; i < 120; i++) Tick(true, sprint);
        Assert.IsFalse(_animation.IsHurting);
        Assert.IsFalse(_motor.InputSuppressed);
        Assert.AreEqual(0f, animator.GetLayerWeight(layer));
    }

    [Test]
    public void HurtTimeoutStopsTheLegStateOnTheExpiryTick()
    {
        var settings = new SerializedObject(_animation);
        settings.FindProperty("_hurtMaxSeconds").floatValue = .5f;
        settings.ApplyModifiedPropertiesWithoutUndo();
        for (int i = 0; i < 60; i++) Tick(direction: Vector2.right);
        Assert.IsTrue(_motor.IsWalking);
        var animator = _bridge.Animator;
        int layer = animator.GetLayerIndex(PlayerAnimation.HurtLegLayerName);
        _animation.PlayHurt(false);
        int ticks = 0;
        while (_animation.IsHurting && ticks++ < 31) Tick(direction: Vector2.right);
        Assert.IsFalse(_animation.IsHurting, "受伤时间到期必须在本次逻辑步退出");
        Assert.IsFalse(animator.GetBool("IsHurt"));
        Assert.IsFalse(_motor.InputSuppressed);
        Assert.AreEqual(0f, animator.GetLayerWeight(layer));
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(layer).IsName(PlayerAnimation.InactiveLegsStateName));
        for (int i = 0; i < 20; i++) Tick(direction: Vector2.right);
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Walk"));
        Assert.AreEqual(0f, animator.GetLayerWeight(layer));
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(layer).IsName(PlayerAnimation.InactiveLegsStateName));
    }

    [Test]
    public void MovingAttackReusesLegsAndStopsThemAtTheEnd()
    {
        for (int i = 0; i < 60; i++) Tick(true);
        var animator = _bridge.Animator;
        int layer = animator.GetLayerIndex(PlayerAnimation.HurtLegLayerName);
        Tick(true, button: ActionInputButtons.Attack);
        Assert.IsTrue(_root.GetComponent<PlayerCombat>().IsAttackSequenceActive);
        Assert.AreEqual(1f, animator.GetLayerWeight(layer));
        Assert.IsFalse(animator.GetCurrentAnimatorStateInfo(layer).IsName(PlayerAnimation.InactiveLegsStateName));
        for (int i = 0; i < 120; i++) Tick(true);
        Assert.IsFalse(_root.GetComponent<PlayerCombat>().IsAttackSequenceActive);
        Assert.AreEqual(0f, animator.GetLayerWeight(layer));
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(layer).IsName(PlayerAnimation.InactiveLegsStateName));
    }

    [Test]
    public void DisablingAnimationDuringAnAttackReleasesLegOverrideImmediately()
    {
        for (int i = 0; i < 60; i++) Tick(true);
        Tick(true, button: ActionInputButtons.Attack);
        var animator = _bridge.Animator;
        int layer = animator.GetLayerIndex(PlayerAnimation.HurtLegLayerName);
        Assert.AreEqual(1f, animator.GetLayerWeight(layer));
        _animation.enabled = false;
        animator.Update(0f);
        Assert.AreEqual(0f, animator.GetLayerWeight(layer));
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(layer).IsName(PlayerAnimation.InactiveLegsStateName));
    }

    [Test]
    public void StationaryHurtEndsAndRepeatedHitRestarts()
    {
        _animation.PlayHurt(false);
        for (int i = 0; i < 25; i++) Tick();
        Assert.AreEqual(0f, _bridge.Animator.GetLayerWeight(_bridge.Animator.GetLayerIndex(PlayerAnimation.HurtLegLayerName)));
        _animation.PlayHurt(false);
        Tick();
        Assert.IsTrue(_animation.IsHurtLocked);
        Assert.Less(_bridge.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime, 0.1f);
        for (int i = 0; i < 120; i++) Tick();
        Assert.IsFalse(_animation.IsHurting);
        Assert.AreEqual(0, _bridge.Animator.GetInteger("Hurt"));
    }

    [Test]
    public void HurtInterruptsAttackWithoutLosingSequenceOwnership()
    {
        Tick(button: ActionInputButtons.Attack);
        Assert.IsTrue(_root.GetComponent<PlayerCombat>().IsAttacking);
        _animation.PlayHurt(true);
        Tick();
        Assert.IsFalse(_root.GetComponent<PlayerCombat>().IsAttacking);
        Assert.IsTrue(_runner.IsConnected);
        Assert.IsTrue(_bridge.IsOwned);
        Assert.IsTrue(_bridge.Animator.GetCurrentAnimatorStateInfo(0).IsName("HitBody"));
        for (int i = 0; i < 120; i++) Tick();
        Tick(button: ActionInputButtons.Attack);
        Assert.IsTrue(_root.GetComponent<PlayerCombat>().IsAttacking);
    }

    [TestCase(-1f, 0f)]
    [TestCase(1f, 0f)]
    [TestCase(-1f, 1f)]
    [TestCase(1f, 1f)]
    [TestCase(0f, -1f)]
    [TestCase(-1f, -1f)]
    [TestCase(1f, -1f)]
    public void HurtKeepsDirectionalWalkingAndReturnsToBaseWalkBlendTree(float x, float z)
    {
        var direction = new Vector2(x, z).normalized;
        for (int i = 0; i < 60; i++) Tick(direction: direction);
        Assert.IsTrue(_motor.IsWalking);
        _animation.PlayHurt(false);
        for (int i = 0; i < 25; i++) Tick(direction: direction);
        var animator = _bridge.Animator;
        int layer = animator.GetLayerIndex(PlayerAnimation.HurtLegLayerName);
        Assert.IsTrue(_animation.IsHurting);
        Assert.AreEqual(1f, animator.GetLayerWeight(layer));
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(layer).IsName("Walk"), "受伤时腿部也要使用七向行走");
        for (int i = 0; i < 120; i++) Tick(direction: direction);
        Assert.IsFalse(_animation.IsHurting);
        Assert.IsFalse(_motor.InputSuppressed);
        Assert.IsTrue(_motor.IsWalking);
        Assert.That(_motor.HorizontalSpeed, Is.EqualTo(_motor.Params.WalkSpeed).Within(.01f));
        Assert.AreEqual(0f, animator.GetLayerWeight(layer));
        Assert.AreEqual(PlayerAnimation.MotionStateWalk, _animation.CurrentMotionState);
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Walk"), "受伤结束必须回基础层七向混合树");
        Assert.That(animator.GetFloat(PlayerAnimation.WalkXParameter), Is.EqualTo(direction.x).Within(.02f));
        Assert.That(animator.GetFloat(PlayerAnimation.WalkZParameter), Is.EqualTo(direction.y).Within(.02f));
    }
}
#endif
