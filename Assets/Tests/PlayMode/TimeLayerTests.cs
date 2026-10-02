using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 时间层语义回归（战斗系统底层接口设计 §4.8 / 框架 4.3）：
// 来源登记同层取最小、释放恢复、限时缩放自动解除（解除计时走 unscaled 不自锁）、暂停全层置零、hit-stop 不自锁
// dt 断言走 WaitForSecondsRealtime 推进真实时间——unscaled 计时不受 timeScale 干扰，语义即被测对象
public class TimeLayerTests
{
    private GameObject _timeGo;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        Time.timeScale = 1f;
        TimeManager.ResetAll();
        _timeGo = new GameObject("TimeManager_Test");
        _timeGo.AddComponent<TimeManager>();
        yield return new WaitForSecondsRealtime(0.05f); // 至少跑数个 fixed tick，产出非零 dt
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        TimeManager.ResetAll();
        if (_timeGo != null) Object.Destroy(_timeGo);
        yield return null;
    }

    // a. 世界层登记 0.5x：只影响世界层；固定步 dt = fixedDeltaTime × 0.5；释放后恢复
    [UnityTest]
    public IEnumerator RegisterScale_WorldOnly_MinAndRestore()
    {
        TimeScaleHandle handle = TimeManager.RegisterScale(TimeLayer.World, 0.5f, "test_a");
        yield return new WaitForSecondsRealtime(0.05f);
        Assert.That(TimeManager.WorldDeltaTime, Is.EqualTo(Time.fixedDeltaTime * 0.5f).Within(1e-4f),
            "世界层 dt 应为 fixedDeltaTime × 0.5");
        Assert.That(TimeManager.PlayerDeltaTime, Is.EqualTo(Time.fixedDeltaTime).Within(1e-4f),
            "玩家层不受世界层来源影响");

        TimeManager.Release(handle);
        yield return new WaitForSecondsRealtime(0.05f);
        Assert.That(TimeManager.WorldDeltaTime, Is.EqualTo(Time.fixedDeltaTime).Within(1e-4f),
            "释放来源后世界层应恢复");
    }

    // b. 同层多来源取最小（互不覆盖——时停/时缓/关卡效果共存的基础）
    [UnityTest]
    public IEnumerator MultipleSources_SameLayer_TakesMinimum()
    {
        TimeScaleHandle h1 = TimeManager.RegisterScale(TimeLayer.World, 0.5f, "test_b1");
        TimeScaleHandle h2 = TimeManager.RegisterScale(TimeLayer.World, 0.2f, "test_b2");
        yield return new WaitForSecondsRealtime(0.05f);
        Assert.That(TimeManager.WorldDeltaTime, Is.EqualTo(Time.fixedDeltaTime * 0.2f).Within(1e-4f),
            "同层两来源（0.5 / 0.2）应取最小 0.2");

        TimeManager.Release(h2);
        yield return new WaitForSecondsRealtime(0.05f);
        Assert.That(TimeManager.WorldDeltaTime, Is.EqualTo(Time.fixedDeltaTime * 0.5f).Within(1e-4f),
            "释放最小来源后回升到 0.5（另一来源仍在）");
        TimeManager.Release(h1);
    }

    // c. 限时缩放自动解除：解除计时走真实时间轴——若错误地走缩放后的层时间，0.08s 会拖成数秒（自锁）
    [UnityTest]
    public IEnumerator TimedScale_ExpiresOnUnscaledClock()
    {
        TimeManager.RegisterTimedScale(TimeLayer.World, 0.1f, 0.08f, "test_c");
        yield return new WaitForSecondsRealtime(0.05f);
        Assert.That(TimeManager.WorldDeltaTime, Is.LessThan(Time.fixedDeltaTime * 0.5f),
            "限时缩放生效期间世界层应被压到 0.1x");

        yield return new WaitForSecondsRealtime(0.25f); // 真实时间远超时长
        Assert.That(TimeManager.WorldDeltaTime, Is.EqualTo(Time.fixedDeltaTime).Within(1e-4f),
            "限时缩放应按 unscaled 时钟到时自动解除");
    }

    // d. 暂停：全层置零（UI/相机走 unscaled 的约定由使用方遵守，本测只锁逻辑层行为）
    [UnityTest]
    public IEnumerator Pause_FreezesBothLayers()
    {
        TimeManager.SetPaused(true);
        yield return new WaitForSecondsRealtime(0.05f);
        Assert.That(TimeManager.WorldDeltaTime, Is.EqualTo(0f).Within(1e-6f), "暂停时世界层 dt = 0");
        Assert.That(TimeManager.PlayerDeltaTime, Is.EqualTo(0f).Within(1e-6f), "暂停时玩家层 dt = 0");
        Assert.That(TimeManager.UnscaledDeltaTime, Is.GreaterThan(0f), "unscaled 时间不受暂停影响");

        TimeManager.SetPaused(false);
        yield return new WaitForSecondsRealtime(0.05f);
        Assert.That(TimeManager.PlayerDeltaTime, Is.EqualTo(Time.fixedDeltaTime).Within(1e-4f), "恢复后玩家层 dt 回满");
    }

    // e. hit-stop 不自锁：0.05s 冻结在真实时间轴上到期——若按冻结后的 dt 计时会拖 20 倍时长
    [UnityTest]
    public IEnumerator HitStop_ExpiresOnUnscaledClock()
    {
        TimeManager.HitStop(0.05f, 0.05f);
        Assert.That(TimeManager.InHitStop, Is.True, "hit-stop 应立即生效");

        yield return new WaitForSecondsRealtime(0.2f);
        Assert.That(TimeManager.InHitStop, Is.False, "hit-stop 应按 unscaled 时钟到期（不自锁）");
        Assert.That(TimeManager.PlayerDeltaTime, Is.EqualTo(Time.fixedDeltaTime).Within(1e-4f));
    }
}
