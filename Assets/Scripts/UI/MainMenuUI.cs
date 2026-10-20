using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public class MainMenuUI : MonoBehaviour
{
    [SerializeField] private GameObject _mainPanel;
    [SerializeField] private Button _startButton;
    [SerializeField] private Button _continueButton;
    [SerializeField] private Button _settingsButton;
    [SerializeField] private Button _exitButton;
    [SerializeField] private Button _levelSelectButton;
    [SerializeField] private Button _thanksButton;
    [SerializeField] private Button _clearSaveButton;
    [SerializeField] private LevelSelectUI _levelSelectPanel;
    [SerializeField] private ThanksUI _thanksPanel;
    [SerializeField] private GameObject _settingsPanel;
    [SerializeField] private Button _closeSettingsButton;
    [SerializeField] private Button _fullscreenButton;
    [SerializeField] private float _buttonTop = -35;
    [SerializeField] private float _buttonSpacing = 105;
    [SerializeField] private string _gameScenePath = "Assets/Scenes/FormalFlowGame.unity";
    private UnityAction _resume;
    private static bool _showThanksOnLoad;

    // 跨场景只传递一次展示请求，复用主菜单已有的致谢实例与返回逻辑。
    public static void RequestThanksOnLoad() => _showThanksOnLoad = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetThanksRequest() => _showThanksOnLoad = false;

    private void Awake()
    {
        _startButton.onClick.AddListener(() => { GameJam.Flow.LevelSelection.Clear(); StartGame(); });
        _levelSelectButton.onClick.AddListener(() => ShowPage(_levelSelectPanel.gameObject));
        _thanksButton.onClick.AddListener(() => ShowPage(_thanksPanel.gameObject));
        _levelSelectPanel.Selected += _ => StartGame();
        _levelSelectPanel.Closed += () => ReturnToMenu(_levelSelectButton);
        _thanksPanel.Closed += () => ReturnToMenu(_thanksButton);
        _continueButton.onClick.AddListener(ContinueGame);
        _clearSaveButton.onClick.AddListener(ClearSave);
        _settingsButton.onClick.AddListener(() => ShowSettings(true));
        _exitButton.onClick.AddListener(ExitGame);
        _settingsPanel.GetComponent<SettingsUI>().Closed += () => ReturnToMenu(_settingsButton);
    }

    private void Start()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        ConfigureContinue(SaveManager.HasSave, ResumeSave);
        EventSystem.current.SetSelectedGameObject(null);
        if (_showThanksOnLoad)
        {
            _showThanksOnLoad = false;
            ShowPage(_thanksPanel.gameObject);
        }
    }

    private void Update()
    {
        if (!_mainPanel.activeSelf) return;
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
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

    private void ClearSave()
    {
        SaveManager.ClearPlayerSave();
        ConfigureContinue(false, null);
        EventSystem.current.SetSelectedGameObject(null);
    }

    private void ResumeSave()
    {
        if (SaveManager.PrepareContinue(_gameScenePath, out string path)) LoadGameScene(path);
    }

    public void RefreshButtons()
    {
        bool canContinue = _resume != null;
        _continueButton.gameObject.SetActive(canContinue);
        _clearSaveButton.interactable = SaveManager.HasSave;
        Button[] buttons = canContinue ? new[] { _startButton, _continueButton, _levelSelectButton, _settingsButton, _thanksButton, _exitButton }
            : new[] { _startButton, _levelSelectButton, _settingsButton, _thanksButton, _exitButton };
        for (int i = 0; i < buttons.Length; i++)
        {
            ((RectTransform)buttons[i].transform).anchoredPosition = new Vector2(0, _buttonTop - i * _buttonSpacing);
            buttons[i].navigation = new Navigation { mode = Navigation.Mode.Explicit,
                selectOnUp = buttons[(i + buttons.Length - 1) % buttons.Length],
                selectOnDown = buttons[(i + 1) % buttons.Length],
                selectOnLeft = _clearSaveButton.interactable ? _clearSaveButton : null };
        }
        _clearSaveButton.navigation = new Navigation { mode = Navigation.Mode.Explicit,
            selectOnUp = _exitButton, selectOnDown = _startButton, selectOnRight = _startButton };
    }

    private void ShowSettings(bool visible)
    {
        if (visible) ShowPage(_settingsPanel);
        else ReturnToMenu(_settingsButton);
    }

    private void ShowPage(GameObject page)
    {
        EventSystem.current.SetSelectedGameObject(null);
        _mainPanel.SetActive(false);
        _settingsPanel.SetActive(page == _settingsPanel);
        _levelSelectPanel.gameObject.SetActive(page == _levelSelectPanel.gameObject);
        _thanksPanel.gameObject.SetActive(page == _thanksPanel.gameObject);
    }

    private void ReturnToMenu(Button selected)
    {
        _settingsPanel.SetActive(false);
        _levelSelectPanel.gameObject.SetActive(false);
        _thanksPanel.gameObject.SetActive(false);
        _mainPanel.SetActive(true);
        EventSystem.current.SetSelectedGameObject(selected.gameObject);
    }

    public void StartGame()
    {
        SaveManager.CancelContinue();
        LoadGameScene(_gameScenePath);
    }

    private void LoadGameScene(string path)
    {
#if UNITY_EDITOR
        UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
            path, new LoadSceneParameters(LoadSceneMode.Single));
#else
        if (!Application.CanStreamedLevelBeLoaded(path))
        {
            SaveManager.CancelContinue();
            Debug.LogError($"Scene is missing from Build Settings: {path}", this);
            return;
        }
        SceneManager.LoadScene(path);
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
