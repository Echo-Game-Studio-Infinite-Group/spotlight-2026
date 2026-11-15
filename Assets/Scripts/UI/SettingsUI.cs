using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

public sealed class SettingsUI : MonoBehaviour
{
    [SerializeField] private Button _fullscreenButton;
    [SerializeField] private Button _closeButton;
    public event Action Closed;

    private void Awake()
    {
        _fullscreenButton.onClick.AddListener(() => Screen.fullScreen = !Screen.fullScreen);
        _closeButton.onClick.AddListener(Close);
    }

    private void OnEnable() => EventSystem.current?.SetSelectedGameObject(_fullscreenButton.gameObject);

    private void Update()
    {
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) Close();
    }

    public void Close()
    {
        gameObject.SetActive(false);
        Closed?.Invoke();
    }
}
