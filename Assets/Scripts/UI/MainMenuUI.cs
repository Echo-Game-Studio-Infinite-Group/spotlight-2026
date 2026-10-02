using UnityEngine;
using UnityEngine.UI;


public class MainMenuUI : MonoBehaviour
{
    public Button BtnStart;

    private void Awake()
    {
        BtnStart.onClick.AddListener(StartGame);
    }

    public void StartGame()
    {
        UnityEngine.SceneManagement.SceneManager.LoadScene("TestScene");
    }
}
