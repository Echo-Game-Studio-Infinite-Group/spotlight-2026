#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class EnemyModelIntegrationTests
{
    private readonly List<GameObject> _created = new List<GameObject>();
    private readonly Vector3 _origin = new Vector3(16000f, 20f, 16000f);
    private Enemy _enemy;
    private EnemyAnimation _animation;
    private Animator _animator;

    [SetUp]
    public void SetUp()
    {
        Create("ModelTestClock").AddComponent<TimeManager>();
        TimeManager.SetPaused(false);
        TimeManager.ClearSlowMotion();
    }

    [TearDown]
    public void TearDown()
    {
        TimeManager.SetPaused(false);
        TimeManager.ClearSlowMotion();
        for (int i = _created.Count - 1; i >= 0; i--)
            if (_created[i] != null) Object.DestroyImmediate(_created[i]);
        _created.Clear();
    }

    private GameObject Create(string name)
    {
        var root = new GameObject(name); root.transform.position = _origin;
        _created.Add(root); return root;
    }

    private static void Set(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

    private GameObject Spawn(bool ranged)
    {
        var holder = Create("ModelTestInactiveHolder"); holder.SetActive(false);
        var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ranged ? "Assets/Prefabs/EnemyTestRange.prefab" : "Assets/Prefabs/EnemyTest.prefab"), holder.transform);
        root.SetActive(false);
        root.GetComponent<NavMeshAgent>().enabled = false;
        Set(root.GetComponent<GibComponent>(), "_deathChance", 0f);
        Set(root.GetComponent<EnemyDrops>(), "_dropChance", 0f);
        root.transform.SetParent(null); root.transform.position = _origin;
        _created.Add(root);
        _enemy = root.GetComponent<Enemy>(); _enemy.SetChasePlayer(false);
        _animation = root.GetComponentInChildren<EnemyAnimation>();
        Set(_animation, "_hurtHitStopSeconds", 0f);
        Set(_animation, "_deathHitStopSeconds", 0f);
        _animator = root.GetComponentInChildren<Animator>();
        root.SetActive(true);
        return root;
    }

    private HealthComponent Target(Vector3 position)
    {
        var target = Create("ModelTestTarget"); target.transform.position = position;
        target.AddComponent<CapsuleCollider>().height = 2f;
        var health = target.AddComponent<HealthComponent>(); health.SetInvulnerableTime(0f);
        target.AddComponent<CombatComponent>().Camp = CampType.Player;
        Physics.SyncTransforms();
        return health;
    }

    [UnityTest]
    public IEnumerator MeleeAvatarUsesCurrentLocomotionWithoutMovingRoot()
    {
        GameObject root = Spawn(false);
        Assert.IsTrue(_animator.isHuman && _animator.avatar.isValid);
        _animation.enabled = false;
        _animator.SetFloat("Speed", 5f);
        Transform foot = _animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        Vector3 start = foot.position; bool moved = false;
        for (int frame = 0; frame < 30; frame++)
        {
            yield return null;
            moved |= Vector3.Distance(start, foot.position) > .02f;
        }
        Assert.IsTrue(moved, "新骨骼必须实际播放现有跑步动作");
        Assert.Less(Vector3.Distance(_origin, root.transform.position), .001f);
    }

    [UnityTest]
    public IEnumerator RangedWalkAndRunAnimateTheNewSkeleton()
    {
        GameObject root = Spawn(true);
        _animation.enabled = false;
        Transform foot = System.Array.Find(root.GetComponentsInChildren<Transform>(true), t => t.name == "mixamorig:LeftFoot");
        Assert.IsNotNull(foot);
        foreach (float speed in new[] { 2f, 5f })
        {
            _animator.SetFloat("Speed", speed);
            Vector3 start = foot.position; bool moved = false;
            for (int frame = 0; frame < 30; frame++)
            {
                yield return null;
                moved |= Vector3.Distance(start, foot.position) > .02f;
            }
            Assert.IsTrue(moved, "走路/奔跑必须驱动新模型的脚部骨骼");
            Assert.Less(Vector3.Distance(_origin, root.transform.position), .001f);
        }
    }

    [UnityTest]
    public IEnumerator MeleeAttackAnimationOpensHitboxAndDamagesTarget()
    {
        GameObject root = Spawn(false);
        HealthComponent target = Target(_origin + Vector3.forward * 1.1f);
        var combat = root.GetComponent<CombatComponent>();
        Assert.IsTrue(combat.TryBeginAttack(TimeManager.WorldTime));
        _animation.PlayAttack();
        bool opened = false;
        Collider hitbox = root.GetComponentInChildren<Hitbox>(true).GetComponent<Collider>();
        float deadline = TimeManager.UnscaledTime + 5f;
        while (TimeManager.UnscaledTime < deadline && _enemy.IsAttacking)
        {
            opened |= hitbox.enabled;
            yield return null;
        }
        Assert.IsTrue(opened, "原近战动画事件必须仍能打开攻击判定");
        Assert.Less(target.Health, target.MaxHealth, "新模型的近战判定必须实际命中");
        Assert.IsFalse(_enemy.IsAttacking);
        Assert.IsFalse(hitbox.enabled);
    }

    [UnityTest]
    public IEnumerator RangedChargingAndActualShotDriveGenericAnimations()
    {
        GameObject root = Spawn(true);
        var ranged = root.GetComponent<RangeComponent>();
        HealthComponent target = Target(_origin + Vector3.forward * 5f);
        int fired = 0; ranged.Fired += () => fired++;
        Assert.IsFalse(ranged.TryFire(TimeManager.WorldTime, target.transform, target));
        Assert.IsTrue(ranged.IsCharging);
        yield return new WaitForSecondsRealtime(.15f);
        Assert.IsTrue(_animator.GetCurrentAnimatorStateInfo(0).IsName("Aim"));
        Assert.AreEqual(0, fired);
        float deadline = TimeManager.UnscaledTime + 2f;
        while (fired == 0 && TimeManager.UnscaledTime < deadline)
        {
            ranged.TryFire(TimeManager.WorldTime, target.transform, target);
            yield return null;
        }
        Assert.AreEqual(1, fired);
        for (int i = 0; i < 5; i++) yield return null;
        Assert.IsTrue(_animator.GetCurrentAnimatorStateInfo(0).IsName("Shoot"));
        Assert.IsFalse(ranged.IsCharging);
        Assert.IsTrue(ranged.Muzzle.IsChildOf(_animator.transform));
        Assert.Less(Vector3.Distance(ranged.Muzzle.position, root.transform.position), 2f);
        Assert.IsFalse(ranged.TryFire(TimeManager.WorldTime, target.transform, target));
        Assert.AreEqual(1, fired);
        deadline = TimeManager.UnscaledTime + ranged.Cooldown;
        while (_animator.GetCurrentAnimatorStateInfo(0).IsName("Shoot") && TimeManager.UnscaledTime < deadline)
            yield return null;
        Assert.IsTrue(_animator.GetCurrentAnimatorStateInfo(0).IsName("Move"), "射击动作必须在下一轮攻击前播完");
    }

    [UnityTest]
    public IEnumerator RangedChargeCancelsImmediatelyOutsideRangeAndRestartsWithFullWindup()
    {
        GameObject root = Spawn(true);
        var ranged = root.GetComponent<RangeComponent>(); ranged.Range = 10f;
        HealthComponent target = Target(_origin + Vector3.forward * 5f);
        float started = TimeManager.WorldTime;
        int fired = 0; ranged.Fired += () => fired++;
        Assert.IsFalse(ranged.TryFire(started, target.transform, target));
        yield return new WaitForSecondsRealtime(.1f);
        Assert.IsTrue(_animator.GetCurrentAnimatorStateInfo(0).IsName("Aim"));
        target.transform.position = _origin + Vector3.forward * 20f;
        Physics.SyncTransforms();
        Assert.IsFalse(ranged.TryFire(started + .1f, target.transform, target));
        Assert.IsFalse(ranged.IsCharging, "脱离射程必须在前摇结束前取消");
        Assert.IsFalse(root.GetComponent<AttackTelegraph>().IsPlaying);
        Assert.IsFalse(root.GetComponent<CombatComponent>().TryGetParryTarget(out _), "取消蓄力后不能残留可弹反的瞄准框");
        yield return new WaitForSecondsRealtime(.1f);
        Assert.IsFalse(_animator.GetBool("Charging"));
        Assert.IsTrue(_animator.GetCurrentAnimatorStateInfo(0).IsName("Move"));
        Assert.IsFalse(ranged.TryFire(started + ranged.TelegraphSeconds + .01f, target.transform, target));
        Assert.AreEqual(0, fired, "旧前摇到期后也不能向射程外补发子弹");
        target.transform.position = _origin + Vector3.forward * 5f;
        Physics.SyncTransforms();
        Assert.IsFalse(ranged.TryFire(started + .3f, target.transform, target));
        Assert.IsFalse(ranged.IsCharging, "取消不应重置原有冷却");
        float restart = started + ranged.Cooldown + .1f;
        Assert.IsFalse(ranged.TryFire(restart, target.transform, target));
        Assert.IsTrue(ranged.IsCharging);
        Assert.IsFalse(ranged.TryFire(restart + ranged.TelegraphSeconds * .5f, target.transform, target));
        Assert.AreEqual(0, fired, "重新进入射程必须重新经过完整前摇");
    }

    [UnityTest]
    public IEnumerator RangedChaseCancelsAimWhenItStopsCallingTryFire()
    {
        GameObject root = Spawn(true);
        var ranged = root.GetComponent<RangeComponent>(); ranged.Range = 10f;
        HealthComponent target = Target(_origin + Vector3.forward * 5f);
        target.gameObject.SetActive(false);
        target.gameObject.AddComponent<CharacterController>();
        var motor = target.gameObject.AddComponent<PlayerMotor>();
        motor.SetParams(AssetDatabase.LoadAssetAtPath<MovementParams>("Assets/Settings/MovementParams.asset"));
        target.gameObject.AddComponent<Player>();
        target.gameObject.SetActive(true); motor.enabled = false;
        ranged.TryFire(TimeManager.WorldTime, target.transform, target);
        yield return new WaitForSecondsRealtime(.1f);
        Assert.IsTrue(ranged.IsCharging);
        target.transform.position = _origin + Vector3.forward * 20f;
        Physics.SyncTransforms();
        typeof(EnemyBase).GetMethod("Chase", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_enemy, null);
        Assert.IsFalse(ranged.IsCharging, "AI 切入追击分支时必须自行清掉蓄力");
        Assert.IsFalse(root.GetComponent<AttackTelegraph>().IsPlaying);
        yield return new WaitForSecondsRealtime(.1f);
        Assert.IsTrue(_animator.GetCurrentAnimatorStateInfo(0).IsName("Move"), "实际模型必须放下瞄准姿势，恢复移动状态");
    }

    [UnityTest]
    public IEnumerator RangedAnimationStopsWithPublicPause()
    {
        Spawn(true);
        yield return null;
        TimeManager.SetPaused(true);
        yield return null;
        float before = _animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
        yield return new WaitForSecondsRealtime(.15f);
        Assert.AreEqual(before, _animator.GetCurrentAnimatorStateInfo(0).normalizedTime, .001f);
        Assert.AreEqual(0f, _animator.speed);
    }

    [UnityTest]
    public IEnumerator MeleeHurtAndDeathStillUseCurrentController() => HurtAndDeath(false);

    [UnityTest]
    public IEnumerator RangedHurtAndDeathUsePackAnimations() => HurtAndDeath(true);

    private IEnumerator HurtAndDeath(bool ranged)
    {
        GameObject root = Spawn(ranged);
        var health = root.GetComponent<HealthComponent>();
        health.TakeEnvironmentalDamage(1f);
        yield return new WaitForSecondsRealtime(.1f);
        Assert.IsTrue(_animation.IsHurting);
        float deadline = TimeManager.UnscaledTime + 4f;
        while (_animation.IsHurting && TimeManager.UnscaledTime < deadline) yield return null;
        Assert.IsFalse(_animation.IsHurting);
        Transform head = ranged ? System.Array.Find(root.GetComponentsInChildren<Transform>(true), t => t.name == "mixamorig:Head") : _animator.GetBoneTransform(HumanBodyBones.Head);
        Vector3 beforeDeath = head.position;
        health.TakeEnvironmentalDamage(health.MaxHealth);
        yield return new WaitForSecondsRealtime(1f);
        Assert.IsTrue(_animator.GetCurrentAnimatorStateInfo(0).IsName("Die"));
        Assert.Greater(Vector3.Distance(beforeDeath, head.position), .02f, "死亡片段必须驱动新模型，不能只切换状态名");
        Assert.IsFalse(health.IsAlive);
        Assert.IsFalse(_enemy.IsAttacking);
        Assert.IsTrue(_animator.GetBool("Dead"));
    }

    [UnityTest]
    public IEnumerator RangedPooledModelCanReturnToMovementAfterDeath()
    {
        GameObject root = Spawn(true);
        HealthComponent health = root.GetComponent<HealthComponent>();
        health.TakeEnvironmentalDamage(health.MaxHealth);
        yield return new WaitForSecondsRealtime(.1f);
        Assert.IsTrue(_animator.GetCurrentAnimatorStateInfo(0).IsName("Die"));
        root.SetActive(false);
        health.Reset();
        _animation.ResetAfterDeath();
        root.SetActive(true);
        yield return new WaitForSecondsRealtime(.1f);
        Assert.IsTrue(_animator.GetCurrentAnimatorStateInfo(0).IsName("Move"));
        Assert.IsTrue(health.IsAlive);
        Assert.IsTrue(_animation.CanAttack);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void NewMeshesStillSupportWaistCut(bool ranged)
    {
        GameObject root = Spawn(ranged);
        var gib = root.GetComponent<GibComponent>();
        Set(gib, "_deathChance", 1f);
        foreach (SkinnedMeshRenderer skin in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            Assert.IsTrue(skin.sharedMesh.isReadable, "断肢需要读取骨骼权重");
        _animator.Update(0f);
        Bounds originalBounds = ModelWorldBounds(root);
        root.GetComponent<HealthComponent>().TakeEnvironmentalDamage(1000f);
        Assert.IsTrue(gib.IsSliced);
        GameObject[] pieces = FindTestPieces(root);
        _created.AddRange(pieces);
        Assert.AreEqual(2, pieces.Length);
        Bounds slicedBounds = PieceWorldBounds(pieces);
        Assert.Less(Vector3.Distance(originalBounds.min, slicedBounds.min), .025f, "断肢必须保持原模型的世界尺寸和位置");
        Assert.Less(Vector3.Distance(originalBounds.max, slicedBounds.max), .025f, "不能只确认 IsSliced，尸块缩小也会返回成功");
        foreach (SkinnedMeshRenderer skin in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            Assert.IsFalse(skin.enabled);
    }
    [Test]
    public void RangedWaistCutLowerBodyFollowsDeathAtFullScale()
    {
        GameObject root = Spawn(true);
        _animator.Update(0f);
        var skin = root.GetComponentInChildren<SkinnedMeshRenderer>();
        var gib = root.GetComponent<GibComponent>();
        Set(gib, "_deathChance", 1f);
        root.GetComponent<HealthComponent>().TakeEnvironmentalDamage(1000f);
        GameObject[] pieces = FindTestPieces(root);
        _created.AddRange(pieces);
        GameObject lower = System.Array.Find(pieces, p => p.name == "WaistCut_Lower");
        Assert.IsNotNull(lower);
        var lowerFilter = lower.GetComponentInChildren<MeshFilter>();
        for (int frame = 0; frame < 4; frame++)
        {
            _animator.Update(.15f);
            typeof(GibComponent).GetMethod("UpdateLowerBodies", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(gib, null);
            Vector3[] expected = SkinWorldVertices(skin);
            Vector3[] actual = lowerFilter.sharedMesh.vertices;
            Vector2[] cut = lowerFilter.sharedMesh.uv2;
            int checkedVertices = 0;
            for (int i = 0; i < expected.Length; i += 31)
            {
                if (cut[i].x >= -.01f) continue;
                Assert.Less(Vector3.Distance(expected[i], lowerFilter.transform.TransformPoint(actual[i])), .025f,
                    "下半身逐帧烘焙也必须与原骨骼重合");
                checkedVertices++;
            }
            Assert.Greater(checkedVertices, 30);
        }
    }

    private GameObject[] FindTestPieces(GameObject root)
        => System.Array.FindAll(root.scene.GetRootGameObjects(), candidate =>
            candidate.name.StartsWith("WaistCut_") && Vector3.Distance(candidate.transform.position, _origin) < 5f);

    // 独立按骨骼权重计算世界顶点，避免用同一种烘焙方式掩盖缩放错误。
    private static Vector3[] SkinWorldVertices(SkinnedMeshRenderer skin)
    {
        Mesh mesh = skin.sharedMesh;
        Vector3[] vertices = mesh.vertices;
        BoneWeight[] weights = mesh.boneWeights;
        Matrix4x4[] matrices = new Matrix4x4[skin.bones.Length];
        Matrix4x4[] bindposes = mesh.bindposes;
        for (int i = 0; i < matrices.Length; i++) matrices[i] = skin.bones[i].localToWorldMatrix * bindposes[i];
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 v = vertices[i]; BoneWeight w = weights[i];
            vertices[i] = matrices[w.boneIndex0].MultiplyPoint3x4(v) * w.weight0
                + matrices[w.boneIndex1].MultiplyPoint3x4(v) * w.weight1
                + matrices[w.boneIndex2].MultiplyPoint3x4(v) * w.weight2
                + matrices[w.boneIndex3].MultiplyPoint3x4(v) * w.weight3;
        }
        return vertices;
    }

    private static Bounds ModelWorldBounds(GameObject root)
    {
        bool started = false; Bounds bounds = default;
        foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            foreach (Vector3 point in SkinWorldVertices(skin))
            {
                if (!started) { bounds = new Bounds(point, Vector3.zero); started = true; }
                else bounds.Encapsulate(point);
            }
        return bounds;
    }

    private static Bounds PieceWorldBounds(GameObject[] pieces)
    {
        bool started = false; Bounds bounds = default;
        foreach (var piece in pieces)
            foreach (var filter in piece.GetComponentsInChildren<MeshFilter>())
                foreach (Vector3 vertex in filter.sharedMesh.vertices)
                {
                    Vector3 point = filter.transform.TransformPoint(vertex);
                    if (!started) { bounds = new Bounds(point, Vector3.zero); started = true; }
                    else bounds.Encapsulate(point);
                }
        return bounds;
    }
}
#endif
