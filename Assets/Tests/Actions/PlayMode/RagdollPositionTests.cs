#if UNITY_EDITOR
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class RagdollPositionTests
{
    private GameObject _root, _ground;
    private PlayerMotor _motor;
    private PlayerRagdollHitStun _stun;
    private Vector3 _spawn, _destination;
    private MovementParams _testParameters;

    [SetUp]
    public void SetUp()
    {
        TimeManager.ClearSlowMotion();
        _spawn = new Vector3(18000f, 10f, 18000f);
        _destination = _spawn + Vector3.forward * 30f;
        _root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab"), _spawn, Quaternion.identity);
        foreach (Camera camera in _root.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        _motor = _root.GetComponent<PlayerMotor>();
        _stun = _root.GetComponent<PlayerRagdollHitStun>();
        _ground = new GameObject("RagdollPositionGround");
        _ground.transform.position = _spawn + Vector3.down * .5f;
        _ground.AddComponent<BoxCollider>().size = new Vector3(200f, 1f, 200f);
        Physics.SyncTransforms();
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(_root);
        Object.DestroyImmediate(_ground);
        if (_testParameters != null) Object.DestroyImmediate(_testParameters);
        TimeManager.ClearSlowMotion();
    }

    private void MoveAndRagdoll(bool moveNormally = false)
    {
        _root.GetComponent<PlayerInputReader>().SetGameplayEnabled(false);
        if (moveNormally)
        {
            _testParameters = Object.Instantiate(_motor.Params);
            _testParameters.Gravity = _testParameters.GroundFriction = _testParameters.RunAccel = 0f;
            _motor.SetParams(_testParameters);
            _motor.SetHorizontalSpeed(30f);
            for (int i = 0; i < 60; i++) _motor.Simulate(default, 1f / 60f, 0f);
        }
        else _motor.Teleport(_destination);
        _stun.EnterHitStun();
        Assert.IsTrue(_stun.IsActive);
        // 由测试主动推进起身入口，先保留几次真实物理步以验证骨骼落点。
        typeof(PlayerRagdollHitStun).GetField("_ragdollEndAt", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(_stun, Time.time + 100f);
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
        => Vector3.ProjectOnPlane(a - b, Vector3.up).magnitude;

    [UnityTest]
    public IEnumerator BeginGetUpKeepsRagdollLandingInsteadOfSpawn()
        => CheckGetUpPosition(false);

    [UnityTest]
    public IEnumerator NormalMovementThenGetUpKeepsRagdollLandingInsteadOfSpawn()
        => CheckGetUpPosition(true);

    private IEnumerator CheckGetUpPosition(bool moveNormally)
    {
        yield return null;
        MoveAndRagdoll(moveNormally);
        Vector3 afterEnter = _root.transform.position;
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();
        Transform hips = (Transform)typeof(PlayerRagdollHitStun)
            .GetField("_hips", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_stun);
        Vector3 landing = hips.position;
        Vector3 beforeGetUp = _root.transform.position;
        typeof(PlayerRagdollHitStun).GetMethod("BeginGetUp", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(_stun, null);
        Vector3 afterGetUp = _root.transform.position;
        string trace = $"spawn={_spawn}; target={_destination}; entered={afterEnter}; landing={landing}; before={beforeGetUp}; after={afterGetUp}";
        Assert.Greater(FlatDistance(landing, _spawn), 20f, "布娃娃骨骼不应回出生点：" + trace);
        Assert.Less(FlatDistance(afterGetUp, landing), .1f, "开始起身应保持骨骼落点：" + trace);
        _stun.ExitHitStun();
    }

    [UnityTest]
    public IEnumerator CombatSceneCollisionKeepsGetUpNearImpact()
    {
        Object.DestroyImmediate(_root);
        Object.DestroyImmediate(_ground);
        _root = _ground = null;
        yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
            "Assets/Scenes/CombatTest.unity", new UnityEngine.SceneManagement.LoadSceneParameters(UnityEngine.SceneManagement.LoadSceneMode.Single));
        yield return null;
        _motor = Object.FindObjectOfType<PlayerMotor>();
        _root = _motor.gameObject;
        _stun = _root.GetComponent<PlayerRagdollHitStun>();
        _root.GetComponent<PlayerInputReader>().SetGameplayEnabled(false);
        Vector3 spawn = _root.transform.position;
        Vector3 impactPosition = spawn;
        bool hit = false;
        int teleports = 0;
        _motor.Teleported += delta => teleports++;
        Vector3 hipsBeforeImpact = Vector3.zero, animatedHipsBeforeImpact = Vector3.zero;
        string animatedHipName = "none";
        _stun.enabled = false;
        _motor.WallImpact += contact =>
        {
            Transform cachedHip = (Transform)typeof(PlayerRagdollHitStun)
                .GetField("_hips", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_stun);
            hipsBeforeImpact = cachedHip.position;
            Animator animator = _root.GetComponent<Animator>();
            Transform animatedHip = animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            if (animatedHip != null) { animatedHipName = animatedHip.name; animatedHipsBeforeImpact = animatedHip.position; }
        };
        _stun.enabled = true;
        _motor.WallImpact += contact => { hit = true; impactPosition = _root.transform.position; };
        _root.transform.rotation = Quaternion.identity;
        _motor.SetHorizontalSpeed(20f);
        for (int i = 0; i < 120 && !_stun.IsActive; i++) yield return new WaitForFixedUpdate();
        Assert.IsTrue(hit && _stun.IsActive, "场景测试必须真实撞墙并进入瘫倒");
        Transform hips = (Transform)typeof(PlayerRagdollHitStun)
            .GetField("_hips", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_stun);
        FieldInfo gettingUp = typeof(PlayerRagdollHitStun).GetField("_inGetUp", BindingFlags.Instance | BindingFlags.NonPublic);
        Vector3 landing = hips.position;
        // 无图形批测可在一秒内跑完上百渲染帧；等待真实计时，才能覆盖完整的一秒布娃娃。
        float getUpDeadline = Time.time + 2f;
        float timeout = Time.realtimeSinceStartup + 10f;
        while (!(bool)gettingUp.GetValue(_stun) && Time.time < getUpDeadline && Time.realtimeSinceStartup < timeout)
        {
            landing = hips.position;
            yield return null;
        }
        Vector3 after = _root.transform.position;
        string trace = $"spawn={spawn}; impact={impactPosition}; hipsBeforeGetUp={landing}; rootAfterGetUp={after}; teleports={teleports}; cachedHipBeforeImpact={hipsBeforeImpact}; animatedHip={animatedHipName}@{animatedHipsBeforeImpact}";
        Assert.IsTrue((bool)gettingUp.GetValue(_stun), "应进入起身阶段：" + trace);
        Assert.Less(FlatDistance(landing, impactPosition), 1f, "骨骼不应留在出生点：" + trace);
        Assert.Less(FlatDistance(after, landing), .5f, "起身不应把根节点搬回出生点：" + trace);
    }

    [UnityTest]
    public IEnumerator FinishingHitStunAndReconnectingControlsKeepPosition()
    {
        yield return null;
        MoveAndRagdoll();
        Vector3 before = _root.transform.position;
        _stun.ExitHitStun();
        Assert.Less(FlatDistance(_root.transform.position, before), .1f,
            $"恢复动作系统不应把玩家搬回出生点：spawn={_spawn}; before={before}; after={_root.transform.position}");
    }
}
#endif
