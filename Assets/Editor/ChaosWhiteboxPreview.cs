using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// 只离线渲染联调物件，不保存诊断机位或改变玩家实际出生位置。
public static class ChaosWhiteboxPreview
{
    public static void TidyAndCapture()
    {
        ChaosWhiteboxSetup.TidyIntegrationLabels();
        Capture();
    }

    public static void Capture()
    {
        if (EditorApplication.isPlaying || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            throw new System.InvalidOperationException("需退出播放并使用真实图形设备");
        EditorSceneManager.OpenScene(TeamWhiteboxSetup.GamePath);
        EditorSceneManager.OpenScene(TeamWhiteboxSetup.SceneFolder + "/Whitebox_Tutorial.unity", OpenSceneMode.Additive);
        foreach (PlayerMotor player in Object.FindObjectsOfType<PlayerMotor>()) player.gameObject.SetActive(false);
        ChaosRoomVariants variants = Object.FindObjectOfType<ChaosRoomVariants>();
        Camera camera = Camera.main;
        camera.enabled = true;
        camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
        camera.transform.position = variants.transform.position - variants.transform.forward * 5f + Vector3.up * 3f;
        camera.transform.LookAt(variants.transform.position + variants.transform.forward * 3f + Vector3.up * 1.5f);
        camera.fieldOfView = 65f;
        var target = new RenderTexture(1280, 720, 24); target.Create();
        RenderTexture previous = RenderTexture.active;
        try
        {
            camera.targetTexture = target;
            for (int index = 0; index < 3; index++)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ChaosWhiteboxSetup.AssetFolder + "/IntegrationVariant_" + index + ".prefab");
                GameObject content = Object.Instantiate(prefab, variants.transform, false);
                foreach (ChaosLamp lamp in Object.FindObjectsOfType<ChaosLamp>())
                {
                    lamp.transform.Find("完整装置").gameObject.SetActive(index == 0);
                    lamp.transform.Find("碎片").gameObject.SetActive(index != 0);
                }
                camera.Render();
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                RenderTexture.active = target;
                var pixels = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                pixels.ReadPixels(new Rect(0f, 0f, target.width, target.height), 0, 0); pixels.Apply();
                File.WriteAllBytes("Logs/ChaosWhiteboxVariant" + (char)('A' + index) + ".png", pixels.EncodeToPNG());
                Object.DestroyImmediate(pixels); Object.DestroyImmediate(content);
            }
        }
        finally
        {
            camera.targetTexture = null; RenderTexture.active = previous;
            target.Release(); Object.DestroyImmediate(target);
        }
        Debug.Log("CHAOS_PREVIEW_PASS: 三种差分联调物件已使用真实 URP 渲染；不是正式 UI 截图");
    }
}
