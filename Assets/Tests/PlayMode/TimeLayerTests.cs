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

    // e2. hit-stop 生效期间两层 dt 同压到冻结 scale（e 只测到期，本测锁生效期读数）
    [UnityTest]
    public IEnumerator HitStop_ActivePeriod_ScalesBothLayers()
    {
        TimeManager.HitStop(0.2f, 0.1f);
        yield return new WaitForSecondsRealtime(0.05f);

        Assert.That(TimeManager.WorldDeltaTime, Is.EqualTo(Time.fixedDeltaTime * 0.1f).Within(1e-4f),
            "生效期世界层 dt 应压到 hit-stop scale");
        Assert.That(TimeManager.PlayerDeltaTime, Is.EqualTo(Time.fixedDeltaTime * 0.1f).Within(1e-4f),
            "生效期玩家层 dt 应同压（hit-stop 是两层同步冻结，不分公司）");
    }

    // e3. hit-stop 叠加语义：重复调用取更长冻结与更小 scale（多段连击命中的实际场景）
    [UnityTest]
    public IEnumerator HitStop_Repeated_TakesLongerAndSmaller()
    {
        TimeManager.HitStop(0.05f, 0.2f);
        TimeManager.HitStop(0.2f, 0.1f); // 更长：覆盖 timer 与 scale
        yield return new WaitForSecondsRealtime(0.08f); // 超过第一段时长，仍在第二段内
        Assert.That(TimeManager.InHitStop, Is.True, "更长的冻结应生效");
        Assert.That(TimeManager.PlayerDeltaTime, Is.EqualTo(Time.fixedDeltaTime * 0.1f).Within(1e-4f),
            "更小的 scale 应生效");

        TimeManager.HitStop(0.2f, 0.5f); // 更短：不得缩短已在进行的冻结
        yield return new WaitForSecondsRealtime(0.02f);
        Assert.That(TimeManager.InHitStop, Is.True, "更短调用不得打断更长冻结");
        Assert.That(TimeManager.PlayerDeltaTime, Is.EqualTo(Time.fixedDeltaTime * 0.1f).Within(1e-4f),
            "更大 scale 不得放宽已生效的更小 scale");
    }

    // f. 暂停不覆盖来源：退出暂停后已登记的来源缩放照常生效（「互不覆盖」验收项）
    [UnityTest]
    public IEnumerator Pause_DoesNotConsumeRegisteredSources()
    {
        TimeScaleHandle handle = TimeManager.RegisterScale(TimeLayer.World, 0.5f, "test_f");
        yield return new WaitForSecondsRealtime(0.03f);

        TimeManager.SetPaused(true);
        yield return new WaitForSecondsRealtime(0.03f);
        Assert.That(TimeManager.WorldDeltaTime, Is.EqualTo(0f).Within(1e-6f), "暂停中世界层 dt = 0");

        TimeManager.SetPaused(false);
        yield return new WaitForSecondsRealtime(0.03f);
        Assert.That(TimeManager.WorldDeltaTime, Is.EqualTo(Time.fixedDeltaTime * 0.5f).Within(1e-4f),
            "退出暂停后来源缩放应原样恢复——暂停不得吞掉已登记来源");

        TimeManager.Release(handle);
    }

    // g. 重开复位：清全部来源/暂停/hit-stop（框架 4.5「重开无残留」的时间侧）
    [UnityTest]
    public IEnumerator ResetAll_ClearsSourcesPauseAndHitStop()
    {
        TimeManager.RegisterScale(TimeLayer.World, 0.3f, "test_g1");
        TimeManager.RegisterScale(TimeLayer.Player, 0.3f, "test_g2");
        TimeManager.SetPaused(true);
        TimeManager.HitStop(1f, 0.05f);

        TimeManager.ResetAll();
        yield return new WaitForSecondsRealtime(0.03f);

        Assert.That(TimeManager.WorldDeltaTime, Is.EqualTo(Time.fixedDeltaTime).Within(1e-4f), "世界来源应被清");
        Assert.That(TimeManager.PlayerDeltaTime, Is.EqualTo(Time.fixedDeltaTime).Within(1e-4f), "玩家来源应被清");
        Assert.That(TimeManager.IsPaused, Is.False, "暂停应被清");
        Assert.That(TimeManager.InHitStop, Is.False, "hit-stop 应被清");
    }

    // h. Release 幂等：无效句柄与重复释放不抛异常（对象失效路径的鲁棒性）
    [Test]
    public void Release_InvalidOrRepeatedHandle_IsIdempotent()
    {
        Assert.DoesNotThrow(() => TimeManager.Release(default), "默认句柄（从未登记）不得抛异常");

        TimeScaleHandle handle = TimeManager.RegisterScale(TimeLayer.World, 0.5f, "test_h");
        TimeManager.Release(handle);
        Assert.DoesNotThrow(() => TimeManager.Release(handle), "重复释放同一句柄不得抛异常");
    }

    // i. 世界层完全冻结（scale=0，时停的机制形态）：只锁机制不锁作用层——
    //    时停登记在哪层是 D1 待决项，但「单层压 0 恒停、另一层照常走满、释放即恢复」是支撑任意裁决的底座
    [UnityTest]
    public IEnumerator WorldLayerFullFreeze_DoesNotTouchOtherLayer()
    {
        TimeScaleHandle handle = TimeManager.RegisterScale(TimeLayer.World, 0f, "test_i");
        yield return new WaitForSecondsRealtime(0.05f);

        Assert.That(TimeManager.WorldDeltaTime, Is.EqualTo(0f).Within(1e-6f), "压 0 层应完全冻结（dt 恒 0）");
        float frozenAt = TimeManager.WorldTime;
        float playerElapsed = TimeManager.PlayerTime;
        yield return new WaitForSecondsRealtime(0.05f);
        Assert.That(TimeManager.WorldTime, Is.EqualTo(frozenAt).Within(1e-4f), "冻结层时间戳应停止累计");
        Assert.That(TimeManager.PlayerTime, Is.GreaterThan(playerElapsed), "另一层时间照常累计");
        Assert.That(TimeManager.PlayerDeltaTime, Is.EqualTo(Time.fixedDeltaTime).Within(1e-4f),
            "另一层应照常走满（时停中玩家/世界各自行动的机制前提）");

        TimeManager.Release(handle);
        yield return new WaitForSecondsRealtime(0.05f);
        Assert.That(TimeManager.WorldDeltaTime, Is.EqualTo(Time.fixedDeltaTime).Within(1e-4f), "释放后冻结层恢复");
    }

    // j. 渲染帧 dt 分离（TimeManager v2 核心特性）：两层 render delta 同乘一帧的 unscaled，
    //    其比值应精确等于层缩放比——与帧时长波动无关（表现层读 *RenderDeltaTime 的依据，框架 4.3）
    [UnityTest]
    public IEnumerator RenderDeltaTime_TracksLayerScaleIndependently()
    {
        TimeScaleHandle handle = TimeManager.RegisterScale(TimeLayer.World, 0.25f, "test_j");
        yield return null; // render delta 在 Update 里产出——至少跑一帧

        Assert.That(TimeManager.WorldRenderDeltaTime, Is.GreaterThan(0f), "渲染 dt 应为正");
        float ratio = TimeManager.WorldRenderDeltaTime / TimeManager.PlayerRenderDeltaTime;
        Assert.That(ratio, Is.EqualTo(0.25f).Within(0.02f),
            "两层渲染 dt 之比应等于层缩放（0.25）——帧时长同源，比值不受帧率波动影响");

        TimeManager.Release(handle);
        yield return null;
        ratio = TimeManager.WorldRenderDeltaTime / TimeManager.PlayerRenderDeltaTime;
        Assert.That(ratio, Is.EqualTo(1f).Within(0.02f), "释放后两层渲染 dt 应同速");
    }
}
