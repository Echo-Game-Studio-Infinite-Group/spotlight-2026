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
        private RoomVariantDefinition _tutorialVariant;

        [SetUp]
        public void SetUp()
        {
            _hadSave = PlayerPrefs.HasKey("playerSave"); _save = PlayerPrefs.GetString("playerSave");
            SaveManager.CancelContinue(); LevelSelection.Clear(); TimeManager.SetPaused(false);
            // 流程测试隔离策划正在编辑的教学敌人点，避免敌人攻击或未烘焙点位影响过门与恢复验证。
            _tutorialVariant = AssetDatabase.LoadAssetAtPath<RunFlowConfig>("Assets/Settings/FormalFlow/RunFlow.asset").InitialRoom;
            _tutorialSpawns = _tutorialVariant.EnemySpawns;
            _tutorialVariant.EnemySpawns = new FlowEnemySpawnBinding[0];
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_ownedMotor != null) _ownedMotor.ReleaseSimulation(this);
            if (RunFlowController.Current != null) yield return RunFlowController.Current.ReleaseRooms();
            SaveManager.CancelContinue(); LevelSelection.Clear(); TimeManager.SetPaused(false);
            if (_hadSave) PlayerPrefs.SetString("playerSave", _save); else PlayerPrefs.DeleteKey("playerSave");
            PlayerPrefs.Save();
            _tutorialVariant.EnemySpawns = _tutorialSpawns;
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
        public IEnumerator NewGamesAndFullRestartsChooseNewSeeds()
        {
            yield return Load();
            Assert.That(_flow.Config.RandomizeSeed, Is.True);
            int first = _flow.RunSeed;
            yield return _flow.ReleaseRooms();
            yield return Load();
            Assert.That(_flow.RunSeed, Is.Not.EqualTo(first));
            int second = _flow.RunSeed;
            Assert.That(_flow.RestartRun(), Is.True);
            yield return Ready();
            Assert.That(_flow.RunSeed, Is.Not.EqualTo(second));
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
            int seed = _flow.RunSeed;
            SaveManager.Init(_ownedMotor.gameObject); SaveManager.SavePlayer();
            Assert.That(SaveManager.PrepareContinue("Assets/Scenes/FormalFlowGame.unity", out _), Is.True);
            Assert.That(SaveManager.ContinueRunSeed, Is.EqualTo(seed));
            yield return Load();
            Assert.That(_flow.RunSeed, Is.EqualTo(seed));
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
        public void FormalConfigContainsElevenBoxesAndSeparateFinalVariant()
        {
            var config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>("Assets/Settings/FormalFlow/RunFlow.asset");
            Assert.That(FlowConfigValidator.Validate(config), Is.Empty);
            var ordinary = config.NormalPools.Single(p => !p.ChangesChaos).Entries.Select(e => e.Variant).ToArray();
            var chaos = config.NormalPools.Single(p => p.ChangesChaos).Entries.Select(e => e.Variant).ToArray();
            Assert.That(ordinary.Select(v => v.Room.DisplayName).Distinct(), Is.EquivalentTo(new[]
            {
                "BOX_S_001", "BOX_S_002", "BOX_S_004", "BOX_S_005", "BOX_S_006", "BOX_S_007",
                "CDWY_BOX_S01_2", "CDWY_BOX_S02_2", "CDWY_BOX_S03_2", "CDWY_BOX_S04_2"
            }));
            Assert.That(chaos.Select(v => v.Room.DisplayName).Distinct(), Is.EqualTo(new[] { "BOX_S_003" }));
            Assert.That(ordinary.Contains(config.FinalRoom), Is.False, "普通池抽到 007 不能提前终局");
            Assert.That(config.FinalRoom.Room.Template, Is.SameAs(ordinary.First(v => v.Room.DisplayName == "BOX_S_007").Room.Template));
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
            var boxes = config.NormalPools.SelectMany(p => p.Entries).Select(e => e.Variant.Room).Distinct()
                .Select(room => room.ResolveVariant(0, null)).OrderBy(v => v.Id).ToArray();
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

        [Test]
        public void FormalChaosVariantsCoverBoundariesAndAppearInBothPools()
        {
            var config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>("Assets/Settings/FormalFlow/RunFlow.asset");
            var rooms = config.NormalPools.SelectMany(pool => pool.Entries).Select(entry => entry.Variant.Room).Distinct().ToArray();
            Assert.That(rooms.Length, Is.EqualTo(11));
            Assert.That(config.NormalPools.Single(pool => !pool.ChangesChaos).Entries.Length, Is.EqualTo(30));
            Assert.That(config.NormalPools.Single(pool => pool.ChangesChaos).Entries.Length, Is.EqualTo(3));
            foreach (var room in rooms)
            {
                Assert.That(room.Variants.Length, Is.EqualTo(3), room.Id);
                Assert.That(room.ResolveVariant(0, null), Is.SameAs(room.ResolveVariant(30, null)));
                Assert.That(room.ResolveVariant(31, null), Is.SameAs(room.ResolveVariant(79, null)));
                Assert.That(room.ResolveVariant(80, null), Is.SameAs(room.ResolveVariant(100, null)));
                Assert.That(room.ResolveVariant(0, null).Id, Is.EqualTo(room.Id + ".default"), "保留旧存档编号");
                Assert.That(room.Variants.Select(v => v.LightColor).Distinct().Count(), Is.EqualTo(3));
                Assert.That(room.Variants.Select(v => v.Narrative).Distinct().Count(), Is.EqualTo(3));
                Assert.That(room.Variants.Select(v => v.StabilizerGain), room.Id == "box_s_003" ? Is.EqualTo(new[] { 10, 15, 10 }) : Is.EqualTo(new[] { 0, 0, 0 }));
                for (int chaos = 0; chaos <= 100; chaos++)
                    Assert.That(room.Variants.Count(v => v.Supports(chaos)), Is.EqualTo(1), room.Id + " / " + chaos);
            }
            Assert.That(config.InitialRoom.Room.Variants.Length, Is.EqualTo(1));
            Assert.That(config.FinalRoom.Room.Variants.Length, Is.EqualTo(1));
            Assert.That(new RoomSelectionPlanner(config, config.Seed).Plan(100), Is.SameAs(config.FinalRoom));
            Assert.That(FlowConfigValidator.Validate(config), Is.Empty);
        }

        [Test]
        public void EveryFormalBoxAppliesAllThreeVisualVariantsWithoutShowingPreloadedNarrative()
        {
            var config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>("Assets/Settings/FormalFlow/RunFlow.asset");
            var rooms = config.NormalPools.SelectMany(pool => pool.Entries).Select(entry => entry.Variant.Room).Distinct();
            foreach (var room in rooms)
            {
                GameObject copy = Object.Instantiate(room.Template.gameObject);
                try
                {
                    var template = copy.GetComponent<FlowRoomTemplate>();
                    var controller = new RoomVariantController(template);
                    var text = template.Narratives.Single();
                    Assert.That(text.font, Is.EqualTo(AssetDatabase.LoadAssetAtPath<Font>("Assets/Art/UI/Fonts/NanoDotSong.ttf")));
                    var lights = copy.GetComponentsInChildren<Light>(true);
                    Assert.That(lights.Length, Is.GreaterThan(0));
                    foreach (var variant in room.Variants)
                    {
                        controller.Apply(variant, false);
                        Assert.That(text.gameObject.activeInHierarchy, Is.False, "预加载不能提前显示叙事");
                        controller.SetCurrent(true);
                        Assert.That(text.gameObject.activeInHierarchy, Is.True);
                        Assert.That(text.text, Is.EqualTo(variant.Narrative));
                        foreach (var light in lights) Assert.That(light.color, Is.EqualTo(variant.LightColor));
                    }
                }
                finally { Object.DestroyImmediate(copy); }
            }
        }

        [Test]
        public void CdwyRoomsKeepArtPrefabReferencesAndNormalizeDoorDirection()
        {
            string[] sources = { "BOX_S01_2", "BOX_S02_2", "BOX_S03 _2", "BOX_S04 _2" };
            for (int i = 0; i < sources.Length; i++)
            {
                string name = "CDWY_BOX_S" + (i + 1).ToString("00") + "_2";
                var room = AssetDatabase.LoadAssetAtPath<RoomDefinition>("Assets/Settings/FormalFlow/" + name + ".asset");
                Assert.That(room, Is.Not.Null, name);
                var template = room.Template;
                var geometry = template.transform.Cast<Transform>().Select(t => PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject))
                    .FirstOrDefault(source => source != null && AssetDatabase.GetAssetPath(source) == "Assets/Scenes/LevelTest_Cdwy 1/Prefabs/" + sources[i] + ".prefab");
                Assert.That(geometry, Is.Not.Null, "必须嵌套资产版预制件，不能导入同名白盒或复制丢失资产引用");
                Assert.That(template.GetComponentsInChildren<Renderer>(true).Length, Is.GreaterThan(50));
                Assert.That(template.Entry.parent, Is.SameAs(template.transform));
                Assert.That(template.Exit.parent, Is.SameAs(template.transform));
                Assert.That(template.Entry.localPosition, Is.EqualTo(Vector3.zero));
                Assert.That(Vector3.Dot(template.Entry.forward, template.transform.forward), Is.GreaterThan(.999f));
                Assert.That(Vector3.Dot(template.Exit.forward, template.transform.forward), Is.GreaterThan(.999f));
                Assert.That(template.Exit.localPosition.z, Is.GreaterThan(100));
                Assert.That(template.GetComponentsInChildren<MonoBehaviour>(true).Any(c => c != null && c.GetType().FullName == "UnityEngine.ProBuilder.ProBuilderMesh"), Is.False);
                var entry = template.GetComponentInChildren<FlowCheckpoint>();
                Assert.That(entry.Id, Is.EqualTo("entry"));
                Assert.That(template.FallHeight, Is.LessThan(Mathf.Min(entry.transform.localPosition.y, template.Exit.localPosition.y) - 5));
            }
        }

        [UnityTest]
        public IEnumerator OldDefaultSaveSelectsChaosVariantAndDeathKeepsCheckpointAndBrokenLamp()
        {
            var room = AssetDatabase.LoadAssetAtPath<RoomDefinition>("Assets/Settings/FormalFlow/BOX_S_003.asset");
            var initial = room.Template.GetComponentInChildren<FlowCheckpoint>();
            foreach (int savedChaos in new[] { 31, 80 })
            {
                var data = new SaveManager.SaveData
                {
                    scenePath = "Assets/Scenes/FormalFlowGame.unity", roomVariantId = "box_s_003.default",
                    roomLocalPosition = initial.transform.localPosition, checkpointId = initial.Id,
                    hasFlowState = true, chaos = savedChaos, brokenDeviceIds = new[] { "lamp" }
                };
                PlayerPrefs.SetString("playerSave", JsonUtility.ToJson(data));
                Assert.That(SaveManager.PrepareContinue(data.scenePath, out _), Is.True);
                yield return Load();
                Assert.That(_flow.CurrentRoom.Variant, Is.SameAs(room.ResolveVariant(savedChaos, null)));
                Assert.That(_flow.Chaos.Value, Is.EqualTo(savedChaos));
                Assert.That(_flow.Checkpoints.Id, Is.EqualTo(initial.Id));
                Assert.That(Vector3.Distance(Player.Current.Motor.transform.position, _flow.CurrentRoom.Root.FindCheckpoint(initial.Id).transform.position), Is.LessThan(.25f));
                Assert.That(_flow.CurrentRoom.Root.Narratives.Single().text, Is.EqualTo(_flow.CurrentRoom.Variant.Narrative));
                Assert.That(_flow.Recover(true), Is.True);
                yield return Ready();
                Assert.That(_flow.Chaos.Value, Is.EqualTo(savedChaos - _flow.Config.DeathPenalty));
                Assert.That(_flow.CurrentRoom.Variant, Is.SameAs(room.ResolveVariant(_flow.Chaos.Value, null)));
                Assert.That(_flow.Checkpoints.Id, Is.EqualTo(initial.Id));
                Assert.That(_flow.CurrentRoom.Root.GetComponentInChildren<StabilityDevice>(true).IsBroken, Is.True);
                Assert.That(_flow.TryBreakDevice(_flow.CurrentRoom, "lamp"), Is.False, "换差分不能重新结算同一次访问的稳定装置");
                Assert.That(_flow.Chaos.Value, Is.EqualTo(savedChaos - _flow.Config.DeathPenalty));
                Assert.That(_flow.CurrentRoom.Root.Narratives.Single().text, Is.EqualTo(_flow.CurrentRoom.Variant.Narrative));
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
