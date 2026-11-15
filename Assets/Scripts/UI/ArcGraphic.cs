using UnityEngine;
using UnityEngine.UI;

// 只生成一段圆环的网格。能量条需要 60° 的大半径圆弧，用 Slider 的 Filled 会沿
// 矩形裁切而不是沿圆弧裁切，所以直接画环带。
[RequireComponent(typeof(CanvasRenderer))]
public class ArcGraphic : MaskableGraphic
{
    [SerializeField] private float _startAngle = 150f;
    [SerializeField] private float _sweepAngle = 60f;
    [SerializeField] private float _radius = 290f;
    [SerializeField] private float _thickness = 12f;
    [SerializeField, Range(0f, 1f)] private float _fill = 1f;
    [SerializeField] private bool _fillFromEnd = true;
    [SerializeField, Min(2)] private int _segmentCount = 48;

    public void Configure(float startAngle, float sweepAngle, float radius, float thickness,
        float fill, bool fillFromEnd)
    {
        _startAngle = startAngle;
        _sweepAngle = sweepAngle;
        _radius = radius;
        _thickness = thickness;
        _fill = Mathf.Clamp01(fill);
        _fillFromEnd = fillFromEnd;
        SetVerticesDirty();
    }

    public void SetFill(float value)
    {
        float next = Mathf.Clamp01(value);
        if (Mathf.Approximately(_fill, next)) return;
        _fill = next;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (_fill <= 0.0001f || _thickness <= 0.0001f || _sweepAngle <= 0f) return;

        float span = _sweepAngle * _fill;
        float start = _fillFromEnd ? _startAngle + (_sweepAngle - span) : _startAngle;
        Vector2 center = GetPixelAdjustedRect().center;
        float half = _thickness * 0.5f;
        int segments = Mathf.Max(2, Mathf.CeilToInt(_segmentCount * _fill));

        for (int i = 0; i < segments; i++)
        {
            float a0 = (start + span * i / segments) * Mathf.Deg2Rad;
            float a1 = (start + span * (i + 1) / segments) * Mathf.Deg2Rad;
            Vector2 d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0));
            Vector2 d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));

            AddQuad(vh,
                center + d0 * (_radius + half),
                center + d1 * (_radius + half),
                center + d1 * (_radius - half),
                center + d0 * (_radius - half));
        }

        // 圆头端帽，避免高亮弧和轨道在端点上出现直角缺口。
        AddDisc(vh, center + new Vector2(Mathf.Cos(start * Mathf.Deg2Rad), Mathf.Sin(start * Mathf.Deg2Rad)) * _radius, half);
        AddDisc(vh, center + new Vector2(Mathf.Cos((start + span) * Mathf.Deg2Rad), Mathf.Sin((start + span) * Mathf.Deg2Rad)) * _radius, half);
    }

    private void AddDisc(VertexHelper vh, Vector2 center, float radius)
    {
        int startIndex = vh.currentVertCount;
        vh.AddVert(center, color, Vector2.zero);
        const int steps = 12;
        for (int i = 0; i <= steps; i++)
        {
            float angle = i * Mathf.PI * 2f / steps;
            vh.AddVert(center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius, color, Vector2.zero);
        }

        for (int i = 0; i < steps; i++)
        {
            vh.AddTriangle(startIndex, startIndex + 1 + i, startIndex + 2 + i);
        }
    }

    private void AddQuad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        int start = vh.currentVertCount;
        vh.AddVert(a, color, Vector2.zero);
        vh.AddVert(b, color, Vector2.zero);
        vh.AddVert(c, color, Vector2.zero);
        vh.AddVert(d, color, Vector2.zero);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }
}
