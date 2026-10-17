#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using System.Reflection;
using GameJam.Actions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;

public sealed class ActionPlayerPlayModeTests
{
    private GameObject _root;
    private Mouse _mouse;
    private Keyboard _keyboard;
    private GameObject _ground;
    private PlayerActionRunner _runner;
    private GameObject _clock;
    private float _mobilitySpentEnergy;
    private InputSettings _previousSettings, _testSettings;
    [UnitySetUp]
    public IEnumerator SetUp()
    {
        _previousSettings = InputSystem.settings;
        _testSettings = Object.Instantiate(_previousSettings);
        _testSettings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
        _testSettings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
        InputSystem.settings = _testSettings;
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        _root = Object.Instantiate(prefab);
        _root.transform.position = new Vector3(3000f, 10f, 3000f);
        _runner = _root.GetComponent<PlayerActionRunner>();
        _mouse = InputSystem.AddDevice<Mouse>();
        _keyboard = InputSystem.AddDevice<Keyboard>();
        yield return null;
        PlayerInputReader reader = _root.GetComponent<PlayerInputReader>();
        typeof(PlayerInputReader).GetMethod("OnApplicationFocus", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(reader, new object[] { true });
        Assert.IsTrue(_runner.IsConnected);
        Assert.IsTrue(reader.GameplayEnabled);
    }
    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (_mouse != null) InputSystem.RemoveDevice(_mouse);
        if (_keyboard != null) InputSystem.RemoveDevice(_keyboard);
        if (_ground != null) Object.Destroy(_ground);
        if (_root != null) Object.Destroy(_root);
        if (_previousSettings != null) InputSystem.settings = _previousSettings;
        if (_testSettings != null) Object.Destroy(_testSettings);
        TimeManager.SetPaused(false);
        TimeManager.ClearSlowMotion();
        if (_clock != null) Object.Destroy(_clock);
        yield return null;
    }
    [UnityTest]
    public IEnumerator MouseAttackUsesSequenceAndManualAnimatorInActualFixedLoop()
    {
        InputSystem.QueueStateEvent(_mouse, new MouseState().WithButton(MouseButton.Left));
        InputSystem.Update();
        for (int i = 0; i < 10 && !_runner.Player.IsRunning; i++) yield return new WaitForFixedUpdate();
        Assert.IsTrue(_runner.Player.IsRunning);
        Assert.AreEqual("player_attack", _runner.Player.State.Action.ActionId);
        Assert.IsTrue(_root.GetComponent<PlayerCombat>().IsSequenceDriven);
        Assert.IsTrue(_root.GetComponent<PlayerMotor>().IsExternallyDriven);
        Assert.IsFalse(_root.GetComponent<ActionAnimatorBridge>().Animator.enabled);
        Assert.That(_runner.LastSimulatedSeconds, Is.EqualTo(TimeManager.PlayerFixedDeltaTime).Within(0.000001f));
    }
    [UnityTest]
    public IEnumerator SimultaneousSkillAndAttackDoesNotFallBackToOrdinaryAttack()
    {
        InputSystem.QueueStateEvent(_mouse, new MouseState().WithButton(MouseButton.Right).WithButton(MouseButton.Left));
        InputSystem.Update();
        Assert.IsTrue(_mouse.leftButton.isPressed && _mouse.rightButton.isPressed);
        for (int i = 0; i < 5; i++) yield return new WaitForFixedUpdate();
        Assert.IsFalse(_runner.Player.IsRunning);
        Assert.IsFalse(_root.GetComponent<PlayerInputReader>().ConsumeAttack(TimeManager.UnscaledTime));
    }
    [UnityTest]
    public IEnumerator DisabledMotorStaysStillWhileRunnerIsEnabledAndResumesAfterEnable()
    {
        PlayerMotor motor = _root.GetComponent<PlayerMotor>();
        motor.enabled = false;
        Vector3 position = _root.transform.position;
        Vector3 velocity = motor.Velocity;
        for (int i = 0; i < 5; i++) yield return new WaitForFixedUpdate();
        Assert.IsTrue(_runner.enabled && _runner.IsConnected);
        Assert.AreEqual(position, _root.transform.position);
        Assert.AreEqual(velocity, motor.Velocity);
        motor.enabled = true;
        for (int i = 0; i < 5; i++) yield return new WaitForFixedUpdate();
        Assert.Less(_root.transform.position.y, position.y);
    }
    [UnityTest]
    public IEnumerator TeleportClearsActionAndReturnsInputOwnershipOnDisable()
    {
        InputSystem.QueueStateEvent(_mouse, new MouseState().WithButton(MouseButton.Left));
        InputSystem.Update();
        for (int i = 0; i < 10 && !_runner.Player.IsRunning; i++) yield return new WaitForFixedUpdate();
        Assert.IsTrue(_runner.Player.IsRunning);
        _root.GetComponent<PlayerMotor>().Teleport(new Vector3(3100f, 10f, 3100f));
        Assert.IsFalse(_runner.Player.IsRunning);
        Assert.AreEqual(0, _runner.Player.PendingCount);
        _runner.enabled = false;
        Assert.IsFalse(_root.GetComponent<PlayerMotor>().IsExternallyDriven);
        Assert.IsFalse(_root.GetComponent<PlayerCombat>().IsSequenceDriven);
        yield return null;
    }
    [UnityTest]
    public IEnumerator DisconnectDuringIdleMovementStopsTickAndAllowsReconnect()
        => DisconnectDuringMovement(false);

