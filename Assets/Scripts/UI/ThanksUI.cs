using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

public sealed class ThanksUI : MonoBehaviour
{
    [SerializeField] private RectTransform _credits;
    [SerializeField] private RectTransform _viewport;
    [SerializeField] private Button _closeButton;
    [SerializeField] private TMP_Text _specialThanks;
    [SerializeField] private CanvasGroup _specialOpacity;
    [SerializeField] private CRTPausePresentation _presentation;
    [SerializeField] private GameObject _title;
    [SerializeField] private GameObject _divider;
    [SerializeField] private string[] _specialLines =
    {
        "特别鸣谢", "Echo Game Studio", "TA/社长大人\nKiiye", "And……", "You"
    };
    [Min(0)] public float ScrollSpeed = 32f;
    [Min(.01f)] public float FadeDuration = 1.2f;
    [Min(0)] public float LineHoldDuration = 1.8f;
    [SerializeField, Min(0)] private float _entranceDelay = .7f;
    [SerializeField, Min(0)] private float _returnDelay = 1f;
    private float _elapsed;
    private bool _showingSpecial;
    private bool _returnPending;
    private float _returnElapsed;
    public event Action Closed;

    private void Awake() => _closeButton.onClick.AddListener(Close);

    private void OnEnable()
    {
        _showingSpecial = false;
        _returnPending = false;
        _returnElapsed = 0;
        _elapsed = 0;
        _presentation.enabled = true;
        _title.SetActive(true);
        _divider.SetActive(true);
        _viewport.gameObject.SetActive(true);
        _specialThanks.gameObject.SetActive(false);
        _specialOpacity.alpha = 0;
        _closeButton.gameObject.SetActive(false);
        _closeButton.navigation = new Navigation { mode = Navigation.Mode.None };
        Canvas.ForceUpdateCanvases();
        _credits.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, _credits.GetComponent<TMP_Text>().preferredHeight);
        _credits.anchoredPosition = new Vector2(0, -_viewport.rect.height);
        EventSystem.current?.SetSelectedGameObject(null);
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        // 收尾等待使用真实时间，Shift 只加速致谢内容。
        if (_returnPending)
        {
            _returnElapsed += Time.unscaledDeltaTime;
            if (_returnElapsed >= _returnDelay) { Close(); return; }
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) Close();
            return;
        }
        float delta = Time.unscaledDeltaTime *
            (keyboard != null && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed) ? 2f : 1f);
        _elapsed += delta;
        if (_showingSpecial) UpdateSpecialThanks();
        else if (_elapsed >= _entranceDelay)
        {
            _credits.anchoredPosition += Vector2.up * (ScrollSpeed * delta);
            if (_credits.anchoredPosition.y > _credits.rect.height) BeginSpecialThanks();
        }
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) Close();
    }

    private void BeginSpecialThanks()
    {
        _showingSpecial = true;
        _elapsed = 0;
        // 每个数组元素是一页，页内换行保留同组署名并整体居中。
        _presentation.enabled = false;
        _title.SetActive(false);
        _divider.SetActive(false);
        _viewport.gameObject.SetActive(false);
        _specialThanks.gameObject.SetActive(true);
        UpdateSpecialThanks();
    }

    private void UpdateSpecialThanks()
    {
        if (_returnPending) return;
        float fade = Mathf.Max(.01f, FadeDuration);
        float duration = fade * 2 + Mathf.Max(0, LineHoldDuration);
        int index = Mathf.Min(Mathf.FloorToInt(_elapsed / duration), _specialLines.Length - 1);
        if (index < 0) { BeginReturn(); return; }
        float clock = _elapsed - index * duration;
        bool last = index == _specialLines.Length - 1;
        _specialThanks.text = _specialLines[index];
        _specialOpacity.alpha = clock < fade ? Mathf.Clamp01(clock / fade)
            : last ? 1 : Mathf.Clamp01((duration - clock) / fade);
        if (last && clock >= fade + Mathf.Max(0, LineHoldDuration)) BeginReturn();
    }

    private void BeginReturn()
    {
        _returnPending = true;
        _returnElapsed = 0;
        RevealReturnButton();
    }

    private void RevealReturnButton()
    {
        if (_closeButton.gameObject.activeSelf) return;
        _closeButton.gameObject.SetActive(true);
        EventSystem.current?.SetSelectedGameObject(_closeButton.gameObject);
    }

    public void Close() { gameObject.SetActive(false); Closed?.Invoke(); }
}
