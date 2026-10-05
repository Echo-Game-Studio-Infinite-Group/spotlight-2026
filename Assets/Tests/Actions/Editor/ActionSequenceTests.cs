using System.Collections.Generic;
using GameJam.Actions;
using NUnit.Framework;
using UnityEngine;

public sealed class ActionSequenceTests
{
    private ActionCatalog _catalog;
    private ActionSequenceTestHost _host;
    private readonly List<ScriptableObject> _created = new List<ScriptableObject>();

    [SetUp]
    public void SetUp()
    {
        _catalog = ScriptableObject.CreateInstance<ActionCatalog>();
        _created.Add(_catalog);
        _host = new ActionSequenceTestHost();
    }
    [TearDown]
    public void TearDown()
    {
        foreach (ScriptableObject asset in _created) Object.DestroyImmediate(asset);
        _created.Clear();
    }
    private ActionDefinition Make(string id, int frames = 30, int buffer = 8)
    {
        ActionDefinition action = ScriptableObject.CreateInstance<ActionDefinition>();
        _created.Add(action);
        action.name = action.ActionId = action.DisplayName = id;
        action.Timeline.Add(new ActionSegment { SegmentId = "s", DisplayName = "段", DurationFrames = frames });
        action.Input.PreInputFrames = buffer;
        _catalog.Actions.Add(action);
        return action;
    }
    private static ActionCancelWindow Allow(ActionDefinition source, ActionDefinition target, int start, int end)
    {
        var window = new ActionCancelWindow
        {
            Start = new ActionFrameAnchor { RelativeTo = ActionFrameAnchor.Boundary.ActionStart, OffsetFrames = start },
            End = new ActionFrameAnchor { RelativeTo = ActionFrameAnchor.Boundary.ActionStart, OffsetFrames = end }
        };
        window.Targets.Add(target); source.CancelWindows.Add(window); return window;
    }
    private ActionSequencePlayer Player() => new ActionSequencePlayer(_catalog, _host, _host);
    private static ActionInputSample Press(long tick, ActionInputButtons button, ActionInputButtons held, Vector2 direction = default)
        => new ActionInputSample(tick, held, direction, new[] { new ActionInputEdge(button, ActionInputStep.Edge.Pressed, held, direction) });

    [Test]
    public void MultipleStartupSegments_AdvanceAndEventsAreOrdered()
    {
        ActionDefinition action = Make("multi");
        action.Timeline = new List<ActionSegment>
        {
            new ActionSegment { SegmentId = "first", DurationFrames = 2 },
            new ActionSegment { SegmentId = "second", DurationFrames = 3 },
            new ActionSegment { SegmentId = "active", Phase = ActionPhase.Active, DurationFrames = 2 }
        };
        action.Timeline[0].Events.Add(new ActionFrameEvent { EventKey = "first" });
        action.Timeline[1].Events.Add(new ActionFrameEvent { EventKey = "second" });
        action.Timeline[2].Events.Add(new ActionFrameEvent { EventKey = "hit" });
        ActionSequencePlayer player = Player(); player.Queue(action, 0); player.Tick(new ActionInputSample(0), 5);
        Assert.AreEqual(2, player.State.SegmentIndex);
        CollectionAssert.AreEqual(new[] { "first", "second", "active" }, _host.Segments);
        CollectionAssert.AreEqual(new[] { "first@0", "second@2", "hit@5" }, _host.Events);
    }

