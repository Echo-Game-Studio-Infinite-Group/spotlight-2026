using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 只处理挡在镜头与角色之间的墙；墙的碰撞体始终保留给移动系统。
[DefaultExecutionOrder(100)]
public sealed class CameraWallFade : MonoBehaviour
{
    [SerializeField] private Transform _target;
    [SerializeField] private Shader _transparentToonShader;
    [SerializeField, Range(0f, 1f)] private float _occludedAlpha = 0.2f;
    [SerializeField, Min(0f)] private float _fadeSpeed = 8f;
    [SerializeField, Min(0f)] private float _probeRadius = 0.1f;
    [SerializeField] private LayerMask _wallLayers = ~0;

    private Collider[] _hits = new Collider[64];
    private readonly Dictionary<Renderer, FadeState> _fading = new Dictionary<Renderer, FadeState>();
    private readonly HashSet<Renderer> _occluded = new HashSet<Renderer>();
    private readonly List<Renderer> _finished = new List<Renderer>();
    private readonly List<Renderer> _children = new List<Renderer>();
    private Camera _camera;
    private Transform _playerRoot;

    private sealed class FadeState
    {
        public Material[] OriginalMaterials;
        public Material[] FadeMaterials;
        public MaterialPropertyBlock[] OriginalProperties;
        public MaterialPropertyBlock[] FadeProperties;
        public Color[] Colors;
        public string[] ColorProperties;
        public float Alpha = 1f;
    }

    // 保留旧装配入口；遮挡判断不再依赖指定材质。
    public void Configure(Transform target, Material wallMaterial, Camera camera = null)
    {
        RestoreAll();
        _target = target;
        _playerRoot = null;
        if (camera != null) _camera = camera;
    }

    private void LateUpdate()
    {
        UpdateFade(TimeManager.UnscaledDeltaTime);
    }

    private void UpdateFade(float deltaTime)
    {
        _occluded.Clear();
        if (_camera == null) _camera = Camera.main;
        if (_target != null && _camera != null)
        {
            if (_playerRoot == null)
            {
                PlayerMotor motor = _target.GetComponentInParent<PlayerMotor>();
                _playerRoot = motor != null ? motor.transform : _target;
            }

            // 重叠查询包含两端，镜头陷入厚墙时也能找到遮挡；满缓冲时扩容重查以免漏墙。
            int count;
            do
            {
                count = Physics.OverlapCapsuleNonAlloc(_target.position, _camera.transform.position,
                    Mathf.Max(0.001f, _probeRadius), _hits, _wallLayers, QueryTriggerInteraction.Ignore);
                if (count < _hits.Length) break;
                Array.Resize(ref _hits, _hits.Length * 2);
            } while (true);

            for (int i = 0; i < count; i++)
            {
                Collider collider = _hits[i];
                if (collider.transform.IsChildOf(_playerRoot)) continue;
                Renderer renderer = collider.GetComponentInParent<Renderer>();
                if (renderer != null) AddOccluder(renderer);
                else
                {
                    collider.GetComponentsInChildren(false, _children);
                    foreach (Renderer child in _children) AddOccluder(child);
                }
            }
        }

        foreach (Renderer renderer in _occluded)
            if (!_fading.ContainsKey(renderer)) BeginFade(renderer);

        _finished.Clear();
        foreach (KeyValuePair<Renderer, FadeState> entry in _fading)
        {
            Renderer renderer = entry.Key;
            if (renderer == null) { _finished.Add(renderer); continue; }
            FadeState state = entry.Value;
            float wanted = _occluded.Contains(renderer) ? _occludedAlpha : 1f;
            state.Alpha = Mathf.MoveTowards(state.Alpha, wanted, Mathf.Max(0f, _fadeSpeed) * deltaTime);
            for (int i = 0; i < state.FadeMaterials.Length; i++)
            {
                if (state.ColorProperties[i] == null) continue;
                Color color = state.Colors[i];
                color.a *= state.Alpha;
                state.FadeProperties[i].SetColor(state.ColorProperties[i], color);
                renderer.SetPropertyBlock(state.FadeProperties[i], i);
            }
            if (wanted >= 1f && state.Alpha >= 1f) _finished.Add(renderer);
        }
        foreach (Renderer renderer in _finished) EndFade(renderer);
    }

    private void AddOccluder(Renderer renderer)
    {
        if (renderer.enabled && !renderer.transform.IsChildOf(_playerRoot) &&
            (renderer is MeshRenderer || renderer is SkinnedMeshRenderer))
            _occluded.Add(renderer);
    }

