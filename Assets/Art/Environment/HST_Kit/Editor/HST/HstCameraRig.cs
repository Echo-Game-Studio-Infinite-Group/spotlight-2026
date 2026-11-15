using System.Linq;
using Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// 换上试验场里现成的第三人称相机跟随 (2026-10-13, 用户裁定"这不是现成的摄像头跟随吗")
// 团队口径 (对齐 project-1/dev: MainGame/CombatTest/LevelTest_Cdwy/BOX_S1013 都是 rig=1+brain=1+vcam=1):
//   Assets/Prefabs/Main Camera.prefab   = Camera + URP附加数据 + CinemachineBrain
//                                         + PlayerCameraRig(鼠标yaw/pitch, 读 PlayerInputReader.LookDelta, 灵敏度0.15)
//                                         + CameraPostProcessing(速度FOV/贴墙拉近, 作用对象是 vcam) + Wwise 件
//   Assets/Prefabs/Virtual Camera.prefab = CinemachineVirtualCamera(Priority 10)
//                                         + 管线 Cinemachine3rdPersonFollow(带避障第三人称臂)
// 用法: 实例化这两个正式预制体 → 完全解包成普通对象(不挂预制体链接, 免悬空引用) → 接线到 Player 的 CameraTarget。
// 此前误建 CinemachineFreeLook(默认轨道/默认灵敏度) 与这套行为完全不同, 已由本工具换掉。
// 注意: PlayerCameraRig 的 _motor/_input 运行时也会从 PlayerMotor.Active 自取; 这里照 CombatTest 的
//       做法(该场景是接线完整的活例)显式接线, 保持场景内引用完整。
public static class HstCameraRig
{
    const string CamPrefab = "Assets/Prefabs/Main Camera.prefab";
    const string VcamPrefab = "Assets/Prefabs/Virtual Camera.prefab";
    const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";

    static readonly string[] Halls =
    {
        "Assets/Art/Environment/HST_Kit/Scenes/SC_Atrium_Military.unity",
        "Assets/Art/Environment/HST_Kit/Scenes/SC_Atrium_Concept.unity",
    };
    // 走廊没有任何代码加载/生成玩家(2026-10-13 全库检索), 必须自包含。
    // 出生位取 4e04023 权威版被误删前的原坐标(38.5, 0.1, 0) 朝 -90°。
    const string Corridor = "Assets/Art/Environment/HST_Kit/Scenes/SC_Corridor_Pragmata.unity";
    static readonly Vector3 CorridorSpawn = new Vector3(38.5f, 0.1f, 0f);
    static readonly Quaternion CorridorSpawnRot = Quaternion.Euler(0f, -90f, 0f);

    [MenuItem("HST/场景 换试验场现成相机跟随(两厅)")]
    public static void Run()
    {
        foreach (var hall in Halls) FixScene(hall, null, Quaternion.identity);
        Debug.Log("[HCR] 全部完成");
    }

    [MenuItem("HST/场景 换试验场现成相机跟随(走廊, 补回玩家)")]
    public static void RunCorridor()
    {
        FixScene(Corridor, CorridorSpawn, CorridorSpawnRot);
        Debug.Log("[HCR] 全部完成");
    }

    // public: 供 HstBlastDoorTestScene(爆门试验场)复用同一套"玩家 + 现成相机 rig"接线,
    // 不另写第二份(改一处即两处生效)。
    public static void FixScene(string path, Vector3? spawnPos, Quaternion spawnRot)
    {
        EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

        // ---- 1) 按名字清掉此前误建的东西(FreeLook / 旧临时名) ----
        // 只在两厅跑; 走廊与 QA 机位(CAM_*)不叫这些名字, 不受影响。
        int removed = 0;
        foreach (var fl in Object.FindObjectsOfType<CinemachineFreeLook>(true))
        { Object.DestroyImmediate(fl.gameObject); removed++; }
        var hstCam = GameObject.Find("HST_FollowCamera");
        if (hstCam != null) { Object.DestroyImmediate(hstCam); removed++; }

        // ---- 2) 玩家实例(最外层根)与 CameraTarget ----
        // 遍历可能先抓到实例的子物体(如 Hitbox), CameraTarget 挂在实例根下, 必须归到最外层根再找。
        GameObject player = null;
        foreach (var g in Object.FindObjectsOfType<GameObject>(true))
            if (PrefabUtility.IsPartOfPrefabInstance(g)
                && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(g) == PlayerPrefabPath)
            { player = PrefabUtility.GetOutermostPrefabInstanceRoot(g); break; }
        if (player == null) player = GameObject.Find("Player");
        if (player == null && spawnPos.HasValue)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            player = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (player != null)
            {
                player.transform.SetPositionAndRotation(spawnPos.Value, spawnRot);
                player.name = "Player";
                Debug.Log($"[HCR] {path} 玩家实例缺失, 按权威版原位补建 @{spawnPos.Value:F1}");
            }
        }
        if (player == null)
        {
            Debug.LogError($"[HCR] {path} 没有玩家实例且未给出生位, 跳过(不存盘)");
            return;
        }
        Transform target = player.transform.Find("CameraTarget");
        if (target == null)
        {
            Debug.LogError($"[HCR] {path} 玩家根下没有 CameraTarget 子节点, 跳过(不存盘)");
            return;
        }

