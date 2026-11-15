#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

// 慢速特效（全局时间 → 屏幕反馈）的回归测试。
//
// 验的是「时间真的慢下来时，画面有没有跟着变」：权重来自 GlobalRate，写进 Volume 的饱和度与暗角。
// 这条链断掉的表现是**沉默**的——游戏慢动作照常跑，画面一点没变，肉眼只能在实机里发现，
// 所以这里把它钉成断言。
public sealed class SlowMotionFeedbackTests
{
    private readonly List<Object> _created = new List<Object>();
    private Volume _volume;
    private VolumeProfile _profile;
    private CameraPostProcessing _feedback;

    [SetUp]
    public void SetUp()
    {
        var clock = new GameObject("SlowFeedbackClock"); _created.Add(clock);
        clock.AddComponent<TimeManager>();
        TimeManager.ClearSlowMotion();

        var host = new GameObject("SlowFeedbackCamera"); _created.Add(host);
        _profile = ScriptableObject.CreateInstance<VolumeProfile>(); _created.Add(_profile);
        _volume = host.AddComponent<Volume>();
        _volume.isGlobal = true;
        _volume.profile = _profile;
        _feedback = host.AddComponent<CameraPostProcessing>();
        // 相机组件在 Start 里补 Volume 引用与基准值；AddComponent 时不会跑，测试里显式调一次。
        Invoke(_feedback, "Start");
    }

    [TearDown]
    public void TearDown()
    {
        TimeManager.ClearSlowMotion();
        for (int i = _created.Count - 1; i >= 0; i--)
            if (_created[i] != null) Object.DestroyImmediate(_created[i]);
        _created.Clear();
    }

    private static void Invoke(object target, string method)
        => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, null);

    private void Advance(float seconds)
    {
        // 固定 60Hz 步长推进，和实机一致；Unscaled 时间由 TimeManager 提供。
        for (float t = 0f; t < seconds; t += 1f / 60f) _feedback.SimulateSlowFeedback(1f / 60f);
    }

    [Test]
    public void SlowWeight_MapsGlobalRateInversely()
    {
        Assert.AreEqual(0f, CameraPostProcessing.SlowWeightFor(1f), 0.0001f, "常速时不应有慢速表现");
        Assert.AreEqual(0.9f, CameraPostProcessing.SlowWeightFor(0.1f), 0.0001f, "压到 10% 应当接近满强度");
        Assert.AreEqual(1f, CameraPostProcessing.SlowWeightFor(0f), 0.0001f);
        Assert.AreEqual(0f, CameraPostProcessing.SlowWeightFor(2f), 0.0001f, "大于 1 的倍率不能变成负强度");
    }

    [Test]
    public void ParryStop_DrivesScreenFeedback()
    {
        float vignetteBefore = _feedback.CurrentVignette;
        // 反馈幅度跟随同一层倍率，支持策划配置的非零停顿倍率。
        TimeManager.ParryStop(0.35f, 0.1f);
        Assert.Less(TimeManager.GlobalRate, 1f, "前置条件：全局时间确实被压慢了");

        Advance(0.2f);

        Assert.Greater(_feedback.SlowWeight, 0.5f, "全局慢速期间慢速特效必须起来");
        Assert.Greater(_feedback.CurrentVignette, vignetteBefore, "暗角应当随慢速增强");
        ColorAdjustments color = null;
        Assert.IsTrue(_profile.TryGet(out color), "Volume 里应当补出 ColorAdjustments 来承载去饱和");
        Assert.Less(color.saturation.value, 0f, "慢速应当去饱和");
    }

    [Test]
    public void NoSlow_KeepsScreenNeutral()
    {
        Advance(0.5f);
        Assert.AreEqual(0f, _feedback.SlowWeight, 0.02f, "常速下不应有慢速表现");
    }

    [Test]
    public void SlowFeedback_ReturnsToBaseAfterEffectEnds()
    {
        TimeManager.ParryStop(0.1f, 0.1f);
        Advance(0.12f);
        Assert.Greater(_feedback.SlowWeight, 0.3f);
        // 测试无帧循环，显式释放已验证的时间来源。
        TimeManager.ClearSlowMotion();
        Advance(1.5f);
        Assert.AreEqual(0f, _feedback.SlowWeight, 0.02f, "慢速结束后权重必须回落到 0");
        Assert.AreEqual(0f, _feedback.CurrentVignette, 0.02f, "暗角要回到基准值");
    }
}
#endif
