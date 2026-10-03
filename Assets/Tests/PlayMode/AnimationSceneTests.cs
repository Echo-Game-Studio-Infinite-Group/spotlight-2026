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
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.transform.position = Vector3.down * 0.5f;
        ground.transform.localScale = new Vector3(100f, 1f, 100f);
        GameObject character = Object.Instantiate(source, Vector3.up * 0.05f, Quaternion.identity);
        try
        {
            PlayerMotor motor = character.GetComponent<PlayerMotor>();
            Animator animator = character.GetComponentInChildren<Animator>(true);
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
        GameObject character = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        Assert.NotNull(character);
        Assert.NotNull(character.GetComponentInChildren<Animator>(true).GetComponent<PlayerAnimation>());
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
        // Idle / Move / Jump / Trackle / Attack
        Assert.AreEqual(5, controller.layers[0].stateMachine.states.Length);

        // MotionState 编号等于状态在列表里的下标，攻击固定为 4（PlayerAnimation.MotionStateAttack）
        Assert.AreEqual("Attack",
            controller.layers[0].stateMachine.states[PlayerAnimation.MotionStateAttack].state.name);
#else
        Assert.Ignore("Animator 资源验证在编辑器 PlayMode 中执行");
#endif
    }

    [UnityTest]
    public IEnumerator AttackDrivesMotionStateFourAndReturnsToLocomotion()
    {
#if UNITY_EDITOR
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.transform.position = Vector3.down * 0.5f;
        ground.transform.localScale = new Vector3(100f, 1f, 100f);
        GameObject character = Object.Instantiate(source, Vector3.up * 0.05f, Quaternion.identity);
        try
        {
            PlayerMotor motor = character.GetComponent<PlayerMotor>();
            PlayerCombat combat = character.GetComponent<PlayerCombat>();
            Animator animator = character.GetComponentInChildren<Animator>(true);
            Assert.NotNull(combat, "Player.prefab 缺少 PlayerCombat");
            motor.enabled = false;

            float now = 0f;
            const float tick = 1f / 60f;
            for (int i = 0; i < 30 && !motor.IsGrounded; i++)
            {
                now += tick;
                motor.Simulate(default, tick, now);
            }

            Assert.IsFalse(combat.IsAttacking);
            now = TimeManager.UnscaledTime;
            Assert.IsTrue(combat.BeginAttack(now), "首次攻击应能立即触发");
            Assert.IsFalse(combat.BeginAttack(now), "冷却期内不应重复触发");

            yield return null;
            Assert.IsTrue(combat.IsAttacking);
            Assert.AreEqual(PlayerAnimation.MotionStateAttack, animator.GetInteger("MotionState"),
                "攻击应把 MotionState 切到 4");

            // 攻击窗口结束后必须回到移动/待机编号，不允许卡在攻击状态
            yield return new WaitForSeconds(combat.AttackDuration + 0.1f);
            Assert.IsFalse(combat.IsAttacking);
            Assert.IsFalse(combat.Hitbox.GetComponent<Collider>().enabled, "收招后判定盒必须关闭");
            yield return null;
            Assert.AreNotEqual(PlayerAnimation.MotionStateAttack, animator.GetInteger("MotionState"));
        }
        finally
        {
            Object.DestroyImmediate(character);
            Object.DestroyImmediate(ground);
        }
#else
        Assert.Ignore("攻击动画验证在编辑器 PlayMode 中执行");
        yield break;
#endif
    }

    [Test]
    public void AttackEventsStayInsideClip()
    {
#if UNITY_EDITOR
        var clips = AssetDatabase.LoadAllAssetsAtPath("Assets/Animations/fbx/Attack.fbx");
        foreach (var asset in clips)
        {
            if (!(asset is AnimationClip clip) || clip.name.StartsWith("__preview__")) continue;
            var events = AnimationUtility.GetAnimationEvents(clip);
            Assert.That(events.Length, Is.GreaterThanOrEqualTo(3));
            foreach (var entry in events)
                Assert.That(entry.time, Is.InRange(0f, clip.length), entry.functionName);
        }
#endif
    }

    [UnityTest]
    public IEnumerator PrefabAnimationHitsStaticEnemyPlaysVfxAndExitsAttack()
    {
#if UNITY_EDITOR
        var source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        var character = Object.Instantiate(source, new Vector3(0, 20, 0), Quaternion.identity);
        var target = new GameObject("StaticEnemy");
        try
        {
            character.GetComponent<PlayerMotor>().enabled = false;
            var animator = character.GetComponentInChildren<Animator>(true);
            var combat = character.GetComponent<PlayerCombat>();
            var vfx = character.GetComponent<PlayerVFXManager>();
            Assert.NotNull(vfx);
            Assert.NotNull(vfx.attack1);
            var box = combat.Hitbox.GetComponent<BoxCollider>();
            target.transform.position = box.transform.TransformPoint(box.center);
            target.AddComponent<BoxCollider>().size = Vector3.one * 0.3f;
            var enemy = target.AddComponent<Enemy>();
            enemy.SetInvulnerableTime(10f);
            TimeManager.ClearSlowMotion();
            combat.Configure(25, 5, 0, 1);
            Assert.IsTrue(combat.BeginAttack(TimeManager.UnscaledTime));
            bool opened = false, particles = false, entered = false;
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 2.2f)
            {
                yield return null;
                opened |= box.enabled;
                particles |= vfx.attack1.isPlaying;
                entered |= animator.GetCurrentAnimatorStateInfo(0).IsName("Attack");
                if (entered && !combat.IsAttacking && !animator.GetCurrentAnimatorStateInfo(0).IsName("Attack")) break;
            }
            Assert.IsTrue(entered, "必须实际进入 Attack");
            Assert.IsTrue(opened, "动画事件必须打开判定");
            Assert.Less(enemy.Health, enemy.MaxHealth, "没有刚体的敌人也必须收到触发伤害");
            Assert.IsTrue(particles, "攻击粒子必须播放");
            Assert.IsFalse(combat.IsAttacking, "收招必须覆盖过长的计时窗口");
            Assert.IsFalse(animator.GetCurrentAnimatorStateInfo(0).IsName("Attack"));
            Assert.IsFalse(box.enabled);
        }
        finally
        {
            Object.DestroyImmediate(character);
            Object.DestroyImmediate(target);
            TimeManager.ClearSlowMotion();
        }
#else
        yield break;
#endif
    }

    private static void SetVelocity(PlayerMotor motor, Vector3 velocity) => typeof(PlayerMotor)
        .GetField("_velocity", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(motor, velocity);
}
