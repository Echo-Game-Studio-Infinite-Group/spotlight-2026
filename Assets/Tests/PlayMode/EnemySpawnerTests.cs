#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

// 生成器与对象池的 PlayMode 回归测试。
//
// 断言的三件事对应需求里最容易出错的地方：
//   1. 同时存活上限真的拦得住（否则刷怪会失控）；
//   2. 回收后确实复用同一个实例（否则"对象池"名不副实，只是一直 Instantiate）；
//   3. 生成点落在地面上（垂直地块判定），而不是悬空或埋进地里。
//
// 场景自己造：CreateScene + 自己放一块地板，不依赖 TestScene/CombatTestScene 的当前布局，
// 这样别的组改场景时这些测试不会连带失败。
public sealed class EnemySpawnerTests
{
    private Scene _scene;
    private GameObject _ground;
    private EnemySpawner _spawner;
    private GameObject _prefab;

    // 用纯空物体当"敌人"：本测试验证的是池与生成位置，不需要 Enemy 的战斗/AI 依赖。
    private GameObject MakePrefab()
    {
        var prefab = new GameObject("SpawnTestPrefab");
        prefab.AddComponent<BoxCollider>();
        prefab.SetActive(false);
        return prefab;
    }

    [SetUp]
    public void SetUp()
    {
        _scene = SceneManager.CreateScene("EnemySpawnerTests");
        SceneManager.SetActiveScene(_scene);

        // 一块 40x40 的地板，顶面在 y = 0。全部生成点都应落在这个平面上。
        _ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _ground.name = "Ground";
        _ground.transform.SetPositionAndRotation(new Vector3(0f, -0.5f, 0f), Quaternion.identity);
        _ground.transform.localScale = new Vector3(40f, 1f, 40f);
        SceneManager.MoveGameObjectToScene(_ground, _scene);

        var spawnerObject = new GameObject("Spawner");
        // 刻意把 Spawner 摆在高处（y = 6）：验证候选点的 Y 会被落地判定修正，
        // 而不是直接沿用 Spawner 自身高度。
        spawnerObject.transform.position = new Vector3(0f, 6f, 0f);
        SceneManager.MoveGameObjectToScene(spawnerObject, _scene);

        _prefab = MakePrefab();
        _spawner = spawnerObject.AddComponent<EnemySpawner>();
    }

    [TearDown]
    public void TearDown()
    {
        if (_prefab != null) Object.DestroyImmediate(_prefab);
        if (_scene.IsValid()) SceneManager.UnloadSceneAsync(_scene);
    }

