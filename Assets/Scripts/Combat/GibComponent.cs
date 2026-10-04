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
    [Tooltip("非 Humanoid 模型可手动指定左右上臂；这些骨骼及子骨骼所属顶点完整保留在上半身")]
    [SerializeField] private Transform[] _protectedArmRoots = System.Array.Empty<Transform>();
    [Tooltip("尸块保留的世界时间秒数；0 表示不自动回收")]
    [SerializeField, Min(0f)] private float _lifetime = 8f;
    [Tooltip("仅供编辑器预览的地面查询；运行时刚体遵循 Physics 碰撞层设置")]
    [SerializeField] private LayerMask _groundMask = Physics.DefaultRaycastLayers;

    private readonly List<Object> _ownedAssets = new List<Object>();
    private readonly List<SkinnedMeshRenderer> _hiddenRenderers = new List<SkinnedMeshRenderer>();
    private readonly List<Collider> _disabledColliders = new List<Collider>();
    private readonly List<Piece> _pieces = new List<Piece>();
    private readonly List<AnimatedLower> _animatedLowers = new List<AnimatedLower>();
    private bool _rolled;
    private float _age;
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
    }

    private sealed class AnimatedLower
    {
        public SkinnedMeshRenderer Source;
        public Mesh Mesh;
        public Transform Root;
        public GibGenerator Cap;
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Vector3> Normals = new List<Vector3>();
        public readonly List<Vector2> CutDistances = new List<Vector2>();
    }

    public bool TrySlice(Vector3 hitDirection)
    {
        if (!isActiveAndEnabled || GetComponent<Enemy>().IsAlive || _rolled) return false;
        _rolled = true;
        return Random.value < _deathChance && CreatePieces(hitDirection);
    }

    private bool CreatePieces(Vector3 hitDirection)
    {
        if (_cutoutShader == null || !_cutoutShader.isSupported) return false;
        var visible = new List<SkinnedMeshRenderer>(GetComponentsInChildren<SkinnedMeshRenderer>())
            .FindAll(r => r.enabled && r.sharedMesh != null && r.sharedMaterials.Length > 0);
        if (visible.Count == 0) return false;

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

        foreach (var source in visible)
        {
            var mesh = new Mesh { name = source.name + "_DeathPose" };
            _ownedAssets.Add(mesh);
            source.BakeMesh(mesh);
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Matrix4x4 matrix = source.transform.localToWorldMatrix;
            Matrix4x4 normalMatrix = matrix.inverse.transpose;
            bool[] protectedVertices = GetProtectedVertices(source, animator);
            var distances = new List<Vector2>(vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = matrix.MultiplyPoint3x4(vertices[i]) - pivot;
                if (i < normals.Length) normals[i] = normalMatrix.MultiplyVector(normals[i]).normalized;
                float distance = protectedVertices[i] ? Mathf.Max(0.1f, Mathf.Abs(vertices[i].y)) : vertices[i].y;
                distances.Add(new Vector2(distance, 0f));
                if (distance >= 0f) Include(upper, vertices[i]);
                Include(lower, new Vector3(vertices[i].x, Mathf.Min(0f, vertices[i].y), vertices[i].z));
            }
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.RecalculateBounds();
            mesh.SetUVs(1, distances);
            Material[] materials = System.Array.ConvertAll(source.sharedMaterials, CreateMaterial);
            AddRenderer(upper, mesh, materials, 1f, true);
            var animated = new AnimatedLower { Source = source, Mesh = Instantiate(mesh), Root = lower.Root,
                Cap = new GibGenerator(mesh) };
            animated.Mesh.MarkDynamic();
            _ownedAssets.Add(animated.Mesh);
            animated.CutDistances.AddRange(distances);
            animated.Mesh.SetUVs(1, animated.CutDistances);
            _animatedLowers.Add(animated);
            AddRenderer(lower, animated.Mesh, materials, -1f, true);
            if (animated.Cap.Mesh != null)
            {
                Mesh cap = Instantiate(animated.Cap.Mesh);
                foreach (Vector3 vertex in cap.vertices) Include(upper, vertex);
                _ownedAssets.Add(cap);
                _ownedAssets.Add(animated.Cap.Mesh);
                var capMaterials = new[] { CreateMaterial(null) };
                AddRenderer(upper, cap, capMaterials, 0f);
                AddRenderer(lower, animated.Cap.Mesh, capMaterials, 0f);
            }
            source.enabled = false;
            _hiddenRenderers.Add(source);
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
            var collider = upper.Root.gameObject.AddComponent<BoxCollider>();
            collider.center = upper.Bounds.center;
            collider.size = Vector3.Max(upper.Bounds.size, Vector3.one * 0.01f);
            upper.Body = upper.Root.gameObject.AddComponent<Rigidbody>();
            upper.Body.useGravity = false;
            upper.Body.interpolation = RigidbodyInterpolation.Interpolate;
            upper.Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            upper.Body.velocity = upper.Velocity;
            upper.Body.angularVelocity = upper.Spin * Mathf.Deg2Rad;
        }
        _age = 0f;
        return true;
    }

    private bool[] GetProtectedVertices(SkinnedMeshRenderer source, Animator animator)
    {
        var roots = new List<Transform>(_protectedArmRoots);
        if (animator != null && animator.isHuman)
        {
            roots.Add(animator.GetBoneTransform(HumanBodyBones.LeftUpperArm));
            roots.Add(animator.GetBoneTransform(HumanBodyBones.RightUpperArm));
        }
        Transform[] bones = source.bones;
        var protectedBones = new bool[bones.Length];
        for (int i = 0; i < bones.Length; i++)
            protectedBones[i] = roots.Exists(root => root != null && bones[i] != null
                && (bones[i] == root || bones[i].IsChildOf(root)));
        var result = new bool[source.sharedMesh.vertexCount];
        BoneWeight[] weights = source.sharedMesh.boneWeights;
        for (int i = 0; i < weights.Length; i++)
        {
            BoneWeight w = weights[i];
            // 保护任何受手臂骨骼影响的顶点，包含肩部混合权重和手指。
            result[i] = Protected(w.boneIndex0, w.weight0) || Protected(w.boneIndex1, w.weight1)
                || Protected(w.boneIndex2, w.weight2) || Protected(w.boneIndex3, w.weight3);
        }
        return result;
        bool Protected(int index, float weight) => weight > 0f && index < protectedBones.Length && protectedBones[index];
    }

    private Piece CreatePiece(string name, Vector3 pivot, Vector3 velocity, Vector3 spin)
    {
        var root = new GameObject(name);
        root.transform.position = pivot;
        // 留在敌人所在场景；由组件显式持有，避免敌人缩放二次作用于烘焙后的世界坐标。
        UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, gameObject.scene);
        var piece = new Piece { Root = root.transform, Velocity = velocity, Spin = spin };
        _pieces.Add(piece);
        return piece;
    }

    private static void Include(Piece piece, Vector3 point)
    {
        if (!piece.HasBounds) { piece.Bounds = new Bounds(point, Vector3.zero); piece.HasBounds = true; }
        else piece.Bounds.Encapsulate(point);
    }

    private void AddRenderer(Piece piece, Mesh mesh, Material[] materials, float side, bool animated = false)
    {
        var child = new GameObject(mesh.name);
        child.layer = gameObject.layer;
        child.transform.SetParent(piece.Root, false);
        child.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = child.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = materials;
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
        string map = source != null && source.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
        string color = source != null && source.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";
        if (source != null && source.HasProperty(map))
        {
            material.SetTexture("_BaseMap", source.GetTexture(map));
            material.SetTextureScale("_BaseMap", source.GetTextureScale(map));
            material.SetTextureOffset("_BaseMap", source.GetTextureOffset(map));
        }
        material.SetColor("_BaseColor", source != null && source.HasProperty(color) ? source.GetColor(color) : _cutColor);
        material.SetColor("_CutColor", _cutColor);
        material.SetVector("_CutPlane", new Vector4(0f, 1f, 0f, 0f));
        material.SetFloat("_CutEdge", _edgeWidth);
        return material;
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
            lower.Source.BakeMesh(lower.Mesh);
            lower.Mesh.GetVertices(lower.Vertices);
            lower.Mesh.GetNormals(lower.Normals);
            Matrix4x4 matrix = lower.Root.worldToLocalMatrix * lower.Source.transform.localToWorldMatrix;
            Matrix4x4 normalMatrix = matrix.inverse.transpose;
            for (int i = 0; i < lower.Vertices.Count; i++)
            {
                lower.Vertices[i] = matrix.MultiplyPoint3x4(lower.Vertices[i]);
                if (i < lower.Normals.Count)
                    lower.Normals[i] = normalMatrix.MultiplyVector(lower.Normals[i]).normalized;
            }
            lower.Mesh.SetVertices(lower.Vertices);
            lower.Mesh.SetNormals(lower.Normals);
            lower.Mesh.SetUVs(1, lower.CutDistances);
            lower.Mesh.RecalculateBounds();
            lower.Cap.Update(lower.Vertices);
        }
    }

    private void Simulate(float dt, float? previewFloor = null)
    {
        if (!IsSliced || _pieces.Count == 0 || dt <= 0f) return;
        _age += dt;
        if (_lifetime > 0f && _age >= _lifetime) { ReleasePieces(); return; }
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

    private void ReleasePieces()
    {
        _animatedLowers.Clear();
        foreach (Piece piece in _pieces)
            if (piece.Root != null) { piece.Root.gameObject.SetActive(false); Release(piece.Root.gameObject); }
        _pieces.Clear();
        foreach (Object asset in _ownedAssets) if (asset != null) Release(asset);
        _ownedAssets.Clear();
    }

    private void OnDisable() => ResetEffect();

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
