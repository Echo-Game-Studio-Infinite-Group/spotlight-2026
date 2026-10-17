using System.Collections;
using System.Linq;
using NUnit.Framework;
using GameJam.Actions;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public sealed class MapStreamingTests
{
    private const string Demo = "MapStreamingDemo";
    private const string First = "Map_Room_01";
    private const string Second = "Map_Room_02";
    private const string Third = "Map_Room_03";
    private MapManager _manager;
    private MovementParams _originalMovement, _speedMovement;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        foreach (MapManager manager in Object.FindObjectsOfType<MapManager>()) Object.Destroy(manager.gameObject);
        yield return null;
        yield return SceneManager.LoadSceneAsync(Demo, LoadSceneMode.Single);
        _manager = MapManager.Instance;
        yield return Until(() => !_manager.IsTransitioning && _manager.CurrentRoomSceneName == First &&
            _manager.IsRoomReady(Second));
        yield return new WaitForFixedUpdate();
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        var player = Object.FindObjectOfType<PlayerMotor>();
        if (player != null && _originalMovement != null) player.SetParams(_originalMovement);
        if (_speedMovement != null) Object.Destroy(_speedMovement);
        _originalMovement = _speedMovement = null;
        if (_manager != null) Object.Destroy(_manager.gameObject);
        yield return null;
    }

    [UnityTest]
    public IEnumerator StartupLoadsOnlyCurrentAndSuccessorAndKeepsPlayerOnFloor()
    {
        var player = Object.FindObjectOfType<PlayerMotor>();
        Assert.That(player, Is.Not.Null);
        Assert.That(player.gameObject.scene.name, Is.EqualTo(Demo));
        Assert.That(player.transform.position.y, Is.GreaterThan(-.1f));
        Assert.That(_manager.IsRoomReady(First), Is.True);
        Assert.That(_manager.IsRoomReady(Second), Is.True);
        Assert.That(_manager.IsRoomReady(Third), Is.False, "预加载不能递归加载整张地图");
        Assert.That(Object.FindObjectOfType<MapBootstrapper>().IsReady, Is.True);
        Assert.That(Object.FindObjectsOfType<PlayerMotor>().Length, Is.EqualTo(1));
        Assert.That(SceneManager.GetActiveScene().name, Is.EqualTo(First));
        yield return null;
    }

    [UnityTest]
    public IEnumerator SplitWhiteboxStillHasItsGeometryAfterReload()
    {
        foreach (string roomName in new[] { First, Second })
        {
            Scene scene = SceneManager.GetSceneByName(roomName);
            GameObject box = scene.GetRootGameObjects().Single(root => root.name.StartsWith("BOX_S_"));
            MeshFilter[] meshes = box.GetComponentsInChildren<MeshFilter>(true);
            Assert.That(meshes.Length, Is.GreaterThan(10));
            foreach (MeshFilter mesh in meshes)
                Assert.That(mesh.sharedMesh, Is.Not.Null, roomName + "/" + mesh.name + " 丢失网格");
        }
        yield return null;
    }

    [UnityTest]
    public IEnumerator DuplicatePreloadDoesNotDuplicateSceneAndAlreadyLoadedEntryReleasesLock()
    {
        Assert.That(_manager.PreloadRoom(Second), Is.True);
        Assert.That(_manager.PreloadRoom(Second), Is.True);
        Assert.That(_manager.TryEnterRoom(string.Empty, First), Is.True);
        Assert.That(_manager.IsTransitioning, Is.False);
        int loaded = 0;
        _manager.RoomLoaded += name => { if (name == Third) loaded++; };
        Assert.That(_manager.PreloadRoom(Third), Is.True);
        Assert.That(_manager.PreloadRoom(Third), Is.True);
        yield return Until(() => _manager.IsRoomReady(Third));
        yield return null;
        int count = 0;
        for (int i = 0; i < SceneManager.sceneCount; i++)
            if (SceneManager.GetSceneAt(i).name == Third) count++;
        Assert.That(count, Is.EqualTo(1));
        Assert.That(loaded, Is.EqualTo(1));
    }

    [UnityTest]
    public IEnumerator WalkingThroughBothDoorsUnloadsPreviousRoomsWithoutReplacingOrTeleportingPlayer()
    {
        var player = Object.FindObjectOfType<PlayerMotor>();
        var camera = Camera.main;
        // 高速验收独立于策划正在调整的速度上限，不修改共享参数资产。
        _originalMovement = player.Params; _speedMovement = Object.Instantiate(_originalMovement);
        _speedMovement.MaxSpeed = 120f; player.SetParams(_speedMovement);
        int playerId = player.GetInstanceID();
        int cameraId = camera.GetInstanceID();
        player.GetComponent<PlayerActionRunner>().enabled = false;
        Assert.That(player.TryAcquireSimulation(this), Is.True);
        int teleports = 0;
        player.Teleport(new Vector3(0f, .1f, 30f));
        player.SetHorizontalSpeed(120f);
        player.Teleported += delta => teleports++;
        yield return WalkAcrossDoor(player, Second);
        yield return Until(() => !_manager.IsTransitioning && !_manager.IsRoomReady(First) && _manager.IsRoomReady(Third));
        Assert.That(Object.FindObjectOfType<PlayerMotor>().GetInstanceID(), Is.EqualTo(playerId));
        Assert.That(Camera.main.GetInstanceID(), Is.EqualTo(cameraId));
        Assert.That(player.HorizontalSpeed, Is.EqualTo(120f).Within(.001f));
        Assert.That(teleports, Is.Zero);
        Assert.That(player.gameObject.scene.name, Is.EqualTo(Demo));

        // 只缩短走到第二道门的等待；跨门本身始终由 CharacterController.Move 和真实触发器驱动。
        player.Teleport(new Vector3(0f, .1f, 62f));
        teleports = 0;
        player.SetHorizontalSpeed(120f);
        yield return WalkAcrossDoor(player, Third);
        yield return Until(() => !_manager.IsTransitioning && !_manager.IsRoomReady(Second));
        Assert.That(_manager.CurrentRoomSceneName, Is.EqualTo(Third));
        Assert.That(_manager.IsRoomReady(First), Is.False);
        Assert.That(_manager.IsRoomReady(Third), Is.True);
        Assert.That(player.GetInstanceID(), Is.EqualTo(playerId));
        Assert.That(Camera.main.GetInstanceID(), Is.EqualTo(cameraId));
        Assert.That(player.HorizontalSpeed, Is.EqualTo(120f).Within(.001f));
        Assert.That(teleports, Is.Zero);
        player.ReleaseSimulation(this);
    }

    [UnityTest]
    public IEnumerator GateStaysClosedUntilTargetSceneIsLoaded()
    {
        var root = new GameObject("GateTest");
        var door = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var gate = root.AddComponent<MapRoomGate>();
        gate.Configure(Third, door);
        yield return null;
        Assert.That(gate.IsOpen, Is.False);
        Assert.That(_manager.PreloadRoom(Third), Is.True);
        yield return Until(() => _manager.IsRoomReady(Third));
        yield return null;
        Assert.That(gate.IsOpen, Is.True);
        Object.Destroy(root);
        Object.Destroy(door);
    }

    [UnityTest]
    public IEnumerator MissingSceneIsRejectedWithoutUnloadingCurrentRoom()
    {
        LogAssert.Expect(LogType.Error, "[MapManager] 房间「Missing_Map_Test_Scene」不在 Build Settings 中");
        Assert.That(_manager.TryEnterRoom(First, "Missing_Map_Test_Scene"), Is.False);
        Assert.That(_manager.IsTransitioning, Is.False);
        Assert.That(_manager.IsRoomReady(First), Is.True);
        Assert.That(_manager.CurrentRoomSceneName, Is.EqualTo(First));
        yield return null;
    }

    [UnityTest]
    public IEnumerator PersistentPlayerSceneAndActiveRoomCannotBeUnloaded()
    {
        LogAssert.Expect(LogType.Error, "[MapManager] 场景「MapStreamingDemo」是常驻场景，不能作为待卸载房间");
        Assert.That(_manager.TryEnterRoom(Demo, Second), Is.False);
        LogAssert.Expect(LogType.Error, "[MapManager] 场景「Map_Room_01」仍在使用，不能卸载");
        Assert.That(_manager.TryUnloadRoom(First), Is.False);
        Assert.That(_manager.IsRoomReady(Demo), Is.True);
        Assert.That(_manager.IsRoomReady(First), Is.True);
        yield return null;
    }

    private IEnumerator WalkAcrossDoor(PlayerMotor player, string target)
    {
        var controller = player.GetComponent<CharacterController>();
        for (int i = 0; i < 60 && _manager.CurrentRoomSceneName != target; i++)
        {
            controller.Move(Vector3.forward * 2f);
            yield return new WaitForFixedUpdate();
        }
        Assert.That(_manager.CurrentRoomSceneName, Is.EqualTo(target), "真实门触发器没有触发切换");
    }

    private static IEnumerator Until(System.Func<bool> predicate)
    {
        float deadline = Time.realtimeSinceStartup + 30f;
        while (!predicate())
        {
            if (Time.realtimeSinceStartup > deadline) Assert.Fail("等待地图异步操作超时");
            yield return null;
        }
    }
}