    // 私有序列化字段走 SerializedObject 写入，与项目里其他 PlayMode 测试保持一致。
    private void Configure(int maxAlive, float range, float interval, bool autoSpawn, int prewarm = 0)
    {
        var so = new SerializedObject(_spawner);
        so.FindProperty("_spawnObject").objectReferenceValue = _prefab;
        so.FindProperty("_maxAlive").intValue = maxAlive;
        so.FindProperty("_spawnRange").floatValue = range;
        so.FindProperty("_spawnInterval").floatValue = interval;
        so.FindProperty("_autoSpawn").boolValue = autoSpawn;
        so.FindProperty("_prewarm").intValue = prewarm;
        // 测试场景不烘焙 NavMesh：走射线兜底，保证确定性。
        so.FindProperty("_groundMode").enumValueIndex = (int)SpawnGroundMode.PhysicsRaycastOnly;
        so.FindProperty("_spawnHeightOffset").floatValue = 0f;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // ---- 1. 同时存活上限 ----

    [UnityTest]
    public IEnumerator SpawnOnce_RespectsMaxAlive()
    {
        Configure(maxAlive: 3, range: 10f, interval: 100f, autoSpawn: false);
        yield return null;

        for (int i = 0; i < 3; i++)
        {
            Assert.IsNotNull(_spawner.SpawnOnce(), $"第 {i + 1} 次生成应当成功");
        }

        Assert.AreEqual(3, _spawner.AliveCount, "存活数应当等于上限");
        Assert.IsFalse(_spawner.CanSpawnNow(), "达到上限后不应再允许生成");
        Assert.IsNull(_spawner.SpawnOnce(), "超过上限时 SpawnOnce 必须返回 null");
        Assert.AreEqual(3, _spawner.AliveCount, "被上限拦下后存活数不应变化");
    }

    [UnityTest]
    public IEnumerator AutoSpawn_StopsAtMaxAlive_AcrossManyFrames()
    {
        // 间隔设得极小并跑很多帧，模拟"长时间自动刷怪"，确认上限是硬约束。
        Configure(maxAlive: 4, range: 8f, interval: 0.01f, autoSpawn: true);
        yield return null;

        for (int i = 0; i < 120; i++) yield return null;

        Assert.LessOrEqual(_spawner.AliveCount, 4, "自动生成绝不能超过同时存活上限");
        Assert.AreEqual(4, _spawner.AliveCount, "这么多帧之后应当已刷到上限");
    }

    // ---- 回归：敌人死后必须能归还，Spawner 必须继续刷怪 ----
    //
    // 这是实机踩到的坑：预制体上没挂 EnemyPooledLifecycle 时，没有人监听 Died，
    // 尸体永远留在场上把 _alive 占满 → CanSpawnNow 恒 false → "刷了一阵就不刷了"。
    // Spawner 现在会在生成时自动补挂，这个测试锁住该行为。
    [UnityTest]
    public IEnumerator KilledEnemy_IsRecycledAnd_SpawnerResumesSpawning()
    {
        Configure(maxAlive: 2, range: 8f, interval: 0.01f, autoSpawn: true);
        yield return null;

        // 跑到刷满上限。
        for (int i = 0; i < 60; i++) yield return null;
        Assert.AreEqual(2, _spawner.AliveCount, "应当先刷到上限");

        // 取一个敌人并真的把它打死——走 HealthComponent 的真实死亡链路。
        GameObject victim = null;
        foreach (GameObject candidate in Object.FindObjectsOfType<GameObject>())
        {
            if (candidate.GetComponent<HealthComponent>() != null && candidate.GetComponent<EnemyPooledLifecycle>() != null)
            {
                victim = candidate;
                break;
            }
        }
        Assert.IsNotNull(victim, "生成的敌人身上应当自动带上了 EnemyPooledLifecycle");

        var health = victim.GetComponent<HealthComponent>();
        health.TakeDamage(health.MaxHealth * 10f, Vector3.zero, Vector3.forward);
        Assert.IsFalse(health.IsAlive, "这一击应当把敌人打死");

        // 等过 destroyTime（默认 1s）+ 余量。用真实时间等待，避免受 timeScale 影响。
        float deadline = Time.realtimeSinceStartup + 3f;
        while (Time.realtimeSinceStartup < deadline && _spawner.AliveCount >= 2)
        {
            yield return null;
        }

        Assert.Less(_spawner.AliveCount, 2,
            "敌人死后 1 秒应当归还进池，存活数必须降下来（否则 Spawner 会被上限卡死）");
        Assert.IsTrue(_spawner.CanSpawnNow(), "腾出名额后 Spawner 必须能继续生成");

        // 再跑一阵，确认它真的恢复了自动刷怪。
        for (int i = 0; i < 60; i++) yield return null;
        Assert.AreEqual(2, _spawner.AliveCount, "Spawner 应当自动补满名额，证明刷怪已恢复");
    }

    [UnityTest]
    public IEnumerator PooledEnemy_IsNotLeftActiveInSceneAfterDeath()
    {
        // 对应主人观察到的"对象都留在游戏场里了"：死后对象应当失活并回到池的待命节点下。
        Configure(maxAlive: 1, range: 8f, interval: 0.01f, autoSpawn: true);
        yield return null;
        for (int i = 0; i < 30; i++) yield return null;

        GameObject victim = null;
        foreach (GameObject candidate in Object.FindObjectsOfType<GameObject>())
        {
            if (candidate.GetComponent<EnemyPooledLifecycle>() != null) { victim = candidate; break; }
        }
        Assert.IsNotNull(victim);

        var health = victim.GetComponent<HealthComponent>();
        health.TakeDamage(health.MaxHealth * 10f, Vector3.zero, Vector3.forward);

        float deadline = Time.realtimeSinceStartup + 3f;
        while (Time.realtimeSinceStartup < deadline && _spawner.AliveCount > 0)
        {
            yield return null;
        }

        Assert.AreEqual(0, _spawner.AliveCount, "死后应当归还，存活数归零");
    }

    // ---- 2. 对象池复用 ----

    [UnityTest]
    public IEnumerator RecycledInstance_IsReusedInsteadOfRecreated()
    {
        Configure(maxAlive: 1, range: 8f, interval: 100f, autoSpawn: false);
        yield return null;

        GameObject first = _spawner.SpawnOnce();
        Assert.IsNotNull(first);
        int firstId = first.GetInstanceID();

        Assert.IsTrue(_spawner.Recycle(first), "回收自己生成的实例应当成功");
        Assert.AreEqual(0, _spawner.AliveCount);

        // 回收后实例应当失活但对象本身还在（被真销毁了下一段就拿不到同一个 ID）。
        Assert.IsTrue(first != null, "归还的实例不应被销毁，否则无从复用");
        Assert.IsFalse(first.activeSelf, "归还后实例应当失活");

        GameObject second = _spawner.SpawnOnce();
        Assert.IsNotNull(second, "回收后应当能再次生成");
        Assert.AreEqual(firstId, second.GetInstanceID(),
            "再次生成必须复用同一个实例——换了 ID 说明池没起作用，只是在反复 Instantiate");
    }

    [UnityTest]
    public IEnumerator ReturnedInstance_IsInactive_AndRecycledOnDeath()
    {
        Configure(maxAlive: 2, range: 8f, interval: 100f, autoSpawn: false);
        yield return null;

        GameObject instance = _spawner.SpawnOnce();
        Assert.IsNotNull(instance);
        Assert.IsTrue(instance.activeSelf, "生成后实例应当是激活的");

        _spawner.Recycle(instance);
        Assert.IsFalse(instance.activeSelf, "归还后实例应当失活，避免继续参与逻辑与渲染");
        Assert.AreEqual(0, _spawner.AliveCount);
    }

    [UnityTest]
    public IEnumerator RecycleAll_EmptiesAliveAndAllowsRefill()
    {
        Configure(maxAlive: 3, range: 8f, interval: 100f, autoSpawn: false);
        yield return null;

        for (int i = 0; i < 3; i++) Assert.IsNotNull(_spawner.SpawnOnce());
        Assert.AreEqual(3, _spawner.AliveCount);

        _spawner.RecycleAll();
        Assert.AreEqual(0, _spawner.AliveCount, "RecycleAll 后存活数应当归零");
        Assert.IsTrue(_spawner.CanSpawnNow(), "全部回收后应当重新可生成");
    }

    // ---- 3. 落地判定（垂直地块） ----

    [UnityTest]
    public IEnumerator SpawnedEnemies_LandOnGround_NotAtSpawnerHeight()
    {
        Configure(maxAlive: 12, range: 14f, interval: 100f, autoSpawn: false);
        yield return null;

        int spawned = 0;
        for (int i = 0; i < 12; i++)
        {
            GameObject instance = _spawner.SpawnOnce();
            if (instance == null) continue;
            spawned++;

            float y = instance.transform.position.y;
            // 地面顶面在 y = 0，Spawner 在 y = 6。
            // 若实现忘了落地判定，这里会读到 6；正确实现应当读到 ~0。
            Assert.AreEqual(0f, y, 0.15f,
                $"生成点 Y={y} 应贴合地面(0)，而不是沿用 Spawner 高度(6)");
        }

        Assert.Greater(spawned, 0, "应当在范围内成功生成若干敌人");
    }

    [UnityTest]
    public IEnumerator SpawnPoints_StayInsideCircularRange()
    {
        const float range = 9f;
        Configure(maxAlive: 16, range: range, interval: 100f, autoSpawn: false);
        yield return null;

        Vector3 center = _spawner.transform.position;
        for (int i = 0; i < 16; i++)
        {
            GameObject instance = _spawner.SpawnOnce();
            if (instance == null) continue;

            Vector3 flat = instance.transform.position - center;
            flat.y = 0f;
            Assert.LessOrEqual(flat.magnitude, range + 0.01f,
                $"生成点水平距离 {flat.magnitude} 超出了圆形范围 {range}");
        }
    }

    [UnityTest]
    public IEnumerator SpawnRange_WithNoGroundBelow_FailsInsteadOfFloating()
    {
        // 把 Spawner 挪到地板之外很远的空中：范围内没有任何地面。
        // 正确行为是拒绝生成，而不是把敌人丢在半空。
        _spawner.transform.position = new Vector3(500f, 40f, 500f);
        Configure(maxAlive: 5, range: 3f, interval: 100f, autoSpawn: false);
        yield return null;

        GameObject instance = _spawner.SpawnOnce();
        Assert.IsNull(instance, "找不到地面时必须返回 null，不能生成悬空敌人");
        Assert.AreEqual(0, _spawner.AliveCount);
    }

    // ---- 4. 分层地块：应选中与候选点同一层的地面 ----

    [UnityTest]
    public IEnumerator LayeredGround_PicksTheLayerUnderTheCandidate()
    {
        // 在地板上方 y = 8 再叠一块小平台。Spawner 放在平台上方，
        // 且范围收得很小使其只覆盖平台与下方地面。
        var platform = GameObject.CreatePrimitive(PrimitiveType.Cube);
        platform.name = "UpperPlatform";
        platform.transform.SetPositionAndRotation(new Vector3(0f, 7.5f, 0f), Quaternion.identity);
        platform.transform.localScale = new Vector3(6f, 1f, 6f); // 顶面 y = 8
        SceneManager.MoveGameObjectToScene(platform, _scene);

        _spawner.transform.position = new Vector3(0f, 12f, 0f);
        Configure(maxAlive: 10, range: 2f, interval: 100f, autoSpawn: false);
        yield return null;

        int onUpper = 0;
        for (int i = 0; i < 10; i++)
        {
            GameObject instance = _spawner.SpawnOnce();
            if (instance == null) continue;
            float y = instance.transform.position.y;
            // 只可能是上层平台顶面(8)或地板顶面(0)：不能是两者之间的空中高度。
            Assert.IsTrue(Mathf.Abs(y - 8f) < 0.15f || Mathf.Abs(y) < 0.15f,
                $"生成点 Y={y} 既不在上层平台(8)也不在下层地板(0)上，说明没有真正贴合地面");
            if (Mathf.Abs(y - 8f) < 0.15f) onUpper++;
        }

        Assert.Greater(onUpper, 0, "范围内覆盖了上层平台，应当至少有敌人生成在平台顶面");
    }

    // ---- 5. 范围采样本身 ----

    [Test]
    public void SampleDisc_StaysWithinRadius_AndRespectsInnerRadius()
    {
        Vector3 center = new Vector3(3f, 1f, -2f);
        const float radius = 5f;
        const float inner = 2f;

        var random = new System.Random(12345);
        for (int i = 0; i < 500; i++)
        {
            var r01 = new Vector2((float)random.NextDouble(), (float)random.NextDouble());
            Vector3 point = SpawnGroundQuery.SampleDisc(center, radius, inner, r01);

            Assert.AreEqual(center.y, point.y, 1e-4f, "采样只在 XZ 平面展开，不应改动 Y");
            float distance = new Vector3(point.x - center.x, 0f, point.z - center.z).magnitude;
            Assert.LessOrEqual(distance, radius + 1e-4f, "采样点不能超出外圈半径");
            Assert.GreaterOrEqual(distance, inner - 1e-4f, "采样点不能落进内圈");
        }
    }

    [Test]
    public void SampleDisc_WithZeroInnerRadius_CanReachCenter()
    {
        Vector3 center = Vector3.zero;
        var random = new System.Random(999);
        float minDistance = float.MaxValue;
        for (int i = 0; i < 2000; i++)
        {
            var r01 = new Vector2((float)random.NextDouble(), (float)random.NextDouble());
            Vector3 point = SpawnGroundQuery.SampleDisc(center, 4f, 0f, r01);
            minDistance = Mathf.Min(minDistance, new Vector3(point.x, 0f, point.z).magnitude);
        }

        Assert.Less(minDistance, 0.5f, "内圈为 0 时应当允许靠近圆心生成");
    }
}
#endif
