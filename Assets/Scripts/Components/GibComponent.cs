using System.Collections.Generic;
using UnityEngine;

// 上半身定格击飞，下半身跟随原骨骼；在模型插值之后同步网格，不复制 AI 或攻击事件。
[DefaultExecutionOrder(100), DisallowMultipleComponent, RequireComponent(typeof(Enemy))]
public sealed class GibComponent : MonoBehaviour
{
    [SerializeField] private Shader _cutoutShader;
    [SerializeField, Range(0f, 1f)] private float _deathChance = 0.5f;
    [SerializeField] private Transform _waist;
    [SerializeField] private float _waistOffset = 0.08f;
    [SerializeField, Range(0f, 1f)] private float _fallbackHeight = 0.55f;
    [SerializeField] private Color _cutColor = new Color(0.24f, 0.025f, 0.02f);
    [SerializeField, Min(0f)] private float _edgeWidth = 0.015f;
    [SerializeField, Min(0f)] private float _separationSpeed = 2.5f;
    [SerializeField, Min(0f)] private float _liftSpeed = 2f;
    [SerializeField, Min(0f)] private float _gravity = 18f;
    [SerializeField, Min(0f)] private float _spinSpeed = 110f;
    [Tooltip("躯干和双臂胶囊的半径倍率，用于调整尸块贴地程度")]
    [SerializeField, Range(0.1f, 1.5f)] private float _colliderRadiusScale = 0.85f;
    [Tooltip("非 Humanoid 模型按左、右顺序指定上臂；子骨骼影响的顶点或挂在其下的零件完整保留在上半身")]
    [SerializeField] private Transform[] _protectedArmRoots = System.Array.Empty<Transform>();
    [Tooltip("尸块保留的世界时间秒数；0 表示不自动回收")]
    [SerializeField, Min(0f)] private float _lifetime = 8f;
    [Tooltip("仅供编辑器预览的地面查询；运行时刚体遵循 Physics 碰撞层设置")]
    [SerializeField] private LayerMask _groundMask = Physics.DefaultRaycastLayers;

    private readonly List<Object> _ownedAssets = new List<Object>();
    private readonly List<Renderer> _hiddenRenderers = new List<Renderer>();
    private readonly List<Collider> _disabledColliders = new List<Collider>();
    private readonly List<Piece> _pieces = new List<Piece>();
    private readonly List<AnimatedLower> _animatedLowers = new List<AnimatedLower>();
    private bool _rolled;
    public bool IsSliced { get; private set; }
    public float DeathChance => _deathChance;

    private sealed class Piece
    {
        public Transform Root;
        public Vector3 Velocity;
        public Vector3 Spin;
        public Bounds Bounds;
        public bool HasBounds;
        public bool Settled;
        public Rigidbody Body;
        public float Rate = 1f;
        // 碎块自己的生存计时，挂在碎块 GameObject 上。
        public GibPieceLifetime Lifetime;
        public readonly List<GibMeshBatch> Batches = new List<GibMeshBatch>();
    }

    private sealed class AnimatedLower
    {
        public Renderer Source;
        public Mesh BakedMesh;
        public Transform Root;
        public GibMeshBatch Batch;
        public int Offset;
        public Matrix4x4 LastMatrix;
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Vector3> Normals = new List<Vector3>();
        public readonly List<Vector4> Tangents = new List<Vector4>();
    }

    public bool TrySlice(Vector3 hitDirection)
    {
        if (!isActiveAndEnabled || GetComponent<Enemy>().IsAlive || _rolled) return false;
        _rolled = true;
        return Random.value < _deathChance && CreatePieces(hitDirection);
    }

    public Renderer[] GetModelRenderers() => System.Array.FindAll(GetComponentsInChildren<Renderer>(),
        r => r.enabled && GetSourceMesh(r) != null && GetSourceMesh(r).vertexCount > 0 && r.sharedMaterials.Length > 0);