    [Test]
    public void AnchorFollowsSegmentWhenEarlierDurationChanges()
    {
        ActionDefinition action = Make("anchor");
        action.Timeline.Add(new ActionSegment { SegmentId = "recovery", DurationFrames = 10 });
        var anchor = new ActionFrameAnchor { SegmentId = "recovery", OffsetFrames = 3 };
        Assert.IsTrue(action.TryResolve(anchor, out int before)); Assert.AreEqual(33, before);
        action.Timeline[0].DurationFrames += 7;
        Assert.IsTrue(action.TryResolve(anchor, out int after)); Assert.AreEqual(40, after);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FloatTailPreservesElapsedTimeWithoutExtraSimulationStep(bool completes)
    {
        ActionDefinition action = Make("float_tail", completes ? 1 : 3);
        if (!completes) action.Timeline[0].Events.Add(new ActionFrameEvent { Frame = 1, EventKey = "boundary" });
        ActionSequencePlayer player = Player(); player.Queue(action, 0);
        const double elapsed = 1.00000005;
        player.Tick(new ActionInputSample(0), elapsed);
        Assert.That(_host.SimulationSteps, Has.Count.EqualTo(1));
        Assert.That(_host.SimulationSteps[0], Is.EqualTo(elapsed).Within(0.000000000001));
        Assert.IsEmpty(_host.IdleSteps);
        Assert.AreEqual(!completes, player.IsRunning);
        if (!completes)
        {
            Assert.AreEqual(1d, player.State.FrameProgress);
            CollectionAssert.AreEqual(new[] { "boundary@1" }, _host.Events);
            player.Tick(new ActionInputSample(1), 0.05);
            Assert.That(player.State.FrameProgress, Is.EqualTo(1.05d).Within(0.000000000001));
            Assert.That(_host.SimulationSteps[1], Is.EqualTo(0.05d).Within(0.000000000001));
            Assert.That(_host.Events, Has.Count.EqualTo(1));
        }
    }

    [TestCase(2, true)]
    [TestCase(4, false)]
    public void CancelWindow_IsLeftClosedRightOpen(int frame, bool allowed)
    {
        ActionDefinition source = Make("source"), target = Make("target"); Allow(source, target, 2, 4);
        ActionSequencePlayer player = Player(); player.Queue(source, 0); player.Tick(new ActionInputSample(0), frame);
        Assert.AreEqual(allowed, player.TryCancelPermission(new ActionInputRequest(target, 1, 1), out _));
    }

    [Test]
    public void OverlappingWindows_IndependentConditionsGrantUnion()
    {
        ActionDefinition source = Make("source"), target = Make("target");
        ActionCancelWindow denied = Allow(source, target, 0, 20); denied.RequireAll.Add("parry"); denied.Priority = 100;
        Allow(source, target, 0, 20).Priority = 3;
        ActionSequencePlayer player = Player(); player.Queue(source, 0); player.Tick(new ActionInputSample(0), 1);
        Assert.IsTrue(player.TryCancelPermission(new ActionInputRequest(target, 1, 1), out int priority)); Assert.AreEqual(3, priority);
        player.Queue(target, 1); player.Tick(new ActionInputSample(1), 1);
        Assert.AreSame(target, player.State.Action); CollectionAssert.AreEqual(new[] { ActionExitReason.Cancelled }, _host.Exits);
    }

    [Test]
    public void PreInputUsesTargetPolicy_ExpiredRequestCannotExecute()
    {
        ActionDefinition source = Make("source"), shortTarget = Make("short", 30, 2), longTarget = Make("long", 30, 8);
        shortTarget.Input.BufferGroup = "short"; longTarget.Input.BufferGroup = "long";
        Allow(source, shortTarget, 5, 20); Allow(source, longTarget, 5, 20);
        ActionSequencePlayer player = Player(); player.Queue(source, 0); player.Tick(new ActionInputSample(0), 1);
        player.Queue(shortTarget, 1); player.Queue(longTarget, 1);
        for (int tick = 1; tick < 5; tick++) player.Tick(new ActionInputSample(tick), 0);
        player.Tick(new ActionInputSample(5), 4);
        Assert.AreSame(longTarget, player.State.Action);
    }

    [Test]
    public void ZeroPreInput_DoesNotWaitForFutureWindow()
    {
        ActionDefinition source = Make("source"), target = Make("target", 30, 0); Allow(source, target, 4, 20);
        ActionSequencePlayer player = Player(); player.Queue(source, 0); player.Tick(new ActionInputSample(0), 1);
        player.Queue(target, 1); player.Tick(new ActionInputSample(1), 1);
        player.Tick(new ActionInputSample(2), 3);
        Assert.AreSame(source, player.State.Action); Assert.AreEqual(0, player.PendingCount);
    }

    [Test]
    public void ZeroPreInput_CannotWaitForBoundaryLaterInSameTick()
    {
        ActionDefinition source = Make("source"), target = Make("target", 30, 0); Allow(source, target, 2, 20);
        ActionSequencePlayer player = Player(); player.Queue(source, 0); player.Tick(new ActionInputSample(0), 1);
        player.Queue(target, 1); player.Tick(new ActionInputSample(1), 1);
        Assert.AreSame(source, player.State.Action); Assert.AreEqual(0, player.PendingCount);
    }

    [Test]
    public void NoSelfCancelWindow_BufferedAttackStartsAfterNaturalEnd()
    {
        ActionDefinition action = Make("basic", 3);
        ActionSequencePlayer player = Player(); player.Queue(action, 0); player.Tick(new ActionInputSample(0), 1);
        long first = player.State.InstanceId;
        player.Queue(action, 1); player.Tick(new ActionInputSample(1), 1); Assert.AreEqual(first, player.State.InstanceId);
        player.Tick(new ActionInputSample(2), 1);
        Assert.AreSame(action, player.State.Action); Assert.AreNotEqual(first, player.State.InstanceId);
        CollectionAssert.AreEqual(new[] { ActionExitReason.Completed }, _host.Exits);
    }

    [Test]
    public void FailedCommit_PreservesCurrentActionEnergyAndInput()
    {
        ActionDefinition source = Make("source"), target = Make("target"); target.EnergyCost = 20; Allow(source, target, 0, 30);
        ActionSequencePlayer player = Player(); player.Queue(source, 0); player.Tick(new ActionInputSample(0), 1);
        _host.CommitSucceeds = false;
        player.Queue(target, 1); player.Tick(new ActionInputSample(1), 1);
        Assert.AreSame(source, player.State.Action); Assert.AreEqual(200, _host.Energy); Assert.AreEqual(1, player.PendingCount); Assert.IsEmpty(_host.Exits);
        _host.CommitSucceeds = true; player.Tick(new ActionInputSample(2), 1);
        Assert.AreSame(target, player.State.Action); Assert.AreEqual(180, _host.Energy); Assert.AreEqual(0, player.PendingCount);
    }

    [Test]
    public void HitStopPreservesConfiguredBuffer_AndDoesNotAdvanceAction()
    {
        ActionDefinition source = Make("source"), target = Make("target", 30, 2); Allow(source, target, 1, 30);
        ActionSequencePlayer player = Player(); player.Queue(source, 0); player.Tick(new ActionInputSample(0), 1);
        player.Queue(target, 1);
        for (int tick = 1; tick <= 10; tick++) player.Tick(new ActionInputSample(tick), 0, true);
        Assert.AreEqual(1, player.State.ActionFrame); Assert.AreEqual(1, player.PendingCount);
        player.Tick(new ActionInputSample(11), 1);
        Assert.AreSame(target, player.State.Action);
    }

    [Test]
    public void HitStopExpiryCanBeDisabledPerTarget()
    {
        ActionDefinition source = Make("source"), target = Make("target", 30, 2); target.Input.FreezeExpiryDuringHitStop = false;
        ActionSequencePlayer player = Player(); player.Queue(source, 0); player.Tick(new ActionInputSample(0), 1); player.Queue(target, 1);
        for (int tick = 1; tick <= 4; tick++) player.Tick(new ActionInputSample(tick), 0, true);
        Assert.AreEqual(0, player.PendingCount);
    }

    [Test]
    public void FractionalPlayerFrames_EventFiresOnceAtBoundary()
    {
        ActionDefinition action = Make("slow"); action.Timeline[0].Events.Add(new ActionFrameEvent { Frame = 2, EventKey = "hit" });
        ActionSequencePlayer player = Player(); player.Queue(action, 0);
        for (int tick = 0; tick < 8; tick++) player.Tick(new ActionInputSample(tick), 0.25);
        Assert.AreEqual(2, player.State.FrameProgress); CollectionAssert.AreEqual(new[] { "hit@2" }, _host.Events);
    }

    [Test]
    public void RepeatedFloatRate_DoesNotDelayIntegerBoundary()
    {
        ActionDefinition action = Make("float_rate"); action.Timeline[0].Events.Add(new ActionFrameEvent { Frame = 7, EventKey = "hit" });
        ActionSequencePlayer player = Player(); player.Queue(action, 0);
        for (int tick = 0; tick < 10; tick++) player.Tick(new ActionInputSample(tick), 0.7f);
        Assert.AreEqual(7, player.State.ActionFrame); CollectionAssert.AreEqual(new[] { "hit@7" }, _host.Events);
    }

    [Test]
    public void PendingCancelSuppressesOldFrameEventAtWindowStart()
    {
        ActionDefinition source = Make("source"), target = Make("target"); Allow(source, target, 2, 20);
        source.Timeline[0].Events.Add(new ActionFrameEvent { Frame = 2, EventKey = "stale.hitbox" });
        ActionSequencePlayer player = Player(); player.Queue(source, 0); player.Tick(new ActionInputSample(0), 1);
        player.Queue(target, 1); player.Tick(new ActionInputSample(1), 1);
        Assert.AreSame(target, player.State.Action); Assert.IsEmpty(_host.Events);
    }

    [Test]
    public void ComboHasOneSemanticOwner_ResourceFailureDoesNotFallback()
    {
        ActionDefinition basic = Make("basic"), combo = Make("combo");
        basic.Input.Steps.Add(new ActionInputStep());
        combo.Input.Steps.Add(new ActionInputStep { RequireHeld = ActionInputButtons.Skill }); combo.Input.Priority = 10; combo.EnergyCost = 100;
        _host.Energy = 0;
        ActionSequencePlayer player = Player(); player.Tick(Press(0, ActionInputButtons.Attack, ActionInputButtons.Attack | ActionInputButtons.Skill), 1);
        Assert.IsFalse(player.IsRunning); Assert.AreEqual(1, player.PendingCount); Assert.IsEmpty(_host.Instances);
        _host.Energy = 100; player.Tick(new ActionInputSample(1), 1);
        Assert.AreSame(combo, player.State.Action); Assert.AreEqual(1, _host.Instances.Count); Assert.AreEqual(0, _host.Energy);
    }

    [Test]
    public void PressAndReleaseBetweenTicks_IsRecognizedOnce()
    {
        ActionDefinition basic = Make("basic", 1); basic.Input.Steps.Add(new ActionInputStep());
        ActionSequencePlayer player = Player();
        player.Tick(new ActionInputSample(0, edges: new[]
        {
            new ActionInputEdge(ActionInputButtons.Attack, ActionInputStep.Edge.Pressed, ActionInputButtons.Attack),
            new ActionInputEdge(ActionInputButtons.Attack, ActionInputStep.Edge.Released, ActionInputButtons.None)
        }), 1);
        player.Tick(new ActionInputSample(1), 1);
        Assert.AreEqual(1, _host.Instances.Count);
    }

    [TestCase(2, true)]
    [TestCase(4, false)]
    public void OrderedSequence_RespectsMaximumStepGap(int gap, bool starts)
    {
        ActionDefinition action = Make("sequence"); action.Input.MaxStepGapFrames = 3;
        action.Input.Steps.Add(new ActionInputStep { Button = ActionInputButtons.Jump });
        action.Input.Steps.Add(new ActionInputStep { Button = ActionInputButtons.Attack });
        ActionSequencePlayer player = Player(); player.Tick(Press(0, ActionInputButtons.Jump, ActionInputButtons.Jump), 1);
        player.Tick(Press(gap, ActionInputButtons.Attack, ActionInputButtons.Attack, Vector2.left), 1);
        Assert.AreEqual(starts, player.IsRunning);
        if (starts) Assert.AreEqual(Vector2.left, player.State.Input.Direction);
    }

    [Test]
    public void LongHold_FiresOnceUntilReleaseAndRepress()
    {
        ActionDefinition action = Make("hold", 1); action.Input.Steps.Add(new ActionInputStep { Button = ActionInputButtons.Skill, Trigger = ActionInputStep.Edge.Held, MinHoldFrames = 3 });
        ActionSequencePlayer player = Player(); player.Tick(Press(0, ActionInputButtons.Skill, ActionInputButtons.Skill), 1);
        for (int tick = 1; tick <= 5; tick++) player.Tick(new ActionInputSample(tick, ActionInputButtons.Skill), 1);
        Assert.AreEqual(1, _host.Instances.Count);
        player.Tick(new ActionInputSample(6, edges: new[] { new ActionInputEdge(ActionInputButtons.Skill, ActionInputStep.Edge.Released, ActionInputButtons.Skill) }), 1);
        player.Tick(Press(7, ActionInputButtons.Skill, ActionInputButtons.Skill), 1);
        for (int tick = 8; tick <= 10; tick++) player.Tick(new ActionInputSample(tick, ActionInputButtons.Skill), 1);
        Assert.AreEqual(2, _host.Instances.Count);
    }

    [Test]
    public void CancelledSource_DiscardsItsOtherRequestsUnlessOptedIn()
    {
        ActionDefinition source = Make("source"), first = Make("first"), leftover = Make("leftover");
        first.Input.BufferGroup = "first"; leftover.Input.BufferGroup = "leftover"; first.Input.Priority = 10;
        Allow(source, first, 0, 30); Allow(source, leftover, 0, 30);
        ActionSequencePlayer player = Player(); player.Queue(source, 0); player.Tick(new ActionInputSample(0), 1);
        player.Queue(first, 1); player.Queue(leftover, 1); player.Tick(new ActionInputSample(1), 1);
        Assert.AreSame(first, player.State.Action); Assert.AreEqual(0, player.PendingCount);
    }

    [Test]
    public void LockedFinisher_CannotCancelEvenWithHighInputPriority()
    {
        ActionDefinition source = Make("chain"), target = Make("basic"); target.Input.Priority = 999;
        Allow(source, target, 0, 5);
        ActionSequencePlayer player = Player(); player.Queue(source, 0); player.Tick(new ActionInputSample(0), 6);
        player.Queue(target, 1); player.Tick(new ActionInputSample(1), 1);
        Assert.AreSame(source, player.State.Action);
    }

    [Test]
    public void ResetClearsInputsAndCooldown_WithoutReusingInstanceIds()
    {
        ActionDefinition action = Make("reset"); action.CooldownFrames = 100;
        ActionSequencePlayer player = Player(); player.Queue(action, 0); player.Tick(new ActionInputSample(0), 1);
        long first = player.State.InstanceId; player.Queue(action, 1); player.Reset();
        Assert.AreEqual(0, player.PendingCount); Assert.IsFalse(player.IsRunning);
        player.Queue(action, 0); player.Tick(new ActionInputSample(0), 1);
        Assert.Greater(player.State.InstanceId, first); Assert.Contains(ActionExitReason.Reset, _host.Exits);
    }

    [Test]
    public void GraphPreservesParallelConditionalEdgesAndCycles()
    {
        ActionDefinition a = Make("a"), b = Make("b");
        Allow(a, b, 0, 8).RequireAll.Add("parry"); Allow(a, b, 4, 12); Allow(b, a, 2, 20);
        List<ActionCancelEdge> edges = ActionCancelGraph.Build(_catalog);
        Assert.AreEqual(3, edges.Count); Assert.AreEqual(2, edges.FindAll(edge => edge.Source == a && edge.Target == b).Count);
        Assert.IsFalse(ActionCatalogValidator.Validate(_catalog).Exists(issue => issue.Level == ActionValidationIssue.Severity.Error));
    }

    [Test]
    public void ValidatorFindsBrokenAnchorAndMissingCatalogTarget()
    {
        ActionDefinition a = Make("a"), b = Make("b"); ActionCancelWindow window = Allow(a, b, 0, 8);
        window.Start.RelativeTo = ActionFrameAnchor.Boundary.SegmentStart; window.Start.SegmentId = "deleted";
        _catalog.Actions.Remove(b);
        List<ActionValidationIssue> issues = ActionCatalogValidator.Validate(_catalog);
        Assert.IsTrue(issues.Exists(issue => issue.Message.Contains("锚点")));
        Assert.IsTrue(issues.Exists(issue => issue.Message.Contains("不在当前动作集")));
    }
}
