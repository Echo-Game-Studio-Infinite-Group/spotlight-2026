#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using GameJam.Actions;
using GameJam.Flow;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace GameJam.FlowTests
{
    public sealed class FormalFlowTests
    {
        private RunFlowController _flow;
        private PlayerMotor _ownedMotor;
        private bool _hadSave;
        private string _save;
        private FlowEnemySpawnBinding[] _tutorialSpawns;
        private bool[] _tutorialSpawnEnabled;

        [SetUp]
        public void SetUp()
        {
            _hadSave = PlayerPrefs.HasKey("playerSave"); _save = PlayerPrefs.GetString("playerSave");
            SaveManager.CancelContinue(); LevelSelection.Clear(); TimeManager.SetPaused(false);
            // 流程测试隔离策划正在编辑的教学敌人点，避免敌人攻击或未烘焙点位影响过门与恢复验证。
            _tutorialSpawns = AssetDatabase.LoadAssetAtPath<RunFlowConfig>("Assets/Settings/FormalFlow/RunFlow.asset").InitialRoom.EnemySpawns;
            _tutorialSpawnEnabled = _tutorialSpawns.Select(spawn => spawn.Enabled).ToArray();
            foreach (var spawn in _tutorialSpawns) spawn.Enabled = false;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_ownedMotor != null) _ownedMotor.ReleaseSimulation(this);
            if (RunFlowController.Current != null) yield return RunFlowController.Current.ReleaseRooms();
            SaveManager.CancelContinue(); LevelSelection.Clear(); TimeManager.SetPaused(false);
            if (_hadSave) PlayerPrefs.SetString("playerSave", _save); else PlayerPrefs.DeleteKey("playerSave");
            PlayerPrefs.Save();
            for (int i = 0; i < _tutorialSpawns.Length; i++) _tutorialSpawns[i].Enabled = _tutorialSpawnEnabled[i];
        }

        private IEnumerator Load()
        {
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/FormalFlowGame.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return Ready();
        }

        private IEnumerator Ready()
        {
            float deadline = Time.realtimeSinceStartup + 30;
            do
            {
                yield return null; _flow = RunFlowController.Current;
                Assert.Less(Time.realtimeSinceStartup, deadline, _flow != null ? _flow.LastError : "没有流程");
            } while (_flow == null || !_flow.IsReady || _flow.IsBusy || _flow.IsPreparing);
        }

        [UnityTest]
        public IEnumerator MainMenuNewGameLoadsFormalSceneAndCancelsContinue()
        {
            var data = new SaveManager.SaveData { scenePath = "Assets/Scenes/FormalFlowGame.unity", roomVariantId = "room01.low" };
            PlayerPrefs.SetString("playerSave", JsonUtility.ToJson(data));
            Assert.That(SaveManager.PrepareContinue(data.scenePath, out _), Is.True);
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/MainMenu.unity", new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            var menu = Object.FindObjectOfType<MainMenuUI>();
            var start = (Button)typeof(MainMenuUI).GetField("_startButton", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(menu);
            start.onClick.Invoke();
            yield return Ready();
            Assert.That(_flow.gameObject.scene.path, Is.EqualTo("Assets/Scenes/FormalFlowGame.unity"));
            Assert.That(SceneUtility.GetBuildIndexByScenePath(_flow.gameObject.scene.path), Is.GreaterThanOrEqualTo(0));
            Assert.That(_flow.CurrentRoom.Variant.Id, Is.EqualTo("boxtr.default"));
            Assert.That(SaveManager.HasPendingContinue, Is.False);
        }

        [UnityTest]
        public IEnumerator TeachingExitCrossesToSuccessorAndContinueRestoresSuccessor()
        {
            yield return Load();
            var source = _flow.CurrentRoom; var target = _flow.NextRoom;
            _ownedMotor = Player.Current.Motor;
            _ownedMotor.GetComponent<PlayerActionRunner>().enabled = false;
            Assert.That(_ownedMotor.TryAcquireSimulation(this), Is.True);
            Vector3 forward = source.Root.Exit.forward;
            _ownedMotor.Teleport(source.Root.Exit.position - forward * 2 + Vector3.up * .15f);
            var controller = _ownedMotor.GetComponent<CharacterController>();
            int steps = Mathf.CeilToInt(Vector3.Distance(source.Root.Exit.position, target.Root.Entry.position) / 2) + 20;
            for (int i = 0; i < steps && _flow.CurrentRoom == source; i++)
            {
                controller.Move(forward * 2); yield return new WaitForFixedUpdate();
            }
            Assert.That(_flow.CurrentRoom, Is.SameAs(target), "教学出口无法正常通过");
            var checkpoint = target.Root.Checkpoints[0];
            _ownedMotor.Teleport(checkpoint.transform.position);
            Assert.That(_flow.CommitCheckpoint(checkpoint), Is.True);
            yield return Ready();
            string id = target.Variant.Id;
            SaveManager.Init(_ownedMotor.gameObject); SaveManager.SavePlayer();
            Assert.That(SaveManager.PrepareContinue("Assets/Scenes/FormalFlowGame.unity", out _), Is.True);
            yield return Load();
            Assert.That(_flow.CurrentRoom.Variant.Id, Is.EqualTo(id));
            Assert.That(_flow.CurrentRoom.Variant.Id, Is.Not.EqualTo("boxtr.default"));
            Assert.That(_flow.Checkpoints.Id, Is.EqualTo(checkpoint.Id));
        }

        [UnityTest]
        public IEnumerator NewRunIgnoresLevelSelectionAndBoxTrHasSafeCheckpoints()
        {
            var config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>("Assets/Settings/FormalFlow/RunFlow.asset");
            Assert.That(FlowConfigValidator.Validate(config), Is.Empty);
            Assert.That(LevelSelection.Select(config, config.FinalRoom), Is.True);
            yield return Load();
            Assert.That(_flow.Config, Is.SameAs(config));
            Assert.That(_flow.CurrentRoom.Variant.Id, Is.EqualTo("boxtr.default"));
            Assert.That(_flow.NextRoom, Is.Not.Null, _flow.LastError);
            var root = _flow.CurrentRoom.Root;
            foreach (FlowCheckpoint checkpoint in root.Checkpoints)
            {
                Vector3 position = checkpoint.transform.position;
                Assert.That(Physics.Raycast(position + Vector3.up * .5f, Vector3.down, 1), Is.True, checkpoint.Id + " 脚下无地板");
                var blockers = Physics.OverlapCapsule(position + Vector3.up * .4f, position + Vector3.up * 1.5f, .3f, ~0, QueryTriggerInteraction.Ignore)
                    .Where(c => c.transform.IsChildOf(root.transform)).Select(c => c.name).ToArray();
                Assert.That(blockers, Is.Empty, checkpoint.Id + " 重生点与地图相交");
            }
            Assert.That(NavMesh.SamplePosition(root.Checkpoints[0].transform.position, out _, 1, NavMesh.AllAreas), Is.True);
            Assert.That(Object.FindObjectsOfType<PlayerMotor>().Length, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator RemovedExampleSaveFallsBackToTeachingRoom()
        {
            var config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>("Assets/Settings/FormalFlow/RunFlow.asset");
            Assert.That(config.ContinueOnlyRooms, Is.Empty);
            var data = new SaveManager.SaveData
            {
                scenePath = "Assets/Scenes/FormalFlowGame.unity", roomVariantId = "tutorial.default",
                hasFlowState = true, checkpointId = "entry"
            };
            PlayerPrefs.SetString("playerSave", JsonUtility.ToJson(data));
            Assert.That(SaveManager.PrepareContinue(data.scenePath, out _), Is.True);
            yield return Load();
            Assert.That(_flow.CurrentRoom.Variant, Is.SameAs(config.InitialRoom));
            Assert.That(SaveManager.HasPendingContinue, Is.False);
        }

        [Test]
        public void FormalConfigContainsSevenBoxesAndSeparateFinalVariant()
        {
            var config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>("Assets/Settings/FormalFlow/RunFlow.asset");
            Assert.That(FlowConfigValidator.Validate(config), Is.Empty);
            var ordinary = config.NormalPools.Single(p => !p.ChangesChaos).Entries.Select(e => e.Variant).ToArray();
            var chaos = config.NormalPools.Single(p => p.ChangesChaos).Entries.Select(e => e.Variant).ToArray();
            Assert.That(ordinary.Select(v => v.Room.DisplayName), Is.EquivalentTo(new[] { "BOX_S_001", "BOX_S_002", "BOX_S_004", "BOX_S_005", "BOX_S_006", "BOX_S_007" }));
            Assert.That(chaos.Select(v => v.Room.DisplayName), Is.EqualTo(new[] { "BOX_S_003" }));
            Assert.That(ordinary.Contains(config.FinalRoom), Is.False, "普通池抽到 007 不能提前终局");
            Assert.That(config.FinalRoom.Room.Template, Is.SameAs(ordinary.Single(v => v.Room.DisplayName == "BOX_S_007").Room.Template));
            foreach (var variant in LevelSelection.GetLevels(config))
            {
                Assert.That(AssetDatabase.GetAssetPath(variant), Does.StartWith("Assets/Settings/FormalFlow/"));
                Assert.That(AssetDatabase.GetAssetPath(variant.Room.Template), Does.StartWith("Assets/Settings/FormalFlow/Templates/"));
            }
        }

        [UnityTest]
        public IEnumerator EveryFormalBoxHasSafeSpawnRecoveryAndTraversableConnection()
        {
            var config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>("Assets/Settings/FormalFlow/RunFlow.asset");
            var boxes = config.NormalPools.SelectMany(p => p.Entries).Select(e => e.Variant).OrderBy(v => v.Id).ToArray();
            foreach (var variant in boxes)
            {
                var initial = variant.Room.Template.GetComponentInChildren<FlowCheckpoint>();
                var data = new SaveManager.SaveData
                {
                    scenePath = "Assets/Scenes/FormalFlowGame.unity", roomVariantId = variant.Id,
                    roomLocalPosition = initial.transform.localPosition, hasFlowState = true, checkpointId = initial.Id
                };
                PlayerPrefs.SetString("playerSave", JsonUtility.ToJson(data));
                Assert.That(SaveManager.PrepareContinue(data.scenePath, out _), Is.True);
                yield return Load();
                Assert.That(_flow.CurrentRoom.Variant, Is.SameAs(variant));
                var source = _flow.CurrentRoom;
                var checkpoint = source.Root.FindCheckpoint("entry");
                Vector3 position = checkpoint.transform.position;
                Assert.That(Physics.Raycast(position + Vector3.up * .5f, Vector3.down, out var ground, 1, ~0, QueryTriggerInteraction.Ignore), Is.True, variant.Id + " 出生点悬空");
                Assert.That(ground.collider.transform.IsChildOf(source.Root.transform), Is.True);
                var blockers = Physics.OverlapCapsule(position + Vector3.up * .4f, position + Vector3.up * 1.5f, .3f, ~0, QueryTriggerInteraction.Ignore)
                    .Where(c => c.transform.IsChildOf(source.Root.transform)).Select(c => c.name).ToArray();
                Assert.That(blockers, Is.Empty, variant.Id + " 出生点卡墙");
                Assert.That(NavMesh.SamplePosition(position, out var nav, 1, NavMesh.AllAreas), Is.True, variant.Id + " 出生点没有导航");
                Assert.That(Vector2.Distance(new Vector2(nav.position.x, nav.position.z), new Vector2(position.x, position.z)), Is.LessThan(.5f));
                if (variant.StabilizerGain > 0)
                {
                    var device = source.Root.GetComponentInChildren<StabilityDevice>(true);
                    int before = _flow.Chaos.Value;
                    Assert.That(_flow.TryBreakDevice(source, device.Id), Is.True);
                    Assert.That(_flow.Chaos.Value, Is.EqualTo(before + variant.StabilizerGain));
                    Assert.That(_flow.TryBreakDevice(source, device.Id), Is.False);
                    yield return Ready();
                }

                _ownedMotor = Player.Current.Motor;
                _ownedMotor.GetComponent<PlayerActionRunner>().enabled = false;
                Assert.That(_ownedMotor.TryAcquireSimulation(this), Is.True);
                _ownedMotor.Teleport(position + Vector3.down * 50);
                yield return new WaitForFixedUpdate();
                yield return Ready();
                Assert.That(Vector3.Distance(_ownedMotor.transform.position, position), Is.LessThan(.25f), variant.Id + " 掉落未恢复出生点");
                var target = _flow.NextRoom;
                Assert.That(target, Is.Not.Null, _flow.LastError);
                Vector3 forward = source.Root.Exit.forward;
                _ownedMotor.Teleport(source.Root.Exit.position - forward * 2 + Vector3.up * .15f);
                var controller = _ownedMotor.GetComponent<CharacterController>();
                int steps = Mathf.CeilToInt(Vector3.Distance(source.Root.Exit.position, target.Root.Entry.position) / 2) + 20;
                for (int i = 0; i < steps && _flow.CurrentRoom == source; i++)
                {
                    controller.Move(forward * 2); yield return new WaitForFixedUpdate();
                }
                Assert.That(_flow.CurrentRoom, Is.SameAs(target), variant.Id + " 出口或后继入口无法通过");
                _ownedMotor.ReleaseSimulation(this); _ownedMotor = null;
                yield return Ready();
                Debug.Log("[正式箱庭验证] " + variant.Id + "：出生、导航、掉落恢复、过门成功");
            }
        }

        [UnityTest]
        public IEnumerator FinalBoxSevenCompletesOnlyAtExit()
        {
            var config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>("Assets/Settings/FormalFlow/RunFlow.asset");
            var checkpoint = config.FinalRoom.Room.Template.GetComponentInChildren<FlowCheckpoint>();
            var data = new SaveManager.SaveData
            {
                scenePath = "Assets/Scenes/FormalFlowGame.unity", roomVariantId = config.FinalRoom.Id,
                roomLocalPosition = checkpoint.transform.localPosition, hasFlowState = true, checkpointId = checkpoint.Id, chaos = config.MaximumChaos
            };
            PlayerPrefs.SetString("playerSave", JsonUtility.ToJson(data));
            Assert.That(SaveManager.PrepareContinue(data.scenePath, out _), Is.True);
            yield return Load();
            Assert.That(_flow.IsComplete, Is.False);
            Assert.That(_flow.NextRoom, Is.Null);
            Player.Current.Motor.Teleport(_flow.CurrentRoom.Root.Exit.position - _flow.CurrentRoom.Root.Exit.forward + Vector3.up * .15f);
            yield return new WaitForFixedUpdate();
            Assert.That(_flow.IsComplete, Is.True);
        }

        [UnityTest]
        public IEnumerator ContinueRestoresLaterBoxTrCheckpointInsteadOfStartingOver()
        {
            yield return Load();
            var checkpoint = _flow.CurrentRoom.Root.Checkpoints.Single(p => p.Id == "north");
            var player = Player.Current;
            Assert.That(_flow.CommitCheckpoint(checkpoint), Is.True);
            player.Motor.Teleport(checkpoint.transform.position);
            SaveManager.Init(player.gameObject); SaveManager.SavePlayer();
            Assert.That(SaveManager.PrepareContinue("Assets/Scenes/FormalFlowGame.unity", out _), Is.True);
            yield return Load();
            Assert.That(_flow.CurrentRoom.Variant.Id, Is.EqualTo("boxtr.default"));
            Assert.That(_flow.Checkpoints.Id, Is.EqualTo("north"));
            var restored = _flow.CurrentRoom.Root.FindCheckpoint("north");
            Assert.That(Vector3.Distance(Player.Current.Motor.transform.position, restored.transform.position), Is.LessThan(.2f));
            Assert.That(SaveManager.HasPendingContinue, Is.False);
        }
    }
}
#endif
