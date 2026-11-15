using System;
using GameJam.Flow;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
public sealed class LevelSelectUI : MonoBehaviour
{
    [SerializeField] private RunFlowConfig _config;
    [SerializeField] private RectTransform _content;
    [SerializeField] private Button _template;
    [SerializeField] private Button _closeButton;
    public event Action<RoomVariantDefinition> Selected;
    public event Action Closed;
    private bool _populated;
    private GameObject _lastSelected;
    private void OnEnable()
    {
        if (!_populated)
        {
            _closeButton.onClick.AddListener(Close);
            var buttons = new System.Collections.Generic.List<Button>();
            foreach (var variant in LevelSelection.GetLevels(_config))
            {
                if (_config.AlwaysStartAtInitialRoom && variant != _config.InitialRoom) continue;
                var button = Instantiate(_template, _content);
                button.name = variant.Id;
                button.GetComponentInChildren<TMP_Text>(true).text = variant.Room.DisplayName + " / " + variant.Id;
                button.onClick.AddListener(() => { if (LevelSelection.Select(_config, variant)) Selected?.Invoke(variant); });
                button.gameObject.SetActive(true);
                buttons.Add(button);
            }
            buttons.Add(_closeButton);
            for (int i = 0; i < buttons.Count; i++)
                buttons[i].navigation = new Navigation { mode = Navigation.Mode.Explicit,
                    selectOnUp = buttons[(i + buttons.Count - 1) % buttons.Count],
                    selectOnDown = buttons[(i + 1) % buttons.Count] };
            _populated = true;
        }
        EventSystem.current?.SetSelectedGameObject(_closeButton.gameObject);
    }
    private void Update()
    {
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) { Close(); return; }
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selected == _lastSelected) return;
        _lastSelected = selected;
        if (selected == null || selected.transform.parent != _content) return;
        Canvas.ForceUpdateCanvases();
        var viewport = (RectTransform)_content.parent;
        var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(viewport, selected.transform);
        float offset = bounds.max.y > viewport.rect.yMax ? viewport.rect.yMax - bounds.max.y
            : bounds.min.y < viewport.rect.yMin ? viewport.rect.yMin - bounds.min.y : 0;
        _content.anchoredPosition += Vector2.up * offset;
    }
    public void Close() { gameObject.SetActive(false); Closed?.Invoke(); }
}
