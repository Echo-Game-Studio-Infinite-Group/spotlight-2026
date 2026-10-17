using UnityEngine;
using UnityEngine.UI;


public class MainMenuUI : MonoBehaviour
{
    public Button btnStart;

    private void Awake()
    {
        btnStart.onClick.AddListener(StartGame);
    }

    public void StartGame()
    {
        UnityEngine.SceneManagement.SceneManager.LoadScene("TestScene");
    }
}
