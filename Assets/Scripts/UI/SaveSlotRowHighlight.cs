using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 槽位卡片的互动反馈：悬浮为蓝色，点击选中为金色。
// 悬浮只改变视觉，不改变当前槽位；暂停页只有点击行才会切换选中目标。
public sealed class SaveSlotRowHighlight : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler, ISelectHandler, IDeselectHandler
{
    private const float HoverLevel = 1f;
    private const float SelectedLevel = 0.55f;
    private const float FocusLevel = 0.3f;
    private const float FadeDuration = 0.16f;
    private const float FillAlpha = 0.28f;
    private const float HoverScale = 1.02f;
    private const float HoverOffsetX = 8f;

    private static readonly Color HoverFillColor = new Color(0.35f, 0.75f, 1f);
    private static readonly Color SelectedFillColor = new Color(1f, 0.78f, 0.25f);
    private static readonly Color HoverAccentColor = new Color(0.55f, 0.9f, 1f);
    private static readonly Color SelectedAccentColor = new Color(1f, 0.82f, 0.3f);
    private static readonly Color SelectedTextColor = new Color(1f, 0.9f, 0.6f);

    private int _slot;
    private RectTransform _rect;
    private Vector2 _basePosition;
    private Image _fill;
    private Image _accent;
    private TMP_Text _label;
    private Color _labelBaseColor = new Color(1f, 1f, 1f, 0.82f);
    private bool _hovered;
    private bool _eventSelected;
    private bool _eventSelectionCounts = true;
    private bool _persistentSelected;
    private float _t;
    private float _gold;

    public static SaveSlotRowHighlight Attach(Button row, TMP_Text label, int slot)
    {
        SaveSlotRowHighlight highlight = row.gameObject.AddComponent<SaveSlotRowHighlight>();
        highlight.Initialize(row, label, slot);
        return highlight;
    }

    // 由面板同步“点击选中的槽位”：暂停页点击行后焦点会移到保存按钮，这里保证行仍有金色选中反馈。
    public void SetPersistentSelected(bool selected) => _persistentSelected = selected;

    // 标题页用 EventSystem 选中作为主要高亮；暂停页只用它做较弱的键盘焦点提示。
    public void SetEventSelectionMode(bool countsAsSelection) => _eventSelectionCounts = countsAsSelection;

    private void Initialize(Button row, TMP_Text label, int slot)
    {
        _slot = slot;
        _rect = (RectTransform)row.transform;
        _basePosition = _rect.anchoredPosition;
        _label = label;

        GameObject fillObject = new GameObject($"HighlightFill_{slot}", typeof(RectTransform), typeof(Image));
        RectTransform fillRect = (RectTransform)fillObject.transform;
        fillRect.SetParent(_rect, false);
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;
        fillRect.SetAsFirstSibling();
        _fill = fillObject.GetComponent<Image>();
        _fill.color = new Color(HoverFillColor.r, HoverFillColor.g, HoverFillColor.b, 0f);
        _fill.raycastTarget = false;

        GameObject accentObject = new GameObject($"Accent_{slot}", typeof(RectTransform), typeof(Image));
        RectTransform accentRect = (RectTransform)accentObject.transform;
        accentRect.SetParent(_rect, false);
        accentRect.anchorMin = new Vector2(0f, 0.5f);
        accentRect.anchorMax = new Vector2(0f, 0.5f);
        accentRect.pivot = new Vector2(0f, 0.5f);
        accentRect.sizeDelta = new Vector2(6f, Mathf.Max(24f, _rect.rect.height * 0.75f));
        accentRect.anchoredPosition = new Vector2(10f, 0f);
        accentRect.SetSiblingIndex(1);
        _accent = accentObject.GetComponent<Image>();
        _accent.color = new Color(HoverAccentColor.r, HoverAccentColor.g, HoverAccentColor.b, 0f);
        _accent.raycastTarget = false;

        Apply(0f, 0f);
    }

    private void Update()
    {
        _gold = Mathf.MoveTowards(_gold, _persistentSelected ? 1f : 0f, Time.unscaledDeltaTime / FadeDuration);

        float selectedLevel = 0f;
        if (_persistentSelected) selectedLevel = SelectedLevel;
        else if (_eventSelected) selectedLevel = _eventSelectionCounts ? SelectedLevel : FocusLevel;

        float target = _hovered ? HoverLevel : selectedLevel;
        _t = Mathf.MoveTowards(_t, target, Time.unscaledDeltaTime / FadeDuration);
        Apply(_t, _gold);
    }

    private void Apply(float t, float gold)
    {
        Color fillColor = Color.Lerp(HoverFillColor, SelectedFillColor, gold);
        if (_fill != null)
            _fill.color = new Color(fillColor.r, fillColor.g, fillColor.b, FillAlpha * t);

        if (_accent != null)
        {
            Color accentColor = Color.Lerp(HoverAccentColor, SelectedAccentColor, gold);
            _accent.color = new Color(accentColor.r, accentColor.g, accentColor.b, Mathf.Clamp01(t * 1.6f));
            _accent.rectTransform.localScale = new Vector3(1f, Mathf.Lerp(0.72f, 1f, t), 1f);
        }

        if (_rect != null)
        {
            _rect.anchoredPosition = _basePosition + new Vector2(HoverOffsetX * t, 0f);
            _rect.localScale = Vector3.one * Mathf.Lerp(1f, HoverScale, t);
        }

        if (_label != null)
        {
            Color highlightedText = Color.Lerp(Color.white, SelectedTextColor, gold);
            _label.color = Color.Lerp(_labelBaseColor, highlightedText, t);
        }
    }

    public void OnPointerEnter(PointerEventData eventData) => _hovered = true;

    public void OnPointerExit(PointerEventData eventData) => _hovered = false;

    public void OnSelect(BaseEventData eventData) => _eventSelected = true;

    public void OnDeselect(BaseEventData eventData) => _eventSelected = false;

    private void OnDisable()
    {
        // 面板关闭时鼠标可能没有触发 PointerExit，这里统一复位，避免下次打开残留。
        _hovered = false;
        _eventSelected = false;
        _t = 0f;
        _gold = 0f;
    }
}
