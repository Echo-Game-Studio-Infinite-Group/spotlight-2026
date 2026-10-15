using System.Collections;
using System.Linq;
using GameJam.Actions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public sealed class TeamWhiteboxTests
{
    private static readonly string[] Rooms =
    {
        "Whitebox_Tutorial", "Whitebox_Room_01", "Whitebox_Room_02", "Whitebox_Room_03", "Whitebox_Room_04"
    };
    private MapManager _manager;

    [UnitySetUp]
    public IEnumerator SetUp()
    {
        foreach (MapManager manager in Object.FindObjectsOfType<MapManager>()) Object.Destroy(manager.gameObject);
        yield return null;
        yield return SceneManager.LoadSceneAsync("Boot", LoadSceneMode.Single);
        yield return Until(() => MapManager.Exists && MapManager.Instance.CurrentRoomSceneName == Rooms[0] &&
            !MapManager.Instance.IsTransitioning && MapManager.Instance.IsRoomReady(Rooms[1]) &&
            Object.FindObjectOfType<MapBootstrapper>() != null && Object.FindObjectOfType<MapBootstrapper>().IsReady);
        _manager = MapManager.Instance;
        yield return null;
    }

    [UnityTearDown]
    public IEnumerator TearDown()
    {
        if (_manager != null) Object.Destroy(_manager.gameObject);
        yield return null;
    }

    [UnityTest]
    public IEnumerator BootStartsAuthoredTutorialWithOnePlayerAndPreloadsOnlySuccessor()
    {
        Assert.That(SceneManager.GetSceneByName("Boot").isLoaded, Is.False);
        Assert.That(_manager.BootstrapSceneName, Is.EqualTo("WhiteboxGame"));
        Assert.That(GameManager.Instance.State, Is.EqualTo(GameState.Playing));
        Assert.That(Object.FindObjectsOfType<PlayerMotor>().Length, Is.EqualTo(1));
        PlayerMotor player = Object.FindObjectOfType<PlayerMotor>();
        Assert.That(player.gameObject.scene.name, Is.EqualTo("WhiteboxGame"));
        Assert.That(Camera.main, Is.Not.Null);
        Assert.That(_manager.IsRoomReady(Rooms[2]), Is.False);
        foreach (string roomName in Rooms.Take(2)) AssertGeometry(roomName);
        MapRoom room = Room(Rooms[0]);
        Assert.That(Vector3.Distance(player.transform.position, room.EntryPoint.position), Is.LessThan(10f));
        Assert.That(player.transform.position.y, Is.GreaterThan(-1f));
        Assert.That(NavMesh.SamplePosition(player.transform.position, out NavMeshHit hit, 4f, NavMesh.AllAreas),
            Is.True, "出生点应有房间导航数据");
        yield return null;
    }

    [UnityTest]
    public IEnumerator NeighboringAuthoredDoorwaysFaceEachOtherAndHaveContinuousFloor()
    {
        MapRoom first = Room(Rooms[0]);
        MapRoom next = Room(Rooms[1]);
        Assert.That(Vector3.Dot(first.ExitPoint.forward, next.EntryPoint.forward), Is.GreaterThan(.999f));
        Assert.That(first.ExitPoint.position.y, Is.EqualTo(next.EntryPoint.position.y).Within(.02f));
        Vector3 connection = next.EntryPoint.position - first.ExitPoint.position;
        Assert.That(Vector3.Cross(connection.normalized, first.ExitPoint.forward).magnitude, Is.LessThan(.01f));
        Assert.That(connection.magnitude, Is.LessThan(25f));
        Vector3 midpoint = (first.ExitPoint.position + next.EntryPoint.position) * .5f;
        Assert.That(Physics.Raycast(midpoint + Vector3.up, Vector3.down, 2f), Is.True);
        yield return null;
    }

    [UnityTest]
    public IEnumerator EveryAuthoredDoorCanBeCrossedAt120MetresPerSecondWithoutLosingPlayerOrVelocity()
    {
        PlayerMotor player = Object.FindObjectOfType<PlayerMotor>();
        int playerId = player.GetInstanceID();
        int cameraId = Camera.main.GetInstanceID();
        player.GetComponent<PlayerActionRunner>().enabled = false;
        Assert.That(player.TryAcquireSimulation(this), Is.True);
        CharacterController controller = player.GetComponent<CharacterController>();
        for (int index = 0; index < Rooms.Length - 1; index++)
        {
            string source = Rooms[index];
            string target = Rooms[index + 1];
            yield return Until(() => _manager.IsRoomReady(target));
            yield return null;
            MapRoom room = Room(source);
            Transform exit = room.ExitPoint;
            GameObject box = SceneManager.GetSceneByName(source).GetRootGameObjects().Single(root => root.name.StartsWith("BOX_S"));
            Transform frame = box.GetComponentsInChildren<Transform>(true)
                .Where(child => child.name.StartsWith("Door"))
                .OrderBy(child => Vector3.Distance(child.GetComponent<Renderer>().bounds.center, exit.position)).First();
            float depth = frame.TransformVector(Vector3.forward * frame.GetComponent<MeshFilter>().sharedMesh.bounds.size.z).magnitude;
            player.Teleport(exit.position - exit.forward * (depth * .5f + 2f) + Vector3.up * .15f);
            player.SetHorizontalSpeed(120f);
            Vector3 direction = exit.forward;
            int teleports = 0;
            System.Action<Vector3> onTeleport = delta => teleports++;
            player.Teleported += onTeleport;
            for (int step = 0; step < 40 && _manager.CurrentRoomSceneName != target; step++)
            {
                controller.Move(direction * 2f);
                yield return new WaitForFixedUpdate();
            }
            player.Teleported -= onTeleport;
            Assert.That(_manager.CurrentRoomSceneName, Is.EqualTo(target), source + " 的真实门洞未能通过，位置=" + player.transform.position);
            yield return Until(() => !_manager.IsTransitioning && !_manager.IsRoomReady(source));
            Assert.That(player.GetInstanceID(), Is.EqualTo(playerId));
            Assert.That(Camera.main.GetInstanceID(), Is.EqualTo(cameraId));
            Assert.That(player.HorizontalSpeed, Is.EqualTo(120f).Within(.001f));
            Assert.That(teleports, Is.Zero);
            AssertGeometry(target);
            for (int previous = 0; previous <= index; previous++)
                Assert.That(_manager.IsRoomReady(Rooms[previous]), Is.False);
        }
        player.ReleaseSimulation(this);
    }

    [UnityTest]
    public IEnumerator FallingRespawnsInCurrentRoomAfterEarlierRoomsHaveBeenUnloaded()
    {
        for (int index = 1; index <= 3; index++)
        {
            string source = Rooms[index - 1];
            string target = Rooms[index];
            Assert.That(_manager.TryEnterRoom(source, target), Is.True);
            yield return Until(() => !_manager.IsTransitioning && _manager.CurrentRoomSceneName == target);
        }
        PlayerMotor player = Object.FindObjectOfType<PlayerMotor>();
        PlayerRespawn respawn = player.GetComponent<PlayerRespawn>();
        MapRoom room = Room(Rooms[3]);
        Assert.That(respawn.RespawnPosition, Is.EqualTo(room.RespawnPoint.position));
        Assert.That(respawn.KillHeight, Is.EqualTo(room.FallDeathY));
        player.GetComponent<PlayerActionRunner>().enabled = false;
        Assert.That(player.TryAcquireSimulation(this), Is.True);
        player.Teleport(room.RespawnPoint.position + Vector3.down * (room.RespawnPoint.position.y - room.FallDeathY + 2f));
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();
        Assert.That(Vector3.Distance(player.transform.position, room.RespawnPoint.position), Is.LessThan(.05f));
        Assert.That(_manager.IsRoomReady(Rooms[0]), Is.False);
        Assert.That(_manager.IsRoomReady(Rooms[3]), Is.True);
        Assert.That(_manager.CurrentRoomSceneName, Is.EqualTo(Rooms[3]));
        player.ReleaseSimulation(this);
    }

    [UnityTest]
    public IEnumerator GameRestartReturnsToTutorialAndRebuildsPlayerInsteadOfLoadingOneRoomAlone()
    {
        int playerId = Object.FindObjectOfType<PlayerMotor>().GetInstanceID();
        Assert.That(_manager.TryEnterRoom(Rooms[0], Rooms[1]), Is.True);
        yield return Until(() => !_manager.IsTransitioning && _manager.IsRoomReady(Rooms[2]));
        GameManager.Instance.RestartLevel();
        yield return Until(() => !_manager.IsTransitioning && _manager.CurrentRoomSceneName == Rooms[0] &&
            _manager.IsRoomReady(Rooms[1]) && Object.FindObjectOfType<PlayerMotor>() != null &&
            Object.FindObjectOfType<PlayerMotor>().GetInstanceID() != playerId &&
            Object.FindObjectOfType<MapBootstrapper>().IsReady);
        Assert.That(_manager.IsRoomReady(Rooms[2]), Is.False);
        Assert.That(Object.FindObjectsOfType<PlayerMotor>().Length, Is.EqualTo(1));
        Assert.That(Camera.main, Is.Not.Null);
        Assert.That(GameManager.Instance.State, Is.EqualTo(GameState.Playing));
        yield return null;
    }

    private static MapRoom Room(string name)
    {
        return SceneManager.GetSceneByName(name).GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<MapRoom>(true)).Single();
    }

    private static void AssertGeometry(string name)
    {
        GameObject root = SceneManager.GetSceneByName(name).GetRootGameObjects().Single(item => item.name.StartsWith("BOX_S"));
        MeshFilter[] meshes = root.GetComponentsInChildren<MeshFilter>(true);
        Assert.That(meshes.Length, Is.GreaterThan(10));
        foreach (MeshFilter filter in meshes) Assert.That(filter.sharedMesh, Is.Not.Null, name + "/" + filter.name);
        Assert.That(root.GetComponentsInChildren<Transform>(true).Count(child => child.name.StartsWith("Door")), Is.EqualTo(2));
    }

    private static IEnumerator Until(System.Func<bool> predicate)
    {
        float deadline = Time.realtimeSinceStartup + 30f;
        while (!predicate())
        {
            if (Time.realtimeSinceStartup > deadline) Assert.Fail("等待团队白盒加载超时");
            yield return null;
        }
    }
}
