using UnityEngine;
using UnityEngine.UI;

// 心率线：振幅跟随血量比例，低血量时波形变平，和心形上的 CRT 扫描线一起表现状态。
[RequireComponent(typeof(CanvasRenderer))]
public class EcgGraphic : MaskableGraphic
{
    [SerializeField, Min(0.1f)] private float _lineWidth = 2.2f;
    [SerializeField, Min(0.1f)] private float _cycles = 1.55f;
    [SerializeField, Min(0f)] private float _cycleSpeed = 0.55f;
    [SerializeField, Range(0f, 1f)] private float _healthRatio = 1f;

    private float _phase;

    public void Configure(float lineWidth, float cycles, float cycleSpeed)
    {
        _lineWidth = Mathf.Max(0.1f, lineWidth);
        _cycles = Mathf.Max(0.1f, cycles);
        _cycleSpeed = Mathf.Max(0f, cycleSpeed);
        SetVerticesDirty();
    }

    public void SetHealthRatio(float value)
    {
        float next = Mathf.Clamp01(value);
        if (Mathf.Abs(next - _healthRatio) < 0.001f) return;
        _healthRatio = next;
        SetVerticesDirty();
    }

    private void Update()
    {
        if (!Application.isPlaying) return;
        _phase = Mathf.Repeat(_phase + Time.unscaledDeltaTime * _cycleSpeed, 1f);
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect rect = GetPixelAdjustedRect();
        if (rect.width <= 0f || rect.height <= 0f) return;

        float amplitude = Mathf.Lerp(rect.height * 0.045f, rect.height * 0.34f, _healthRatio);
        int steps = Mathf.Clamp(Mathf.CeilToInt(rect.width / 2f), 16, 128);
        Vector2 previous = new Vector2(rect.xMin, rect.center.y + Wave(_phase) * amplitude);

        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps;
            float x = Mathf.Lerp(rect.xMin, rect.xMax, t);
            float y = rect.center.y + Wave(t * _cycles + _phase) * amplitude;
            Vector2 current = new Vector2(x, y);
            AddSegment(vh, previous, current);
            previous = current;
        }
    }

    // 一个小凹口、一段陡峭主峰，再回到基线；数值落在 -1..1，方便只调振幅。
    private static float Wave(float u)
    {
        u = Mathf.Repeat(u, 1f);
        if (u < 0.52f) return 0f;
        if (u < 0.60f) return Mathf.Sin((u - 0.52f) / 0.08f * Mathf.PI) * -0.22f;
        if (u < 0.65f) return Mathf.Lerp(-0.1f, 1f, (u - 0.60f) / 0.05f);
        if (u < 0.70f) return Mathf.Lerp(1f, -0.55f, (u - 0.65f) / 0.05f);
        if (u < 0.78f) return Mathf.Lerp(-0.55f, 0f, (u - 0.70f) / 0.08f);
        return 0f;
    }

    private void AddSegment(VertexHelper vh, Vector2 from, Vector2 to)
    {
        Vector2 direction = to - from;
        if (direction.sqrMagnitude <= 0.000001f) return;
        Vector2 normal = new Vector2(-direction.y, direction.x).normalized * (_lineWidth * 0.5f);

        int start = vh.currentVertCount;
        vh.AddVert(from - normal, color, Vector2.zero);
        vh.AddVert(from + normal, color, Vector2.zero);
        vh.AddVert(to + normal, color, Vector2.zero);
        vh.AddVert(to - normal, color, Vector2.zero);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }
}
