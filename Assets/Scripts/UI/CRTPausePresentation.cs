using UnityEngine;
using UnityEngine.UI;

public sealed class CRTPausePresentation : MonoBehaviour
{
    [SerializeField] private RawImage _title;
    [SerializeField] private CanvasGroup _titleOpacity;
    [SerializeField] private RectTransform _divider;
    [SerializeField] private CanvasGroup _buttons;
    [SerializeField, Min(.01f)] private float _flickerDuration = .4f;
    [SerializeField, Min(.01f)] private float _revealDuration = .3f;
    [SerializeField, Min(1)] private float _flickerFrameRate = 24;
    [SerializeField, Range(0, 1)] private float _flickerOpacity = .12f;
    [SerializeField, Range(0, 1)] private float _entryGlitch = .9f;
    [SerializeField, Range(0, 1)] private float _idleGlitch = .12f;
    private Material _titleInstance;
    private float _startedAt;

    private void OnEnable()
    {
        _startedAt = Time.unscaledTime;
        Preview(0);
    }

    private void Update() => Preview(Time.unscaledTime - _startedAt);

    public void Preview(float clock)
    {
        // 独立材质避免暂停标题的时钟和故障强度污染主菜单。
        if (_titleInstance == null)
        {
            _titleInstance = new Material(_title.material);
            _title.material = _titleInstance;
        }
        bool entering = clock < _flickerDuration;
        _titleOpacity.alpha = entering && Mathf.Floor(clock * _flickerFrameRate) % 3 == 0 ? _flickerOpacity : 1;
        _titleInstance.SetFloat("_Clock", clock);
        _titleInstance.SetFloat("_Glitch", entering ? _entryGlitch : _idleGlitch);
        float progress = Mathf.Clamp01((clock - _flickerDuration) / _revealDuration);
        _divider.localScale = new Vector3(progress, 1, 1);
        _buttons.alpha = progress;
        _buttons.interactable = progress >= 1;
        _buttons.blocksRaycasts = progress >= 1;
    }

    private void OnDestroy()
    {
        if (_titleInstance == null) return;
        if (Application.isPlaying) Destroy(_titleInstance);
        else DestroyImmediate(_titleInstance);
    }
}
