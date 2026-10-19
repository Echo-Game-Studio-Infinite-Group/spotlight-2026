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

        [SetUp]
        public void SetUp()
        {
            _hadSave = PlayerPrefs.HasKey("playerSave"); _save = PlayerPrefs.GetString("playerSave");
            SaveManager.CancelContinue(); LevelSelection.Clear(); TimeManager.SetPaused(false);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_ownedMotor != null) _ownedMotor.ReleaseSimulation(this);
            if (RunFlowController.Current != null) yield return RunFlowController.Current.ReleaseRooms();
            SaveManager.CancelContinue(); LevelSelection.Clear(); TimeManager.SetPaused(false);
            if (_hadSave) PlayerPrefs.SetString("playerSave", _save); else PlayerPrefs.DeleteKey("playerSave");
            PlayerPrefs.Save();
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
        public IEnumerator ContinuePreservesLegacyTutorialSave()
        {
            var config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>("Assets/Settings/FormalFlow/RunFlow.asset");
            var legacy = config.ContinueOnlyRooms.Single();
            Assert.That(LevelSelection.GetLevels(config).Contains(legacy), Is.False);
            var data = new SaveManager.SaveData
            {
                scenePath = "Assets/Scenes/FormalFlowGame.unity", roomVariantId = legacy.Id,
                roomLocalPosition = legacy.Room.Template.GetComponentInChildren<FlowCheckpoint>().transform.localPosition,
                hasFlowState = true, checkpointId = "entry"
            };
            PlayerPrefs.SetString("playerSave", JsonUtility.ToJson(data));
            Assert.That(SaveManager.PrepareContinue(data.scenePath, out _), Is.True);
            yield return Load();
            Assert.That(_flow.CurrentRoom.Variant, Is.SameAs(legacy));
            Assert.That(_flow.Checkpoints.Id, Is.EqualTo("entry"));
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
