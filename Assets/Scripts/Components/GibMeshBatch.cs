using System.Collections.Generic;
using UnityEngine;

// 相同渲染设置合为一个网格，同材质合为一个 SubMesh；保留零件的顶点区间供动画更新。
internal sealed class GibMeshBatch
{
    public readonly Renderer Source;
    public readonly List<Vector3> Vertices = new List<Vector3>();
    public readonly List<Vector3> Normals = new List<Vector3>();
    public readonly List<Vector4> Tangents = new List<Vector4>();
    public readonly List<Material> Materials = new List<Material>();
    private readonly List<Vector2> _uvs = new List<Vector2>();
    private readonly List<Vector2> _distances = new List<Vector2>();
    private readonly List<List<int>> _triangles = new List<List<int>>();
    private readonly List<Vector3> _capVertices = new List<Vector3>();
    private readonly List<Vector3> _capNormals = new List<Vector3>();
    private int _capOffset;
    public Mesh Mesh { get; private set; }
    public GibGenerator Cap { get; private set; }
    public bool Dirty;

    public GibMeshBatch(Renderer source) => Source = source;

    public bool Matches(Renderer source) => Source.gameObject.layer == source.gameObject.layer
        && Source.shadowCastingMode == source.shadowCastingMode && Source.receiveShadows == source.receiveShadows
        && Source.lightProbeUsage == source.lightProbeUsage && Source.reflectionProbeUsage == source.reflectionProbeUsage
        && Source.probeAnchor == source.probeAnchor && Source.renderingLayerMask == source.renderingLayerMask;

    public bool Append(Mesh source, Material[] materials, float side)
    {
        Vector2[] distances = source.uv2;
        var kept = new List<int>[source.subMeshCount];
        int indexCount = 0;
        for (int sub = 0; sub < kept.Length; sub++)
        {
            kept[sub] = new List<int>();
            int[] triangles = source.GetTriangles(sub);
            for (int i = 0; i < triangles.Length; i += 3)
            {
                // 完全位于被裁掉一侧的三角形不再提交 GPU，跨切面的三角形仍交给 Cutout。
                if (side != 0f && distances[triangles[i]].x * side < 0f
                    && distances[triangles[i + 1]].x * side < 0f && distances[triangles[i + 2]].x * side < 0f) continue;
                kept[sub].Add(triangles[i]);
                kept[sub].Add(triangles[i + 1]);
                kept[sub].Add(triangles[i + 2]);
            }
            indexCount += kept[sub].Count;
        }
        if (indexCount == 0) return false;

        int offset = Vertices.Count;
        Vector3[] vertices = source.vertices;
        Vector3[] normals = source.normals;
        Vector4[] tangents = source.tangents;
        Vector2[] uvs = source.uv;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vertices.Add(vertices[i]);
            Normals.Add(i < normals.Length ? normals[i] : Vector3.up);
            Tangents.Add(i < tangents.Length ? tangents[i] : new Vector4(1f, 0f, 0f, 1f));
            _uvs.Add(i < uvs.Length ? uvs[i] : Vector2.zero);
            _distances.Add(i < distances.Length ? distances[i] : Vector2.zero);
        }
        for (int sub = 0; sub < kept.Length; sub++)
        {
            if (kept[sub].Count == 0) continue;
            Material material = materials[Mathf.Min(sub, materials.Length - 1)];
            int slot = Materials.IndexOf(material);
            if (slot < 0)
            {
                slot = Materials.Count;
                Materials.Add(material);
                _triangles.Add(new List<int>());
            }
            foreach (int index in kept[sub]) _triangles[slot].Add(offset + index);
        }
        return true;
    }

    public void AddCap(Mesh cap, Material material)
    {
        _capOffset = Vertices.Count;
        Append(cap, new[] { material }, 0f);
    }

    public void GenerateCap() => Cap = new GibGenerator(Mesh);

    public Mesh Build(string name)
    {
        if (Vertices.Count == 0) return null;
        if (Mesh == null) Mesh = new Mesh { name = name };
        Mesh.indexFormat = Vertices.Count > ushort.MaxValue
            ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        UploadVertices();
        Mesh.SetUVs(0, _uvs);
        Mesh.SetUVs(1, _distances);
        Mesh.subMeshCount = _triangles.Count;
        for (int sub = 0; sub < _triangles.Count; sub++) Mesh.SetTriangles(_triangles[sub], sub, false);
        Mesh.RecalculateBounds();
        return Mesh;
    }

    public void UpdateMesh()
    {
        if (!Dirty) return;
        if (Cap?.Mesh != null)
        {
            Cap.Update(Vertices);
            Cap.Mesh.GetVertices(_capVertices);
            Cap.Mesh.GetNormals(_capNormals);
            for (int i = 0; i < _capVertices.Count; i++)
            {
                Vertices[_capOffset + i] = _capVertices[i];
                Normals[_capOffset + i] = _capNormals[i];
            }
        }
        UploadVertices();
        Dirty = false;
    }

    private void UploadVertices()
    {
        Mesh.SetVertices(Vertices);
        Mesh.SetNormals(Normals);
        Mesh.SetTangents(Tangents);
        Mesh.RecalculateBounds();
    }
}
