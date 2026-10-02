using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// 运行时探针：进 Play 模式实测机位是否真的跟随玩家
// 相机由 CinemachineBrain 驱动，这里只读 Camera.main 的实际 transform，不依赖任何业务脚本
// 只在批次排障时用，不属于业务代码
public static class FollowProbe
{
    private const string ScenePath = "Assets/Scenes/TestScene.unity";

    // 与 CinemachineFramingTransposer 的 TrackedObjectOffset.y 对齐，角度判定才有意义
    private const float TrackedHeight = 1.45f;

    private static int frames;
    private static GameObject player;
    private static Transform cam;
    private static Vector3 lastCameraPosition;

    [MenuItem("超高速行者/探针/相机跟随实测")]
    public static void Run()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        EditorApplication.update += Step;
        EditorApplication.EnterPlaymode();
    }

    private static void Step()
    {
        if (!EditorApplication.isPlaying)
        {
            return;
        }

        frames++;

        if (frames == 1)
        {
            player = GameObject.Find("Player");
            Camera mainCamera = Camera.main;
            cam = mainCamera != null ? mainCamera.transform : null;
            Debug.Log("[Probe] player=" + (player != null) + " camera=" + (cam != null));

            if (player != null)
            {
                PlayerMotor motor = player.GetComponent<PlayerMotor>();
                PlayerInputReader input = player.GetComponent<PlayerInputReader>();
                PlayerCameraRig rig = player.GetComponent<PlayerCameraRig>();
                SpeedCameraFeedback feedback = player.GetComponent<SpeedCameraFeedback>();
                CharacterController controller = player.GetComponent<CharacterController>();
                // PlayerMotor 从同物体取 IPlayerInput；缺 PlayerInputReader 会静默吃空输入帧（角色不动）
                Debug.Log("[Probe] PlayerMotor=" + (motor != null) + " enabled=" + (motor != null && motor.enabled)
                    + " cc.enabled=" + (controller != null && controller.enabled)
                    + " input=" + (input != null)
                    + " cameraRig=" + (rig != null)
                    + " speedFeedback=" + (feedback != null));
            }

            return;
        }

        if (cam == null || player == null)
        {
            Debug.LogError("[Probe] 缺少 player 或 Main Camera，探针中止（检查场景是否被 CinemachineBrain 接管）");
            Finish(1);
            return;
        }

        if (frames == 20)
        {
            lastCameraPosition = cam.position;
            Debug.Log("[Probe] 静止: player=" + player.transform.position + " cam=" + cam.position
                + " 水平距离=" + HorizontalDistance());
            // 手动瞬移玩家，不依赖输入
            player.transform.position += new Vector3(20f, 0f, 0f);
            return;
        }

        if (frames == 40)
        {
            Debug.Log("[Probe] 瞬移X+20 后: player=" + player.transform.position + " cam=" + cam.position
                + " 水平距离=" + HorizontalDistance()
                + " 相机位移=" + (cam.position - lastCameraPosition).magnitude.ToString("F2"));
            lastCameraPosition = cam.position;
            player.transform.position += new Vector3(0f, 0f, 25f);
            return;
        }

        if (frames == 60)
        {
            Debug.Log("[Probe] 二次瞬移Z+25 后: player=" + player.transform.position + " cam=" + cam.position
                + " 水平距离=" + HorizontalDistance()
                + " 相机位移=" + (cam.position - lastCameraPosition).magnitude.ToString("F2"));
            Debug.Log("[Probe] 相机朝向与玩家夹角=" + Vector3.Angle(cam.forward,
                (player.transform.position + Vector3.up * TrackedHeight) - cam.position).ToString("F1")
                + " 度（越肩 POV 机位不为 0 属正常）");
            Finish(0);
        }
    }

    private static void Finish(int exitCode)
    {
        EditorApplication.update -= Step;
        EditorApplication.ExitPlaymode();
        EditorApplication.Exit(exitCode);
    }

    private static string HorizontalDistance()
    {
        Vector3 delta = cam.position - player.transform.position;
        delta.y = 0f;
        return delta.magnitude.ToString("F2");
    }
}
