using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// 一键生成灰盒验证场景：地面、蹬墙测试走廊、斜坡、限高门（测滑铲）、玩家（PlayerMotor）、主相机
// 菜单入口：超高速行者/生成灰盒场景；保存为 Assets/_Project/Scenes/Graybox.unity
// 场景内摆位数值是灰盒几何尺寸（关卡布局，不是手感参数），调手感一律改 MovementParams
public static class GrayboxSceneBuilder
{
    private const string ScenePath = "Assets/_Project/Scenes/Graybox.unity";
    private const string ParamsAssetPath = "Assets/_Project/Settings/MovementParams.asset";

    [MenuItem("超高速行者/生成灰盒场景")]
    public static void Build()
    {
        EnsureFolders();
        MovementParams movementParams = EnsureMovementParams();

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        CreateGround();
        CreateWallJumpCorridor();
        CreateRamp();
        CreateSlideGate(movementParams);
        CreatePlayer(movementParams);
        CreateCameraAndLight();
        new GameObject("TimeManager").AddComponent<TimeManager>();

        bool saved = EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log(saved ? "[GrayboxSceneBuilder] 灰盒场景已生成：" + ScenePath
                        : "[GrayboxSceneBuilder] 场景保存失败：" + ScenePath);
    }

    private static void EnsureFolders()
    {
        EnsureFolder("Assets", "_Project");
        EnsureFolder("Assets/_Project", "Scenes");
        EnsureFolder("Assets/_Project", "Settings");
    }

    private static void EnsureFolder(string parent, string name)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + name))
        {
            AssetDatabase.CreateFolder(parent, name);
        }
    }

    private static MovementParams EnsureMovementParams()
    {
        MovementParams asset = AssetDatabase.LoadAssetAtPath<MovementParams>(ParamsAssetPath);
        if (asset == null)
        {
            asset = ScriptableObject.CreateInstance<MovementParams>();
            AssetDatabase.CreateAsset(asset, ParamsAssetPath);
        }
        return asset;
    }

    private static void CreateGround()
    {
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = Vector3.zero;
        ground.transform.localScale = new Vector3(4f, 1f, 8f); // 40m x 80m
    }

    // 蹬墙测试：两条平行高墙形成走廊，供空中反复横跳蹬墙
    private static void CreateWallJumpCorridor()
    {
        CreateBox("WallJumpWall_Left", new Vector3(-3f, 2f, 8f), new Vector3(0.5f, 4f, 12f));
        CreateBox("WallJumpWall_Right", new Vector3(3f, 2f, 8f), new Vector3(0.5f, 4f, 12f));
    }

    private static void CreateRamp()
    {
        GameObject ramp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ramp.name = "Ramp";
        ramp.transform.position = new Vector3(10f, 0.9f, 0f);
        ramp.transform.rotation = Quaternion.Euler(-15f, 0f, 0f);
        ramp.transform.localScale = new Vector3(4f, 0.5f, 8f);
    }

    // 限高门：净空取“滑铲高度 + 余量”，站立高度无法通过
    private static void CreateSlideGate(MovementParams movementParams)
    {
        float clearance = movementParams.SlideCapsuleHeight + 0.3f;
        float lintelThickness = 1f;
        float z = 16f;

        CreateBox("GatePost_Left", new Vector3(-1.75f, 1f, z), new Vector3(0.5f, 2f, 0.5f));
        CreateBox("GatePost_Right", new Vector3(1.75f, 1f, z), new Vector3(0.5f, 2f, 0.5f));
        CreateBox("GateLintel", new Vector3(0f, clearance + lintelThickness * 0.5f, z),
            new Vector3(4f, lintelThickness, 0.5f));
    }

    private static void CreatePlayer(MovementParams movementParams)
    {
        GameObject player = new GameObject("Player");
        player.transform.position = new Vector3(0f, 0.1f, 0f);

        CharacterController controller = player.AddComponent<CharacterController>();
        controller.radius = movementParams.CapsuleBaseRadius;
        controller.height = movementParams.CapsuleBaseHeight;
        controller.center = new Vector3(0f, controller.height * 0.5f, 0f); // pivot 在脚底，与 PlayerMotor 约定一致

        PlayerMotor motor = player.AddComponent<PlayerMotor>();
        SerializedObject serializedMotor = new SerializedObject(motor);
        serializedMotor.FindProperty("_params").objectReferenceValue = movementParams;
        serializedMotor.ApplyModifiedPropertiesWithoutUndo();

        player.AddComponent<DebugHUD>(); // cl_showspeed 惯例的调试 HUD（F3 开关）
    }

    private static void CreateCameraAndLight()
    {
        // PlayerMotor 只读 Camera.main 的 yaw；后续由 Cinemachine 相机系统替换
        GameObject cameraGo = new GameObject("Main Camera");
        cameraGo.tag = "MainCamera";
        cameraGo.AddComponent<Camera>();
        cameraGo.AddComponent<AudioListener>();
        cameraGo.transform.position = new Vector3(0f, 3f, -8f);
        cameraGo.transform.rotation = Quaternion.Euler(20f, 0f, 0f);

        GameObject lightGo = new GameObject("Directional Light");
        Light light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
    }

    private static GameObject CreateBox(string name, Vector3 position, Vector3 scale)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.position = position;
        box.transform.localScale = scale;
        return box;
    }
}
