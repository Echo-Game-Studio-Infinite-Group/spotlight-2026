using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

// 输入仲裁与取消表回归（设计 §4.9 / §4.2）：
// 修复目标即旧版吞键病灶——组合仲裁先消费触发键后判条件，规则短路即吞；
// 新语义「先匹配全部条件，再消费触发键」+「Peek 不消费、Commit 才消费（激活失败零副作用）」
public class InputAndCancelTests
{
    private InputBuffer _buffer;

    private const float Window = 0.12f;

    [SetUp]
    public void SetUp()
    {
        _buffer = new InputBuffer(Window, 16);
    }

    private static InputSnapshot Snap(bool w = false, bool s = false, bool a = false, bool d = false,
        bool shift = false, bool q = false, bool mouse0 = false, bool mouse1 = false)
    {
        return new InputSnapshot
        {
            WHeld = w, SHeld = s, AHeld = a, DHeld = d,
            ShiftHeld = shift, QHeld = q, Mouse0Held = mouse0, Mouse1Held = mouse1,
        };
    }

    // a. 吞键回归（旧版必挂）：按右键时 W 未按——旧版 Turnaround 检查先吃掉 Mouse1，
    //    随后按 W 时 Accelerate 永远取不到该键；新版 Peek 不消费，右键按下沿活到 W 按下
    [Test]
    public void Arbitrate_RmbWithoutW_DoesNotSwallowForLaterAccelerate()
    {
        _buffer.Push(KeyCode.Mouse1);

        // 第一拍：右键按住、W 未按——组合不成立，落入时停（持续型，不消费）
        InputIntent first = _buffer.PeekIntent(Snap(mouse1: true));
        Assert.That(first, Is.EqualTo(InputIntent.TimeStop), "无修饰组合时右键长按应为时停语义");

        // 第二拍：W 按下——加速（W+右键）应能取到仍在缓冲的右键按下沿
        InputIntent second = _buffer.PeekIntent(Snap(w: true, mouse1: true));
        Assert.That(second, Is.EqualTo(InputIntent.Accelerate), "右键按下沿不得被先前失败组合吞掉");

        _buffer.ConsumeFor(InputIntent.Accelerate, Snap(w: true, mouse1: true));
        Assert.That(_buffer.HasBuffered(KeyCode.Mouse1), Is.False, "提交后触发键应被消费");
    }

    // b. 优先级：连斩（右键+W+左键）压过推斩（右键+左键）压过普攻（左键）
    [Test]
    public void Arbitrate_Priority_RashomonOverPushSlashOverAttack()
    {
        _buffer.Push(KeyCode.Mouse0);
        Assert.That(_buffer.PeekIntent(Snap(w: true, mouse1: true)), Is.EqualTo(InputIntent.Rashomon));
        Assert.That(_buffer.PeekIntent(Snap(mouse1: true)), Is.EqualTo(InputIntent.PushSlash));
        Assert.That(_buffer.PeekIntent(Snap()), Is.EqualTo(InputIntent.Attack));
    }

    // c. Peek 不消费：验证失败零副作用——同一按下沿可重复 Peek，直到提交才消失
    [Test]
    public void Peek_DoesNotConsume_UntilCommit()
    {
        _buffer.Push(KeyCode.Mouse0);
        Assert.That(_buffer.PeekIntent(Snap()), Is.EqualTo(InputIntent.Attack));
        Assert.That(_buffer.PeekIntent(Snap()), Is.EqualTo(InputIntent.Attack), "Peek 不应消费缓冲");

        _buffer.ConsumeFor(InputIntent.Attack, Snap());
        Assert.That(_buffer.PeekIntent(Snap()), Is.EqualTo(InputIntent.None), "提交消费后不再有意图");
    }

    // d. 冲刺修饰：Q+左键 → 冲刺而非普攻
    [Test]
    public void Arbitrate_QPlusMouse0_IsDash()
    {
        _buffer.Push(KeyCode.Mouse0);
        Assert.That(_buffer.PeekIntent(Snap(q: true)), Is.EqualTo(InputIntent.Dash));
    }

    // e. 闪避：A/D+Shift → Dodge
    [Test]
    public void Arbitrate_DirPlusShift_IsDodge()
    {
        _buffer.Push(KeyCode.LeftShift);
        Assert.That(_buffer.PeekIntent(Snap(a: true, shift: true)), Is.EqualTo(InputIntent.Dodge));
    }

    // f. 窗口过期：超窗按下沿作废（帧率无关——时间戳走 unscaled）
    [UnityTest]
    public IEnumerator Buffer_ExpiredOutsideWindow()
    {
        _buffer.Push(KeyCode.Mouse0);
        yield return new WaitForSecondsRealtime(Window + 0.15f);
        Assert.That(_buffer.PeekIntent(Snap()), Is.EqualTo(InputIntent.None), "超窗按下沿应作废");
    }

