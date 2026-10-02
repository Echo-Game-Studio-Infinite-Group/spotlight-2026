using UnityEngine;
using UnityEngine.UI;

// *Warning* 使用Time.timeScale，需后续更改
public class PauseUIManager : MonoBehaviour
{
    private bool _isPause;

    // 暂停菜单
    public GameObject PauseMenuPanel;
    public Button BtnResume;
    public Button BtnRetry;
    public Button BtnReturn;

    public CameraController MouseScript;

    private void Awake()
    {
        _isPause = false;

        PauseMenuPanel.SetActive(false);
        BtnResume.onClick.AddListener(ResumeGame);
        BtnRetry.onClick.AddListener(RetryGame);
        BtnReturn.onClick.AddListener(ReturnToTitle);

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
        MouseScript.enabled = false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        AudioListener.pause = true;
        PauseMenuPanel.SetActive(true);
    }

    public void ResumeGame()
    {
        _isPause = false;
        Time.timeScale = 1.0f; // *Warning*
        MouseScript.enabled = true;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        AudioListener.pause = false;
        PauseMenuPanel.SetActive(false);
    }

    public void RetryGame()
    {
        _isPause = false;
        Time.timeScale = 1.0f; // *Warning*
        MouseScript.enabled = true;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        AudioListener.pause = false;
        PauseMenuPanel.SetActive(false);
        UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
    }

    public void ReturnToTitle()
    {
        _isPause = false;
        Time.timeScale = 1.0f; // *Warning*
        MouseScript.enabled = true;
        AudioListener.pause = false;
        PauseMenuPanel.SetActive(false);
        UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenuScene");
    }
}
