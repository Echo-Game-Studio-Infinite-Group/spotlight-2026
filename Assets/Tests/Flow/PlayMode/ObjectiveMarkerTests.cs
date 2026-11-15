#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using System.Reflection;
using GameJam.Flow;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace GameJam.FlowTests
{
    public sealed class ObjectiveMarkerTests
    {
        private bool _hadSave;
        private string _save;
        private RoomVariantDefinition _variant;
        private FlowEnemySpawnBinding[] _spawns;
        private ObjectiveMarker _marker;

        [SetUp]
        public void SetUp()
        {
            _hadSave = PlayerPrefs.HasKey("playerSave");
            _save = PlayerPrefs.GetString("playerSave");
            SaveManager.CancelContinue(); LevelSelection.Clear(); TimeManager.SetPaused(false);
            _variant = AssetDatabase.LoadAssetAtPath<RoomVariantDefinition>("Assets/Settings/FormalFlow/BOX_S_001.default.asset");
            _spawns = _variant.EnemySpawns;
            _variant.EnemySpawns = new FlowEnemySpawnBinding[0];
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_marker != null) Object.Destroy(_marker.gameObject);
            TimeManager.SetPaused(false);
            if (RunFlowController.Current != null) yield return RunFlowController.Current.ReleaseRooms();
            SaveManager.CancelContinue(); LevelSelection.Clear();
            if (_hadSave) PlayerPrefs.SetString("playerSave", _save); else PlayerPrefs.DeleteKey("playerSave");
            PlayerPrefs.Save();
            _variant.EnemySpawns = _spawns;
            yield return null;
        }

        [UnityTest]
        public IEnumerator BoxS001MarkerWorksWithNewHudAndCleansUpItsVisual()
        {
            var checkpoint = _variant.Room.Template.GetComponentInChildren<FlowCheckpoint>();
            var data = new SaveManager.SaveData
            {
                scenePath = "Assets/Scenes/FormalFlowGame.unity", roomVariantId = _variant.Id,
                roomLocalPosition = checkpoint.transform.localPosition, checkpointId = checkpoint.Id,
                hasFlowState = true, chaos = _variant.MinimumChaos
            };
            PlayerPrefs.SetString("playerSave", JsonUtility.ToJson(data));
            Assert.That(SaveManager.PrepareContinue(data.scenePath, out _), Is.True);
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(data.scenePath, new LoadSceneParameters(LoadSceneMode.Single));
            float deadline = Time.realtimeSinceStartup + 30;
            while (RunFlowController.Current == null || !RunFlowController.Current.IsReady || RunFlowController.Current.IsBusy)
            {
                Assert.Less(Time.realtimeSinceStartup, deadline, "BOX_S_001 加载超时");
                yield return null;
            }
            var flow = RunFlowController.Current;
            Assert.That(flow.CurrentRoom.Variant.Id, Is.EqualTo(_variant.Id));
            var hud = Object.FindObjectOfType<PlayerHudView>();
            Assert.That(hud, Is.Not.Null);
            Assert.That(Object.FindObjectOfType<HealthBar>(), Is.Null, "测试需覆盖没有旧 HealthBar 的新版 HUD");
            TimeManager.SetPaused(true);
            yield return null;
            var camera = Camera.main;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/ObjectiveMarker.prefab");
            var point = Object.Instantiate(prefab, flow.CurrentRoom.Root.transform);
            point.transform.position = camera.transform.position + camera.transform.forward * 12 - Vector3.up;
            _marker = point.GetComponent<ObjectiveMarker>();
            yield return null;
            yield return null;
            var visual = (RectTransform)typeof(ObjectiveMarker).GetField("_visual", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_marker);
            Assert.That(_marker.enabled, Is.True);
            Assert.That(visual.GetComponentInParent<Canvas>(), Is.SameAs(hud.GetComponentInParent<Canvas>()));
            Assert.That(visual.gameObject.activeInHierarchy, Is.True);
            Assert.That(visual.GetComponentInChildren<Image>().sprite, Is.Not.Null);
            string meters = Mathf.RoundToInt(Vector3.Distance(Player.Current.Motor.transform.position, point.transform.position)) + " m";
            Assert.That(visual.GetComponentInChildren<Text>().text, Is.EqualTo(meters));
            Vector3 expected = camera.WorldToScreenPoint(point.transform.position + Vector3.up);
            Vector2 actual = RectTransformUtility.WorldToScreenPoint(null, visual.position);
            Assert.That(Vector2.Distance(actual, expected), Is.LessThan(2f));

            point.transform.position = camera.transform.position - camera.transform.forward * 12 - Vector3.up;
            yield return null;
            Assert.That(visual.gameObject.activeSelf, Is.False);
            point.transform.position = camera.transform.position + camera.transform.forward * 12 - Vector3.up;
            yield return null;
            Assert.That(visual.gameObject.activeInHierarchy, Is.True);
            _marker.SetVisible(false);
            Assert.That(visual.gameObject.activeSelf, Is.False);
            _marker.SetVisible(true);
            yield return null;
            Assert.That(visual.gameObject.activeInHierarchy, Is.True);
            Object.Destroy(point);
            yield return null;
            Assert.That(visual == null, Is.True, "移到 HUD 的图标应随任务点销毁");
        }
    }
}
#endif
