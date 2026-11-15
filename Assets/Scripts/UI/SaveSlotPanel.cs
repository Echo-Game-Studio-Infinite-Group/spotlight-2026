using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// 三槽位页面：标题页“有档读取 / 空档开新游戏”，暂停页“保存 + 读取”。
// 功能阶段用代码构建最小 UI；样式阶段可整体替换为正式预制体，调用方接口保持不变。
public sealed class SaveSlotPanel : MonoBehaviour
{
    public enum Mode { Title, Pause }

    public event Action<int> SlotActivated;
    public event Action<int> SaveRequested;
    public event Action<int> LoadRequested;
    public event Action Closed;

    private Mode _mode;
    private Button[] _rows;
    private TMP_Text[] _rowTexts;
    private GameObject _actions;
    private Button _saveButton;
    private Button _loadButton;
    private Button _closeButton;
    private TMP_Text _saveLabel;
    private TMP_Text _loadLabel;
    private TMP_Text _title;
    private TMP_Text _hint;
    private int _selectedSlot = 1;
    private TMP_FontAsset _font;
    private Material _fontMaterial;
    private Color _textColor = Color.white;
    private float _fontSize = 26f;

    public bool IsOpen => gameObject.activeSelf;

    public static SaveSlotPanel Create(RectTransform parent, TMP_Text styleSource)
    {
        GameObject root = new GameObject("SaveSlotPanel", typeof(RectTransform));
        SaveSlotPanel panel = root.AddComponent<SaveSlotPanel>();
        panel.Build(parent, styleSource);
        return panel;
    }