    private static Mesh GetSourceMesh(Renderer source)
    {
        if (source is SkinnedMeshRenderer skin) return skin.sharedMesh;
        return source is MeshRenderer && source.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
    }

    public string GetSetupError()
    {
        if (_cutoutShader == null || !_cutoutShader.isSupported) return "请指定受支持的 Cutout Shader。";
        Renderer[] renderers = GetModelRenderers();
        if (renderers.Length == 0) return "模型中没有可用的 SkinnedMeshRenderer 或 MeshRenderer + MeshFilter。";
        foreach (Renderer renderer in renderers)
            if (renderer is MeshRenderer && !GetSourceMesh(renderer).isReadable)
                return $"零件 {renderer.name} 的网格不可读，请在模型导入设置中开启 Read/Write。";
        return null;
    }

    private bool CreatePieces(Vector3 hitDirection)
    {
        string error = GetSetupError();
        if (error != null) { Debug.LogWarning("[Gib] " + error, this); return false; }
        Renderer[] visible = GetModelRenderers();

        Bounds bounds = visible[0].bounds;
        foreach (var renderer in visible) bounds.Encapsulate(renderer.bounds);
        Transform waist = _waist;
        Animator animator = GetComponentInChildren<Animator>();
        if (waist == null && animator != null && animator.isHuman)
            waist = animator.GetBoneTransform(HumanBodyBones.Hips);
        Vector3 pivot = waist != null ? waist.position + Vector3.up * _waistOffset
            : new Vector3(bounds.center.x, Mathf.Lerp(bounds.min.y, bounds.max.y, _fallbackHeight), bounds.center.z);
        Vector3 direction = Vector3.ProjectOnPlane(hitDirection, Vector3.up).normalized;
        if (direction.sqrMagnitude < 0.001f) direction = transform.forward;
        var upper = CreatePiece("WaistCut_Upper", pivot, direction * _separationSpeed + Vector3.up * _liftSpeed,
            Vector3.Cross(Vector3.up, direction) * _spinSpeed);
        var lower = CreatePiece("WaistCut_Lower", pivot, Vector3.zero, Vector3.zero);
        lower.Settled = true;
        var colliderPoints = new[] { new List<Vector3>(), new List<Vector3>(), new List<Vector3>() };
        var armRoots = new List<Transform>();
        if (animator != null && animator.isHuman)
        {
            armRoots.Add(animator.GetBoneTransform(HumanBodyBones.LeftUpperArm));
            armRoots.Add(animator.GetBoneTransform(HumanBodyBones.RightUpperArm));
        }
        armRoots.AddRange(_protectedArmRoots);
        var materialCache = new Dictionary<Material, Material>();
        Material capMaterial = null;
        Material GetMaterial(Material source)
        {
            if (source == null) return capMaterial ?? (capMaterial = CreateMaterial(null));
            if (!materialCache.TryGetValue(source, out Material result))
                materialCache.Add(source, result = CreateMaterial(source));
            return result;
        }

        foreach (var source in visible)
        {
            Mesh sourceMesh = GetSourceMesh(source);
            var mesh = source is SkinnedMeshRenderer ? new Mesh() : Instantiate(sourceMesh);
            if (source is SkinnedMeshRenderer skin) skin.BakeMesh(mesh);
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector4[] tangents = mesh.tangents;
            Matrix4x4 matrix = source.transform.localToWorldMatrix;
            Matrix4x4 normalMatrix = matrix.inverse.transpose;
            float handedness = matrix.determinant < 0f ? -1f : 1f;
            int[] vertexParts = GetVertexParts(source, armRoots);
            var distances = new List<Vector2>(vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = matrix.MultiplyPoint3x4(vertices[i]) - pivot;
                if (i < normals.Length) normals[i] = normalMatrix.MultiplyVector(normals[i]).normalized;
                if (i < tangents.Length) tangents[i] = TransformTangent(tangents[i], matrix, handedness);
                float distance = vertexParts[i] > 0 ? Mathf.Max(0.1f, Mathf.Abs(vertices[i].y)) : vertices[i].y;
                distances.Add(new Vector2(distance, 0f));
                if (distance >= 0f)
                {
                    Include(upper, vertices[i]);
                    colliderPoints[vertexParts[i]].Add(vertices[i]);
                }
                Include(lower, new Vector3(vertices[i].x, Mathf.Min(0f, vertices[i].y), vertices[i].z));
            }
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.RecalculateBounds();
            mesh.SetUVs(1, distances);
            Material[] materials = System.Array.ConvertAll(source.sharedMaterials, GetMaterial);
            FindBatch(upper, source).Append(mesh, materials, 1f);
            GibMeshBatch lowerBatch = FindBatch(lower, source);
            int offset = lowerBatch.Vertices.Count;
            if (lowerBatch.Append(mesh, materials, -1f))
            {
                var animated = new AnimatedLower { Source = source,
                    BakedMesh = source is SkinnedMeshRenderer ? mesh : null, Root = lower.Root,
                    Batch = lowerBatch, Offset = offset, LastMatrix = lower.Root.worldToLocalMatrix * matrix };
                // 普通零件的原始网格不变，只需缓存一次；上半身独占零件完全不参与后续动画更新。
                if (animated.BakedMesh == null)
                {
                    sourceMesh.GetVertices(animated.Vertices);
                    sourceMesh.GetNormals(animated.Normals);
                    sourceMesh.GetTangents(animated.Tangents);
                }
                _animatedLowers.Add(animated);
                if (animated.BakedMesh != null) _ownedAssets.Add(mesh);
                else Release(mesh);
            }
            else Release(mesh);
            source.enabled = false;
            _hiddenRenderers.Add(source);
        }
        foreach (GibMeshBatch batch in lower.Batches)
        {
            Mesh mesh = batch.Build("WaistCut_Lower_Mesh");
            if (mesh == null) continue;
            _ownedAssets.Add(mesh);
            batch.GenerateCap();
            if (batch.Cap.Mesh != null)
            {
                _ownedAssets.Add(batch.Cap.Mesh);
                foreach (Vector3 vertex in batch.Cap.Mesh.vertices) Include(upper, vertex);
                FindBatch(upper, batch.Source).AddCap(batch.Cap.Mesh, GetMaterial(null));
                batch.AddCap(batch.Cap.Mesh, GetMaterial(null));
                batch.Build(mesh.name);
            }
            mesh.MarkDynamic();
            AddRenderer(lower, mesh, batch.Materials.ToArray(), -1f, true, batch.Source);
        }
        foreach (GibMeshBatch batch in upper.Batches)
        {
            Mesh mesh = batch.Build("WaistCut_Upper_Mesh");
            if (mesh == null) continue;
            _ownedAssets.Add(mesh);
            AddRenderer(upper, mesh, batch.Materials.ToArray(), 1f, true, batch.Source);
        }
        // 尸块仅作表现，避免原来的完整胶囊继续挡住玩家或地面查询。
        foreach (Collider collider in GetComponentsInChildren<Collider>())
            if (collider.enabled)
            {
                _disabledColliders.Add(collider);
                collider.enabled = false;
            }
        IsSliced = true;
        if (Application.isPlaying)
        {
            // 只给脱离的上半身添加碰撞；下半身仍由死亡动画驱动。
            AddUpperColliders(upper, colliderPoints, animator, armRoots);
            upper.Body = upper.Root.gameObject.AddComponent<Rigidbody>();
            upper.Body.useGravity = false;
            upper.Body.interpolation = RigidbodyInterpolation.Interpolate;
            upper.Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            upper.Body.velocity = upper.Velocity;
            upper.Body.angularVelocity = upper.Spin * Mathf.Deg2Rad;
        }
        return true;
    }

