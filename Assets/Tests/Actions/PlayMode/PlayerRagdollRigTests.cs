#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class PlayerRagdollRigTests
{
    private GameObject _root, _ground;
    private PlayerRagdollHitStun _stun;
    private Animator _animator;
    private Transform _hips;
    private Rigidbody[] _bodies;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [SetUp]
    public void SetUp()
    {
        _root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab"),
            new Vector3(19000f, 20f, 19000f), Quaternion.identity);
        foreach (Camera camera in _root.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        _root.GetComponent<PlayerInputReader>().SetGameplayEnabled(false);
        _animator = _root.GetComponent<Animator>();
        _stun = _root.GetComponent<PlayerRagdollHitStun>();
        _hips = _animator.GetBoneTransform(HumanBodyBones.Hips);
        _bodies = _hips.GetComponentsInChildren<Rigidbody>(true);
        TimeManager.ClearSlowMotion();
        TimeManager.SetPaused(true);
        Physics.SyncTransforms();
    }

    [TearDown]
    public void TearDown()
    {
        if (_stun != null) _stun.ExitHitStun();
        if (_root != null) Object.DestroyImmediate(_root);
        if (_ground != null) Object.DestroyImmediate(_ground);
        TimeManager.SetPaused(false);
        TimeManager.ClearSlowMotion();
    }

    private T Read<T>(string field)
        => (T)typeof(PlayerRagdollHitStun).GetField(field, PrivateInstance).GetValue(_stun);

    [Test]
    public void PhysicsBelongsToVisibleAvatarInsteadOfLegacySkeleton()
    {
        Assert.AreSame(_hips, Read<Transform>("_hips"));
        Assert.That(_hips.name, Is.EqualTo("CC_Base_Hip"));
        Assert.That(_bodies.Length, Is.EqualTo(11));
        var visibleSkins = _root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(skin => skin.enabled && skin.gameObject.activeInHierarchy).ToArray();
        foreach (Rigidbody body in _bodies)
        {
            Assert.IsTrue(visibleSkins.Any(skin => skin.bones.Contains(body.transform)),
                "物理骨骼必须参与当前可见模型的蒙皮：" + body.name);
            Assert.IsTrue(body.isKinematic);
            Assert.IsTrue(body.useGravity);
            Assert.IsFalse(body.GetComponent<Collider>().enabled);
            if (body.transform == _hips) continue;
            CharacterJoint joint = body.GetComponent<CharacterJoint>();
            Assert.IsNotNull(joint, body.name);
            Assert.Contains(joint.connectedBody, _bodies, body.name);
        }
        Transform legacyHips = _root.GetComponentsInChildren<Transform>(true)
            .Single(bone => bone.name == "mixamorig:Hips");
        Assert.IsEmpty(legacyHips.GetComponentsInChildren<Rigidbody>(true));
        Assert.IsEmpty(legacyHips.GetComponentsInChildren<Joint>(true));
        Assert.IsEmpty(legacyHips.GetComponentsInChildren<Collider>(true));
    }

    [UnityTest]
    public IEnumerator RagdollGravityDeformsTheVisibleSkinnedMesh()
    {
        // 验证蒙皮本身随物理下落，防止只检查 IsActive 而漏掉旧骨架在动的回归。
        _animator.Update(0f);
        var skin = _root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(value => value.enabled && value.bones.Contains(_hips))
            .OrderByDescending(value => value.sharedMesh.vertexCount).First();
        var mesh = new Mesh();
        try
        {
            skin.BakeMesh(mesh);
            Vector3 beforeSkin = skin.transform.TransformPoint(mesh.bounds.center);
            Vector3 beforeHips = _hips.position;
            Vector3 rootPosition = _root.transform.position;
            _stun.EnterHitStun();
            Assert.IsFalse(_animator.enabled);
            Assert.IsTrue(_bodies.All(body => !body.isKinematic && body.GetComponent<Collider>().enabled));
            TimeManager.SetPaused(false);
            for (int i = 0; i < 18; i++) yield return new WaitForFixedUpdate();
            Assert.IsFalse(Read<bool>("_inGetUp"));
            Assert.AreEqual(rootPosition, _root.transform.position);
            Assert.Less(_hips.position.y, beforeHips.y - .2f, "当前 Avatar 的髋部必须受重力驱动");
            skin.BakeMesh(mesh);
            Assert.Less(skin.transform.TransformPoint(mesh.bounds.center).y, beforeSkin.y - .15f,
                "可见蒙皮必须跟随物理骨架下落");
            foreach (CharacterJoint joint in _hips.GetComponentsInChildren<CharacterJoint>(true))
                Assert.Less(Vector3.Distance(joint.transform.TransformPoint(joint.anchor),
                    joint.connectedBody.transform.TransformPoint(joint.connectedAnchor)), .1f,
                    "关节不应把模型拉散：" + joint.name);
        }
        finally { Object.DestroyImmediate(mesh); }
    }

    [UnityTest]
    public IEnumerator RagdollLastsOneSecondThenRestoresAnimatedRecovery()
    {
        _ground = new GameObject("RagdollRigFloor");
        _ground.transform.position = _root.transform.position + Vector3.down * .5f;
        _ground.AddComponent<BoxCollider>().size = new Vector3(100f, 1f, 100f);
        Physics.SyncTransforms();
        var data = new SerializedObject(_stun);
        Assert.That(data.FindProperty("_ragdollSeconds").floatValue, Is.EqualTo(1f));
        TimeManager.SetPaused(false);
        float started = Time.time;
        _stun.EnterHitStun();
        Assert.That(Read<float>("_ragdollEndAt") - started, Is.EqualTo(1f).Within(.001f));
        while (Time.time < started + .85f) yield return null;
        Assert.IsFalse(Read<bool>("_inGetUp"));
        Assert.IsFalse(_animator.enabled);
        Assert.IsTrue(_bodies.All(body => !body.isKinematic));
        while (!Read<bool>("_inGetUp") && Time.time < started + 1.5f) yield return null;
        Assert.IsTrue(Read<bool>("_inGetUp"));
        Assert.IsTrue(_stun.IsActive);
        Assert.IsTrue(_animator.enabled);
        Assert.IsTrue(_root.GetComponent<PlayerMotor>().enabled);
        Assert.IsFalse(_root.GetComponent<PlayerInputReader>().GameplayEnabled);
        Assert.IsTrue(_bodies.All(body => body.isKinematic && !body.GetComponent<Collider>().enabled));
        _stun.ExitHitStun();
        Assert.IsFalse(_stun.IsActive);
        Assert.IsTrue(_root.GetComponent<PlayerInputReader>().GameplayEnabled);
        Assert.IsTrue(_root.GetComponent<CharacterController>().enabled);
    }
}
#endif
