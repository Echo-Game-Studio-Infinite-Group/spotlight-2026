using UnityEngine;
using UnityEngine.UI;


// *Warning* 使用Time.timeScale，需后续更改
public class PauseUIManager : MonoBehaviour
{
    private bool _isPause;

    [SerializeField] private GameObject _pauseMenuPanel;
    private Button _btnRetry;
    private Button _btnReturn;
    private object _pauseUIOwner = new object();

    private void Awake()
    {
        _isPause = false;

        _pauseMenuPanel.SetActive(false);
        Button[] btns = _pauseMenuPanel.GetComponentsInChildren<Button>();
        _btnRetry = btns[0];
        _btnReturn = btns[1];
        _btnRetry.onClick.AddListener(RetryGame);
        _btnReturn.onClick.AddListener(ReturnToTitle);
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

        TimeManager.Apply(TimeManager.TimeLayer.Game, 0.0f, 0.0f, _pauseUIOwner);
        TimeManager.Apply(TimeManager.TimeLayer.Player, 0.0f, 0.0f, _pauseUIOwner);
        TimeManager.Apply(TimeManager.TimeLayer.World, 0.0f, 0.0f, _pauseUIOwner);

        AudioListener.pause = true;
        _pauseMenuPanel.SetActive(true);
    }

    public void ResumeGame()
    {
        _isPause = false;

        TimeManager.Release(TimeManager.TimeLayer.Game, _pauseUIOwner);
        TimeManager.Release(TimeManager.TimeLayer.Player, _pauseUIOwner);
        TimeManager.Release(TimeManager.TimeLayer.World, _pauseUIOwner);

        AudioListener.pause = false;
        _pauseMenuPanel.SetActive(false);
    }

    public void RetryGame()
    {
        _isPause = false;

        TimeManager.Release(TimeManager.TimeLayer.Game, _pauseUIOwner);
        TimeManager.Release(TimeManager.TimeLayer.Player, _pauseUIOwner);
        TimeManager.Release(TimeManager.TimeLayer.World, _pauseUIOwner);

        AudioListener.pause = false;
        _pauseMenuPanel.SetActive(false);
        UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
    }

    public void ReturnToTitle()
    {
        _isPause = false;

        TimeManager.Release(TimeManager.TimeLayer.Game, _pauseUIOwner);
        TimeManager.Release(TimeManager.TimeLayer.Player, _pauseUIOwner);
        TimeManager.Release(TimeManager.TimeLayer.World, _pauseUIOwner);

        AudioListener.pause = false;
        _pauseMenuPanel.SetActive(false);
        UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenuScene");
    }
}
