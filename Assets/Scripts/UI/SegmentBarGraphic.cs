using UnityEngine;
using UnityEngine.UI;

// 电量格：整格点亮，按四舍五入匹配“18.6/30 显示 10 格”的视觉预期。
[RequireComponent(typeof(CanvasRenderer))]
public class SegmentBarGraphic : MaskableGraphic
{
    [SerializeField, Min(1)] private int _count = 16;
    [SerializeField, Min(0f)] private float _gap = 6f;
    [SerializeField, Min(0f)] private float _border = 1f;
    [SerializeField] private Color _litFill = new Color32(63, 196, 255, 255);
    [SerializeField] private Color _litBorder = new Color32(143, 239, 255, 115);
    [SerializeField] private Color _dimFill = new Color32(234, 249, 255, 13);
    [SerializeField] private Color _dimBorder = new Color32(103, 235, 255, 41);

    // 初始 0 格并立即建网格，预制体编辑状态下也能看到暗格底槽。
    private int _lit = 0;

    public void Configure(int count, float gap, float border, Color litFill, Color litBorder,
        Color dimFill, Color dimBorder)
    {
        _count = Mathf.Max(1, count);
        _gap = Mathf.Max(0f, gap);
        _border = Mathf.Max(0f, border);
        _litFill = litFill;
        _litBorder = litBorder;
        _dimFill = dimFill;
        _dimBorder = dimBorder;
        SetVerticesDirty();
    }

    public void SetRatio(float value)
    {
        int lit = Mathf.RoundToInt(Mathf.Clamp01(value) * _count);
        if (lit == _lit) return;
        _lit = lit;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (_lit < 0) return;

        Rect rect = GetPixelAdjustedRect();
        float cellWidth = (rect.width - _gap * (_count - 1)) / _count;
        if (cellWidth <= 0f) return;

        for (int i = 0; i < _count; i++)
        {
            float xMin = rect.xMin + i * (cellWidth + _gap);
            bool lit = i < _lit;
            Color border = lit ? _litBorder : _dimBorder;
            Color fill = lit ? _litFill : _dimFill;

            AddRect(vh, new Rect(xMin, rect.yMin, cellWidth, rect.height), border);
            AddRect(vh, new Rect(xMin + _border, rect.yMin + _border,
                Mathf.Max(0f, cellWidth - _border * 2f), Mathf.Max(0f, rect.height - _border * 2f)), fill);
        }
    }

    private void AddRect(VertexHelper vh, Rect rect, Color tint)
    {
        int start = vh.currentVertCount;
        vh.AddVert(new Vector2(rect.xMin, rect.yMin), tint, Vector2.zero);
        vh.AddVert(new Vector2(rect.xMin, rect.yMax), tint, Vector2.zero);
        vh.AddVert(new Vector2(rect.xMax, rect.yMax), tint, Vector2.zero);
        vh.AddVert(new Vector2(rect.xMax, rect.yMin), tint, Vector2.zero);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }
}
