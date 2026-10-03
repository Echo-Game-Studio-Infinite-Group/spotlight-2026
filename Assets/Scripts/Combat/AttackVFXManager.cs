using UnityEngine;

// 攻击特效管理器：由**动画事件**触发，沿用参考实现 LittleAdventure 的 PlayerVFXManager 结构。
//
// 与参考实现的差异（必须说明，否则会以为是抄漏了）：
// 参考实现用的是工程自带的 ParticleSystem / VisualEffect 资产（attack1/2/3）。
// spotlight-2026 里**没有任何粒子或 VFX 素材**，直接引用会得到空引用。
// 因此这里用「运行时生成的斩击弧」实现同样的事件驱动结构：
// 半透明弧面网格 + 加色混合，沿挥砍方向展开并淡出，结束后自销毁。
// 结构（动画事件 → 解绑父级 → Play → 播完自毁）与参考实现一致，换素材时只需替换 SpawnSlash。
[DisallowMultipleComponent]
public sealed class AttackVFXManager : MonoBehaviour
{
    [SerializeField] private Transform _origin;
    [SerializeField, Min(0.05f)] private float _slashLifetime = 0.28f;
    [SerializeField, Min(0f)] private float _slashRadius = 1.9f;
    [SerializeField, Min(0f)] private float _slashHeight = 1.1f;
    [SerializeField] private Color _slashColor = new Color(0.75f, 0.92f, 1f, 0.85f);
    [SerializeField] private bool _logPlayback;

    private static Material _slashMaterial;

    public void Configure(Transform origin, float lifetime, float radius, float height)
    {
        _origin = origin;
        _slashLifetime = Mathf.Max(0.05f, lifetime);
        _slashRadius = Mathf.Max(0f, radius);
        _slashHeight = Mathf.Max(0f, height);
    }

    // 动画事件调用：第 cnt 段攻击的特效（与参考实现的 UpdateAttack(int) 同名同签名，方便动画事件直接对接）
    public void UpdateAttack(int cnt = 1)
    {
        Debug.Log($"[AttackVFX] UpdateAttack({cnt}) 被调用，origin={(_origin != null ? _origin.name : "空(用自身)")}");
        SpawnSlash(cnt);
    }

    private void SpawnSlash(int combo)
    {
        Transform basis = _origin != null ? _origin : transform;
        GameObject slash = CreateSlashVisual(basis, combo);
        if (slash == null) return;

        // 解绑父级：特效留在原地，不跟着角色跑 —— 与参考实现 SetParent(null) 同一个理由
        slash.transform.SetParent(null, true);
        SlashEffect effect = slash.AddComponent<SlashEffect>();
        effect.Play(_slashLifetime);
        if (_logPlayback) Debug.Log($"[AttackVFX] 第 {combo} 段斩击特效已播放");
    }

    // 用运行时网格搭一个扇形剖面：外侧弧、内侧弧、两端闭合，形成一个「月牙」形状的斩击面
    private GameObject CreateSlashVisual(Transform basis, int combo)
    {
        GameObject root = new GameObject("SlashVFX");
        // 放在角色身前偏上：与手部挥砍高度大致齐平
        root.transform.position = basis.position + Vector3.up * _slashHeight;
        // 用角色朝向做基准，再按连招段数略微偏转，避免三段完全重叠
        float yaw = basis.eulerAngles.y + (combo - 1) * 18f;
        root.transform.rotation = Quaternion.Euler(70f, yaw, 0f);

        MeshFilter filter = root.AddComponent<MeshFilter>();
        MeshRenderer renderer = root.AddComponent<MeshRenderer>();
        filter.sharedMesh = BuildArcMesh();
        renderer.sharedMaterial = ResolveSlashMaterial();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return root;
    }

    // 月牙斩击面：外半径 R、内半径 0.45R、张角 150°，三角带拼出来
    private Mesh BuildArcMesh()
    {
        const int segments = 24;
        const float sweepDegrees = 150f;
        float outer = Mathf.Max(0.2f, _slashRadius);
        float inner = outer * 0.45f;

        Vector3[] vertices = new Vector3[(segments + 1) * 2];
        int[] triangles = new int[segments * 6];
        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;
            float angle = Mathf.Deg2Rad * Mathf.Lerp(-sweepDegrees * 0.5f, sweepDegrees * 0.5f, t);
            Vector3 direction = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
            vertices[i * 2] = direction * inner;
            vertices[i * 2 + 1] = direction * outer;
        }

        for (int i = 0; i < segments; i++)
        {
            int v = i * 2;
            int[] quad = { v, v + 2, v + 1, v + 1, v + 2, v + 3 };
            for (int k = 0; k < 6; k++) triangles[i * 6 + k] = quad[k];
        }

        Mesh mesh = new Mesh { name = "SlashArc" };
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // 加色混合的透明材质：工程内没有可用的特效材质，运行时创建一个最简的
    private static Material ResolveSlashMaterial()
    {
        if (_slashMaterial != null) return _slashMaterial;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Sprites/Default");

        _slashMaterial = new Material(shader) { name = "SlashVFX(Instance)" };
        Color color = new Color(0.75f, 0.92f, 1f, 0.85f);
        _slashMaterial.color = color;
        if (_slashMaterial.HasProperty("_BaseColor")) _slashMaterial.SetColor("_BaseColor", color);
        if (_slashMaterial.HasProperty("_Surface")) _slashMaterial.SetFloat("_Surface", 1f);
        if (_slashMaterial.HasProperty("_Blend")) _slashMaterial.SetFloat("_Blend", 1f);
        if (_slashMaterial.HasProperty("_SrcBlend")) _slashMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (_slashMaterial.HasProperty("_DstBlend")) _slashMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
        if (_slashMaterial.HasProperty("_ZWrite")) _slashMaterial.SetInt("_ZWrite", 0);
        _slashMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        _slashMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        return _slashMaterial;
    }

    // 单个斩击特效的生命周期：展开 + 淡出，播完自销毁
    private sealed class SlashEffect : MonoBehaviour
    {
        private MeshRenderer _renderer;
        private MaterialPropertyBlock _block;
        private float _lifetime = 0.28f;
        private float _age;

        public void Play(float lifetime)
        {
            _lifetime = Mathf.Max(0.05f, lifetime);
            _renderer = GetComponent<MeshRenderer>();
            _block = new MaterialPropertyBlock();
            transform.localScale = Vector3.one * 0.6f;
        }

        private void Update()
        {
            _age += TimeManager.UnscaledDeltaTime;
            float t = Mathf.Clamp01(_age / _lifetime);
            // 快速展开、缓慢淡出：起手有力，收尾留一点残影
            transform.localScale = Vector3.one * Mathf.Lerp(0.6f, 1.25f, Mathf.Sqrt(t));
            if (_renderer != null)
            {
                Color color = new Color(0.75f, 0.92f, 1f, 1f - t);
                _renderer.GetPropertyBlock(_block);
                _block.SetColor("_BaseColor", color);
                _block.SetColor("_Color", color);
                _renderer.SetPropertyBlock(_block);
            }

            if (_age >= _lifetime) Destroy(gameObject);
        }
    }
}
