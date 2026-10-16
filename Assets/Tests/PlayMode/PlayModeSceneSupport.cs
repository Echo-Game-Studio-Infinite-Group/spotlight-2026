#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

// 美术场景可使用 Generic 或刚性零件；Humanoid 算法回归用例显式建立自己的靶子。
internal static class PlayModeSceneSupport
{
    internal static Enemy CreateHumanoidEnemyFixture()
    {
        Enemy seed = Object.FindObjectOfType<Enemy>();
        if (seed == null) throw new System.InvalidOperationException("场景缺少导航敌人，无法建立战斗测试靶子");
        NavMeshAgent sourceAgent = seed.GetComponent<NavMeshAgent>();
        var filter = new NavMeshQueryFilter { agentTypeID = sourceAgent.agentTypeID, areaMask = sourceAgent.areaMask };
        if (!NavMesh.SamplePosition(seed.transform.position, out NavMeshHit floor, 5f, filter))
            throw new System.InvalidOperationException("测试靶子的出生点没有烘焙导航网格");
        foreach (Enemy other in Object.FindObjectsOfType<Enemy>()) other.gameObject.SetActive(false);
        GameObject root = new GameObject("HumanoidCombatFixture"); root.SetActive(false);
        root.transform.position = floor.position + Vector3.up * sourceAgent.baseOffset;
        HealthComponent health = root.AddComponent<HealthComponent>(); health.SetMaxHealth(120f); health.SetInvulnerableTime(0f);
        CombatComponent combat = root.AddComponent<CombatComponent>();
        combat.Camp = CampType.Enemy; combat.Damage = 8f; combat.Cooldown = 1.1f; combat.WindowSeconds = 0f;
        CapsuleCollider collider = root.AddComponent<CapsuleCollider>(); collider.center = Vector3.up; collider.height = 2f; collider.radius = .3f;
        NavMeshAgent agent = root.AddComponent<NavMeshAgent>();
        agent.agentTypeID = sourceAgent.agentTypeID; agent.areaMask = sourceAgent.areaMask;
        agent.baseOffset = sourceAgent.baseOffset; agent.height = 2f; agent.radius = .3f;
        Enemy enemy = root.AddComponent<Enemy>(); enemy.ConfigureStats(120f, 8f, 2f);
        GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Art/Models/Player.fbx");
        GameObject model = Object.Instantiate(asset, root.transform); model.name = "HumanoidModel";
        Animator animator = model.GetComponent<Animator>();
        animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/Animations/EnemyTest.controller");
        animator.applyRootMotion = false; model.AddComponent<EnemyAnimation>();
        GameObject box = new GameObject("AttackHitbox"); box.transform.SetParent(root.transform, false);
        BoxCollider volume = box.AddComponent<BoxCollider>(); volume.isTrigger = true;
        volume.center = new Vector3(0f, 1f, 1f); volume.size = new Vector3(2f, 2f, 2f);
        Hitbox hitbox = box.AddComponent<Hitbox>(); enemy.ConfigureAttackHitbox(hitbox);
        GibComponent gib = root.AddComponent<GibComponent>();
        var settings = new SerializedObject(gib);
        settings.FindProperty("_cutoutShader").objectReferenceValue = Shader.Find("GameJam/EnemyWaistCutout");
        settings.FindProperty("_lifetime").floatValue = 0f;
        settings.ApplyModifiedPropertiesWithoutUndo();
        root.SetActive(true); animator.Rebind(); animator.Update(0f);
        settings.Update(); settings.FindProperty("_waist").objectReferenceValue = animator.GetBoneTransform(HumanBodyBones.Hips);
        settings.ApplyModifiedPropertiesWithoutUndo();
        return enemy;
    }
}
#endif
