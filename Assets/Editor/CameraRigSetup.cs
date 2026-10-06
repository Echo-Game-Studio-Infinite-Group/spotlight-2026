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
//
// 组件归属只有两条线，别混：
//   震屏链  —— CinemachineImpulseListener + CameraShaker —— 挂 vcam 物体。
//              impulse 施加在 vcam 的最终输出上，离开 vcam 就不生效。
//   渲染链  —— CinemachineBrain + PlayerCameraRig + CameraPostProcessing —— 挂 Main Camera。
//              Brain 是 Camera 组件的孪生组件，脱离相机无法存在。
// 装配时会把挂错位置的组件搬回对应物体，避免「东一个西一个」。
//
// 相机本体走预制体：场景里没有就实例化 Assets/Prefabs 下那两个，
// 这样「把相机删光再装配」也能装回来，而不是留下一个裸的空物体。
public static class CameraRigSetup
{
    private static readonly string[] Scenes =
    {
        "Assets/Scenes/LevelTestScene.unity"
    };

    private const string MainCameraPrefabPath = "Assets/Prefabs/Main Camera.prefab";
    private const string VirtualCameraPrefabPath = "Assets/Prefabs/Virtual Camera.prefab";
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

        StripMissingScripts(scene);

        PlayerMotor player = FindPlayer(scene);
        if (player == null) throw new InvalidOperationException(path + " 里找不到 PlayerMotor，无法定位玩家");
        Transform target = EnsureCameraTarget(player.transform);
        RemoveCharacterCamera(player.transform);

        GameObject cameraObject = EnsureMainCamera(scene);
        CinemachineVirtualCamera vcam = EnsureVirtualCamera(scene, target);
        EnsureBrain(cameraObject);
        EnsureRig(cameraObject, player, target, vcam);
        EnsurePostProcessing(cameraObject, player, vcam, FindGlobalVolume(scene));
        EnforceOwnership(cameraObject, vcam);
        DisableCompetingCameras(scene, vcam);

        // 改完必须 SetDirty 才会落盘；漏掉时内存值正确、磁盘是空的。
        // 预制体实例上的改动还要 RecordPrefabInstancePropertyModifications，
        // 否则 SaveScene 写回的是预制体原值，表现为「改完打开又是空的」。
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