    // —— 取消表 —— //

    private static AttackDefinition NewDef(string name)
    {
        AttackDefinition def = ScriptableObject.CreateInstance<AttackDefinition>();
        def.DisplayName = name;
        return def;
    }

    private static AttackCancelTable NewTable(params CancelRule[] rules)
    {
        AttackCancelTable table = ScriptableObject.CreateInstance<AttackCancelTable>();
        table.Rules = rules;
        return table;
    }

    // g. 窗口匹配：段相位 + 相对时间窗 + 段号
    [Test]
    public void CancelTable_WindowMatching()
    {
        AttackDefinition a = NewDef("A");
        AttackDefinition b = NewDef("B");
        AttackCancelTable table = NewTable(new CancelRule
        {
            SourceAttack = a,
            SourcePhase = AttackPhaseKind.Recovery,
            WindowStart = 0f,
            WindowEnd = 0.1f,
            TargetAttack = b,
            RequiredIntent = InputIntent.Attack,
        });

        Assert.That(table.Query(a, 0, AttackPhaseKind.Recovery, 0.05f, InputIntent.Attack, 1f), Is.EqualTo(b),
            "恢复窗内应可取消");
        Assert.That(table.Query(a, 0, AttackPhaseKind.Recovery, 0.15f, InputIntent.Attack, 1f), Is.Null,
            "窗口外（0.15 > 0.1）不可取消");
        Assert.That(table.Query(a, 0, AttackPhaseKind.Startup, 0.05f, InputIntent.Attack, 1f), Is.Null,
            "相位不符不可取消");
        Assert.That(table.Query(a, 0, AttackPhaseKind.Recovery, 0.05f, InputIntent.Dash, 1f), Is.Null,
            "意图不符不可取消");
    }

    // h. 段号匹配：连斩前段（0-2）可取消、收尾段（3）无窗口即不可取消——「没有开窗口的位置就没有取消」
    [Test]
    public void CancelTable_SegmentScoping()
    {
        AttackDefinition a = NewDef("A");
        AttackDefinition b = NewDef("B");
        AttackCancelTable table = NewTable(new CancelRule
        {
            SourceAttack = a,
            SourceSegment = 0,
            SourcePhase = AttackPhaseKind.Any,
            TargetAttack = b,
            RequiredIntent = InputIntent.Attack,
        });

        Assert.That(table.Query(a, 0, AttackPhaseKind.Active, 0.01f, InputIntent.Attack, 1f), Is.EqualTo(b), "段 0 命中");
        Assert.That(table.Query(a, 1, AttackPhaseKind.Active, 0.01f, InputIntent.Attack, 1f), Is.Null, "段 1 无规则");
    }

    // i. 特例压过通配：同意图同时命中指定来源规则与通配规则时，特例先返回
    [Test]
    public void CancelTable_SpecificBeatsWildcard()
    {
        AttackDefinition a = NewDef("A");
        AttackDefinition b = NewDef("B");
        AttackDefinition c = NewDef("C");
        AttackCancelTable table = NewTable(
            new CancelRule { SourceAttack = null, TargetAttack = c, RequiredIntent = InputIntent.Attack },
            new CancelRule { SourceAttack = a, TargetAttack = b, RequiredIntent = InputIntent.Attack });

        Assert.That(table.Query(a, 0, AttackPhaseKind.Recovery, 0f, InputIntent.Attack, 1f), Is.EqualTo(b),
            "特例规则应压过通配");
        AttackDefinition other = NewDef("Other");
        Assert.That(table.Query(other, 0, AttackPhaseKind.Recovery, 0f, InputIntent.Attack, 1f), Is.EqualTo(c),
            "无特例命中时落通配（泛取消）");
    }

    // j. 附加条件：目标激活所需最低速度（速度形态门槛——「取消到高速变体」的守门）
    [Test]
    public void CancelTable_MinSpeedRatioGate()
    {
        AttackDefinition a = NewDef("A");
        AttackDefinition b = NewDef("B");
        AttackCancelTable table = NewTable(new CancelRule
        {
            SourceAttack = a,
            SourcePhase = AttackPhaseKind.Recovery,
            TargetAttack = b,
            RequiredIntent = InputIntent.Attack,
            MinSpeedRatio = 1.5f,
        });

        Assert.That(table.Query(a, 0, AttackPhaseKind.Recovery, 0f, InputIntent.Attack, 1.4f), Is.Null,
            "速度比不足最低门槛不可取消");
        Assert.That(table.Query(a, 0, AttackPhaseKind.Recovery, 0f, InputIntent.Attack, 1.5f), Is.EqualTo(b),
            "速度比达门槛（含等于）可取消");
    }