        // ---- 3) 取或建 rig 两件套(幂等: 重跑只重新接线), 完全解包不挂链接 ----
        var camGo = Object.FindObjectsOfType<Camera>(true)
            .FirstOrDefault(c => c.GetComponent<PlayerCameraRig>() != null && c.name == "Main Camera")?.gameObject;
        var vcam = Object.FindObjectsOfType<CinemachineVirtualCamera>(true).FirstOrDefault(v => v.name == "Virtual Camera");
        if (camGo == null)
        {
            var camPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CamPrefab);
            camGo = (GameObject)PrefabUtility.InstantiatePrefab(camPrefab);
            if (camGo == null) { Debug.LogError($"[HCR] {path} Main Camera.prefab 实例化失败"); return; }
            PrefabUtility.UnpackPrefabInstance(camGo, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        }
        if (vcam == null)
        {
            var vcamPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(VcamPrefab);
            var vcamGo = (GameObject)PrefabUtility.InstantiatePrefab(vcamPrefab);
            if (vcamGo == null) { Debug.LogError($"[HCR] {path} Virtual Camera.prefab 实例化失败"); return; }
            PrefabUtility.UnpackPrefabInstance(vcamGo, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            vcam = vcamGo.GetComponentInChildren<CinemachineVirtualCamera>(true);
        }
        camGo.name = "Main Camera";
        vcam.gameObject.name = "Virtual Camera";

        // 摆位只影响编辑器观感: Play 后 3rdPersonFollow 按目标臂长取位
        vcam.transform.position = target.position + new Vector3(0.5f, 0.4f, -3.5f);
        camGo.transform.position = vcam.transform.position;

        // ---- 4) 接线 (私有 SerializeField 走 SerializedObject) ----
        var motor = player.GetComponent<PlayerMotor>();
        var input = motor != null ? motor.GetComponent<PlayerInputReader>() : null;

        var rig = camGo.GetComponent<PlayerCameraRig>();
        var so = new SerializedObject(rig);
        so.FindProperty("_motor").objectReferenceValue = motor;
        so.FindProperty("_input").objectReferenceValue = input;
        so.FindProperty("_target").objectReferenceValue = target;
        so.FindProperty("_virtualCamera").objectReferenceValue = vcam;
        so.ApplyModifiedPropertiesWithoutUndo();

        var cpp = camGo.GetComponent<CameraPostProcessing>();
        if (cpp != null)
        {
            var so2 = new SerializedObject(cpp);
            so2.FindProperty("_motor").objectReferenceValue = motor;
            so2.FindProperty("_virtualCamera").objectReferenceValue = vcam;
            // _volume 留空: 两厅是零 Volume 场景(纪律4), CameraPostProcessing.Start 对 null 有兜底
            so2.ApplyModifiedPropertiesWithoutUndo();
        }

        vcam.Follow = target;
        vcam.LookAt = target;
        EditorUtility.SetDirty(vcam);

        // ---- 5) 健康守卫: 达标才存盘 (坑15: 存盘+日志必须落地) ----
        var activeCams = Object.FindObjectsOfType<Camera>().Where(c => c.enabled && c.isActiveAndEnabled).ToList();
        var brains = Object.FindObjectsOfType<CinemachineBrain>();
        var followOk = vcam.Follow != null && vcam.LookAt != null;
        var wired = new SerializedObject(rig).FindProperty("_target").objectReferenceValue != null;
        if (activeCams.Count != 1 || brains.Length != 1 || !followOk || !wired)
        {
            foreach (var c in Object.FindObjectsOfType<Camera>(true))
                Debug.Log($"[HCR] {path} 相机清单: {c.name} enabled={c.enabled} activeInHierarchy={c.gameObject.activeInHierarchy}");
            Debug.LogError($"[HCR] {path} 守卫未过: 启用相机{activeCams.Count}(应1) Brain{brains.Length}(应1) vcam接线={followOk} rig接线={wired}, 不存盘");
            return;
        }

        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[HCR] {path} 已装现成相机rig(Main Camera+Virtual Camera, Player@{player.transform.position:F1}), 场景已存盘");
    }
}