    private static int GetTransformPart(Transform bone, List<Transform> roots)
    {
        int index = roots.FindIndex(root => root != null && bone != null && (bone == root || bone.IsChildOf(root)));
        return index < 0 ? 0 : 1 + index % 2;
    }

    private static int[] GetVertexParts(Renderer source, List<Transform> roots)
    {
        var result = new int[GetSourceMesh(source).vertexCount];
        if (!(source is SkinnedMeshRenderer skin))
        {
            int part = GetTransformPart(source.transform, roots);
            for (int i = 0; i < result.Length; i++) result[i] = part;
            return result;
        }
        int[] boneParts = System.Array.ConvertAll(skin.bones, bone => GetTransformPart(bone, roots));
        BoneWeight[] weights = skin.sharedMesh.boneWeights;
        for (int i = 0; i < weights.Length; i++)
        {
            BoneWeight w = weights[i];
            // 保护任何受手臂骨骼影响的顶点，包含肩部混合权重和手指。
            float strongest = 0f;
            Assign(w.boneIndex0, w.weight0);
            Assign(w.boneIndex1, w.weight1);
            Assign(w.boneIndex2, w.weight2);
            Assign(w.boneIndex3, w.weight3);
            void Assign(int index, float weight)
            {
                if (index < 0 || index >= boneParts.Length || boneParts[index] == 0 || weight <= strongest) return;
                result[i] = boneParts[index];
                strongest = weight;
            }
        }
        return result;
    }

