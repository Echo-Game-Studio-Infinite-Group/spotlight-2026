using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// 只处理挡在镜头与角色之间的墙；墙的碰撞体始终保留给移动系统。
[DefaultExecutionOrder(100)]
public sealed class CameraWallFade : MonoBehaviour
{
    [SerializeField] private Transform _target;
    [SerializeField] private Material _wallMaterial;
    [SerializeField, Range(0f, 1f)] private float _occludedAlpha = 0.2f;
    [SerializeField] private float _fadeSpeed = 8f;
    [SerializeField] private float _probeRadius = 0.1f;
    [SerializeField] private LayerMask _wallLayers = ~0;

    private readonly RaycastHit[] _hits = new RaycastHit[64];
    private readonly Dictionary<Renderer, FadeState> _fading = new Dictionary<Renderer, FadeState>();
    private readonly HashSet<Renderer> _occluded = new HashSet<Renderer>();
    private readonly List<Renderer> _finished = new List<Renderer>();
    private Camera _camera;
    private Material _fadeMaterial;

    private sealed class FadeState
    {
        public Material[] OriginalMaterials;
        public MaterialPropertyBlock OriginalProperties;
        public MaterialPropertyBlock FadeProperties;
        public float Alpha = 1f;
    }

    public void Configure(Transform target, Material wallMaterial, Camera camera = null)
    {
        _target = target;
        _wallMaterial = wallMaterial;
        if (camera != null) _camera = camera;
    }

    private void LateUpdate()
    {
        if (_target == null || _wallMaterial == null) return;
        if (_camera == null) _camera = Camera.main;
        if (_camera == null) return;

        _occluded.Clear();
        Vector3 start = _target.position;
        Vector3 distance = _camera.transform.position - start;
        float length = distance.magnitude;
        if (length > 0.001f)
        {
            int count = _probeRadius > 0f
                ? Physics.SphereCastNonAlloc(start, _probeRadius, distance / length,
                    _hits, length, _wallLayers, QueryTriggerInteraction.Ignore)
                : Physics.RaycastNonAlloc(start, distance / length,
                    _hits, length, _wallLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Renderer renderer = _hits[i].collider.GetComponent<Renderer>();
                if (renderer != null && (_fading.ContainsKey(renderer) || ContainsWallMaterial(renderer)))
                    _occluded.Add(renderer);
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
            state.Alpha = Mathf.MoveTowards(state.Alpha, wanted,
                Mathf.Max(0f, _fadeSpeed) * TimeManager.UnscaledDeltaTime);
            Color color = _wallMaterial.GetColor("_BaseColor");
            color.a *= state.Alpha;
            state.FadeProperties.SetColor("_BaseColor", color);
            renderer.SetPropertyBlock(state.FadeProperties);
            if (wanted >= 1f && state.Alpha >= 1f) _finished.Add(renderer);
        }
        foreach (Renderer renderer in _finished) EndFade(renderer);
    }

    private bool ContainsWallMaterial(Renderer renderer)
    {
        foreach (Material material in renderer.sharedMaterials)
            if (material == _wallMaterial) return true;
        return false;
    }

    private void BeginFade(Renderer renderer)
    {
        if (_fadeMaterial == null) CreateFadeMaterial();
        var state = new FadeState
        {
            OriginalMaterials = renderer.sharedMaterials,
            OriginalProperties = new MaterialPropertyBlock(),
            FadeProperties = new MaterialPropertyBlock()
        };
        renderer.GetPropertyBlock(state.OriginalProperties);
        renderer.GetPropertyBlock(state.FadeProperties);
        Material[] materials = (Material[])state.OriginalMaterials.Clone();
        for (int i = 0; i < materials.Length; i++)
            if (materials[i] == _wallMaterial) materials[i] = _fadeMaterial;
        renderer.sharedMaterials = materials;
        _fading.Add(renderer, state);
    }

    private void CreateFadeMaterial()
    {
        _fadeMaterial = new Material(_wallMaterial) { name = "Wall Camera Fade" };
        _fadeMaterial.SetOverrideTag("RenderType", "Transparent");
        _fadeMaterial.SetFloat("_Surface", 1f);
        _fadeMaterial.SetFloat("_BlendModePreserveSpecular", 0f);
        _fadeMaterial.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        _fadeMaterial.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        _fadeMaterial.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        _fadeMaterial.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        _fadeMaterial.SetFloat("_ZWrite", 0f);
        _fadeMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        _fadeMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        _fadeMaterial.SetShaderPassEnabled("DepthOnly", false);
        _fadeMaterial.SetShaderPassEnabled("ShadowCaster", false);
        _fadeMaterial.renderQueue = (int)RenderQueue.Transparent;
    }

    private void EndFade(Renderer renderer)
    {
        if (!_fading.TryGetValue(renderer, out FadeState state)) return;
        if (renderer != null)
        {
            renderer.sharedMaterials = state.OriginalMaterials;
            renderer.SetPropertyBlock(state.OriginalProperties);
        }
        _fading.Remove(renderer);
    }

    private void OnDisable()
    {
        var renderers = new List<Renderer>(_fading.Keys);
        foreach (Renderer renderer in renderers) EndFade(renderer);
        _occluded.Clear();
    }

    private void OnDestroy()
    {
        if (_fadeMaterial != null) Destroy(_fadeMaterial);
    }
}
