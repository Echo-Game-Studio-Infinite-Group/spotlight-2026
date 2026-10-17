using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;

// 复用普通敌人的接线；模型、体型和战斗数值作为重型敌人的独立配置落盘。
[InitializeOnLoad]
public static class EnemyHeavyPrefabBuilder
{
    private const string PrefabPath = "Assets/Prefabs/EnemyHeavy.prefab";
    private const string ModelFolder = "Assets/Art/Models/EnemyHeavy/";
    private const string RequestPath = "Library/EnemyHeavyBuild.request";
    private const string ResultPath = "Library/EnemyHeavyBuild.result";

    static EnemyHeavyPrefabBuilder()
    {
        EditorApplication.delayCall += RunRequested;
    }

    private static void RunRequested()
    {
        if (!File.Exists(RequestPath)) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
        {
            EditorApplication.delayCall += RunRequested;
            return;
        }
        File.Delete(RequestPath);
        try
        {
            Build();
            File.WriteAllText(ResultPath, "SUCCESS: " + PrefabPath);
        }
        catch (Exception error)
        {
            File.WriteAllText(ResultPath, error.ToString());
            Debug.LogException(error);
        }
    }

    [MenuItem("超高速行者/更新重型敌人模型与动画")]
    public static void Build()
    {
        PrepareModel();
        if (!File.Exists(PrefabPath))
        {
            string seed = File.ReadAllText("Assets/Prefabs/EnemyTest.prefab")
                .Replace("m_Name: EnemyTest", "m_Name: EnemyHeavy")
                .Replace("493ed823406b8ca4a8bcc329b6f91fa5", "4436bc5251a0dcb488c9d608448002be");
            File.WriteAllText(PrefabPath, seed);
            AssetDatabase.ImportAsset(PrefabPath, ImportAssetOptions.ForceSynchronousImport);
        }

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            Animator oldAnimator = root.GetComponentInChildren<Animator>(true);
            if (oldAnimator != null)
            {
                Transform oldModel = oldAnimator.transform;
                while (oldModel.parent != root.transform) oldModel = oldModel.parent;
                UnityEngine.Object.DestroyImmediate(oldModel.gameObject);
            }
            root.name = "EnemyHeavy";
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.SetActive(true);
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelFolder + "combat-mech_idle.fbx");
            if (source == null) throw new InvalidOperationException("缺少重型敌人的新模型。");
            var model = (GameObject)PrefabUtility.InstantiatePrefab(source, root.transform);
            model.name = "HeavyModel";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;
            Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
            Material mechMaterial = CreateMaterial();
            foreach (Renderer renderer in renderers)
                renderer.sharedMaterials = renderer.sharedMaterials.Select(_ => mechMaterial).ToArray();
            Bounds bounds = BoundsOf(renderers);
            const float height = 2.5f;
            model.transform.localScale *= height / bounds.size.y;
            bounds = BoundsOf(renderers);
            model.transform.localPosition -= new Vector3(bounds.center.x, bounds.min.y + height * 0.5f, bounds.center.z);
            foreach (Transform child in model.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = root.layer;

            Animator animator = model.GetComponentInChildren<Animator>(true);
            // 独立导出的机甲 FBX 没有 Animator，预制体装配时显式补齐。
            if (animator == null) animator = model.AddComponent<Animator>();
            animator.avatar = AssetDatabase.LoadAllAssetsAtPath(ModelFolder + "combat-mech_idle.fbx")
                .OfType<Avatar>().FirstOrDefault();
            animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/Animations/EnemyHeavy.controller");
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var animation = animator.gameObject.AddComponent<EnemyAnimation>();
            SetFloat(animation, "_hurtHitStopSeconds", 0f);
            SetFloat(animation, "_deathHitStopSeconds", 0f);

            CharacterController capsule = root.GetComponent<CharacterController>();
            capsule.height = height;
            capsule.radius = 0.55f;
            capsule.center = Vector3.zero;
            NavMeshAgent agent = root.GetComponent<NavMeshAgent>();
            agent.height = height;
            agent.radius = capsule.radius;
            agent.baseOffset = height * 0.5f;
            agent.speed = 1.5f;
            agent.stoppingDistance = 1.15f;
            EnemyHeavy enemy = root.GetComponent<EnemyHeavy>();
            SetFloat(enemy, "_walkSpeed", 1.5f);
            SetFloat(enemy, "_runSpeed", 1.5f);
            SetFloat(enemy, "_attackRange", 1.7f);
            SetFloat(root.GetComponent<HealthComponent>(), "_maxHealth", 300f);
            Transform hitbox = root.GetComponentInChildren<Hitbox>(true).transform;
            hitbox.localPosition = new Vector3(0f, 0f, 1.6f);
            hitbox.localScale = new Vector3(1.5f, 1.5f, 1f);
            root.transform.Find("AttackTelegraph").localPosition = new Vector3(0f, 1.55f, 0f);
            root.transform.Find("MarkIndicator").localPosition = new Vector3(0f, 1.5f, 0f);
            string gibError = root.GetComponent<GibComponent>().GetSetupError();
            if (gibError != null) throw new InvalidOperationException(gibError);
            foreach (Material material in renderers.SelectMany(r => r.sharedMaterials))
                if (material == null || material.shader == null || !material.shader.isSupported)
                    throw new InvalidOperationException("重型模型存在无效材质或不受支持的 Shader。");
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }

