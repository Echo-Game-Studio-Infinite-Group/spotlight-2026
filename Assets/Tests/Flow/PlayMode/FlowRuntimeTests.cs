using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GameJam.Actions;
using GameJam.Flow;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif

namespace GameJam.FlowTests
{
    public sealed class FlowRuntimeTests
    {
        private RunFlowController _flow;
        private PlayerMotor _motor;
        private HealthComponent _health;
        private MovementParams _originalMovement, _speedMovement;
        private readonly List<FlowRoomInstance> _spawnRooms = new List<FlowRoomInstance>();
        private readonly List<Object> _spawnObjects = new List<Object>();
        [UnitySetUp]
        public IEnumerator SetUp()
        {
            foreach (MapManager map in Object.FindObjectsOfType<MapManager>()) Object.Destroy(map.gameObject);
            TimeManager.SetPaused(false);
            yield return null;
#if UNITY_EDITOR
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/FlowGame.unity", new LoadSceneParameters(LoadSceneMode.Single));
#else
            Assert.Ignore("白盒资产回归需在编辑器运行");
#endif
            _flow = Object.FindObjectOfType<RunFlowController>();
            _motor = Object.FindObjectOfType<PlayerMotor>(); _health = _motor.GetComponent<HealthComponent>();
            yield return Ready();
            _motor.GetComponent<PlayerActionRunner>().enabled = false;
            Assert.That(_motor.TryAcquireSimulation(this), Is.True);
        }
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            TimeManager.SetPaused(false);
            if (_motor != null && _originalMovement != null) _motor.SetParams(_originalMovement);
            if (_speedMovement != null) Object.Destroy(_speedMovement);
            _originalMovement = _speedMovement = null;
            if (_motor != null) _motor.ReleaseSimulation(this);
            if (_spawnRooms.Count > 0)
            {
                MapManager maps = MapManager.Instance;
                maps.ReleaseDetachedFlowRooms(_spawnRooms.ToArray());
                foreach (FlowRoomInstance room in _spawnRooms) yield return Until(() => !maps.FlowRooms.Contains(room));
                _spawnRooms.Clear();
            }
            foreach (Object item in _spawnObjects) if (item != null) Object.Destroy(item);
            _spawnObjects.Clear();
            if (_flow != null) yield return _flow.ReleaseRooms();
            yield return null;
        }
        private IEnumerator Ready() => Until(() => _flow != null && _flow.IsReady && !_flow.IsBusy && !_flow.IsPreparing && _flow.NextRoom != null);
        private static IEnumerator Until(Func<bool> predicate)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!predicate())
            {
                if (Time.realtimeSinceStartup >= deadline)
                    Assert.Fail("等待流程超时：" + (RunFlowController.Current != null ? RunFlowController.Current.LastError : "没有流程组件"));
                yield return null;
            }
        }
        private void EnterAndCommit()
        {
            Assert.That(_flow.TryEnterNextRoom(), Is.True);
            FlowCheckpoint checkpoint = _flow.CurrentRoom.Root.Checkpoints.OrderBy(point => point.Priority).First();
            _motor.Teleport(checkpoint.transform.position);
            Assert.That(_flow.CommitCheckpoint(checkpoint), Is.True);
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator SpawnPointsFollowRealRoomNavigationVariantRebuildAndUnload()
        {
            MapManager maps = MapManager.Instance;
            RoomDefinition definition = Object.Instantiate(_flow.CurrentRoom.Variant.Room); _spawnObjects.Add(definition);
            GameObject source = Object.Instantiate(definition.Template.gameObject); source.SetActive(false); _spawnObjects.Add(source);
            definition.Template = source.GetComponent<FlowRoomTemplate>();
            var pointObject = new GameObject("集成出生点"); pointObject.transform.SetParent(source.transform, false);
            FlowEnemySpawnPoint point = pointObject.AddComponent<FlowEnemySpawnPoint>(); point.Id = "integration";
            RoomVariantDefinition first = Object.Instantiate(_flow.CurrentRoom.Variant); _spawnObjects.Add(first); first.Room = definition;
            RoomVariantDefinition second = Object.Instantiate(first); _spawnObjects.Add(second);
            first.EnemySpawns = new[] { new FlowEnemySpawnBinding { PointId = point.Id, Enabled = true,
                EnemyPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/EnemyTest.prefab") } };
            second.EnemySpawns = new[] { new FlowEnemySpawnBinding { PointId = point.Id, Enabled = true,
                EnemyPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/EnemyTestRange.prefab") } };
            definition.Variants = new[] { first, second };
            FlowRoomInstance room = maps.PreloadFlowRoom(first, new Pose(new Vector3(40000f, 0f, 40000f), Quaternion.Euler(0f, 90f, 0f)), _flow);
            _spawnRooms.Add(room);
            yield return Until(() => !room.IsLoading);
            Assert.That(room.IsReady, Is.True, room.Error);
            Assert.That(room.Root.Enemies.Instances, Is.Empty, "预加载不能提前启动敌人");
            Vector3 candidate = room.Root.Entry.position + room.Root.Entry.forward * 5f;
            Assert.That(NavMesh.SamplePosition(candidate, out NavMeshHit hit, 40f, NavMesh.AllAreas), Is.True);
            Vector3 local = room.Root.transform.InverseTransformPoint(hit.position);
            point.transform.localPosition = local;
            room.Root.GetComponentsInChildren<FlowEnemySpawnPoint>(true).Single().transform.localPosition = local;
            Assert.That(maps.TryActivateFlowRoom(room), Is.True);
            GameObject firstEnemy = room.Root.Enemies.Instances[point.Id];
            Assert.That(firstEnemy.GetComponent<NavMeshAgent>().isOnNavMesh, Is.True);
            Assert.That(firstEnemy.GetComponent<RangeComponent>(), Is.Null);
            yield return maps.RebuildFlowRoom(room, second, _flow);
            yield return null;
            Assert.That(room.Error, Is.Empty);
            Assert.That(firstEnemy == null, Is.True, "重建应清理旧差分敌人");
            GameObject secondEnemy = room.Root.Enemies.Instances[point.Id];
            Assert.That(secondEnemy.GetComponent<RangeComponent>(), Is.Not.Null);
            Assert.That(secondEnemy.GetComponent<NavMeshAgent>().isOnNavMesh, Is.True);
            Assert.That(maps.TryActivateFlowRoom(_flow.CurrentRoom), Is.True);
            Assert.That(secondEnemy.activeInHierarchy, Is.False, "离开当前房间应暂停敌人");
            Assert.That(maps.TryUnloadFlowRoom(room), Is.True);
            yield return Until(() => !room.IsUnloading);
            Assert.That(secondEnemy == null, Is.True);
            Assert.That(room.Scene.isLoaded, Is.False);
        }
#endif

        [UnityTest]
        public IEnumerator StartupKeepsOnePlayerAndOnlyCurrentAndSuccessor()
        {
            Assert.That(Object.FindObjectsOfType<PlayerMotor>().Length, Is.EqualTo(1));
            Assert.That(MapManager.Instance.FlowRooms.Count, Is.EqualTo(2));
            Assert.That(_flow.Checkpoints.Room, Is.SameAs(_flow.CurrentRoom));
            Assert.That(_flow.CurrentRoom.Root.EntryDoor.activeSelf, Is.True);
            Assert.That(_flow.CurrentRoom.Root.ExitDoor.activeSelf, Is.False);
            Assert.That(Physics.Raycast(_motor.transform.position + Vector3.up, Vector3.down, 2f), Is.True);
            Assert.That(MapManager.Instance.TryUnloadFlowRoom(_flow.CurrentRoom), Is.False);
            yield return null;
        }
        [UnityTest]
        public IEnumerator EnteringWithoutCheckpointRetainsThePredecessor()
        {
            FlowRoomInstance prior = _flow.CurrentRoom;
            Assert.That(_flow.TryEnterNextRoom(), Is.True);
            yield return Ready();
            Assert.That(prior.Scene.isLoaded, Is.True);
            Assert.That(prior.IsCheckpointProtected, Is.True);
            Assert.That(_flow.History.Count, Is.EqualTo(2));
            Assert.That(_flow.CurrentRoom.Root.EntryDoor.activeSelf, Is.False);
            FlowCheckpoint checkpoint = _flow.CurrentRoom.Root.Checkpoints[0];
            _motor.Teleport(checkpoint.transform.position); _flow.CommitCheckpoint(checkpoint);
            yield return Until(() => !prior.Scene.isLoaded);
            Assert.That(_flow.History.Count, Is.EqualTo(1));
            Assert.That(_flow.CurrentRoom.Root.EntryDoor.activeSelf, Is.True);
        }
        [UnityTest]
        public IEnumerator DeathBeforeNewCheckpointReturnsToTheProtectedRoom()
        {
            FlowRoomInstance protectedRoom = _flow.Checkpoints.Room;
            Assert.That(_flow.TryEnterNextRoom(), Is.True);
            Assert.That(_flow.Recover(true), Is.True);
            Assert.That(_flow.Recover(true), Is.False);
            yield return Ready();
            Assert.That(_flow.CurrentRoom, Is.SameAs(protectedRoom));
            Assert.That(_flow.History.Count, Is.EqualTo(1));
            Assert.That(_health.IsAlive, Is.True);
        }
        [UnityTest]
        public IEnumerator DeathRebuildsCurrentRoomUsingTheNewChaosVariant()
        {
            _flow.Chaos.Add(35, "test"); yield return Ready();
            EnterAndCommit(); yield return Ready();
            FlowRoomInstance room = _flow.CurrentRoom;
            long id = room.Id; string family = room.Variant.Room.Id;
            Assert.That(room.Variant.MinimumChaos, Is.EqualTo(31));
            _health.SetInvulnerableTime(0); _health.TakeDamage(_health.MaxHealth);
            Assert.That(_flow.IsBusy, Is.True);
            yield return Ready();
            Assert.That(_flow.Chaos.Value, Is.EqualTo(25));
            Assert.That(_flow.CurrentRoom.Id, Is.EqualTo(id));
            Assert.That(_flow.CurrentRoom.Variant.Room.Id, Is.EqualTo(family));
            Assert.That(_flow.CurrentRoom.Variant.MaximumChaos, Is.EqualTo(30));
            Assert.That(_health.Health, Is.EqualTo(_health.MaxHealth));
            Assert.That(_motor.HorizontalSpeed, Is.Zero);
            Assert.That(_motor.GetComponent<CharacterController>().enabled, Is.True);
        }
        [UnityTest]
        public IEnumerator FallingCostsHealthButDoesNotApplyTheDeathPenalty()
        {
            _flow.Chaos.Add(25, "test"); yield return Ready();
            RoomVariantDefinition planned = _flow.NextRoom.Variant;
            uint random = _flow.Planner.Random.State;
            float before = _health.Health; _health.SetInvulnerableTime(0);
            Assert.That(_flow.HandleFall(), Is.True); Assert.That(_flow.HandleFall(), Is.False);
            yield return Ready();
            Assert.That(_health.Health, Is.EqualTo(before - _flow.Config.FallDamage));
            Assert.That(_flow.Chaos.Value, Is.EqualTo(25));
            Assert.That(_flow.NextRoom.Variant, Is.SameAs(planned));
            Assert.That(_flow.Planner.Random.State, Is.EqualTo(random));
        }
        [UnityTest]
        public IEnumerator CombatInvulnerabilityDoesNotCancelTheFallPenalty()
        {
            _health.SetInvulnerableTime(10f); _health.TakeDamage(1f);
            Assert.That(_health.IsInvulnerable, Is.True);
            float before = _health.Health;
            _flow.HandleFall(); yield return Ready();
            Assert.That(_health.Health, Is.EqualTo(before - _flow.Config.FallDamage));
            Assert.That(_flow.Chaos.Value, Is.Zero);
        }
        [UnityTest]
        public IEnumerator RecoveryRemovesSpawnedSceneRootObjectsOutsideTheTemplate()
        {
            var spawned = new GameObject("房间内动态根对象");
            SceneManager.MoveGameObjectToScene(spawned, _flow.CurrentRoom.Scene);
            Assert.That(spawned.transform.parent, Is.Null);
            _flow.Recover(false); yield return Ready();
            Assert.That(spawned == null, Is.True, "重建必须清理模板层级外的动态敌人、弹体和碎块");
            Assert.That(_flow.CurrentRoom.Root, Is.Not.Null);
        }
        [UnityTest]
        public IEnumerator BrokenDeviceRemainsBrokenAfterDeathInTheSameVisit()
        {
            for (int i = 0; _flow.CurrentRoom.Variant.StabilizerGain == 0 && i < 10; i++)
            { EnterAndCommit(); yield return Ready(); }
            Assert.That(_flow.CurrentRoom.Variant.StabilizerGain, Is.GreaterThan(0));
            StabilityDevice device = _flow.CurrentRoom.Root.GetComponentInChildren<StabilityDevice>();
            int gain = _flow.CurrentRoom.Variant.StabilizerGain;
            device.ReceiveAttack(new AttackDamageRequest(100, device.transform.position, Vector3.forward, 1, 0));
            Assert.That(_flow.Chaos.Value, Is.EqualTo(gain));
            device.ReceiveAttack(new AttackDamageRequest(100, device.transform.position, Vector3.forward, 2, 0));
            Assert.That(_flow.Chaos.Value, Is.EqualTo(gain));
            _flow.Recover(true); yield return Ready();
            StabilityDevice rebuilt = _flow.CurrentRoom.Root.GetComponentInChildren<StabilityDevice>(true);
            Assert.That(rebuilt.IsBroken, Is.True);
            int chaos = _flow.Chaos.Value;
            rebuilt.ReceiveAttack(new AttackDamageRequest(100, rebuilt.transform.position, Vector3.forward, 3, 0));
            Assert.That(_flow.Chaos.Value, Is.EqualTo(chaos));
        }
        [UnityTest]
        public IEnumerator SameTemplateCanExistAsTwoIndependentSceneInstances()
        {
            MapManager maps = MapManager.Instance;
            FlowRoomInstance a = maps.PreloadFlowRoom(_flow.Config.InitialRoom, new Pose(new Vector3(10000, 0, 0), Quaternion.identity), _flow);
            FlowRoomInstance b = maps.PreloadFlowRoom(_flow.Config.InitialRoom, new Pose(new Vector3(11000, 0, 0), Quaternion.Euler(0, 90, 0)), _flow);
            yield return Until(() => a.IsReady && b.IsReady);
            Assert.That(a.Id, Is.Not.EqualTo(b.Id)); Assert.That(a.Scene.handle, Is.Not.EqualTo(b.Scene.handle));
            Assert.That(a.Root, Is.Not.SameAs(b.Root));
            a.BrokenDevices.Add("lamp"); Assert.That(b.BrokenDevices, Is.Empty);
            Assert.That(NavMesh.SamplePosition(a.Root.Checkpoints[0].transform.position, out NavMeshHit hit, 3f, NavMesh.AllAreas), Is.True);
            Assert.That(hit.position.x, Is.GreaterThan(9000));
            maps.TryUnloadFlowRoom(a); maps.TryUnloadFlowRoom(b);
            yield return Until(() => !a.Scene.isLoaded && !b.Scene.isLoaded);
        }
        [UnityTest]
        public IEnumerator ActualDoorCrossingAt120MetresPerSecondPreservesPlayerAndSpeed()
        {
            int playerId = _motor.GetInstanceID(), cameraId = Camera.main.GetInstanceID();
            _originalMovement = _motor.Params; _speedMovement = Object.Instantiate(_originalMovement);
            _speedMovement.MaxSpeed = 120f; _motor.SetParams(_speedMovement);
            for (int door = 0; door < 12; door++)
            {
                FlowRoomInstance source = _flow.CurrentRoom, target = _flow.NextRoom;
                Vector3 forward = source.Root.Exit.forward;
                _motor.Teleport(source.Root.Exit.position - forward * 2f + Vector3.up * .15f);
                _motor.SetHorizontalSpeed(120);
                int teleports = 0; Action<Vector3> handler = delta => teleports++;
                _motor.Teleported += handler;
                CharacterController controller = _motor.GetComponent<CharacterController>();
                int steps = Mathf.CeilToInt(Vector3.Distance(source.Root.Exit.position, target.Root.Entry.position) / 2f) + 20;
                for (int step = 0; step < steps && _flow.CurrentRoom == source; step++)
                { controller.Move(forward * 2f); yield return new WaitForFixedUpdate(); }
                _motor.Teleported -= handler;
                Assert.That(_flow.CurrentRoom, Is.SameAs(target), source.Variant.Id + " 跨门失败，位置=" + _motor.transform.position);
                Assert.That(_motor.GetInstanceID(), Is.EqualTo(playerId)); Assert.That(Camera.main.GetInstanceID(), Is.EqualTo(cameraId));
                Assert.That(_motor.HorizontalSpeed, Is.EqualTo(120).Within(.001f)); Assert.That(teleports, Is.Zero);
                FlowCheckpoint checkpoint = target.Root.Checkpoints[0];
                _motor.Teleport(checkpoint.transform.position); _flow.CommitCheckpoint(checkpoint);
                yield return Ready();
            }
        }
        [UnityTest]
        public IEnumerator RecoveryRestoresTheCheckpointFacingInBothMotorAndCameraTarget()
        {
            PlayerCameraRig rig = _flow.gameObject.scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<PlayerCameraRig>()).Single();
            rig.SetViewRotation(Quaternion.Euler(10, 135, 0));
            _flow.Recover(false); yield return Ready();
            FlowCheckpoint checkpoint = _flow.CurrentRoom.Root.FindCheckpoint(_flow.Checkpoints.Id);
            Assert.That(Quaternion.Angle(_motor.transform.rotation, checkpoint.transform.rotation), Is.LessThan(.01f));
            Assert.That(Quaternion.Angle(rig.ViewRotation, checkpoint.transform.rotation), Is.LessThan(.01f));
        }
        [UnityTest]
        public IEnumerator FiftyVisitsReleaseOldScenesAndKeepIdsUnique()
        {
            var ids = new HashSet<long> { _flow.CurrentRoom.Id };
            for (int i = 0; i < 50; i++)
            {
                EnterAndCommit(); yield return Ready();
                yield return Until(() => MapManager.Instance.FlowRooms.Count == 2);
                Assert.That(ids.Add(_flow.CurrentRoom.Id), Is.True);
            }
            Assert.That(_flow.Chaos.Value, Is.Zero);
            yield return null;
            Assert.That(Resources.FindObjectsOfTypeAll<Mesh>().Count(mesh => mesh.name == "动态连接段去重地板"), Is.LessThanOrEqualTo(4));
        }
        [UnityTest]
        public IEnumerator CheckpointPriorityCanOnlyMoveBackWhenExplicitlyAllowed()
        {
            FlowCheckpoint entry = _flow.CurrentRoom.Root.Checkpoints[0];
            var point = new GameObject("late"); point.transform.SetParent(_flow.CurrentRoom.Root.transform, false);
            FlowCheckpoint late = point.AddComponent<FlowCheckpoint>(); late.Id = "late"; late.Priority = 10;
            Assert.That(_flow.CommitCheckpoint(late), Is.True);
            Assert.That(_flow.CommitCheckpoint(entry), Is.False);
            entry.AllowReactivation = true;
            Assert.That(_flow.CommitCheckpoint(entry), Is.True);
            Assert.That(_flow.Checkpoints.Id, Is.EqualTo(entry.Id));
            yield return null;
        }
        [UnityTest]
        public IEnumerator DevicesAdvanceTheWholeRunIntoTheFinalRoomAndCompletion()
        {
            for (int i = 0; _flow.Chaos.Value < 100 && i < 200; i++)
            {
                EnterAndCommit(); yield return Ready();
                if (_flow.CurrentRoom.Variant.StabilizerGain == 0) continue;
                StabilityDevice device = _flow.CurrentRoom.Root.GetComponentInChildren<StabilityDevice>();
                device.ReceiveAttack(new AttackDamageRequest(100, device.transform.position, Vector3.forward, i + 1, 0));
                yield return Ready();
            }
            Assert.That(_flow.Chaos.Value, Is.EqualTo(100)); Assert.That(_flow.IsComplete, Is.False);
            Assert.That(_flow.NextRoom.Variant, Is.SameAs(_flow.Config.FinalRoom));
            EnterAndCommit();
            _motor.Teleport(_flow.CurrentRoom.Root.Exit.position);
            yield return new WaitForFixedUpdate(); yield return null;
            Assert.That(_flow.IsComplete, Is.True); Assert.That(GameManager.Instance.State, Is.EqualTo(GameState.End));
        }
        [UnityTest]
        public IEnumerator RestartClearsTheRunAndReleasesPreviousRoomInstances()
        {
            _flow.Chaos.Add(35, "test"); yield return Ready(); EnterAndCommit(); yield return Ready();
            long previousId = _flow.CurrentRoom.Id;
            Assert.That(_flow.RestartRun(), Is.True);
            yield return Ready();
            Assert.That(_flow.Chaos.Value, Is.Zero); Assert.That(_flow.CurrentRoom.Variant, Is.SameAs(_flow.Config.InitialRoom));
            Assert.That(_flow.CurrentRoom.Id, Is.GreaterThan(previousId));
            Assert.That(MapManager.Instance.FlowRooms.Count, Is.EqualTo(2));
            Assert.That(_flow.Planner.VisitCount(_flow.Config.InitialRoom), Is.EqualTo(1));
        }
    }
}
