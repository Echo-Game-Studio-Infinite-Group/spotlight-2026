using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// 一键生成灰盒验证场景：网格地面、蹬墙测试走廊、斜坡、限高门（测滑铲）、可见玩家、跟随相机
// 菜单入口：超高速行者/生成灰盒场景；保存为 Assets/_Project/Scenes/Graybox.unity
// 场景内摆位数值是灰盒几何尺寸（关卡布局，不是手感参数），调手感一律改 MovementParams
// 可读性约定（灰盒惯例）：中灰材质+受控对比度，玩家用自发光橙+青色朝向鼻，速度感靠地面网格读出
public static class GrayboxSceneBuilder
{
    private const string ScenePath = "Assets/_Project/Scenes/Graybox.unity";
    private const string ParamsAssetPath = "Assets/_Project/Settings/MovementParams.asset";
    private const string MaterialsFolder = "Assets/_Project/Materials";
    private const string GridTexturePath = MaterialsFolder + "/GrayGrid.asset";

    [MenuItem("超高速行者/生成灰盒场景")]
    public static void Build()
    {
        EnsureFolders();
        MovementParams movementParams = EnsureMovementParams();
        Materials mats = EnsureMaterials();

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // 灰盒对比度基线：平面环境光 + 暗色背景，避免默认天空盒把画面洗白
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.52f, 0.54f, 0.58f);

        CreateGround(mats);
        CreateWallJumpCorridor(mats);
        CreateRamp(mats);
        CreateSlideGate(mats, movementParams);
        Transform player = CreatePlayer(mats, movementParams);
        CreateCameraAndLight(player);

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
        EnsureFolder("Assets/_Project", "Materials");
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

    private struct Materials
    {
        public Material Ground;
        public Material Wall;
        public Material Lintel;
        public Material Player;
        public Material Nose;
    }

    // 材质作为资产落盘：场景渲染器引用资产而不是内联对象，保证跨打开/入库后不丢引用
    private static Materials EnsureMaterials()
    {
        return new Materials
        {
            Ground = EnsureMaterial("GrayMat_Ground", m =>
            {
                m.mainTexture = EnsureGridTexture();
                m.color = new Color(0.42f, 0.43f, 0.45f);
                m.mainTextureScale = new Vector2(10f, 20f); // 地面 40x80m，纹理覆盖 4m → 副格线 2m、主格线 4m
            }),
            Wall = EnsureMaterial("GrayMat_Wall", m => m.color = new Color(0.58f, 0.52f, 0.46f)),
            Lintel = EnsureMaterial("GrayMat_Lintel", m => m.color = new Color(0.75f, 0.25f, 0.2f)),
            Player = EnsureMaterial("GrayMat_Player", m =>
            {
                m.color = new Color(1f, 0.5f, 0.1f);
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", new Color(0.8f, 0.35f, 0.05f)); // 自发光保证任何光照下可见
            }),
            Nose = EnsureMaterial("GrayMat_Nose", m =>
            {
                m.color = new Color(0.1f, 0.95f, 1f);
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", new Color(0f, 0.7f, 0.8f));
            }),
        };
    }

