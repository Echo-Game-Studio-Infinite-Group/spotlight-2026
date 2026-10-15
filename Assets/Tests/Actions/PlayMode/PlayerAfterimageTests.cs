#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameJam.Actions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

public sealed class PlayerAfterimageTests
{
    private readonly List<Object> _created = new List<Object>();
    private GameObject _root;
    private PlayerMotor _motor;
    private PlayerActionRunner _runner;
    private PlayerAfterimageEffect _effect;
    private long _tick;

    private sealed class Energy : IEnergyAccount
    {
        public float Balance = 200f;
        public float CurrentEnergy => Balance;
        public float MaxEnergy => 200f;
        public bool TrySpend(float amount)
        {
            if (amount > Balance) return false;
            Balance -= amount;
            return true;
        }
    }

    [SetUp]
    public void SetUp()
    {
        if (Object.FindObjectOfType<TimeManager>() == null)
        { GameObject clock = new GameObject("AfterimageClock"); _created.Add(clock); clock.AddComponent<TimeManager>(); }
        // 此夹具手动推进逻辑帧，截图跨帧时不能再让真实 FixedUpdate 重复推进同一个执行器。
        TimeManager.SetPaused(true);
        TimeManager.ClearSlowMotion();
        _tick = 0;
        _root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab"));
        _created.Add(_root);
        foreach (Camera camera in _root.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        _runner = _root.GetComponent<PlayerActionRunner>();
        _motor = _root.GetComponent<PlayerMotor>();
        _effect = _root.GetComponent<PlayerAfterimageEffect>();
        Assert.NotNull(_effect, "先装配蓝色机动残影");
        Assert.IsTrue(_effect.Connect());
        _runner.SetEnergyAccount(new Energy());
        _motor.Teleport(new Vector3(5000f, 10f, 5000f));
        GameObject ground = new GameObject("AfterimageGround"); _created.Add(ground);
        ground.transform.position = _root.transform.position + Vector3.down * .5f;
        ground.AddComponent<BoxCollider>().size = new Vector3(200f, 1f, 200f);
        Physics.SyncTransforms();
        Tick(); Tick();
        Assert.IsTrue(_motor.IsGrounded);
    }

    [TearDown]
    public void TearDown()
    {
        TimeManager.SetPaused(false);
        _runner?.Disconnect();
        for (int i = _created.Count - 1; i >= 0; i--)
            if (_created[i] != null) Object.DestroyImmediate(_created[i]);
        _created.Clear();
        TimeManager.ClearSlowMotion();
    }

    private void Tick(ActionInputButtons buttons = ActionInputButtons.None, Vector2 direction = default, float dt = 1f / 60f)
    {
        var edges = new List<ActionInputEdge>();
        foreach (ActionInputButtons button in new[] { ActionInputButtons.Skill, ActionInputButtons.Sprint, ActionInputButtons.Jump, ActionInputButtons.Attack })
            if ((buttons & button) != 0) edges.Add(new ActionInputEdge(button, ActionInputStep.Edge.Pressed, buttons, direction));
        _runner.SimulateTick(new ActionInputSample(_tick++, buttons, direction, edges),
            new PlayerInputFrame { Move = direction }, dt);
    }

    private void Advance(int ticks)
    {
        for (int i = 0; i < ticks; i++) Tick();
    }

    private void StartEnergyDash() => Tick(ActionInputButtons.Skill | ActionInputButtons.Sprint);

    private TimeStopController PrepareTimeStop(float balance = 200f, float costPerTick = 0f)
    {
        TimeManager.SetPaused(false);
        EnergyParams parameters = ScriptableObject.CreateInstance<EnergyParams>(); _created.Add(parameters);
        parameters.TimeStopCostPerTick = costPerTick;
        TimeStopController controller = _root.GetComponent<TimeStopController>();
        controller.Configure(parameters, _root.GetComponent<PlayerInputReader>(), new Energy { Balance = balance });
        return controller;
    }

    [Test]
    public void PrefabWiringTogglesBlueOutlineAndOnlyMobilityActionsHaveEvents()
    {
        Object controller = null;
        for (int i = 0; i < _effect.EmissionChanged.GetPersistentEventCount(); i++)
            if (_effect.EmissionChanged.GetPersistentMethodName(i) == "SetEnabled")
                controller = _effect.EmissionChanged.GetPersistentTarget(i);
        Assert.NotNull(controller);
        var enabledProperty = controller.GetType().GetProperty("EffectEnabled");
        Assert.IsFalse((bool)enabledProperty.GetValue(controller));
        Assert.Greater(_effect.Settings.BodyColor.b, _effect.Settings.BodyColor.r);
        Assert.AreEqual(4, _effect.Settings.MaxSnapshots);
        foreach (ActionDefinition action in _runner.Catalog.Actions)
        {
            int starts = action.Timeline.Sum(segment => segment.Events.Count(e => e.EventKey == PlayerAfterimageEffect.StartEventKey));
            bool mobility = action.ActionId == "player_dash" || action.ActionId == "player_high_jump";
            Assert.AreEqual(mobility ? 1 : 0, starts, action.ActionId);
        }
        StartEnergyDash();
        Assert.IsTrue((bool)enabledProperty.GetValue(controller));
        Advance(17);
        Assert.IsFalse((bool)enabledProperty.GetValue(controller));
    }

    [Test]
    public void RunningBhopAndAttackNeverStartAfterimages()
    {
        _motor.SetHorizontalSpeed(40f);
        for (int i = 0; i < 8; i++) Tick(direction: Vector2.up);
        Tick(ActionInputButtons.Jump, Vector2.up);
        Advance(5);
        Assert.Greater(_motor.JumpCount, 0);
        Tick(ActionInputButtons.Attack);
        Advance(5);
        Assert.IsFalse(_effect.IsEmitting);
        Assert.AreEqual(0, _effect.CaptureCount);
    }

    [Test]
    public void EnergyDashCapturesOnlyUntilRecoveryAndThenFades()
    {
        StartEnergyDash();
        Assert.IsTrue(_effect.IsEmitting);
        Assert.Greater(_effect.ActiveSnapshotCount, 0);
        Advance(17);
        Assert.IsFalse(_effect.IsEmitting, "收招首帧停止生成");
        Assert.Greater(_effect.ActiveSnapshotCount, 0, "已留下的残影继续淡出");
        int captures = _effect.CaptureCount;
        Advance(14);
        Assert.AreEqual(captures, _effect.CaptureCount);
        Assert.AreEqual(0, _effect.ActiveSnapshotCount);
    }

    [Test]
    public void OrdinarySprintNeverEnablesOutlineOrCapturesSnapshots()
    {
        Tick(ActionInputButtons.Sprint);
        Assert.AreEqual("player_sprint", _runner.Player.State.Action.ActionId);
        Advance(35);
        Assert.IsFalse(_effect.IsEmitting);
        Assert.AreEqual(0, _effect.CaptureCount);
    }

    [Test]
    public void RightHoldStartsEffectOnlyWhenTimeStopActivatesAndReleaseLetsTrailFade()
    {
        TimeStopController controller = PrepareTimeStop();
        controller.SimulateTick(true, 1, 0f);
        for (int i = 0; i < controller.HoldThresholdFrames - 1; i++) controller.SimulateTick(true, 1, 0f);
        Assert.IsFalse(controller.IsTimeStopping);
        Assert.IsFalse(_effect.IsEmitting);
        controller.SimulateTick(true, 1, 0f);
        Assert.IsTrue(controller.IsTimeStopping);
        Assert.IsTrue(_effect.IsEmitting);
        _motor.SetHorizontalSpeed(10f);
        Advance(12);
        Assert.Greater(_effect.ActiveSnapshotCount, 0);
        controller.SimulateTick(false, 1, 0f);
        Assert.IsFalse(_effect.IsEmitting);
        Assert.Greater(_effect.ActiveSnapshotCount, 0);
        Advance(14);
        Assert.AreEqual(0, _effect.ActiveSnapshotCount);
    }

    [Test]
    public void ActiveTimeStopKeepsEffectAfterOrdinarySprintEndsAndWhileStationary()
    {
        Tick(ActionInputButtons.Sprint);
        TimeStopController controller = PrepareTimeStop();
        controller.BeginTimeStop();
        Advance(35);
        Assert.IsFalse(_runner.Player.IsRunning);
        Assert.IsTrue(controller.IsTimeStopping);
        Assert.IsTrue(_effect.IsEmitting);
        _motor.SetHorizontalSpeed(0f);
        int captures = _effect.CaptureCount;
        Advance(18);
        Assert.IsTrue(_effect.IsEmitting, "静止仍保持蓝色本体效果");
        Assert.AreEqual(captures, _effect.CaptureCount, "静止不重复叠人形");
        controller.EndTimeStop();
        Assert.IsFalse(_effect.IsEmitting);
    }

    [Test]
    public void TimeStopSourceSurvivesEnergyDashRecovery()
    {
        StartEnergyDash();
        TimeStopController controller = PrepareTimeStop();
        controller.BeginTimeStop();
        Advance(18);
        Assert.IsTrue(_effect.IsEmitting);
        controller.EndTimeStop();
        Assert.IsFalse(_effect.IsEmitting);
    }

    [Test]
    public void HighJumpTakesOverTrailWhenItEndsTimeStop()
    {
        TimeStopController controller = PrepareTimeStop();
        controller.BeginTimeStop();
        Tick(ActionInputButtons.Skill | ActionInputButtons.Jump);
        Assert.IsFalse(controller.IsTimeStopping);
        Assert.IsTrue(_effect.IsEmitting);
        Assert.Greater(_effect.ActiveSnapshotCount, 0);
    }

    [Test]
    public void FailedTimeStopDoesNotStartEffectAndEnergyExhaustionStopsIt()
    {
        TimeStopController controller = PrepareTimeStop(0f, 1f);
        controller.BeginTimeStop();
        Assert.IsFalse(controller.IsTimeStopping);
        Assert.IsFalse(_effect.IsEmitting);
        controller = PrepareTimeStop(.2f, .1f);
        controller.BeginTimeStop();
        Assert.IsTrue(_effect.IsEmitting);
        Tick(); Tick(); Tick();
        Assert.IsFalse(controller.IsTimeStopping);
        Assert.IsFalse(_effect.IsEmitting);
    }

    [TestCase("teleport")]
    [TestCase("death")]
    [TestCase("input")]
    [TestCase("disable")]
    public void TimeStopExitPathsReleaseEffectImmediately(string transition)
    {
        TimeStopController controller = PrepareTimeStop();
        controller.BeginTimeStop();
        _motor.SetHorizontalSpeed(10f);
        Advance(5);
        Assert.IsTrue(_effect.IsEmitting);
        switch (transition)
        {
            case "teleport": _motor.Teleport(_root.transform.position + Vector3.right * 20f); break;
            case "death": _root.GetComponent<HealthComponent>().TakeDamage(10000f); break;
            case "input": _root.GetComponent<PlayerInputReader>().SetGameplayEnabled(false); break;
            case "disable": controller.enabled = false; break;
        }
        Assert.IsFalse(controller.IsTimeStopping);
        Assert.IsFalse(_effect.IsEmitting);
    }

    [TestCase(ActionInputButtons.Sprint)]
    [TestCase(ActionInputButtons.Jump)]
    public void UnaffordableSkillDoesNotStartEffectOrFallBack(ActionInputButtons trigger)
    {
        _runner.SetEnergyAccount(new Energy { Balance = 0f });
        Tick(ActionInputButtons.Skill | trigger);
        Assert.IsFalse(_effect.IsEmitting);
        Assert.AreEqual(0, _effect.CaptureCount);
    }

    [Test]
    public void HighJumpLeavesAscendingTrailAndStopsAtApex()
    {
        Tick(ActionInputButtons.Skill | ActionInputButtons.Jump);
        Assert.IsTrue(_effect.IsEmitting);
        Assert.Greater(_effect.ActiveSnapshotCount, 0);
        for (int i = 0; i < 240 && _motor.Velocity.y > 0f; i++) Tick();
        Assert.LessOrEqual(_motor.Velocity.y, 0f);
        Assert.IsFalse(_effect.IsEmitting);
        int captures = _effect.CaptureCount;
        Advance(15);
        Assert.AreEqual(captures, _effect.CaptureCount);
        Assert.AreEqual(0, _effect.ActiveSnapshotCount);
    }

    [Test]
    public void AirborneHighJumpRequestNeverCapturesBeforeTakeoff()
    {
        Tick(ActionInputButtons.Jump);
        Tick(ActionInputButtons.Skill | ActionInputButtons.Jump);
        Advance(4);
        Assert.IsFalse(_motor.IsGrounded);
        Assert.AreEqual(0, _effect.CaptureCount);
    }

    [Test]
    public void SnapshotsKeepWorldPoseAndReuseBoundedMeshPool()
    {
        StartEnergyDash();
        PlayerAfterimageSnapshot first = _effect.Snapshots.First(snapshot => snapshot.IsActive);
        Matrix4x4 matrix = first.GetMatrix(0);
        Vector3 position = first.Position;
        Vector3[] vertices = first.GetMesh(0).vertices;
        Assert.Greater(vertices.Length, 0);
        int[] meshIds = _effect.Snapshots.SelectMany(snapshot => Enumerable.Range(0, snapshot.PartCount)
            .Select(part => snapshot.GetMesh(part).GetInstanceID())).ToArray();
        Advance(4);
        Assert.Greater(Vector3.Distance(position, _root.transform.position), .1f);
        Assert.AreEqual(matrix, first.GetMatrix(0));
        CollectionAssert.AreEqual(vertices, first.GetMesh(0).vertices);
        Advance(12);
        Assert.Greater(_effect.CaptureCount, _effect.Settings.MaxSnapshots);
        Assert.LessOrEqual(_effect.ActiveSnapshotCount, _effect.Settings.MaxSnapshots);
        CollectionAssert.AreEqual(meshIds, _effect.Snapshots.SelectMany(snapshot => Enumerable.Range(0, snapshot.PartCount)
            .Select(part => snapshot.GetMesh(part).GetInstanceID())).ToArray());
    }

    [Test]
    public void SnapshotSamplingRequiresBothTimeAndTravelDistance()
    {
        StartEnergyDash();
        int firstCount = _effect.CaptureCount;
        Advance(2);
        Assert.AreEqual(firstCount, _effect.CaptureCount);
        Tick();
        Assert.Greater(_effect.CaptureCount, firstCount);
        _motor.SetHorizontalSpeed(0f);
        int stationaryCount = _effect.CaptureCount;
        Advance(6);
        Assert.AreEqual(stationaryCount, _effect.CaptureCount, "停在原地不堆叠人形");
    }

    [Test]
    public void CancellationStopsEmissionButKeepsExistingTrailForFade()
    {
        Tick(ActionInputButtons.Skill | ActionInputButtons.Jump);
        Advance(6);
        _motor.SetHorizontalSpeed(0f);
        Tick(ActionInputButtons.Attack);
        Assert.AreEqual("player_attack", _runner.Player.State.Action.ActionId);
        Assert.IsFalse(_effect.IsEmitting);
        Assert.Greater(_effect.ActiveSnapshotCount, 0);
        Advance(14);
        Assert.AreEqual(0, _effect.ActiveSnapshotCount);
    }

    [TestCase("reset")]
    [TestCase("interrupt")]
    [TestCase("teleport")]
    [TestCase("death")]
    [TestCase("ragdoll")]
    [TestCase("disable")]
    public void ExceptionalTransitionsClearEverySnapshotImmediately(string transition)
    {
        StartEnergyDash();
        Advance(4);
        Assert.Greater(_effect.ActiveSnapshotCount, 0);
        switch (transition)
        {
            case "reset": _runner.ResetActions(); break;
            case "interrupt": _runner.Interrupt(); break;
            case "teleport": _motor.Teleport(_root.transform.position + Vector3.right * 20f); break;
            case "death": _root.GetComponent<HealthComponent>().TakeDamage(10000f); break;
            case "ragdoll": _root.GetComponent<PlayerRagdollHitStun>().EnterHitStun(); break;
            case "disable": _effect.enabled = false; break;
        }
        Assert.IsFalse(_effect.IsEmitting);
        Assert.AreEqual(0, _effect.ActiveSnapshotCount);
    }

    [Test]
    public void LifetimesFollowPlayerSimulationAndFreezeAtZeroDelta()
    {
        StartEnergyDash();
        PlayerAfterimageSnapshot first = _effect.Snapshots.First(snapshot => snapshot.IsActive);
        float age = first.Age;
        int count = _effect.CaptureCount;
        for (int i = 0; i < 20; i++) Tick(dt: 0f);
        Assert.AreEqual(age, first.Age);
        Assert.AreEqual(count, _effect.CaptureCount);
        for (int i = 0; i < 6; i++) Tick(dt: .1f / 60f);
        Assert.That(first.Age - age, Is.EqualTo(.01f).Within(.00001f));
    }

    [UnityTest]
    public IEnumerator RenderBlueTrailPreview()
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("截图验收需要图形设备");
        Assert.IsTrue(_effect.Settings.Material.shader.isSupported);
        Tick(ActionInputButtons.Skill | ActionInputButtons.Sprint);
        Advance(9);
        Assert.GreaterOrEqual(_effect.ActiveSnapshotCount, 3);
        TimeManager.SetPaused(true);
        GameObject cameraObject = new GameObject("AfterimagePreviewCamera"); _created.Add(cameraObject);
        Camera camera = cameraObject.AddComponent<Camera>();
        Vector3 center = _root.transform.position + Vector3.back * 2f + Vector3.up;
        camera.transform.position = center + new Vector3(8f, 3f, -4f);
        camera.transform.LookAt(center);
        camera.orthographic = true;
        camera.orthographicSize = 2.9f;
        camera.backgroundColor = new Color(.12f, .13f, .16f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        var renderTexture = new RenderTexture(1280, 720, 24); _created.Add(renderTexture);
        renderTexture.Create();
        camera.targetTexture = renderTexture;
        GameObject lightObject = new GameObject("AfterimagePreviewLight"); _created.Add(lightObject);
        Light light = lightObject.AddComponent<Light>(); light.type = LightType.Directional;
        light.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
        yield return null;
        yield return null;
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = renderTexture;
        var texture = new Texture2D(1280, 720, TextureFormat.RGB24, false); _created.Add(texture);
        texture.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); texture.Apply();
        RenderTexture.active = previous;
        File.WriteAllBytes(Path.Combine(Application.dataPath, "../AfterimagePreview.png"), texture.EncodeToPNG());
        int bluePixels = texture.GetPixels32().Count(color => color.b > color.r * 1.3f && color.b > color.g * 1.15f && color.b > 100);
        Assert.Greater(bluePixels, 500, "实际渲染结果中必须出现蓝色残影");
        camera.targetTexture = null;
        TimeManager.SetPaused(false);
    }
}
#endif
