#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;

public sealed class EnemyAnimationTests
{
    [Test]
    public void Attack_ReturnsToMove_AndCanRepeat()
    {
        var controller = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(
            "Assets/Animations/EnemyTest.controller");
        var states = controller.layers[0].stateMachine.states;
        var attack = states.Single(s => s.state.name == "Attack").state;
        var exit = attack.transitions.Single(t => t.destinationState.name == "Move");
        Assert.IsTrue(exit.hasExitTime, "无条件的 Attack 出口必须启用 Exit Time，否则 Unity 会忽略它");
        Assert.AreEqual(1f, exit.exitTime);
        Assert.IsEmpty(exit.conditions);

        var root = new GameObject("AttackExitRegression");
        try
        {
            var animator = root.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            // 此用例只验证 Animator 出口，不执行独立测试对象没有接线的战斗事件。
            animator.fireEvents = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Update(0f);
            for (int repeat = 0; repeat < 2; repeat++)
            {
                animator.SetTrigger("Attack");
                for (int frame = 0; frame < 12; frame++) animator.Update(1f / 60f);
                Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Attack"));
                int frames = Mathf.CeilToInt((attack.motion.averageDuration + exit.duration + .2f) * 60f);
                for (int frame = 0; frame < frames; frame++) animator.Update(1f / 60f);
                Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Move"), "攻击播完必须真正退出 Animator 状态");
                Assert.IsFalse(animator.IsInTransition(0));
            }
        }
        finally { Object.DestroyImmediate(root); }
    }

    [Test]
    public void MoveTree_NormalAndInjuredSpeedTrees()
    {
        var controller = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(
            "Assets/Animations/EnemyTest.controller");
        var states = controller.layers[0].stateMachine.states;
        Assert.IsFalse(states.Any(s => s.state.name == "Injured"));
        var move = (UnityEditor.Animations.BlendTree)states.Single(s => s.state.name == "Move").state.motion;
        Assert.AreEqual("Injured", move.blendParameter);
        Assert.AreEqual(2, move.children.Length);
        var normal = (UnityEditor.Animations.BlendTree)move.children[0].motion;
        Assert.AreEqual("Normal", normal.name);
        Assert.AreEqual("Speed", normal.blendParameter);
        CollectionAssert.AreEqual(new[] { 0f, 2f, 5f }, normal.children.Select(c => c.threshold).ToArray());
        var injured = (UnityEditor.Animations.BlendTree)move.children[1].motion;
        Assert.AreEqual("Injured", injured.name);
        Assert.AreEqual("Speed", injured.blendParameter);
        CollectionAssert.AreEqual(new[] { 0f, 2f, 5f }, injured.children.Select(c => c.threshold).ToArray());
        CollectionAssert.AreEqual(new[] { "IdleInjured", "WalkInjured", "RunInjured" }, injured.children.Select(c => c.motion.name).ToArray());
        Assert.IsTrue(injured.children.All(c => ((AnimationClip)c.motion).isLooping));
        Assert.IsTrue(controller.parameters.Any(p => p.name == "Hurt" && p.type == AnimatorControllerParameterType.Trigger));
        Assert.IsTrue(controller.layers[0].stateMachine.anyStateTransitions.Any(t => t.destinationState.name == "Hurt"
            && t.conditions.Any(c => c.parameter == "Hurt")));
        var hurtEntry = controller.layers[0].stateMachine.anyStateTransitions.Single(t => t.destinationState.name == "Hurt");
        Assert.IsFalse(hurtEntry.hasExitTime);
        Assert.AreEqual(0f, hurtEntry.duration, "hurt must not wait for a long blend");
        Assert.IsTrue(states.All(s => s.state.transitions.All(t => t.destinationState.name != "Hurt")));
    }

    [UnityTest]
    public IEnumerator ModelInterpolation_DoesNotMovePhysicsRoot()
    {
        var root = new GameObject("InterpolationTest");
        root.AddComponent<Enemy>();
        root.transform.position = Vector3.right * 10;
        var model = new GameObject("Model");
        model.transform.SetParent(root.transform, false);
        model.transform.localPosition = Vector3.up;
        var animation = model.AddComponent<EnemyAnimation>();
        float alpha = Mathf.Clamp01((Time.unscaledTime - Time.fixedUnscaledTime) / Time.fixedUnscaledDeltaTime);
        animation.SendMessage("LateUpdate");
        Vector3 expected = Vector3.Lerp(Vector3.zero, root.transform.position, alpha) + Vector3.up;
        Assert.Less(Vector3.Distance(expected, model.transform.position), .001f);
        Assert.AreEqual(Vector3.right * 10, root.transform.position);
        Object.Destroy(root);
        yield return null;
    }

