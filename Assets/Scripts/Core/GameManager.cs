using UnityEngine;
using UnityEngine.SceneManagement;

public enum GameState
{
    Start,
    Playing,
    Pause,
    End
}

// 局内流程：只负责开始/暂停/恢复/结束与重载场景，不持有任何场景对象。
// 玩家的状态（血量等）归场景里的 Player 与 HealthComponent，
// 这里只保留「跨场景仍然成立」的东西，避免 DontDestroyOnLoad 对象握着已销毁的场景引用。
public class GameManager : MonoBehaviour
{
    private static GameManager _instance = null;

    public static GameManager Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = new GameObject("GameManager").AddComponent<GameManager>();
            }

            return _instance;
        }
    }

    public GameState State { get; private set; } = GameState.Start;
    public bool InputAllowed = true;

    private void Awake()
    {
        if (_instance && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    public void StartGame()
    {
        Player player = Player.Current;
        if (player != null) player.ResetForNewRun();
        InputAllowed = true;
        State = GameState.Playing;
    }

    public void PauseGame()
    {
        if (State != GameState.Playing) return;

        InputAllowed = false;
        State = GameState.Pause;
    }

    public void ResumeGame()
    {
        if (State != GameState.Pause) return;

        InputAllowed = true;
        State = GameState.Playing;
    }

    public void EndGame()
    {
        InputAllowed = false;
        State = GameState.End;
    }

    public void RestartLevel()
    {
        Scene active = SceneManager.GetActiveScene();

        if (active.buildIndex < 0)
        {
            Debug.LogError("[GameManager] 当前场景不在 Build Settings 中，无法重载", this);
            return;
        }

        // 玩家随场景重建，血量由新的 HealthComponent 从满开始，这里不再手动 Reset。
        SceneManager.LoadScene(active.buildIndex);
    }
}
