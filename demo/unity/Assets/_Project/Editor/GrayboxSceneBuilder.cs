using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// 一键生成灰盒验证场景（高速游戏规格）：
//   巨幅网格地面（142x117m，2m/4m 格线）+ 椭圆环形跑道（双护栏 + 每10m红色刻度条）
//   中央测试区（蹬墙走廊/斜坡/限高门）+ 外围挡墙 + 可见玩家 + 跟随相机 + 掉落复活
// 菜单入口：超高速行者/生成灰盒场景；保存为 Assets/_Project/Scenes/Graybox.unity
// 场地尺寸由高速物理倒推：护栏 3m（跳跃上抛 1.6m 无法越出）、外围墙 6m（蹬墙链余量）；
// 转弯半径 r=v²/a 决定环道半径：阈值 10m/s 需 r≈10m，环道中心线 58x45m 可容纳约 2-3 档速度
public static class GrayboxSceneBuilder
{
    private const string ScenePath = "Assets/_Project/Scenes/Graybox.unity";
    private const string ParamsAssetPath = "Assets/_Project/Settings/MovementParams.asset";
    private const string MaterialsFolder = "Assets/_Project/Materials";
    private const string GridTexturePath = MaterialsFolder + "/GrayGrid.asset";

    // 环道几何（中心线椭圆 + 内外护栏）
    private const float RingA = 58f;
    private const float RingB = 45f;
    private const float TrackHalfWidth = 3f;
    private const int RailSegments = 48;
    private const int MarkerCount = 32; // 中心线周长约 320m → 每条约 10m

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
        CreateRingTrack(mats);
        CreatePerimeter(mats);
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
                m.mainTextureScale = new Vector2(35.5f, 29.25f); // 地面 142x117m，纹理覆盖 4m → 副格线 2m、主格线 4m
            }),
            Wall = EnsureMaterial("GrayMat_Wall", m => m.color = new Color(0.58f, 0.52f, 0.46f)),
            Lintel = EnsureMaterial("GrayMat_Lintel", m => m.color = new Color(0.75f, 0.25f, 0.2f)),
            Player = EnsureMaterial("GrayMat_Player", m =>
            {
                m.color = new Color(1f, 0.5f, 0.1f);
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", new Color(0.8f, 0.35f, 0.05f)); // 自发光保证任何光照下可见
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

    // 程序化网格纹理：副格线每 2m、主格线每 4m——速度与距离的读数标尺
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

    // 巨幅地面：142x117m 铺满到外围墙脚下——场地内没有任何虚空，掉不下去
    private static void CreateGround(Materials mats)
    {
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = Vector3.zero;
        ground.transform.localScale = new Vector3(14.2f, 1f, 11.7f); // Plane 原型 10x10m
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

    // 限高门：净空取"滑铲高度 + 余量"，站立高度无法通过
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

    // 椭圆环道：内外双护栏（3m 高，跳跃上抛仅 1.6m 越不出去）+ 中心线红色刻度条（约每 10m）
    private static void CreateRingTrack(Materials mats)
    {
        CreateEllipseRail(mats.Wall, RingA - TrackHalfWidth, RingB - TrackHalfWidth);
        CreateEllipseRail(mats.Wall, RingA + TrackHalfWidth, RingB + TrackHalfWidth);

        for (int i = 0; i < MarkerCount; i++)
        {
            float t = i / (float)MarkerCount * Mathf.PI * 2f;
            float tNext = (i + 1) / (float)MarkerCount * Mathf.PI * 2f;
            Vector3 p = Ellipse(RingA, RingB, t);
            Vector3 pNext = Ellipse(RingA, RingB, tNext);
            float yaw = Mathf.Atan2(pNext.x - p.x, pNext.z - p.z) * Mathf.Rad2Deg;

            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = "TrackMarker_" + i;
            Object.DestroyImmediate(marker.GetComponent<BoxCollider>()); // 地面刻度不需要碰撞
            marker.transform.SetPositionAndRotation(new Vector3(p.x, 0.011f, p.z), Quaternion.Euler(0f, yaw, 0f));
            marker.transform.localScale = new Vector3(TrackHalfWidth * 2f * 0.8f, 0.02f, 0.25f);
            marker.GetComponent<MeshRenderer>().sharedMaterial = mats.Lintel;
        }
    }

    private static void CreateEllipseRail(Material material, float a, float b)
    {
        for (int i = 0; i < RailSegments; i++)
        {
            float t0 = i / (float)RailSegments * Mathf.PI * 2f;
            float t1 = (i + 1) / (float)RailSegments * Mathf.PI * 2f;
            Vector3 p0 = Ellipse(a, b, t0);
            Vector3 p1 = Ellipse(a, b, t1);
            Vector3 mid = (p0 + p1) * 0.5f;
            float length = Vector3.Distance(p0, p1) + 0.4f; // 微重叠防缝隙
            float yaw = Mathf.Atan2(p1.x - p0.x, p1.z - p0.z) * Mathf.Rad2Deg;

            GameObject seg = GameObject.CreatePrimitive(PrimitiveType.Cube);
            seg.name = "Rail";
            seg.transform.SetPositionAndRotation(new Vector3(mid.x, 1.5f, mid.z), Quaternion.Euler(0f, yaw, 0f));
            seg.transform.localScale = new Vector3(0.4f, 3f, length);
            seg.GetComponent<MeshRenderer>().sharedMaterial = material;
        }
    }

    private static Vector3 Ellipse(float a, float b, float t)
    {
        return new Vector3(a * Mathf.Sin(t), 0f, b * Mathf.Cos(t));
    }

    // 外围挡墙 6m：兜住蹬墙链等超高过冲，与地面边缘相接——封闭场地
    private static void CreatePerimeter(Materials mats)
    {
        CreateBox("Perimeter_N", new Vector3(0f, 3f, 57f), new Vector3(142f, 6f, 1f), mats.Wall);
        CreateBox("Perimeter_S", new Vector3(0f, 3f, -57f), new Vector3(142f, 6f, 1f), mats.Wall);
        CreateBox("Perimeter_E", new Vector3(70.5f, 3f, 0f), new Vector3(1f, 6f, 115f), mats.Wall);
        CreateBox("Perimeter_W", new Vector3(-70.5f, 3f, 0f), new Vector3(1f, 6f, 115f), mats.Wall);
    }

    private static Transform CreatePlayer(Materials mats, MovementParams movementParams)
    {
        GameObject player = new GameObject("Player");
        player.transform.position = new Vector3(0f, 0.1f, -RingB); // 出生在环道南侧直道，起步即沿环

        CharacterController controller = player.AddComponent<CharacterController>();
        controller.radius = movementParams.CapsuleBaseRadius;
        controller.height = movementParams.CapsuleBaseHeight;
        controller.center = new Vector3(0f, controller.height * 0.5f, 0f); // pivot 在脚底，与 PlayerMotor 约定一致

        PlayerMotor motor = player.AddComponent<PlayerMotor>();
        SerializedObject serializedMotor = new SerializedObject(motor);
        serializedMotor.FindProperty("_params").objectReferenceValue = movementParams;
        serializedMotor.ApplyModifiedPropertiesWithoutUndo();

        player.AddComponent<DebugHUD>();       // cl_showspeed 惯例的调试 HUD（F3 开关）
        player.AddComponent<PlayerRespawn>();  // 掉出世界秒回出生点

        // 可见玩家：自发光胶囊，尺寸随受击框变细同步（朝向读红色速度向量 gizmo）
        GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        visual.name = "Visual";
        Object.DestroyImmediate(visual.GetComponent<CapsuleCollider>()); // 碰撞一律归 CharacterController
        visual.GetComponent<MeshRenderer>().sharedMaterial = mats.Player;
        visual.transform.SetParent(player.transform, false);

        CapsuleVisualSync sync = player.AddComponent<CapsuleVisualSync>();
        SerializedObject serializedSync = new SerializedObject(sync);
        serializedSync.FindProperty("_visual").objectReferenceValue = visual.transform;
        serializedSync.ApplyModifiedPropertiesWithoutUndo();

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

        // 编辑态初始机位：出生点后方，面向环道切线方向（+x）
        cameraGo.transform.SetPositionAndRotation(
            player.position + new Vector3(-6.5f, 2.5f, 0f),
            Quaternion.Euler(18f, 90f, 0f));

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
