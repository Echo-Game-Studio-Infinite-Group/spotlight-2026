using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class PauseUIManager : MonoBehaviour
{
    [SerializeField] private GameObject _pauseMenuPanel;
    private PlayerInputReader _input;
    private bool _isPause;

    private void Awake()
    {
        _input = FindObjectOfType<PlayerInputReader>();
        Button[] buttons = _pauseMenuPanel.GetComponentsInChildren<Button>(true);
        buttons[0].onClick.AddListener(RetryGame);
        buttons[1].onClick.AddListener(ReturnToTitle);
        _pauseMenuPanel.SetActive(false);
    }

    private void OnEnable() => _input.PauseRequested += TogglePause;

    private void OnDisable()
    {
        _input.PauseRequested -= TogglePause;
        if (_isPause) ResumeGame();
    }

    private void TogglePause()
    {
        if (_isPause) ResumeGame();
        else PauseGame();
    }

    public void PauseGame()
    {
        _isPause = true;
        TimeManager.SetPaused(true);
        _input.SetGameplayEnabled(false);
        AudioListener.pause = true;
        _pauseMenuPanel.SetActive(true);
    }

    public void ResumeGame()
    {
        _isPause = false;
        TimeManager.SetPaused(false);
        _input.SetGameplayEnabled(true);
        AudioListener.pause = false;
        _pauseMenuPanel.SetActive(false);
    }

    public void RetryGame() => LoadScene(SceneManager.GetActiveScene().path);
    public void ReturnToTitle() => LoadScene("Assets/Scenes/MainMenuScene.unity");

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
