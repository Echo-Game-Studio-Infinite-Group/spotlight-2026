using System;
using Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class MovementSceneSetup
{
    public const string ScenePath = "Assets/Scenes/TestScene.unity";
    public const string CharacterPath = "Assets/Prefabs/character.prefab";

    [MenuItem("超高速行者/装配 character 与 Cinemachine")]
    public static void Rebuild()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.playModeStateChanged -= AfterPlay;
            EditorApplication.playModeStateChanged += AfterPlay;
            EditorApplication.isPlaying = false;
            return;
        }
        Apply(EditorSceneManager.OpenScene(ScenePath));
    }

    private static void AfterPlay(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        EditorApplication.playModeStateChanged -= AfterPlay;
        EditorApplication.delayCall += Rebuild;
    }

    public static void Apply(Scene scene)
    {
        var parameters = AssetDatabase.LoadAssetAtPath<MovementParams>("Assets/Settings/MovementParams.asset");
        var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>("Assets/Input/PlayerControls.inputactions");
        if (parameters == null || actions == null) throw new InvalidOperationException("移动参数或 Action Map 缺失");
        ConfigurePrefab(parameters, actions);

        // character.prefab 是模型变体，实例根沿用源预制体的名字（就是 "Player"），按名字找不到：
        // 改为按「是否实例自 character.prefab」识别，避免重复实例化、以及把上一份装好的角色当成旧 Player 删掉
        GameObject character = FindCharacterInstance(scene);
        GameObject oldPlayer = FindLegacyPlayer(scene, character);
        if (character == null)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPath);
            character = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            if (oldPlayer != null)
                character.transform.SetPositionAndRotation(oldPlayer.transform.position, oldPlayer.transform.rotation);
        }
        GameObject cameraObject = Find(scene, "Main Camera");
        if (cameraObject == null) throw new InvalidOperationException("场景缺少 Main Camera");
        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(cameraObject);
        CinemachineBrain brain = GetOrAdd<CinemachineBrain>(cameraObject);
        brain.m_IgnoreTimeScale = true;
        brain.m_UpdateMethod = CinemachineBrain.UpdateMethod.LateUpdate;
        GetOrAdd<UniversalAdditionalCameraData>(cameraObject).renderPostProcessing = true;

        Volume volume = Find(scene, "Global Volume")?.GetComponent<Volume>();
        if (volume == null)
        {
            var volumeObject = new GameObject("Global Volume");
            SceneManager.MoveGameObjectToScene(volumeObject, scene);
            volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Settings/RunVolume.asset");
        }
        ConfigureCharacter(character, parameters, actions, cameraObject.transform, volume);
        // 只清理由旧装配创建的控制对象，不碰地图、模型与其他场景内容。
        if (oldPlayer != null && oldPlayer != character) UnityEngine.Object.DestroyImmediate(oldPlayer);
        GameObject oldCamera = Find(scene, "Player Virtual Camera");
        if (oldCamera != null && !oldCamera.transform.IsChildOf(character.transform))
            UnityEngine.Object.DestroyImmediate(oldCamera);
        // 场景里遗留的独立虚拟相机可能与角色相机同优先级，抢走 Brain 的控制权。
        GameObject looseCamera = Find(scene, "Virtual Camera");
        if (looseCamera != null && !looseCamera.transform.IsChildOf(character.transform))
        {
            CinemachineVirtualCamera competingCamera = looseCamera.GetComponent<CinemachineVirtualCamera>();
            if (competingCamera != null) competingCamera.enabled = false;
        }
        if (Find(scene, "TimeManager") == null)
        {
            var time = new GameObject("TimeManager");
            SceneManager.MoveGameObjectToScene(time, scene);
            time.AddComponent<TimeManager>();
        }
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
                if (component != null)
                {
                    EditorUtility.SetDirty(component);
                    if (PrefabUtility.IsPartOfPrefabInstance(component))
                        PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                }
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Verify(scene);
    }

    private static void ConfigurePrefab(MovementParams parameters, InputActionAsset actions)
    {
        GameObject contents = PrefabUtility.LoadPrefabContents(CharacterPath);
        try
        {
            CapsuleCollider oldCollider = contents.GetComponent<CapsuleCollider>();
            if (oldCollider != null)
            {
                // 沿用美术角色已调好的胶囊尺寸；Motor 的 pivot 约定仍为脚底。
                parameters.CapsuleBaseHeight = oldCollider.height;
                parameters.CapsuleBaseRadius = oldCollider.radius;
                parameters.CapsuleFastHeight = Mathf.Min(parameters.CapsuleFastHeight, oldCollider.height);
                parameters.CapsuleMinRadius = Mathf.Min(parameters.CapsuleMinRadius, oldCollider.radius);
                EditorUtility.SetDirty(parameters);
            }
            ConfigureCharacter(contents, parameters, actions, null, null);
            PrefabUtility.SaveAsPrefabAsset(contents, CharacterPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(contents); }
    }

    private static void ConfigureCharacter(GameObject character, MovementParams parameters,
        InputActionAsset actions, Transform cameraReference, Volume volume)
    {
        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(character);
        foreach (CapsuleCollider collider in character.GetComponents<CapsuleCollider>())
            UnityEngine.Object.DestroyImmediate(collider);
        CharacterController controller = GetOrAdd<CharacterController>(character);
        controller.radius = parameters.CapsuleBaseRadius;
        controller.height = parameters.CapsuleBaseHeight;
        controller.center = Vector3.up * (controller.height * 0.5f);
        controller.minMoveDistance = 0f;
        foreach (Animator animator in character.GetComponentsInChildren<Animator>(true))
        {
            animator.runtimeAnimatorController = PlayerAnimationSetup.EnsureController();
            animator.applyRootMotion = false;
            EditorUtility.SetDirty(animator);
            // character 是模型的变体，必须记录覆盖才能在重新导入后保留关闭状态。
            if (PrefabUtility.IsPartOfPrefabInstance(animator))
                PrefabUtility.RecordPrefabInstancePropertyModifications(animator);
        }
        foreach (Transform child in character.GetComponentsInChildren<Transform>(true)) child.gameObject.tag = "Player";

        PlayerInputReader input = GetOrAdd<PlayerInputReader>(character);
        input.Configure(actions);
        PlayerMotor motor = GetOrAdd<PlayerMotor>(character);
        motor.SetParams(parameters);
        motor.SetMovementReference(cameraReference);
        motor.enabled = true;
        GetOrAdd<PlayerAnimation>(character).Configure(character.GetComponentInChildren<Animator>(true));
        GetOrAdd<PlayerRespawn>(character);
        GetOrAdd<DebugHUD>(character);

        Transform target = character.transform.Find("CameraTarget");
        if (target == null)
        {
            target = new GameObject("CameraTarget").transform;
            target.SetParent(character.transform, false);
            target.localPosition = Vector3.up * 1.2f;
            target.localRotation = Quaternion.Euler(15f, 0f, 0f);
        }
        Transform rig = character.transform.Find("CharacterCamera");
        if (rig == null)
        {
            rig = new GameObject("CharacterCamera").transform;
            rig.SetParent(character.transform, false);
        }
        CinemachineVirtualCamera vcam = GetOrAdd<CinemachineVirtualCamera>(rig.gameObject);
        vcam.Follow = target;
        vcam.LookAt = target;
        Cinemachine3rdPersonFollow follow = vcam.GetCinemachineComponent<Cinemachine3rdPersonFollow>();
        if (follow == null)
        {
            follow = vcam.AddCinemachineComponent<Cinemachine3rdPersonFollow>();
            follow.Damping = new Vector3(0.08f, 0.15f, 0.08f);
            follow.ShoulderOffset = Vector3.zero;
            follow.VerticalArmLength = 0f;
            follow.CameraDistance = 6f;
            follow.CameraRadius = 0.2f;
            follow.IgnoreTag = "Player";
        }
        // 狭缝中避障会反复缩放镜头；遮挡由 CameraWallFade 接管。
        follow.CameraCollisionFilter = 0;
        GetOrAdd<PlayerCameraRig>(character).Configure(motor, input, target, vcam);
        GetOrAdd<SpeedCameraFeedback>(character).Configure(motor, vcam, volume);
        Material wallMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Materials/Wall.mat");
        GetOrAdd<CameraWallFade>(character).Configure(target, wallMaterial);
        foreach (Component component in character.GetComponentsInChildren<Component>(true))
            if (component != null) EditorUtility.SetDirty(component);
    }

    public static void Verify(Scene scene)
    {
        GameObject character = FindCharacterInstance(scene);
        GameObject camera = Find(scene, "Main Camera");
        PlayerMotor motor = character != null ? character.GetComponent<PlayerMotor>() : null;
        CinemachineVirtualCamera vcam = character != null ? character.GetComponentInChildren<CinemachineVirtualCamera>() : null;
        CinemachineVirtualCamera looseVcam = Find(scene, "Virtual Camera")?.GetComponent<CinemachineVirtualCamera>();
        if (motor == null || !motor.enabled || motor.Params == null || character.GetComponent<PlayerInputReader>() == null ||
            character.GetComponent<PlayerCameraRig>() == null || character.GetComponent<SpeedCameraFeedback>() == null ||
            camera == null || camera.GetComponent<CinemachineBrain>() == null || vcam == null || vcam.Follow == null ||
            (looseVcam != null && looseVcam.isActiveAndEnabled && looseVcam != vcam) ||
            FindLegacyPlayer(scene, character) != null || Find(scene, "Player Virtual Camera") != null)
            throw new InvalidOperationException("character 的移动或 Cinemachine 接线校验失败");
        Debug.Log("[MovementSceneSetup] character 预制体及场景已绑定新运动与相机，旧 Player 已清理");
    }

    private static T GetOrAdd<T>(GameObject go) where T : Component
    {
        // Unity 原生组件的空引用可能保留托管包装，不能用 ?? 判断。
        T component = go.GetComponent<T>();
        return component != null ? component : go.AddComponent<T>();
    }
    private static GameObject Find(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name) return child.gameObject;
        return null;
    }

    // character 实例的根名字继承自模型源预制体（就是 "Player"），只能按「实例自哪个预制体」识别
    private static GameObject FindCharacterInstance(Scene scene)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterPath);
        if (prefab == null) return null;
        foreach (GameObject root in scene.GetRootGameObjects())
            if (PrefabUtility.GetCorrespondingObjectFromSource(root) == prefab) return root;
        return null;
    }

    // 旧装配遗留的裸角色：挂着 PlayerMotor 但不是 character 实例（时间久了可能已被改名）
    private static GameObject FindLegacyPlayer(Scene scene, GameObject character)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
            if (root != character && root.GetComponent<PlayerMotor>() != null) return root;
        return null;
    }
}