    [UnityTest]
    public IEnumerator DisconnectDuringActionMovementStopsTickAndAllowsReconnect()
        => DisconnectDuringMovement(true);

    private IEnumerator DisconnectDuringMovement(bool startAction)
    {
        if (startAction)
        {
            InputSystem.QueueStateEvent(_mouse, new MouseState().WithButton(MouseButton.Left));
            InputSystem.Update();
            for (int i = 0; i < 10 && !_runner.Player.IsRunning; i++) yield return new WaitForFixedUpdate();
            Assert.IsTrue(_runner.Player.IsRunning);
        }
        PlayerMotor motor = _root.GetComponent<PlayerMotor>();
        ActionSequencePlayer player = _runner.Player;
        bool disconnected = false;
        bool simulated = false;
        System.Action<Vector3> disconnect = _ => { disconnected = true; _runner.enabled = false; };
        _runner.Simulated += _ => simulated = true;
        motor.MotionPathPoint += disconnect;
        try
        {
            for (int i = 0; i < 5 && !disconnected; i++) yield return new WaitForFixedUpdate();
            Assert.IsTrue(disconnected);
            Assert.IsFalse(_runner.IsConnected);
            Assert.IsNull(_runner.Player);
            Assert.IsFalse(player.IsRunning);
            Assert.IsFalse(simulated);
            Assert.IsFalse(motor.IsExternallyDriven);
            Assert.IsFalse(_root.GetComponent<PlayerCombat>().IsSequenceDriven);
            Assert.IsTrue(_root.GetComponent<ActionAnimatorBridge>().Animator.enabled);
        }
        finally { motor.MotionPathPoint -= disconnect; }
        _runner.enabled = true;
        Assert.IsTrue(_runner.IsConnected);
        Assert.AreNotSame(player, _runner.Player);
        yield return new WaitForFixedUpdate();
        Assert.IsTrue(simulated);
        Assert.IsTrue(motor.IsExternallyDriven);
    }

    private TimeStopController PrepareTimeStop()
    {
        if (Object.FindObjectOfType<TimeManager>() == null)
        {
            _clock = new GameObject("ActionTimeStopPlayModeClock");
            _clock.AddComponent<TimeManager>();
        }
        _root.GetComponent<VectorEnergy>().SetForDebug(200f);
        TimeStopController controller = _root.GetComponent<TimeStopController>();
        Assert.NotNull(controller);
        return controller;
    }

