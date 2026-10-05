#if UNITY_EDITOR
using System.Collections;
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
    private PlayerActionRunner _runner;
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
        if (_root != null) Object.Destroy(_root);
        if (_previousSettings != null) InputSystem.settings = _previousSettings;
        if (_testSettings != null) Object.Destroy(_testSettings);
        TimeManager.ClearSlowMotion();
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
}
#endif
