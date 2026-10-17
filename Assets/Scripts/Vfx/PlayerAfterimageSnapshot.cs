using System;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class PlayerAfterimageSnapshot : IDisposable
{
    private static readonly int BodyColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int EdgeColorId = Shader.PropertyToID("_EdgeColor");
    private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
    private static readonly int EdgePowerId = Shader.PropertyToID("_EdgePower");
    private readonly Mesh[] _meshes;
    private readonly Matrix4x4[] _matrices;
    private readonly int[] _layers;
    private readonly bool[] _visible;
    private readonly MaterialPropertyBlock _properties = new MaterialPropertyBlock();

    public bool IsActive { get; private set; }
    public float Age { get; private set; }
    public Vector3 Position { get; private set; }
    public int PartCount => _meshes.Length;
    public Mesh GetMesh(int part) => _meshes[part];
    public Matrix4x4 GetMatrix(int part) => _matrices[part];

    public PlayerAfterimageSnapshot(int parts, int slot)
    {
        _meshes = new Mesh[parts];
        _matrices = new Matrix4x4[parts];
        _layers = new int[parts];
        _visible = new bool[parts];
        for (int i = 0; i < parts; i++)
        {
            _meshes[i] = new Mesh { name = "PlayerAfterimage_" + slot + "_" + i, hideFlags = HideFlags.HideAndDontSave };
            _meshes[i].MarkDynamic();
        }
    }

    public bool Capture(SkinnedMeshRenderer[] sources, Vector3 position)
    {
        IsActive = false;
        for (int i = 0; i < _meshes.Length; i++)
        {
            SkinnedMeshRenderer source = sources[i];
            _visible[i] = source != null && source.enabled && source.gameObject.activeInHierarchy && source.sharedMesh != null;
            if (!_visible[i]) continue;
            // 顶点保持蒙皮节点的局部空间，完整世界矩阵只保存一次；之后不会再跟随人物骨骼。
            source.BakeMesh(_meshes[i], false);
            _matrices[i] = source.localToWorldMatrix;
            _layers[i] = source.gameObject.layer;
            IsActive = true;
        }
        Position = position;
        Age = 0f;
        return IsActive;
    }

    public void Advance(float deltaTime, float lifetime)
    {
        if (!IsActive) return;
        Age += deltaTime;
        if (Age >= lifetime) IsActive = false;
    }

    public void Clear() => IsActive = false;

    public void Draw(PlayerAfterimageSettings settings, Camera camera = null)
    {
        if (!IsActive || settings.Material == null) return;
        float progress = Mathf.Clamp01(Age / Mathf.Max(0.01f, settings.Lifetime));
        float fade = settings.Fade != null ? settings.Fade.Evaluate(progress) : 1f - progress;
        _properties.SetColor(BodyColorId, settings.BodyColor);
        _properties.SetColor(EdgeColorId, settings.EdgeColor);
        _properties.SetFloat(OpacityId, settings.Opacity * Mathf.Clamp01(fade));
        _properties.SetFloat(EdgePowerId, settings.EdgePower);
        for (int i = 0; i < _meshes.Length; i++)
        {
            if (!_visible[i]) continue;
            for (int submesh = 0; submesh < _meshes[i].subMeshCount; submesh++)
                Graphics.DrawMesh(_meshes[i], _matrices[i], settings.Material, _layers[i], camera, submesh,
                    _properties, ShadowCastingMode.Off, false, null, LightProbeUsage.Off);
        }
    }

    public void Dispose()
    {
        IsActive = false;
        foreach (Mesh mesh in _meshes)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(mesh);
            else UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
}
