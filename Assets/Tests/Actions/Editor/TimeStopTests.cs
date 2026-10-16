using System.Collections.Generic;
using System.Reflection;
using GameJam.Actions;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class TimeStopTests
{
    private readonly List<Object> _created = new List<Object>();
    private TimeStopController _controller;
    private EnergyParams _parameters;
    private ActionTestEnergy _energy;
    private HealthComponent _health;

    [SetUp]
    public void SetUp()
    {
        var clock = new GameObject("TimeStopTestClock"); _created.Add(clock);
        TimeManager manager = clock.AddComponent<TimeManager>();
        Invoke(manager, "Awake");
        var root = new GameObject("TimeStopTestPlayer"); _created.Add(root);
        _health = root.AddComponent<HealthComponent>();
        _controller = root.AddComponent<TimeStopController>();
        Invoke(_controller, "OnEnable");
        _parameters = ScriptableObject.CreateInstance<EnergyParams>(); _created.Add(_parameters);
        _energy = new ActionTestEnergy { Balance = 100f };
        _controller.Configure(_parameters, null, _energy);
        _controller.SimulateTick(false, 0, 1f / 60f);
    }
    [TearDown]
    public void TearDown()
    {
        TimeManager.SetPaused(false);
        _controller?.EndTimeStop();
        for (int i = _created.Count - 1; i >= 0; i--) if (_created[i] != null) Object.DestroyImmediate(_created[i]);
        _created.Clear();
    }
    private static void Invoke(object target, string method)
        => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, null);
    private void Hold(int elapsed, long version = 1)
    {
        _controller.SimulateTick(true, version, 1f / 60f * TimeManager.PlayerRate);
        for (int i = 0; i < elapsed; i++)
            _controller.SimulateTick(true, version, 1f / 60f * TimeManager.PlayerRate);
    }

    [Test]
    public void ThresholdUsesInputFramesAndSlowsPlayerAndWorldExactlyOnce()
    {
        _parameters.TimeStopCostPerTick = 0f;
        Hold(29);
        Assert.IsFalse(_controller.IsTimeStopping);
        Assert.AreEqual(1f, TimeManager.GlobalRate);
        _controller.SimulateTick(true, 1, 1f / 60f);
        Assert.IsTrue(_controller.IsTimeStopping);
        Assert.AreEqual(30, _controller.HeldFrames);
        Assert.That(TimeManager.GlobalRate, Is.EqualTo(.2f));
        Assert.That(TimeManager.WorldRate, Is.EqualTo(.2f));
        Assert.That(TimeManager.PlayerRate, Is.EqualTo(.2f));
        Assert.AreEqual(1f, Time.timeScale);
    }
    [Test]
    public void ReleaseAndRapidRepressStartAFullNewThreshold()
    {
        Hold(20);
        _controller.SimulateTick(true, 2, 1f / 60f);
        Assert.AreEqual(0, _controller.HeldFrames);
        for (int i = 0; i < 29; i++) _controller.SimulateTick(true, 2, 1f / 60f);
        Assert.IsFalse(_controller.IsTimeStopping);
        _controller.SimulateTick(true, 2, 1f / 60f);
        Assert.IsTrue(_controller.IsTimeStopping);
        _controller.SimulateTick(false, 2, 1f / 60f);
        Assert.AreEqual(1f, TimeManager.GlobalRate);
        Assert.AreEqual(1f, TimeManager.PlayerRate);
    }
    [Test]
    public void InsufficientEnergyDoesNotRestartWhileStillHeld()
    {
        _energy.Balance = 0f;
        Hold(30);
        Assert.IsFalse(_controller.IsTimeStopping);
        _energy.Balance = 100f;
        Hold(60);
        Assert.IsFalse(_controller.IsTimeStopping);
        _controller.SimulateTick(false, 1, 1f / 60f);
        Hold(30, 2);
        Assert.IsTrue(_controller.IsTimeStopping);
    }
    [Test]
    public void FirstActivationAndFractionalTicksChargeActualPlayerTime()
    {
        Hold(30);
        Assert.That(_energy.Balance, Is.EqualTo(99.8f).Within(.0001f));
        for (int i = 0; i < 5; i++) _controller.SimulateTick(true, 1, .2f / 60f);
        Assert.That(_energy.Balance, Is.EqualTo(98.8f).Within(.0001f));
        _controller.SimulateTick(false, 1, 1f / 60f);
        Assert.That(_energy.Balance, Is.EqualTo(98.8f).Within(.0001f));
    }
    [Test]
    public void DepletedEnergyReleasesBothLayersAndRequiresRelease()
    {
        _energy.Balance = 1f;
        Hold(30);
        for (int i = 0; i < 6; i++) _controller.SimulateTick(true, 1, .2f / 60f);
        Assert.IsFalse(_controller.IsTimeStopping);
        Assert.AreEqual(1f, TimeManager.GlobalRate);
        _energy.Balance = 100f;
        Hold(40);
        Assert.IsFalse(_controller.IsTimeStopping);
    }
    [Test]
    public void HitStopComposesByMinimumAndSurvivesTimeStopRelease()
    {
        Hold(30);
        TimeManager.HitStop(10f, .05f);
        Assert.AreEqual(.05f, TimeManager.PlayerRate);
        Assert.AreEqual(.05f, TimeManager.WorldRate);
        Assert.IsTrue(TimeManager.InHitStop);
        _controller.EndTimeStop();
        Assert.AreEqual(1f, TimeManager.GlobalRate);
        Assert.AreEqual(.05f, TimeManager.PlayerRate);
        TimeManager.ClearSlowMotion();
        Assert.AreEqual(1f, TimeManager.PlayerRate);
    }
    [Test]
    public void SeparateOwnersAndRepeatedStartDoNotEraseOtherEffects()
    {
        object other = new object();
        TimeManager.SetTimeStop(true, .4f, other);
        Hold(30);
        _controller.BeginTimeStop(); _controller.BeginTimeStop();
        Assert.AreEqual(.2f, TimeManager.GlobalRate);
        _controller.EndTimeStop();
        Assert.AreEqual(.4f, TimeManager.GlobalRate);
        Assert.AreEqual(.4f, TimeManager.PlayerRate);
        TimeManager.SetTimeStop(false, owner: other);
        Assert.AreEqual(1f, TimeManager.GlobalRate);
    }
    [Test]
    public void LongHoldThresholdIsIndependentOfPlayerHitStop()
    {
        TimeManager.HitStop(10f, .05f);
        Hold(30);
        Assert.IsTrue(_controller.IsTimeStopping);
        Assert.AreEqual(.2f, TimeManager.GlobalRate);
        Assert.AreEqual(.05f, TimeManager.PlayerRate);
    }
    [Test]
    public void DeathRevivePauseAndDisableReleaseTheEffect()
    {
        Hold(30);
        _health.TakeDamage(1000f);
        Assert.IsFalse(_controller.IsTimeStopping);
        _health.Reset();
        Hold(40);
        Assert.IsFalse(_controller.IsTimeStopping);
        _controller.SimulateTick(false, 1, 1f / 60f);
        Hold(30, 2);
        Assert.IsTrue(_controller.IsTimeStopping);
        TimeManager.SetPaused(true);
        Invoke(_controller, "Update");
        Assert.AreEqual(1f, TimeManager.GlobalRate);
        TimeManager.SetPaused(false);
        _controller.SimulateTick(false, 2, 1f / 60f);
        Hold(30, 3);
        Invoke(_controller, "OnDisable");
        Assert.AreEqual(1f, TimeManager.PlayerRate);
    }
}
