using System.Collections;
using System.Collections.Generic;
using GameJam.Flow;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace GameJam.FlowTests
{
    public sealed class FlowTextRuntimeTests
    {
        private GameObject _root, _cameraObject, _clock;
        private FlowRoomTemplate _template;
        private FlowTextPoint _point;
        private RoomVariantDefinition _variant;
        private FlowRoomTexts _texts;
        private readonly List<GameObject> _previousMainCameras = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            // 完整流程测试会留下流程场景的镜头；本用例临时隔离主相机标签，结束后恢复。
            foreach (Camera camera in Object.FindObjectsOfType<Camera>(true))
                if (camera.CompareTag("MainCamera")) { _previousMainCameras.Add(camera.gameObject); camera.tag = "Untagged"; }
            _clock = new GameObject("文字时间测试"); _clock.AddComponent<TimeManager>(); TimeManager.SetPaused(false);
            _root = new GameObject("运行文字模板"); _template = _root.AddComponent<FlowRoomTemplate>();
            _root.transform.SetPositionAndRotation(new Vector3(33000f, 30f, 33000f), Quaternion.Euler(0f, 90f, 0f));
            GameObject prefab = null;
#if UNITY_EDITOR
            prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Flow/TextPoint.prefab");
#endif
            Assert.That(prefab, Is.Not.Null, "测试前先生成文字点预制体");
            var item = Object.Instantiate(prefab);
            item.transform.SetParent(_root.transform, false); _point = item.GetComponent<FlowTextPoint>(); _point.Id = "point";
            _variant = ScriptableObject.CreateInstance<RoomVariantDefinition>();
            _variant.Texts = new[] { new FlowTextBinding { PointId = "point", Enabled = true, Content = "保持速度\n前进", Trigger = FlowTextTrigger.RoomEntered, HoldSeconds = .1f, AppearanceSpeed = 100f, DisappearanceSpeed = 100f } };
            _texts = new FlowRoomTexts(_template, _variant);
        }
        [TearDown]
        public void TearDown()
        {
            TimeManager.SetPaused(false);
            Object.DestroyImmediate(_root); Object.DestroyImmediate(_variant);
            if (_cameraObject != null) Object.DestroyImmediate(_cameraObject);
            foreach (GameObject camera in _previousMainCameras) if (camera != null) camera.tag = "MainCamera";
            _previousMainCameras.Clear();
            Object.DestroyImmediate(_clock);
        }

        [UnityTest]
        public IEnumerator PreloadStaysHiddenAndRoomEntryPlaysOnlyOnce()
        {
            _texts.SetCurrent(false); yield return null; yield return null;
            Assert.That(_point.HasTriggered, Is.False); Assert.That(_point.Visual.IsVisible, Is.False);
            _texts.SetCurrent(true); yield return null;
            Assert.That(_point.HasTriggered, Is.True); Assert.That(_point.Visual.Content, Is.EqualTo("保持速度\n前进"));
            _point.Visual.Advance(1f); Assert.That(_point.Visual.IsVisible, Is.False);
            _texts.SetCurrent(false); _texts.SetCurrent(true); yield return null;
            Assert.That(_point.Visual.IsVisible, Is.False);
            _texts = new FlowRoomTexts(_template, _variant); _texts.SetCurrent(true); yield return null;
            Assert.That(_point.HasTriggered, Is.True); Assert.That(_point.Visual.IsVisible, Is.True);
        }

        [UnityTest]
        public IEnumerator VisibilityAndDistanceGateTriggeringInRotatedRoom()
        {
            _variant.Texts[0].Trigger = FlowTextTrigger.Visible; _variant.Texts[0].MaximumDistance = 10f;
            _point.Bind(_variant.Texts[0]);
            _cameraObject = new GameObject("文字镜头"); _cameraObject.tag = "MainCamera";
            Camera camera = _cameraObject.AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(_point.Visual.WorldBounds.center - _point.transform.forward * 20f, _point.transform.rotation);
            _texts.SetCurrent(true); yield return null; Assert.That(_point.HasTriggered, Is.False);
            camera.transform.position += _point.transform.forward * 15f;
            camera.transform.rotation = Quaternion.LookRotation(-_point.transform.forward); yield return null;
            Assert.That(_point.HasTriggered, Is.False);
            camera.transform.rotation = _point.transform.rotation;
            Assert.That(Camera.main, Is.SameAs(camera));
            Assert.That(_point.CanSee(camera), Is.True, "文字边界 " + _point.Visual.WorldBounds + "，镜头 " + camera.transform.position);
            yield return null;
            Assert.That(_point.HasTriggered, Is.True);
            Assert.That(_point.Visual.transform.position, Is.EqualTo(_point.transform.position));
        }

        [UnityTest]
        public IEnumerator DisabledContentAndPausePreventTriggerAndPersistentTextReturnsWithoutReplay()
        {
            _variant.Texts[0].Display = FlowTextDisplay.Persistent;
            _point.Bind(_variant.Texts[0]); _texts.SetCurrent(true);
            _point.gameObject.SetActive(false); yield return null; Assert.That(_point.HasTriggered, Is.False);
            _point.gameObject.SetActive(true); TimeManager.SetPaused(true); yield return null;
            Assert.That(_point.HasTriggered, Is.False);
            TimeManager.SetPaused(false); yield return null;
            Assert.That(_point.HasTriggered, Is.True);
            _point.Visual.Advance(1f); Assert.That(_point.Visual.IsVisible, Is.True); Assert.That(_point.Visual.IsPlaying, Is.False);
            _texts.SetCurrent(false); Assert.That(_point.Visual.IsVisible, Is.False);
            _texts.SetCurrent(true); Assert.That(_point.Visual.IsVisible, Is.True); Assert.That(_point.Visual.IsPlaying, Is.False);
        }

        [UnityTest]
        public IEnumerator SlowMotionDoesNotChangeTextClockAndPauseFreezesIt()
        {
            _variant.Texts[0].AppearanceSpeed = 1f; _point.Bind(_variant.Texts[0]);
            TimeManager.Apply(TimeManager.TimeLayer.Global, .01f, 0f, this); _texts.SetCurrent(true); yield return null;
            Material material = _point.Visual.GetComponent<Renderer>().sharedMaterial;
            float before = material.GetFloat("_Right"); yield return new WaitForSecondsRealtime(.12f);
            Assert.That(material.GetFloat("_Right"), Is.GreaterThan(before));
            TimeManager.SetPaused(true); before = material.GetFloat("_Right"); yield return new WaitForSecondsRealtime(.1f);
            Assert.That(material.GetFloat("_Right"), Is.EqualTo(before));
        }
    }
}
