#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using GameJam.Actions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class SlideAnimationTests
{
    private readonly List<Object> _created = new List<Object>();
    private GameObject _root;
    private PlayerMotor _motor;
    private PlayerActionRunner _runner;
    private PlayerAnimation _animation;
    private Animator _animator;
    private long _tick;

    [SetUp]
    public void SetUp()
    {
        TimeManager.ClearSlowMotion();
        _root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab"));
        _created.Add(_root);
        foreach (Camera camera in _root.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        _motor = _root.GetComponent<PlayerMotor>();
        MovementParams parameters = Object.Instantiate(_motor.Params); _created.Add(parameters);
        parameters.SlideDecel = 2f; _motor.SetParams(parameters);
        _runner = _root.GetComponent<PlayerActionRunner>();
        _animation = _root.GetComponentInChildren<PlayerAnimation>();
        _animator = _animation.Animator;
        _motor.Teleport(new Vector3(8000f, 10f, 8000f));
        var ground = new GameObject("SlideAnimationGround"); _created.Add(ground);
        ground.transform.position = _root.transform.position + Vector3.down * .5f;
        ground.AddComponent<BoxCollider>().size = new Vector3(1000f, 1f, 1000f);
        Physics.SyncTransforms(); _tick = 0; Tick(); Tick();
        Assert.IsTrue(_motor.IsGrounded);
        _motor.SetHorizontalSpeed(12f);
        Tick(ActionInputButtons.Slide);
        Assert.IsTrue(_motor.IsSliding);
    }

    [TearDown]
    public void TearDown()
    {
        _runner?.Disconnect();
        for (int i = _created.Count - 1; i >= 0; i--)
            if (_created[i] != null) Object.DestroyImmediate(_created[i]);
        _created.Clear();
        TimeManager.ClearSlowMotion();
    }

    private void Tick(ActionInputButtons buttons = ActionInputButtons.None, float dt = 1f / 60f, bool slideHeld = true)
    {
        ActionInputButtons held = buttons | (slideHeld ? ActionInputButtons.Slide : ActionInputButtons.None);
        ActionInputEdge[] edges = buttons == ActionInputButtons.None ? null :
            new[] { new ActionInputEdge(buttons, ActionInputStep.Edge.Pressed, held) };
        _runner.SimulateTick(new ActionInputSample(_tick++, held, Vector2.zero, edges),
            new PlayerInputFrame { SlideHeld = slideHeld }, dt);
    }

    private void Advance(int ticks)
    { for (int i = 0; i < ticks; i++) Tick(); }

    [Test]
    public void ActualSlideHoldsLowestPoseAfterClipDurationWhileStillMoving()
    {
        Advance(110);
        Assert.IsTrue(_motor.IsSliding);
        Assert.IsTrue(_animation.IsHoldingSlidePose);
        Assert.IsFalse(_animation.IsSlideRecovering);
        Assert.That(_animation.SlideNormalizedTime, Is.EqualTo(_animation.SlideHoldNormalizedTime).Within(.00001f));
        Assert.IsTrue(_animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Trackle"));
        Assert.IsTrue(_animator.GetCurrentAnimatorClipInfo(0).Any(info =>
            AssetDatabase.GetAssetPath(info.clip) == "Assets/Animations/fbx/Running Slide.fbx"));
        Transform leg = _animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        Quaternion pose = leg.localRotation;
        Vector3 hips = _animator.GetBoneTransform(HumanBodyBones.Hips).localPosition;
        Vector3 position = _root.transform.position;
        Advance(25);
        Assert.Greater(Vector3.Distance(position, _root.transform.position), .1f);
        Assert.Less(Quaternion.Angle(pose, leg.localRotation), .01f);
        Assert.Less(Vector3.Distance(hips, _animator.GetBoneTransform(HumanBodyBones.Hips).localPosition), .0001f);
        Assert.IsFalse(_animator.applyRootMotion);
    }

    [Test]
    public void HeldPoseIsCloseToGroundAndKeepsAuthoredCrouchHeight()
    {
        Advance(60);
        float lowest = float.PositiveInfinity;
        foreach (SkinnedMeshRenderer renderer in _root.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var mesh = new Mesh();
            try
            {
                renderer.BakeMesh(mesh, false);
                foreach (Vector3 vertex in mesh.vertices)
                    lowest = Mathf.Min(lowest, renderer.transform.TransformPoint(vertex).y);
            }
            finally { Object.DestroyImmediate(mesh); }
        }
        float hipsHeight = _animator.GetBoneTransform(HumanBodyBones.Hips).position.y - _root.transform.position.y;
        Assert.That(lowest - 10f, Is.InRange(-.05f, .05f), "脚底必须贴近地面");
        Assert.That(hipsHeight, Is.InRange(.1f, .45f), "不能把铲下姿态保持在站立髋部高度");
    }

    [Test]
    public void StopPlaysRecoveryThenReturnsToIdle()
    {
        Advance(60);
        float heldTime = _animation.SlideNormalizedTime;
        _motor.SetHorizontalSpeed(0f); Tick();
        Assert.IsFalse(_motor.IsSliding);
        Assert.IsTrue(_animation.IsSlideRecovering);
        Assert.Greater(_animation.SlideNormalizedTime, heldTime);
        Advance(100);
        Assert.IsFalse(_animation.IsSlideRecovering);
        Assert.AreEqual(PlayerAnimation.MotionStateIdle, _animation.CurrentMotionState);
        Assert.IsTrue(_animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Idle"));
    }

    [Test]
    public void ReleaseWhileMovingPlaysRecoveryThenReturnsToIdle()
    {
        Advance(60);
        float heldTime = _animation.SlideNormalizedTime;
        Tick(slideHeld: false);
        Assert.IsFalse(_motor.IsSliding);
        Assert.Greater(_motor.HorizontalSpeed, 0f);
        Assert.IsTrue(_animation.IsSlideRecovering);
        Assert.Greater(_animation.SlideNormalizedTime, heldTime);
        Advance(100);
        Assert.IsFalse(_animation.IsSlideRecovering);
        Assert.AreEqual(PlayerAnimation.MotionStateIdle, _animation.CurrentMotionState);
        Assert.IsTrue(_animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Idle"));
    }

    [Test]
    public void EarlyReleaseStartsStandingRecoveryWithoutFinishingSlideEntry()
    {
        Assert.Less(_animation.SlideNormalizedTime, _animation.SlideHoldNormalizedTime);
        Tick(slideHeld: false);
        Assert.IsFalse(_motor.IsSliding);
        Assert.Greater(_motor.HorizontalSpeed, 0f);
        Assert.IsTrue(_animation.IsSlideRecovering);
        Assert.Greater(_animation.SlideNormalizedTime, _animation.SlideHoldNormalizedTime);
    }

    [Test]
    public void StoppedPlayerHoldsPoseUnderCeilingAndRecoversAfterItClears()
    {
        GameObject ceiling = new GameObject("SlideAnimationCeiling"); _created.Add(ceiling);
        ceiling.transform.position = _root.transform.position + Vector3.up * 1.3f;
        ceiling.AddComponent<BoxCollider>().size = new Vector3(10f, .4f, 300f);
        Physics.SyncTransforms();
        Advance(70);
        Assert.IsTrue(_motor.IsSlideHeadBlocked);
        _motor.SetHorizontalSpeed(0f); Advance(30);
        Assert.IsTrue(_motor.IsSliding);
        Assert.IsTrue(_animation.IsHoldingSlidePose);
        Assert.IsFalse(_animation.IsSlideRecovering);
        ceiling.SetActive(false); Physics.SyncTransforms(); Tick();
        Assert.IsFalse(_motor.IsSliding);
        Assert.IsTrue(_animation.IsSlideRecovering);
    }

    [Test]
    public void JumpCancelsHoldAndSkipsStandingRecovery()
    {
        Advance(60); Tick(ActionInputButtons.Jump);
        Assert.IsFalse(_motor.IsSliding);
        Assert.Greater(_motor.Velocity.y, 0f);
        Assert.IsFalse(_animation.IsSlideRecovering);
        Advance(10);
        Assert.AreEqual(PlayerAnimation.MotionStateJump, _animation.CurrentMotionState);
        Assert.IsTrue(_animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Jump")
            || _animator.IsInTransition(0) && _animator.GetNextAnimatorStateInfo(0).IsName("Base Layer.Jump"),
            $"current={_animator.GetCurrentAnimatorStateInfo(0).fullPathHash}, next={_animator.GetNextAnimatorStateInfo(0).fullPathHash}, jump={Animator.StringToHash("Base Layer.Jump")}");
    }

    [Test]
    public void SlideTimeUsesOnlyPlayerSimulationDelta()
    {
        float start = _animation.SlideNormalizedTime;
        for (int i = 0; i < 20; i++) Tick(dt: 0f);
        Assert.AreEqual(start, _animation.SlideNormalizedTime);
        for (int i = 0; i < 6; i++) Tick(dt: .1f / 60f);
        AnimationClip clip = _animator.runtimeAnimatorController.animationClips.First(value =>
            AssetDatabase.GetAssetPath(value) == "Assets/Animations/fbx/Running Slide.fbx");
        Assert.That(_animation.SlideNormalizedTime - start, Is.EqualTo(.01f / clip.length).Within(.00001f));
    }
}
#endif
