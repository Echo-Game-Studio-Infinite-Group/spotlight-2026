using System.Reflection;
using NUnit.Framework;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
#endif

public sealed class AnimationSceneTests
{
    [Test]
    public void AnimationParametersFollowGroundJumpAndSlide()
    {
#if UNITY_EDITOR
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        Assert.NotNull(source);
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.transform.position = Vector3.down * 0.5f;
        ground.transform.localScale = new Vector3(100f, 1f, 100f);
        GameObject character = Object.Instantiate(source, Vector3.up * 0.05f, Quaternion.identity);
        try
        {
            PlayerMotor motor = character.GetComponent<PlayerMotor>();
            Animator animator = character.GetComponentInChildren<Animator>();
            PlayerAnimation animation = character.GetComponentInChildren<PlayerAnimation>();
            animation.SetSequenceDriven(true);
            motor.enabled = false;
            Physics.SyncTransforms();
            float now = 0f;
            const float tick = 1f / 60f;
            for (int i = 0; i < 30 && !motor.IsGrounded; i++)
            {
                now += tick;
                motor.Simulate(default, tick, now);
            }
            Assert.IsTrue(motor.IsGrounded);
            animation.UpdateLocomotion(tick);
            Assert.AreEqual(0, animator.GetInteger("MotionState"));

            SetVelocity(motor, Vector3.forward * 2f);
            now += tick;
            motor.Simulate(new PlayerInputFrame { Move = Vector2.up }, tick, now);
            animation.UpdateLocomotion(tick);
            Assert.AreEqual(1, animator.GetInteger("MotionState"));

            motor.Teleport(Vector3.up * 0.05f);
            for (int i = 0; i < 30 && !motor.IsGrounded; i++)
            {
                now += tick;
                motor.Simulate(default, tick, now);
            }
            Assert.IsTrue(motor.IsGrounded);
            now += tick;
            motor.Simulate(new PlayerInputFrame { JumpPressed = true, JumpTime = now }, tick, now);
            animation.UpdateLocomotion(tick);
            Assert.AreEqual(2, animator.GetInteger("MotionState"));

            motor.Teleport(Vector3.up * 0.05f);
            for (int i = 0; i < 30 && !motor.IsGrounded; i++)
            {
                now += tick;
                motor.Simulate(default, tick, now);
            }
            Assert.IsTrue(motor.IsGrounded);
            SetVelocity(motor, Vector3.forward * motor.Params.GroundSpeedThreshold);
            now += tick;
            motor.Simulate(new PlayerInputFrame { Move = Vector2.up, SlidePressed = true, SlideHeld = true }, tick, now);
            animation.UpdateLocomotion(tick);
            Assert.AreEqual(3, animator.GetInteger("MotionState"));
        }
        finally
        {
            Object.DestroyImmediate(character);
            Object.DestroyImmediate(ground);
        }
#else
        Assert.Ignore("动画播放验证在编辑器 PlayMode 中执行");
#endif
    }

    [Test]
    public void Player_HasPlayableIdleMoveJumpAndTackle()
    {
#if UNITY_EDITOR
        GameObject character = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        Assert.NotNull(character);
        Assert.NotNull(character.GetComponentInChildren<PlayerAnimation>(true));
        Animator animator = character.GetComponentInChildren<Animator>(true);
        Assert.NotNull(animator);
        Assert.IsFalse(animator.applyRootMotion);
        Assert.NotNull(animator.avatar);
        Assert.IsTrue(animator.avatar.isValid);
        var controller = animator.runtimeAnimatorController as AnimatorController;
        Assert.NotNull(controller);
        Assert.AreEqual("Idle", controller.layers[0].stateMachine.defaultState.name);

        foreach (string name in new[] { "Idle", "Move", "Jump", "Trackle" })
        {
            AnimatorState state = System.Array.Find(controller.layers[0].stateMachine.states,
                child => child.state.name == name).state;
            Assert.NotNull(state, name + " 缺少动画状态");
            Assert.NotNull(state.motion, name + " 缺少动画片段");
        }
#else
        Assert.Ignore("Animator 资源验证在编辑器 PlayMode 中执行");
#endif
    }

    private static void SetVelocity(PlayerMotor motor, Vector3 velocity) => typeof(PlayerMotor)
        .GetField("_velocity", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(motor, velocity);
}
