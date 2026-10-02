using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// 运行时探针：进 Play 模式实测相机是否真的跟随玩家
// 只在批次排障时用，不属于业务代码
public static class FollowProbe
{
    private const string ScenePath = "Assets/Scenes/TestScene.unity";

    private static int frames;
    private static GameObject player;
    private static CameraController controller;
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
            controller = Object.FindObjectOfType<CameraController>();
            Debug.Log("[Probe] player=" + (player != null) + " controller=" + (controller != null));
            if (controller != null)
            {
                Debug.Log("[Probe] controller.enabled=" + controller.enabled
                    + " isActiveAndEnabled=" + controller.isActiveAndEnabled
                    + " playerRef=" + (controller.player != null)
                    + " volumeRef=" + (controller.volume != null));
            }

            if (player != null)
            {
                CharacterMovement movement = player.GetComponent<CharacterMovement>();
                Debug.Log("[Probe] CharacterMovement=" + (movement != null)
                    + " cc.enabled=" + player.GetComponent<CharacterController>().enabled);
            }

            return;
        }

        if (frames == 20)
        {
            lastCameraPosition = controller.transform.position;
            Debug.Log("[Probe] 静止: player=" + player.transform.position + " cam=" + controller.transform.position
                + " 水平距离=" + HorizontalDistance());
            // 手动瞬移玩家，不依赖输入
            player.transform.position += new Vector3(20f, 0f, 0f);
            return;
        }

        if (frames == 40)
        {
            Debug.Log("[Probe] 瞬移X+20 后: player=" + player.transform.position + " cam=" + controller.transform.position
                + " 水平距离=" + HorizontalDistance()
                + " 相机位移=" + (controller.transform.position - lastCameraPosition).magnitude.ToString("F2"));
            lastCameraPosition = controller.transform.position;
            player.transform.position += new Vector3(0f, 0f, 25f);
            return;
        }

        if (frames == 60)
        {
            Debug.Log("[Probe] 二次瞬移Z+25 后: player=" + player.transform.position + " cam=" + controller.transform.position
                + " 水平距离=" + HorizontalDistance()
                + " 相机位移=" + (controller.transform.position - lastCameraPosition).magnitude.ToString("F2"));
            Debug.Log("[Probe] 相机朝向与目标夹角=" + Vector3.Angle(controller.transform.forward,
                (player.transform.position + Vector3.up * 1.2f) - controller.transform.position).ToString("F1") + " 度（0=正对）");
            EditorApplication.update -= Step;
            EditorApplication.ExitPlaymode();
            EditorApplication.Exit(0);
        }
    }

    private static string HorizontalDistance()
    {
        Vector3 delta = controller.transform.position - player.transform.position;
        delta.y = 0f;
        return delta.magnitude.ToString("F2");
    }
}
