#if UNITY_EDITOR
using System.Collections;
using System.Reflection;
using GameJam.Flow;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

public sealed class SaveRecoveryTests
{
    private const string SaveKey = "playerSave";
    private bool _hadSave;
    private string _originalSave;
    private Player _player;
    private RunFlowController _flow;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        _hadSave = PlayerPrefs.HasKey(SaveKey);
        _originalSave = PlayerPrefs.GetString(SaveKey);
        SaveManager.CancelContinue();
        LevelSelection.Clear();
        TimeManager.SetPaused(false);
        yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/FlowGame.unity",
            new LoadSceneParameters(LoadSceneMode.Single));
        yield return Ready();
        FocusPlayer();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        TimeManager.SetPaused(false);
        if (RunFlowController.Current != null) yield return RunFlowController.Current.ReleaseRooms();
        SaveManager.CancelContinue();
        if (_hadSave) PlayerPrefs.SetString(SaveKey, _originalSave);
        else PlayerPrefs.DeleteKey(SaveKey);
        PlayerPrefs.Save();
    }

    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

    private IEnumerator Ready()
    {
        float deadline = Time.realtimeSinceStartup + 30f;
        do
        {
            yield return null;
            _flow = RunFlowController.Current;
            Assert.Less(Time.realtimeSinceStartup, deadline, _flow != null ? _flow.LastError : "流程未加载");
        } while (_flow == null || !_flow.IsReady || _flow.IsBusy || _flow.IsPreparing);
        _player = Player.Current;
        Assert.IsNotNull(_player);
        foreach (Enemy enemy in Object.FindObjectsOfType<Enemy>()) enemy.gameObject.SetActive(false);
    }

    private void FocusPlayer()
    {
        var input = _player.GetComponent<PlayerInputReader>();
        typeof(PlayerInputReader).GetField("_focused", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(input, true);
        input.SetGameplayEnabled(true);
    }

    private IEnumerator ReturnAndContinue(string savedOverride = null)
    {
        Object.FindObjectOfType<PauseUIManager>().ReturnToTitle();
        yield return null;
        var menu = Object.FindObjectOfType<MainMenuUI>();
        Assert.IsNotNull(menu);
        if (savedOverride != null) PlayerPrefs.SetString(SaveKey, savedOverride);
        Assert.IsTrue(SaveManager.HasSave);
        var button = Field<Button>(menu, "_continueButton");
        Assert.IsTrue(button.gameObject.activeInHierarchy);
        button.onClick.Invoke();
        yield return Ready();
        FocusPlayer();
    }

    private IEnumerator DieAndRespawn()
    {
        var health = _player.Health;
        var death = _player.GetComponent<PlayerDeathAnimation>();
        health.TakeEnvironmentalDamage(health.MaxHealth);
        float deadline = Time.realtimeSinceStartup + 10f;
        while (DeathRespawnUI.Instance == null || !DeathRespawnUI.Instance.IsOpen)
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "死亡面板未打开");
            yield return null;
        }
        DeathRespawnUI.Instance.respawnButton.onClick.Invoke();
        yield return Ready();
        deadline = Time.realtimeSinceStartup + 4f;
        while (death.IsReviving)
        {
            Assert.Less(Time.realtimeSinceStartup, deadline, "复活一直隐藏：PlayerRate=" + TimeManager.PlayerRate);
            yield return null;
        }
        Assert.IsTrue(health.IsAlive);
        Assert.IsTrue(_player.Motor.enabled);
        Assert.IsTrue(_player.GetComponent<PlayerInputReader>().GameplayEnabled);
        Assert.IsFalse(_player.GetComponentInChildren<SkinnedMeshRenderer>().forceRenderingOff);
        Assert.AreSame(_player.Motor, PlayerMotor.Active, "复活后全局玩家引用没有恢复");
    }

    [UnityTest]
    public IEnumerator ContinueCanRespawnTwice()
    {
        yield return ReturnAndContinue();
        yield return DieAndRespawn();
        yield return DieAndRespawn();
    }

    private IEnumerator EnterBreakableRoom()
    {
        for (int i = 0; _flow.CurrentRoom.Variant.StabilizerGain <= 0 && i < 12; i++)
        {
            Assert.IsTrue(_flow.TryEnterNextRoom());
            var checkpoint = _flow.CurrentRoom.Root.Checkpoints[0];
            _player.Motor.Teleport(checkpoint.transform.position);
            _flow.CommitCheckpoint(checkpoint);
            yield return Ready();
        }
        Assert.Greater(_flow.CurrentRoom.Variant.StabilizerGain, 0);
    }

    private static void Break(StabilityDevice device)
    {
        device.ReceiveAttack(new AttackDamageRequest(device.BreakHealth, device.transform.position, Vector3.forward, 1, 0));
    }

    private StabilityDevice FindDevice(string id)
    {
        foreach (var device in _flow.CurrentRoom.Root.GetComponentsInChildren<StabilityDevice>(true))
            if (device.Id == id) return device;
        Assert.Fail("读档后装置消失：" + id);
        return null;
    }

    private static void AssertBroken(StabilityDevice device)
    {
        Assert.IsTrue(device.IsBroken);
        if (device.Visual != null) Assert.IsFalse(device.Visual.activeSelf);
        foreach (var light in device.GetComponentsInChildren<Light>(true)) Assert.IsFalse(light.enabled);
        foreach (var collider in device.GetComponentsInChildren<Collider>(true)) Assert.IsFalse(collider.enabled);
    }

    [UnityTest]
    public IEnumerator ContinueRestoresChaosAndBrokenLightsWithoutAwardingChaosAgain()
    {
        yield return EnterBreakableRoom();
        var room = _flow.CurrentRoom;
        int before = Mathf.Min(room.Variant.MaximumChaos, _flow.Config.MaximumChaos - room.Variant.StabilizerGain);
        _flow.Chaos.Add(before - _flow.Chaos.Value, "存档测试");
        yield return Ready();
        var device = room.Root.GetComponentInChildren<StabilityDevice>(true);
        string id = device.Id;
        Break(device);
        AssertBroken(device);
        int savedChaos = _flow.Chaos.Value;
        Assert.AreEqual(before + room.Variant.StabilizerGain, savedChaos);
        yield return Ready();
        string variantId = room.Variant.Id;
        string checkpointId = _flow.Checkpoints.Id;
        Vector3 localPosition = room.Root.transform.InverseTransformPoint(_player.Motor.transform.position);
        for (int i = 0; i < 2; i++)
        {
            yield return ReturnAndContinue();
            Assert.AreEqual(variantId, _flow.CurrentRoom.Variant.Id);
            Assert.AreEqual(checkpointId, _flow.Checkpoints.Id);
            Assert.AreEqual(savedChaos, _flow.Chaos.Value);
            Assert.Less(Vector3.Distance(localPosition,
                _flow.CurrentRoom.Root.transform.InverseTransformPoint(_player.Motor.transform.position)), 0.1f);
            Assert.IsFalse(SaveManager.HasPendingContinue, "继续请求必须消费，避免后续开局重复读档");
            device = FindDevice(id);
            AssertBroken(device);
            Break(device);
            Assert.AreEqual(savedChaos, _flow.Chaos.Value);
        }
        int expectedAfterDeath = Mathf.Max(_flow.Config.MinimumChaos, savedChaos - _flow.Config.DeathPenalty);
        yield return DieAndRespawn();
        Assert.AreEqual(expectedAfterDeath, _flow.Chaos.Value);
        AssertBroken(FindDevice(id));
    }

    [UnityTest]
    public IEnumerator ManualLoadRestoresBothUnbrokenAndBrokenDeviceSnapshots()
    {
        yield return EnterBreakableRoom();
        var device = _flow.CurrentRoom.Root.GetComponentInChildren<StabilityDevice>(true);
        int initialChaos = _flow.Chaos.Value;
        SaveManager.Init(_player.gameObject);
        SaveManager.SavePlayer();
        Break(device);
        yield return Ready();
        AssertBroken(device);
        SaveManager.LoadPlayer();
        yield return Ready();
        Assert.IsFalse(device.IsBroken);
        Assert.AreEqual(initialChaos, _flow.Chaos.Value);
        Break(device);
        yield return Ready();
        int savedChaos = _flow.Chaos.Value;
        Assert.Greater(savedChaos, initialChaos);
        SaveManager.SavePlayer();
        for (int i = 0; i < 2; i++)
        {
            _flow.Chaos.Add(1, "存档测试");
            yield return Ready();
            SaveManager.LoadPlayer();
            yield return Ready();
            Assert.AreEqual(savedChaos, _flow.Chaos.Value);
            AssertBroken(device);
        }
    }

    [UnityTest]
    public IEnumerator LegacySaveWithoutFlowStateKeepsRoomStartingChaos()
    {
        RoomVariantDefinition variant = null;
        foreach (var candidate in LevelSelection.GetLevels(_flow.Config))
            if (candidate.MinimumChaos > _flow.Config.InitialChaos) { variant = candidate; break; }
        Assert.IsNotNull(variant);
        string legacy = "{\"playerPosition\":{\"x\":0,\"y\":1,\"z\":0},\"roomVariantId\":\"" + variant.Id +
            "\",\"roomLocalPosition\":{\"x\":0,\"y\":1,\"z\":0}}";
        yield return ReturnAndContinue(legacy);
        Assert.AreEqual(variant.Id, _flow.CurrentRoom.Variant.Id);
        Assert.AreEqual(variant.MinimumChaos, _flow.Chaos.Value);
        Assert.IsEmpty(_flow.CurrentRoom.BrokenDevices);
    }

    [UnityTest]
    public IEnumerator ContinueCanRestartFromPauseThenRespawn()
    {
        yield return EnterBreakableRoom();
        yield return ReturnAndContinue();
        var pause = Object.FindObjectOfType<PauseUIManager>();
        var oldRoom = _flow.CurrentRoom;
        pause.PauseGame();
        pause.RetryGame();
        yield return Ready();
        FocusPlayer();
        Assert.AreNotSame(oldRoom, _flow.CurrentRoom);
        Assert.AreSame(_flow.Config.InitialRoom, _flow.CurrentRoom.Variant);
        Assert.AreEqual(_flow.Config.InitialChaos, _flow.Chaos.Value);
        Assert.IsEmpty(_flow.CurrentRoom.BrokenDevices);
        Assert.IsFalse(SaveManager.HasPendingContinue);
        Assert.IsFalse(TimeManager.IsPaused);
        Assert.Greater(TimeManager.PlayerDeltaTime, 0f);
        Assert.IsTrue(_player.Motor.enabled);
        Assert.IsFalse(_player.GetComponentInChildren<SkinnedMeshRenderer>().forceRenderingOff);
        yield return DieAndRespawn();
    }

    [UnityTest]
    public IEnumerator LevelSelectionStartsSelectedRoomWithoutRestoringSave()
    {
        var config = _flow.Config;
        RoomVariantDefinition selected = null;
        foreach (var candidate in LevelSelection.GetLevels(config))
            if (candidate != config.InitialRoom && candidate != config.FinalRoom) { selected = candidate; break; }
        Assert.IsNotNull(selected);
        Assert.IsTrue(LevelSelection.Select(config, selected));
        yield return _flow.ReleaseRooms();
        yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/FlowGame.unity",
            new LoadSceneParameters(LoadSceneMode.Single));
        yield return Ready();
        Assert.AreSame(selected, _flow.CurrentRoom.Variant);
        Assert.AreEqual(Mathf.Clamp(config.InitialChaos, selected.MinimumChaos, selected.MaximumChaos), _flow.Chaos.Value);
        Assert.IsNull(LevelSelection.Consume(config));
        Assert.IsFalse(SaveManager.HasPendingContinue);
    }

    [UnityTest]
    public IEnumerator ClearSaveButtonRemovesContinueAndKeepsSettings()
    {
        Object.FindObjectOfType<PauseUIManager>().ReturnToTitle();
        yield return null;
        var menu = Object.FindObjectOfType<MainMenuUI>();
        var clear = Field<Button>(menu, "_clearSaveButton");
        Assert.IsTrue(clear.gameObject.activeInHierarchy);
        Assert.IsTrue(clear.interactable);
        Assert.AreEqual(Vector2.zero, ((RectTransform)clear.transform).anchorMin);
        Assert.AreEqual(Vector2.zero, ((RectTransform)clear.transform).anchorMax);
        string sentinel = "SaveRecoveryTests.Settings";
        PlayerPrefs.SetInt(sentinel, 42);
        try
        {
            clear.onClick.Invoke();
            Assert.IsFalse(SaveManager.HasSave);
            Assert.IsFalse(PlayerPrefs.HasKey(SaveKey));
            Assert.IsFalse(Field<Button>(menu, "_continueButton").gameObject.activeSelf);
            Assert.IsFalse(clear.interactable);
            Assert.AreEqual(42, PlayerPrefs.GetInt(sentinel));
        }
        finally { PlayerPrefs.DeleteKey(sentinel); }
    }
}
#endif
