using System.Collections;
using System.Collections.Generic;
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
    public IEnumerator AllFourDoorSeamsHaveNoAddedOrCrossSceneOverlapAndContinuousCollision()
    {
        for (int index = 0; index + 1 < Rooms.Length; index++)
        {
            yield return Until(() => _manager.IsRoomReady(Rooms[index + 1]));
            MapRoom room = Room(Rooms[index]);
            MapRoom next = Room(Rooms[index + 1]);
            Vector3 direction = room.ExitPoint.forward;
            Transform sourceDoor = DoorFrame(Rooms[index], room.ExitPoint);
            Transform targetDoor = DoorFrame(Rooms[index + 1], next.EntryPoint);
            float sourceDepth = sourceDoor.TransformVector(Vector3.forward * sourceDoor.GetComponent<MeshFilter>().sharedMesh.bounds.size.z).magnitude;
            float targetDepth = targetDoor.TransformVector(Vector3.forward * targetDoor.GetComponent<MeshFilter>().sharedMesh.bounds.size.z).magnitude;
            Vector3 start = room.ExitPoint.position - direction * (sourceDepth * .5f + .7f);
            Vector3 end = next.EntryPoint.position + direction * (targetDepth * .5f + .7f);
            Vector3 side = Vector3.Cross(Vector3.up, direction);
            List<FloorTriangle> floors = GatherFloors(new[] { Rooms[index], Rooms[index + 1] }, start.y);
            int samples = 0;
            for (float along = .137f; along < Vector3.Distance(start, end); along += .25f)
                foreach (float lateral in new[] { -1.4f, 0f, 1.4f })
                {
                    Vector3 point = start + direction * along + side * lateral;
                    MeshFilter[] visible = floors.Where(floor => ContainsFloorPoint(floor, point))
                        .Select(floor => floor.Filter).Distinct().ToArray();
                    Assert.That(visible, Is.Not.Empty, Rooms[index] + " 门口缺少可见地面，位置=" + point);
                    bool integrationOverlap = false;
                    for (int left = 0; left < visible.Length; left++)
                        for (int right = left + 1; right < visible.Length; right++)
                            integrationOverlap |= visible[left].gameObject.scene != visible[right].gameObject.scene ||
                                IsSeamFloor(visible[left].name) || IsSeamFloor(visible[right].name);
                    // 原白盒同一房间内的原始物体相交不属于本次接缝生成；检查新增层和跨场景层。
                    Assert.That(integrationOverlap, Is.False,
                        Rooms[index] + " 门口存在共面重叠：" + string.Join(", ", visible.Select(filter => filter.name)) + " 位置=" + point);
                    Assert.That(Physics.Raycast(point + Vector3.up * .5f, Vector3.down, 1f, ~0,
                        QueryTriggerInteraction.Ignore), Is.True, Rooms[index] + " 门口缺少碰撞地板，位置=" + point);
                    samples++;
                }
            Debug.Log($"WHITEBOX_SEAM_TEST {Rooms[index]} → {Rooms[index + 1]} samples={samples}");
            Assert.That(_manager.TryEnterRoom(Rooms[index], Rooms[index + 1]), Is.True);
            yield return Until(() => !_manager.IsTransitioning && _manager.CurrentRoomSceneName == Rooms[index + 1]);
            foreach (MapSeamSurface seam in SceneManager.GetSceneByName(Rooms[index + 1]).GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<MapSeamSurface>()))
                if (seam.NeighborSceneName == Rooms[index])
                    Assert.That(seam.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(seam.OwnerOnlyMesh), "邻房卸载后应恢复本房补面");
            List<FloorTriangle> remainingFloors = GatherFloors(new[] { Rooms[index + 1] }, start.y);
            for (float inside = .137f; inside < 2f; inside += .25f)
                foreach (float lateral in new[] { -1.4f, 0f, 1.4f })
                {
                    Vector3 point = next.EntryPoint.position + direction * (targetDepth * .5f + inside) + side * lateral;
                    Assert.That(remainingFloors.Any(floor => ContainsFloorPoint(floor, point)), Is.True,
                        Rooms[index + 1] + " 卸载上一房后入口缺少可见地面，位置=" + point);
                    Assert.That(Physics.Raycast(point + Vector3.up * .5f, Vector3.down, 1f, ~0,
                        QueryTriggerInteraction.Ignore), Is.True, "卸载上一房后入口缺少碰撞地板，位置=" + point);
                }
        }
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

    private struct FloorTriangle
    {
        public Vector3 A;
        public Vector3 B;
        public Vector3 C;
        public MeshFilter Filter;
    }

    private static bool IsSeamFloor(string name) => name == "入口门槛" || name == "出口门槛" || name == "两门间连接地板";

    private static List<FloorTriangle> GatherFloors(IEnumerable<string> scenes, float height)
    {
        var floors = new List<FloorTriangle>();
        foreach (string scene in scenes)
            foreach (MeshFilter filter in SceneManager.GetSceneByName(scene).GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<MeshFilter>()))
            {
                Renderer renderer = filter.GetComponent<Renderer>();
                if (filter.sharedMesh == null || renderer == null || !renderer.enabled ||
                    renderer.bounds.min.y > height + .001f || renderer.bounds.max.y < height - .001f) continue;
                Vector3[] vertices = filter.sharedMesh.vertices;
                int[] indices = filter.sharedMesh.triangles;
                for (int triangle = 0; triangle < indices.Length; triangle += 3)
                {
                    Vector3 a = filter.transform.TransformPoint(vertices[indices[triangle]]);
                    Vector3 b = filter.transform.TransformPoint(vertices[indices[triangle + 1]]);
                    Vector3 c = filter.transform.TransformPoint(vertices[indices[triangle + 2]]);
                    if (Mathf.Abs(a.y - height) > .001f || Mathf.Abs(b.y - height) > .001f ||
                        Mathf.Abs(c.y - height) > .001f || Vector3.Cross(b - a, c - a).normalized.y < .999f) continue;
                    floors.Add(new FloorTriangle { A = a, B = b, C = c, Filter = filter });
                }
            }
        return floors;
    }

    private static Transform DoorFrame(string sceneName, Transform anchor)
    {
        return SceneManager.GetSceneByName(sceneName).GetRootGameObjects().Single(root => root.name.StartsWith("BOX_S"))
            .GetComponentsInChildren<Transform>(true).Where(child => child.name.StartsWith("Door"))
            .OrderBy(child => Vector3.Distance(child.GetComponent<Renderer>().bounds.center, anchor.position)).First();
    }

    private static bool ContainsFloorPoint(FloorTriangle triangle, Vector3 point)
    {
        Vector3 u = triangle.B - triangle.A;
        Vector3 v = triangle.C - triangle.A;
        Vector3 offset = point - triangle.A;
        float determinant = u.x * v.z - u.z * v.x;
        if (Mathf.Abs(determinant) < .000001f) return false;
        float b = (offset.x * v.z - offset.z * v.x) / determinant;
        float c = (u.x * offset.z - u.z * offset.x) / determinant;
        return b >= -.00001f && c >= -.00001f && b + c <= 1.00001f;
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
