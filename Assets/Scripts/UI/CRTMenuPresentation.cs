using UnityEngine;
using UnityEngine.UI;

public sealed class CRTMenuPresentation : MonoBehaviour
{
    [SerializeField] private RectTransform _title;
    [SerializeField] private CanvasGroup _titleOpacity;
    [SerializeField] private RawImage _background;
    [SerializeField] private RawImage[] _titleImages;
    [SerializeField] private float _flickerDuration = .55f;
    [SerializeField] private float _travelDuration = 2.4f;
    [SerializeField] private float _introFrameRate = 8;
    [SerializeField, Range(0, 1)] private float _settledOpacity = .55f;
    [SerializeField, Range(0, 1)] private float _idleGlitch = .12f;
    [Header("标题位置与切入")]
    [SerializeField] private float _startAnchorX = .25f;
    [SerializeField] private float _endAnchorX = .5f;
    [SerializeField] private float _titleHeight = 325;
    [SerializeField] private float _flickerFrameRate = 24;
    [SerializeField, Range(0, 1)] private float _flickerOpacity = .12f;
    [SerializeField, Range(0, 1)] private float _entryGlitch = .9f;
    [SerializeField, Range(0, 1)] private float _travelGlitch = .32f;
    private Material _backgroundInstance;
    private Material _titleInstance;
    private float _startedAt;

    private void Start()
    {
        InitializeMaterials();
        _startedAt = Time.unscaledTime;
        Preview(0);
    }

    public void InitializeMaterials()
    {
        if (_backgroundInstance != null) return;
        _backgroundInstance = new Material(_background.material);
        _background.material = _backgroundInstance;
        _titleInstance = new Material(_titleImages[0].material);
        foreach (RawImage image in _titleImages) image.material = _titleInstance;
    }

    private void Update() => Preview(Time.unscaledTime - _startedAt);

    // 预览与运行时共用采样，抽帧只作用于入场，背景始终按非缩放时间流动。
    public void Preview(float clock)
    {
        InitializeMaterials();
        Material background = _backgroundInstance != null ? _backgroundInstance : _background.material;
        Material title = _titleInstance != null ? _titleInstance : _titleImages[0].material;
        background.SetFloat("_Clock", clock);
        float stepped = Mathf.Floor(Mathf.Max(0, clock - _flickerDuration) * _introFrameRate) / _introFrameRate;
        float progress = Mathf.Clamp01(stepped / _travelDuration);
        _title.anchorMin = _title.anchorMax = new Vector2(Mathf.Lerp(_startAnchorX, _endAnchorX, progress), .5f);
        _title.anchoredPosition = new Vector2(0, _titleHeight);
        float flicker = Mathf.Floor(clock * _flickerFrameRate) % 3 == 0 ? _flickerOpacity : 1;
        // 渐隐保留可读的最终亮度，不能把菜单标题完全隐藏。
        _titleOpacity.alpha = clock < _flickerDuration ? flicker : Mathf.Lerp(1, _settledOpacity, progress);
        title.SetFloat("_Glitch", clock < _flickerDuration ? _entryGlitch : Mathf.Lerp(_travelGlitch, _idleGlitch, progress));
        title.SetFloat("_Clock", clock);
    }

    private void Release(Material material)
    {
        if (Application.isPlaying) Destroy(material);
        else DestroyImmediate(material);
    }

    private void OnDestroy()
    {
        if (_backgroundInstance != null) Release(_backgroundInstance);
        if (_titleInstance != null) Release(_titleInstance);
    }
}
