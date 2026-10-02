using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

// 项目装配：把 Built-in RP 工程切到 URP，并接线加速测试场景
//   · 生成 URP 管线资产与渲染器，并把 RadialRedshiftFeature 注册进去
//   · 把遗留的 Built-in Standard 材质转换成 URP/Lit（否则切管线后全部变洋红）
//   · 清理已删除脚本留下的 Missing 组件，给 Player 挂 CharacterMovement、给相机挂 CameraController
//   · 补一个 Global Volume 承载 RunVolume（URP 色差 + 动态模糊）
// 可重复执行：已有资产与组件会被复用而不是重复创建
public static class ProjectBootstrap
{
    private const string SettingsFolder = "Assets/Settings";
    private const string PipelineAssetPath = SettingsFolder + "/URP-Asset.asset";
    private const string RendererDataPath = SettingsFolder + "/URP-Renderer.asset";
    private const string RunVolumePath = SettingsFolder + "/RunVolume.asset";
    private const string RedshiftMaterialPath = "Assets/Materials/RadialRedshift.mat";
    private const string MovementParamsPath = SettingsFolder + "/MovementParams.asset";
    private const string ScenePath = "Assets/Scenes/TestScene.unity";

    [MenuItem("超高速行者/装配 URP 与加速测试场景")]
    public static void Build()
    {
        // 播放模式下 EditorSceneManager.OpenScene 会被 Unity 直接拒绝（InvalidOperationException），
        // 装配会中途夭折并留下半成品工程。这里自动退出播放模式后重试，不再让主人踩这个坑。
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[ProjectBootstrap] 检测到 Play 模式：场景装配只能在编辑模式做，正在退出播放模式后重试…");
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.isPlaying = false;
            return;
        }

