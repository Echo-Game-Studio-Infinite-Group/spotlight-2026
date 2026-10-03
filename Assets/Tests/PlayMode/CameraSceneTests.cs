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
    public IEnumerator CameraOrbitsIndependently_AndWasdTurnsCharacterWithoutCameraFeedback()
    {
#if UNITY_EDITOR
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
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
            Vector2[] moves = { Vector2.up, Vector2.left, Vector2.down, Vector2.right, Vector2.one.normalized };
            for (int direction = 0; direction < yaws.Length; direction++)
            {
                rig.enabled = false;
                Quaternion view = Quaternion.Euler(direction % 2 == 0 ? 70f : -25f, yaws[direction], 0f);
                camera.Follow.rotation = view;
                rig.enabled = true;
                Quaternion before = character.transform.rotation;
                yield return null;
                brain.ManualUpdate();
                Assert.Less(Quaternion.Angle(before, character.transform.rotation), 0.1f,
                    "单独转动相机不能改变人物朝向");
                Vector3 expected = Quaternion.Euler(0f, yaws[direction], 0f) *
                    new Vector3(moves[direction].x, 0f, moves[direction].y);
                for (int frame = 0; frame < 40; frame++)
                {
                    motor.Simulate(new PlayerInputFrame { Move = moves[direction] }, 1f / 60f, 0f);
                    yield return null;
                    brain.ManualUpdate();
                    Assert.IsTrue(brain.IsLive(camera));
                    Assert.Less(Vector3.Angle(Vector3.up, character.transform.up), 0.1f,
                        "角色不能继承相机俯仰或 Dutch");
                    Assert.Less(Quaternion.Angle(view, camera.Follow.rotation), 0.1f,
                        "角色转身不能累加到相机目标朝向");
                    Vector3 cameraForward = Vector3.ProjectOnPlane(cameraObject.transform.forward, Vector3.up);
                    Assert.Less(Vector3.Angle(cameraForward, Quaternion.Euler(0f, yaws[direction], 0f) * Vector3.forward), 0.1f);
                }
                Assert.Less(Vector3.Angle(expected, character.transform.forward), 0.2f,
                    "人物应转向相机相对的 WASD 方向，包括斜向输入");
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
        GameObject character = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        Assert.NotNull(character);
        Assert.NotNull(character.GetComponent<Player>());
        Assert.NotNull(character.GetComponent<PlayerCombat>());
        Assert.AreEqual(0, GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(character));
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
        Cinemachine3rdPersonFollow follow = camera.GetCinemachineComponent<Cinemachine3rdPersonFollow>();
        Assert.NotNull(follow);
        Assert.AreEqual(0, follow.CameraCollisionFilter.value);
        CameraWallFade wallFade = character.GetComponent<CameraWallFade>();
        Assert.NotNull(wallFade);
        var wallFadeBindings = new SerializedObject(wallFade);
        Assert.AreEqual(camera.Follow, wallFadeBindings.FindProperty("_target").objectReferenceValue);
        Assert.NotNull(wallFadeBindings.FindProperty("_wallMaterial").objectReferenceValue);
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
    public IEnumerator OccludingWallFadesAndRestoresWithoutChangingCollider()
    {
#if UNITY_EDITOR
        Material wallMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Materials/Wall.mat");
        Assert.NotNull(wallMaterial);
        GameObject target = new GameObject("FadeTarget");
        GameObject cameraObject = new GameObject("FadeTestCamera", typeof(Camera));
        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        GameObject fadeObject = new GameObject("CameraWallFadeTest");
        try
        {
            target.transform.position = Vector3.up;
            cameraObject.transform.position = Vector3.up + Vector3.back * 6f;
            wall.transform.position = Vector3.up + Vector3.back * 3f;
            wall.transform.localScale = new Vector3(2f, 3f, 0.5f);
            Renderer renderer = wall.GetComponent<Renderer>();
            Collider collider = wall.GetComponent<Collider>();
            renderer.sharedMaterial = wallMaterial;
            CameraWallFade fade = fadeObject.AddComponent<CameraWallFade>();
            fade.Configure(target.transform, wallMaterial, cameraObject.GetComponent<Camera>());
            Physics.SyncTransforms();
            yield return null;
            Assert.AreNotSame(wallMaterial, renderer.sharedMaterial);
            var properties = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(properties);
            Assert.Less(properties.GetColor("_BaseColor").a, 1f);
            Assert.IsTrue(collider.enabled);

            wall.transform.position += Vector3.right * 10f;
            Physics.SyncTransforms();
            for (int i = 0; i < 30 && renderer.sharedMaterial != wallMaterial; i++) yield return null;
            Assert.AreSame(wallMaterial, renderer.sharedMaterial);
            Assert.IsTrue(collider.enabled);
        }
        finally
        {
            Object.DestroyImmediate(fadeObject);
            Object.DestroyImmediate(wall);
            Object.DestroyImmediate(cameraObject);
            Object.DestroyImmediate(target);
        }
#else
        Assert.Ignore("遮挡材质验证在编辑器 PlayMode 中执行");
        yield break;
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
            int playerCount = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
                playerCount += root.GetComponentsInChildren<PlayerMotor>(true).Length;
            Assert.AreEqual(1, playerCount, "场景必须只保留新版玩家，避免输入与相机互相竞争");
            Assert.NotNull(motor);
            Assert.NotNull(motor.GetComponent<Player>());
            Assert.NotNull(motor.GetComponent<PlayerCombat>());
            Assert.NotNull(motor.GetComponent<PlayerHealth>());
            Assert.NotNull(motor.GetComponent<PlayerCameraRig>());
            Assert.NotNull(motor.GetComponent<SpeedCameraFeedback>());
            Assert.NotNull(motor.GetComponent<CameraWallFade>());
            foreach (GameObject root in scene.GetRootGameObjects())
                Assert.AreNotEqual("Player Virtual Camera", root.name);
            Assert.NotNull(brain);
            Assert.NotNull(time);
            Assert.IsTrue(brain.m_IgnoreTimeScale);
            // 场景加载恢复协程时，Brain 可能尚未经历第一次 LateUpdate。
            for (int i = 0; i < 10 && brain.ActiveVirtualCamera == null; i++) yield return null;
            Assert.NotNull(brain.ActiveVirtualCamera);
            CinemachineVirtualCamera characterCamera = motor.GetComponentInChildren<CinemachineVirtualCamera>();
            Assert.NotNull(characterCamera);
            Assert.AreEqual(0, characterCamera.GetCinemachineComponent<Cinemachine3rdPersonFollow>().CameraCollisionFilter.value);
            var wallBindings = new SerializedObject(motor.GetComponent<CameraWallFade>());
            Assert.AreEqual(characterCamera.Follow, wallBindings.FindProperty("_target").objectReferenceValue);
            Assert.NotNull(wallBindings.FindProperty("_wallMaterial").objectReferenceValue);
            Assert.IsTrue(brain.IsLive(characterCamera), "Brain 应使用 character 下的虚拟相机");
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
