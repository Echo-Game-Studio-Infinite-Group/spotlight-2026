using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

internal sealed class ProtagonistAssetPostprocessor : AssetPostprocessor
{
    private const string Root = "Assets/Art/Characters/Protagonist/";
    private bool IsCharacterAsset => assetPath.StartsWith(Root, StringComparison.Ordinal);

    private void OnPreprocessModel()
    {
        if (!IsCharacterAsset) return;
        var importer = (ModelImporter)assetImporter;
        importer.importAnimation = false;
        importer.importCameras = false;
        importer.importLights = false;
        importer.importNormals = ModelImporterNormals.Import;
        importer.importTangents = ModelImporterTangents.CalculateMikk;
        importer.materialImportMode = ModelImporterMaterialImportMode.None;
        importer.meshCompression = ModelImporterMeshCompression.Off;
        importer.isReadable = false;
        importer.generateSecondaryUV = false;
    }

    private void OnPreprocessTexture()
    {
        if (!IsCharacterAsset) return;
        var importer = (TextureImporter)assetImporter;
        bool normal = assetPath.EndsWith("_normal.png", StringComparison.OrdinalIgnoreCase);
        bool color = assetPath.EndsWith("_basecolor.png", StringComparison.OrdinalIgnoreCase);
        bool sdf = assetPath.EndsWith("_sdf.png", StringComparison.OrdinalIgnoreCase);
        importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        importer.sRGBTexture = color;
        importer.alphaSource = TextureImporterAlphaSource.None;
        importer.mipmapEnabled = !sdf;
        importer.wrapMode = sdf ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
        bool portraitTexture = assetPath == Root + "Textures/Face/character_face_basecolor.png"
            || assetPath == Root + "Textures/Face/character_face_normal.png"
            || assetPath == Root + "Textures/Hair/character_hair_basecolor.png"
            || assetPath == Root + "Textures/Hair/character_hair_normal.png";
        // 仅近景脸发跨 mip 平滑过渡，避免把数值遮罩与其它纹理的既有采样行为一并改掉。
        importer.filterMode = portraitTexture ? FilterMode.Trilinear : FilterMode.Bilinear;
        importer.maxTextureSize = 2048;
        importer.textureCompression = sdf ? TextureImporterCompression.Uncompressed : TextureImporterCompression.CompressedHQ;
    }

    private void OnPostprocessModel(GameObject root)
    {
        if (!IsCharacterAsset) return;
        var seen = new HashSet<Mesh>();
        foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            if (filter.sharedMesh && seen.Add(filter.sharedMesh)) StoreSmoothOutlineNormals(filter.sharedMesh);
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (renderer.sharedMesh && seen.Add(renderer.sharedMesh)) StoreSmoothOutlineNormals(renderer.sharedMesh);
    }

    private static void StoreSmoothOutlineNormals(Mesh mesh)
    {
        Vector3[] vertices = mesh.vertices;
        Vector3[] sourceNormals = mesh.normals;
        var sums = new Dictionary<Vector3Int, Vector3>();
        float quantization = 1f / Mathf.Max(mesh.bounds.size.magnitude * .000001f, .0000001f);
        var keys = new Vector3Int[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            keys[i] = Vector3Int.RoundToInt(vertices[i] * quantization);
        for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
        {
            if (mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
            int[] indices = mesh.GetTriangles(submesh);
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                Vector3 ab = vertices[b] - vertices[a], ac = vertices[c] - vertices[a];
                Vector3 face = Vector3.Cross(ab, ac);
                if (face.sqrMagnitude < 1e-20f) continue;
                face.Normalize();
                Add(sums, keys[a], face * Vector3.Angle(ab, ac));
                Add(sums, keys[b], face * Vector3.Angle(-ab, vertices[c] - vertices[b]));
                Add(sums, keys[c], face * Vector3.Angle(-ac, vertices[b] - vertices[c]));
            }
        }
        if (mesh.tangents.Length != vertices.Length) mesh.RecalculateTangents();
        Vector4[] tangents = mesh.tangents;
        var result = new List<Vector3>(vertices.Length);
        for (int i = 0; i < vertices.Length; i++)
        {
            sums.TryGetValue(keys[i], out Vector3 normal);
            if (normal.sqrMagnitude < 1e-12f)
                normal = sourceNormals.Length == vertices.Length ? sourceNormals[i] : Vector3.up;
            normal.Normalize();
            Vector3 n = sourceNormals.Length == vertices.Length ? sourceNormals[i].normalized : normal;
            Vector4 tangent = tangents.Length == vertices.Length ? tangents[i] : new Vector4(1, 0, 0, 1);
            Vector3 t = new Vector3(tangent.x, tangent.y, tangent.z);
            t = (t - n * Vector3.Dot(n, t)).normalized;
            if (t.sqrMagnitude < .5f)
                t = Vector3.Cross(n, Mathf.Abs(n.y) < .99f ? Vector3.up : Vector3.right).normalized;
            Vector3 b = Vector3.Cross(n, t) * (tangent.w < 0 ? -1f : 1f);
            // 切线空间存储才能随将来的蒙皮TBN一起变形，直接存对象空间会把描边方向留在绑定姿势。
            result.Add(new Vector3(Vector3.Dot(normal, t), Vector3.Dot(normal, b), Vector3.Dot(normal, n)));
        }
        // 仅占UV3，不能破坏原始切线/法线/主UV和后续烘焙UV；原始锐边用于着色，焊接法线只用于壳描边。
        mesh.SetUVs(2, result);
    }

    private static void Add(Dictionary<Vector3Int, Vector3> sums, Vector3Int key, Vector3 value)
    {
        sums.TryGetValue(key, out Vector3 previous);
        sums[key] = previous + value;
    }
}
