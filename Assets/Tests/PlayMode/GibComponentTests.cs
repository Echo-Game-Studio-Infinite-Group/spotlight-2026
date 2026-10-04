#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public sealed class GibComponentTests
{
    [UnityTest]
    public IEnumerator DeathGate_OneRoll_AnimatedLower_AndReset()
    {
        yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/AnimTestScene.unity",
            new LoadSceneParameters(LoadSceneMode.Single));
        var gib = Object.FindObjectOfType<GibComponent>();
        var enemy = gib.GetComponent<Enemy>();
        enemy.SetChasePlayer(false);
        enemy.SetInvulnerableTime(0f);
        enemy.SetDamagePopup(null);
        var settings = new SerializedObject(gib);
        settings.FindProperty("_deathChance").floatValue = 0f;
        settings.ApplyModifiedPropertiesWithoutUndo();
        Assert.IsFalse(gib.TrySlice(Vector3.forward), "活着不能肢解");
        enemy.TakeDamage(enemy.MaxHealth, enemy.transform.position, Vector3.forward);
        Assert.IsFalse(gib.IsSliced, "概率为零不能肢解");
        settings.Update();
        settings.FindProperty("_deathChance").floatValue = 1f;
        settings.FindProperty("_lifetime").floatValue = 0f;
        settings.ApplyModifiedPropertiesWithoutUndo();
        Assert.IsFalse(gib.TrySlice(Vector3.forward), "死亡后不能重复抽取概率");
        enemy.ResetHealth();
        enemy.TakeDamage(1f, enemy.transform.position, Vector3.forward);
        Assert.IsFalse(gib.IsSliced, "非致命伤不能肢解");
        enemy.TakeDamage(enemy.MaxHealth, enemy.transform.position, Vector3.forward);
        Assert.IsTrue(gib.IsSliced, "重置后致命伤可以再次触发");
        var roots = enemy.gameObject.scene.GetRootGameObjects();
        var upper = System.Array.Find(roots, go => go.name == "WaistCut_Upper");
        var lower = System.Array.Find(roots, go => go.name == "WaistCut_Lower");
        Assert.NotNull(upper);
        Assert.NotNull(lower);
        var body = upper.GetComponent<Rigidbody>();
        Assert.NotNull(body, "上半身应使用真实刚体");
        Assert.IsFalse(body.isKinematic);
        var capsules = upper.GetComponentsInChildren<CapsuleCollider>();
        Assert.AreEqual(3, capsules.Length, "躯干和双臂必须使用三个胶囊");
        Assert.AreEqual(3, upper.GetComponentsInChildren<Collider>().Length);
        foreach (var capsule in capsules)
        {
            Assert.AreSame(body, capsule.attachedRigidbody, "三个胶囊共用上半身刚体");
            Assert.IsFalse(capsule.isTrigger);
            Assert.AreEqual(LayerMask.NameToLayer("Ignore Raycast"), capsule.gameObject.layer);
            foreach (var player in Object.FindObjectsOfType<PlayerMotor>())
                foreach (var playerCollider in player.GetComponentsInChildren<Collider>(true))
                {
                    Assert.AreNotEqual(playerCollider.gameObject.layer, capsule.gameObject.layer);
                    Assert.IsTrue(Physics.GetIgnoreCollision(capsule, playerCollider), "尸块不能阻挡玩家");
                }
        }
        Assert.IsNull(lower.GetComponent<Rigidbody>(), "下半身继续随动画倒下");
        var animator = enemy.GetComponentInChildren<Animator>();
        Transform leftArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform rightArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        int protectedCount = 0;
        foreach (var renderer in enemy.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            Mesh pieceMesh = System.Array.Find(upper.GetComponentsInChildren<MeshFilter>(),
                filter => filter.sharedMesh.name == renderer.name + "_DeathPose").sharedMesh;
            Vector2[] cut = pieceMesh.uv2;
            BoneWeight[] weights = renderer.sharedMesh.boneWeights;
            Transform[] bones = renderer.bones;
            for (int i = 0; i < weights.Length; i++)
            {
                Transform bone = bones[weights[i].boneIndex0];
                if (bone == null || !(bone == leftArm || bone.IsChildOf(leftArm)
                    || bone == rightArm || bone.IsChildOf(rightArm))) continue;
                Assert.Greater(cut[i].x, 0f, "手臂和手掌必须完整留在上半身");
                protectedCount++;
            }
        }
        Assert.Greater(protectedCount, 0, "必须覆盖实际手臂顶点");
        Assert.AreSame(upper.GetComponentInChildren<MeshRenderer>().sharedMaterial,
            lower.GetComponentInChildren<MeshRenderer>().sharedMaterial);
        var lowerFilter = System.Array.Find(lower.GetComponentsInChildren<MeshFilter>(),
            filter => System.Array.Exists(filter.sharedMesh.uv2, uv => uv.x < 0f));
        Assert.NotNull(lowerFilter, "测试需要包含下半身的网格");
        var skin = System.Array.Find(enemy.GetComponentsInChildren<SkinnedMeshRenderer>(),
            renderer => lowerFilter.sharedMesh.name == renderer.name + "_DeathPose(Clone)");
        string meshName = skin.name + "_DeathPose";
        Mesh lowerMesh = lowerFilter.sharedMesh;
        Mesh upperMesh = System.Array.Find(upper.GetComponentsInChildren<MeshFilter>(),
            filter => filter.sharedMesh.name == meshName).sharedMesh;
        Vector3[] before = lowerMesh.vertices;
        Vector2[] distances = lowerMesh.uv2;
        Mesh cap = System.Array.Find(lower.GetComponentsInChildren<MeshFilter>(),
            filter => filter.sharedMesh.name == "WaistCut_Surface").sharedMesh;
        Vector3 capStart = cap.bounds.center;
        Vector3 rootStart = lower.transform.position;
        yield return new WaitForSecondsRealtime(1f);
        yield return new WaitForEndOfFrame();
        Assert.AreEqual(rootStart, lower.transform.position, "下半身不能再受尸块重力和旋转影响");
        CollectionAssert.AreEqual(distances, lowerMesh.uv2, "动画中应保持死亡瞬间的裁切距离");
        CollectionAssert.AreEqual(before, upperMesh.vertices, "上半身仍定格在断裂姿势");
        Assert.Greater((cap.bounds.center - capStart).sqrMagnitude, 0.01f, "断口应随倒地动作移动");
        var baked = new Mesh();
        try
        {
            skin.BakeMesh(baked);
            Vector3[] actual = lowerMesh.vertices;
            Vector3[] expected = baked.vertices;
            Matrix4x4 matrix = lower.transform.worldToLocalMatrix * skin.transform.localToWorldMatrix;
            float movement = 0f;
            for (int i = 0; i < actual.Length; i++)
            {
                Assert.Less((actual[i] - matrix.MultiplyPoint3x4(expected[i])).sqrMagnitude, 0.000001f,
                    "下半身顶点必须匹配当前骨骼动画");
                if (distances[i].x < 0f) movement = Mathf.Max(movement, (actual[i] - before[i]).sqrMagnitude);
            }
            Assert.Greater(movement, 0.01f, "下半身必须继续播放死亡动画");
        }
        finally { Object.Destroy(baked); }
        // 在远离测试场景的平台验证真实碰撞，避免场景中的墙壁或其他敌人干扰。
        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        try
        {
            floor.transform.position = new Vector3(1000f, -5f, 1000f);
            floor.transform.localScale = new Vector3(20f, 1f, 20f);
            body.position = new Vector3(1000f, 0f, 1000f);
            body.rotation = Quaternion.identity;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.WakeUp();
            Physics.SyncTransforms();
            yield return new WaitForSecondsRealtime(2f);
            Assert.Less(body.position.y, -1f, "上半身必须受重力实际下落");
            float floorTop = floor.GetComponent<Collider>().bounds.max.y;
            float bodyBottom = float.PositiveInfinity;
            foreach (var capsule in capsules) bodyBottom = Mathf.Min(bodyBottom, capsule.bounds.min.y);
            Assert.That(bodyBottom, Is.InRange(floorTop - 0.1f, floorTop + 0.15f),
                "上半身必须停在地面，不能穿透或悬空");
        }
        finally { Object.Destroy(floor); }
        enemy.ResetHealth();
        Assert.IsFalse(gib.IsSliced);
        yield return null;
        Assert.IsTrue(upper == null && lower == null, "重置后回收尸块");
    }
}
#endif