    private void AddUpperColliders(Piece upper, List<Vector3>[] parts, Animator animator, List<Transform> armRoots)
    {
        // 玩家与地面共用 Default，不能禁用整层碰撞；只忽略实际玩家的碰撞体。
        var playerColliders = new List<Collider>();
        foreach (var player in FindObjectsOfType<PlayerMotor>())
            playerColliders.AddRange(player.GetComponentsInChildren<Collider>(true));
        upper.Root.gameObject.layer = LayerMask.NameToLayer("Ignore Raycast");
        string[] names = { "TorsoCollider", "LeftArmCollider", "RightArmCollider" };
        for (int part = 0; part < parts.Length; part++)
        {
            List<Vector3> points = parts[part];
            if (points.Count == 0) continue;
            Vector3 axis = transform.up;
            if (part > 0 && armRoots.Count >= part && armRoots[part - 1] != null)
            {
                Vector3 shoulder = armRoots[part - 1].position;
                Vector3 end = shoulder;
                foreach (Vector3 point in points)
                    if ((upper.Root.position + point - shoulder).sqrMagnitude > (end - shoulder).sqrMagnitude)
                        end = upper.Root.position + point;
                if (animator != null && animator.isHuman)
                {
                    Transform hand = animator.GetBoneTransform(part == 1 ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                    if (hand != null) end = hand.position;
                }
                if ((end - shoulder).sqrMagnitude > 0.0001f) axis = (end - shoulder).normalized;
            }
            Quaternion rotation = Quaternion.FromToRotation(Vector3.up, axis);
            Quaternion inverse = Quaternion.Inverse(rotation);
            var bounds = new Bounds(inverse * points[0], Vector3.zero);
            foreach (Vector3 point in points) bounds.Encapsulate(inverse * point);
            var child = new GameObject(names[part]);
            child.layer = upper.Root.gameObject.layer;
            child.transform.SetParent(upper.Root, false);
            child.transform.localRotation = rotation;
            var capsule = child.AddComponent<CapsuleCollider>();
            capsule.direction = 1;
            capsule.center = bounds.center;
            capsule.radius = Mathf.Max(0.01f, Mathf.Max(bounds.extents.x, bounds.extents.z) * _colliderRadiusScale);
            capsule.height = Mathf.Max(bounds.size.y, capsule.radius * 2f);
            foreach (Collider playerCollider in playerColliders)
                Physics.IgnoreCollision(capsule, playerCollider);
        }
    }

    private Piece CreatePiece(string name, Vector3 pivot, Vector3 velocity, Vector3 spin)
    {
        var root = new GameObject(name);
        root.transform.position = pivot;
        // 留在敌人所在场景；由组件显式持有，避免敌人缩放二次作用于烘焙后的世界坐标。
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, gameObject.scene);
        // 生存计时交给碎块自己：敌人被对象池回收时会失活，计时若挂在敌人身上
        // 就会跟着停摆或把碎块一起销毁。这里只把策划配的 _lifetime 注入进去。
        var lifetime = root.AddComponent<GibPieceLifetime>();
        lifetime.Configure(_lifetime);
        var piece = new Piece { Root = root.transform, Velocity = velocity, Spin = spin, Lifetime = lifetime };
        _pieces.Add(piece);
        return piece;
    }

