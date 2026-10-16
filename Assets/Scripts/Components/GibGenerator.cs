using System.Collections.Generic;
using UnityEngine;

// 只补平面断口，不改主体拓扑；主体的半身可见性仍完全由 Shader Cutout 控制。
internal sealed class GibGenerator
{
    private const float WeldToleranceSquared = 0.00000001f;
    private readonly List<Edge> _edges = new List<Edge>();
    private readonly List<Vector2Int> _loops = new List<Vector2Int>();
    private readonly List<Vector3> _vertices = new List<Vector3>();
    public Mesh Mesh { get; }

    private struct Edge
    {
        public int A, B;
        public float T;
        public Vector3 Evaluate(IList<Vector3> vertices) => Vector3.LerpUnclamped(vertices[A], vertices[B], T);
    }

    public GibGenerator(Mesh source)
    {
        Vector3[] vertices = source.vertices;
        Vector2[] distances = source.uv2;
        int[] triangles = source.triangles;
        var segments = new List<Edge>();
        var intersections = new List<Edge>(3);
        for (int i = 0; i < triangles.Length; i += 3)
        {
            intersections.Clear();
            for (int edge = 0; edge < 3; edge++)
            {
                float a = distances[triangles[i + edge]].x;
                float b = distances[triangles[i + (edge + 1) % 3]].x;
                if ((a > 0f) == (b > 0f)) continue;
                intersections.Add(new Edge { A = triangles[i + edge], B = triangles[i + (edge + 1) % 3],
                    T = a / (a - b) });
            }
            if (intersections.Count == 2)
            {
                segments.Add(intersections[0]);
                segments.Add(intersections[1]);
            }
        }
        var capTriangles = new List<int>();
        // 焊接 UV 接缝后的各个闭环分别封口，避免手臂与躯干的截面被连成一整片。
        while (segments.Count > 0)
        {
            var loop = new List<Edge> { segments[0], segments[1] };
            segments.RemoveRange(0, 2);
            bool closed = false;
            while (true)
            {
                Vector3 end = loop[loop.Count - 1].Evaluate(vertices);
                if ((end - loop[0].Evaluate(vertices)).sqrMagnitude < WeldToleranceSquared) { closed = true; break; }
                int match = segments.FindIndex(p => (p.Evaluate(vertices) - end).sqrMagnitude < WeldToleranceSquared);
                if (match < 0) break;
                int pair = match ^ 1;
                loop.Add(segments[pair]);
                segments.RemoveRange(match & ~1, 2);
            }
            if (!closed || loop.Count < 4) continue;
            loop.RemoveAt(loop.Count - 1);
            int start = _edges.Count;
            _loops.Add(new Vector2Int(start, loop.Count));
            _edges.Add(default);
            _edges.AddRange(loop);
            for (int i = 0; i < loop.Count; i++)
            {
                capTriangles.Add(start);
                capTriangles.Add(start + 1 + i);
                capTriangles.Add(start + 1 + (i + 1) % loop.Count);
            }
        }
        if (capTriangles.Count == 0) return;
        Mesh = new Mesh { name = "WaistCut_Surface", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        Mesh.MarkDynamic();
        foreach (Edge edge in _edges) _vertices.Add(edge.Evaluate(vertices));
        Mesh.SetVertices(_vertices);
        Mesh.SetTriangles(capTriangles, 0);
        Update(vertices);
    }

    public void Update(IList<Vector3> vertices)
    {
        if (Mesh == null) return;
        // 沿死亡时相交的同一条边、同一比例插值，断口才能始终贴合 Shader 的裁切边界。
        for (int i = 0; i < _edges.Count; i++) _vertices[i] = _edges[i].Evaluate(vertices);
        foreach (Vector2Int loop in _loops)
        {
            Vector3 center = Vector3.zero;
            for (int i = 1; i <= loop.y; i++) center += _vertices[loop.x + i];
            _vertices[loop.x] = center / loop.y;
        }
        Mesh.SetVertices(_vertices);
        Mesh.RecalculateNormals();
        Mesh.RecalculateBounds();
    }
}
