using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public class PauseUIManager : MonoBehaviour
{
    [SerializeField] private GameObject _pauseMenuPanel;
    [SerializeField] private Button _resumeButton;
    [SerializeField] private Button _retryButton;
    [SerializeField] private Button _returnButton;
    [SerializeField] private Button _settingsButton;
    [SerializeField] private GameObject _settingsPanel;
    [SerializeField] private GameObject _pauseContent;
    [SerializeField] private Button _closeSettingsButton;
    [SerializeField] private Button _fullscreenButton;
    private PlayerInputReader _input;
    private bool _isPause;
    // 多槽位入口与页面在运行时构建，不改动 PauseMenu 预制体结构。
    private Button _saveSlotsButton;
    private SaveSlotPanel _saveSlotPanel;

    private void Awake()
    {
        _input = FindObjectOfType<PlayerInputReader>();
        _resumeButton.onClick.AddListener(ResumeGame);
        _retryButton.onClick.AddListener(RetryGame);
        _returnButton.onClick.AddListener(ReturnToTitle);
        _settingsButton.onClick.AddListener(() => ShowSettings(true));
        _closeSettingsButton.onClick.AddListener(() => ShowSettings(false));
        _fullscreenButton.onClick.AddListener(() => Screen.fullScreen = !Screen.fullScreen);
        _pauseMenuPanel.SetActive(false);
        EnsureSaveSlotsButton();
    }

    private void OnEnable() => _input.PauseRequested += TogglePause;

    private void OnDisable()
    {
        if (_input != null) _input.PauseRequested -= TogglePause;
        if (_isPause) ResumeGame();
    }

    private void TogglePause()
    {
        if (DeathRespawnUI.Instance != null && DeathRespawnUI.Instance.IsOpen) return;
        if (_saveSlotPanel != null && _saveSlotPanel.IsOpen) { CloseSaveSlots(); return; }
        if (_isPause && _settingsPanel.activeSelf) { ShowSettings(false); return; }
        if (_isPause) ResumeGame();
        else PauseGame();
    }

    public void PauseGame()
    {
        if (DeathRespawnUI.Instance != null && DeathRespawnUI.Instance.IsOpen) return;
        _isPause = true;
        TimeManager.SetPaused(true);
        _input.SetGameplayEnabled(false);
        AudioListener.pause = true;
        _pauseMenuPanel.SetActive(true);
        ShowSettings(false);
        EventSystem.current.SetSelectedGameObject(_resumeButton.gameObject);
    }

    public void ResumeGame()
    {
        if (DeathRespawnUI.Instance != null && DeathRespawnUI.Instance.IsOpen) return;
        _isPause = false;
        TimeManager.SetPaused(false);
        // 场景卸载时玩家可能先于 UI 被销毁，仍需解除全局暂停。
        if (_input != null) _input.SetGameplayEnabled(true);
        AudioListener.pause = false;
        _pauseMenuPanel.SetActive(false);
    }

    public void RetryGame()
    {
        var flow = GameJam.Flow.RunFlowController.Current;
        if (flow == null)
        {
            LoadScene(_input.gameObject.scene.path);
            return;
        }
        // 活动场景是叠加加载的房间，重载它会丢失玩家和流程宿主。
        if (!flow.RestartRun()) return;
        ResumeGame();
        _input.SetGameplayEnabled(false);
    }

    private void EnsureSaveSlotsButton()
    {
        if (_saveSlotsButton != null || _settingsButton == null) return;
        // 五个按钮在 500x400 的列表区域内均分；“存档”插在继续与重新开始之间。
        const float spacing = 80f;
        float firstY = spacing * 2f;
        ((RectTransform)_resumeButton.transform).anchoredPosition = new Vector2(0f, firstY);
        ((RectTransform)_retryButton.transform).anchoredPosition = new Vector2(0f, firstY - spacing * 2f);
        ((RectTransform)_settingsButton.transform).anchoredPosition = new Vector2(0f, firstY - spacing * 3f);
        ((RectTransform)_returnButton.transform).anchoredPosition = new Vector2(0f, firstY - spacing * 4f);

        _saveSlotsButton = Instantiate(_settingsButton, _settingsButton.transform.parent);
        _saveSlotsButton.name = "SaveSlotsButton";
        RectTransform newRect = (RectTransform)_saveSlotsButton.transform;
        newRect.anchorMin = newRect.anchorMax = new Vector2(.5f, .5f);
        newRect.pivot = new Vector2(.5f, .5f);
        newRect.anchoredPosition = new Vector2(0f, firstY - spacing);
        _saveSlotsButton.onClick.RemoveAllListeners();
        _saveSlotsButton.onClick.AddListener(OpenSaveSlots);
        TMP_Text label = _saveSlotsButton.GetComponentInChildren<TMP_Text>(true);
        if (label != null) label.text = "存档";

        Navigation resumeNavigation = _resumeButton.navigation;
        resumeNavigation.mode = Navigation.Mode.Explicit;
        resumeNavigation.selectOnDown = _saveSlotsButton;
        _resumeButton.navigation = resumeNavigation;

        Navigation retryNavigation = _retryButton.navigation;
        retryNavigation.mode = Navigation.Mode.Explicit;
        retryNavigation.selectOnUp = _saveSlotsButton;
        _retryButton.navigation = retryNavigation;

        _saveSlotsButton.navigation = new Navigation
        {
            mode = Navigation.Mode.Explicit,
            selectOnUp = _resumeButton,
            selectOnDown = _retryButton,
            selectOnLeft = _resumeButton,
            selectOnRight = _resumeButton
        };
    }

    private void EnsureSaveSlotPanel()
    {
        if (_saveSlotPanel != null) return;
        Canvas canvas = GetComponentInParent<Canvas>();
        if (canvas == null)
            foreach (Canvas candidate in FindObjectsOfType<Canvas>())
                if (candidate.isRootCanvas) { canvas = candidate; break; }
        if (canvas == null) return;
        _saveSlotPanel = SaveSlotPanel.Create((RectTransform)canvas.rootCanvas.transform,
            _resumeButton.GetComponentInChildren<TMP_Text>(true));
        _saveSlotPanel.SaveRequested += SaveToSlot;
        _saveSlotPanel.LoadRequested += LoadSlot;
        _saveSlotPanel.Closed += CloseSaveSlots;
    }

    private void OpenSaveSlots()
    {
        EnsureSaveSlotPanel();
        if (_saveSlotPanel == null) return;
        _pauseContent.SetActive(false);
        _settingsPanel.SetActive(false);
        _saveSlotPanel.Show(SaveSlotPanel.Mode.Pause);
    }

    private void CloseSaveSlots()
    {
        if (_saveSlotPanel != null) _saveSlotPanel.Hide();
        _pauseContent.SetActive(true);
        _settingsPanel.SetActive(false);
        if (EventSystem.current != null && _saveSlotsButton != null)
            EventSystem.current.SetSelectedGameObject(_saveSlotsButton.gameObject);
    }

    private void SaveToSlot(int slot)
    {
        SaveManager.SetActiveSlot(slot);
        if (PlayerMotor.Active != null) SaveManager.PlayerObj = PlayerMotor.Active.gameObject;
        SaveManager.SavePlayer();
        _saveSlotPanel.Refresh();
    }

    private void LoadSlot(int slot)
    {
        string fallback = _input != null ? _input.gameObject.scene.path : string.Empty;
        if (!SaveManager.PrepareContinue(slot, fallback, out string path)) return;
        // 按需求直接读取目标槽，当前未保存进度丢弃；先收回流式房间再切场景。
        var flow = GameJam.Flow.RunFlowController.Current;
        if (flow != null)
        {
            StartCoroutine(LoadSlotAfterRelease(flow, path));
            return;
        }
        LoadScene(path);
    }

    private IEnumerator LoadSlotAfterRelease(GameJam.Flow.RunFlowController flow, string path)
    {
        yield return flow.ReleaseRooms();
        LoadScene(path);
    }

    private void ShowSettings(bool visible)
    {
        _settingsPanel.SetActive(visible);
        _pauseContent.SetActive(!visible);
        EventSystem.current.SetSelectedGameObject(visible ? _fullscreenButton.gameObject : _resumeButton.gameObject);
    }

    public void ReturnToTitle()
    {
        if (PlayerMotor.Active != null)
        {
            SaveManager.PlayerObj = PlayerMotor.Active.gameObject;
            SaveManager.SavePlayer();
        }
        LoadScene("Assets/Scenes/MainMenu.unity");
    }

    private void LoadScene(string path)
    {
#if !UNITY_EDITOR
        if (!Application.CanStreamedLevelBeLoaded(path))
        {
            Debug.LogError($"Scene is missing from Build Settings: {path}", this);
            return;
        }
#endif
        ResumeGame();
        _input.SetGameplayEnabled(false);
#if UNITY_EDITOR
        UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
            path, new LoadSceneParameters(LoadSceneMode.Single));
#else
        SceneManager.LoadScene(path);
#endif
    }
}
