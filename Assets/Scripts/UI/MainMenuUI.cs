﻿using TMPro;
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
    // 多槽位入口与页面在运行时构建，避免改动场景与预制体接线。
    private Button _saveSlotsButton;
    private SaveSlotPanel _saveSlotPanel;
    private int _pendingNewGameSlot;

    // 跨场景只传递一次展示请求，复用主菜单已有的致谢实例与返回逻辑。
    public static void RequestThanksOnLoad() => _showThanksOnLoad = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetThanksRequest() => _showThanksOnLoad = false;

    private void Awake()
    {
        _startButton.onClick.AddListener(StartNewGameFromMainButton);
        _levelSelectButton.onClick.AddListener(() => ShowPage(_levelSelectPanel.gameObject));
        _thanksButton.onClick.AddListener(() => ShowPage(_thanksPanel.gameObject));
        _levelSelectPanel.Selected += _ => OnLevelSelected();
        _levelSelectPanel.Closed += ReturnFromLevelSelect;
        _thanksPanel.Closed += () => ReturnToMenu(_thanksButton);
        _continueButton.onClick.AddListener(ContinueGame);
        _clearSaveButton.onClick.AddListener(ClearSave);
        _settingsButton.onClick.AddListener(() => ShowSettings(true));
        _exitButton.onClick.AddListener(ExitGame);
        _settingsPanel.GetComponent<SettingsUI>().Closed += () => ReturnToMenu(_settingsButton);
        EnsureSaveSlotsButton();
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
        int slot = SaveManager.FindLatestSlot();
        if (slot <= 0) slot = SaveManager.ActiveSlot;
        SaveManager.SetActiveSlot(slot);
        if (SaveManager.PrepareContinue(_gameScenePath, out string path)) LoadGameScene(path);
    }

    public void RefreshButtons()
    {
        bool canContinue = _resume != null;
        _continueButton.gameObject.SetActive(canContinue);
        _clearSaveButton.interactable = SaveManager.HasSave;
        Button[] buttons = canContinue ? new[] { _startButton, _continueButton, _saveSlotsButton, _levelSelectButton, _settingsButton, _thanksButton, _exitButton }
            : new[] { _startButton, _saveSlotsButton, _levelSelectButton, _settingsButton, _thanksButton, _exitButton };
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
        if (_saveSlotPanel != null) _saveSlotPanel.Hide();
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

    // “开始游戏”沿用原入口：优先写入第一个空槽；三槽都有档时沿用激活槽，首次存档才覆盖。
    private void StartNewGameFromMainButton()
    {
        int slot = SaveManager.FindFirstEmptySlot();
        SaveManager.BeginNewGame(slot > 0 ? slot : SaveManager.ActiveSlot);
        GameJam.Flow.LevelSelection.Clear();
        StartGame();
    }

    private void EnsureSaveSlotsButton()
    {
        if (_saveSlotsButton != null) return;
        _saveSlotsButton = Instantiate(_levelSelectButton, _levelSelectButton.transform.parent);
        _saveSlotsButton.name = "SaveSlotsButton";
        _saveSlotsButton.onClick.RemoveAllListeners();
        _saveSlotsButton.onClick.AddListener(OpenSaveSlots);
        TMP_Text label = _saveSlotsButton.GetComponentInChildren<TMP_Text>(true);
        if (label != null) label.text = "选择存档";
    }

    private void EnsureSaveSlotPanel()
    {
        if (_saveSlotPanel != null) return;
        // 主菜单面板本身就是 Canvas 根，槽位页必须挂在独立 Overlay Canvas 上，
        // 否则 OpenSaveSlots 隐藏主菜单时会把它一起隐藏，表现为主菜单黑屏。
        GameObject canvasObject = new GameObject("SaveSlotCanvas", typeof(RectTransform), typeof(Canvas),
            typeof(CanvasScaler), typeof(GraphicRaycaster));
        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;
        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0f;
        _saveSlotPanel = SaveSlotPanel.Create((RectTransform)canvasObject.transform,
            _startButton.GetComponentInChildren<TMP_Text>(true));
        _saveSlotPanel.SlotActivated += OnSlotActivated;
        _saveSlotPanel.Closed += OnSaveSlotsClosed;
    }

    private void OpenSaveSlots()
    {
        EnsureSaveSlotPanel();
        if (_saveSlotPanel == null) return;
        EventSystem.current.SetSelectedGameObject(null);
        _mainPanel.SetActive(false);
        _settingsPanel.SetActive(false);
        _levelSelectPanel.gameObject.SetActive(false);
        _thanksPanel.gameObject.SetActive(false);
        _saveSlotPanel.Show(SaveSlotPanel.Mode.Title);
    }

    private void OnSlotActivated(int slot)
    {
        SaveManager.SetActiveSlot(slot);
        if (SaveManager.HasSaveInSlot(slot))
        {
            _saveSlotPanel.Hide();
            if (SaveManager.PrepareContinue(_gameScenePath, out string path)) LoadGameScene(path);
            return;
        }
        // 空槽沿用到现有选关页，选完关卡才真正开局。
        _pendingNewGameSlot = slot;
        ShowPage(_levelSelectPanel.gameObject);
    }

    private void OnSaveSlotsClosed()
    {
        _pendingNewGameSlot = 0;
        ReturnToMenu(_saveSlotsButton);
    }

    private void OnLevelSelected()
    {
        int slot = _pendingNewGameSlot;
        _pendingNewGameSlot = 0;
        if (slot > 0) SaveManager.BeginNewGame(slot);
        StartGame();
    }

    private void ReturnFromLevelSelect()
    {
        if (_pendingNewGameSlot > 0)
        {
            OpenSaveSlots();
            return;
        }
        ReturnToMenu(_levelSelectButton);
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
