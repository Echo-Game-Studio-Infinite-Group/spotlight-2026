using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// 纯色圆角面板。设计里的血/速面板是 50% 透明黑，不需要贴图或九宫格。
[RequireComponent(typeof(CanvasRenderer))]
public class RoundedPanelGraphic : MaskableGraphic
{
    [SerializeField, Min(0f)] private float _radius = 8f;
    [SerializeField, Range(2, 16)] private int _cornerSteps = 5;

    public void Configure(float radius)
    {
        _radius = Mathf.Max(0f, radius);
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect rect = GetPixelAdjustedRect();
        float radius = Mathf.Min(_radius, Mathf.Min(rect.width, rect.height) * 0.5f);
        float r = radius;

        List<Vector2> points = new List<Vector2>(4 * (_cornerSteps + 1));
        AddCorner(points, new Vector2(rect.xMax - r, rect.yMin + r), -90f, 0f, r);
        AddCorner(points, new Vector2(rect.xMax - r, rect.yMax - r), 0f, 90f, r);
        AddCorner(points, new Vector2(rect.xMin + r, rect.yMax - r), 90f, 180f, r);
        AddCorner(points, new Vector2(rect.xMin + r, rect.yMin + r), 180f, 270f, r);

        int centerIndex = vh.currentVertCount;
        vh.AddVert(rect.center, color, Vector2.zero);
        for (int i = 0; i < points.Count; i++) vh.AddVert(points[i], color, Vector2.zero);
        for (int i = 0; i < points.Count; i++)
        {
            vh.AddTriangle(centerIndex, centerIndex + 1 + i, centerIndex + 1 + (i + 1) % points.Count);
        }
    }

    private void AddCorner(List<Vector2> points, Vector2 center, float from, float to, float radius)
    {
        int steps = Mathf.Max(1, _cornerSteps);
        for (int i = 0; i <= steps; i++)
        {
            float angle = Mathf.Lerp(from, to, i / (float)steps) * Mathf.Deg2Rad;
            points.Add(center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
        }
    }
}
