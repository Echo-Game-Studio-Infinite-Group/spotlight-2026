#if UNITY_EDITOR
using System.Collections.Generic;
using Cinemachine;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// VCameraEffects 的 parry 运镜回归测试。
//
// 验的是「parry 一下镜头推近，parry 完自己回位」这条曲线，而不是某个内部字段：
// 推镜力度上去 → FOV 收窄 + 跟随臂拉近 → 保持时间走完 → 全部回到基准。
// 这条链断掉的表现是镜头卡在推近状态（或者根本不推），两种都很难在实机里判断是参数问题还是没接上。
public sealed class VCameraEffectsTests
{
    private readonly List<GameObject> _created = new List<GameObject>();
    private CinemachineVirtualCamera _vcam;
    private Cinemachine3rdPersonFollow _follow;
    private VCameraEffects _effects;
    private float _baseDistance;
    private float _baseFov;

    [SetUp]
    public void SetUp()
    {
        var host = new GameObject("ParryCameraFixture"); _created.Add(host);
        _vcam = host.AddComponent<CinemachineVirtualCamera>();
        _follow = _vcam.AddCinemachineComponent<Cinemachine3rdPersonFollow>();
        Assert.IsNotNull(_follow, "测试夹具需要 Cinemachine3rdPersonFollow 才能验跟随臂拉近");
        _follow.CameraDistance = 6f;
        _baseDistance = _follow.CameraDistance;
        _baseFov = _vcam.m_Lens.FieldOfView;
        // Awake 会自己取 vcam / follow 并补 ImpulseListener
        _effects = host.AddComponent<VCameraEffects>();
    }

    [TearDown]
    public void TearDown()
    {
        for (int i = _created.Count - 1; i >= 0; i--)
            if (_created[i] != null) Object.DestroyImmediate(_created[i]);
        _created.Clear();
    }

    private void Advance(float seconds)
    {
        for (float t = 0f; t < seconds; t += 1f / 60f) _effects.SimulateCameraEffects(1f / 60f);
    }

    [Test]
    public void ParryZoom_PunchesIn_ThenReturnsToBase()
    {
        VCameraEffects.PlayParryZoom();
        Advance(0.12f);

        Assert.Greater(_effects.ParryZoomWeight, 0.5f, "parry 之后镜头应当明显推近");
        Assert.Less(_effects.CurrentFieldOfView, _baseFov - 1f, "推近必须收窄 FOV");
        Assert.Less(_effects.CurrentDistance, _baseDistance - 0.1f, "推近同时应当把跟随臂拉近");

        // 保持时间走完 + 释放响应收敛，镜头必须回到基准，不能卡在推近状态
        Advance(3f);
        Assert.AreEqual(0f, _effects.ParryZoomWeight, 0.01f, "parry 推镜必须能自己回位");
        Assert.AreEqual(_baseFov, _effects.CurrentFieldOfView, 0.05f, "FOV 要回到基准");
        Assert.AreEqual(_baseDistance, _effects.CurrentDistance, 0.05f, "跟随臂距离要回到基准");
    }

    [Test]
    public void NoParry_KeepsCameraAtBase()
    {
        Advance(0.5f);
        Assert.AreEqual(0f, _effects.ParryZoomWeight, 0.01f, "没 parry 时不该推镜");
        Assert.AreEqual(_baseFov, _effects.CurrentFieldOfView, 0.05f);
        Assert.AreEqual(_baseDistance, _effects.CurrentDistance, 0.05f);
    }

    [Test]
    public void RepeatedParry_DoesNotStackBeyondFullPunch()
    {
        VCameraEffects.PlayParryZoom();
        Advance(0.1f);
        VCameraEffects.PlayParryZoom();
        Advance(0.1f);
        Assert.LessOrEqual(_effects.ParryZoomWeight, 1.001f, "连续 parry 不能把推镜叠到超过满幅");
        Assert.Less(_effects.CurrentFieldOfView, _baseFov + 0.01f);
    }

    [Test]
    public void Emit_StillDrivesImpulseDirection()
    {
        var sourceGo = new GameObject("ParryCameraImpulseSource"); _created.Add(sourceGo);
        var source = sourceGo.AddComponent<CinemachineImpulseSource>();
        source.m_DefaultVelocity = Vector3.zero;

        VCameraEffects.Emit(source, Vector3.forward, 20f);

        // Normalize 依赖静态实例：Emit 会在生成 impulse 前把方向写进 source，方向被改写即证明链路通
        Assert.Greater(source.m_DefaultVelocity.magnitude, 0.01f, "震屏方向应当被写入");
    }
}
#endif
