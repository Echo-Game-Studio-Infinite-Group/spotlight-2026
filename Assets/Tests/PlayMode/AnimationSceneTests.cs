using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
#endif

public sealed class AnimationSceneTests
{
    [UnityTest]
    public IEnumerator AnimationParametersFollowGroundJumpAndSlide()
    {
#if UNITY_EDITOR
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/character.prefab");
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.transform.position = Vector3.down * 0.5f;
        ground.transform.localScale = new Vector3(100f, 1f, 100f);
        GameObject character = Object.Instantiate(source, Vector3.up * 0.05f, Quaternion.identity);
        try
        {
            PlayerMotor motor = character.GetComponent<PlayerMotor>();
            Animator animator = character.GetComponentInChildren<Animator>();
            motor.enabled = false;
            float now = 0f;
            const float tick = 1f / 60f;
            for (int i = 0; i < 30 && !motor.IsGrounded; i++)
            {
                now += tick;
                motor.Simulate(default, tick, now);
            }
            Assert.IsTrue(motor.IsGrounded);
            yield return null;
            Assert.AreEqual(0, animator.GetInteger("MotionState"));

            SetVelocity(motor, Vector3.forward * 2f);
            now += tick;
            motor.Simulate(default, tick, now);
            yield return null;
            Assert.AreEqual(1, animator.GetInteger("MotionState"));
            Assert.Less(animator.GetFloat("RunBlend"), 0.01f);

            SetVelocity(motor, Vector3.forward * motor.Params.GroundSpeedThreshold);
            now += tick;
            motor.Simulate(default, tick, now);
            yield return null;
            Assert.Greater(animator.GetFloat("RunBlend"), 0f);

            motor.Teleport(Vector3.up * 0.05f);
            for (int i = 0; i < 30 && !motor.IsGrounded; i++)
            {
                now += tick;
                motor.Simulate(default, tick, now);
            }
            Assert.IsTrue(motor.IsGrounded);
            now += tick;
            motor.Simulate(new PlayerInputFrame { JumpPressed = true, JumpTime = now }, tick, now);
            yield return null;
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
            motor.Simulate(new PlayerInputFrame { SlidePressed = true, SlideHeld = true }, tick, now);
            yield return null;
            Assert.AreEqual(3, animator.GetInteger("MotionState"));
        }
        finally
        {
            Object.DestroyImmediate(character);
            Object.DestroyImmediate(ground);
        }
#else
        Assert.Ignore("动画播放验证在编辑器 PlayMode 中执行");
        yield break;
#endif
    }

    [Test]
    public void Character_HasPlayableIdleWalkRunJumpAndTackle()
    {
#if UNITY_EDITOR
        GameObject character = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/character.prefab");
        Assert.NotNull(character);
        Assert.NotNull(character.GetComponent<PlayerAnimation>());
        Animator animator = character.GetComponentInChildren<Animator>(true);
        Assert.NotNull(animator);
        Assert.IsFalse(animator.applyRootMotion);
        Assert.NotNull(animator.avatar);
        Assert.IsTrue(animator.avatar.isValid);
        var controller = animator.runtimeAnimatorController as AnimatorController;
        Assert.NotNull(controller);
        Assert.AreEqual("Idle", controller.layers[0].stateMachine.defaultState.name);

        foreach (var child in controller.layers[0].stateMachine.states)
        {
            Assert.NotNull(child.state.motion, child.state.name + " 缺少动画片段");
            if (child.state.name != "Move") continue;
            BlendTree tree = child.state.motion as BlendTree;
            Assert.NotNull(tree);
            Assert.AreEqual("RunBlend", tree.blendParameter);
            Assert.AreEqual(2, tree.children.Length);
            Assert.That(tree.children[0].threshold, Is.EqualTo(0f));
            Assert.That(tree.children[1].threshold, Is.EqualTo(1f));
            Assert.NotNull(tree.children[0].motion);
            Assert.NotNull(tree.children[1].motion);
        }
        Assert.AreEqual(4, controller.layers[0].stateMachine.states.Length);
#else
        Assert.Ignore("Animator 资源验证在编辑器 PlayMode 中执行");
#endif
    }

    private static void SetVelocity(PlayerMotor motor, Vector3 velocity) => typeof(PlayerMotor)
        .GetField("_velocity", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(motor, velocity);
}