    private void Build(RectTransform parent, TMP_Text styleSource)
    {
        if (styleSource != null)
        {
            _font = styleSource.font;
            _fontMaterial = styleSource.fontSharedMaterial;
            _textColor = styleSource.color;
            _fontSize = Mathf.Max(20f, styleSource.fontSize * .8f);
        }

        RectTransform rect = (RectTransform)transform;
        rect.SetParent(parent, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.SetAsLastSibling();

        Image backdrop = gameObject.AddComponent<Image>();
        backdrop.color = new Color(0f, 0f, 0f, .82f);
        backdrop.raycastTarget = true;

        _title = CreateLabel(rect, "Title", new Vector2(0f, 370f), new Vector2(900f, 80f), 46f, TextAlignmentOptions.Center);
        _hint = CreateLabel(rect, "Hint", new Vector2(0f, -245f), new Vector2(900f, 40f), 24f, TextAlignmentOptions.Center);

        _rows = new Button[SaveManager.SlotCount];
        _rowTexts = new TMP_Text[SaveManager.SlotCount];
        for (int i = 0; i < SaveManager.SlotCount; i++)
        {
            int slot = i + 1;
            Button row = CreateButton(rect, "Slot" + slot, new Vector2(0f, 210f - i * 170f), new Vector2(920f, 150f));
            TMP_Text label = row.GetComponentInChildren<TMP_Text>(true);
            label.alignment = TextAlignmentOptions.Left;
            label.margin = new Vector4(36f, 8f, 36f, 8f);
            _rows[i] = row;
            _rowTexts[i] = label;
            row.onClick.AddListener(() => OnRowClicked(slot));
        }

        // 暂停页的“保存 / 读取”只在选中槽位后显示；标题页不创建这两个入口。
        _actions = new GameObject("Actions", typeof(RectTransform));
        RectTransform actionsRect = (RectTransform)_actions.transform;
        actionsRect.SetParent(rect, false);
        actionsRect.anchorMin = actionsRect.anchorMax = new Vector2(.5f, .5f);
        actionsRect.sizeDelta = new Vector2(920f, 80f);
        actionsRect.anchoredPosition = new Vector2(0f, -320f);

        _saveButton = CreateButton(actionsRect, "Save", new Vector2(-210f, 0f), new Vector2(360f, 70f));
        _saveLabel = _saveButton.GetComponentInChildren<TMP_Text>(true);
        _saveButton.onClick.AddListener(() => SaveRequested?.Invoke(_selectedSlot));

        _loadButton = CreateButton(actionsRect, "Load", new Vector2(210f, 0f), new Vector2(360f, 70f));
        _loadLabel = _loadButton.GetComponentInChildren<TMP_Text>(true);
        _loadButton.onClick.AddListener(() =>
        {
            if (SaveManager.HasSaveInSlot(_selectedSlot)) LoadRequested?.Invoke(_selectedSlot);
        });

        _closeButton = CreateButton(rect, "Close", new Vector2(0f, -410f), new Vector2(260f, 60f));
        TMP_Text closeLabel = _closeButton.GetComponentInChildren<TMP_Text>(true);
        closeLabel.text = "返回";
        _closeButton.onClick.AddListener(Close);

        gameObject.SetActive(false);
    }

    public void Show(Mode mode)
    {
        _mode = mode;
        _title.text = mode == Mode.Title ? "选择存档" : "存档";
        _hint.gameObject.SetActive(mode == Mode.Pause);
        _actions.SetActive(mode == Mode.Pause);
        gameObject.SetActive(true);
        _selectedSlot = 1;
        Refresh();
        SelectRow(1);
    }

    public void Hide() => gameObject.SetActive(false);

    public void Close()
    {
        if (!IsOpen) return;
        gameObject.SetActive(false);
        Closed?.Invoke();
    }

    public void Refresh()
    {
        for (int i = 0; i < SaveManager.SlotCount; i++)
        {
            int slot = i + 1;
            if (SaveManager.TryGetSummary(slot, out SaveManager.Summary summary))
            {
                _rowTexts[i].text =
                    $"存档 {slot}\n{summary.roomDisplayName}\n混乱度 {summary.chaos} · {summary.SavedAtText}";
            }
            else
            {
                _rowTexts[i].text = _mode == Mode.Title
                    ? $"存档 {slot}\n空档位 · 点击开始新游戏"
                    : $"存档 {slot}\n空档位";
            }
        }
        RefreshActions();
    }

    private void RefreshActions()
    {
        if (_mode != Mode.Pause || _hint == null) return;
        bool hasSave = SaveManager.HasSaveInSlot(_selectedSlot);
        _hint.text = $"当前选中：存档 {_selectedSlot}";
        _saveLabel.text = hasSave ? "覆盖保存到此槽" : "保存到此槽";
        _loadLabel.text = "读取此槽";
        _loadButton.interactable = hasSave;
    }

    private void OnRowClicked(int slot)
    {
        _selectedSlot = slot;
        if (_mode == Mode.Title)
        {
            SlotActivated?.Invoke(slot);
            return;
        }
        RefreshActions();
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(_saveButton.gameObject);
    }

    private void SelectRow(int slot)
    {
        _selectedSlot = slot;
        if (EventSystem.current != null && _rows[slot - 1] != null)
            EventSystem.current.SetSelectedGameObject(_rows[slot - 1].gameObject);
        RefreshActions();
    }

    private void Update()
    {
        if (!IsOpen) return;
        if (_mode == Mode.Title)
        {
            // 暂停页的关闭键由 PauseUIManager 统一处理，这里只管标题页。
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) Close();
            return;
        }
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        for (int i = 0; i < _rows.Length; i++)
        {
            if (selected != _rows[i].gameObject || _selectedSlot == i + 1) continue;
            _selectedSlot = i + 1;
            RefreshActions();
            break;
        }
    }

    private Button CreateButton(RectTransform parent, string name, Vector2 position, Vector2 size)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = position;

        Image image = go.GetComponent<Image>();
        image.color = new Color(1f, 1f, 1f, .08f);

        Button button = go.GetComponent<Button>();
        button.targetGraphic = image;
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(.75f, .92f, 1f, 1f);
        colors.selectedColor = colors.highlightedColor;
        colors.pressedColor = new Color(.55f, .8f, 1f, 1f);
        button.colors = colors;

        GameObject labelObject = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        RectTransform labelRect = (RectTransform)labelObject.transform;
        labelRect.SetParent(rect, false);
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(24f, 8f);
        labelRect.offsetMax = new Vector2(-24f, -8f);

        TMP_Text label = labelObject.GetComponent<TMP_Text>();
        ApplyFont(label, _fontSize);
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;
        return button;
    }

    private TMP_Text CreateLabel(RectTransform parent, string name, Vector2 position, Vector2 size, float fontSize,
        TextAlignmentOptions alignment)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = position;

        TMP_Text text = go.GetComponent<TMP_Text>();
        ApplyFont(text, fontSize);
        text.alignment = alignment;
        text.raycastTarget = false;
        return text;
    }

    private void ApplyFont(TMP_Text text, float fontSize)
    {
        if (_font != null) text.font = _font;
        if (_fontMaterial != null) text.fontSharedMaterial = _fontMaterial;
        text.color = _textColor;
        text.fontSize = fontSize;
        text.enableWordWrapping = false;
    }
}
