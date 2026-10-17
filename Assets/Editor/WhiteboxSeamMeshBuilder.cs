using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// 裁剪使用独立显示副本，碰撞体和团队原始网格资产保持不变。
public static class WhiteboxSeamMeshBuilder
{
    private const float PlaneTolerance = .0005f;
    private const float MinimumArea = .000001f;

    private struct Vertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 Uv;
        public Vector2 Uv2;
        public Vector4 Tangent;

        public static Vertex Lerp(Vertex a, Vertex b, float t) => new Vertex
        {
            Position = Vector3.LerpUnclamped(a.Position, b.Position, t),
            Normal = Vector3.LerpUnclamped(a.Normal, b.Normal, t).normalized,
            Uv = Vector2.LerpUnclamped(a.Uv, b.Uv, t),
            Uv2 = Vector2.LerpUnclamped(a.Uv2, b.Uv2, t),
            Tangent = Vector4.LerpUnclamped(a.Tangent, b.Tangent, t)
        };
    }

    private struct Triangle
    {
        public Vector3 A;
        public Vector3 B;
        public Vector3 C;
        public Vector3 Normal;
        public Bounds Bounds;
    }

    public static void TrimThreshold(GameObject threshold, GameObject authoredRoom, string folder)
    {
        TrimBox(threshold, new[] { authoredRoom }, folder);
    }

    public static Mesh TrimBox(GameObject threshold, IEnumerable<GameObject> authoredRooms, string folder, string suffix = "")
    {
        // 每次从同一立方体重建，避免重复修复时继续切割上一次结果。
        GameObject template = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Mesh source = template.GetComponent<MeshFilter>().sharedMesh;
        Object.DestroyImmediate(template);
        return TrimMesh(threshold, source, authoredRooms, folder, suffix);
    }

    public static Mesh TrimMesh(GameObject threshold, Mesh source, IEnumerable<GameObject> authoredRooms,
        string folder, string suffix, bool skipUnchanged = false)
    {
        threshold.GetComponent<MeshFilter>().sharedMesh = source;
        var blockers = new List<Triangle>();
        Bounds thresholdBounds = threshold.GetComponent<Renderer>().bounds;
        thresholdBounds.Expand(PlaneTolerance * 2f);
        foreach (MeshFilter filter in authoredRooms.SelectMany(root => root.GetComponentsInChildren<MeshFilter>(true)))
        {
            if (filter.name == "入口门槛" || filter.name == "出口门槛") continue;
            Renderer renderer = filter.GetComponent<Renderer>();
            if (filter.sharedMesh == null || renderer == null || !renderer.enabled ||
                !filter.gameObject.activeInHierarchy || !renderer.bounds.Intersects(thresholdBounds)) continue;
            MapSeamSurface selector = filter.GetComponent<MapSeamSurface>();
            Mesh blockerMesh = selector != null ? selector.OwnerOnlyMesh : filter.sharedMesh;
            Vector3[] vertices = blockerMesh.vertices;
            int[] indices = blockerMesh.triangles;
            for (int i = 0; i < indices.Length; i += 3)
            {
                Vector3 a = filter.transform.TransformPoint(vertices[indices[i]]);
                Vector3 b = filter.transform.TransformPoint(vertices[indices[i + 1]]);
                Vector3 c = filter.transform.TransformPoint(vertices[indices[i + 2]]);
                var bounds = new Bounds(a, Vector3.zero);
                bounds.Encapsulate(b);
                bounds.Encapsulate(c);
                bounds.Expand(PlaneTolerance * 2f);
                Vector3 cross = Vector3.Cross(b - a, c - a);
                if (!bounds.Intersects(thresholdBounds) || cross.sqrMagnitude < MinimumArea * MinimumArea) continue;
                blockers.Add(new Triangle { A = a, B = b, C = c, Normal = cross.normalized, Bounds = bounds });
            }
        }

        Vector3[] positions = source.vertices;
        Vector3[] normals = source.normals;
        Vector2[] uv = source.uv;
        Vector2[] uv2 = source.uv2;
        Vector4[] tangents = source.tangents;
        var submeshes = new List<int>[source.subMeshCount];
        var result = new List<Vertex>();
        int cuts = 0;
        for (int submesh = 0; submesh < source.subMeshCount; submesh++)
        {
        submeshes[submesh] = new List<int>();
        int[] triangles = source.GetTriangles(submesh);
        for (int i = 0; i < triangles.Length; i += 3)
        {
            var polygon = new List<Vertex>();
            for (int corner = 0; corner < 3; corner++)
            {
                int index = triangles[i + corner];
                polygon.Add(new Vertex
                {
                    Position = threshold.transform.TransformPoint(positions[index]),
                    Normal = normals.Length == positions.Length ? normals[index] : Vector3.up,
                    Uv = uv.Length == positions.Length ? uv[index] : Vector2.zero,
                    Uv2 = uv2.Length == positions.Length ? uv2[index] : Vector2.zero,
                    Tangent = tangents.Length == positions.Length ? tangents[index] : Vector4.zero
                });
            }
            Vector3 normal = Vector3.Cross(polygon[1].Position - polygon[0].Position,
                polygon[2].Position - polygon[0].Position).normalized;
            var bounds = new Bounds(polygon[0].Position, Vector3.zero);
            foreach (Vertex vertex in polygon) bounds.Encapsulate(vertex.Position);
            bounds.Expand(PlaneTolerance * 2f);
            var fragments = new List<List<Vertex>> { polygon };
            foreach (Triangle blocker in blockers)
            {
                // 反向面通常会被背面剔除，不能拿它遮掉正向的补洞面。
                if (!bounds.Intersects(blocker.Bounds) || Vector3.Dot(normal, blocker.Normal) < .9999f ||
                    Mathf.Abs(Vector3.Dot(normal, blocker.A - polygon[0].Position)) > PlaneTolerance ||
                    Mathf.Abs(Vector3.Dot(normal, blocker.B - polygon[0].Position)) > PlaneTolerance ||
                    Mathf.Abs(Vector3.Dot(normal, blocker.C - polygon[0].Position)) > PlaneTolerance) continue;
                var remaining = new List<List<Vertex>>();
                foreach (List<Vertex> fragment in fragments)
                    Subtract(fragment, blocker, remaining, ref cuts);
                fragments = remaining;
                if (fragments.Count == 0) break;
            }
            foreach (List<Vertex> fragment in fragments)
                for (int corner = 1; corner + 1 < fragment.Count; corner++)
                {
                    if (Vector3.Cross(fragment[corner].Position - fragment[0].Position,
                        fragment[corner + 1].Position - fragment[0].Position).sqrMagnitude < MinimumArea * MinimumArea) continue;
                    submeshes[submesh].Add(result.Count);
                    submeshes[submesh].Add(result.Count + 1);
                    submeshes[submesh].Add(result.Count + 2);
                    result.Add(fragment[0]);
                    result.Add(fragment[corner]);
                    result.Add(fragment[corner + 1]);
                }
        }
        }

        if (skipUnchanged && cuts == 0) return source;

        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder(System.IO.Path.GetDirectoryName(folder).Replace('\\', '/'), System.IO.Path.GetFileName(folder));
        var mesh = new Mesh { name = threshold.scene.name + "_" + threshold.name + suffix };
        mesh.indexFormat = result.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(result.Select(vertex => threshold.transform.InverseTransformPoint(vertex.Position)).ToList());
        mesh.SetNormals(result.Select(vertex => vertex.Normal).ToList());
        mesh.SetUVs(0, result.Select(vertex => vertex.Uv).ToList());
        if (uv2.Length == positions.Length) mesh.SetUVs(1, result.Select(vertex => vertex.Uv2).ToList());
        if (tangents.Length == positions.Length) mesh.SetTangents(result.Select(vertex => vertex.Tangent).ToList());
        mesh.subMeshCount = submeshes.Length;
        for (int submesh = 0; submesh < submeshes.Length; submesh++) mesh.SetTriangles(submeshes[submesh], submesh);
        mesh.RecalculateBounds();
        string path = folder + "/" + mesh.name + ".asset";
        Mesh saved = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (saved == null) AssetDatabase.CreateAsset(mesh, path);
        else
        {
            EditorUtility.CopySerialized(mesh, saved);
            Object.DestroyImmediate(mesh);
            mesh = saved;
        }
        threshold.GetComponent<MeshFilter>().sharedMesh = mesh;
        Debug.Log($"WHITEBOX_SEAM_TRIM {threshold.scene.name}/{threshold.name} cuts={cuts} triangles={result.Count / 3}");
        return mesh;
    }

    private static void Subtract(List<Vertex> polygon, Triangle triangle, List<List<Vertex>> remaining, ref int cuts)
    {
        Vector3[] points = { triangle.A, triangle.B, triangle.C };
        List<Vertex> intersection = polygon;
        for (int edge = 0; edge < 3 && intersection.Count >= 3; edge++)
            intersection = Clip(intersection, points[edge], Vector3.Cross(triangle.Normal,
                points[(edge + 1) % 3] - points[edge]).normalized, true);
        if (Area(intersection) <= MinimumArea)
        {
            remaining.Add(polygon);
            return;
        }
        cuts++;
        List<Vertex> inside = polygon;
        for (int edge = 0; edge < 3 && inside.Count >= 3; edge++)
        {
            Vector3 inward = Vector3.Cross(triangle.Normal, points[(edge + 1) % 3] - points[edge]).normalized;
            List<Vertex> outside = Clip(inside, points[edge], inward, false);
            if (Area(outside) > MinimumArea) remaining.Add(outside);
            inside = Clip(inside, points[edge], inward, true);
        }
    }

    private static List<Vertex> Clip(List<Vertex> polygon, Vector3 origin, Vector3 inward, bool keepInside)
    {
        var result = new List<Vertex>();
        if (polygon.Count == 0) return result;
        Vertex previous = polygon[polygon.Count - 1];
        float previousDistance = Vector3.Dot(previous.Position - origin, inward) * (keepInside ? 1f : -1f);
        foreach (Vertex current in polygon)
        {
            float currentDistance = Vector3.Dot(current.Position - origin, inward) * (keepInside ? 1f : -1f);
            if ((previousDistance >= 0f) != (currentDistance >= 0f))
                result.Add(Vertex.Lerp(previous, current, previousDistance / (previousDistance - currentDistance)));
            if (currentDistance >= 0f) result.Add(current);
            previous = current;
            previousDistance = currentDistance;
        }
        return result;
    }

    private static float Area(List<Vertex> polygon)
    {
        Vector3 sum = Vector3.zero;
        for (int i = 1; i + 1 < polygon.Count; i++)
            sum += Vector3.Cross(polygon[i].Position - polygon[0].Position, polygon[i + 1].Position - polygon[0].Position);
        return sum.magnitude * .5f;
    }
}