        RunBuild();
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode)
        {
            return;
        }

        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        // 退出播放模式后场景会重新加载，等一帧再动，否则拿到的还是播放态的场景对象
        EditorApplication.delayCall += RunBuild;
    }

    private static void RunBuild()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[ProjectBootstrap] 仍处于 Play 模式，装配中止。请手动点 Stop 后重新执行菜单。");
            return;
        }

        SetupPipeline();
        ConvertMaterials();
        WireScene();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Verify();
        Debug.Log("[ProjectBootstrap] 装配完成");
    }

    private static void SetupPipeline()
    {
        EnsureFolder("Assets", "Settings");

        UniversalRendererData rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererDataPath);
        if (rendererData == null)
        {
            rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(rendererData, RendererDataPath);
        }

        // 关键：postProcessData 为空时 URP 不会创建任何后处理 Pass（PostProcessPasses.isCreated 为 false），
        // Volume 上的色差与动态模糊会静默失效。Unity 官方创建路径会设它，这里直接 CreateInstance 绕过了，必须补。
        if (rendererData.postProcessData == null)
        {
            rendererData.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(
                "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
            if (rendererData.postProcessData == null)
            {
                Debug.LogError("[ProjectBootstrap] 未能加载 URP 默认 PostProcessData，后处理将不生效");
            }
        }

        // 把渲染器里其余的空资源引用（shader / XR 数据）补齐，与官方创建路径一致
        ResourceReloader.ReloadAllNullIn(rendererData, "Packages/com.unity.render-pipelines.universal");

        // 必须无条件标脏：写在上面的 if 里的话，第二次执行走不到，赋值就不会落盘，
        // 紧接着的 ForceUpdate 重导入还会从磁盘把空值读回来（这个坑真的踩过）
        EditorUtility.SetDirty(rendererData);

        // 注册速度感全屏 Pass。重复执行时必须判断，否则每跑一次就多叠一层
        bool alreadyRegistered = false;
        foreach (ScriptableRendererFeature existing in rendererData.rendererFeatures)
        {
            if (existing is RadialRedshiftFeature)
            {
                alreadyRegistered = true;
                break;
            }
        }

        if (!alreadyRegistered)
        {
            RadialRedshiftFeature feature = ScriptableObject.CreateInstance<RadialRedshiftFeature>();
            feature.name = "RadialRedshiftFeature";
            feature.settings.material = AssetDatabase.LoadAssetAtPath<Material>(RedshiftMaterialPath);
            feature.settings.falloffPower = 1.5f;                          // 中心清晰、边缘拉伸
            feature.settings.samples = 8;                                  // 采样步数，够用且省
            feature.settings.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
            AssetDatabase.AddObjectToAsset(feature, rendererData);
            rendererData.rendererFeatures.Add(feature);
            EditorUtility.SetDirty(rendererData);
        }

        UniversalRenderPipelineAsset pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelineAssetPath);
        if (pipeline == null)
        {
            pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            AssetDatabase.CreateAsset(pipeline, PipelineAssetPath);
        }

        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;

        // 延迟渲染与抗锯齿是本项目后续要调的项，这里先给一份能用的基线
        pipeline.supportsHDR = true;
        pipeline.msaaSampleCount = 4;
        EditorUtility.SetDirty(pipeline);

        // 先落盘再重导入：反过来会让 ForceUpdate 用磁盘上的旧内容覆盖掉内存里刚写入的 Feature 与 PostProcessData
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(RendererDataPath, ImportAssetOptions.ForceUpdate);
        AssetDatabase.SaveAssets();
        Debug.Log("[ProjectBootstrap] URP 管线已装配：" + PipelineAssetPath);
    }

    // Built-in Standard → URP/Lit。属性名不同，必须先读旧值再换 shader，否则旧属性读不到
    private static void ConvertMaterials()
    {
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null)
        {
            Debug.LogError("[ProjectBootstrap] 找不到 Universal Render Pipeline/Lit，URP 包可能未安装");
            return;
        }

        int converted = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null || material.shader == null || material.shader.name != "Standard")
            {
                continue;
            }

            Color baseColor = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
            Texture baseMap = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
            Vector2 tiling = baseMap != null ? material.GetTextureScale("_MainTex") : Vector2.one;
            Vector2 offset = baseMap != null ? material.GetTextureOffset("_MainTex") : Vector2.zero;
            float metallic = material.HasProperty("_Metallic") ? material.GetFloat("_Metallic") : 0f;
            float smoothness = material.HasProperty("_Glossiness") ? material.GetFloat("_Glossiness") : 0.5f;
            Color emission = material.HasProperty("_EmissionColor") ? material.GetColor("_EmissionColor") : Color.black;
            bool emissive = material.IsKeywordEnabled("_EMISSION") && emission.maxColorComponent > 0f;

            material.shader = lit;
            material.SetColor("_BaseColor", baseColor);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);

            if (baseMap != null)
            {
                material.SetTexture("_BaseMap", baseMap);
                material.SetTextureScale("_BaseMap", tiling);
                material.SetTextureOffset("_BaseMap", offset);
            }

            if (emissive)
            {
                // URP 的自发光靠关键字 + 颜色同时生效，只设颜色不亮
                material.SetColor("_EmissionColor", emission);
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            EditorUtility.SetDirty(material);
            converted++;
        }

        AssetDatabase.SaveAssets();
        Debug.Log("[ProjectBootstrap] 已转换材质数：" + converted);
    }

    private static void WireScene()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        // 1) 代码已整体删除，旧组件全部变成 Missing Script — 先清干净再接线
        int removed = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(child.gameObject);
            }
        }

        GameObject player = FindInScene(scene, "Player");
        GameObject cameraGo = FindInScene(scene, "Main Camera");
        if (player == null || cameraGo == null)
        {
            Debug.LogError("[ProjectBootstrap] 场景缺少 Player 或 Main Camera，接线中止");
            return;
        }

        // 1b) 时间分层必须真实挂在场景里——否则全部逻辑跑 Time.deltaTime 退化路径，
        //     "逻辑只读 TimeManager 缩放时间"（AGENTS.md 第 5 条）形同虚设
        if (FindInScene(scene, "TimeManager") == null)
        {
            GameObject timeGo = new GameObject("TimeManager");
            SceneManager.MoveGameObjectToScene(timeGo, scene);
            timeGo.AddComponent<TimeManager>();
        }

        // 2) Player：两套移动机制都挂上，靠 enabled 开关切换。
        //    两者都要求同一个 CharacterController 且都直读输入，同时启用会互相抢控制权，所以必须二选一。
        //    只在组件是新建的时候设默认值 —— 重复执行不覆盖主人手动选的开关状态。
        PlayerMotor motor = player.GetComponent<PlayerMotor>();
        if (motor == null)
        {
            motor = player.AddComponent<PlayerMotor>();
            motor.enabled = true; // 默认启用原版 bhop 机制（DebugHUD 也依赖它）
        }

        SerializedObject motorSo = new SerializedObject(motor);
        SerializedProperty paramsProperty = motorSo.FindProperty("_params");
        if (paramsProperty != null && paramsProperty.objectReferenceValue == null)
        {
            paramsProperty.objectReferenceValue = AssetDatabase.LoadAssetAtPath<MovementParams>(MovementParamsPath);
            motorSo.ApplyModifiedPropertiesWithoutUndo();
        }

        CharacterMovement characterMovement = player.GetComponent<CharacterMovement>();
        if (characterMovement == null)
        {
            characterMovement = player.AddComponent<CharacterMovement>();
            characterMovement.enabled = false; // 默认让位给 PlayerMotor
        }

        // CharacterMovement 的移动方向以相机为基准，所以必须指向相机（切到它时才有正确朝向）
        SerializedObject movementSo = new SerializedObject(characterMovement);
        SerializedProperty referenceProperty = movementSo.FindProperty("_movementReference");
        if (referenceProperty != null)
        {
            referenceProperty.objectReferenceValue = cameraGo.transform;
            movementSo.ApplyModifiedPropertiesWithoutUndo();
        }

        if (player.GetComponent<PlayerRespawn>() == null)
        {
            player.AddComponent<PlayerRespawn>();
        }

        if (player.GetComponent<DebugHUD>() == null)
        {
            player.AddComponent<DebugHUD>();
        }

        // 4) 可见胶囊跟随受击框（PlayerMotor 变速改胶囊尺寸；CharacterMovement 不改，视觉保持基准尺寸）
        //    场景里子物体叫 "Model"（早期装配曾用 "Visual"）——两个名字都兜底，避免重跑装配把已接好的引用清成 null
        Transform visual = player.transform.Find("Model");
        if (visual == null) visual = player.transform.Find("Visual");
        CapsuleVisualSync visualSync = player.GetComponent<CapsuleVisualSync>();
        if (visualSync == null)
        {
            visualSync = player.AddComponent<CapsuleVisualSync>();
        }

        if (visual != null)
        {
            SerializedObject syncSo = new SerializedObject(visualSync);
            syncSo.FindProperty("_visual").objectReferenceValue = visual;
            syncSo.ApplyModifiedPropertiesWithoutUndo();

            // 图元 pivot 在几何中心，而 CharacterController 的 pivot 在脚底，必须抬高半个身高
            CharacterController controller = player.GetComponent<CharacterController>();
            float centerY = controller != null ? controller.center.y : 1f;
            visual.localPosition = new Vector3(0f, centerY, 0f);
            visual.localRotation = Quaternion.identity;
            visual.localScale = Vector3.one;
        }

        // 4) 相机：挂 URP 附加数据 + 特效控制器，并指向 Global Volume
        if (cameraGo.GetComponent<UniversalAdditionalCameraData>() == null)
        {
            cameraGo.AddComponent<UniversalAdditionalCameraData>();
        }

        UniversalAdditionalCameraData cameraData = cameraGo.GetComponent<UniversalAdditionalCameraData>();
        cameraData.renderPostProcessing = true;

        CameraController cameraController = cameraGo.GetComponent<CameraController>();
        if (cameraController == null)
        {
            cameraController = cameraGo.AddComponent<CameraController>();
        }

        Volume volume = FindOrCreateGlobalVolume(scene);
        cameraController.player = player;
        cameraController.volume = volume;

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        Debug.Log("[ProjectBootstrap] 场景接线完成：清除 Missing 组件 " + removed + " 个，保存=" + saved);
    }

    private static Volume FindOrCreateGlobalVolume(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Volume existing = root.GetComponentInChildren<Volume>(true);
            if (existing != null)
            {
                return existing;
            }
        }

        GameObject go = new GameObject("Global Volume");
        SceneManager.MoveGameObjectToScene(go, scene);

        Volume volume = go.AddComponent<Volume>();
        volume.isGlobal = true;
        volume.priority = 0f;
        volume.sharedProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(RunVolumePath);
        if (volume.sharedProfile == null)
        {
            Debug.LogWarning("[ProjectBootstrap] 未找到 RunVolume.asset，色差与动态模糊不会生效");
        }

        return volume;
    }

    private static GameObject FindInScene(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == name)
            {
                return root;
            }

            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == name)
                {
                    return child.gameObject;
                }
            }
        }

        return null;
    }

    private static void EnsureFolder(string parent, string name)
    {
        if (!AssetDatabase.IsValidFolder(parent + "/" + name))
        {
            AssetDatabase.CreateFolder(parent, name);
        }
    }

    // 自检：把关键绑定打成一条日志，批次模式下可直接从 stdout 判定成功与否
    [MenuItem("超高速行者/自检装配结果")]
    public static void Verify()
    {
        List<string> lines = new List<string>();

        UniversalRenderPipelineAsset pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelineAssetPath);
        lines.Add("管线资产: " + (pipeline != null ? "OK" : "缺失"));
        lines.Add("GraphicsSettings.defaultRenderPipeline: " + (GraphicsSettings.defaultRenderPipeline != null ? GraphicsSettings.defaultRenderPipeline.name : "空"));

        UniversalRendererData rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererDataPath);
        bool featureRegistered = false;
        if (rendererData != null)
        {
            foreach (ScriptableRendererFeature feature in rendererData.rendererFeatures)
            {
                if (feature is RadialRedshiftFeature)
                {
                    featureRegistered = true;
                }
            }
        }

        lines.Add("RadialRedshiftFeature 已注册: " + featureRegistered);
        // postProcessData 为 null 时 URP 不创建后处理 Pass —— 必须单独验证，否则效果会静默消失
        lines.Add("Renderer.postProcessData: " + (rendererData != null && rendererData.postProcessData != null
            ? rendererData.postProcessData.name
            : "空（后处理不会生效）"));
        // 上面读的是内存对象，内存正确不代表落盘正确（SetDirty 漏掉时会骗过自检）——这里直接查磁盘文本
        string rendererText = System.IO.File.Exists(RendererDataPath)
            ? System.IO.File.ReadAllText(RendererDataPath)
            : string.Empty;
        lines.Add("Renderer 资产磁盘上的 postProcessData 已落盘: " + !rendererText.Contains("postProcessData: {fileID: 0}"));
        lines.Add("Renderer 资产磁盘上的 Feature 已落盘: " + rendererText.Contains("RadialRedshiftFeature"));
        // m_RendererDataList 是 internal，取不到；用 SerializedObject 读序列化字段
        bool pipelineUsesRenderer = false;
        if (pipeline != null)
        {
            SerializedProperty rendererList = new SerializedObject(pipeline).FindProperty("m_RendererDataList");
            if (rendererList != null && rendererList.arraySize > 0)
            {
                pipelineUsesRenderer = rendererList.GetArrayElementAtIndex(0).objectReferenceValue == rendererData;
            }
        }

        lines.Add("Pipeline 引用该 Renderer: " + pipelineUsesRenderer);

        Material redshift = AssetDatabase.LoadAssetAtPath<Material>(RedshiftMaterialPath);
        lines.Add("RadialRedshift 材质 shader: " + (redshift != null && redshift.shader != null ? redshift.shader.name : "空"));

        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        GameObject player = FindInScene(scene, "Player");
        GameObject cameraGo = FindInScene(scene, "Main Camera");
        lines.Add("Player.CharacterMovement: " + (player != null && player.GetComponent<CharacterMovement>() != null));
        // 两套移动机制同时挂载，必须只有一个 enabled —— 两个都跑会互相抢 CharacterController
        PlayerMotor motor = player != null ? player.GetComponent<PlayerMotor>() : null;
        CharacterMovement characterMovement = player != null ? player.GetComponent<CharacterMovement>() : null;
        bool motorOn = motor != null && motor.enabled;
        bool movementOn = characterMovement != null && characterMovement.enabled;
        lines.Add("Player.PlayerMotor enabled: " + motorOn + "（参数资产=" + (motor != null && motor.Params != null ? motor.Params.name : "空") + "）");
        lines.Add("Player.CharacterMovement enabled: " + movementOn);
        lines.Add("移动机制二选一成立: " + (motorOn ^ movementOn));
        lines.Add("Player.DebugHUD: " + (player != null && player.GetComponent<DebugHUD>() != null));
        lines.Add("Player.CapsuleVisualSync: " + (player != null && player.GetComponent<CapsuleVisualSync>() != null));
        lines.Add("Player.PlayerRespawn: " + (player != null && player.GetComponent<PlayerRespawn>() != null));
        lines.Add("Camera.CameraController: " + (cameraGo != null && cameraGo.GetComponent<CameraController>() != null));
        lines.Add("Camera.UniversalAdditionalCameraData: " + (cameraGo != null && cameraGo.GetComponent<UniversalAdditionalCameraData>() != null));

        CameraController controller = cameraGo != null ? cameraGo.GetComponent<CameraController>() : null;
        lines.Add("CameraController.player 已连: " + (controller != null && controller.player != null));
        lines.Add("CameraController.volume 已连: " + (controller != null && controller.volume != null));
        lines.Add("RunVolume profile: " + (controller != null && controller.volume != null && controller.volume.sharedProfile != null
            ? controller.volume.sharedProfile.name
            : "空"));

        foreach (string line in lines)
        {
            Debug.Log("[Verify] " + line);
        }
    }
}
