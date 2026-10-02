using System.Collections;
using Cinemachine;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

public sealed class CameraSceneTests
{
    [UnityTest]
    public IEnumerator CharacterFacesRenderedCamera_WithoutRotatingCameraAgain()
    {
#if UNITY_EDITOR
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/character.prefab");
        GameObject character = Object.Instantiate(prefab, Vector3.up * 100f, Quaternion.identity);
        GameObject cameraObject = new GameObject("FacingTestCamera", typeof(Camera), typeof(CinemachineBrain));
        try
        {
            PlayerMotor motor = character.GetComponent<PlayerMotor>();
            motor.enabled = false;
            character.GetComponent<PlayerInputReader>().SetGameplayEnabled(false);
            character.GetComponent<SpeedCameraFeedback>().enabled = false;
            PlayerCameraRig rig = character.GetComponent<PlayerCameraRig>();
            var camera = character.GetComponentInChildren<CinemachineVirtualCamera>();
            camera.Priority = 1000;
            camera.m_Lens.Dutch = 20f;
            camera.GetCinemachineComponent<Cinemachine3rdPersonFollow>().CameraCollisionFilter = 0;
            CinemachineBrain brain = cameraObject.GetComponent<CinemachineBrain>();
            brain.m_UpdateMethod = CinemachineBrain.UpdateMethod.ManualUpdate;
            motor.SetMovementReference(cameraObject.transform);

            float[] yaws = { 90f, 179f, -179f, -90f, 0f };
            Vector2[] moves = { Vector2.zero, Vector2.left, Vector2.down, Vector2.right, Vector2.up };
            for (int direction = 0; direction < yaws.Length; direction++)
            {
                rig.enabled = false;
                Quaternion view = Quaternion.Euler(direction % 2 == 0 ? 70f : -25f, yaws[direction], 0f);
                camera.Follow.rotation = view;
                rig.enabled = true;
                for (int frame = 0; frame < 5; frame++)
                {
                    yield return null;
                    motor.Simulate(new PlayerInputFrame { Move = moves[direction] }, 1f / 60f, 0f);
                    brain.ManualUpdate();
                    Assert.IsTrue(brain.IsLive(camera));
                    Vector3 expected = Vector3.ProjectOnPlane(cameraObject.transform.forward, Vector3.up).normalized;
                    Assert.Less(Vector3.Angle(expected, character.transform.forward), 0.1f,
                        "静止、横移和后退都应朝向本帧实际相机");
                    Assert.Less(Vector3.Angle(Vector3.up, character.transform.up), 0.1f,
                        "角色不能继承相机俯仰或 Dutch");
                    Assert.Less(Quaternion.Angle(view, camera.Follow.rotation), 0.1f,
                        "角色转身不能累加到相机目标朝向");
                    Assert.Less(Mathf.Abs(Mathf.DeltaAngle(yaws[direction], character.transform.eulerAngles.y)), 0.1f);
                }
            }
        }
        finally
        {
            Object.DestroyImmediate(character);
            Object.DestroyImmediate(cameraObject);
        }
#else
        Assert.Ignore("角色预制体验证在编辑器 PlayMode 中执行");
        yield break;
#endif
    }

    [Test]
    public void CharacterPrefab_HasOneMotorAndSelfContainedCameraBindings()
    {
#if UNITY_EDITOR
        GameObject character = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/character.prefab");
        Assert.NotNull(character);
        PlayerMotor motor = character.GetComponent<PlayerMotor>();
        Assert.NotNull(motor);
        Assert.NotNull(motor.Params);
        Assert.AreEqual(1, character.GetComponentsInChildren<PlayerMotor>(true).Length);
        Assert.NotNull(character.GetComponent<CharacterController>());
        Assert.IsNull(character.GetComponent<CapsuleCollider>());
        Assert.NotNull(character.GetComponent<PlayerInputReader>());
        Assert.NotNull(character.GetComponent<PlayerCameraRig>());
        Assert.NotNull(character.GetComponent<SpeedCameraFeedback>());
        var camera = character.GetComponentInChildren<CinemachineVirtualCamera>(true);
        Assert.NotNull(camera);
        Assert.AreEqual(character.transform.Find("CameraTarget"), camera.Follow);
        Assert.NotNull(camera.GetCinemachineComponent<Cinemachine3rdPersonFollow>());
        var rig = new SerializedObject(character.GetComponent<PlayerCameraRig>());
        Assert.AreEqual(motor, rig.FindProperty("_motor").objectReferenceValue);
        var feedback = new SerializedObject(character.GetComponent<SpeedCameraFeedback>());
        Assert.AreEqual(motor, feedback.FindProperty("_motor").objectReferenceValue);
        Assert.AreEqual(camera, feedback.FindProperty("_virtualCamera").objectReferenceValue);
        Assert.Greater(character.GetComponentsInChildren<Renderer>(true).Length, 0);
        foreach (Animator animator in character.GetComponentsInChildren<Animator>(true)) Assert.IsFalse(animator.applyRootMotion);
#else
        Assert.Ignore("预制体接线验证在编辑器 PlayMode 中执行");
#endif
    }

    [UnityTest]
    public IEnumerator TestScene_CinemachineFollowsDuringPlayerFreeze()
    {
#if UNITY_EDITOR
        Scene original = SceneManager.GetActiveScene();
        Scene scene = EditorSceneManager.LoadSceneInPlayMode("Assets/Scenes/TestScene.unity", new LoadSceneParameters(LoadSceneMode.Additive));
        yield return null;
        PlayerMotor motor = null;
        CinemachineBrain brain = null;
        TimeManager time = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (motor == null) motor = root.GetComponentInChildren<PlayerMotor>();
            if (brain == null) brain = root.GetComponentInChildren<CinemachineBrain>();
            if (time == null) time = root.GetComponentInChildren<TimeManager>();
        }
        try
        {
            Assert.NotNull(motor);
            Assert.AreEqual("character", motor.name);
            Assert.NotNull(motor.GetComponent<PlayerCameraRig>());
            Assert.NotNull(motor.GetComponent<SpeedCameraFeedback>());
            foreach (GameObject root in scene.GetRootGameObjects())
                Assert.That(root.name, Is.Not.EqualTo("Player").And.Not.EqualTo("Player Virtual Camera"));
            Assert.NotNull(brain);
            Assert.NotNull(time);
            Assert.IsTrue(brain.m_IgnoreTimeScale);
            // 场景加载恢复协程时，Brain 可能尚未经历第一次 LateUpdate。
            for (int i = 0; i < 10 && brain.ActiveVirtualCamera == null; i++) yield return null;
            Assert.NotNull(brain.ActiveVirtualCamera);
            time.PlayerScale = 0f;
            Vector3 before = brain.transform.position;
            motor.Teleport(motor.transform.position + Vector3.up * 10f);
            for (int i = 0; i < 10; i++) yield return null;
            Assert.Greater(brain.transform.position.y - before.y, 5f);
            Assert.AreEqual(Vector3.zero, motor.Velocity);
        }
        finally
        {
            SceneManager.SetActiveScene(original);
            SceneManager.UnloadSceneAsync(scene);
        }
        yield return null;
#else
        Assert.Ignore("场景装配验证在编辑器 PlayMode 中执行");
        yield break;
#endif
    }
}
