using System.Collections.Generic;
using UnityEngine;

namespace GameJam.Flow
{
    // 显示网格只减去共面覆盖区；碰撞地板保持连续，不能把视觉去重变成碰撞缺口。
    public static class FlowFloorMesh
    {
        private const float PlaneTolerance = 0.0005f;
        private const float MinimumArea = 0.000001f;
        public static Mesh Create(Transform floor, params FlowRoomTemplate[] rooms)
        {
            var polygon = new List<Vector3> {
                floor.TransformPoint(new Vector3(-.5f, .5f, -.5f)), floor.TransformPoint(new Vector3(-.5f, .5f, .5f)),
                floor.TransformPoint(new Vector3(.5f, .5f, .5f)), floor.TransformPoint(new Vector3(.5f, .5f, -.5f)) };
            var bounds = new Bounds(polygon[0], Vector3.zero);
            foreach (Vector3 point in polygon) bounds.Encapsulate(point);
            bounds.Expand(PlaneTolerance * 2f);
            var fragments = new List<List<Vector3>> { polygon };
            foreach (FlowRoomTemplate room in rooms)
                foreach (MeshFilter filter in room.GetComponentsInChildren<MeshFilter>(false))
                {
                    Mesh mesh = filter.sharedMesh;
                    Renderer renderer = filter.GetComponent<Renderer>();
                    if (mesh == null || !mesh.isReadable || renderer == null || !renderer.enabled || !renderer.bounds.Intersects(bounds)) continue;
                    Vector3[] vertices = mesh.vertices; int[] triangles = mesh.triangles;
                    for (int i = 0; i < triangles.Length && fragments.Count > 0; i += 3)
                    {
                        Vector3 a = filter.transform.TransformPoint(vertices[triangles[i]]), b = filter.transform.TransformPoint(vertices[triangles[i + 1]]),
                            c = filter.transform.TransformPoint(vertices[triangles[i + 2]]);
                        if (Mathf.Abs(a.y - polygon[0].y) > PlaneTolerance || Mathf.Abs(b.y - polygon[0].y) > PlaneTolerance ||
                            Mathf.Abs(c.y - polygon[0].y) > PlaneTolerance || Vector3.Cross(b - a, c - a).normalized.y < .9999f) continue;
                        Bounds triangleBounds = new Bounds(a, Vector3.zero); triangleBounds.Encapsulate(b); triangleBounds.Encapsulate(c);
                        triangleBounds.Expand(PlaneTolerance * 2f);
                        if (!triangleBounds.Intersects(bounds)) continue;
                        var remaining = new List<List<Vector3>>();
                        foreach (List<Vector3> fragment in fragments) Subtract(fragment, new[] { a, b, c }, remaining);
                        fragments = remaining;
                    }
                }
            var output = new List<Vector3>(); var indices = new List<int>(); var uvs = new List<Vector2>();
            foreach (List<Vector3> fragment in fragments)
                for (int i = 1; i + 1 < fragment.Count; i++)
                {
                    Vector3[] triangle = { fragment[0], fragment[i], fragment[i + 1] };
                    if (Vector3.Cross(triangle[1] - triangle[0], triangle[2] - triangle[0]).sqrMagnitude < MinimumArea * MinimumArea) continue;
                    foreach (Vector3 vertex in triangle)
                    {
                        indices.Add(output.Count); Vector3 local = floor.InverseTransformPoint(vertex); output.Add(local); uvs.Add(new Vector2(local.x, local.z));
                    }
                }
            var result = new Mesh { name = "动态连接段去重地板", indexFormat = output.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            result.SetVertices(output); result.SetUVs(0, uvs); result.SetTriangles(indices, 0); result.RecalculateNormals(); result.RecalculateBounds(); return result;
        }
        private static void Subtract(List<Vector3> polygon, Vector3[] triangle, List<List<Vector3>> output)
        {
            List<Vector3> intersection = polygon;
            for (int edge = 0; edge < 3 && intersection.Count >= 3; edge++)
                intersection = Clip(intersection, triangle[edge], Vector3.Cross(Vector3.up, triangle[(edge + 1) % 3] - triangle[edge]).normalized, true);
            if (Area(intersection) <= MinimumArea) { output.Add(polygon); return; }
            List<Vector3> inside = polygon;
            for (int edge = 0; edge < 3 && inside.Count >= 3; edge++)
            {
                Vector3 inward = Vector3.Cross(Vector3.up, triangle[(edge + 1) % 3] - triangle[edge]).normalized;
                List<Vector3> outside = Clip(inside, triangle[edge], inward, false);
                if (Area(outside) > MinimumArea) output.Add(outside);
                inside = Clip(inside, triangle[edge], inward, true);
            }
        }
        private static List<Vector3> Clip(List<Vector3> polygon, Vector3 origin, Vector3 inward, bool keepInside)
        {
            var result = new List<Vector3>();
            if (polygon.Count == 0) return result;
            Vector3 previous = polygon[polygon.Count - 1];
            float previousDistance = Vector3.Dot(previous - origin, inward) * (keepInside ? 1 : -1);
            foreach (Vector3 current in polygon)
            {
                float currentDistance = Vector3.Dot(current - origin, inward) * (keepInside ? 1 : -1);
                if ((previousDistance >= 0) != (currentDistance >= 0)) result.Add(Vector3.LerpUnclamped(previous, current, previousDistance / (previousDistance - currentDistance)));
                if (currentDistance >= 0) result.Add(current);
                previous = current; previousDistance = currentDistance;
            }
            return result;
        }
        private static float Area(List<Vector3> polygon)
        {
            Vector3 sum = Vector3.zero;
            for (int i = 1; i + 1 < polygon.Count; i++) sum += Vector3.Cross(polygon[i] - polygon[0], polygon[i + 1] - polygon[0]);
            return sum.magnitude * .5f;
        }
    }
}
