using System.IO;
using UnityEditor;
using UnityEngine;

public class ScreenCaptureEditor : EditorWindow
{
    private static string directory = "Screenshots/Capture/";
    private static string latestScreenshotPath = "";
    private bool initDone = false;

    private GUIStyle BigText;
    
    // 新增：目标截图高度（4K为2160，8K为4320）
    private int targetHeight = 2160; 

    void InitStyles()
    {
        initDone = true;
        BigText = new GUIStyle(GUI.skin.label)
        {
            fontSize = 20,
            fontStyle = FontStyle.Bold
        };
    }

    private void OnGUI()
    {
        if (!initDone) InitStyles();

        GUILayout.Label("Screen Capture Tools", BigText);
        
        // 设置输出高度
        GUILayout.BeginHorizontal();
        GUILayout.Label("目标截图高度(像素):", GUILayout.Width(120));
        targetHeight = EditorGUILayout.IntField(targetHeight);
        if (GUILayout.Button("4K", GUILayout.Width(40))) targetHeight = 2160;
        if (GUILayout.Button("8K", GUILayout.Width(40))) targetHeight = 4320;
        GUILayout.EndHorizontal();

        GUILayout.Space(5);

        if (GUILayout.Button("截取 Scene 窗口 (超清无损，无 Gizmos)", GUILayout.Height(30)))
        {
            CaptureSceneViewHighRes();
        }

        if (GUILayout.Button("单张截图 (Game视图/原尺寸)"))
        {
            TakeScreenshot();
        }

        if (GUILayout.Button("打开文件夹"))
        {
            ShowFolder();
        }
        GUILayout.Label("保存路径: " + Path.GetFullPath(directory));
    }

    [MenuItem("Tools/Screenshots/打开窗口 &`", false, 0)]
    public static void ShowWindow()
    {
        GetWindow(typeof(ScreenCaptureEditor));
    }

    [MenuItem("Tools/Screenshots/存储路径 &2", false, 2)]
    private static void ShowFolder()
    {
        if (File.Exists(latestScreenshotPath))
        {
            EditorUtility.RevealInFinder(latestScreenshotPath);
            return;
        }
        Directory.CreateDirectory(directory);
        EditorUtility.RevealInFinder(directory);
    }

    [MenuItem("Tools/Screenshots/单张截图 &1", false, 1)]
    private static void TakeScreenshot()
    {
        string absoluteDir = Path.GetFullPath(directory);
        Directory.CreateDirectory(absoluteDir);
        var path = Path.Combine(absoluteDir, System.DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss") + "_Game.png");
        latestScreenshotPath = path;
        
        ScreenCapture.CaptureScreenshot(path);
        Debug.Log($"Game视图截图: <b>{path}</b>");
    }

    // 核心：超分辨率截取 Scene 窗口
    private void CaptureSceneViewHighRes()
    {
        SceneView view = SceneView.lastActiveSceneView;
        if (view == null)
        {
            Debug.LogError("找不到激活的 Scene 窗口！");
            return;
        }

        Camera cam = view.camera;
        
        // 获取当前窗口的宽高比
        float currentAspect = (float)view.position.width / view.position.height;

        // 根据目标高度计算目标宽度，保持画面比例完全不变
        int targetW = Mathf.RoundToInt(targetHeight * currentAspect);
        int targetH = targetHeight;

        // 限制一下防止爆显存（最高支持 16K）
        if (targetW > 16384) targetW = 16384;
        if (targetH > 16384) targetH = 16384;

        // 保存相机原有的状态，截图后恢复，防止影响编辑器显示
        RenderTexture originalRT = cam.targetTexture;
        float originalAspect = cam.aspect;

        // 创建超高清 RT (使用 24位深度，抗锯齿可选)
        RenderTexture rt = new RenderTexture(targetW, targetH, 24, RenderTextureFormat.ARGB32);
        rt.antiAliasing = 8; // 开启 8x 抗锯齿，让线更平滑

        cam.targetTexture = rt;
        cam.aspect = currentAspect;

        // 强制渲染一次
        cam.Render();

        // 保存图片
        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(targetW, targetH, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
        tex.Apply();
        RenderTexture.active = null;

        // 恢复相机状态
        cam.targetTexture = originalRT;
        cam.aspect = originalAspect;

        // 写入文件
        string absoluteDir = Path.GetFullPath(directory);
        Directory.CreateDirectory(absoluteDir);
        var path = Path.Combine(absoluteDir, System.DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss") + $"_Scene_{targetW}x{targetH}.png");

        byte[] bytes = tex.EncodeToPNG();
        File.WriteAllBytes(path, bytes);

        // 清理内存
        DestroyImmediate(rt);
        DestroyImmediate(tex);

        Debug.Log($"超清 Scene 截图已保存: <b>{path}</b> 分辨率: {targetW}x{targetH}");
        
        // 截图后自动弹出文件夹
        EditorUtility.RevealInFinder(path);
    }
}