    private static void Include(Piece piece, Vector3 point)
    {
        if (!piece.HasBounds) { piece.Bounds = new Bounds(point, Vector3.zero); piece.HasBounds = true; }
        else piece.Bounds.Encapsulate(point);
    }

    private static GibMeshBatch FindBatch(Piece piece, Renderer source)
    {
        GibMeshBatch batch = piece.Batches.Find(candidate => candidate.Matches(source));
        if (batch == null) piece.Batches.Add(batch = new GibMeshBatch(source));
        return batch;
    }

    private void AddRenderer(Piece piece, Mesh mesh, Material[] materials, float side, bool animated = false,
        Renderer source = null)
    {
        var child = new GameObject(mesh.name);
        child.layer = source != null ? source.gameObject.layer : gameObject.layer;
        child.transform.SetParent(piece.Root, false);
        child.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = child.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = materials;
        // 把渲染用到的资源所有权转交碎块自己：敌人被池复用时会跑 ResetEffect，
        // 若资源还挂在敌人身上就会被那一刻释放，碎块随即变成空白。
        // 用 _ownedAssets.Remove 的返回值做"首个认领者"判定——
        // 材质在 GetMaterial 里是缓存共享的，同一份可能被上下半身同时引用，
        // Remove 成功才说明是本碎块第一个拿到它，避免重复销毁。
        if (piece.Lifetime != null)
        {
            if (_ownedAssets.Remove(mesh)) piece.Lifetime.Own(mesh);
            foreach (Material material in materials)
            {
                if (material != null && _ownedAssets.Remove(material)) piece.Lifetime.Own(material);
            }
        }
        if (source != null)
        {
            renderer.shadowCastingMode = source.shadowCastingMode;
            renderer.receiveShadows = source.receiveShadows;
            renderer.lightProbeUsage = source.lightProbeUsage;
            renderer.reflectionProbeUsage = source.reflectionProbeUsage;
            renderer.probeAnchor = source.probeAnchor;
            renderer.renderingLayerMask = source.renderingLayerMask;
        }
        // 下半身用死亡瞬间的距离裁切，避免倒地时穿过原平面导致身体重新显现。
        var properties = new MaterialPropertyBlock();
        properties.SetFloat("_CutSide", side);
        properties.SetFloat("_UseCutDistance", animated ? 1f : 0f);
        renderer.SetPropertyBlock(properties);
    }

    private Material CreateMaterial(Material source)
    {
        var material = new Material(_cutoutShader);
        _ownedAssets.Add(material);
        // 保留 URP Lit 的贴图、HDR 自发光、表面参数和关键字，仅在运行时副本上追加裁切。
        if (source != null) material.CopyPropertiesFromMaterial(source);
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        string map = source != null && source.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
        string color = source != null && source.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
        if (source != null && source.HasProperty(map))
        {
            material.SetTexture("_BaseMap", source.GetTexture(map));
            material.SetTextureScale("_BaseMap", source.GetTextureScale(map));
            material.SetTextureOffset("_BaseMap", source.GetTextureOffset(map));
        }
        material.SetColor("_BaseColor", source != null && source.HasProperty(color) ? source.GetColor(color) : _cutColor);
        material.SetFloat("_CutSurface", source == null ? 1f : 0f);
        material.SetColor("_CutColor", _cutColor);
        material.SetVector("_CutPlane", new Vector4(0f, 1f, 0f, 0f));
        material.SetFloat("_CutEdge", _edgeWidth);
        return material;
    }

