#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// 视线判定的回归测试：远程兵与重型激光「隔墙索敌」就是这段逻辑漏了。
// 只验判定本身（不需要子弹 / 预制体），够把最常见的回归钉住。
public sealed class SightCheckTests
{
    private readonly List<GameObject> _created = new List<GameObject>();
    private readonly RaycastHit[] _buffer = new RaycastHit[8];
    private Transform _target;
    private GameObject _wall;

    [SetUp]
    public void SetUp()
    {
        var go = new GameObject("SightTestTarget");
        _created.Add(go);
        go.transform.position = new Vector3(0f, 0f, 10f);
        var capsule = go.AddComponent<CapsuleCollider>();
        capsule.center = new Vector3(0f, 1f, 0f);
        capsule.height = 2f;
        capsule.radius = 0.4f;
        _target = go.transform;

        _wall = new GameObject("SightTestWall");
        _created.Add(_wall);
        _wall.transform.position = new Vector3(0f, 1f, 5f);
        _wall.AddComponent<BoxCollider>().size = new Vector3(4f, 4f, 1f);
        Physics.SyncTransforms();
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = _created.Count - 1; i >= 0; i--)
            if (_created[i] != null) Object.DestroyImmediate(_created[i]);
        _created.Clear();
    }

    [Test]
    public void WallBlocksLineOfSight()
    {
        bool clear = SightCheck.HasLineOfSight(Vector3.zero, _target, null, Physics.DefaultRaycastLayers, _buffer);
        Assert.IsFalse(clear, "墙挡在中间时必须判定为视线受阻（否则会隔墙开火）");
    }

    [Test]
    public void NoObstacleMeansClearSight()
    {
        Object.DestroyImmediate(_wall);
        Physics.SyncTransforms();
        bool clear = SightCheck.HasLineOfSight(Vector3.zero, _target, null, Physics.DefaultRaycastLayers, _buffer);
        Assert.IsTrue(clear, "没有遮挡时必须能看到目标");
    }

    [Test]
    public void OwnColliderDoesNotBlockSight()
    {
        // 枪口常埋在敌人自己的碰撞体里：自己的碰撞体不能算遮挡
        var self = new GameObject("SightTestSelf");
        _created.Add(self);
        self.transform.position = new Vector3(0f, 1f, 1f);
        self.AddComponent<BoxCollider>().size = new Vector3(2f, 2f, 2f);
        Physics.SyncTransforms();

        bool blockedByWall = SightCheck.HasLineOfSight(Vector3.zero, _target, self.transform, Physics.DefaultRaycastLayers, _buffer);
        Object.DestroyImmediate(_wall);
        Physics.SyncTransforms();
        bool clear = SightCheck.HasLineOfSight(Vector3.zero, _target, self.transform, Physics.DefaultRaycastLayers, _buffer);

        Assert.IsFalse(blockedByWall, "墙还在时依然是受阻");
        Assert.IsTrue(clear, "自己身上的碰撞体不能挡住视线");
    }

    [Test]
    public void MaskExcludesLayers()
    {
        // 墙放在 IgnoreRaycast 层，用默认掩码之外的层过滤应该看不到它 → 视线畅通
        _wall.layer = 2;   // Unity 内置 Ignore Raycast
        Physics.SyncTransforms();
        bool clear = SightCheck.HasLineOfSight(Vector3.zero, _target, null, Physics.DefaultRaycastLayers, _buffer);
        Assert.IsTrue(clear, "层掩码之外的东西不该算遮挡");
    }
}
#endif