    private void BeginFade(Renderer renderer)
    {
        Material[] originals = renderer.sharedMaterials;
        var state = new FadeState
        {
            OriginalMaterials = originals,
            FadeMaterials = (Material[])originals.Clone(),
            OriginalProperties = new MaterialPropertyBlock[originals.Length],
            FadeProperties = new MaterialPropertyBlock[originals.Length],
            Colors = new Color[originals.Length],
            ColorProperties = new string[originals.Length]
        };
        for (int i = 0; i < originals.Length; i++)
        {
            Material original = originals[i];
            if (original == null) continue;
            string colorProperty = original.HasProperty("_BaseColor") ? "_BaseColor" :
                original.HasProperty("_Color") ? "_Color" : null;
            if (colorProperty == null) continue;
            var properties = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(properties, i);
            state.OriginalProperties[i] = properties;
            var fadeProperties = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(fadeProperties, i);
            // 索引属性块优先于 Renderer 属性块，透明期间必须保留同样的覆盖关系。
            if (fadeProperties.isEmpty) renderer.GetPropertyBlock(fadeProperties);
            state.Colors[i] = fadeProperties.HasColor(colorProperty)
                ? fadeProperties.GetColor(colorProperty) : original.GetColor(colorProperty);
            state.ColorProperties[i] = colorProperty;
            state.FadeProperties[i] = fadeProperties;
            state.FadeMaterials[i] = CreateFadeMaterial(original);
        }
        renderer.sharedMaterials = state.FadeMaterials;
        _fading.Add(renderer, state);
    }

    private Material CreateFadeMaterial(Material original)
    {
        Shader shader = original.shader;
        // 不透明 NPR 的 Pass 固定写深度，必须使用项目内相同属性布局的透明版本。
        if (original.shader.name == "OurFunction/NPR/Deferred Toon")
        {
            if (_transparentToonShader == null)
                _transparentToonShader = Shader.Find("OurFunction/NPR/Transparent Toon");
            if (_transparentToonShader != null) shader = _transparentToonShader;
        }
        // 地图使用材质变体；从 Shader 新建独立材质，避免继承父材质后无法切换透明 Shader。
        var material = new Material(shader) { name = original.name + " (Camera Fade)" };
        material.CopyPropertiesFromMaterial(original);
        material.SetOverrideTag("RenderType", "Transparent");
        SetFloat(material, "_Surface", 1f);
        SetFloat(material, "_Mode", 3f);
        SetFloat(material, "_Blend", 0f);
        SetFloat(material, "_BlendModePreserveSpecular", 0f);
        SetFloat(material, "_SrcBlend", (float)BlendMode.SrcAlpha);
        SetFloat(material, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        SetFloat(material, "_SrcBlendAlpha", (float)BlendMode.One);
        SetFloat(material, "_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        SetFloat(material, "_ZWrite", 0f);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.DisableKeyword("_ALPHAMODULATE_ON");
        material.DisableKeyword("_ALPHATEST_ON");
        material.SetShaderPassEnabled("DepthOnly", false);
        material.SetShaderPassEnabled("DepthNormals", false);
        material.SetShaderPassEnabled("ShadowCaster", false);
        material.renderQueue = (int)RenderQueue.Transparent;
        return material;
    }

    private static void SetFloat(Material material, string property, float value)
    {
        if (material.HasProperty(property)) material.SetFloat(property, value);
    }

    private void EndFade(Renderer renderer)
    {
        FadeState state = _fading[renderer];
        if (renderer != null)
        {
            renderer.sharedMaterials = state.OriginalMaterials;
            for (int i = 0; i < state.OriginalProperties.Length; i++)
                if (state.OriginalProperties[i] != null)
                    renderer.SetPropertyBlock(state.OriginalProperties[i].isEmpty ? null : state.OriginalProperties[i], i);
        }
        for (int i = 0; i < state.FadeMaterials.Length; i++)
            if (state.FadeMaterials[i] != state.OriginalMaterials[i]) Destroy(state.FadeMaterials[i]);
        _fading.Remove(renderer);
    }

    private void RestoreAll()
    {
        _finished.Clear();
        _finished.AddRange(_fading.Keys);
        foreach (Renderer renderer in _finished) EndFade(renderer);
        _occluded.Clear();
    }

    private void OnDisable() => RestoreAll();
}
