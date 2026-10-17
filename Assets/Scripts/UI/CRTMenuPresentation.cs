using UnityEngine;
using UnityEngine.UI;

public sealed class CRTMenuPresentation : MonoBehaviour
{
    [SerializeField] private RectTransform _title;
    [SerializeField] private CanvasGroup _titleOpacity;
    [SerializeField] private RawImage _background;
    [SerializeField] private RawImage[] _titleImages;
    [SerializeField] private float _flickerDuration = .55f;
    [SerializeField] private float _travelDuration = 1.8f;
    [SerializeField] private float _introFrameRate = 12;
    private Material _backgroundInstance;
    private Material _titleInstance;
    private float _startedAt;

    private void Start()
    {
        InitializeMaterials();
        _startedAt = Time.unscaledTime;
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
        _title.anchorMin = _title.anchorMax = new Vector2(Mathf.Lerp(.25f, .5f, progress), .5f);
        _title.anchoredPosition = new Vector2(0, 325);
        float flicker = Mathf.Floor(clock * 24) % 3 == 0 ? .12f : 1;
        _titleOpacity.alpha = clock < _flickerDuration ? flicker : Mathf.Lerp(.35f, 1, progress);
        title.SetFloat("_Glitch", clock < _flickerDuration ? .9f : (1 - progress) * .32f);
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
