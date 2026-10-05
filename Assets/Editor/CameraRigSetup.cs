using System;
using Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// 相机一键装配：向 CombatTestScene 的现有形态看齐——
//   · 场景根一台独立的 Virtual Camera（不挂在玩家下，也不套 CharacterCamera 包装节点）
//   · Main Camera 只放 Brain、URP 数据、PlayerCameraRig、CameraPostProcessing
//   · 玩家的 CameraTarget 仍是唯一的瞄准枢轴，vcam 的 Follow / LookAt 都指向它
// 幂等：对已经装好的场景（如 CombatTestScene）执行是空操作，可反复点。
public static class CameraRigSetup
{
    private static readonly string[] Scenes =
    {
        "Assets/Scenes/TestScene.unity",
        "Assets/Scenes/CombatTestScene.unity"
    };

    private const string VirtualCameraName = "Virtual Camera";
    private const string CharacterCameraName = "CharacterCamera";
    private const string CameraTargetName = "CameraTarget";
    private const float CameraTargetHeight = 1.2f;
    // 高于场景里任何遗留相机，保证玩家机位稳定接管 Brain。
    private const int VirtualCameraPriority = 10;

    [MenuItem("超高速行者/相机/一键装配")]
    public static void Wire()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.playModeStateChanged -= AfterPlay;
            EditorApplication.playModeStateChanged += AfterPlay;
            EditorApplication.isPlaying = false;
            return;
        }
        if (!EditorSceneManager.SaveOpenScenes()) throw new InvalidOperationException("当前场景保存失败");
        var setup = EditorSceneManager.GetSceneManagerSetup();
        try
        {
            foreach (string path in Scenes) WireScene(path);
        }
        finally { EditorSceneManager.RestoreSceneManagerSetup(setup); }
        AssetDatabase.SaveAssets();
        Debug.Log("[CameraRigSetup] 相机已装配：场景根 Virtual Camera + Main Camera 接线，玩家下不再有 CharacterCamera");
    }

    private static void AfterPlay(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode) return;
        EditorApplication.playModeStateChanged -= AfterPlay;
        EditorApplication.delayCall += Wire;
    }

    private static void WireScene(string path)
    {
        Scene scene = SceneManager.GetSceneByPath(path);
        if (!scene.IsValid() || !scene.isLoaded) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

        PlayerMotor player = FindPlayer(scene);
        if (player == null) throw new InvalidOperationException(path + " 里找不到 PlayerMotor，无法定位玩家");
        Transform target = EnsureCameraTarget(player.transform);
        RemoveCharacterCamera(player.transform);

        GameObject cameraObject = FindMainCamera(scene);
        CinemachineVirtualCamera vcam = EnsureVirtualCamera(scene, target);
        EnsureBrain(cameraObject);
        EnsureRig(cameraObject, player, target, vcam);
        EnsurePostProcessing(cameraObject, player, vcam, FindGlobalVolume(scene));
        DisableCompetingCameras(scene, vcam);

        // 改完必须 SetDirty 才会落盘；漏掉时内存值正确、磁盘是空的。
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                EditorUtility.SetDirty(component);
                if (PrefabUtility.IsPartOfPrefabInstance(component))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            }
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException(path + " 保存失败");
        Debug.Log($"[CameraRigSetup] {scene.name}: 玩家={player.name} vcam={vcam.name} 目标={target.name}");
    }

    // 优先取场景根上的 PlayerMotor：场景里可能同时躺着旧的敌人靶子或额外玩家。
    private static PlayerMotor FindPlayer(Scene scene)
    {
        PlayerMotor fallback = null;
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (PlayerMotor motor in root.GetComponentsInChildren<PlayerMotor>(true))
            {
                if (fallback == null) fallback = motor;
                if (motor.transform.parent == null) return motor;
            }
        return fallback;
    }

    private static Transform EnsureCameraTarget(Transform player)
    {
        Transform target = player.Find(CameraTargetName);
        if (target != null) return target;
        var host = new GameObject(CameraTargetName);
        target = host.transform;
        target.SetParent(player, false);
        target.localPosition = Vector3.up * CameraTargetHeight;
        return target;
    }

    // 相机改由场景根 vcam 承担后，玩家下的 CharacterCamera 包装节点就是多余的。
    private static void RemoveCharacterCamera(Transform player)
    {
        Transform wrapper = player.Find(CharacterCameraName);
        if (wrapper == null) return;
        if (PrefabUtility.IsPartOfPrefabInstance(wrapper))
        {
            Debug.LogWarning($"[CameraRigSetup] {player.name}/{CharacterCameraName} 来自预制体，"
                + "场景里删不掉，请在预制体上移除", wrapper);
            return;
        }
        UnityEngine.Object.DestroyImmediate(wrapper.gameObject);
    }

    private static GameObject FindMainCamera(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                if (child.CompareTag("MainCamera")) return child.gameObject;
        throw new InvalidOperationException(scene.name + " 里没有 tag=MainCamera 的相机");
    }

    private static CinemachineVirtualCamera EnsureVirtualCamera(Scene scene, Transform target)
    {
        GameObject host = null;
        foreach (GameObject root in scene.GetRootGameObjects())
            if (root.name == VirtualCameraName) { host = root; break; }
        if (host == null)
        {
            host = new GameObject(VirtualCameraName);
            SceneManager.MoveGameObjectToScene(host, scene);
        }

        CinemachineVirtualCamera vcam = GetOrAdd<CinemachineVirtualCamera>(host);
        vcam.Priority = VirtualCameraPriority;
        vcam.Follow = target;
        vcam.LookAt = target;

        Cinemachine3rdPersonFollow follow = vcam.GetCinemachineComponent<Cinemachine3rdPersonFollow>();
        if (follow == null) follow = vcam.AddCinemachineComponent<Cinemachine3rdPersonFollow>();
        follow.Damping = new Vector3(0.08f, 0.15f, 0.08f);
        follow.ShoulderOffset = Vector3.zero;
        follow.VerticalArmLength = 0f;
        follow.CameraSide = 1f;
        follow.CameraDistance = 6f;
        follow.CameraRadius = 0.2f;
        follow.IgnoreTag = "Player";
        // 狭缝中避障会让镜头反复缩放；遮挡交给 CameraWallFade，不走碰撞。
        follow.CameraCollisionFilter = 0;

        CameraShaker.EnsureListener(vcam);
        SetPrivate(GetOrAdd<CameraShaker>(host), "_virtualCamera", vcam);
        return vcam;
    }

    private static void EnsureBrain(GameObject cameraObject)
    {
        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(cameraObject);
        CinemachineBrain brain = GetOrAdd<CinemachineBrain>(cameraObject);
        brain.m_IgnoreTimeScale = true;
        brain.m_UpdateMethod = CinemachineBrain.UpdateMethod.LateUpdate;
        GetOrAdd<UniversalAdditionalCameraData>(cameraObject).renderPostProcessing = true;
    }

    private static void EnsureRig(GameObject cameraObject, PlayerMotor player, Transform target, CinemachineVirtualCamera vcam)
    {
        GetOrAdd<PlayerCameraRig>(cameraObject)
            .Configure(player, player.GetComponent<PlayerInputReader>(), target, vcam);
    }

    private static void EnsurePostProcessing(GameObject cameraObject, PlayerMotor player, CinemachineVirtualCamera vcam, Volume volume)
    {
        GetOrAdd<CameraPostProcessing>(cameraObject).Configure(player, vcam, volume);
    }

    private static Volume FindGlobalVolume(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Volume volume in root.GetComponentsInChildren<Volume>(true))
                if (volume.isGlobal) return volume;
        return null;
    }

    // 场景里遗留的独立虚拟相机可能与玩家相机同优先级，抢走 Brain 的控制权。
    private static void DisableCompetingCameras(Scene scene, CinemachineVirtualCamera keep)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (CinemachineVirtualCamera other in root.GetComponentsInChildren<CinemachineVirtualCamera>(true))
                if (other != keep) other.enabled = false;
    }

    private static T GetOrAdd<T>(GameObject go) where T : Component
    {
        // Unity 原生组件的空引用可能保留托管包装，不能用 ?? 判断。
        T component = go.GetComponent<T>();
        return component != null ? component : go.AddComponent<T>();
    }

    // 装配工具写的是私有序列化字段，只能走 SerializedObject。
    private static void SetPrivate(UnityEngine.Object owner, string field, UnityEngine.Object value)
    {
        var serialized = new SerializedObject(owner);
        SerializedProperty property = serialized.FindProperty(field);
        if (property == null) throw new InvalidOperationException(owner.GetType().Name + " 缺少字段 " + field);
        property.objectReferenceValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }
}