    // LevelTestScene 里遗留了 GUID 已失效的脚本（如 NavMesh 上那个），不清掉 Console 一直报警。
    private static void StripMissingScripts(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);
            if (removed > 0) Debug.Log($"[CameraRigSetup] {scene.name}/{root.name}: 清掉 {removed} 个丢失脚本");
        }
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

    // CameraTarget 是 Player.prefab 自带的子节点，正常情况下直接复用。
    // 找不到时**不能**在预制体实例下新建子物体：那种「实例内新增节点」写引用时，
    // 场景里存的是被覆盖的实例节点，而 vcam 在场景根上——两边对不上号，落盘就成了空引用。
    // 所以这里只在预制体被改造过、确实缺失时才补，并且补完给出明确警告。
    private static Transform EnsureCameraTarget(Transform player)
    {
        Transform target = player.Find(CameraTargetName);
        if (target != null) return target;

        var host = new GameObject(CameraTargetName);
        target = host.transform;
        target.SetParent(player, false);
        target.localPosition = Vector3.up * CameraTargetHeight;
        Debug.LogWarning($"[CameraRigSetup] {player.name} 下缺 {CameraTargetName}，已临时新建；"
            + "正确做法是在 Player.prefab 上补这个节点，否则换场景还得再来一次", host);
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

    // 场景里没有 MainCamera 时从预制体装回来；找不到预制体才报错。
    // 主人把相机删光后直接点装配，走的就是这条路径。
    private static GameObject EnsureMainCamera(Scene scene)
    {
        GameObject found = FindMainCamera(scene);
        if (found != null) return found;
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MainCameraPrefabPath);
        if (prefab == null) throw new InvalidOperationException(
            scene.name + " 里没有 tag=MainCamera 的相机，且找不到预制体 " + MainCameraPrefabPath);
        GameObject instance = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
        if (instance == null) throw new InvalidOperationException("实例化 " + MainCameraPrefabPath + " 失败");
        // 实例化会继承预制体里的位置；相机位置由 Brain/vcam 每帧接管，这里只保证它在场景根。
        instance.transform.SetParent(null, true);
        Debug.Log($"[CameraRigSetup] {scene.name}: 从 {MainCameraPrefabPath} 实例化 Main Camera");
        return instance;
    }

    private static GameObject FindMainCamera(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                if (child.CompareTag("MainCamera")) return child.gameObject;
        return null;
    }

    // 场景里可能叫别的名字（例如解包自预制体后仍旧名）；
    // 找不到就从预制体实例化，避免同一场景出现两台抢镜头的 vcam。
    private static CinemachineVirtualCamera EnsureVirtualCamera(Scene scene, Transform target)
    {
        CinemachineVirtualCamera vcam = FindVirtualCamera(scene);
        if (vcam == null)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(VirtualCameraPrefabPath);
            if (prefab == null) throw new InvalidOperationException(
                scene.name + " 里没有虚拟相机，且找不到预制体 " + VirtualCameraPrefabPath);
            GameObject instance = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
            if (instance == null) throw new InvalidOperationException("实例化 " + VirtualCameraPrefabPath + " 失败");
            instance.transform.SetParent(null, true);
            instance.name = VirtualCameraName;
            vcam = instance.GetComponent<CinemachineVirtualCamera>();
            if (vcam == null) throw new InvalidOperationException(VirtualCameraPrefabPath + " 根节点没有 CinemachineVirtualCamera");
            Debug.Log($"[CameraRigSetup] {scene.name}: 从 {VirtualCameraPrefabPath} 实例化 Virtual Camera");
        }
        GameObject host = vcam.gameObject;

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
        // Follow / LookAt 指向玩家预制体实例里的子节点，必须记录覆盖才会落盘。
        Record(vcam);
        Record(follow);
        return vcam;
    }

    // 优先按名字取；名字对不上时退而取场景里任意一台已启用的 vcam，最后才当作没有。
    private static CinemachineVirtualCamera FindVirtualCamera(Scene scene)
    {
        CinemachineVirtualCamera fallback = null;
        foreach (GameObject root in scene.GetRootGameObjects())
            foreach (CinemachineVirtualCamera candidate in root.GetComponentsInChildren<CinemachineVirtualCamera>(true))
            {
                if (candidate.gameObject.name == VirtualCameraName) return candidate;
                if (fallback == null) fallback = candidate;
            }
        return fallback;
    }

    private static void EnsureBrain(GameObject cameraObject)
    {
        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(cameraObject);
        CinemachineBrain brain = GetOrAdd<CinemachineBrain>(cameraObject);
        brain.m_IgnoreTimeScale = true;
        brain.m_UpdateMethod = CinemachineBrain.UpdateMethod.LateUpdate;
        GetOrAdd<UniversalAdditionalCameraData>(cameraObject).renderPostProcessing = true;
        Record(brain);
    }

    private static void EnsureRig(GameObject cameraObject, PlayerMotor player, Transform target, CinemachineVirtualCamera vcam)
    {
        // _motor / _input 已隐藏且运行时会自取，仍然写一遍是为了让场景文件自解释、也方便排障。
        PlayerCameraRig rig = GetOrAdd<PlayerCameraRig>(cameraObject);
        rig.Configure(player, player.GetComponent<PlayerInputReader>(), target, vcam);
        Record(rig);
    }

    private static void EnsurePostProcessing(GameObject cameraObject, PlayerMotor player, CinemachineVirtualCamera vcam, Volume volume)
    {
        CameraPostProcessing processing = GetOrAdd<CameraPostProcessing>(cameraObject);
        processing.Configure(player, vcam, volume);
        Record(processing);
    }

    // 跨预制体实例写引用时，Unity 只有收到这条记录才会把改动写进实例覆盖列表。
    // 漏掉的表现是：Inspector 当场看着接好了，保存后重开全是空——相机就是不动。
    private static void Record(Component component)
    {
        if (component == null) return;
        EditorUtility.SetDirty(component);
        if (PrefabUtility.IsPartOfPrefabInstance(component))
            PrefabUtility.RecordPrefabInstancePropertyModifications(component);
    }

    // 归属规则见文件头：震屏链跟 vcam、渲染链跟主相机。
    // 从预制体解包或跨场景复制后，这两类组件常被搬到对方物体上，
    // 症状是「Inspector 看着有、运行起来静默失效」，所以这里主动搬回正确位置。
    private static void EnforceOwnership(GameObject cameraObject, CinemachineVirtualCamera vcam)
    {
        GameObject vcamObject = vcam.gameObject;
        MoveTo<CameraShaker>(cameraObject, vcamObject);
        MoveTo<CinemachineImpulseListener>(cameraObject, vcamObject);
        MoveTo<PlayerCameraRig>(vcamObject, cameraObject);
        MoveTo<CameraPostProcessing>(vcamObject, cameraObject);
        MoveTo<CinemachineBrain>(vcamObject, cameraObject);
    }

    // 只在组件真的挂错物体时才搬；目标上已有同类组件时以目标那份为准，丢掉错位的那份。
    private static void MoveTo<T>(GameObject from, GameObject to) where T : Component
    {
        if (from == to) return;
        T stray = from.GetComponent<T>();
        if (stray == null) return;
        if (to.GetComponent<T>() == null)
        {
            UnityEditorInternal.ComponentUtility.CopyComponent(stray);
            UnityEditorInternal.ComponentUtility.PasteComponentAsNew(to);
            Debug.Log($"[CameraRigSetup] {typeof(T).Name}: {from.name} → {to.name}（归属修正）");
        }
        UnityEngine.Object.DestroyImmediate(stray);
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