    [UnityTest]
    public IEnumerator Scene_Locomotion_Thresholds_Chase_Hitbox_Death()
    {
        yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/AnimTestScene.unity", new LoadSceneParameters(LoadSceneMode.Single));
        var enemy = Object.FindObjectOfType<Enemy>();
        var player = Object.FindObjectOfType<PlayerMotor>();
        var animator = enemy.GetComponentInChildren<Animator>();
        var hitbox = enemy.GetComponentInChildren<Hitbox>();
        Assert.NotNull(player);
        Assert.IsTrue(animator.isHuman, "Enemy avatar");
        GameManager.Instance.StartGame();
        player.enabled = false;
        enemy.SetChasePlayer(false);
        yield return new WaitForSeconds(.6f);
        enemy.enabled = false;
        enemy.SetInvulnerableTime(0);
        Assert.AreEqual(2, enemy.WalkSpeed);
        Assert.AreEqual(5, enemy.RunSpeed);
        Assert.IsFalse(hitbox.GetComponent<Collider>().enabled);
        var speed = typeof(Enemy).GetField("<Speed>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (var test in new[]{(0f,"Idle"),(2f,"Walk"),(5f,"RunFast")})
        {
            speed.SetValue(enemy, test.Item1);
            yield return new WaitForSeconds(.6f);
            Assert.AreEqual(test.Item1, animator.GetFloat("Speed"), .05f);
            Assert.IsTrue(animator.GetCurrentAnimatorClipInfo(0).Any(c=>c.clip.name==test.Item2 && c.weight>.95f), test.Item2);
        }
        enemy.TakeDamage(70, enemy.transform.position, Vector3.zero);
        yield return new WaitForSeconds(.6f);
        Assert.AreEqual(30, animator.GetFloat("Hp"));
        Assert.AreEqual(0f, animator.GetFloat("Injured"), "30% is healthy");
        enemy.TakeDamage(1, enemy.transform.position, Vector3.zero);
        yield return new WaitForSeconds(1.3f);
        foreach (var test in new[]{(0f,"IdleInjured"),(2f,"WalkInjured"),(5f,"RunInjured")})
        {
            speed.SetValue(enemy, test.Item1);
            yield return new WaitForSeconds(.6f);
            Assert.AreEqual(1f, animator.GetFloat("Injured"));
            Assert.IsTrue(animator.GetCurrentAnimatorClipInfo(0).Any(c=>c.clip.name==test.Item2 && c.weight>.95f), test.Item2);
        }
        enemy.SetMaxHealth(200);
        enemy.TakeDamage(140, enemy.transform.position, Vector3.zero);
        Assert.IsFalse(enemy.IsInjured);
        enemy.TakeDamage(1, enemy.transform.position, Vector3.zero);
        Assert.IsTrue(enemy.IsInjured, "threshold scales with MaxHealth");

        enemy.enabled = true;
        player.Teleport(enemy.transform.position + Vector3.forward * 9);
        enemy.SetChasePlayer(true);
        yield return new WaitForSeconds(.15f);
        Assert.AreEqual(0f, enemy.Speed, "hurt reaction stops chasing");
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Hurt"));
        yield return new WaitForSeconds(1.3f);
        Assert.Greater(enemy.Speed, 0f, "injured locomotion resumes after hurt");

        enemy.ResetHealth();
        enemy.enabled = true;
        player.Teleport(enemy.transform.position + Vector3.forward * 9);
        enemy.SetChasePlayer(true);
        bool ran=false, walked=false;
        float deadline=Time.realtimeSinceStartup+5;
        while(!enemy.IsAttacking && Time.realtimeSinceStartup<deadline)
        {
            ran |= Mathf.Abs(enemy.Speed-5)<.05f;
            walked |= Mathf.Abs(enemy.Speed-2)<.05f;
            yield return new WaitForFixedUpdate();
        }
        Assert.IsTrue(ran,"far chase runs");
        Assert.IsTrue(walked,"near chase walks");
        Assert.IsTrue(enemy.IsAttacking,"arrived in attack range");
        Assert.AreEqual(100, GameManager.Instance.Player.Health,"proximity must not cause damage");
        enemy.SetChasePlayer(false);
        yield return new WaitForSeconds(1.9f);
        Assert.AreEqual(92, GameManager.Instance.Player.Health,"animation hitbox hits once");
        Assert.IsFalse(enemy.IsAttacking,"attack finishes");
        Assert.IsFalse(hitbox.GetComponent<Collider>().enabled,"attack window closes");

        enemy.SetChasePlayer(true);
        deadline=Time.realtimeSinceStartup+3;
        while(!enemy.IsAttacking && Time.realtimeSinceStartup<deadline) yield return null;
        Assert.IsTrue(enemy.IsAttacking,"can attack again");
        enemy.TakeDamage(1, enemy.transform.position, Vector3.zero);
        Assert.IsFalse(enemy.IsAttacking, "hurt cancels attack immediately");
        Assert.IsFalse(hitbox.GetComponent<Collider>().enabled);
        animator.Update(0f);
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Hurt"), "hurt enters on the first animation evaluation");
        enemy.SetChasePlayer(false);
        yield return new WaitForSeconds(.2f);
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Hurt"));
        yield return new WaitForSeconds(1.3f);
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Move"));
        Assert.IsFalse(enemy.GetComponentInChildren<EnemyAnimation>().IsHurting, "AI must unlock after Hurt");
        enemy.TakeDamage(1, enemy.transform.position, Vector3.zero);
        enemy.TakeDamage(enemy.MaxHealth, enemy.transform.position, Vector3.zero);
        yield return new WaitForSeconds(.3f);
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Die"));
        Assert.IsFalse(enemy.IsAttacking);
        Assert.IsFalse(hitbox.GetComponent<Collider>().enabled);
        Assert.IsFalse(enemy.GetComponent<CharacterController>().enabled);
        float hp=GameManager.Instance.Player.Health;
        Vector3 position=enemy.transform.position;
        yield return new WaitForSeconds(5);
        Assert.AreEqual(hp,GameManager.Instance.Player.Health,"dead enemy cannot damage");
        Assert.AreEqual(position,enemy.transform.position,"dead enemy cannot chase");
        Assert.IsFalse(animator.GetCurrentAnimatorStateInfo(0).loop);
        Assert.GreaterOrEqual(animator.GetCurrentAnimatorStateInfo(0).normalizedTime,1);
        Debug.Log("ENEMY_PLAYMODE_OK");
        Object.Destroy(GameManager.Instance.gameObject);
    }
}
#endif