    private static Vector4 TransformTangent(Vector4 tangent, Matrix4x4 matrix, float handedness)
    {
        // 法线走逆转置，切线走模型矩阵；镜像缩放还会翻转切线空间的手性。
        Vector3 direction = matrix.MultiplyVector(new Vector3(tangent.x, tangent.y, tangent.z)).normalized;
        return new Vector4(direction.x, direction.y, direction.z, tangent.w * handedness);
    }

    private void FixedUpdate()
    {
        foreach (Piece piece in _pieces)
        {
            if (piece.Body == null) continue;
            float rate = TimeManager.WorldRate;
            if (piece.Body.IsSleeping() && Mathf.Approximately(rate, piece.Rate)) continue;
            if (piece.Rate > 0f)
            {
                piece.Velocity = piece.Body.velocity / piece.Rate;
                piece.Spin = piece.Body.angularVelocity / piece.Rate;
            }
            piece.Body.isKinematic = rate <= 0f;
            if (rate > 0f)
            {
                piece.Body.velocity = piece.Velocity * rate;
                piece.Body.angularVelocity = piece.Spin * rate;
                piece.Body.AddForce(Vector3.down * (_gravity * rate * rate), ForceMode.Acceleration);
            }
            piece.Rate = rate;
        }
        Simulate(TimeManager.WorldFixedDeltaTime);
    }

    private void LateUpdate() => UpdateLowerBodies();

    private void UpdateLowerBodies()
    {
        foreach (AnimatedLower lower in _animatedLowers)
        {
            if (lower.Source == null) continue;
            Matrix4x4 matrix = lower.Root.worldToLocalMatrix * lower.Source.transform.localToWorldMatrix;
            if (lower.Source is SkinnedMeshRenderer skin)
            {
                skin.BakeMesh(lower.BakedMesh);
                lower.BakedMesh.GetVertices(lower.Vertices);
                lower.BakedMesh.GetNormals(lower.Normals);
                lower.BakedMesh.GetTangents(lower.Tangents);
            }
            else if (matrix == lower.LastMatrix) continue;
            lower.LastMatrix = matrix;
            Matrix4x4 normalMatrix = matrix.inverse.transpose;
            float handedness = matrix.determinant < 0f ? -1f : 1f;
            for (int i = 0; i < lower.Vertices.Count; i++)
            {
                int target = lower.Offset + i;
                lower.Batch.Vertices[target] = matrix.MultiplyPoint3x4(lower.Vertices[i]);
                if (i < lower.Normals.Count)
                    lower.Batch.Normals[target] = normalMatrix.MultiplyVector(lower.Normals[i]).normalized;
                if (i < lower.Tangents.Count)
                    lower.Batch.Tangents[target] = TransformTangent(lower.Tangents[i], matrix, handedness);
            }
            lower.Batch.Dirty = true;
        }
        foreach (Piece piece in _pieces)
            if (piece.Settled)
                foreach (GibMeshBatch batch in piece.Batches) batch.UpdateMesh();
    }

