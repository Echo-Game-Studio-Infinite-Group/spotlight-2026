using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// 用实际 URP 渲染检查接缝，输出只写入被忽略的 Logs，不保存诊断机位到关卡。
public static class WhiteboxSeamPreview
{
    public static void Capture()
    {
        if (UnityEditor.EditorApplication.isPlaying) throw new System.InvalidOperationException("请先停止播放");
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            throw new System.InvalidOperationException("检查图需要真实图形设备，不能使用 -nographics");
        EditorSceneManager.OpenScene(TeamWhiteboxSetup.GamePath);
        EditorSceneManager.OpenScene(TeamWhiteboxSetup.SceneFolder + "/Whitebox_Tutorial.unity", OpenSceneMode.Additive);
        EditorSceneManager.OpenScene(TeamWhiteboxSetup.SceneFolder + "/Whitebox_Room_01.unity", OpenSceneMode.Additive);
        foreach (Transform item in Object.FindObjectsOfType<Transform>())
            if (item.name.Contains("木门") || item.name == "Player") item.gameObject.SetActive(false);
        foreach (MapSeamSurface seam in Object.FindObjectsOfType<MapSeamSurface>())
            seam.GetComponent<MeshFilter>().sharedMesh = UnityEngine.SceneManagement.SceneManager.GetSceneByName(seam.NeighborSceneName).isLoaded
                ? seam.NeighborLoadedMesh : seam.OwnerOnlyMesh;
        Camera camera = Camera.main;
        camera.enabled = true;
        camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
        camera.fieldOfView = 65f;
        var texture = new RenderTexture(1280, 720, 24);
        texture.Create();
        RenderTexture previous = RenderTexture.active;
        try
        {
            camera.targetTexture = texture;
            for (int view = 0; view < 3; view++)
            {
                camera.transform.position = new Vector3(79.75f + view * .25f, 3f, 4f);
                camera.transform.LookAt(new Vector3(80f, .3f, -3.5f));
                camera.Render();
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = texture });
                RenderTexture.active = texture;
                var image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                image.Apply();
                File.WriteAllBytes("Logs/WhiteboxSeamAfter" + view + ".png", image.EncodeToPNG());
                Object.DestroyImmediate(image);
            }
        }
        finally
        {
            RenderTexture.active = previous;
            camera.targetTexture = null;
            texture.Release();
            Object.DestroyImmediate(texture);
        }
        Debug.Log("WHITEBOX_SEAM_PREVIEW_PASS: 已导出三个真实渲染机位");
    }
}