    [UnityTest]
    public IEnumerator RightHoldStartsAfterThirtyInputFramesAndAllowsLeftAttackDuringGlobalSlow()
    {
        TimeStopController controller = PrepareTimeStop();
        InputSystem.QueueStateEvent(_mouse, new MouseState().WithButton(MouseButton.Right));
        InputSystem.Update();
        for (int i = 0; i < 29; i++) yield return new WaitForFixedUpdate();
        Assert.IsFalse(controller.IsTimeStopping);
        for (int i = 0; i < 4 && !controller.IsTimeStopping; i++) yield return new WaitForFixedUpdate();
        Assert.IsTrue(controller.IsTimeStopping);
        Assert.AreEqual(.2f, TimeManager.GlobalRate);
        Assert.AreEqual(.2f, TimeManager.PlayerRate);
        Assert.AreEqual(.2f, TimeManager.WorldRate);
        InputSystem.QueueStateEvent(_mouse, new MouseState().WithButton(MouseButton.Right).WithButton(MouseButton.Left));
        InputSystem.Update();
        for (int i = 0; i < 3 && !_runner.Player.IsRunning; i++) yield return new WaitForFixedUpdate();
        Assert.IsTrue(_runner.Player.IsRunning);
        Assert.AreEqual("player_attack", _runner.Player.State.Action.ActionId);
        double progress = _runner.Player.State.FrameProgress;
        float before = _root.GetComponent<VectorEnergy>().CurrentEnergy;
        for (int i = 0; i < 5; i++) yield return new WaitForFixedUpdate();
        Assert.That(_runner.Player.State.FrameProgress - progress, Is.EqualTo(1d).Within(.00001));
        Assert.That(_runner.LastSimulatedSeconds, Is.EqualTo(.2f / 60f).Within(.000001));
        Assert.That(before - _root.GetComponent<VectorEnergy>().CurrentEnergy, Is.EqualTo(1f).Within(.0001f));
        Assert.AreEqual(1f, _root.GetComponent<ActionAnimatorBridge>().Animator.speed);
        InputSystem.QueueStateEvent(_mouse, new MouseState().WithButton(MouseButton.Left));
        InputSystem.Update();
        Assert.IsFalse(controller.IsTimeStopping);
        Assert.AreEqual(1f, TimeManager.GlobalRate);
        long instance = _runner.Player.State.InstanceId;
        yield return new WaitForFixedUpdate();
        Assert.AreEqual(instance, _runner.Player.State.InstanceId);
        Assert.That(_runner.LastSimulatedSeconds, Is.EqualTo(1f / 60f).Within(.000001));
    }

    [UnityTest]
    public IEnumerator PauseAndInputClearReleaseTimeStopAndRequireFreshHold()
    {
        TimeStopController controller = PrepareTimeStop();
        // 此用例等待多次长按，隔离掉灰盒掉出地图后的自动传送复位。
        _root.GetComponent<PlayerMotor>().enabled = false;
        InputSystem.QueueStateEvent(_mouse, new MouseState().WithButton(MouseButton.Right));
        InputSystem.Update();
        for (int i = 0; i < 33; i++) yield return new WaitForFixedUpdate();
        Assert.IsTrue(controller.IsTimeStopping);
        TimeManager.SetPaused(true);
        yield return null;
        Assert.IsFalse(controller.IsTimeStopping);
        Assert.AreEqual(1f, TimeManager.GlobalRate);
        TimeManager.SetPaused(false);
        for (int i = 0; i < 35; i++) yield return new WaitForFixedUpdate();
        Assert.IsFalse(controller.IsTimeStopping);
        InputSystem.QueueStateEvent(_mouse, new MouseState()); InputSystem.Update();
        InputSystem.QueueStateEvent(_mouse, new MouseState().WithButton(MouseButton.Right)); InputSystem.Update();
        for (int i = 0; i < 33; i++) yield return new WaitForFixedUpdate();
        Assert.IsTrue(controller.IsTimeStopping);
        _root.GetComponent<PlayerInputReader>().SetGameplayEnabled(false);
        Assert.IsFalse(controller.IsTimeStopping);
        Assert.AreEqual(1f, TimeManager.PlayerRate);
    }

    [UnityTest]
    public IEnumerator HitStopEndsBackAtGlobalRateWithoutDoubleSlow()
    {
        TimeStopController controller = PrepareTimeStop();
        InputSystem.QueueStateEvent(_mouse, new MouseState().WithButton(MouseButton.Right));
        InputSystem.Update();
        controller.BeginTimeStop();
        TimeManager.HitStop(.05f, .05f);
        Assert.AreEqual(.05f, TimeManager.PlayerRate);
        Assert.AreEqual(.05f, TimeManager.WorldRate);
        Assert.IsTrue(TimeManager.InHitStop);
        yield return new WaitForSecondsRealtime(.08f);
        Assert.IsFalse(TimeManager.InHitStop);
        Assert.IsTrue(controller.IsTimeStopping);
        Assert.AreEqual(.2f, TimeManager.GlobalRate);
        Assert.AreEqual(.2f, TimeManager.PlayerRate);
        Assert.AreEqual(.2f, TimeManager.WorldRate);
    }