    private void Simulate(float dt, float? previewFloor = null)
    {
        if (!IsSliced || _pieces.Count == 0 || dt <= 0f) return;
        // 这里不再计时销毁：生存计时已交给每块碎块自己的 GibPieceLifetime，
        // 两处同时倒计时会互相打架（敌人还活着时碎块就被这里提前销毁）。
        foreach (Piece piece in _pieces)
        {
            if (piece.Settled || piece.Body != null) continue;
            piece.Velocity += Vector3.down * (_gravity * dt);
            piece.Root.position += piece.Velocity * dt;
            piece.Root.Rotate(piece.Spin * dt, Space.World);
            Vector3 extents = piece.Bounds.extents;
            float bottom = piece.Root.TransformPoint(piece.Bounds.center).y
                - Mathf.Abs(piece.Root.right.y) * extents.x
                - Mathf.Abs(piece.Root.up.y) * extents.y
                - Mathf.Abs(piece.Root.forward.y) * extents.z;
            // 从尸块上方探测地面，查询长度包含本帧下落距离，避免高速落地穿透。
            float reach = piece.Bounds.size.magnitude + piece.Velocity.magnitude * dt;
            Vector3 origin = piece.Root.position + Vector3.up * reach;
            float floor = previewFloor ?? (Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
                reach + Mathf.Max(0f, piece.Root.position.y - bottom), _groundMask, QueryTriggerInteraction.Ignore)
                ? hit.point.y : float.NegativeInfinity);
            if (piece.Velocity.y <= 0f && bottom <= floor)
            {
                piece.Root.position += Vector3.up * (floor - bottom);
                piece.Settled = true;
            }
        }
    }

    public void ResetEffect()
    {
        ReleasePieces();
        foreach (var renderer in _hiddenRenderers) if (renderer != null) renderer.enabled = true;
        foreach (var collider in _disabledColliders) if (collider != null) collider.enabled = true;
        _hiddenRenderers.Clear();
        _disabledColliders.Clear();
        _rolled = false;
        IsSliced = false;
    }

    // 解除对碎块的驱动，但**不销毁它们**。
    //
    // 碎块的存亡由各自的 GibPieceLifetime 决定（到点自己消失；生存时间配 0 就常驻）。
    // 这里原先会 Destroy 掉所有碎块，导致敌人被对象池复用时（ResetHealth → ResetEffect）
    // 把场上还活着的碎块一并清掉——表现就是"碎块过一会儿突然全部消失"。
    //
    // 解除驱动前必须把重力交还给物理引擎：上半身的 Rigidbody 是 useGravity = false，
    // 重力一直靠本组件 FixedUpdate 每帧 AddForce 喂；本组件一旦不再驱动它
    // （失活或解除引用），它就会带着当前速度永远飘在空中。
    private void ReleasePieces()
    {
        foreach (Piece piece in _pieces)
        {
            // 交还重力后，上半身由物理引擎自然落地；下半身没有刚体，
            // 本来就是静止的死亡姿态，解除驱动即冻结在原地，符合预期。
            if (piece.Body != null) piece.Body.useGravity = true;
        }
        _pieces.Clear();
        _animatedLowers.Clear();
        // 渲染用的 Mesh/Material 已在 AddRenderer 里转交给各碎块持有，
        // 留在 _ownedAssets 里的都是中间产物（切面封口网格、烘焙网格等），此处释放是安全的。
        foreach (Object asset in _ownedAssets) if (asset != null) Release(asset);
        _ownedAssets.Clear();
    }

    // 敌人失活（被对象池回收）时：解除驱动，但保留碎块。
    //
    // 为什么不能沿用原来的 OnDisable → ResetEffect：那会销毁场上还活着的碎块。
    // 也不能简单清空引用就完事——上半身的重力靠本组件 FixedUpdate 喂，
    // 清引用而不交还重力会让它带着速度永远飘在空中（实机踩过这个坑）。
    // ReleasePieces 现在同时做这两件事：交还重力 + 解除引用，且不销毁碎块。
    private void OnDisable()
    {
        ReleasePieces();
    }

    private static void Release(Object asset)
    {
        if (Application.isPlaying) Destroy(asset);
        else DestroyImmediate(asset);
    }

#if UNITY_EDITOR
    // 仅允许隔离预览场景绕过死亡和概率，不能在原场景意外触发。
    public bool BeginPreview(Vector3 direction)
    {
        if (Application.isPlaying || !UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene)) return false;
        ResetEffect();
        return CreatePieces(direction);
    }

    public void SimulatePreview(float dt, float floor)
    {
        if (!Application.isPlaying && UnityEditor.SceneManagement.EditorSceneManager.IsPreviewScene(gameObject.scene))
        {
            Simulate(dt, floor);
            UpdateLowerBodies();
        }
    }
#endif
}
