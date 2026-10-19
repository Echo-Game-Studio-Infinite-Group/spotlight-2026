using TMPro;
using UnityEngine;
using UnityEngine.UI;

// 只把现有资源数值映射到图层，不修改血量、能量或时间状态。
public sealed class ResourceBarVisual : MonoBehaviour
{
    [SerializeField] private RectTransform[] _fills;
    [SerializeField] private Vector2[] _widthRanges;
    [SerializeField] private GameObject[] _segments;
    [SerializeField] private Image[] _glows;
    [SerializeField] private TextMeshProUGUI _currentText;
    [SerializeField] private TextMeshProUGUI _maximumText;
    [SerializeField] private bool _segmented;
    [SerializeField, Range(0f, 1f)] private float _glowThreshold;

    private float _lastCurrent = float.NaN;
    private float _lastMaximum = float.NaN;

    public void SetValue(float current, float maximum)
    {
        current = Mathf.Clamp(current, 0f, Mathf.Max(0f, maximum));
        if (current == _lastCurrent && maximum == _lastMaximum) return;
        _lastCurrent = current;
        _lastMaximum = maximum;

        float ratio = maximum > 0f ? Mathf.Clamp01(current / maximum) : 0f;
        _currentText.SetText("{0:0}", Mathf.Floor(current));
        _maximumText.SetText("/ {0:0}", Mathf.Floor(Mathf.Max(0f, maximum)));

        if (_segmented)
        {
            int count = Mathf.FloorToInt(ratio * _segments.Length);
            for (int i = 0; i < _segments.Length; i++)
                _segments[i].SetActive(i < count);
        }
        else
        {
            for (int i = 0; i < _fills.Length; i++)
            {
                Vector2 size = _fills[i].sizeDelta;
                size.x = Mathf.Lerp(_widthRanges[i].x, _widthRanges[i].y, ratio);
                _fills[i].sizeDelta = size;
            }
        }

        // 消耗到低值也更新光晕，避免保留高能量时的亮度。
        float alpha = Mathf.InverseLerp(_glowThreshold, 1f, ratio);
        if (!_segmented) alpha = Mathf.SmoothStep(0f, 1f, alpha);
        foreach (Image glow in _glows)
        {
            Color color = glow.color;
            color.a = alpha;
            glow.color = color;
        }
    }
}
