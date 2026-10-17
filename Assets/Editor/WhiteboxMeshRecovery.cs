using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// 团队压缩包保留了 ProBuilder 的原始几何，但未携带可运行 Mesh；恢复静态网格，避免引入新插件。
public static class WhiteboxMeshRecovery
{
    public static void RepairPrefab(string path, string meshFolder)
    {
        RepairPrefab(path, meshFolder, new HashSet<string>());
    }

    private static void RepairPrefab(string path, string meshFolder, HashSet<string> visited)
    {
        if (!visited.Add(path)) return;
        foreach (string dependency in AssetDatabase.GetDependencies(path, false))
            if (dependency != path && dependency.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) &&
                dependency.StartsWith(TeamWhiteboxSetup.ImportFolder + "/", StringComparison.Ordinal))
                RepairPrefab(dependency, meshFolder, visited);
        string yaml = File.ReadAllText(path);
        var records = new Dictionary<long, string>();
        foreach (string block in Regex.Split(yaml, @"(?m)^--- !u!"))
        {
            if (!block.StartsWith("114 &", StringComparison.Ordinal) ||
                !block.Contains("8233d90336aea43098adf6dbabd606a2") || !block.Contains("  m_Positions:")) continue;
            long id = long.Parse(Regex.Match(block, @"m_GameObject: \{fileID: (\d+)\}").Groups[1].Value);
            records[id] = block;
        }
        if (records.Count == 0) return;

        Directory.CreateDirectory(meshFolder);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var sourceFilters = asset.GetComponentsInChildren<MeshFilter>(true);
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        int repaired = 0;
        try
        {
            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            if (filters.Length != sourceFilters.Length) throw new InvalidDataException("预制体层级不一致：" + path);
            for (int i = 0; i < filters.Length; i++)
            {
                if (filters[i].sharedMesh != null) continue;
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sourceFilters[i].gameObject, out string guid, out long id);
                if (!records.TryGetValue(id, out string block))
                    throw new InvalidDataException("缺失网格没有原始顶点：" + path + "/" + filters[i].name);
                Mesh mesh = Recover(block);
                mesh.name = Path.GetFileNameWithoutExtension(path) + "_" + filters[i].name;
                string meshPath = meshFolder + "/" + guid + "_" + id + ".asset";
                Mesh existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                if (existing == null) AssetDatabase.CreateAsset(mesh, meshPath);
                else
                {
                    EditorUtility.CopySerialized(mesh, existing);
                    UnityEngine.Object.DestroyImmediate(mesh);
                    mesh = existing;
                }
                filters[i].sharedMesh = mesh;
                foreach (MeshCollider collider in filters[i].GetComponents<MeshCollider>())
                    if (collider.sharedMesh == null) collider.sharedMesh = mesh;
                repaired++;
            }
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(child.gameObject);
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        Debug.Log($"TEAM_MESH_RECOVERED {Path.GetFileNameWithoutExtension(path)} count={repaired}");
    }

    private static Mesh Recover(string block)
    {
        string positions = Field(block, "m_Positions");
        Vector3[] vertices = Regex.Matches(positions, @"\{x: ([^,]+), y: ([^,]+), z: ([^}]+)\}")
            .Cast<Match>().Select(match => new Vector3(Number(match.Groups[1].Value),
                Number(match.Groups[2].Value), Number(match.Groups[3].Value))).ToArray();
        var triangles = new Dictionary<int, List<int>>();
        foreach (Match face in Regex.Matches(Field(block, "m_Faces"),
            @"m_Indexes: ([0-9a-fA-F]+)[\s\S]*?m_SubmeshIndex: (\d+)"))
        {
            int submesh = int.Parse(face.Groups[2].Value);
            if (!triangles.TryGetValue(submesh, out List<int> indices))
            {
                indices = new List<int>();
                triangles.Add(submesh, indices);
            }
            string hex = face.Groups[1].Value;
            if (hex.Length % 8 != 0) throw new InvalidDataException("三角形索引格式错误");
            for (int offset = 0; offset < hex.Length; offset += 8)
            {
                uint index = 0;
                for (int byteIndex = 0; byteIndex < 4; byteIndex++)
                    index |= Convert.ToUInt32(hex.Substring(offset + byteIndex * 2, 2), 16) << (byteIndex * 8);
                if (index >= vertices.Length) throw new InvalidDataException("三角形索引越界");
                indices.Add((int)index);
            }
        }
        if (vertices.Length < 3 || triangles.Count == 0) throw new InvalidDataException("原始几何不完整");
        var mesh = new Mesh { indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        mesh.vertices = vertices;
        mesh.subMeshCount = triangles.Keys.Max() + 1;
        foreach (var pair in triangles) mesh.SetTriangles(pair.Value, pair.Key);
        Vector2[] uv = Regex.Matches(Field(block, "m_Textures0"), @"\{x: ([^,]+), y: ([^}]+)\}")
            .Cast<Match>().Select(match => new Vector2(Number(match.Groups[1].Value), Number(match.Groups[2].Value))).ToArray();
        if (uv.Length == vertices.Length) mesh.uv = uv;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static string Field(string block, string name)
    {
        return Regex.Match(block, @"(?ms)^  " + name + @":\r?\n(.*?)(?=^  [a-zA-Z_]|\z)").Groups[1].Value;
    }

    private static float Number(string value) => float.Parse(value, CultureInfo.InvariantCulture);
}