    private IEnumerator PrepareMobilityGround()
    {
        _ground = new GameObject("MobilityInputGround");
        _ground.transform.position = _root.transform.position + Vector3.down * .5f;
        _ground.AddComponent<BoxCollider>().size = new Vector3(200f, 1f, 200f);
        Physics.SyncTransforms();
        for (int i = 0; i < 5 && !_root.GetComponent<PlayerMotor>().IsGrounded; i++) yield return new WaitForFixedUpdate();
        Assert.IsTrue(_root.GetComponent<PlayerMotor>().IsGrounded);
        VectorEnergy energy = _root.GetComponent<VectorEnergy>();
        energy.SetForDebug(200f);
        _mobilitySpentEnergy = 0f;
        float previousBalance = energy.CurrentEnergy;
        // 四倍速度会立即积能，记录实际扣费，避免把同 tick 的收入误判为少扣费用。
        energy.Changed += balance =>
        {
            if (balance < previousBalance) _mobilitySpentEnergy += previousBalance - balance;
            previousBalance = balance;
        };
    }

    [UnityTest]
    public IEnumerator ForwardKeyRunsWithoutShiftAndReleaseIdlesWhileStillCoasting()
    {
        yield return PrepareMobilityGround();
        for (int i = 0; i < 60; i++) yield return new WaitForFixedUpdate();
        InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.W)); InputSystem.Update();
        for (int i = 0; i < 45; i++) yield return new WaitForFixedUpdate();
        PlayerMotor motor = _root.GetComponent<PlayerMotor>();
        PlayerAnimation animation = _root.GetComponentInChildren<PlayerAnimation>();
        Animator animator = _root.GetComponent<ActionAnimatorBridge>().Animator;
        Assert.That(motor.HorizontalSpeed, Is.EqualTo(motor.Params.GroundSpeedThreshold).Within(.001f));
        Assert.AreEqual(PlayerAnimation.MotionStateMove, animation.CurrentMotionState);
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Move"));
        Transform leg = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        Quaternion pose = leg.localRotation;
        for (int i = 0; i < 6; i++) yield return new WaitForFixedUpdate();
        Assert.Greater(Quaternion.Angle(pose, leg.localRotation), .1f);
        InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); InputSystem.Update();
        yield return new WaitForFixedUpdate();
        Assert.IsFalse(motor.HasForwardInput);
        Assert.Greater(motor.HorizontalSpeed, 0f);
        Assert.AreEqual(PlayerAnimation.MotionStateIdle, animation.CurrentMotionState);
        for (int i = 0; i < 8; i++) yield return new WaitForFixedUpdate();
        Assert.Greater(motor.HorizontalSpeed, 0f);
        Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Idle"));
    }

    [UnityTest]
    public IEnumerator ShiftFromRunAddsConfiguredBoostOnceWithoutEnergyCostOrTimeEffect()
    {
        yield return PrepareMobilityGround();
        for (int i = 0; i < 60; i++) yield return new WaitForFixedUpdate();
        InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.W)); InputSystem.Update();
        for (int i = 0; i < 45; i++) yield return new WaitForFixedUpdate();
        int commands = 0;
        _runner.FrameEvent += (state, frameEvent) => { if (frameEvent.MotorCommand == MotorCommandKind.SprintBoost) commands++; };
        InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.W, Key.LeftShift)); InputSystem.Update();
        yield return new WaitForFixedUpdate();
        Assert.AreEqual("player_sprint", _runner.Player.State.Action.ActionId);
        PlayerMotor motor = _root.GetComponent<PlayerMotor>();
        float boostRatio = _runner.Player.State.Action.Timeline.SelectMany(segment => segment.Events)
            .First(frameEvent => frameEvent.MotorCommand == MotorCommandKind.SprintBoost).Value;
        Assert.That(motor.HorizontalSpeed, Is.EqualTo(Mathf.Min(motor.Params.MaxSpeed,
            motor.Params.GroundSpeedThreshold + motor.Params.LowSpeedThreshold * boostRatio)).Within(.001f));
        Assert.AreEqual(0f, _mobilitySpentEnergy);
        Assert.AreEqual(1f, TimeManager.GlobalRate);
        for (int i = 0; i < 35; i++) yield return new WaitForFixedUpdate();
        Assert.AreEqual(1, commands);
        Assert.IsFalse(_runner.Player.IsRunning);
        Assert.AreEqual(0f, _mobilitySpentEnergy);
    }

    [UnityTest]
    public IEnumerator HoldingShiftThenPressingRightMouseDashesOnceAndDoesNotReopenTimeStop()
    {
        yield return PrepareMobilityGround();
        int accelerationCommands = 0;
        _runner.FrameEvent += (state, frameEvent) =>
        {
            if (frameEvent.EventKey == "motor.command" && frameEvent.MotorCommand == MotorCommandKind.Accelerate) accelerationCommands++;
        };
        InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.LeftShift)); InputSystem.Update();
        yield return new WaitForFixedUpdate();
        InputSystem.QueueStateEvent(_mouse, new MouseState().WithButton(MouseButton.Right)); InputSystem.Update();
        for (int i = 0; i < 5 && (!_runner.Player.IsRunning || _runner.Player.State.Action.ActionId != "player_dash"); i++) yield return new WaitForFixedUpdate();
        Assert.AreEqual("player_dash", _runner.Player.State.Action.ActionId);
        PlayerMotor motor = _root.GetComponent<PlayerMotor>();
        Assert.That(motor.HorizontalSpeed, Is.EqualTo(motor.Params.GroundSpeedThreshold * 4f).Within(.001f));
        Assert.That(_mobilitySpentEnergy, Is.EqualTo(50f).Within(.001f));
        for (int i = 0; i < 34; i++) yield return new WaitForFixedUpdate();
        Assert.AreEqual(1, accelerationCommands);
        Assert.That(_mobilitySpentEnergy, Is.EqualTo(50f).Within(.001f));
        Assert.GreaterOrEqual(_root.GetComponent<VectorEnergy>().CurrentEnergy, 150f);
        Assert.IsFalse(_root.GetComponent<TimeStopController>().IsTimeStopping);
    }

    [UnityTest]
    public IEnumerator HoldingRightMouseThenPressingShiftUsesAlternativeDashTrigger()
    {
        yield return PrepareMobilityGround();
        InputSystem.QueueStateEvent(_mouse, new MouseState().WithButton(MouseButton.Right)); InputSystem.Update();
        yield return new WaitForFixedUpdate();
        Assert.IsFalse(_runner.Player.IsRunning);
        InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.RightShift)); InputSystem.Update();
        for (int i = 0; i < 5 && !_runner.Player.IsRunning; i++) yield return new WaitForFixedUpdate();
        Assert.AreEqual("player_dash", _runner.Player.State.Action.ActionId);
        PlayerMotor motor = _root.GetComponent<PlayerMotor>();
        Assert.That(motor.HorizontalSpeed, Is.EqualTo(motor.Params.GroundSpeedThreshold * 4f).Within(.001f));
        Assert.That(_mobilitySpentEnergy, Is.EqualTo(50f).Within(.001f));
    }

    [UnityTest]
    public IEnumerator RightMouseForwardWithoutShiftDoesNotDash()
    {
        yield return PrepareMobilityGround();
        int accelerationCommands = 0;
        _runner.FrameEvent += (state, frameEvent) =>
        {
            if (frameEvent.EventKey == "motor.command" && frameEvent.MotorCommand == MotorCommandKind.Accelerate) accelerationCommands++;
        };
        InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.W)); InputSystem.Update();
        InputSystem.QueueStateEvent(_mouse, new MouseState().WithButton(MouseButton.Right)); InputSystem.Update();
        for (int i = 0; i < 5; i++) yield return new WaitForFixedUpdate();
        Assert.IsFalse(_runner.Player.IsRunning);
        Assert.AreEqual(0, accelerationCommands);
    }

    [UnityTest]
    public IEnumerator RightMouseSpaceUsesHighJumpAndCannotRelaunchInAir()
    {
        yield return PrepareMobilityGround();
        InputSystem.QueueStateEvent(_mouse, new MouseState().WithButton(MouseButton.Right)); InputSystem.Update();
        InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.Space)); InputSystem.Update();
        PlayerMotor motor = _root.GetComponent<PlayerMotor>();
        for (int i = 0; i < 5 && motor.JumpCount == 0; i++) yield return new WaitForFixedUpdate();
        Assert.AreEqual("player_high_jump", _runner.Player.State.Action.ActionId);
        Assert.Greater(motor.Velocity.y, motor.Params.JumpSpeed);
        Assert.AreEqual(1, motor.JumpCount);
        InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); InputSystem.Update();
        InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.Space)); InputSystem.Update();
        for (int i = 0; i < 10; i++) yield return new WaitForFixedUpdate();
        Assert.AreEqual(1, motor.JumpCount);
        Assert.That(_root.GetComponent<VectorEnergy>().CurrentEnergy, Is.EqualTo(180f).Within(.001f));
    }
}
#endif