    private static Material EnsureMaterial(string name, System.Action<Material> setup)
    {
        string path = MaterialsFolder + "/" + name + ".mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(mat, path);
        }
        setup(mat);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // 程序化 2m 网格纹理：小格线 + 每 4 格一条主格线，主格线是读速度与距离的标尺
    private static Texture2D EnsureGridTexture()
    {
        Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(GridTexturePath);
        if (tex != null) return tex;

        const int size = 512;   // 纹理覆盖 4m：两条副格线（每 2m）+ 一条主格线（每 4m）
        const int cell = 256;
        tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
        };
        Color bg = new Color(0.85f, 0.85f, 0.85f);
        Color line = new Color(0.55f, 0.55f, 0.55f);
        Color major = new Color(0.3f, 0.3f, 0.3f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool onGrid = x % cell < 2 || y % cell < 2;
                bool isMajor = x < 3 || y < 3;
                tex.SetPixel(x, y, onGrid ? (isMajor ? major : line) : bg);
            }
        }
        tex.Apply();
        AssetDatabase.CreateAsset(tex, GridTexturePath);
        return tex;
    }

    private static void CreateGround(Materials mats)
    {
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = Vector3.zero;
        ground.transform.localScale = new Vector3(4f, 1f, 8f); // 40m x 80m
        ground.GetComponent<MeshRenderer>().sharedMaterial = mats.Ground;
    }

    // 蹬墙测试：两条平行高墙形成走廊，供空中反复横跳蹬墙
    private static void CreateWallJumpCorridor(Materials mats)
    {
        CreateBox("WallJumpWall_Left", new Vector3(-3f, 2f, 8f), new Vector3(0.5f, 4f, 12f), mats.Wall);
        CreateBox("WallJumpWall_Right", new Vector3(3f, 2f, 8f), new Vector3(0.5f, 4f, 12f), mats.Wall);
    }

    private static void CreateRamp(Materials mats)
    {
        GameObject ramp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ramp.name = "Ramp";
        ramp.transform.position = new Vector3(10f, 0.9f, 0f);
        ramp.transform.rotation = Quaternion.Euler(-15f, 0f, 0f);
        ramp.transform.localScale = new Vector3(4f, 0.5f, 8f);
        ramp.GetComponent<MeshRenderer>().sharedMaterial = mats.Wall;
    }

    // 限高门：净空取“滑铲高度 + 余量”，站立高度无法通过
    private static void CreateSlideGate(Materials mats, MovementParams movementParams)
    {
        float clearance = movementParams.SlideCapsuleHeight + 0.3f;
        float lintelThickness = 1f;
        float z = 16f;

        CreateBox("GatePost_Left", new Vector3(-1.75f, 1f, z), new Vector3(0.5f, 2f, 0.5f), mats.Wall);
        CreateBox("GatePost_Right", new Vector3(1.75f, 1f, z), new Vector3(0.5f, 2f, 0.5f), mats.Wall);
        CreateBox("GateLintel", new Vector3(0f, clearance + lintelThickness * 0.5f, z),
            new Vector3(4f, lintelThickness, 0.5f), mats.Lintel);
    }

    private static Transform CreatePlayer(Materials mats, MovementParams movementParams)
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

        // 可见玩家：自发光胶囊（随受击框变细同步）+ 青色朝向鼻（读转向）
        GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        visual.name = "Visual";
        Object.DestroyImmediate(visual.GetComponent<CapsuleCollider>()); // 碰撞一律归 CharacterController
        visual.GetComponent<MeshRenderer>().sharedMaterial = mats.Player;
        visual.transform.SetParent(player.transform, false);

        CapsuleVisualSync sync = player.AddComponent<CapsuleVisualSync>();
        SerializedObject serializedSync = new SerializedObject(sync);
        serializedSync.FindProperty("_visual").objectReferenceValue = visual.transform;
        serializedSync.ApplyModifiedPropertiesWithoutUndo();

        GameObject nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
        nose.name = "Nose";
        Object.DestroyImmediate(nose.GetComponent<BoxCollider>());
        nose.GetComponent<MeshRenderer>().sharedMaterial = mats.Nose;
        nose.transform.SetParent(player.transform, false);
        nose.transform.localPosition = new Vector3(0f, 1.1f, 0.6f);
        nose.transform.localScale = new Vector3(0.12f, 0.12f, 0.4f);

        return player.transform;
    }

    private static void CreateCameraAndLight(Transform player)
    {
        GameObject cameraGo = new GameObject("Main Camera");
        cameraGo.tag = "MainCamera";
        Camera camera = cameraGo.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.13f, 0.14f, 0.17f); // 暗色实底，避免天空盒洗白
        cameraGo.AddComponent<AudioListener>();

        GrayboxFollowCamera follow = cameraGo.AddComponent<GrayboxFollowCamera>();
        SerializedObject serializedFollow = new SerializedObject(follow);
        serializedFollow.FindProperty("_target").objectReferenceValue = player;
        serializedFollow.ApplyModifiedPropertiesWithoutUndo();

        GameObject lightGo = new GameObject("Directional Light");
        Light light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.0f;
        light.color = new Color(1f, 0.97f, 0.92f);
        lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
    }

    private static GameObject CreateBox(string name, Vector3 position, Vector3 scale, Material material)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = name;
        box.transform.position = position;
        box.transform.localScale = scale;
        box.GetComponent<MeshRenderer>().sharedMaterial = material;
        return box;
    }
}
