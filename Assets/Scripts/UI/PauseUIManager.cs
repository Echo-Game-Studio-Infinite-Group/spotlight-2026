using UnityEngine;
using UnityEngine.UI;

// *Warning* 使用Time.timeScale，需后续更改
public class PauseUIManager : MonoBehaviour
{
    private bool _isPause;

    // 暂停菜单
    public GameObject pauseMenuPanel;
    public Button btnResume;
    public Button btnRetry;
    public Button btnReturn;

    public CameraController mouseScript;

    private void Awake()
    {
        _isPause = false;

        pauseMenuPanel.SetActive(false);
        btnResume.onClick.AddListener(ResumeGame);
        btnRetry.onClick.AddListener(RetryGame);
        btnReturn.onClick.AddListener(ReturnToTitle);

    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (_isPause)
            {
                ResumeGame();
            }
            else
            {
                PauseGame();
            }
        }
    }

    public void PauseGame()
    {
        _isPause = true;
        Time.timeScale = 0.0f; // *Warning*
        mouseScript.enabled = false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        AudioListener.pause = true;
        pauseMenuPanel.SetActive(true);
    }

    public void ResumeGame()
    {
        _isPause = false;
        Time.timeScale = 1.0f; // *Warning*
        mouseScript.enabled = true;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        AudioListener.pause = false;
        pauseMenuPanel.SetActive(false);
    }

    public void RetryGame()
    {
        _isPause = false;
        Time.timeScale = 1.0f; // *Warning*
        mouseScript.enabled = true;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        AudioListener.pause = false;
        pauseMenuPanel.SetActive(false);
        UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
    }

    public void ReturnToTitle()
    {
        _isPause = false;
        Time.timeScale = 1.0f; // *Warning*
        mouseScript.enabled = true;
        AudioListener.pause = false;
        pauseMenuPanel.SetActive(false);
        UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenuScene");
    }
}
