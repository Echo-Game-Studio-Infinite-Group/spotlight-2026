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

    public void RetryGame() => LoadScene(SceneManager.GetActiveScene().path);
    private void ShowSettings(bool visible)
    {
        _settingsPanel.SetActive(visible);
        _pauseContent.SetActive(!visible);
        EventSystem.current.SetSelectedGameObject(visible ? _fullscreenButton.gameObject : _resumeButton.gameObject);
    }

    public void ReturnToTitle() => LoadScene("Assets/Scenes/MainMenu.unity");

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
