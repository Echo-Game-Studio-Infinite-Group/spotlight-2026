using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public class MainMenuUI : MonoBehaviour
{
    [SerializeField] private Button _startButton;
    [SerializeField] private Button _continueButton;
    [SerializeField] private Button _settingsButton;
    [SerializeField] private Button _exitButton;
    [SerializeField] private GameObject _settingsPanel;
    [SerializeField] private Button _closeSettingsButton;
    [SerializeField] private Button _fullscreenButton;
    [SerializeField] private float _buttonTop = -35;
    [SerializeField] private float _buttonSpacing = 105;
    [SerializeField] private string _gameScenePath = "Assets/Scenes/TestScene.unity";
    private UnityAction _resume;

    private void Awake()
    {
        _startButton.onClick.AddListener(StartGame);
        _continueButton.onClick.AddListener(ContinueGame);
        _settingsButton.onClick.AddListener(() => ShowSettings(true));
        _exitButton.onClick.AddListener(ExitGame);
        _closeSettingsButton.onClick.AddListener(() => ShowSettings(false));
        _fullscreenButton.onClick.AddListener(() => Screen.fullScreen = !Screen.fullScreen);
    }

    private void Start()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        RefreshButtons();
        EventSystem.current.SetSelectedGameObject(null);
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        if (keyboard.escapeKey.wasPressedThisFrame && _settingsPanel.activeSelf) ShowSettings(false);
        if (EventSystem.current.currentSelectedGameObject == null &&
            (keyboard.downArrowKey.wasPressedThisFrame || keyboard.upArrowKey.wasPressedThisFrame ||
             keyboard.wKey.wasPressedThisFrame || keyboard.sKey.wasPressedThisFrame))
            EventSystem.current.SetSelectedGameObject(_settingsPanel.activeSelf ? _fullscreenButton.gameObject : _startButton.gameObject);
    }

    // 存档提供方必须同时提供真实的恢复操作，避免把新游戏伪装成继续游戏。
    public void ConfigureContinue(bool available, UnityAction resume)
    {
        _resume = available ? resume : null;
        RefreshButtons();
    }

    private void ContinueGame() => _resume?.Invoke();

    private void RefreshButtons()
    {
        bool canContinue = _resume != null;
        _continueButton.gameObject.SetActive(canContinue);
        Button[] buttons = canContinue ? new[] { _startButton, _continueButton, _settingsButton, _exitButton }
            : new[] { _startButton, _settingsButton, _exitButton };
        for (int i = 0; i < buttons.Length; i++)
        {
            ((RectTransform)buttons[i].transform).anchoredPosition = new Vector2(0, _buttonTop - i * _buttonSpacing);
            buttons[i].navigation = new Navigation { mode = Navigation.Mode.Explicit,
                selectOnUp = buttons[(i + buttons.Length - 1) % buttons.Length],
                selectOnDown = buttons[(i + 1) % buttons.Length] };
        }
    }

    private void ShowSettings(bool visible)
    {
        _settingsPanel.SetActive(visible);
        EventSystem.current.SetSelectedGameObject(visible ? _fullscreenButton.gameObject : _settingsButton.gameObject);
    }

    public void StartGame()
    {
#if UNITY_EDITOR
        UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
            _gameScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#else
        if (!Application.CanStreamedLevelBeLoaded(_gameScenePath))
        {
            Debug.LogError($"Scene is missing from Build Settings: {_gameScenePath}", this);
            return;
        }
        SceneManager.LoadScene(_gameScenePath);
#endif
    }

    public void ExitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