        AssetDatabase.SaveAssets();
        Debug.Log("[EnemyHeavy] 已替换新机甲模型、材质和动画，场景未写盘。");
    }

    private static void PrepareModel()
    {
        string[] files = { "idle", "walk", "handgun-fire", "being-attacked", "disable" };
        foreach (string file in files)
        {
            string path = ModelFolder + "combat-mech_" + file + ".fbx";
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.isReadable = true;
            var clips = importer.defaultClipAnimations;
            foreach (var clip in clips) clip.loopTime = file == "idle" || file == "walk";
            importer.clipAnimations = clips;
            importer.SaveAndReimport();
        }
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/Animations/EnemyHeavy.controller");
        foreach (var layer in controller.layers) RemapStates(layer.stateMachine);
        EditorUtility.SetDirty(controller);
    }

    private static void RemapStates(AnimatorStateMachine machine)
    {
        foreach (var child in machine.states)
        {
            string clipName;
            switch (child.state.name)
            {
                case "Idle": clipName = "idle"; break;
                case "Move": clipName = "walk"; break;
                case "Attack": clipName = "handgun-fire"; break;
                case "Hurt": clipName = "being-attacked"; break;
                case "Dead": clipName = "disable"; break;
                default: continue;
            }
            child.state.motion = AssetDatabase.LoadAllAssetsAtPath(ModelFolder + "combat-mech_" + clipName + ".fbx")
                .OfType<AnimationClip>().First(clip => !clip.name.StartsWith("__preview__"));
        }
        foreach (var child in machine.stateMachines) RemapStates(child.stateMachine);
    }

    private static Material CreateMaterial()
    {
        const string path = "Assets/Art/Materials/EnemyHeavy.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(material, path);
        }
        material.SetTexture("_BaseMap", Texture("Mech_baseColor.jpg"));
        material.SetTexture("_BumpMap", Texture("Mech_normal.png", true));
        material.EnableKeyword("_NORMALMAP");
        material.SetTexture("_OcclusionMap", Texture("Mech_occlusion.jpg"));
        material.SetTexture("_MetallicGlossMap", Texture("Mech_metallic.jpg"));
        material.EnableKeyword("_METALLICSPECGLOSSMAP");
        material.SetFloat("_Smoothness", 0.35f);
        material.SetTexture("_EmissionMap", Texture("Mech_emissive.jpg"));
        material.SetColor("_EmissionColor", Color.white);
        material.EnableKeyword("_EMISSION");
        EditorUtility.SetDirty(material);
        return material;
    }

    private static Texture2D Texture(string file, bool normal = false)
    {
        string path = ModelFolder + "textures/" + file;
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        var type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        bool srgb = file == "Mech_baseColor.jpg" || file == "Mech_emissive.jpg";
        if (importer.textureType != type || importer.sRGBTexture != srgb)
        {
            importer.textureType = type;
            importer.sRGBTexture = srgb;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    private static Bounds BoundsOf(Renderer[] renderers)
    {
        if (renderers.Length == 0) throw new InvalidOperationException("重型模型没有 Renderer。");
        Bounds bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
        return bounds;
    }

    private static void SetFloat(UnityEngine.Object target, string name, float value)
    {
        var serialized = new SerializedObject(target);
        serialized.FindProperty(name).floatValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(target);
    }
}
