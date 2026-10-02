using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

// 场景装配回归：原「检查装配结果」菜单的场景部分迁此——工具负责转换、测试负责断言
// 场景加载纪律与 CameraSceneTests 一致：additive 加载 TestScene、finally 里复位活动场景并卸载、非编辑器平台 Ignore
// 布局断言兼容 Hurtbox 在角色根（旧场景现状）与子物体（装配工具迁移后）两种形态
public sealed class SceneBootstrapTests
{
    // additive 加载与角色定位的共享装载器：结果写入 handle（迭代器不能带 out 参数）
    private sealed class SceneHandle
    {
        public Scene Scene;
        public GameObject Character;
    }

#if UNITY_EDITOR
    private static IEnumerator LoadTestScene(SceneHandle handle)
    {
        handle.Scene = EditorSceneManager.LoadSceneInPlayMode("Assets/Scenes/TestScene.unity",
            new LoadSceneParameters(LoadSceneMode.Additive));
        yield return null;
        // 角色实例按 PlayerMotor 组件定位（同 CameraSceneTests / 装配工具）：根名随预制体文件名变过，不依赖名字
        foreach (GameObject root in handle.Scene.GetRootGameObjects())
        {
            PlayerMotor motor = root.GetComponentInChildren<PlayerMotor>();
            if (motor != null)
            {
                handle.Character = motor.gameObject;
                break;
            }
        }
    }
#endif

    [UnityTest]
    public IEnumerator TestScene_CharacterHasFullCombatComponents()
    {
#if UNITY_EDITOR
        Scene original = SceneManager.GetActiveScene();
        var handle = new SceneHandle();
        yield return LoadTestScene(handle);
        try
        {
            Assert.NotNull(handle.Character, "场景缺少角色（未找到 PlayerMotor）");
            GameObject character = handle.Character;
            Assert.NotNull(character.GetComponentInChildren<InputSampler>(), "角色缺少 InputSampler");
            HealthComponent health = character.GetComponentInChildren<HealthComponent>();
            Assert.NotNull(health, "角色缺少 HealthComponent");
            Assert.NotNull(character.GetComponentInChildren<PlayerCombat>(), "角色缺少 PlayerCombat");

            Hitbox[] hitboxes = character.GetComponentsInChildren<Hitbox>();
            Assert.AreEqual(2, hitboxes.Length, "角色应有伤害/parry 两个判定框");
            bool hasDamage = false;
            bool hasParry = false;
            foreach (Hitbox box in hitboxes)
            {
                if (box.Kind == HitboxKind.Damage) hasDamage = true;
                else if (box.Kind == HitboxKind.Parry) hasParry = true;
            }
            Assert.IsTrue(hasDamage, "缺少 Kind=Damage 的判定框");
            Assert.IsTrue(hasParry, "缺少 Kind=Parry 的判定框");

            // GetComponentInChildren 兼容 Hurtbox 在根与子物体两种布局
            Hurtbox hurtbox = character.GetComponentInChildren<Hurtbox>();
            Assert.NotNull(hurtbox, "角色缺少 Hurtbox");
            Assert.NotNull(hurtbox.Health, "Hurtbox 未接到角色血量组件");
        }
        finally
        {
            // 失败路径也要复位：卸载前恢复活动场景，避免污染后续测试
            SceneManager.SetActiveScene(original);
            SceneManager.UnloadSceneAsync(handle.Scene);
        }
        yield return null;
#else
        Assert.Ignore("场景装配验证在编辑器 PlayMode 中执行");
        yield break;
#endif
    }

    [UnityTest]
    public IEnumerator TestScene_HasSceneLevelServices()
    {
#if UNITY_EDITOR
        Scene original = SceneManager.GetActiveScene();
        var handle = new SceneHandle();
        yield return LoadTestScene(handle);
        try
        {
            DamageResolver resolver = null;
            TimeManager time = null;
            foreach (GameObject root in handle.Scene.GetRootGameObjects())
            {
                if (resolver == null) resolver = root.GetComponentInChildren<DamageResolver>();
                if (time == null) time = root.GetComponentInChildren<TimeManager>();
            }
            Assert.NotNull(resolver, "场景缺少 DamageResolver（每 tick 命中结算）");
            // TimeManager 按类型找，不限名字
            Assert.NotNull(time, "场景缺少 TimeManager");
        }
        finally
        {
            SceneManager.SetActiveScene(original);
            SceneManager.UnloadSceneAsync(handle.Scene);
        }
        yield return null;
#else
        Assert.Ignore("场景装配验证在编辑器 PlayMode 中执行");
        yield break;
#endif
    }

    [UnityTest]
    public IEnumerator TestScene_MovementWiringOnCharacter()
    {
#if UNITY_EDITOR
        Scene original = SceneManager.GetActiveScene();
        var handle = new SceneHandle();
        yield return LoadTestScene(handle);
        try
        {
            Assert.NotNull(handle.Character, "场景缺少角色（未找到 PlayerMotor）");
            PlayerMotor motor = handle.Character.GetComponent<PlayerMotor>();
            Assert.NotNull(motor, "角色缺少 PlayerMotor");
            Assert.NotNull(motor.Params, "PlayerMotor.Params 未接线（MovementParams 资产缺失或未 SetParams）");
        }
        finally
        {
            SceneManager.SetActiveScene(original);
            SceneManager.UnloadSceneAsync(handle.Scene);
        }
        yield return null;
#else
        Assert.Ignore("场景装配验证在编辑器 PlayMode 中执行");
        yield break;
#endif
    }
}
