using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

// 复用普通敌人的接线；模型、体型和战斗数值作为重型敌人的独立配置落盘。
[InitializeOnLoad]
public static class EnemyHeavyPrefabBuilder
{
    private const string PrefabPath = "Assets/Prefabs/EnemyHeavy.prefab";
    private const string ScenePath = "Assets/Scenes/CombatTest.unity";
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

    [MenuItem("超高速行者/创建重型敌人预制体并放入 CombatTest")]
    public static void Build()
    {
        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        // 不把用户尚未保存的其他场景编辑一并写盘。
        if (scene.IsValid() && scene.isDirty)
            throw new InvalidOperationException("CombatTest 有未保存修改，请先保存场景后重新运行菜单。");
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
            var source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Art/Models/EnemyHeavyTmp/NPC_01.fbx");
            var model = (GameObject)PrefabUtility.InstantiatePrefab(source, root.transform);
            model.name = "HeavyModel";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;
            Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
            Bounds bounds = BoundsOf(renderers);
            const float height = 2.5f;
            model.transform.localScale *= height / bounds.size.y;
            bounds = BoundsOf(renderers);
            model.transform.localPosition -= new Vector3(bounds.center.x, bounds.min.y + height * 0.5f, bounds.center.z);
            foreach (Transform child in model.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = root.layer;

            Animator animator = model.GetComponentInChildren<Animator>(true);
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
            SetFloat(root.GetComponent<CombatComponent>(), "_damage", 20f);
            SetFloat(root.GetComponent<CombatComponent>(), "_cooldown", 1.6f);
            Transform hitbox = root.GetComponentInChildren<Hitbox>(true).transform;
            hitbox.localPosition = new Vector3(0f, 0f, 1f);
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

        bool opened = !scene.IsValid();
        if (opened) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        try
        {
            if (!scene.GetRootGameObjects().Any(go => go.GetComponentInChildren<EnemyHeavy>(true) != null))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                Vector3 position = new Vector3(9f, 0f, -9f);
                if (NavMesh.SamplePosition(position, out NavMeshHit hit, 5f, NavMesh.AllAreas)) position = hit.position;
                instance.transform.position = position + Vector3.up * 1.25f;
                instance.SetActive(true);
                PrefabUtility.RecordPrefabInstancePropertyModifications(instance.transform);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
        }
        finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
        Debug.Log("[EnemyHeavy] 预制体已创建并放入 CombatTest。");
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
