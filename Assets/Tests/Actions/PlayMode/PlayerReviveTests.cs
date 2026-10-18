#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GameJam.Actions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public sealed class PlayerReviveTests
{
    private GameObject _root;
    private PlayerDeathAnimation _death;
    private PlayerInputReader _input;
    private HealthComponent _health;
    private PlayerMotor _motor;
    private PlayerActionRunner _runner;
    private SkinnedMeshRenderer _skin;
    private readonly object _flowLock = new object();

    [SetUp]
    public void SetUp()
    {
        _root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab"));
        foreach (Camera camera in _root.GetComponentsInChildren<Camera>(true)) camera.enabled = false;
        _death = _root.GetComponent<PlayerDeathAnimation>();
        _health = _root.GetComponent<HealthComponent>();
        _input = _root.GetComponent<PlayerInputReader>();
        _motor = _root.GetComponent<PlayerMotor>();
        _runner = _root.GetComponent<PlayerActionRunner>();
        _skin = _root.GetComponentInChildren<SkinnedMeshRenderer>();
        _motor.Teleport(new Vector3(17000f, 30f, 17000f));
        Set(_input, "_focused", true);
        _input.SetGameplayEnabled(true);
        TimeManager.SetPaused(false);
        TimeManager.ClearSlowMotion();
    }

    [TearDown]
    public void TearDown()
    {
        TimeManager.Release(TimeManager.TimeLayer.Player, _flowLock);
        TimeManager.SetPaused(false);
        if (_runner != null) _runner.Disconnect();
        if (_root != null) Object.DestroyImmediate(_root);
        TimeManager.ClearSlowMotion();
    }

    private static void Set(object target, string field, object value)
        => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

    private float Elapsed => (float)typeof(PlayerDeathAnimation)
        .GetField("_reviveElapsed", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_death);

    private void Kill()
    {
        _health.TakeEnvironmentalDamage(_health.MaxHealth);
        Assert.IsTrue(_death.IsDead);
        Assert.IsFalse(_motor.enabled);
    }

    private void Revive()
    {
        Kill();
        _motor.Teleport(new Vector3(17003f, 30f, 17001f));
        _health.Reset();
        // 死亡界面和流式关卡都会再打开输入，仍不能跳过闪烁。
        _input.SetGameplayEnabled(true);
    }

    private IEnumerator WaitUntilSolid()
    {
        float deadline = TimeManager.UnscaledTime + 3f;
        while (_death.IsReviving && TimeManager.UnscaledTime < deadline) yield return null;
        Assert.IsFalse(_death.IsReviving, "闪烁未能结束");
    }

    [UnityTest]
    public IEnumerator ReviveBlinksAtCheckpointThenReturnsControl()
    {
        Revive();
        Vector3 checkpoint = _root.transform.position;
        Assert.IsTrue(_death.IsReviving);
        Assert.IsFalse(_death.IsDead);
        Assert.IsTrue(_skin.forceRenderingOff);
        Assert.IsFalse(_motor.enabled);
        Assert.IsFalse(_runner.enabled);
        Assert.IsFalse(_input.GameplayEnabled);
        Assert.AreEqual(0f, _health.TakeDamage(10f));
        bool sawVisible = false;
        float deadline = TimeManager.UnscaledTime + 3f;
        while (_death.IsReviving && TimeManager.UnscaledTime < deadline)
        {
            Assert.Less(Vector3.Distance(checkpoint, _root.transform.position), .001f);
            Assert.IsFalse(_input.GameplayEnabled);
            sawVisible |= !_skin.forceRenderingOff;
            yield return null;
        }
        Assert.IsTrue(sawVisible, "应经过可见阶段，而不是只隐藏后出现");
        Assert.IsFalse(_death.IsReviving);
        Assert.IsFalse(_skin.forceRenderingOff);
        Assert.IsTrue(_motor.enabled);
        Assert.IsTrue(_runner.enabled);
        Assert.IsTrue(_input.GameplayEnabled);
        Assert.IsTrue(_runner.IsConnected);
    }

    [UnityTest]
    public IEnumerator PauseKeepsBlinkAndProtectionUntilResumed()
    {
        Revive();
        TimeManager.SetPaused(true);
        float elapsed = Elapsed;
        yield return new WaitForSecondsRealtime(.25f);
        Assert.AreEqual(elapsed, Elapsed);
        Assert.IsTrue(_death.IsReviving);
        Assert.AreEqual(0f, _health.TakeDamage(10f));
        _input.SetGameplayEnabled(true);
        Assert.IsFalse(_input.GameplayEnabled);
        TimeManager.SetPaused(false);
        yield return WaitUntilSolid();
        Assert.IsTrue(_input.GameplayEnabled);
    }

    [UnityTest]
    public IEnumerator StreamingLockWaitsForTeleportAndDoesNotSkipEffect()
    {
        Kill();
        TimeManager.Apply(TimeManager.TimeLayer.Player, 0f, owner: _flowLock);
        _health.RestoreHealth(50f);
        for (int i = 0; i < 5; i++) yield return null;
        Assert.AreEqual(0f, Elapsed);
        Vector3 destination = new Vector3(17010f, 31f, 17010f);
        _motor.Teleport(destination);
        TimeManager.Release(TimeManager.TimeLayer.Player, _flowLock);
        _input.SetGameplayEnabled(true);
        Assert.IsFalse(_input.GameplayEnabled);
        yield return WaitUntilSolid();
        Assert.AreEqual(destination, _root.transform.position);
        Assert.IsTrue(_motor.enabled);
    }

    [UnityTest]
    public IEnumerator ExistingDisabledRenderersAndComponentsStayDisabled()
    {
        var respawn = _root.GetComponent<PlayerRespawn>();
        respawn.enabled = false;
        var extra = new GameObject("HiddenMesh");
        extra.transform.SetParent(_root.transform);
        var hidden = extra.AddComponent<MeshRenderer>();
        hidden.enabled = false;
        hidden.forceRenderingOff = true;
        Revive();
        yield return WaitUntilSolid();
        Assert.IsFalse(hidden.enabled);
        Assert.IsTrue(hidden.forceRenderingOff);
        Assert.IsFalse(respawn.enabled);
        Assert.IsFalse(_skin.forceRenderingOff);
    }

    [UnityTest]
    public IEnumerator FinishingDoesNotOverrideAnotherInputOwner()
    {
        Revive();
        var owner = new object();
        _input.SetGameplayBlocked(owner, true);
        yield return WaitUntilSolid();
        Assert.IsFalse(_input.GameplayEnabled);
        _input.SetGameplayBlocked(owner, false);
        Assert.IsTrue(_input.GameplayEnabled);
    }

    [UnityTest]
    public IEnumerator EnvironmentalDeathDuringBlinkCanReviveAgain()
    {
        Revive();
        Kill();
        Assert.IsFalse(_death.IsReviving);
        Assert.IsFalse(_skin.forceRenderingOff);
        _health.Reset();
        yield return WaitUntilSolid();
        Assert.IsTrue(_motor.enabled);
        Assert.IsTrue(_runner.enabled);
        Assert.IsTrue(_input.GameplayEnabled);
    }

    [Test]
    public void DisablingEffectRestoresVisibilityAndReleasesInputLock()
    {
        Revive();
        _death.enabled = false;
        Assert.IsFalse(_skin.forceRenderingOff);
        Assert.IsFalse(_death.IsReviving);
        Assert.IsTrue(_motor.enabled);
        Assert.IsTrue(_input.GameplayEnabled);
    }

    [Test]
    public void ZeroBlinkCountRestoresImmediately()
    {
        Set(_death, "_reviveBlinks", 0);
        Revive();
        Assert.IsFalse(_death.IsReviving);
        Assert.IsTrue(_motor.enabled);
        Assert.IsTrue(_input.GameplayEnabled);
    }

    [UnityTest]
    public IEnumerator CombatSceneDeathButtonRunsBlinkBeforeUnlock()
        => CheckSceneRespawn("Assets/Scenes/CombatTest.unity");

    [UnityTest]
    public IEnumerator StreamingSceneDeathButtonRebuildsThenRunsBlink()
        => CheckSceneRespawn("Assets/Scenes/FlowGame.unity");

    private IEnumerator CheckSceneRespawn(string path)
    {
        Object.DestroyImmediate(_root);
        _root = null;
        _runner = null;
        yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(
            path, new LoadSceneParameters(LoadSceneMode.Single));
        yield return null;
        var flow = GameJam.Flow.RunFlowController.Current;
        float deadline = TimeManager.UnscaledTime + 30f;
        while (flow != null && (!flow.IsReady || flow.IsBusy || flow.IsPreparing))
        {
            Assert.Less(TimeManager.UnscaledTime, deadline, "关卡初始化超时");
            yield return null;
        }
        foreach (Enemy enemy in Object.FindObjectsOfType<Enemy>()) enemy.gameObject.SetActive(false);
        _root = Player.Current.gameObject;
        _motor = _root.GetComponent<PlayerMotor>();
        _input = _root.GetComponent<PlayerInputReader>();
        _health = _root.GetComponent<HealthComponent>();
        _death = _root.GetComponent<PlayerDeathAnimation>();
        _runner = _root.GetComponent<PlayerActionRunner>();
        Set(_input, "_focused", true);
        _input.SetGameplayEnabled(true);
        GameManager.Instance.StartGame();
        Assert.IsNotNull(DeathRespawnUI.Instance);
        Kill();
        deadline = TimeManager.UnscaledTime + 5f;
        while (!DeathRespawnUI.Instance.IsOpen)
        {
            Assert.Less(TimeManager.UnscaledTime, deadline, "死亡动画结束后没有出现菜单");
            yield return null;
        }
        Assert.IsTrue(TimeManager.IsPaused);
        DeathRespawnUI.Instance.respawnButton.onClick.Invoke();
        deadline = TimeManager.UnscaledTime + 30f;
        while (!_death.IsReviving)
        {
            Assert.Less(TimeManager.UnscaledTime, deadline, "重建/复活后未进入闪烁");
            yield return null;
        }
        Assert.IsFalse(_input.GameplayEnabled);
        Assert.IsFalse(_motor.enabled);
        Assert.IsFalse(DeathRespawnUI.Instance.IsOpen);
        if (flow != null)
        {
            Vector3 checkpoint = flow.CurrentRoom.Root.FindCheckpoint(flow.Checkpoints.Id).transform.position;
            Assert.Less(Vector3.Distance(checkpoint, _root.transform.position), .01f);
        }
        yield return WaitUntilSolid();
        Assert.IsTrue(_motor.enabled);
        Assert.IsTrue(_input.GameplayEnabled);
        Assert.IsTrue(_runner.IsConnected);
        Assert.IsFalse(TimeManager.IsPaused);
        Assert.IsTrue(_health.IsAlive);
        // 实际场景中查看最终站立姿态，截图留在本地 Temp，不进入提交。
        if (path.Contains("CombatTest")) ScreenCapture.CaptureScreenshot("Temp/ReviveSolid.png");
        yield return null;
        if (flow != null) yield return flow.ReleaseRooms();
        _root = null;
        _runner = null;
    }
}
#endif
