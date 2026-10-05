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
    public IEnumerator MikuRigidParts_ArmProtection_AnimatedTransforms_AndReset()
    {
        var scene = SceneManager.CreateScene("GibRigidPartsRegression");
        var root = new GameObject("RigidPartsEnemy");
        SceneManager.MoveGameObjectToScene(root, scene);
        try
        {
            root.transform.SetPositionAndRotation(new Vector3(25f, 15f, 25f), Quaternion.Euler(0f, 35f, 0f));
            root.transform.localScale = new Vector3(1f, 1.2f, 0.9f);
            var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Art/Models/MikuGandam/Miku Snow Ver.fbx");
            Assert.NotNull(modelAsset);
            var model = Object.Instantiate(modelAsset, root.transform);
            Assert.IsEmpty(model.GetComponentsInChildren<SkinnedMeshRenderer>(), "必须覆盖无蒙皮的实际零件模型");
            Transform[] bones = model.GetComponentsInChildren<Transform>();
            Transform waist = System.Array.Find(bones, bone => bone.name == "Bone.001");
            Transform leftArm = System.Array.Find(bones, bone => bone.name == "Uper Arm.L");
            Transform rightArm = System.Array.Find(bones, bone => bone.name == "Uper Arm.R");
            Transform leg = System.Array.Find(bones, bone => bone.name == "Uper Leg.L");
            Assert.NotNull(waist);
            Assert.NotNull(leftArm);
            Assert.NotNull(rightArm);
            Assert.NotNull(leg);
            var enemy = root.AddComponent<Enemy>();
            var gib = root.AddComponent<GibComponent>();
            var settings = new SerializedObject(gib);
            settings.FindProperty("_cutoutShader").objectReferenceValue = Shader.Find("GameJam/EnemyWaistCutout");
            settings.FindProperty("_waist").objectReferenceValue = waist;
            settings.FindProperty("_waistOffset").floatValue = 0f;
            settings.FindProperty("_deathChance").floatValue = 1f;
            settings.FindProperty("_lifetime").floatValue = 0f;
            var arms = settings.FindProperty("_protectedArmRoots");
            arms.arraySize = 2;
            arms.GetArrayElementAtIndex(0).objectReferenceValue = leftArm;
            arms.GetArrayElementAtIndex(1).objectReferenceValue = rightArm;
            settings.ApplyModifiedPropertiesWithoutUndo();
            // 空蒙皮组件与原本隐藏的零件不能阻止收集，也不能在重置时被误开启。
            root.AddComponent<SkinnedMeshRenderer>();
            var hidden = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hidden.transform.SetParent(root.transform, false);
            hidden.GetComponent<MeshRenderer>().enabled = false;
            Renderer[] sources = gib.GetModelRenderers();
            Assert.AreEqual(92, sources.Length, "必须自动收集全部 Miku 零件");
            Assert.IsNull(gib.GetSetupError());
            var bounds = sources[0].bounds;
            foreach (Renderer source in sources) bounds.Encapsulate(source.bounds);
            leftArm.position = waist.position - Vector3.up * (bounds.size.y + 1f);
            var legRenderer = leg.GetComponentInChildren<MeshRenderer>();
            Mesh sourceMesh = legRenderer.GetComponent<MeshFilter>().sharedMesh;
            Vector3[] originalVertices = sourceMesh.vertices;
            enemy.TakeDamage(enemy.MaxHealth, root.transform.position, Vector3.forward);
            Assert.IsTrue(gib.IsSliced);
            var roots = scene.GetRootGameObjects();
            var upper = System.Array.Find(roots, go => go.name == "WaistCut_Upper");
            var lower = System.Array.Find(roots, go => go.name == "WaistCut_Lower");
            Assert.NotNull(upper.GetComponent<Rigidbody>());
            Assert.AreEqual(3, upper.GetComponentsInChildren<CapsuleCollider>().Length,
                $"切面必须穿过躯干：模型高度 {bounds.min.y}~{bounds.max.y}，腰线 {waist.position.y}");
            foreach (Renderer source in sources) Assert.IsFalse(source.enabled);
            Assert.AreEqual(1, upper.GetComponentsInChildren<MeshRenderer>().Length, "92 个零件应合为一张上半身网格");
            Assert.AreEqual(1, lower.GetComponentsInChildren<MeshRenderer>().Length, "下半身与封口也应合为一张网格");
            Mesh upperMesh = upper.GetComponentInChildren<MeshFilter>().sharedMesh;
            Mesh lowerMesh = lower.GetComponentInChildren<MeshFilter>().sharedMesh;
            Vector3[] upperBefore = upperMesh.vertices;
            Vector2[] upperDistances = upperMesh.uv2;
            Debug.Log($"[GibBatchValidation] MikuGandam：源零件={sources.Length}，尸块 Renderer=2，"
                + $"上/下半身材质槽={upperMesh.subMeshCount}/{lowerMesh.subMeshCount}，"
                + $"上/下半身顶点={upperMesh.vertexCount}/{lowerMesh.vertexCount}");
            int protectedParts = 0;
            foreach (Renderer source in sources)
            {
                if (!source.transform.IsChildOf(leftArm)) continue;
                Vector3[] expected = GetPartVertices(source, upper.transform);
                int offset = FindVertexBlock(upperMesh, expected);
                Assert.GreaterOrEqual(offset, 0, "合并后不能丢失手臂零件");
                Assert.IsTrue(System.Array.Exists(expected, vertex => vertex.y < 0f), "手臂放在腰线下才能验证保护");
                for (int i = 0; i < expected.Length; i++)
                    Assert.Greater(upperDistances[offset + i].x, 0f, "所有手臂零件必须完整留在上半身");
                protectedParts++;
            }
            Assert.Greater(protectedParts, 3, "必须覆盖手臂及子骨骼的多个零件");
            int legOffset = FindVertexBlock(lowerMesh, GetPartVertices(legRenderer, lower.transform));
            Assert.GreaterOrEqual(legOffset, 0, "合并后必须保留腿部");
            Vector3[] before = lowerMesh.vertices;
            Vector2[] cut = lowerMesh.uv2;
            Vector3 lowerPosition = lower.transform.position;
            leg.localRotation *= Quaternion.Euler(35f, 10f, 0f);
            leg.localPosition += new Vector3(0.3f, -0.2f, 0.1f);
            yield return null;
            Matrix4x4 matrix = lower.transform.worldToLocalMatrix * legRenderer.transform.localToWorldMatrix;
            Vector3[] actual = lowerMesh.vertices;
            float movement = 0f;
            for (int i = 0; i < originalVertices.Length; i++)
            {
                Assert.Less((actual[legOffset + i] - matrix.MultiplyPoint3x4(originalVertices[i])).sqrMagnitude, 0.000001f,
                    "下半身零件必须跟随骨骼 Transform");
                movement = Mathf.Max(movement, (actual[legOffset + i] - before[legOffset + i]).sqrMagnitude);
            }
            Assert.Greater(movement, 0.000001f);
            Assert.AreEqual(lowerPosition, lower.transform.position);
            CollectionAssert.AreEqual(upperBefore, upperMesh.vertices, "上半身保持死亡时的姿势");
            CollectionAssert.AreEqual(cut, lowerMesh.uv2, "动画不能改变切面分组");
            CollectionAssert.AreEqual(originalVertices, sourceMesh.vertices, "不能修改模型资产的网格");
            enemy.ResetHealth();
            foreach (Renderer source in sources) Assert.IsTrue(source.enabled);
            Assert.IsFalse(hidden.GetComponent<MeshRenderer>().enabled);
            yield return null;
            Assert.IsTrue(upper == null && lower == null);
            Assert.NotNull(sourceMesh, "重置不能销毁模型资产");
            Assert.AreEqual(92, gib.GetModelRenderers().Length);
        }
        finally
        {
            Object.DestroyImmediate(root);
            SceneManager.UnloadSceneAsync(scene);
        }
    }

    [UnityTest]
    public IEnumerator UnreadableRigidMesh_DoesNotHideOrCreatePartialPieces()
    {
        var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Mesh mesh = Object.Instantiate(root.GetComponent<MeshFilter>().sharedMesh);
        try
        {
            root.name = "UnreadablePart";
            root.GetComponent<MeshFilter>().sharedMesh = mesh;
            mesh.UploadMeshData(true);
            var enemy = root.AddComponent<Enemy>();
            var gib = root.AddComponent<GibComponent>();
            var settings = new SerializedObject(gib);
            settings.FindProperty("_cutoutShader").objectReferenceValue = Shader.Find("GameJam/EnemyWaistCutout");
            settings.FindProperty("_deathChance").floatValue = 1f;
            settings.ApplyModifiedPropertiesWithoutUndo();
            StringAssert.Contains("Read/Write", gib.GetSetupError());
            int rootCount = root.scene.rootCount;
            LogAssert.Expect(LogType.Warning, "[Gib] " + gib.GetSetupError());
            enemy.TakeDamage(enemy.MaxHealth, root.transform.position, Vector3.forward);
            Assert.IsFalse(gib.IsSliced);
            Assert.IsTrue(root.GetComponent<MeshRenderer>().enabled);
            Assert.AreEqual(rootCount, root.scene.rootCount, "不可读网格应在生成前失败");
            yield return null;
        }
        finally
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(mesh);
        }
    }

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
        Assert.AreEqual(1, upper.GetComponentsInChildren<MeshRenderer>().Length, "上半身和断口应合并");
        Assert.AreEqual(1, lower.GetComponentsInChildren<MeshRenderer>().Length, "下半身和断口应合并");
        Mesh upperMesh = upper.GetComponentInChildren<MeshFilter>().sharedMesh;
        Mesh lowerMesh = lower.GetComponentInChildren<MeshFilter>().sharedMesh;
        Vector3[] upperBefore = upperMesh.vertices;
        var animator = enemy.GetComponentInChildren<Animator>();
        Transform leftArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform rightArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        int protectedCount = 0;
        foreach (var renderer in enemy.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            int offset = FindVertexBlock(upperMesh, GetPartVertices(renderer, upper.transform));
            Vector2[] cut = upperMesh.uv2;
            BoneWeight[] weights = renderer.sharedMesh.boneWeights;
            Transform[] bones = renderer.bones;
            for (int i = 0; i < weights.Length; i++)
            {
                Transform bone = bones[weights[i].boneIndex0];
                if (bone == null || !(bone == leftArm || bone.IsChildOf(leftArm)
                    || bone == rightArm || bone.IsChildOf(rightArm))) continue;
                Assert.GreaterOrEqual(offset, 0, "合并后必须完整保留手臂网格");
                Assert.Greater(cut[offset + i].x, 0f, "手臂和手掌必须完整留在上半身");
                protectedCount++;
            }
        }
        Assert.Greater(protectedCount, 0, "必须覆盖实际手臂顶点");
        Assert.AreSame(upper.GetComponentInChildren<MeshRenderer>().sharedMaterial,
            lower.GetComponentInChildren<MeshRenderer>().sharedMaterial);
        var skin = System.Array.Find(enemy.GetComponentsInChildren<SkinnedMeshRenderer>(),
            renderer => FindVertexBlock(lowerMesh, GetPartVertices(renderer, lower.transform)) >= 0);
        Assert.NotNull(skin, "测试需要包含下半身的蒙皮网格");
        int skinOffset = FindVertexBlock(lowerMesh, GetPartVertices(skin, lower.transform));
        Vector3[] before = lowerMesh.vertices;
        Vector2[] distances = lowerMesh.uv2;
        Vector3 capStart = GetCapCenter(lower);
        Vector3 rootStart = lower.transform.position;
        yield return new WaitForSecondsRealtime(1f);
        yield return null;
        Assert.AreEqual(rootStart, lower.transform.position, "下半身不能再受尸块重力和旋转影响");
        CollectionAssert.AreEqual(distances, lowerMesh.uv2, "动画中应保持死亡瞬间的裁切距离");
        CollectionAssert.AreEqual(upperBefore, upperMesh.vertices, "上半身仍定格在断裂姿势");
        Assert.Greater((GetCapCenter(lower) - capStart).sqrMagnitude, 0.01f, "断口应随倒地动作移动");
        var baked = new Mesh();
        try
        {
            skin.BakeMesh(baked);
            Vector3[] actual = lowerMesh.vertices;
            Vector3[] expected = baked.vertices;
            Matrix4x4 matrix = lower.transform.worldToLocalMatrix * skin.transform.localToWorldMatrix;
            float movement = 0f;
            for (int i = 0; i < expected.Length; i++)
            {
                int target = skinOffset + i;
                Assert.Less((actual[target] - matrix.MultiplyPoint3x4(expected[i])).sqrMagnitude, 0.000001f,
                    "下半身顶点必须匹配当前骨骼动画");
                if (distances[target].x < 0f) movement = Mathf.Max(movement, (actual[target] - before[target]).sqrMagnitude);
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

    private static Vector3[] GetPartVertices(Renderer source, Transform piece)
    {
        Mesh baked = null;
        Mesh mesh;
        if (source is SkinnedMeshRenderer skin)
        {
            baked = new Mesh();
            skin.BakeMesh(baked);
            mesh = baked;
        }
        else mesh = source.GetComponent<MeshFilter>().sharedMesh;
        Vector3[] vertices = mesh.vertices;
        Matrix4x4 matrix = piece.worldToLocalMatrix * source.transform.localToWorldMatrix;
        for (int i = 0; i < vertices.Length; i++) vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
        if (baked != null) Object.DestroyImmediate(baked);
        return vertices;
    }

    private static int FindVertexBlock(Mesh mesh, Vector3[] expected)
    {
        Vector3[] actual = mesh.vertices;
        for (int start = 0; expected.Length > 0 && start + expected.Length <= actual.Length; start++)
        {
            if ((actual[start] - expected[0]).sqrMagnitude > 0.000001f) continue;
            int i = 1;
            while (i < expected.Length && (actual[start + i] - expected[i]).sqrMagnitude < 0.000001f) i++;
            if (i == expected.Length) return start;
        }
        return -1;
    }

    private static Vector3 GetCapCenter(GameObject piece)
    {
        var renderer = piece.GetComponentInChildren<MeshRenderer>();
        int slot = System.Array.FindIndex(renderer.sharedMaterials, material => material.GetFloat("_CutSurface") > 0.5f);
        Assert.GreaterOrEqual(slot, 0, "合并网格必须包含断口材质槽");
        Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.GetTriangles(slot);
        Assert.Greater(triangles.Length, 0);
        var bounds = new Bounds(vertices[triangles[0]], Vector3.zero);
        foreach (int index in triangles) bounds.Encapsulate(vertices[index]);
        return bounds.center;
    }
}
#endif
