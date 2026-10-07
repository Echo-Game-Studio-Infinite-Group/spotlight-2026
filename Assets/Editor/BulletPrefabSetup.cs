using UnityEditor;
using UnityEngine;

// 一次性装配工具：把子弹预制体接到 Hitbox 判定链上。
//
// 为什么用脚本而不是手改 .prefab：Hitbox 带 [RequireComponent(BoxCollider/Rigidbody)]，
// 手工拼 YAML 要自己分配 fileID、维护 m_Component 列表，很容易拼出个打不开的预制体。
// 走 PrefabUtility 让 Unity 自己维护这些引用。
//
// 幂等：已经挂过的组件不会重复添加，可以反复执行。
public static class BulletPrefabSetup
{
    private const string BulletPath = "Assets/Prefabs/Bullet.prefab";

    [MenuItem("超高速行者/装配子弹判定盒")]
    public static void SetupBullet()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("[BulletPrefabSetup] 请先退出播放模式再装配预制体。");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(BulletPath);
        if (root == null)
        {
            Debug.LogError($"[BulletPrefabSetup] 打不开 {BulletPath}");
            return;
        }

        try
        {
            // Hitbox 会带出 BoxCollider + Rigidbody
            Hitbox hitbox = root.GetComponent<Hitbox>();
            if (hitbox == null) hitbox = root.AddComponent<Hitbox>();

            var collider = root.GetComponent<BoxCollider>();
            if (collider == null) collider = root.AddComponent<BoxCollider>();
            // 判定盒必须是触发器：Hitbox 靠 OnTriggerEnter 上报，实体碰撞会挡住子弹飞行。
            collider.isTrigger = true;
            // 弹体默认很小，给一个贴合视觉的判定尺寸，具体可按美术调。
            if (collider.size == Vector3.one) collider.size = new Vector3(0.25f, 0.25f, 0.6f);

            var body = root.GetComponent<Rigidbody>();
            if (body == null) body = root.AddComponent<Rigidbody>();
            // 位移由 Projectile 自己积分，物理只负责发触发器事件。
            body.isKinematic = true;
            body.useGravity = false;

            CombatComponent combat = root.GetComponent<CombatComponent>();
            if (combat == null) combat = root.AddComponent<CombatComponent>();

            PrefabUtility.SaveAsPrefabAsset(root, BulletPath);
            Debug.Log($"[BulletPrefabSetup] 完成：Hitbox={(hitbox != null)} BoxCollider.trigger={collider.isTrigger} "
                + $"Rigidbody.kinematic={body.isKinematic} CombatComponent={(combat != null)}");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }
}
