#if UNITY_EDITOR
using System;
using System.Collections;
using System.Reflection;
using GameJam.Flow;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace GameJam.FlowTests
{
    public sealed class EndingSceneTests
    {
        private bool _hadSave;
        private string _save;

        [SetUp]
        public void SetUp()
        {
            _hadSave = PlayerPrefs.HasKey("playerSave");
            _save = PlayerPrefs.GetString("playerSave");
            SaveManager.CancelContinue();
            LevelSelection.Clear();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (RunFlowController.Current != null) yield return RunFlowController.Current.ReleaseRooms();
            SaveManager.CancelContinue(); LevelSelection.Clear(); TimeManager.SetPaused(false);
            Time.timeScale = 1;
            if (_hadSave) PlayerPrefs.SetString("playerSave", _save); else PlayerPrefs.DeleteKey("playerSave");
            PlayerPrefs.Save();
        }

        [UnityTest]
        public IEnumerator ManualPagesUseUnscaledTimeAndPreventAccidentalSkipping()
        {
            yield return SceneManager.LoadSceneAsync("Assets/Scenes/Ending.unity");
            var ending = Object.FindObjectOfType<EndingUI>();
            Assert.That(ending, Is.Not.Null);
            ending.enabled = false;
            Set(ending, "_pages", new[] { "第一页测试", "第二页测试" });
            Set(ending, "_fadeDuration", .03f);
            Set(ending, "_minimumReadTime", .2f);
            Set(ending, "_pageMaterials", null);
            Time.timeScale = 0;
            ending.enabled = true;
            var text = Get<TMP_Text>(ending, "_storyText");
            yield return new WaitForSecondsRealtime(.08f);
            Assert.That(text.text, Is.EqualTo("第一页测试"));
            ending.Advance();
            Assert.That(text.text, Is.EqualTo("第一页测试"));
            yield return new WaitForSecondsRealtime(.25f);
            ending.Advance(); ending.Advance();
            yield return new WaitForSecondsRealtime(.12f);
            Assert.That(text.text, Is.EqualTo("第二页测试"));
            Assert.That(Get<CanvasGroup>(ending, "_textOpacity").alpha, Is.EqualTo(1).Within(.001));
            Assert.That(text.color, Is.EqualTo(Color.white));
            Assert.That(text.fontSharedMaterial.IsKeywordEnabled("GLOW_ON"), Is.False);
            Assert.That(AudioDebugOverlay.Visible, Is.False);
            Assert.That(GameObject.Find("Black Background").GetComponent<Image>().color, Is.EqualTo(Color.black));
            Assert.That(Object.FindObjectsOfType<PlayerMotor>(), Is.Empty);
            text.ForceMeshUpdate();
            Assert.That(text.isTextOverflowing, Is.False);
        }

        [UnityTest]
        public IEnumerator EndingOpensExistingThanksAndCreditsReturnToMenu()
        {
            yield return SceneManager.LoadSceneAsync("Assets/Scenes/Ending.unity");
            var ending = Object.FindObjectOfType<EndingUI>();
            Set(ending, "_fadeDuration", .02f);
            Set(ending, "_minimumReadTime", 0f);
            Set(ending, "_autoAdvanceSeconds", .05f);
            yield return Until(() => SceneManager.GetActiveScene().name == "MainMenu" && Object.FindObjectOfType<ThanksUI>() != null);
            var menu = Object.FindObjectOfType<MainMenuUI>();
            var thanks = Get<ThanksUI>(menu, "_thanksPanel");
            Assert.That(thanks.gameObject.name, Is.EqualTo("Thanks"));
            Assert.That(Get<GameObject>(menu, "_mainPanel").activeSelf, Is.False);
            Assert.That(Object.FindObjectsOfType<ThanksUI>(), Has.Length.EqualTo(1));
            int sceneHandle = menu.gameObject.scene.handle;
            thanks.ScrollSpeed = 100000;
            thanks.FadeDuration = .02f;
            thanks.LineHoldDuration = 0;
            Set(thanks, "_entranceDelay", 0f);
            Set(thanks, "_returnDelay", 0f);
            yield return Until(() => !thanks.gameObject.activeSelf);
            Assert.That(Get<GameObject>(menu, "_mainPanel").activeSelf, Is.True);
            Assert.That(menu.gameObject.scene.handle, Is.EqualTo(sceneHandle));
            // 请求已消费，正常重开主菜单仍应显示菜单。
            yield return SceneManager.LoadSceneAsync("Assets/Scenes/MainMenu.unity");
            yield return null;
            menu = Object.FindObjectOfType<MainMenuUI>();
            Assert.That(Get<GameObject>(menu, "_mainPanel").activeSelf, Is.True);
            Assert.That(Get<ThanksUI>(menu, "_thanksPanel").gameObject.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator FormalFinalExitLoadsEndingAndReleasesRooms()
        {
            var config = AssetDatabase.LoadAssetAtPath<RunFlowConfig>("Assets/Settings/FormalFlow/RunFlow.asset");
            var checkpoint = config.FinalRoom.Room.Template.GetComponentInChildren<FlowCheckpoint>();
            var save = new SaveManager.SaveData
            {
                scenePath = "Assets/Scenes/FormalFlowGame.unity", roomVariantId = config.FinalRoom.Id,
                roomLocalPosition = checkpoint.transform.localPosition, hasFlowState = true,
                checkpointId = checkpoint.Id, chaos = config.MaximumChaos
            };
            PlayerPrefs.SetString("playerSave", JsonUtility.ToJson(save));
            Assert.That(SaveManager.PrepareContinue(save.scenePath, out _), Is.True);
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(save.scenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return Until(() => RunFlowController.Current != null && RunFlowController.Current.IsReady &&
                !RunFlowController.Current.IsBusy && !RunFlowController.Current.IsPreparing);
            var flow = RunFlowController.Current;
            Assert.That(flow.GetComponent<RunEndingTransition>(), Is.Not.Null);
            Player.Current.Motor.Teleport(flow.CurrentRoom.Root.Exit.position - flow.CurrentRoom.Root.Exit.forward + Vector3.up * .15f);
            yield return Until(() => SceneManager.GetActiveScene().name == "Ending");
            Assert.That(Object.FindObjectOfType<EndingUI>(), Is.Not.Null);
            Assert.That(RunFlowController.Current, Is.Null);
            Assert.That(Object.FindObjectsOfType<PlayerMotor>(), Is.Empty);
            Assert.That(MapManager.Instance.FlowRooms, Is.Empty);
        }

        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static IEnumerator Until(Func<bool> predicate)
        {
            float deadline = Time.realtimeSinceStartup + 30;
            while (!predicate())
            {
                Assert.Less(Time.realtimeSinceStartup, deadline, "等待结局流程超时");
                yield return null;
            }
        }
    }
}
#endif
