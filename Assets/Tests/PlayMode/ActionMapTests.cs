using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

public sealed class ActionMapTests : InputTestFixture
{
    private GameObject _player;
    private PlayerInputReader _input;
    private InputActionAsset _asset;
    private Keyboard _keyboard;

    [SetUp]
    public override void Setup()
    {
        base.Setup();
        _asset = InputActionAsset.FromJson(System.IO.File.ReadAllText(System.IO.Path.Combine(Application.dataPath, "Input/PlayerControls.inputactions")));
        _keyboard = InputSystem.AddDevice<Keyboard>();
        _asset.devices = new InputDevice[] { _keyboard };
        _player = new GameObject("InputTest");
        _player.SetActive(false);
        _input = _player.AddComponent<PlayerInputReader>();
        _input.Configure(_asset);
        _player.SetActive(true);
        _player.SendMessage("OnApplicationFocus", true);
    }
    [TearDown]
    public override void TearDown()
    {
        Object.DestroyImmediate(_player);
        Object.DestroyImmediate(_asset);
        InputSystem.RemoveDevice(_keyboard);
        base.TearDown();
    }

    [Test]
    public void PressAndReleaseBetweenTicks_IsDeliveredExactlyOnce()
    {
        Send(Key.Space);
        Send();
        Assert.IsTrue(_input.ReadFrame().JumpPressed);
        Assert.IsFalse(_input.ReadFrame().JumpPressed);
    }

    [Test]
    public void SprintAndSlide_AreIndependentActions()
    {
        Send(Key.W, Key.RightShift);
        PlayerInputFrame sprint = _input.ReadFrame();
        Assert.AreEqual(Vector2.up, sprint.Move);
        Assert.IsTrue(sprint.SprintHeld);
        Assert.IsFalse(sprint.SlidePressed);
        Send(Key.W, Key.RightShift, Key.LeftCtrl);
        PlayerInputFrame slide = _input.ReadFrame();
        Assert.IsTrue(slide.SlidePressed);
        Assert.IsTrue(slide.SlideHeld);
        Assert.IsFalse(_input.ReadFrame().SlidePressed);
    }

    [Test]
    public void MapDisabled_ClearsQueuedPress_AndDebugRemainsAvailable()
    {
        Send(Key.Space);
        _input.SetGameplayEnabled(false);
        Assert.IsFalse(_input.ReadFrame().JumpPressed);
        int count = 0;
        _input.ToggleHUD += () => count++;
        Send(Key.F3);
        Assert.AreEqual(1, count);
        Send();
        _input.SetGameplayEnabled(true);
        Assert.IsFalse(_input.ReadFrame().JumpPressed);
    }

    private void Send(params Key[] keys)
    {
        InputSystem.QueueStateEvent(_keyboard, new KeyboardState(keys));
        InputSystem.Update();
    }
}