    // k. 窗口边界含端点：起止时刻本身都在窗口内（RuleMatches 的开闭区间语义）
    [Test]
    public void CancelTable_WindowBoundariesInclusive()
    {
        AttackDefinition a = NewDef("A");
        AttackDefinition b = NewDef("B");
        AttackCancelTable table = NewTable(new CancelRule
        {
            SourceAttack = a,
            SourcePhase = AttackPhaseKind.Recovery,
            WindowStart = 0.05f,
            WindowEnd = 0.1f,
            TargetAttack = b,
            RequiredIntent = InputIntent.Attack,
        });

        Assert.That(table.Query(a, 0, AttackPhaseKind.Recovery, 0.05f, InputIntent.Attack, 1f), Is.EqualTo(b),
            "窗口起点时刻应可取消（含端点）");
        Assert.That(table.Query(a, 0, AttackPhaseKind.Recovery, 0.1f, InputIntent.Attack, 1f), Is.EqualTo(b),
            "窗口终点时刻应可取消（含端点）");
        Assert.That(table.Query(a, 0, AttackPhaseKind.Recovery, 0.049f, InputIntent.Attack, 1f), Is.Null,
            "窗口起点前不可取消");
        Assert.That(table.Query(a, 0, AttackPhaseKind.Recovery, 0.101f, InputIntent.Attack, 1f), Is.Null,
            "窗口终点后不可取消");
    }

    // l. 清空缓冲：暂停进入/重开复位清旧输入（框架 4.1：不在恢复时补发）
    [Test]
    public void Buffer_Clear_RemovesAllEntries()
    {
        _buffer.Push(KeyCode.Mouse0);
        _buffer.Push(KeyCode.Mouse1);
        _buffer.Clear();

        Assert.That(_buffer.HasBuffered(KeyCode.Mouse0), Is.False, "清空后不得残留按下沿");
        Assert.That(_buffer.PeekIntent(Snap()), Is.EqualTo(InputIntent.None), "清空后仲裁应无意图");
    }

    // m. 容量满覆盖最旧：环形缓冲不丢新条目（快速连按不吞最后输入）
    [Test]
    public void Buffer_CapacityFull_OverwritesOldest()
    {
        _buffer.Push(KeyCode.Mouse0); // 最旧
        _buffer.Push(KeyCode.Q);
        _buffer.Push(KeyCode.Space);
        _buffer.Push(KeyCode.LeftShift); // 倒数第 16 条
        for (int i = 0; i < 14; i++) _buffer.Push(KeyCode.Alpha1 + i); // 累计 18 条 > 容量 16：最旧 3 条被覆盖
        _buffer.Push(KeyCode.Mouse1);  // 第 19 条，再次覆盖一轮最旧

        Assert.That(_buffer.HasBuffered(KeyCode.Mouse0), Is.False, "最旧条目应被覆盖（不无限增长）");
        Assert.That(_buffer.HasBuffered(KeyCode.Mouse1), Is.True, "最新条目应保留");
        Assert.That(_buffer.HasBuffered(KeyCode.LeftShift), Is.True, "覆盖轮次未到的条目应保留");
    }

    // n. 时停持续型不消费：ConsumeFor(TimeStop) 不得吞掉任何按下沿（长按右键语义不占缓冲条目）
    [Test]
    public void TimeStop_ConsumeNeverSwallowsEntries()
    {
        _buffer.Push(KeyCode.Mouse1);

        Assert.That(_buffer.PeekIntent(Snap(mouse1: true)), Is.EqualTo(InputIntent.TimeStop));
        _buffer.ConsumeFor(InputIntent.TimeStop, Snap(mouse1: true));

        Assert.That(_buffer.HasBuffered(KeyCode.Mouse1), Is.True,
            "时停提交不得消费右键按下沿（持续型无触发键）");
    }

    // o. 折返与高跳：规则表其余右键组合行（优先级基线待哈士奇核——本测锁现状顺序，改序只改断言参数）
    [Test]
    public void Arbitrate_TurnaroundAndHighJump_IntentMapping()
    {
        _buffer.Push(KeyCode.Mouse1);
        Assert.That(_buffer.PeekIntent(Snap(s: true)), Is.EqualTo(InputIntent.Turnaround),
            "S+右键按下沿应为折返");
        _buffer.ConsumeFor(InputIntent.Turnaround, Snap(s: true));
        Assert.That(_buffer.HasBuffered(KeyCode.Mouse1), Is.False, "折返提交应消费右键按下沿");

        _buffer.Push(KeyCode.Space);
        Assert.That(_buffer.PeekIntent(Snap(mouse1: true)), Is.EqualTo(InputIntent.HighJump),
            "右键按住+空格按下沿应为高跳");
        _buffer.ConsumeFor(InputIntent.HighJump, Snap(mouse1: true));
        Assert.That(_buffer.HasBuffered(KeyCode.Space), Is.False, "高跳提交应消费空格按下沿");
    }
}
